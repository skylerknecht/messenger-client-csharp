using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace MessengerClient
{
    public class TcpConnection
    {
        public MessengerClient Messenger;
        public string ClientId;
        public string BindId;
        public TcpClient Client;
        public NetworkStream Stream;
        public BlockingCollection<byte[]> WriteQueue;

        public TcpConnection(MessengerClient messenger, string clientId, TcpClient client, string bindId = null)
        {
            Messenger = messenger;
            ClientId = clientId;
            BindId = bindId;
            Client = client;
            Stream = client.GetStream();
            WriteQueue = new BlockingCollection<byte[]>();
        }

        public bool Abort()
        {
            var pair = new KeyValuePair<string, TcpConnection>(ClientId, this);
            if (!((ICollection<KeyValuePair<string, TcpConnection>>)Messenger.TcpClients).Remove(pair))
                return false;

            WriteQueue.CompleteAdding();
            try { Client.Close(); } catch { }
            return true;
        }

        public void AbortAndSignal()
        {
            if (Abort() && !Messenger.Killed)
                Messenger.SendUpstreamMessageAsync(new SendDataMessage(ClientId, Array.Empty<byte>()));
        }

        public void SendData(byte[] data)
        {
            if (data.Length == 0)
            {
                Abort();
            }
            else
            {
                try { WriteQueue.Add(data); } catch (InvalidOperationException) { }
            }
        }

        public void WriteLoop()
        {
            try
            {
                foreach (var d in WriteQueue.GetConsumingEnumerable())
                    Stream.Write(d, 0, d.Length);
            }
            catch
            {
                AbortAndSignal();
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
