using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MessengerClient
{
    internal class HTTPMessengerClient : MessengerClient
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private readonly ConcurrentDictionary<string, TcpClient> clients = new ConcurrentDictionary<string, TcpClient>();
        private readonly ConcurrentQueue<Message> downstreamQueue = new ConcurrentQueue<Message>();
        private string serverID = string.Empty;

        public override async Task Connect(string uri)
        {
            try
            {
                // Get the serverID
                HttpResponseMessage response = await httpClient.GetAsync(uri);
                response.EnsureSuccessStatusCode();
                serverID = await response.Content.ReadAsStringAsync();

                // Start receiving messages
                await ReceiveMessages(uri);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception: {e}");
            }
        }

        private async Task ReceiveMessages(string uri)
        {
            while (true)
            {
                await Task.Delay(100);
                try
                {
                    List<Message> messagesToSend = new List<Message>();

                    // Dequeue all messages from the downstream queue
                    while (downstreamQueue.TryDequeue(out Message downstreamMessage))
                    {
                        messagesToSend.Add(downstreamMessage);
                    }

                    if (messagesToSend.Count == 0)
                    {
                        // Post an empty message to keep the connection alive
                        messagesToSend.Add(GenerateDownstreamMessage("", new byte[] { }));
                    }

                    string jsonMessage = JsonSerializer.Serialize(messagesToSend);
                    HttpContent content = new StringContent(jsonMessage, Encoding.UTF8, "application/json");
                    HttpResponseMessage response = await httpClient.PostAsync(uri, content);
                    response.EnsureSuccessStatusCode();

                    // Process the response which might contain multiple messages
                    string completeMessage = await response.Content.ReadAsStringAsync();
                    var messages = JsonSerializer.Deserialize<String[]>(completeMessage);

                    foreach (var message in messages)
                    {
                        _ = HandleMessage(uri, message);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Exception: {e}");
                    break;
                }
            }
        }

        private async Task HandleMessage(string uri, string completeMessage)
        {
            Console.WriteLine(completeMessage);
            Message msg = JsonSerializer.Deserialize<Message>(completeMessage);
            if (clients.TryGetValue(msg.identifier, out TcpClient client))
            {
                NetworkStream stream = client.GetStream();
                byte[] data = Base64ToBytes(msg.msg);
                await stream.WriteAsync(data, 0, data.Length);
            }
            else
            {
                await SocksConnect(uri, completeMessage);
            }
        }

        private async Task SocksConnect(string uri, string socksConnectRequest)
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
                downstreamQueue.Enqueue(GenerateDownstreamMessage(request.identifier, socksConnectResults));

                await Stream(uri, request.identifier, client);
            }
            catch (Exception ex)
            {
                socksConnectResults = SocksConnectResults(request.identifier, 1, null, 0);
                downstreamQueue.Enqueue(GenerateDownstreamMessage(request.identifier, socksConnectResults));
            }
        }

        public async Task Stream(string uri, string identifier, TcpClient client)
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

                var message = GenerateDownstreamMessage(identifier, data);

                // Add the message to the downstream queue
                downstreamQueue.Enqueue(message);
            }
            stream.Close();
        }

        public Message GenerateDownstreamMessage(string identifier, byte[] msg)
        {
            return new Message
            {
                identifier = $"{serverID}:{identifier}",
                msg = BytesToBase64(msg)
            };
        }

        private byte[] SocksConnectResults(string identifier, int rep, string bindAddr, int bindPort)
        {
            byte[] bindAddressBytes = string.IsNullOrEmpty(bindAddr) ? new byte[] { 0x00 } : IPAddress.Parse(bindAddr).GetAddressBytes();
            byte[] bindPortBytes = BitConverter.GetBytes((ushort)bindPort);
            Array.Reverse(bindPortBytes);

            var message = new byte[] {
                5,
                (byte)rep,
                0,
                1,
            };

            var fullMessage = new List<byte>();
            fullMessage.AddRange(message);
            fullMessage.AddRange(bindAddressBytes);
            fullMessage.AddRange(bindPortBytes);

            return fullMessage.ToArray();
        }
    }
}
