using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class RemotePortForwarder
    {
        private readonly MessengerClient _messenger;
        public string Identifier { get; }
        public string ListeningHost => _listeningHost;
        public int ListeningPort => _listeningPort;
        private readonly string _listeningHost;
        private readonly int _listeningPort;
        private readonly string _destinationHost;
        private readonly int _destinationPort;

        private TcpListener _tcpListener;
        public RemotePortForwarder(MessengerClient messenger, string bindId, string listeningHost, int listeningPort, string destinationHost, int destinationPort)
        {
            _messenger = messenger;
            Identifier = bindId;
            _listeningHost = listeningHost;
            _listeningPort = listeningPort;
            _destinationHost = destinationHost;
            _destinationPort = destinationPort;
        }

        public async Task<int> StartAsync()
        {
            try
            {
                var addresses = Dns.GetHostAddresses(_listeningHost);
                _tcpListener = new TcpListener(addresses[0], _listeningPort);
                _tcpListener.Start();
                Console.WriteLine($"[+] Remote Port Forwarder listening on {_listeningHost}:{_listeningPort}");
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[!] {_listeningHost}:{_listeningPort} is already in use or encountered an error: {ex.Message}");
                if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse) return 2;
                if (ex.SocketErrorCode == SocketError.AccessDenied) return 3;
                return 1;
            }

            if (_messenger.Killed)
            {
                try { _tcpListener.Stop(); } catch { }
                return 1;
            }

            _messenger.RemotePortForwarders.TryAdd(Identifier, this);
            _ = Task.Run(() => AcceptLoopAsync());
            return 0;
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _tcpListener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client);
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
            catch { }
            finally
            {
                await CleanupAsync();
            }
        }

        private async Task CleanupAsync()
        {
            RemotePortForwarder removed;
            if (!_messenger.RemotePortForwarders.TryRemove(Identifier, out removed))
                return;

            _messenger.CloseConnectionsForBind(Identifier);

            if (!_messenger.Killed)
            {
                try
                {
                    await _messenger.SendUpstreamMessageAsync(new InitiateBINDRep(Identifier, _listeningHost, _listeningPort, 5));
                }
                catch { }
            }
        }

        public void Stop()
        {
            try
            {
                _tcpListener?.Stop();
            }
            catch { }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            if (_messenger.Killed || !_messenger.RemotePortForwarders.ContainsKey(Identifier))
            {
                try { client.Close(); } catch { }
                return;
            }

            var clientId = MessengerClient.AlphanumericIdentifier();

            var connection = _messenger.RegisterTcpClient(clientId, client, bindId: Identifier);
            if (connection == null)
                return;

            if (!_messenger.RemotePortForwarders.ContainsKey(Identifier))
            {
                connection.Abort();
                return;
            }

            var upstreamMessage = new InitiateTCPClientReq(
                clientId,
                _destinationHost,
                _destinationPort,
                _listeningHost,
                _listeningPort
            );

            await _messenger.SendUpstreamMessageAsync(upstreamMessage);
        }
    }
}
