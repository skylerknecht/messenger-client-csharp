using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class WebSocketMessengerClient : MessengerClient
    {
        private readonly Uri _uri;
        private readonly byte[] _encryptionKey;
        private readonly IWebProxy _proxy;
        private ClientWebSocket _webSocket;
        private readonly ConcurrentQueue<object> _downstreamMessages;
        private string _messengerId;
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public WebSocketMessengerClient(string uri, byte[] encryptionKey, IWebProxy proxy = null)
        {
            _uri = new Uri(uri);
            _encryptionKey = encryptionKey;
            _proxy = proxy; 
            _webSocket = new ClientWebSocket();
            _downstreamMessages = new ConcurrentQueue<object>();
            _messengerId = String.Empty;

            if (_proxy != null)
            {
                _webSocket.Options.Proxy = _proxy;
            }
        }

        public override async Task ConnectAsync()
        {
            try
            {
                Console.WriteLine("Connecting to WebSocket server...");
                await _webSocket.ConnectAsync(_uri, CancellationToken.None);
                Console.WriteLine("Connected!");

                var receivingTask = ReceiveMessagesAsync();
                var sendingTask = SendMessagesAsync(_cancellationTokenSource.Token);
                await Task.WhenAll(receivingTask, sendingTask);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error connecting to WebSocket server: {ex.Message}");
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[4096];
            var messageBuffer = new MemoryStream(); 

            while (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine("WebSocket connection closed.");
                        break;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        byte[] messageData = messageBuffer.ToArray();
                        messageBuffer.SetLength(0); 

                        try
                        {
                            var messages = DeserializeMessages(_encryptionKey, messageData);

                            foreach (var message in messages)
                            {
                                _ = Task.Run(async () =>
                                {
                                    await HandleMessageAsync(message);
                                });                                
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error parsing message: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error receiving message: {ex.Message}");
                }
            }
        }

        public override async Task HandleMessageAsync(object message)
        {
            switch (message)
            {
                case InitiateForwarderClientReq reqMessage:
                    await HandleInitiateForwarderClientReqAsync(reqMessage);
                    break;

                case InitiateForwarderClientRep repMessage:
                    await StreamAsync(repMessage.ForwarderClientId);
                    break;

                case SendDataMessage sendDataMessage:
                    if (ForwarderClients.TryGetValue(sendDataMessage.ForwarderClientId, out var client))
                    {
                        await client.GetStream().WriteAsync(sendDataMessage.Data, 0, sendDataMessage.Data.Length);
                    }
                    break;

                case CheckInMessage checkInMessage:
                    _messengerId = checkInMessage.MessengerId;
                    break;

                default:
                    Console.WriteLine("Unknown message type received");
                    break;
            }
        }

        public override async Task SendDownstreamMessageAsync(object message)
        {
            _downstreamMessages.Enqueue(message);
        }

        private async Task SendMessagesAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_downstreamMessages.IsEmpty)
                {
                    await Task.Delay(10, token); // Wait briefly before rechecking
                    continue;
                }

                var downstreamMessages = new List<object>{ new CheckInMessage(_messengerId) };

                while (_downstreamMessages.TryDequeue(out var message))
                {
                    downstreamMessages.Add(message);
                }

                var content = new ArraySegment<byte>(SerializeMessages(_encryptionKey, downstreamMessages));
                await _webSocket.SendAsync(content, WebSocketMessageType.Binary, true, token);
            }
        }

        public async Task CloseAsync()
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing connection", CancellationToken.None);
                Console.WriteLine("WebSocket connection closed.");
            }
        }
    }
}