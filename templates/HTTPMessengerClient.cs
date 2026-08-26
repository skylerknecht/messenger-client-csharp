using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class HTTPMessengerClient : MessengerClient
    {
        private readonly string _uri;
        private readonly HttpClient _httpClient;
        private readonly byte[] _encryptionKey;
        private readonly ConcurrentQueue<object> _upstreamMessages;
        private readonly List<object> _pending = new List<object>();

        public HTTPMessengerClient(string uri, byte[] encryptionKey, string userAgent, IWebProxy proxy = null)
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
            _httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
            _upstreamMessages = new ConcurrentQueue<object>();
        }

        public override async Task ConnectAsync()
        {
            var upstreamMessage = MessageBuilder.SerializeMessage(_encryptionKey, new CheckInMessage(Identifier));
            HttpContent content = new ByteArrayContent(upstreamMessage);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            HttpResponseMessage response;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                response = await _httpClient.PostAsync(_uri, content, cts.Token);
            }
            response.EnsureSuccessStatusCode();

            if (string.IsNullOrEmpty(Identifier))
            {
                byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
                var (_, parsedMessage) = MessageParser.DeserializeMessage(_encryptionKey, responseBytes);

                if (parsedMessage is CheckInMessage checkInMsg)
                {
                    Identifier = checkInMsg.MessengerId;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Expected CheckInMessage, got {parsedMessage.GetType().Name}"
                    );
                }
            }
        }

        public override async Task StartAsync()
        {
            await ReadvertiseForwardersAsync();

            while (!Killed)
            {
                if (_pending.Count == 0)
                {
                    int drained = 0;
                    while (drained < 5 && _upstreamMessages.TryDequeue(out var message))
                    {
                        _pending.Add(message);
                        drained++;
                    }
                }

                var batch = new List<object> { new CheckInMessage(Identifier) };
                batch.AddRange(_pending);

                HttpContent content = new ByteArrayContent(SerializeMessages(_encryptionKey, batch));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                HttpResponseMessage response;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    response = await _httpClient.PostAsync(_uri, content, cts.Token);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"Poll failed: HTTP {response.StatusCode}");

                    _pending.Clear();

                    var responseData = await response.Content.ReadAsByteArrayAsync();
                    var messages = DeserializeMessages(_encryptionKey, responseData);

                    if (messages.Any(m => m is CheckOutMessage))
                    {
                        HandleCheckout();
                        break;
                    }

                    foreach (var msg in messages)
                    {
                        DispatchMessage(msg);
                    }
                }

                await Task.Delay(100);
            }
        }

        public override Task SendUpstreamMessageAsync(object message)
        {
            _upstreamMessages.Enqueue(message);
            return Task.CompletedTask;
        }

        public override void CloseTransport()
        {
            try { _httpClient?.Dispose(); } catch { }
        }
    }
}
