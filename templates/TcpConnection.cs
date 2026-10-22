using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceClient
{
    public class TcpConnection
    {
        public ServiceClient Messenger;
        public string ClientId;
        public string BindId;
        public TcpClient Client;
        public NetworkStream Stream;
        private readonly ConcurrentQueue<byte[]> _writeQueue;
        private readonly SemaphoreSlim _signal;
        private readonly CancellationTokenSource _cts;

        public TcpConnection(ServiceClient messenger, string clientId, TcpClient client, string bindId = null)
        {
            Messenger = messenger;
            ClientId = clientId;
            BindId = bindId;
            Client = client;
            Stream = client.GetStream();
            _writeQueue = new ConcurrentQueue<byte[]>();
            _signal = new SemaphoreSlim(0);
            _cts = new CancellationTokenSource();
        }

        public bool Abort()
        {
            var pair = new KeyValuePair<string, TcpConnection>(ClientId, this);
            if (!((ICollection<KeyValuePair<string, TcpConnection>>)Messenger.TcpClients).Remove(pair))
                return false;

            _cts.Cancel();
            try { Client.Close(); } catch { }
            return true;
        }

        public void AbortAndSignal()
        {
            if (Abort() && !Messenger.Killed)
                Messenger.SendUpstreamMessageAsync(new SendDataMessage(ClientId, Array.Empty<byte>()));
        }

        public void GracefulClose()
        {
            var pair = new KeyValuePair<string, TcpConnection>(ClientId, this);
            ((ICollection<KeyValuePair<string, TcpConnection>>)Messenger.TcpClients).Remove(pair);
            _cts.Cancel();
        }

        public void SendData(byte[] data)
        {
            if (data.Length == 0)
            {
                GracefulClose();
            }
            else
            {
                if (_cts.IsCancellationRequested) return;
                _writeQueue.Enqueue(data);
                _signal.Release();
            }
        }

        // Async producer-consumer pattern (SemaphoreSlim + ConcurrentQueue) replaces
        // the former BlockingCollection + synchronous WriteLoop. The old loop held a
        // thread pool thread for the lifetime of every TCP connection, which starved
        // the pool when many connections were open (e.g. 150 SOCKS-proxied hosts).
        // .NET Framework 4.7.2 lacks Channel<T> without extra DLLs, so we use the
        // same primitives the framework's own WebSocketBase uses internally.
        public async Task WriteLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await _signal.WaitAsync(_cts.Token);
                    while (_writeQueue.TryDequeue(out var d))
                        await Stream.WriteAsync(d, 0, d.Length);
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                AbortAndSignal();
            }
            finally
            {
                try { Client.Close(); } catch { }
            }
        }

        public async Task StreamAsync()
        {
            try
            {
                var buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = await Stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    var dataToSend = new byte[bytesRead];
                    Array.Copy(buffer, 0, dataToSend, 0, bytesRead);
                    await Messenger.SendUpstreamMessageAsync(new SendDataMessage(ClientId, dataToSend));
                }
            }
            catch { }
            finally
            {
                AbortAndSignal();
            }
        }
    }
}
