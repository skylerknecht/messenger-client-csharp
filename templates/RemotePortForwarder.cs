using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class RemotePortForwarder
    {
        private readonly MessengerClient _messenger;
        private readonly string _listeningHost;
        private readonly int _listeningPort;
        private readonly string _destinationHost;
        private readonly int _destinationPort;

        private TcpListener _tcpListener;

        public string Name { get; private set; }

        public RemotePortForwarder(MessengerClient messenger, string config)
        {
            _messenger = messenger;
            (_listeningHost, _listeningPort, _destinationHost, _destinationPort) = ParseConfig(config);
            Name = "Remote Port Forwarder";
        }

        public async Task StartAsync()
        {
            try
            {
                var addresses = Dns.GetHostAddresses(_listeningHost);
                _tcpListener = new TcpListener(addresses[0], _listeningPort);
                _tcpListener.Start();
                Console.WriteLine($"[+] Remote Port Forwarder {GetHashCode()} is listening on {_listeningHost}:{_listeningPort}");

                while (true)
                {
                    var client = await _tcpListener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client);
                }
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[!] {_listeningHost}:{_listeningPort} is already in use or encountered an error: {ex.Message}");
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            var clientId = Guid.NewGuid().ToString();

            var downstreamMessage = new InitiateForwarderClientReq(
                clientId,
                _destinationHost,
                _destinationPort
            );

            _messenger.ForwarderClients[clientId] = client;
            await _messenger.SendDownstreamMessageAsync(downstreamMessage);
        }

        private (string listeningHost, int listeningPort, string destinationHost, int destinationPort) ParseConfig(string config)
        {
            var parts = config.Split(':');
            if (parts.Length != 4)
                throw new ArgumentException("Invalid config format. Expected format: listeningHost:listeningPort:destinationHost:destinationPort");

            return (parts[0], int.Parse(parts[1]), parts[2], int.Parse(parts[3]));
        }
    }
}
