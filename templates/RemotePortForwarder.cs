using System;
using System.Collections.Generic;
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
        private readonly List<string> _clientIds = new List<string>();

        public RemotePortForwarder(MessengerClient messenger, string bindId, string listeningHost, int listeningPort, string destinationHost, int destinationPort)
        {
            _messenger = messenger;
            Identifier = bindId;
            _listeningHost = listeningHost;
            _listeningPort = listeningPort;
            _destinationHost = destinationHost;
            _destinationPort = destinationPort;
        }

        public async Task<bool> StartAsync()
        {
            try
            {
                var addresses = Dns.GetHostAddresses(_listeningHost);
                _tcpListener = new TcpListener(addresses[0], _listeningPort);
                _tcpListener.Start();
                Console.WriteLine($"[+] Remote Port Forwarder listening on {_listeningHost}:{_listeningPort}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            var client = await _tcpListener.AcceptTcpClientAsync();
                            _ = HandleClientAsync(client);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (SocketException)
                    {
                    }
                });

                return true;
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[!] {_listeningHost}:{_listeningPort} is already in use or encountered an error: {ex.Message}");
                return false;
            }
        }

        public void Stop()
        {
            try
            {
                _tcpListener?.Stop();
            }
            catch
            {
            }
        }

        public void CloseAllClients()
        {
            foreach (var clientId in _clientIds)
            {
                TcpClient client;
                if (_messenger.TcpClients.TryRemove(clientId, out client))
                {
                    try { client.Close(); } catch { }
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            var clientId = MessengerClient.AlphanumericIdentifier();
            _clientIds.Add(clientId);

            _messenger.TcpClients[clientId] = client;

            var downstreamMessage = new InitiateTCPClientReq(
                clientId,
                _destinationHost,
                _destinationPort
            );

            await _messenger.SendDownstreamMessageAsync(downstreamMessage);
        }
    }
}
