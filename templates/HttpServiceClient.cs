using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceClient
{
    public class HttpServiceClient : ServiceClient
    {
        private readonly string _uri;
        private readonly HttpClient _httpClient;
        private readonly byte[] _encryptionKey;
        private readonly ConcurrentQueue<object> _upstreamMessages;
        private readonly List<object> _pending = new List<object>();
        private readonly SemaphoreSlim _pollWake = new SemaphoreSlim(0, 1);

        public HttpServiceClient(string uri, byte[] encryptionKey, string userAgent, IWebProxy proxy = null)
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
            byte[] responseBytes;
            using (var content = new ByteArrayContent(MessageBuilder.SerializeMessage(_encryptionKey, new CheckInMessage(Identifier))))
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var response = await _httpClient.PostAsync(_uri, content, cts.Token))
                {
                    response.EnsureSuccessStatusCode();
                    responseBytes = await response.Content.ReadAsByteArrayAsync();
                }
            }

            if (!string.IsNullOrEmpty(Identifier))
            {
                if (responseBytes != null && responseBytes.Length > 0)
                {
                    var messages = DeserializeMessages(_encryptionKey, responseBytes);
                    if (messages.Any(m => m is CheckOutMessage))
                    {
                        HandleCheckout();
                        return;
                    }
                    foreach (var msg in messages)
                    {
                        DispatchMessage(msg);
                    }
                }
                return;
            }

            var connectMessages = DeserializeMessages(_encryptionKey, responseBytes);

            if (connectMessages.Count > 0 && connectMessages[0] is CheckInMessage checkInMsg)
            {
                Identifier = checkInMsg.MessengerId;
            }
            else
            {
                throw new InvalidOperationException("Expected CheckInMessage from server");
            }
        }

        public override async Task StartAsync()
        {
            await ReadvertiseForwardersAsync();
{% if not no_adaptive_polling %}
            double pollDelay = 0.1;
            const double PollMin = 0.1;
            const double PollMax = 10.0;
            const double PollFactor = 1.1;
            const double PollJitter = 0.2;
            var rng = new Random();
{% endif %}

            while (!Killed)
            {
                _pollWake.Wait(0);
                if (_pending.Count == 0)
                {
                    while (_pending.Count < MaxBatchSize && _upstreamMessages.TryDequeue(out var message))
                        _pending.Add(message);
                }

                var batch = new List<object> { new CheckInMessage(Identifier) };
                batch.AddRange(_pending);
{% if not no_adaptive_polling %}
                bool sentActivity = _pending.Any(m => !(m is CheckInMessage) && !(m is CheckOutMessage));
{% endif %}

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

{% if not no_adaptive_polling %}
                    int realMsgs = messages.Count(m => !(m is CheckInMessage) && !(m is CheckOutMessage));
                    if (sentActivity || realMsgs > 0)
                        pollDelay = PollMin;
                    else
                        pollDelay = Math.Min(pollDelay * PollFactor, PollMax);
{% endif %}

                    foreach (var msg in messages)
                    {
                        DispatchMessage(msg);
                    }
                }

                if (Killed) break;
{% if not no_adaptive_polling %}
                if (!_upstreamMessages.IsEmpty) continue;
                double jittered = pollDelay * (1.0 - PollJitter + rng.NextDouble() * 2 * PollJitter);
                await _pollWake.WaitAsync((int)(jittered * 1000));
{% else %}
                await _pollWake.WaitAsync(100);
{% endif %}
            }
        }

        public override Task SendUpstreamMessageAsync(object message)
        {
            _upstreamMessages.Enqueue(message);
{% if not no_adaptive_polling %}
            WakePoll();
{% endif %}
            return Task.CompletedTask;
        }

        private void WakePoll()
        {
            try { _pollWake.Release(); }
            catch (SemaphoreFullException) { }
        }

        protected override void HandleCheckout()
        {
            base.HandleCheckout();
            WakePoll();
        }

        public override void CloseTransport()
        {
            WakePoll();
            try { _httpClient?.Dispose(); } catch { }
        }
    }
}
