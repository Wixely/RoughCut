using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using RoughCut.Core;

namespace RoughCut.Media;

internal sealed record Packet(long Pts, long? Dts, long Duration, long Size, string Hash);
internal sealed record ExportSource(string Path, VideoIndex Video, int AudioIndex, int SampleRate, int Channels,
    TimeBase AudioTimeBase, Packet[] VideoPackets, Packet[] AudioPackets, bool CompletePngPackets,
    bool ExactAudioPackets, long AudioSamples, long MaximumAudioTimestampErrorMicroseconds);

internal sealed class ExportProbe(string ffmpeg, string ffprobe)
{
    public async Task<ExportSource> ReadAsync(string path, bool inspectPngPackets, CancellationToken cancellationToken)
    {
        var result = await ToolProcess.RunAsync(ffprobe,
            ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm",
             "-show_streams", "-show_packets", "-show_chapters", "-show_data_hash", "sha256", "-show_entries",
             "stream=index,codec_type,codec_name,time_base,start_pts,sample_rate,channels,initial_padding:packet=stream_index,pts,dts,duration,size,data_hash:chapter=id",
             "-of", "json", "-i", path], cancellationToken: cancellationToken);
        using var document = JsonDocument.Parse(result.Output);
        if (document.RootElement.TryGetProperty("chapters", out var chapters) && chapters.GetArrayLength() != 0)
            throw new NotSupportedException("Chapter retiming is not yet implemented; this slice rejects chapter-bearing inputs rather than discarding chapters.");
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        if (streams.Length != 2 || streams.Count(s => s.GetProperty("codec_type").GetString() == "video") != 1 ||
            streams.Count(s => s.GetProperty("codec_type").GetString() == "audio") != 1)
            throw new NotSupportedException("This export slice requires exactly one video and one audio stream in Matroska; other streams must not be silently discarded.");
        var audio = streams.Single(s => s.GetProperty("codec_type").GetString() == "audio");
        if (audio.GetProperty("codec_name").GetString() != "pcm_s16le")
            throw new NotSupportedException("This export slice supports PCM signed 16-bit little-endian audio only; compressed audio/priming is not yet validated.");
        var audioIndex = audio.GetProperty("index").GetInt32();
        int rate = int.Parse(audio.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture);
        int channels = audio.GetProperty("channels").GetInt32();
        if (rate != 48000 || channels is < 1 or > 2 ||
            !audio.TryGetProperty("start_pts", out var start) || start.GetInt64() != 0 ||
            (audio.TryGetProperty("initial_padding", out var padding) && padding.GetInt32() != 0))
            throw new NotSupportedException("Audio must start at zero, have no priming, and use 48 kHz mono/stereo PCM.");
        var ratio = audio.GetProperty("time_base").GetString()!.Split('/');
        var audioBase = new TimeBase(long.Parse(ratio[0], CultureInfo.InvariantCulture), long.Parse(ratio[1], CultureInfo.InvariantCulture));
        if (audioBase != new TimeBase(1, 1000)) throw new NotSupportedException("Initial audio packet validation requires a 1/1000 time base.");
        var video = await new MediaReader(ffmpeg, ffprobe).IndexAsync(path, cancellationToken);
        if (video.Info.Codec is not ("png" or "ffv1" or "h264") || video.Info.StartTicks != 0 ||
            video.Info.Width > 1920 || video.Info.Height > 1080 || video.Frames.Length > 6000 ||
            new MediaTime(video.Info.DurationTicks, video.Info.TimeBase).CompareTo(new(60, new(1, 1))) > 0)
            throw new NotSupportedException("Export is bounded to PNG/FFV1/H.264 SDR video, zero origin, 1920x1080, 6000 frames and 60 seconds.");
        var frameDuration = video.Frames[0].Duration;
        if (video.Frames.Where((f, i) => f.Pts != i * frameDuration || f.Duration != frameDuration).Any())
            throw new NotSupportedException("Export currently requires contiguous constant-duration frames; VFR/gaps remain unsupported.");
        TimeMath.ExactTicks(new(frameDuration, video.Info.TimeBase), new(1, 1000));
        var packets = document.RootElement.GetProperty("packets").EnumerateArray().ToArray();
        Packet[] ForStream(int index) => packets.Where(p => p.GetProperty("stream_index").GetInt32() == index)
            .Select(p => new Packet(p.GetProperty("pts").GetInt64(), p.TryGetProperty("dts", out var dts) ? dts.GetInt64() : null,
                p.GetProperty("duration").GetInt64(), long.Parse(p.GetProperty("size").GetString()!, CultureInfo.InvariantCulture),
                p.GetProperty("data_hash").GetString()!)).ToArray();
        var audioPackets = ForStream(audioIndex);
        var videoPackets = ForStream(video.Info.StreamIndex);
        if (videoPackets.Length != video.Frames.Length || audioPackets.Length == 0)
            throw new NotSupportedException("Unexpected packet/frame layout.");
        long samples = 0;
        long maxError = 0;
        bool exactAudio = true;
        foreach (var packet in audioPackets)
        {
            if (packet.Size <= 0 || packet.Size % (channels * 2) != 0 || packet.Pts != packet.Dts || packet.Duration <= 0)
                throw new NotSupportedException("Invalid PCM packet layout.");
            var packetSamples = packet.Size / (channels * 2);
            var difference = BigInteger.Abs((BigInteger)packet.Pts * rate - (BigInteger)samples * 1000);
            var durationError = BigInteger.Abs((BigInteger)packet.Duration * rate - (BigInteger)packetSamples * 1000);
            if (difference > rate || durationError > rate)
                throw new NotSupportedException("Audio packet timing has gaps or drift beyond the one-millisecond container tolerance.");
            maxError = Math.Max(maxError, checked((long)((difference * 1000 + rate - 1) / rate)));
            exactAudio &= difference == 0 && durationError == 0;
            samples = checked(samples + packetSamples);
        }
        if (samples != TimeMath.ExactTicks(new(video.Info.DurationTicks, video.Info.TimeBase), new(1, rate)))
            throw new NotSupportedException("Source audio/video lengths do not match exactly.");
        bool completePng = false;
        if (video.Info.Codec == "png" && inspectPngPackets)
        {
            var png = await ToolProcess.RunAsync(ffprobe,
                ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm", "-select_streams", "v:0",
                 "-show_packets", "-show_data", "-show_entries", "packet=data", "-of", "json", "-i", path], cancellationToken: cancellationToken);
            using var data = JsonDocument.Parse(png.Output);
            var payloads = data.RootElement.GetProperty("packets").EnumerateArray().ToArray();
            completePng = payloads.Length == videoPackets.Length && payloads.All(p =>
                IsStandalonePng(FromHexDump(p.GetProperty("data").GetString()!), video.Info.Width, video.Info.Height));
            completePng &= videoPackets.Where((p, i) => p.Pts != video.Frames[i].Pts || p.Dts != p.Pts || p.Duration != frameDuration).Count() == 0;
        }
        return new(path, video, audioIndex, rate, channels, audioBase, videoPackets, audioPackets,
            completePng, exactAudio, samples, maxError);
    }

    private static byte[] FromHexDump(string dump)
    {
        var hex = new StringBuilder();
        foreach (var line in dump.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) throw new InvalidDataException("Invalid packet dump.");
            var data = line[(colon + 2)..];
            var end = data.IndexOf("  ", StringComparison.Ordinal);
            hex.Append((end >= 0 ? data[..end] : data).Replace(" ", ""));
        }
        return Convert.FromHexString(hex.ToString());
    }

    // Codec-aware proof: a complete RGB8 PNG datastream in EACH packet, not just a keyframe flag.
    private static bool IsStandalonePng(byte[] data, int width, int height)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length < 45 || !data.AsSpan(0, 8).SequenceEqual(signature)) return false;
        int offset = 8;
        bool header = false, pixels = false;
        while (offset <= data.Length - 12)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            if (size > data.Length - offset - 12) return false;
            var type = Encoding.ASCII.GetString(data, offset + 4, 4);
            if (!header)
            {
                if (type != "IHDR" || size != 13 || BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 8, 4)) != width ||
                    BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 12, 4)) != height ||
                    !data.AsSpan(offset + 16, 5).SequenceEqual(new byte[] { 8, 2, 0, 0, 0 })) return false;
                header = true;
            }
            else if (type is "IHDR" or "acTL" or "fcTL" or "fdAT") return false;
            if (type == "IDAT") pixels = true;
            offset += checked((int)size + 12);
            if (type == "IEND") return size == 0 && pixels && offset == data.Length;
        }
        return false;
    }
}
