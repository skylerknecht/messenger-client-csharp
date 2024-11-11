using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class HTTPMessengerClient : MessengerClient
    {
        private readonly string _uri;
        private readonly HttpClient _httpClient;
        private readonly byte[] _encryptionKey;
        private readonly ConcurrentQueue<byte[]> DownstreamMessages;
        private string MessengerId;

        public HTTPMessengerClient(string uri, byte[] encryptionKey)
        {
            _uri = uri;
            _httpClient = new HttpClient();
            _encryptionKey = encryptionKey;
            DownstreamMessages = new ConcurrentQueue<byte[]>();
        }

        public override async Task ConnectAsync()
        {
            try
            {
                Console.WriteLine($"Connecting to HTTP server at {_uri}");
                var response = await _httpClient.GetStringAsync(_uri);
                MessengerId = response.Trim(); // Assuming server returns a unique messenger ID.
                Console.WriteLine($"Connected to server with Messenger ID: {MessengerId}");

                // Start polling for new messages
                await PollServerAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error connecting to server: {ex.Message}");
                throw;
            }
        }

        private async Task PollServerAsync()
        {
            while (true)
            {
                try
                {
                    // Collect pending downstream messages
                    var downstreamMessages = MessageBuilder.CheckIn(MessengerId);
                    while (DownstreamMessages.TryDequeue(out var message))
                    {
                        downstreamMessages = CombineArrays(downstreamMessages, message);
                    }

                    var encryptedDownstreamMessages = Crypto.Encrypt(_encryptionKey, downstreamMessages);
                    HttpContent content = new ByteArrayContent(encryptedDownstreamMessages);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    var response = await _httpClient.PostAsync(_uri, content);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Failed to poll server. HTTP {response.StatusCode}");
                        break;
                    }

                    var encryptedResponseData = await response.Content.ReadAsByteArrayAsync();
                    var decryptedResponseData = Crypto.Decrypt(_encryptionKey, encryptedResponseData);
                    var messages = MessageParser.ParseMessages(decryptedResponseData);

                    foreach (var message in messages)
                    {
                        await HandleMessageAsync(message); // Pass the parsed message here
                    }

                    await Task.Delay(1000); // Wait before polling again
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error polling server: {ex.Message}");
                    break;
                }
            }
        }

        public override async Task SendDownstreamMessageAsync(byte[] messageData)
        {
            try
            {
                DownstreamMessages.Enqueue(messageData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error enqueuing downstream message: {ex.Message}");
            }
        }

        public override async Task HandleMessageAsync(Message message)
        {
            // Correctly process the parsed message object
            switch (message)
            {
                case InitiateForwarderClientReqMessage reqMessage:
                    await HandleInitiateForwarderClientReqAsync(reqMessage);
                    break;

                case InitiateForwarderClientRepMessage repMessage:
                    _ = StreamAsync(repMessage.ForwarderClientId);
                    break;

                case SendDataMessage sendDataMessage:
                    if (ForwarderClients.TryGetValue(sendDataMessage.ForwarderClientId, out var client))
                    {
                        await client.GetStream().WriteAsync(sendDataMessage.Data, 0, sendDataMessage.Data.Length);
                    }
                    break;

                case CheckInMessage checkInMessage:
                    break;

                default:
                    Console.WriteLine("Unknown message type received");
                    break;
            }
        }

        private static byte[] CombineArrays(byte[] first, byte[] second)
        {
            var result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }
    }
}
