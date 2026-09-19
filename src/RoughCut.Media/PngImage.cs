using System.Buffers.Binary;

namespace RoughCut.Media;

public static class PngImage
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static (int Width, int Height) ReadDimensions(ReadOnlySpan<byte> data)
    {
        if (data.Length < 45 || !data[..8].SequenceEqual(Signature)) throw new InvalidDataException("Image is not a valid PNG.");
        var offset = 8;
        var width = 0;
        var height = 0;
        var sawHeader = false;
        var sawData = false;
        var sawEnd = false;
        while (offset <= data.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            if (length > int.MaxValue || offset + 12L + length > data.Length) throw new InvalidDataException("PNG chunk length is invalid.");
            var count = (int)length;
            var type = data.Slice(offset + 4, 4);
            var chunk = data.Slice(offset + 8, count);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8 + count, 4));
            if (Crc32(type, chunk) != expectedCrc) throw new InvalidDataException("PNG chunk checksum is invalid.");
            var name = System.Text.Encoding.ASCII.GetString(type);
            if (!sawHeader && name != "IHDR") throw new InvalidDataException("PNG header must be the first chunk.");
            if (name == "IHDR")
            {
                if (sawHeader || count != 13) throw new InvalidDataException("PNG header is invalid.");
                width = BinaryPrimitives.ReadInt32BigEndian(chunk[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(chunk.Slice(4, 4));
                if (width <= 0 || height <= 0 || (long)width * height > 33_177_600)
                    throw new InvalidDataException("PNG dimensions exceed the 8K pixel limit.");
                if (chunk[8] != 8 || chunk[9] is not (2 or 6) || chunk[10] != 0 || chunk[11] != 0 || chunk[12] != 0)
                    throw new NotSupportedException("Only non-interlaced 8-bit RGB or RGBA PNG images are supported.");
                sawHeader = true;
            }
            else if (name == "IDAT") sawData = true;
            else if (name == "IEND")
            {
                if (count != 0 || !sawData || offset + 12 != data.Length) throw new InvalidDataException("PNG end chunk is invalid.");
                sawEnd = true;
            }
            offset += 12 + count;
            if (sawEnd) break;
        }
        if (!sawHeader || !sawData || !sawEnd) throw new InvalidDataException("PNG is incomplete.");
        return (width, height);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> chunk)
    {
        uint crc = uint.MaxValue;
        foreach (var value in type) crc = Step(crc, value);
        foreach (var value in chunk) crc = Step(crc, value);
        return ~crc;
    }

    private static uint Step(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        return crc;
    }
}
