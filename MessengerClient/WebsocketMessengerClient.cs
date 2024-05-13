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
                    Task receiveTask = Task.Run(async () => await ReceiveMessages(ws));
                    await receiveTask;
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
                HandleMessege(ws, completeMessage.ToString());
            }
        }

        private void HandleMessege(ClientWebSocket ws, string completeMessege)
        {
            Message msg = JsonSerializer.Deserialize<Message>(completeMessege);

            if (clients.TryGetValue(msg.identifier, out Socket client))
            {
                client.Send(Base64ToBytes(msg.msg));
            }
            else
            {
                SocksConnect(ws, completeMessege);
            }
        }

        private void SocksConnect(ClientWebSocket ws, string socksConnectRequest)
        {
            Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var request = JsonSerializer.Deserialize<SocksConnectRequest>(socksConnectRequest);
            ArraySegment<byte> socksConnectResults = new ArraySegment<byte> { };
            SocketAsyncEventArgs connectEventArgs = new SocketAsyncEventArgs();
            connectEventArgs.RemoteEndPoint = new DnsEndPoint(request.address, request.port);
            connectEventArgs.Completed += (sender, args) =>
            {
                if (args.SocketError == SocketError.Success)
                {
                    clients.Add(request.identifier, client);

                    string bindAddr = ((IPEndPoint)client.LocalEndPoint).Address.ToString();
                    int bindPort = ((IPEndPoint)client.LocalEndPoint).Port;


                    socksConnectResults = SocksConnectResults(request.identifier, 0, bindAddr, bindPort);

                    ws.SendAsync(socksConnectResults, WebSocketMessageType.Text, true, CancellationToken.None);
                    Stream(ws, request.identifier, client);
                }
                else
                {
                    socksConnectResults = SocksConnectResults(request.identifier, 1, null, 0);
                    ws.SendAsync(socksConnectResults, WebSocketMessageType.Text, true, CancellationToken.None);
                }
            };
            client.ConnectAsync(connectEventArgs);

        }

        public void Stream(ClientWebSocket ws, string identifier, Socket client)
        {
            byte[] downstream_data = new byte[4096];
            SocketAsyncEventArgs e = new SocketAsyncEventArgs();
            e.SetBuffer(downstream_data, 0, downstream_data.Length);
            e.Completed += async(sender2, args2) =>
            {
                if (e.SocketError == SocketError.Success && e.BytesTransferred > 0)
                {
                    byte[] data = new byte[e.BytesTransferred];
                    Array.Copy(e.Buffer, data, e.BytesTransferred);
                    await ws.SendAsync(GenerateDownstreamMessege(identifier, data), WebSocketMessageType.Text, true, CancellationToken.None);
                    client.ReceiveAsync(e);
                }
            };
            client.ReceiveAsync(e);
        }
    }
}
