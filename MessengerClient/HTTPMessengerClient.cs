using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class HTTPMessengerClient : MessengerClient
    {
        private readonly string _uri;
        private readonly HttpClient _httpClient;
        private readonly byte[] _encryptionKey;
        private readonly ConcurrentQueue<object> _downstreamMessages;
        private string _messengerId;

        public HTTPMessengerClient(string uri, byte[] encryptionKey, IWebProxy proxy = null)
        {
            _uri = uri;
            _encryptionKey = encryptionKey;

            var handler = new HttpClientHandler();
            if (proxy != null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }

            _httpClient = new HttpClient(handler);
            _downstreamMessages = new ConcurrentQueue<object>();
        }

        public override async Task ConnectAsync()
        {
            try
            {
                Console.WriteLine($"Connecting to HTTP server at {_uri}");

                var downstreamMessage = MessageBuilder.SerializeMessage(_encryptionKey, new CheckInMessage(""));
                HttpContent content = new ByteArrayContent(downstreamMessage);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                var response = await _httpClient.PostAsync(_uri, content);

                byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();

                var (_, parsedMessage) = MessageParser.DeserializeMessage(_encryptionKey, responseBytes);

                if (parsedMessage is CheckInMessage checkInMsg)
                {
                    _messengerId = checkInMsg.MessengerId;
                    Console.WriteLine($"Connected to server with Messenger ID: {_messengerId}");
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Expected a CheckInMessage, but got {parsedMessage.GetType().Name}"
                    );
                }
                await PollServerAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error connecting to server: {ex}");
                throw;
            }
        }

        private async Task PollServerAsync()
        {
            while (true)
            {
                try
                {
                    var downstreamMessages = new List<object>();

                    CheckInMessage checkInMessage = new CheckInMessage(_messengerId);
                    downstreamMessages.Add(checkInMessage);

                    while (_downstreamMessages.TryDequeue(out var message))
                    {
                        downstreamMessages.Add(message);
                    }

                    HttpContent content = new ByteArrayContent(SerializeMessages(_encryptionKey, downstreamMessages));
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    var response = await _httpClient.PostAsync(_uri, content);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Failed to poll server. HTTP {response.StatusCode}");
                        break;
                    }

                    var responseData = await response.Content.ReadAsByteArrayAsync();
                    var messages = DeserializeMessages(_encryptionKey, responseData);

                    foreach (var message in messages)
                    {
                        _ = Task.Run(() => HandleMessageAsync(message));
                    }

                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error polling server: {ex.Message}");
                    break;
                }
            }
        }

        public override async Task SendDownstreamMessageAsync(object message)
        {
            try
            {
                _downstreamMessages.Enqueue(message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error enqueuing downstream message: {ex.Message}");
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
                    break;

                default:
                    Console.WriteLine("Unknown message type received");
                    break;
            }
        }
    }
}