using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class WebSocketMessengerClient : MessengerClient
    {
        private readonly Uri _uri;
        private ClientWebSocket _webSocket;
        private ConcurrentQueue<ArraySegment<byte>> _messageQueue = new ConcurrentQueue<ArraySegment<byte>>();
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public WebSocketMessengerClient(string uri)
        {
            _uri = new Uri(uri);
            _webSocket = new ClientWebSocket();
        }

        /// <summary>
        /// Establishes a WebSocket connection to the server.
        /// </summary>
        public override async Task ConnectAsync()
        {
            try
            {
                Console.WriteLine("Connecting to WebSocket server...");
                await _webSocket.ConnectAsync(_uri, CancellationToken.None);
                Console.WriteLine("Connected!");

                // Start receiving messages
                var receivingTask = ReceiveMessagesAsync();
                var sendingTask = SendMessages(_webSocket, _cancellationTokenSource.Token);
                await Task.WhenAll(receivingTask, sendingTask);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error connecting to WebSocket server: {ex.Message}");
            }
        }

        /// <summary>
        /// Receives messages from the WebSocket server.
        /// </summary>
        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[4096];
            while (_webSocket.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("WebSocket connection closed.");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var messageData = new byte[result.Count];
                    Array.Copy(buffer, messageData, result.Count);

                    try
                    {
                        var message = MessageParser.ParseMessage(messageData);
                        _ = HandleMessageAsync(message);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error parsing message: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Handles incoming messages based on their type.
        /// </summary>
        /// <param name="message">The parsed message.</param>
        public override async Task HandleMessageAsync(Message message)
        {
            Console.WriteLine($"Received message: {message}");
            switch (message)
            {
                case InitiateForwarderClientReqMessage reqMessage:
                    Console.WriteLine("InitiateForwarderClientReq message received");
                    await HandleInitiateForwarderClientReqAsync(reqMessage);
                    break;

                case InitiateForwarderClientRepMessage repMessage:
                    Console.WriteLine("InitiateForwarderClientRep message received");
                    _ = StreamAsync(repMessage.ForwarderClientId);
                    break;

                case SendDataMessage sendDataMessage:
                    Console.WriteLine("SendDataMessage message received");
                    if (ForwarderClients.TryGetValue(sendDataMessage.ForwarderClientId, out var client))
                    {
                        Console.WriteLine("sending data");
                        await client.GetStream().WriteAsync(sendDataMessage.Data, 0, sendDataMessage.Data.Length);
                    }
                    break;

                case CheckInMessage checkInMessage:
                    Console.WriteLine($"Check-In Message: Messenger ID: {checkInMessage.MessengerId}");
                    break;

                default:
                    Console.WriteLine("Unknown message type received");
                    break;
            }
        }

        /// <summary>
        /// Sends a downstream message to the WebSocket server.
        /// </summary>
        /// <param name="messageData">The byte array containing the message data.</param>
        public override async Task SendDownstreamMessageAsync(byte[] messageData)
        {
            Console.WriteLine($"sending {_webSocket.State == WebSocketState.Open}");
            _messageQueue.Enqueue(new ArraySegment<byte>(messageData));
        }

        private async Task SendMessages(ClientWebSocket ws, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                while (_messageQueue.TryDequeue(out var message))
                {
                    await ws.SendAsync(message, WebSocketMessageType.Binary, true, token);
                }
                await Task.Delay(10); // Adjust delay as necessary
            }
        }

        /// <summary>
        /// Closes the WebSocket connection.
        /// </summary>
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
