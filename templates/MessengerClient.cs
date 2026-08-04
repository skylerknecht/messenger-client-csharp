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
        public ConcurrentDictionary<string, TcpClient> ForwarderClients = new ConcurrentDictionary<string, TcpClient>();

        public Action OnConnected { get; set; }

        public abstract Task ConnectAsync();

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

        public async Task HandleInitiateForwarderClientReqAsync(InitiateForwarderClientReq message)
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
                ForwarderClients[message.ForwarderClientId] = client;

                var localEndPoint = (IPEndPoint)client.Client.LocalEndPoint;
                string bindAddress = localEndPoint.Address.ToString();
                int bindPort = localEndPoint.Port;
                int atype = (target.AddressFamily == AddressFamily.InterNetwork) ? 1 : 4;

                var repObj = new InitiateForwarderClientRep(
                    message.ForwarderClientId,
                    bindAddress,
                    bindPort,
                    atype,
                    0
                );

                await SendDownstreamMessageAsync(repObj);
                await StreamAsync(message.ForwarderClientId);
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
                    case SocketError.AddressFamilyNotSupported:
                        reason = 0x08;
                        break;
                    default:
                        reason = 0x01;
                        break;
                }

                var repObj = new InitiateForwarderClientRep(
                    message.ForwarderClientId,
                    "0.0.0.0",
                    0,
                    1,
                    reason
                );

                await SendDownstreamMessageAsync(repObj);
            }
            catch (ArgumentException)
            {
                var repObj = new InitiateForwarderClientRep(
                    message.ForwarderClientId,
                    "0.0.0.0",
                    0,
                    1,
                    0x04
                );

                await SendDownstreamMessageAsync(repObj);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Unhandled error: {ex}");
                var repObj = new InitiateForwarderClientRep(
                    message.ForwarderClientId,
                    "0.0.0.0",
                    0,
                    1,
                    0x01
                );

                await SendDownstreamMessageAsync(repObj);
            }
            finally
            {
                socket?.Dispose();
            }
        }

        protected async Task StreamAsync(string forwarderClientId)
        {
            if (!ForwarderClients.TryGetValue(forwarderClientId, out TcpClient client))
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

                    var sdmObj = new SendDataMessage(forwarderClientId, dataToSend);
                    await SendDownstreamMessageAsync(sdmObj);
                }
            }
            catch
            {
            }
            finally
            {
                stream?.Dispose();
                if (ForwarderClients.TryRemove(forwarderClientId, out var removed))
                {
                    removed.Close();
                    var closeObj = new SendDataMessage(forwarderClientId, Array.Empty<byte>());
                    await SendDownstreamMessageAsync(closeObj);
                }
            }
        }
    }
}
