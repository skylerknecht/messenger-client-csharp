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
        private readonly double _retryDuration;
        private readonly int _retryAttempts;
        private readonly ConcurrentQueue<object> _downstreamMessages;
        private string _messengerId;

        public HTTPMessengerClient(string uri, byte[] encryptionKey, string userAgent, double retryDuration, int retryAttempts, IWebProxy proxy = null)
        {
            _uri = uri;
            _encryptionKey = encryptionKey;
            _retryDuration = retryDuration;
            _retryAttempts = retryAttempts;

            var handler = new HttpClientHandler();
            if (proxy != null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }

            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
            _downstreamMessages = new ConcurrentQueue<object>();
        }

        public override async Task ConnectAsync()
        {
            int retryDelay = (int)((_retryDuration / _retryAttempts) * 1000);
            int consecutiveFailures = 0;

            while (consecutiveFailures < _retryAttempts)
            {
                try
                {
                    Console.WriteLine($"Connecting to HTTP server at {_uri}");

                    var downstreamMessage = MessageBuilder.SerializeMessage(_encryptionKey, new CheckInMessage(_messengerId));
                    HttpContent content = new ByteArrayContent(downstreamMessage);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                    var response = await _httpClient.PostAsync(_uri, content);
                    response.EnsureSuccessStatusCode();

                    if (string.IsNullOrEmpty(_messengerId))
                    {
                        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
                        var (_, parsedMessage) = MessageParser.DeserializeMessage(_encryptionKey, responseBytes);

                        if (parsedMessage is CheckInMessage checkInMsg)
                        {
                            _messengerId = checkInMsg.MessengerId;
                            Console.WriteLine($"[+] Connected with Messenger ID: {_messengerId}");
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"Expected CheckInMessage, got {parsedMessage.GetType().Name}"
                            );
                        }
                    }

                    consecutiveFailures = 0;
                    await PollServerAsync();
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    Console.WriteLine($"[!] Connection failed: {ex.Message}");
                    if (consecutiveFailures < _retryAttempts)
                    {
                        Console.WriteLine("[*] Retrying connection...");
                        await Task.Delay(retryDelay);
                    }
                }
            }

            Console.WriteLine($"[-] Reconnect failed after {_retryAttempts} attempts. Giving up.");
        }


        private async Task PollServerAsync()
        {
            while (true)
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
                    throw new HttpRequestException($"Poll failed: HTTP {response.StatusCode}");

                var responseData = await response.Content.ReadAsByteArrayAsync();
                var messages = DeserializeMessages(_encryptionKey, responseData);

                foreach (var msg in messages)
                {
                    _ = Task.Run(() => HandleMessageAsync(msg));
                }

                await Task.Delay(1000);
            }
        }

        public override Task SendDownstreamMessageAsync(object message)
        {
            _downstreamMessages.Enqueue(message);
            return Task.CompletedTask;
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
                    break;

                default:
                    Console.WriteLine("Unknown message type received");
                    break;
            }
        }
    }
}
