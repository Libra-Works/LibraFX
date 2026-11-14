using System.Buffers.Binary;

namespace LiberaFx.Models.Graph;

public static class CommentImage
{
    public const int MaximumBytes = 512 * 1024;
    public const int MaximumSourceBytes = 16 * 1024 * 1024;
    private const int MaximumDimension = 4096;
    private const int MaximumSourceDimension = 8192;

    public static bool IsValid(ReadOnlySpan<byte> data)
    {
        return data.Length <= MaximumBytes && TryGetDimensions(data, out var width, out var height) &&
               ValidDimensions(width, height, MaximumDimension);
    }

    public static bool IsValidSource(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length > MaximumSourceBytes || !TryGetDimensions(data, out var w, out var h) ||
            !ValidDimensions(w, h, MaximumSourceDimension)) return false;
        width = (int)w;
        height = (int)h;
        return true;
    }

    private static bool TryGetDimensions(ReadOnlySpan<byte> data, out uint width, out uint height)
    {
        width = 0;
        height = 0;
        if (data.Length >= 24 &&
            data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
            height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
            return true;
        }
        if (data.Length < 4 || data[0] != 255 || data[1] != 216) return false;
        var index = 2;
        while (index < data.Length)
        {
            if (data[index++] != 255) return false;
            while (index < data.Length && data[index] == 255) index++;
            if (index >= data.Length) return false;
            var marker = data[index++];
            if (marker is 0xd8 or 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (marker is 0xd9 or 0xda || index + 2 > data.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
            if (length < 2 || index + length > data.Length) return false;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or
                0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (length < 7) return false;
                width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index + 5, 2));
                height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index + 3, 2));
                return true;
            }
            index += length;
        }
        return false;
    }

    private static bool ValidDimensions(uint width, uint height, int maximum) =>
        width > 0 && width <= maximum && height > 0 && height <= maximum;
}
