using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;


// ----------------------------------------------------------------------
// 1. Message Classes (now using int instead of uint)
// ----------------------------------------------------------------------
public class CheckInMessage
{
    public string MessengerId { get; }
    public CheckInMessage(string messengerId)
    {
        MessengerId = messengerId;
    }
}

public class InitiateForwarderClientReq
{
    public string ForwarderClientId { get; }
    public string IpAddress { get; }
    public int Port { get; }

    public InitiateForwarderClientReq(string forwarderClientId, string ipAddress, int port)
    {
        ForwarderClientId = forwarderClientId;
        IpAddress = ipAddress;
        Port = port;
    }
}

public class InitiateForwarderClientRep
{
    public string ForwarderClientId { get; }
    public string BindAddress { get; }
    public int BindPort { get; }
    public int AddressType { get; }
    public int Reason { get; }

    public InitiateForwarderClientRep(
        string forwarderClientId,
        string bindAddress,
        int bindPort,
        int addressType,
        int reason)
    {
        ForwarderClientId = forwarderClientId;
        BindAddress = bindAddress;
        BindPort = bindPort;
        AddressType = addressType;
        Reason = reason;
    }
}

public class SendDataMessage
{
    public string ForwarderClientId { get; }
    public byte[] Data { get; }

    public SendDataMessage(string forwarderClientId, byte[] data)
    {
        ForwarderClientId = forwarderClientId;
        Data = data;
    }
}


// ----------------------------------------------------------------------
// 2. MessageParser: Reading/Decrypting Bytes
// ----------------------------------------------------------------------
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

    public static InitiateForwarderClientReq ParseInitiateForwarderClientReq(byte[] value)
    {
        var (forwarderClientId, remainder) = ReadString(value);
        var (ipAddress, remainder2) = ReadString(remainder);
        var (port, remainder3) = ReadUInt32(remainder2);

        return new InitiateForwarderClientReq(
            forwarderClientId,
            ipAddress,
            (int)port
        );
    }

    public static InitiateForwarderClientRep ParseInitiateForwarderClientRep(byte[] value)
    {
        var (forwarderClientId, remainder) = ReadString(value);
        var (bindAddress, remainder2) = ReadString(remainder);
        var (bindPort, remainder3) = ReadUInt32(remainder2);
        var (addressType, remainder4) = ReadUInt32(remainder3);
        var (reason, remainder5) = ReadUInt32(remainder4);

        return new InitiateForwarderClientRep(
            forwarderClientId,
            bindAddress,
            (int)bindPort,
            (int)addressType,
            (int)reason
        );
    }

    public static SendDataMessage ParseSendData(byte[] value)
    {
        var (forwarderClientId, remainder) = ReadString(value);
        var (encodedData, remainder2) = ReadString(remainder);

        byte[] rawData = Convert.FromBase64String(encodedData);
        return new SendDataMessage(
            forwarderClientId,
            rawData
        );
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
                    byte[] decrypted = MessengerClient.Crypto.Decrypt(encryptionKey, payload);
                    parsedMsg = ParseInitiateForwarderClientReq(decrypted);
                    break;
                }
            case 0x02:
                {
                    byte[] decrypted = MessengerClient.Crypto.Decrypt(encryptionKey, payload);
                    parsedMsg = ParseInitiateForwarderClientRep(decrypted);
                    break;
                }
            case 0x03:
                {
                    byte[] decrypted = MessengerClient.Crypto.Decrypt(encryptionKey, payload);
                    parsedMsg = ParseSendData(decrypted);
                    break;
                }
            case 0x04:
                {
                    parsedMsg = ParseCheckIn(payload);
                    break;
                }
            default:
                throw new ArgumentException($"Unknown message type: 0x{messageType:X}");
        }

        return (leftover, parsedMsg);
    }
}


// ----------------------------------------------------------------------
// 3. MessageBuilder: Creating/Encrypting Bytes
// ----------------------------------------------------------------------
public static class MessageBuilder
{
    public static byte[] SerializeMessage(byte[] encryptionKey, object msg)
    {
        byte[] payload;
        uint messageType;

        switch (msg)
        {
            case InitiateForwarderClientReq req:
                messageType = 0x01;
                payload = MessengerClient.Crypto.Encrypt(
                    encryptionKey,
                    BuildInitiateForwarderClientReq(
                        req.ForwarderClientId,
                        req.IpAddress,
                        req.Port
                    )
                );
                break;

            case InitiateForwarderClientRep rep:
                messageType = 0x02;
                payload = MessengerClient.Crypto.Encrypt(
                    encryptionKey,
                    BuildInitiateForwarderClientRep(
                        rep.ForwarderClientId,
                        rep.BindAddress,
                        rep.BindPort,
                        rep.AddressType,
                        rep.Reason
                    )
                );
                break;

            case SendDataMessage sdm:
                messageType = 0x03;
                payload = MessengerClient.Crypto.Encrypt(
                    encryptionKey,
                    BuildSendData(
                        sdm.ForwarderClientId,
                        sdm.Data
                    )
                );
                break;

            case CheckInMessage cim:
                messageType = 0x04;
                payload = BuildCheckInMessage(cim.MessengerId);
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

    public static byte[] BuildInitiateForwarderClientReq(
        string forwarderClientId,
        string ipAddress,
        int port)
    {
        var part1 = BuildString(forwarderClientId);
        var part2 = BuildString(ipAddress);

        byte[] part3 = new byte[4];
        WriteUInt32(part3, 0, (uint)port);

        return Combine(part1, part2, part3);
    }

    public static byte[] BuildInitiateForwarderClientRep(
        string forwarderClientId,
        string bindAddress,
        int bindPort,
        int addressType,
        int reason)
    {
        var part1 = BuildString(forwarderClientId);
        var part2 = BuildString(bindAddress);

        byte[] part3 = new byte[12];
        WriteUInt32(part3, 0, (uint)bindPort);
        WriteUInt32(part3, 4, (uint)addressType);
        WriteUInt32(part3, 8, (uint)reason);

        return Combine(part1, part2, part3);
    }

    public static byte[] BuildSendData(
        string forwarderClientId,
        byte[] data)
    {
        var part1 = BuildString(forwarderClientId);
        string encodedData = Convert.ToBase64String(data);
        var part2 = BuildString(encodedData);

        return Combine(part1, part2);
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
