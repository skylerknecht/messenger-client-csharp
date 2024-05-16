using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net.Sockets;
using System.Net;

namespace MessengerClient
{
    internal class WebSocketMessengerClient : MessengerClient
    {
        public override async Task Connect(string uri)
        {
            using (ClientWebSocket ws = new ClientWebSocket())
            {
                try
                {
                    await ws.ConnectAsync(new Uri(uri), CancellationToken.None);
                    await ReceiveMessages(ws);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Exception: {e.ToString()}");
                }
                finally
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        Console.WriteLine("WebSocket connection closed.");
                    }
                }
            }
        }

        private async Task ReceiveMessages(ClientWebSocket ws)
        {
            byte[] buffer = new byte[4096];

            while (ws.State == WebSocketState.Open)
            {
                var completeMessage = new StringBuilder();
                WebSocketReceiveResult result = null;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    string messagePart = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    completeMessage.Append(messagePart);

                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server requested closure", CancellationToken.None);
                    Console.WriteLine("WebSocket server requested closure.");
                    break;
                }
                _ = HandleMessege(ws, completeMessage.ToString());
            }
        }

        private async Task HandleMessege(ClientWebSocket ws, string completeMessege)
        {
            Message msg = JsonSerializer.Deserialize<Message>(completeMessege);
            //Console.WriteLine(completeMessege);
            if (clients.TryGetValue(msg.identifier, out TcpClient client))
            {
                NetworkStream stream = client.GetStream();
                byte[] data = Base64ToBytes(msg.msg);
                stream.Write(data, 0, data.Length);
            }
            else
            {
                await SocksConnect(ws, completeMessege);
            }
        }

        private async Task SocksConnect(ClientWebSocket ws, string socksConnectRequest)
        {
            TcpClient client = new TcpClient();
            var request = JsonSerializer.Deserialize<SocksConnectRequest>(socksConnectRequest);
            ArraySegment<byte> socksConnectResults = new ArraySegment<byte> { };
            SocketAsyncEventArgs connectEventArgs = new SocketAsyncEventArgs();
            try
            {
                await client.ConnectAsync(request.address, request.port);
                IPEndPoint localEndPoint = client.Client.LocalEndPoint as IPEndPoint;
                string bindAddr = localEndPoint.Address.ToString();
                int bindPort = localEndPoint.Port;
                socksConnectResults = SocksConnectResults(request.identifier, 0, bindAddr, bindPort);
                clients[request.identifier] = client;
                await ws.SendAsync(socksConnectResults, WebSocketMessageType.Text, true, CancellationToken.None);
                await Stream(ws, request.identifier, client);
            }
            catch (Exception ex)
            {
                socksConnectResults = SocksConnectResults(request.identifier, 1, null, 0);
                await ws.SendAsync(socksConnectResults, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        public async Task Stream(ClientWebSocket ws, string identifier, TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            while (true)
            {
                byte[] buffer = new byte[4096];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                {
                    break;
                }


                byte[] data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);
                await ws.SendAsync(GenerateDownstreamMessege(identifier, data), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            stream.Close();
        }
    }
}