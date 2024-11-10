using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

namespace MessengerClient
{
    public static class MessageParser
    {
        public static int ReadUInt32(byte[] data, ref int offset)
        {
            if (data.Length < offset + 4)
                throw new ArgumentException("Insufficient data to read UInt32");

            int value = BitConverter.ToInt32(data.Skip(offset).Take(4).Reverse().ToArray(), 0); // Big-endian
            offset += 4;
            return value;
        }

        public static string ReadString(byte[] data, ref int offset)
        {
            int length = ReadUInt32(data, ref offset);
            if (data.Length < offset + length)
                throw new ArgumentException("Insufficient data to read string");

            string value = Encoding.UTF8.GetString(data, offset, length);
            offset += length;
            return value;
        }

        public static HeaderInfo ParseHeader(byte[] data, ref int offset)
        {
            int messageType = ReadUInt32(data, ref offset);
            int messageLength = ReadUInt32(data, ref offset);

            return new HeaderInfo
            {
                MessageType = messageType,
                MessageLength = messageLength
            };
        }

        public static Message ParseMessage(byte[] data)
        {
            int offset = 0;
            var header = ParseHeader(data, ref offset);

            int bodyLength = header.MessageLength - 8; // Subtract 8 bytes for the header
            if (offset + bodyLength > data.Length)
                throw new ArgumentException("Message body length exceeds available data");

            byte[] body = new byte[bodyLength];
            Array.Copy(data, offset, body, 0, bodyLength);

            switch (header.MessageType)
            {
                case 0x01:
                    return ParseInitiateForwarderClientReq(body);
                case 0x02:
                    return ParseInitiateForwarderClientRep(body);
                case 0x03:
                    return ParseSendData(body);
                case 0x04:
                    return ParseCheckIn(body);
                default:
                    throw new InvalidOperationException($"Unknown message type: {header.MessageType}");
            }
        }

        public static List<Message> ParseMessages(byte[] data)
        {
            int offset = 0;
            var messages = new List<Message>();

            while (offset < data.Length)
            {
                var header = ParseHeader(data, ref offset);
                int messageLength = header.MessageLength;

                if (offset + messageLength - 8 > data.Length)
                    throw new ArgumentException("Message body length exceeds available data");

                byte[] fullMessage = new byte[messageLength];
                Array.Copy(data, offset - 8, fullMessage, 0, messageLength);

                messages.Add(ParseMessage(fullMessage));
                offset += messageLength - 8;
            }

            return messages;
        }

        private static Message ParseInitiateForwarderClientReq(byte[] data)
        {
            int offset = 0;
            var forwarderClientId = ReadString(data, ref offset);
            var ipAddress = ReadString(data, ref offset);
            var port = ReadUInt32(data, ref offset);

            return new InitiateForwarderClientReqMessage
            {
                MessageType = 0x01,
                ForwarderClientId = forwarderClientId,
                IpAddress = ipAddress,
                Port = port
            };
        }

        private static Message ParseInitiateForwarderClientRep(byte[] data)
        {
            int offset = 0;
            var forwarderClientId = ReadString(data, ref offset);
            var bindAddress = ReadString(data, ref offset);
            var bindPort = ReadUInt32(data, ref offset);
            var addressType = ReadUInt32(data, ref offset);
            var reason = ReadUInt32(data, ref offset);

            return new InitiateForwarderClientRepMessage
            {
                MessageType = 0x02,
                ForwarderClientId = forwarderClientId,
                BindAddress = bindAddress,
                BindPort = bindPort,
                AddressType = addressType,
                Reason = reason
            };
        }

        private static Message ParseSendData(byte[] data)
        {
            int offset = 0;
            var forwarderClientId = ReadString(data, ref offset);
            var encodedData = ReadString(data, ref offset);
            var decodedData = Convert.FromBase64String(encodedData);

            return new SendDataMessage
            {
                MessageType = 0x03,
                ForwarderClientId = forwarderClientId,
                Data = decodedData
            };
        }

        private static Message ParseCheckIn(byte[] data)
        {
            int offset = 0;
            var messengerId = ReadString(data, ref offset);

            return new CheckInMessage
            {
                MessageType = 0x04,
                MessengerId = messengerId
            };
        }
    }

    public static class MessageBuilder
    {
        public static byte[] WriteUInt32(int value)
        {
            return BitConverter.GetBytes(value).Reverse().ToArray(); // Big-endian
        }

        public static byte[] WriteString(string value)
        {
            var encoded = Encoding.UTF8.GetBytes(value);
            var length = WriteUInt32(encoded.Length);
            return CombineArrays(length, encoded);
        }

        public static byte[] Header(int messageType, byte[] value)
        {
            var messageLength = 8 + value.Length;
            return CombineArrays(WriteUInt32(messageType), WriteUInt32(messageLength), value);
        }

        public static byte[] InitiateForwarderClientReq(string forwarderClientId, string ipAddress, int port)
        {
            var body = CombineArrays(
                WriteString(forwarderClientId),
                WriteString(ipAddress),
                WriteUInt32(port)
            );
            return Header(0x01, body);
        }

        public static byte[] InitiateForwarderClientRep(string forwarderClientId, string bindAddress, int bindPort, int addressType, int reason)
        {
            var body = CombineArrays(
                WriteString(forwarderClientId),
                WriteString(bindAddress),
                WriteUInt32(bindPort),
                WriteUInt32(addressType),
                WriteUInt32(reason)
            );
            return Header(0x02, body);
        }

        public static byte[] SendData(string forwarderClientId, byte[] data)
        {
            var encodedData = Convert.ToBase64String(data);
            var body = CombineArrays(
                WriteString(forwarderClientId),
                WriteString(encodedData)
            );
            return Header(0x03, body);
        }

        public static byte[] CheckIn(string messengerId)
        {
            var body = WriteString(messengerId);
            return Header(0x04, body);
        }

        private static byte[] CombineArrays(params byte[][] arrays)
        {
            int totalLength = arrays.Sum(arr => arr.Length);
            var result = new byte[totalLength];
            int offset = 0;

            foreach (var arr in arrays)
            {
                Array.Copy(arr, 0, result, offset, arr.Length);
                offset += arr.Length;
            }

            return result;
        }
    }

    public class HeaderInfo
    {
        public int MessageType { get; set; }
        public int MessageLength { get; set; }
    }

    public abstract class Message
    {
        public int MessageType { get; set; }
    }

    public class InitiateForwarderClientReqMessage : Message
    {
        public string ForwarderClientId { get; set; }
        public string IpAddress { get; set; }
        public int Port { get; set; }
    }

    public class InitiateForwarderClientRepMessage : Message
    {
        public string ForwarderClientId { get; set; }
        public string BindAddress { get; set; }
        public int BindPort { get; set; }
        public int AddressType { get; set; }
        public int Reason { get; set; }
    }

    public class SendDataMessage : Message
    {
        public string ForwarderClientId { get; set; }
        public byte[] Data { get; set; }
    }

    public class CheckInMessage : Message
    {
        public string MessengerId { get; set; }
    }
}
