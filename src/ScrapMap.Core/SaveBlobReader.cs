using System.Buffers.Binary;

namespace ScrapMap.Core;

internal static class SaveBlobReader
{
    public static int ReadInt32BigEndian(ReadOnlySpan<byte> bytes) =>
        BinaryPrimitives.ReadInt32BigEndian(bytes);

    public static float ReadSingleBigEndian(ReadOnlySpan<byte> bytes)
    {
        var bits = BinaryPrimitives.ReadInt32BigEndian(bytes);
        return BitConverter.Int32BitsToSingle(bits);
    }

    public static string ReadReversedUuid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16) throw new ArgumentException("A UUID needs 16 bytes.", nameof(bytes));

        Span<byte> reversed = stackalloc byte[16];
        for (var i = 0; i < reversed.Length; i++)
        {
            reversed[i] = bytes[15 - i];
        }

        var hex = Convert.ToHexString(reversed).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}

