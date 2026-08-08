using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MessengerClient
{
    public abstract class MessengerClient
    {
        public string Identifier = string.Empty;
        public ConcurrentDictionary<string, TcpClient> TcpClients = new ConcurrentDictionary<string, TcpClient>();
        public List<RemotePortForwarder> RemotePortForwarders = new List<RemotePortForwarder>();

        public Action OnConnected { get; set; }

        public abstract Task ConnectAsync();

        public abstract Task StartAsync();

        public abstract Task SendDownstreamMessageAsync(object message);

        public abstract Task HandleMessageAsync(object message);

        private static readonly Random _random = new Random();
        private const string _alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        public static string AlphanumericIdentifier(int length = 10)
        {
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = _alphanumeric[_random.Next(_alphanumeric.Length)];
            return new string(chars);
        }

        public static List<object> DeserializeMessages(byte[] encryptionKey, byte[] rawData)
        {
            var messages = new List<object>();
            byte[] leftover = rawData;

            while (leftover.Length >= 8)
            {
                var (newLeftover, parsedMessage) = MessageParser.DeserializeMessage(encryptionKey, leftover);
                messages.Add(parsedMessage);
                leftover = newLeftover;
            }

            return messages;
        }

        public static byte[] SerializeMessages(byte[] encryptionKey, IEnumerable<object> messages)
        {
            MemoryStream ms = null;
            try
            {
                ms = new MemoryStream();
                foreach (var message in messages)
                {
                    byte[] singleMessageBytes = MessageBuilder.SerializeMessage(encryptionKey, message);
                    ms.Write(singleMessageBytes, 0, singleMessageBytes.Length);
                }
                return ms.ToArray();
            }
            finally
            {
                if (ms != null)
                    ms.Dispose();
            }
        }

        public async Task HandleBindAsync(InitiateBINDReq message)
        {
            RemotePortForwarder existing = null;
            lock (RemotePortForwarders)
            {
                existing = RemotePortForwarders.Find(f => f.Identifier == message.BindId);
                if (existing != null)
                    RemotePortForwarders.Remove(existing);
            }

            if (existing != null)
            {
                existing.CloseAllClients();
                existing.Stop();
                var rep = new InitiateBINDRep(message.BindId, "0.0.0.0", 0, 0);
                await SendDownstreamMessageAsync(rep);
                return;
            }

            try
            {
                var forwarder = new RemotePortForwarder(this, message.BindId, message.ListeningHost, message.ListeningPort, message.DestinationHost, message.DestinationPort);
                bool success = await forwarder.StartAsync();
                if (!success)
                {
                    var rep = new InitiateBINDRep(message.BindId, message.ListeningHost, message.ListeningPort, 1);
                    await SendDownstreamMessageAsync(rep);
                    return;
                }
                lock (RemotePortForwarders)
                {
                    RemotePortForwarders.Add(forwarder);
                }
                var successRep = new InitiateBINDRep(message.BindId, message.ListeningHost, message.ListeningPort, 0);
                await SendDownstreamMessageAsync(successRep);
            }
            catch
            {
                var rep = new InitiateBINDRep(message.BindId, message.ListeningHost, message.ListeningPort, 1);
                await SendDownstreamMessageAsync(rep);
            }
        }

        public async Task HandleInitiateTCPClientReqAsync(InitiateTCPClientReq message)
        {
            Socket socket = null;
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(message.IpAddress);
                var target = addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6);

                socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                if (socket.AddressFamily == AddressFamily.InterNetworkV6)
                    socket.DualMode = true;

                await socket.ConnectAsync(target, message.Port);

                var client = new TcpClient { Client = socket };
                socket = null;
                TcpClients[message.ClientId] = client;

                var localEndPoint = (IPEndPoint)client.Client.LocalEndPoint;
                var remoteEndPoint = (IPEndPoint)client.Client.RemoteEndPoint;
                string bindAddress = localEndPoint.Address.ToString();
                int bindPort = localEndPoint.Port;
                string remoteAddr = remoteEndPoint.Address.ToString();
                int remotePort = remoteEndPoint.Port;
                int atype = (target.AddressFamily == AddressFamily.InterNetwork) ? 1 : 4;

                var repObj = new InitiateTCPClientRep(
                    message.ClientId, bindAddress, bindPort, atype, 0,
                    remoteAddr, remotePort
                );

                await SendDownstreamMessageAsync(repObj);
                await StreamAsync(message.ClientId);
            }
            catch (SocketException ex)
            {
                byte reason;
                switch (ex.SocketErrorCode)
                {
                    case SocketError.NetworkUnreachable:
                        reason = 0x03;
                        break;
                    case SocketError.HostUnreachable:
                        reason = 0x04;
                        break;
                    case SocketError.ConnectionRefused:
                        reason = 0x05;
                        break;
                    case SocketError.TimedOut:
                        reason = 0x06;
                        break;
                    case SocketError.ProtocolNotSupported:
                        reason = 0x07;
                        break;
                    case SocketError.AddressFamilyNotSupported:
                        reason = 0x08;
                        break;
                    default:
                        reason = 0x01;
                        break;
                }

                var repObj = new InitiateTCPClientRep(
                    message.ClientId, "0.0.0.0", 0, 1, reason,
                    "0.0.0.0", 0
                );

                await SendDownstreamMessageAsync(repObj);
            }
            catch (ArgumentException)
            {
                var repObj = new InitiateTCPClientRep(
                    message.ClientId, "0.0.0.0", 0, 1, 0x04,
                    "0.0.0.0", 0
                );

                await SendDownstreamMessageAsync(repObj);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Unhandled error: {ex}");
                var repObj = new InitiateTCPClientRep(
                    message.ClientId, "0.0.0.0", 0, 1, 0x01,
                    "0.0.0.0", 0
                );

                await SendDownstreamMessageAsync(repObj);
            }
            finally
            {
                socket?.Dispose();
            }
        }

        protected async Task StreamAsync(string clientId)
        {
            if (!TcpClients.TryGetValue(clientId, out TcpClient client))
                return;

            NetworkStream stream = null;

            try
            {
                stream = client.GetStream();
                var buffer = new byte[4096];
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    var dataToSend = new byte[bytesRead];
                    Array.Copy(buffer, 0, dataToSend, 0, bytesRead);

                    var sdmObj = new SendDataMessage(clientId, dataToSend);
                    await SendDownstreamMessageAsync(sdmObj);
                }
            }
            catch
            {
            }
            finally
            {
                stream?.Dispose();
                if (TcpClients.TryRemove(clientId, out var removed))
                {
                    removed.Close();
                    var closeObj = new SendDataMessage(clientId, Array.Empty<byte>());
                    await SendDownstreamMessageAsync(closeObj);
                }
            }
        }
    }
}
