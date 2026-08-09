using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class WebSocketMessengerClient : MessengerClient
    {
        private readonly Uri _uri;
        private readonly byte[] _encryptionKey;
        private readonly string _userAgent;
        private readonly IWebProxy _proxy;
        private ClientWebSocket _webSocket;
        private readonly ConcurrentQueue<object> _downstreamMessages;
        // Wakes the single send loop the instant something is enqueued — no
        // polling — while guaranteeing exactly one task ever calls SendAsync.
        // ClientWebSocket aborts on overlapping sends, so serialization here is
        // a correctness requirement, not an optimization.
        private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);
        private CancellationTokenSource _cancellationTokenSource;

        public WebSocketMessengerClient(string uri, byte[] encryptionKey, string userAgent, IWebProxy proxy = null)
        {
            _uri = new Uri(uri);
            _encryptionKey = encryptionKey;
            _userAgent = userAgent;
            _proxy = proxy;
            _webSocket = new ClientWebSocket();
            _downstreamMessages = new ConcurrentQueue<object>();
        }

        public override async Task ConnectAsync()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();

            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();
            if (_proxy != null)
                _webSocket.Options.Proxy = _proxy;

            await _webSocket.ConnectAsync(_uri, CancellationToken.None);

            var checkIn = new CheckInMessage(Identifier);
            var content = new ArraySegment<byte>(SerializeMessages(_encryptionKey, new List<object> { checkIn }));
            await _webSocket.SendAsync(content, WebSocketMessageType.Binary, true, CancellationToken.None);

            if (string.IsNullOrEmpty(Identifier))
            {
                var ms = new MemoryStream();
                var buffer = new byte[4096];
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("Server closed during check-in");
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                byte[] messageData = ms.ToArray();
                ms.Dispose();

                var responseMessages = DeserializeMessages(_encryptionKey, messageData);

                if (responseMessages[0] is CheckInMessage responseCheckIn)
                {
                    Identifier = responseCheckIn.MessengerId;
                }
                else
                {
                    throw new Exception("Expected CheckInMessage from server");
                }
            }
        }

        public override async Task StartAsync()
        {
            _cancellationTokenSource = new CancellationTokenSource();

            // Any messages queued while disconnected already have their signal
            // permits (Release runs on every enqueue), so the send loop drains
            // the backlog on its own. Ensure at least one wake in case a
            // backlog exists but permits were consumed by a prior cancelled loop.
            if (!_downstreamMessages.IsEmpty)
                _sendSignal.Release();

            var receivingTask = ReceiveMessagesAsync();
            var sendingTask = SendMessagesAsync(_cancellationTokenSource.Token);

            await Task.WhenAll(receivingTask, sendingTask);
        }

        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[4096];
            var messageBuffer = new MemoryStream();

            try
            {
            while (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        byte[] messageData = messageBuffer.ToArray();
                        messageBuffer.SetLength(0);

                        try
                        {
                            var messages = DeserializeMessages(_encryptionKey, messageData);

                            foreach (var message in messages)
                            {
                                _ = Task.Run(async () =>
                                {
                                    await HandleMessageAsync(message);
                                });
                            }
                        }
                        catch (DecryptionException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[!] Error parsing message: {ex.Message}");
                        }
                    }
                }
                catch (DecryptionException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Error receiving message: {ex.Message}");
                    break;
                }
            }
            }
            finally
            {
                // Wake the signal-driven send loop so it observes the dropped
                // connection and exits; otherwise Task.WhenAll would hang on it.
                try { _cancellationTokenSource.Cancel(); } catch { }
            }
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
                    Identifier = checkInMessage.MessengerId;
                    break;

                default:
                    break;
            }
        }

        public override Task SendDownstreamMessageAsync(object message)
        {
            // Producer: never touch the socket. Enqueue and wake the send loop.
            // Serialization is the send loop's job — see _sendSignal.
            _downstreamMessages.Enqueue(message);
            _sendSignal.Release();
            return Task.CompletedTask;
        }

        private async Task SendMessagesAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Park (no CPU) until a message is enqueued or we're cancelled.
                    await _sendSignal.WaitAsync(token);

                    if (token.IsCancellationRequested || _webSocket.State != WebSocketState.Open)
                        break;

                    // Coalesce everything queued right now into one frame.
                    var downstreamMessages = new List<object> { new CheckInMessage(Identifier) };
                    while (_downstreamMessages.TryDequeue(out var msg))
                        downstreamMessages.Add(msg);

                    if (downstreamMessages.Count == 1)
                        continue; // surplus permit from a batched drain — nothing to send

                    var content = new ArraySegment<byte>(SerializeMessages(_encryptionKey, downstreamMessages));
                    await _webSocket.SendAsync(content, WebSocketMessageType.Binary, true, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelled because the connection dropped — expected, just exit.
            }
        }

        public async Task CloseAsync()
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing connection", CancellationToken.None);
            }
        }
    }
}
