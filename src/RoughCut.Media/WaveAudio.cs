using System.Buffers.Binary;

namespace RoughCut.Media;

public sealed record WaveInfo(int SampleRate, int Channels, int BitsPerSample, long Samples);

public static class WaveAudio
{
    public const int MaxBytes = 16 * 1024 * 1024;

    public static WaveInfo Inspect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 44 || data.Length > MaxBytes || !data[..4].SequenceEqual("RIFF"u8) ||
            !data.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Voice preview must be a bounded RIFF/WAVE file.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) != data.Length - 8)
            throw new InvalidDataException("WAVE RIFF length is invalid.");
        int? sampleRate = null, channels = null, bits = null, blockAlign = null;
        long? dataBytes = null;
        var offset = 12;
        while (offset <= data.Length - 8)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var padded = length + (length & 1);
            if (length > int.MaxValue || offset + 8L + padded > data.Length) throw new InvalidDataException("WAVE chunk length is invalid.");
            var chunk = data.Slice(offset + 8, (int)length);
            if (data.Slice(offset, 4).SequenceEqual("fmt "u8))
            {
                if (sampleRate is not null || chunk.Length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(chunk[..2]) != 1)
                    throw new NotSupportedException("Only one PCM WAVE format chunk is supported.");
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(4, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(12, 2));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(14, 2));
            }
            else if (data.Slice(offset, 4).SequenceEqual("data"u8))
            {
                if (dataBytes is not null) throw new NotSupportedException("Multiple WAVE data chunks are not supported.");
                dataBytes = length;
            }
            offset += checked(8 + (int)padded);
        }
        if (offset != data.Length || sampleRate is null || channels is null || bits is null || blockAlign is null || dataBytes is null)
            throw new InvalidDataException("WAVE requires complete fmt and data chunks.");
        if (channels is < 1 or > 2 || sampleRate is < 8_000 or > 96_000 || bits != 16 || blockAlign != channels * 2 || dataBytes <= 0 || dataBytes % blockAlign != 0)
            throw new NotSupportedException("Voice preview must be mono/stereo 16-bit PCM at 8-96 kHz with complete samples.");
        return new(sampleRate.Value, channels.Value, bits.Value, dataBytes.Value / blockAlign.Value);
    }
}
