using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;


// Thrown when an encrypted payload cannot be decrypted — almost always a wrong
// encryption key. Treated as fatal: the messenger can never decrypt server
// traffic, so Main logs once and stops instead of reconnecting in a loop.
public class DecryptionException : Exception
{
    public DecryptionException(string message, Exception inner) : base(message, inner) { }
}

public class CheckInMessage
{
    public string MessengerId { get; }
    public CheckInMessage(string messengerId)
    {
        MessengerId = messengerId;
    }
}

public class InitiateTCPClientReq
{
    public string ClientId { get; }
    public string DestinationHost { get; }
    public int DestinationPort { get; }
    public string ListeningHost { get; }
    public int ListeningPort { get; }

    public InitiateTCPClientReq(string clientId, string destinationHost, int destinationPort, string listeningHost = "", int listeningPort = 0)
    {
        ClientId = clientId;
        DestinationHost = destinationHost;
        DestinationPort = destinationPort;
        ListeningHost = listeningHost;
        ListeningPort = listeningPort;
    }
}

public class InitiateTCPClientRep
{
    public string ClientId { get; }
    public string BindAddress { get; }
    public int BindPort { get; }
    public int AddressType { get; }
    public int Reason { get; }
    public string RemoteAddr { get; }
    public int RemotePort { get; }

    public InitiateTCPClientRep(
        string clientId,
        string bindAddress,
        int bindPort,
        int addressType,
        int reason,
        string remoteAddr,
        int remotePort)
    {
        ClientId = clientId;
        BindAddress = bindAddress;
        BindPort = bindPort;
        AddressType = addressType;
        Reason = reason;
        RemoteAddr = remoteAddr;
        RemotePort = remotePort;
    }
}

public class SendDataMessage
{
    public string ClientId { get; }
    public byte[] Data { get; }

    public SendDataMessage(string clientId, byte[] data)
    {
        ClientId = clientId;
        Data = data;
    }
}

public class InitiateBINDReq
{
    public string BindId { get; }
    public string ListeningHost { get; }
    public int ListeningPort { get; }
    public string DestinationHost { get; }
    public int DestinationPort { get; }

    public InitiateBINDReq(string bindId, string listeningHost, int listeningPort, string destinationHost, int destinationPort)
    {
        BindId = bindId;
        ListeningHost = listeningHost;
        ListeningPort = listeningPort;
        DestinationHost = destinationHost;
        DestinationPort = destinationPort;
    }
}

public class InitiateBINDRep
{
    public string BindId { get; }
    public string ListeningHost { get; }
    public int ListeningPort { get; }
    public int Reason { get; }

    public InitiateBINDRep(string bindId, string listeningHost, int listeningPort, int reason)
    {
        BindId = bindId;
        ListeningHost = listeningHost;
        ListeningPort = listeningPort;
        Reason = reason;
    }
}


public static class MessageParser
{
    public static (uint Value, byte[] Remainder) ReadUInt32(byte[] data)
    {
        if (data.Length < 4)
            throw new ArgumentException("Not enough bytes to read a 32-bit value.");

        uint value = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        byte[] remainder = data.Skip(4).ToArray();

        return (value, remainder);
    }

    public static (string Value, byte[] Remainder) ReadString(byte[] data)
    {
        var (length, remainder) = ReadUInt32(data);
        if (remainder.Length < length)
            throw new ArgumentException($"Not enough bytes to read string of length {length}.");

        string s = Encoding.UTF8.GetString(remainder, 0, (int)length);
        byte[] leftover = remainder.Skip((int)length).ToArray();

        return (s, leftover);
    }

    public static CheckInMessage ParseCheckIn(byte[] value)
    {
        var (messengerId, _) = ReadString(value);
        return new CheckInMessage(messengerId);
    }

    public static InitiateTCPClientReq ParseInitiateTCPClientReq(byte[] value)
    {
        var (clientId, remainder) = ReadString(value);
        var (destinationHost, remainder2) = ReadString(remainder);
        var (destinationPort, remainder3) = ReadUInt32(remainder2);

        // Optional listening endpoint appended by a remote port forwarder.
        string listeningHost = "";
        uint listeningPort = 0;
        if (remainder3.Length > 0)
        {
            var (lh, remainder4) = ReadString(remainder3);
            var (lp, _) = ReadUInt32(remainder4);
            listeningHost = lh;
            listeningPort = lp;
        }

        return new InitiateTCPClientReq(clientId, destinationHost, (int)destinationPort, listeningHost, (int)listeningPort);
    }

    public static InitiateTCPClientRep ParseInitiateTCPClientRep(byte[] value)
    {
        var (clientId, remainder) = ReadString(value);
        var (bindAddress, remainder2) = ReadString(remainder);
        var (bindPort, remainder3) = ReadUInt32(remainder2);
        var (addressType, remainder4) = ReadUInt32(remainder3);
        var (reason, remainder5) = ReadUInt32(remainder4);

        // remote_addr / remote_port are optional — the server omits them when
        // it has no remote info (e.g. a reason!=0 denial). Only read them if
        // bytes remain, otherwise a Rep without them overruns the buffer.
        string remoteAddr = "";
        uint remotePort = 0;
        if (remainder5.Length > 0)
        {
            var (ra, remainder6) = ReadString(remainder5);
            var (rp, _) = ReadUInt32(remainder6);
            remoteAddr = ra;
            remotePort = rp;
        }

        return new InitiateTCPClientRep(
            clientId, bindAddress, (int)bindPort, (int)addressType, (int)reason,
            remoteAddr, (int)remotePort
        );
    }

    public static SendDataMessage ParseSendData(byte[] value)
    {
        var (clientId, remainder) = ReadString(value);
        var (encodedData, _) = ReadString(remainder);

        byte[] rawData = Convert.FromBase64String(encodedData);
        return new SendDataMessage(clientId, rawData);
    }

    public static InitiateBINDReq ParseInitiateBINDReq(byte[] value)
    {
        var (bindId, remainder) = ReadString(value);
        var (listeningHost, remainder2) = ReadString(remainder);
        var (listeningPort, remainder3) = ReadUInt32(remainder2);
        var (destinationHost, remainder4) = ReadString(remainder3);
        var (destinationPort, _) = ReadUInt32(remainder4);

        return new InitiateBINDReq(bindId, listeningHost, (int)listeningPort, destinationHost, (int)destinationPort);
    }

    public static InitiateBINDRep ParseInitiateBINDRep(byte[] value)
    {
        var (bindId, remainder) = ReadString(value);
        var (listeningHost, remainder2) = ReadString(remainder);
        var (listeningPort, remainder3) = ReadUInt32(remainder2);
        var (reason, _) = ReadUInt32(remainder3);

        return new InitiateBINDRep(bindId, listeningHost, (int)listeningPort, (int)reason);
    }

    private static byte[] DecryptOrThrow(byte[] encryptionKey, byte[] payload)
    {
        try
        {
            return MessengerClient.Crypto.Decrypt(encryptionKey, payload);
        }
        catch (DecryptionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DecryptionException("Failed to decrypt payload", ex);
        }
    }

    public static (byte[] leftover, object parsedMessage) DeserializeMessage(
        byte[] encryptionKey,
        byte[] rawData)
    {
        var (messageType, dataAfterType) = ReadUInt32(rawData);
        var (messageLength, dataAfterLength) = ReadUInt32(dataAfterType);

        if (messageLength < 8)
            throw new ArgumentException("Invalid message: length field too small.");

        int payloadLen = (int)(messageLength - 8);
        if (dataAfterLength.Length < payloadLen)
            throw new ArgumentException("Not enough bytes in data for the payload.");

        byte[] payload = dataAfterLength.Take(payloadLen).ToArray();
        byte[] leftover = dataAfterLength.Skip(payloadLen).ToArray();

        object parsedMsg;
        switch (messageType)
        {
            case 0x01:
                {
                    byte[] decrypted = DecryptOrThrow(encryptionKey, payload);
                    parsedMsg = ParseInitiateTCPClientReq(decrypted);
                    break;
                }
            case 0x02:
                {
                    byte[] decrypted = DecryptOrThrow(encryptionKey, payload);
                    parsedMsg = ParseInitiateTCPClientRep(decrypted);
                    break;
                }
            case 0x03:
                {
                    byte[] decrypted = DecryptOrThrow(encryptionKey, payload);
                    parsedMsg = ParseSendData(decrypted);
                    break;
                }
            case 0x04:
                {
                    parsedMsg = ParseCheckIn(payload);
                    break;
                }
            case 0x05:
                {
                    byte[] decrypted = DecryptOrThrow(encryptionKey, payload);
                    parsedMsg = ParseInitiateBINDReq(decrypted);
                    break;
                }
            case 0x06:
                {
                    byte[] decrypted = DecryptOrThrow(encryptionKey, payload);
                    parsedMsg = ParseInitiateBINDRep(decrypted);
                    break;
                }
            default:
                throw new ArgumentException($"Unknown message type: 0x{messageType:X}");
        }

        return (leftover, parsedMsg);
    }
}


public static class MessageBuilder
{
    public static byte[] SerializeMessage(byte[] encryptionKey, object msg)
    {
        byte[] payload;
        uint messageType;

        switch (msg)
        {
            case InitiateTCPClientReq req:
                messageType = 0x01;
                payload = MessengerClient.Crypto.Encrypt(
                    encryptionKey,
                    BuildInitiateTCPClientReq(req.ClientId, req.DestinationHost, req.DestinationPort, req.ListeningHost, req.ListeningPort)
                );
                break;

            case InitiateTCPClientRep rep:
                messageType = 0x02;
                payload = MessengerClient.Crypto.Encrypt(
                    encryptionKey,
                    BuildInitiateTCPClientRep(
                        rep.ClientId, rep.BindAddress, rep.BindPort,
                        rep.AddressType, rep.Reason, rep.RemoteAddr, rep.RemotePort
                    )
                );
                break;

            case SendDataMessage sdm:
                messageType = 0x03;
                payload = MessengerClient.Crypto.Encrypt(
                    encryptionKey,
                    BuildSendData(sdm.ClientId, sdm.Data)
                );
                break;

            case CheckInMessage cim:
                messageType = 0x04;
                payload = BuildCheckInMessage(cim.MessengerId);
                break;

            case InitiateBINDReq bindReq:
                messageType = 0x05;
                payload = MessengerClient.Crypto.Encrypt(
                    encryptionKey,
                    BuildInitiateBINDReq(
                        bindReq.BindId, bindReq.ListeningHost, bindReq.ListeningPort,
                        bindReq.DestinationHost, bindReq.DestinationPort
                    )
                );
                break;

            case InitiateBINDRep bindRep:
                messageType = 0x06;
                payload = MessengerClient.Crypto.Encrypt(
                    encryptionKey,
                    BuildInitiateBINDRep(
                        bindRep.BindId, bindRep.ListeningHost, bindRep.ListeningPort,
                        bindRep.Reason
                    )
                );
                break;

            default:
                throw new ArgumentException($"Unknown message type: {msg.GetType().Name}");
        }

        return BuildMessage(messageType, payload);
    }

    public static byte[] BuildMessage(uint messageType, byte[] payload)
    {
        uint messageLength = (uint)(8 + payload.Length);

        byte[] header = new byte[8];
        WriteUInt32(header, 0, messageType);
        WriteUInt32(header, 4, messageLength);

        return Combine(header, payload);
    }

    public static byte[] BuildString(string value)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        byte[] lengthBytes = new byte[4];
        WriteUInt32(lengthBytes, 0, (uint)encoded.Length);

        return Combine(lengthBytes, encoded);
    }

    public static byte[] BuildCheckInMessage(string messengerId)
    {
        return BuildString(messengerId);
    }

    public static byte[] BuildInitiateTCPClientReq(string clientId, string destinationHost, int destinationPort,
                                                   string listeningHost = "", int listeningPort = 0)
    {
        var part1 = BuildString(clientId);
        var part2 = BuildString(destinationHost);

        byte[] part3 = new byte[4];
        WriteUInt32(part3, 0, (uint)destinationPort);

        if (!string.IsNullOrEmpty(listeningHost))
        {
            var part4 = BuildString(listeningHost);
            byte[] part5 = new byte[4];
            WriteUInt32(part5, 0, (uint)listeningPort);
            return Combine(part1, part2, part3, part4, part5);
        }

        return Combine(part1, part2, part3);
    }

    public static byte[] BuildInitiateTCPClientRep(
        string clientId, string bindAddress, int bindPort,
        int addressType, int reason, string remoteAddr, int remotePort)
    {
        var part1 = BuildString(clientId);
        var part2 = BuildString(bindAddress);

        byte[] part3 = new byte[12];
        WriteUInt32(part3, 0, (uint)bindPort);
        WriteUInt32(part3, 4, (uint)addressType);
        WriteUInt32(part3, 8, (uint)reason);

        var part4 = BuildString(remoteAddr);

        byte[] part5 = new byte[4];
        WriteUInt32(part5, 0, (uint)remotePort);

        return Combine(part1, part2, part3, part4, part5);
    }

    public static byte[] BuildSendData(string clientId, byte[] data)
    {
        var part1 = BuildString(clientId);
        string encodedData = Convert.ToBase64String(data);
        var part2 = BuildString(encodedData);

        return Combine(part1, part2);
    }

    public static byte[] BuildInitiateBINDReq(
        string bindId, string listeningHost, int listeningPort,
        string destinationHost, int destinationPort)
    {
        var part1 = BuildString(bindId);
        var part2 = BuildString(listeningHost);

        byte[] part3 = new byte[4];
        WriteUInt32(part3, 0, (uint)listeningPort);

        var part4 = BuildString(destinationHost);

        byte[] part5 = new byte[4];
        WriteUInt32(part5, 0, (uint)destinationPort);

        return Combine(part1, part2, part3, part4, part5);
    }

    public static byte[] BuildInitiateBINDRep(
        string bindId, string listeningHost, int listeningPort, int reason)
    {
        var part1 = BuildString(bindId);
        var part2 = BuildString(listeningHost);

        byte[] part3 = new byte[8];
        WriteUInt32(part3, 0, (uint)listeningPort);
        WriteUInt32(part3, 4, (uint)reason);

        return Combine(part1, part2, part3);
    }

    public static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    public static byte[] Combine(params byte[][] arrays)
    {
        int totalLength = 0;
        foreach (var arr in arrays)
            totalLength += arr.Length;

        byte[] result = new byte[totalLength];
        int offset = 0;

        foreach (var arr in arrays)
        {
            Buffer.BlockCopy(arr, 0, result, offset, arr.Length);
            offset += arr.Length;
        }

        return result;
    }
}
