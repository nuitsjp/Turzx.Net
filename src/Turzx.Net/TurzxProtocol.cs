using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Turzx;

internal static class TurzxProtocol
{
    internal static void Synchronize(WinUsbConnection connection)
    {
        connection.DrainPendingResponses();
        Exchange(connection, 10, []);
    }

    internal static void SendFrame(WinUsbConnection connection, TurzxFrame frame)
    {
        try { Exchange(connection, frame.Encoding == TurzxFrameEncoding.Jpeg ? (byte)101 : (byte)102, frame.Bytes); }
        catch (Exception ex) when (ex is not TurzxDeliveryUnknownException)
        {
            throw new TurzxDeliveryUnknownException("The frame transfer failed before a valid acknowledgement; delivery is unknown.", ex);
        }
    }

    private static void Exchange(WinUsbConnection connection, byte command, byte[] payload)
    {
        var header = new byte[504];
        header[0] = command;
        header[2] = 0x1A;
        header[3] = 0x6D;
        uint timestamp = (uint)DateTime.Now.TimeOfDay.TotalMilliseconds;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)payload.Length);
        using var des = DES.Create();
        des.Key = "slv3tuzx"u8.ToArray();
        des.IV = des.Key;
        des.Padding = PaddingMode.None;
        des.Mode = CipherMode.CBC;
        using var encryptor = des.CreateEncryptor();
        var packet = new byte[512 + payload.Length];
        encryptor.TransformFinalBlock(header, 0, header.Length).CopyTo(packet, 0);
        packet[510] = 0xA1;
        packet[511] = 0x1A;
        payload.CopyTo(packet, 512);
        connection.Write(packet);
        var response = connection.ReadResponse();
        if (response.Length < 6 || response[0] != command || response[1] != 0xC8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(2)) != timestamp)
            throw new IOException("Unexpected TURZX response.");
    }
}
