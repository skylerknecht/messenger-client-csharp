using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MessengerClient
{
    public abstract class MessengerClient
    {
        // Stores active TcpClients with their unique identifiers
        public ConcurrentDictionary<string, TcpClient> ForwarderClients = new ConcurrentDictionary<string, TcpClient>();

        /// <summary>
        /// Establishes the connection to the server.
        /// </summary>
        public abstract Task ConnectAsync();

        /// <summary>
        /// Sends a downstream message to the server.
        /// </summary>
        /// <param name="messageData">The byte array containing the message data.</param>
        public abstract Task SendDownstreamMessageAsync(byte[] messageData);

        /// <summary>
        /// Handles an incoming message from the server.
        /// </summary>
        /// <param name="data">The byte array containing the message data.</param>
        public abstract Task HandleMessageAsync(Message message);

        /// <summary>
        /// Handles a request to initiate a forwarder client.
        /// </summary>
        /// <param name="message">The parsed message containing the request data.</param>
        public async Task HandleInitiateForwarderClientReqAsync(InitiateForwarderClientReqMessage message)
        {
            try
            {
                var client = new TcpClient();
                Console.WriteLine($"{message.IpAddress} {message.Port}");
                await client.ConnectAsync(message.IpAddress, message.Port);
                ForwarderClients[message.ForwarderClientId] = client;

                var bindAddress = ((System.Net.IPEndPoint)client.Client.LocalEndPoint).Address.ToString();
                var bindPort = ((System.Net.IPEndPoint)client.Client.LocalEndPoint).Port;

                var downstreamMessage = MessageBuilder.InitiateForwarderClientRep(message.ForwarderClientId, bindAddress, bindPort, 0, 0);
                await SendDownstreamMessageAsync(downstreamMessage);

                // Start streaming for this forwarder client
                _ = StreamAsync(message.ForwarderClientId);
            }
            catch (Exception ex) 
            {
                Console.WriteLine(ex);
                var downstreamMessage = MessageBuilder.InitiateForwarderClientRep(
                    message.ForwarderClientId, string.Empty, 0, 0, 1);
                await SendDownstreamMessageAsync(downstreamMessage);
            }
        }

        /// <summary>
        /// Streams data from a forwarder client to the server.
        /// </summary>
        /// <param name="forwarderClientId">The unique identifier of the forwarder client.</param>
        protected async Task StreamAsync(string forwarderClientId)
        {
            if (!ForwarderClients.TryGetValue(forwarderClientId, out var client))
                return;

            NetworkStream stream = null;

            try
            {
                stream = client.GetStream();
                var buffer = new byte[4096];
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    // Allocate a new array for the valid data
                    var dataToSend = new byte[bytesRead];
                    Array.Copy(buffer, 0, dataToSend, 0, bytesRead);

                    var downstreamMessage = MessageBuilder.SendData(forwarderClientId, dataToSend);
                    await SendDownstreamMessageAsync(downstreamMessage);
                }
            }
            catch
            {
                // Handle client disconnection or stream errors
            }
            finally
            {
                // Ensure cleanup
                if (stream != null)
                    stream.Dispose();

                // Remove the client from the dictionary
                ForwarderClients.TryRemove(forwarderClientId, out _);

                // Notify the server about client disconnection
                var closeMessage = MessageBuilder.SendData(forwarderClientId, Array.Empty<byte>());
                await SendDownstreamMessageAsync(closeMessage);
            }
        }
    }
}
