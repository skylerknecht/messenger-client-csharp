using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private readonly BlockingCollection<object> _upstreamMessages = new BlockingCollection<object>();
        private CancellationTokenSource _cancellationTokenSource;

        public WebSocketMessengerClient(string uri, byte[] encryptionKey, string userAgent, IWebProxy proxy = null)
        {
            _uri = new Uri(uri);
            _encryptionKey = encryptionKey;
            _userAgent = userAgent;
            _proxy = proxy;
            _webSocket = new ClientWebSocket();
        }

        public override async Task ConnectAsync()
        {
            if (_cancellationTokenSource != null)
            {
                try { _cancellationTokenSource.Cancel(); } catch { }
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
            }

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
            await ReadvertiseForwardersAsync();

            _cancellationTokenSource = new CancellationTokenSource();

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
            while (_webSocket.State == WebSocketState.Open && !Killed)
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

                            if (messages.Any(m => m is CheckOutMessage))
                            {
                                HandleCheckout();
                                break;
                            }

                            foreach (var message in messages)
                            {
                                DispatchMessage(message);
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

                        if (Killed)
                            break;
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
                try { _cancellationTokenSource.Cancel(); } catch { }
            }
        }

        public override Task SendUpstreamMessageAsync(object message)
        {
            _upstreamMessages.Add(message);
            return Task.CompletedTask;
        }

        private async Task SendMessagesAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var first = _upstreamMessages.Take(token);
                    if (_webSocket.State != WebSocketState.Open)
                        break;

                    var batch = new List<object> { new CheckInMessage(Identifier), first };
                    while (_upstreamMessages.TryTake(out var msg))
                        batch.Add(msg);

                    var content = new ArraySegment<byte>(SerializeMessages(_encryptionKey, batch));
                    await _webSocket.SendAsync(content, WebSocketMessageType.Binary, true, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public override void CloseTransport()
        {
            try { _cancellationTokenSource?.Cancel(); } catch { }
            try { _cancellationTokenSource?.Dispose(); } catch { }
            try { _webSocket?.Dispose(); } catch { }
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
