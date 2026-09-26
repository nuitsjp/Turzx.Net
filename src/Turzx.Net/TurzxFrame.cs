using System.Buffers.Binary;

namespace Turzx;

public enum TurzxFrameEncoding { Jpeg, Png }

/// <summary>An encoded 462×1920 portrait frame. It is rotated clockwise for the 1920×462 panel.</summary>
public sealed class TurzxFrame
{
    private readonly byte[] _bytes;
    private TurzxFrame(TurzxFrameEncoding encoding, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(bytes), "The encoded frame must be 1..1048576 bytes.");
        if (encoding == TurzxFrameEncoding.Png) ValidatePng(bytes);
        else ValidateJpeg(bytes);
        Encoding = encoding;
        _bytes = bytes.ToArray();
    }

    public TurzxFrameEncoding Encoding { get; }
    public int Width => 462;
    public int Height => 1920;
    internal byte[] Bytes => _bytes;
    public static TurzxFrame FromJpeg(ReadOnlySpan<byte> bytes) => new(TurzxFrameEncoding.Jpeg, bytes);
    public static TurzxFrame FromPng(ReadOnlySpan<byte> bytes) => new(TurzxFrameEncoding.Png, bytes);

    private static void ValidatePng(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]) != 462 || BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]) != 1920)
            throw new ArgumentException("Expected a 462×1920 PNG image.", nameof(bytes));
    }

    private static void ValidateJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            throw new ArgumentException("Expected a JPEG image.", nameof(bytes));
        int offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset++] != 0xFF) break;
            while (offset < bytes.Length && bytes[offset] == 0xFF) offset++;
            if (offset >= bytes.Length) break;
            byte marker = bytes[offset++];
            if (marker == 0xD9 || marker == 0xDA) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || offset + length > bytes.Length) break;
            if (marker == 0xC0)
            {
                if (length < 7 || BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]) != 1920 ||
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]) != 462)
                    throw new ArgumentException("Expected a 462×1920 baseline JPEG image.", nameof(bytes));
                return;
            }
            offset += length;
        }
        throw new ArgumentException("Expected a 462×1920 baseline JPEG image.", nameof(bytes));
    }
}
