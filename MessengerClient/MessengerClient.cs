﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace MessengerClient
{
    internal abstract class MessengerClient
    {
        public byte[] Key;
        public Dictionary<string, TcpClient> clients = new Dictionary<string, TcpClient>();
        public readonly int bufferSize = 4096;

        public MessengerClient(byte[] key)
        {
            Key = key;
        }

        public abstract Task Connect(string uri);

        public string BytesToBase64(byte[] data)
        {
            return Convert.ToBase64String(data);
        }

        public byte[] Base64ToBytes(string data)
        {
            return Convert.FromBase64String(data);
        }

        public byte[] StringToBytes(string data)
        {
            return Encoding.UTF8.GetBytes(data);
        }

        public class Message
        {
            public string identifier { get; set; }
            public string msg { get; set; }
        }

        public class SocksConnectRequest
        {
            public string identifier { get; set; }
            public int atype { get; set; }
            public string address { get; set; }
            public int port { get; set; }
            public string client_id { get; set; }
        }

        public byte[] SocksConnectResults(string identifier, int rep, string bindAddr, int bindPort)
        {
            byte[] bindAddressBytes = string.IsNullOrEmpty(bindAddr) ? new byte[] { 0x00 } : IPAddress.Parse(bindAddr).GetAddressBytes();
            byte[] bindPortBytes = BitConverter.GetBytes((ushort)bindPort);
            Array.Reverse(bindPortBytes);

            var message = new byte[] {
                5,
                (byte)rep,
                0,
                1,
            };

            var fullMessage = new List<byte>();
            fullMessage.AddRange(message);
            fullMessage.AddRange(bindAddressBytes);
            fullMessage.AddRange(bindPortBytes);
            return fullMessage.ToArray();
        }
    }
}