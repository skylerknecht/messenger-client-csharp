using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace MessengerClient
{
    public abstract class MessengerClient
    {
        public string Identifier = string.Empty;
        public ConcurrentDictionary<string, TcpConnection> TcpClients = new ConcurrentDictionary<string, TcpConnection>();
        public ConcurrentDictionary<string, RemotePortForwarder> RemotePortForwarders = new ConcurrentDictionary<string, RemotePortForwarder>();
        public volatile bool Killed = false;

        public Action OnConnected { get; set; }

        public abstract Task ConnectAsync();

        public abstract Task StartAsync();

        public abstract Task SendUpstreamMessageAsync(object message);

        public abstract void CloseTransport();

        public void Cleanup()
        {
            foreach (var forwarder in RemotePortForwarders.Values.ToArray())
                forwarder.Stop();

            foreach (var connection in TcpClients.Values.ToArray())
                connection.Abort();

            CloseTransport();
        }

        public TcpConnection RegisterTcpClient(string clientId, TcpClient rawClient, string bindId = null)
        {
            if (Killed)
            {
                try { rawClient.Close(); } catch { }
                return null;
            }

            var connection = new TcpConnection(this, clientId, rawClient, bindId);
            if (!TcpClients.TryAdd(clientId, connection))
            {
                try { rawClient.Close(); } catch { }
                return null;
            }

            if (Killed)
            {
                connection.Abort();
                return null;
            }

            Task.Run(() => connection.WriteLoop());
            return connection;
        }

        public void CloseConnectionsForBind(string bindId)
        {
            foreach (var connection in TcpClients.Values.ToArray())
            {
                if (connection.BindId == bindId)
                    connection.Abort();
            }
        }

        public void DispatchMessage(object message)
        {
            switch (message)
            {
                case InitiateTCPClientReq reqMessage:
                    _ = Task.Run(async () => await HandleInitiateTCPClientReqAsync(reqMessage));
                    break;

                case InitiateTCPClientRep repMessage:
                    TcpConnection repConnection;
                    if (!TcpClients.TryGetValue(repMessage.ClientId, out repConnection))
                        break;
                    if (repMessage.Reason != 0)
                    {
                        repConnection.Abort();
                        break;
                    }
                    _ = repConnection.StreamAsync();
                    break;

                case SendDataMessage sendDataMessage:
                    TcpConnection sdmConnection;
                    if (!TcpClients.TryGetValue(sendDataMessage.ClientId, out sdmConnection))
                        break;
                    sdmConnection.SendData(sendDataMessage.Data);
                    break;

                case InitiateBINDReq bindReqMessage:
                    _ = Task.Run(async () => await HandleBindAsync(bindReqMessage));
                    break;

                case CheckInMessage checkInMessage:
                    Identifier = checkInMessage.MessengerId;
                    break;

                case CheckOutMessage _:
                    HandleCheckout();
                    break;

                default:
                    break;
            }
        }

        protected void HandleCheckout()
        {
            Console.WriteLine("[!] Kill signal received");
            Killed = true;

            foreach (var forwarder in RemotePortForwarders.Values.ToArray())
                forwarder.Stop();

            foreach (var connection in TcpClients.Values.ToArray())
                connection.Abort();
        }

        protected async Task ReadvertiseForwardersAsync()
        {
            foreach (var forwarder in RemotePortForwarders.Values.ToArray())
            {
                await SendUpstreamMessageAsync(
                    new InitiateBINDRep(forwarder.Identifier, forwarder.ListeningHost, forwarder.ListeningPort, 0));
            }
        }

        private const string _alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        public static string AlphanumericIdentifier(int length = 10)
        {
            var bytes = new byte[length];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = _alphanumeric[bytes[i] % _alphanumeric.Length];
            return new string(chars);
        }

        public static List<object> DeserializeMessages(byte[] encryptionKey, byte[] rawData)
        {
            var messages = new List<object>();
            byte[] leftover = rawData;

            while (leftover.Length >= 8)
            {
                var (newLeftover, parsedMessage) = MessageParser.DeserializeMessage(encryptionKey, leftover);
                if (parsedMessage == null)
                    break;
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
            if (Killed) return;

            if (string.IsNullOrEmpty(message.ListeningHost))
            {
                RemotePortForwarder existing;
                if (RemotePortForwarders.TryGetValue(message.BindId, out existing))
                    existing.Stop();
                return;
            }

            if (RemotePortForwarders.ContainsKey(message.BindId))
            {
                await SendUpstreamMessageAsync(new InitiateBINDRep(message.BindId, message.ListeningHost, message.ListeningPort, 0));
                return;
            }

            try
            {
                var forwarder = new RemotePortForwarder(this, message.BindId, message.ListeningHost, message.ListeningPort, message.DestinationHost, message.DestinationPort);
                int reason = await forwarder.StartAsync();
                if (reason != 0)
                {
                    if (!Killed)
                        await SendUpstreamMessageAsync(new InitiateBINDRep(message.BindId, message.ListeningHost, message.ListeningPort, reason));
                    return;
                }

                if (Killed)
                {
                    forwarder.Stop();
                    return;
                }

                await SendUpstreamMessageAsync(new InitiateBINDRep(message.BindId, message.ListeningHost, message.ListeningPort, 0));
            }
            catch
            {
                if (!Killed)
                    await SendUpstreamMessageAsync(new InitiateBINDRep(message.BindId, message.ListeningHost, message.ListeningPort, 1));
            }
        }

        public async Task HandleInitiateTCPClientReqAsync(InitiateTCPClientReq message)
        {
            if (Killed) return;
            Socket socket = null;
            TcpConnection connection = null;
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(message.DestinationHost);
                var target = addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6);

                socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                if (socket.AddressFamily == AddressFamily.InterNetworkV6)
                    socket.DualMode = true;

                var connectTask = socket.ConnectAsync(target, message.DestinationPort);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                    throw new SocketException((int)SocketError.TimedOut);
                await connectTask;

                if (Killed)
                {
                    socket.Dispose();
                    socket = null;
                    return;
                }

                var client = new TcpClient { Client = socket };
                connection = RegisterTcpClient(message.ClientId, client);
                if (connection == null)
                {
                    socket = null;
                    if (!Killed)
                    {
                        await SendUpstreamMessageAsync(new InitiateTCPClientRep(
                            message.ClientId, "0.0.0.0", 0, 1, 0x01,
                            "0.0.0.0", 0));
                    }
                    return;
                }
                socket = null;

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

                await SendUpstreamMessageAsync(repObj);
                await connection.StreamAsync();
            }
            catch (SocketException ex)
            {
                if (!Killed)
                {
                    byte reason;
                    switch (ex.SocketErrorCode)
                    {
                        case SocketError.NetworkUnreachable:
                            reason = 0x03;
                            break;
                        case SocketError.HostUnreachable:
                        case SocketError.HostNotFound:
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

                    await SendUpstreamMessageAsync(new InitiateTCPClientRep(
                        message.ClientId, "0.0.0.0", 0, 1, reason,
                        "0.0.0.0", 0));
                }
            }
            catch (ArgumentException)
            {
                if (!Killed)
                {
                    await SendUpstreamMessageAsync(new InitiateTCPClientRep(
                        message.ClientId, "0.0.0.0", 0, 1, 0x04,
                        "0.0.0.0", 0));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Unhandled error: {ex}");
                if (!Killed)
                {
                    await SendUpstreamMessageAsync(new InitiateTCPClientRep(
                        message.ClientId, "0.0.0.0", 0, 1, 0x01,
                        "0.0.0.0", 0));
                }
            }
            finally
            {
                if (connection != null)
                    connection.Abort();
                socket?.Dispose();
            }
        }
    }
}
