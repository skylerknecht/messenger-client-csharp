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
        private readonly ConcurrentQueue<object> _downstreamMessages;

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
            _downstreamMessages = new ConcurrentQueue<object>();
        }

        public override async Task ConnectAsync()
        {
            var downstreamMessage = MessageBuilder.SerializeMessage(_encryptionKey, new CheckInMessage(Identifier));
            HttpContent content = new ByteArrayContent(downstreamMessage);
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
                var downstreamMessages = new List<object>();

                downstreamMessages.Add(new CheckInMessage(Identifier));

                int drained = 0;
                while (drained < 5 && _downstreamMessages.TryDequeue(out var message))
                {
                    downstreamMessages.Add(message);
                    drained++;
                }

                HttpContent content = new ByteArrayContent(SerializeMessages(_encryptionKey, downstreamMessages));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                HttpResponseMessage response;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    response = await _httpClient.PostAsync(_uri, content, cts.Token);
                }

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Poll failed: HTTP {response.StatusCode}");

                var responseData = await response.Content.ReadAsByteArrayAsync();
                var messages = DeserializeMessages(_encryptionKey, responseData);

                if (messages.Any(m => m is CheckOutMessage))
                {
                    HandleCheckOut();
                    break;
                }

                foreach (var msg in messages)
                {
                    _ = Task.Run(() => HandleMessageAsync(msg));
                }

                await Task.Delay(100);
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
                case InitiateTCPClientReq reqMessage:
                    await HandleInitiateTCPClientReqAsync(reqMessage);
                    break;

                case InitiateTCPClientRep repMessage:
                    if (!TcpClients.TryGetValue(repMessage.ClientId, out var repClient))
                        break;
                    if (repMessage.Reason != 0)
                    {
                        if (TcpClients.TryRemove(repMessage.ClientId, out var denied))
                            denied.Close();
                        break;
                    }
                    await StreamAsync(repMessage.ClientId);
                    break;

                case SendDataMessage sendDataMessage:
                    if (sendDataMessage.Data.Length == 0)
                    {
                        if (TcpClients.TryRemove(sendDataMessage.ClientId, out var closedClient))
                            closedClient.Close();
                    }
                    else if (TcpClients.TryGetValue(sendDataMessage.ClientId, out var client))
                    {
                        await client.GetStream().WriteAsync(sendDataMessage.Data, 0, sendDataMessage.Data.Length);
                    }
                    break;

                case InitiateBINDReq bindReqMessage:
                    await HandleBindAsync(bindReqMessage);
                    break;

                case CheckInMessage checkInMessage:
                    break;

                case CheckOutMessage _:
                    HandleCheckOut();
                    break;

                default:
                    break;
            }
        }
    }
}
