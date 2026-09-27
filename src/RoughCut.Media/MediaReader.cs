using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RoughCut.Core;

namespace RoughCut.Media;

public sealed record VideoInfo(string Sha256, int StreamIndex, string Codec, int Width, int Height,
    TimeBase TimeBase, long StartTicks, long DurationTicks, int FrameCount);
public sealed record FrameImage(FrameInfo Info, byte[] Png);
internal sealed record DecodedFrame(long Pts, long Duration);
internal sealed record VideoIndex(VideoInfo Info, DecodedFrame[] Frames);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(VideoInfo))]
[JsonSerializable(typeof(CutPoints))]
[JsonSerializable(typeof(AudioProfile))]
[JsonSerializable(typeof(MuxPlan))]
[JsonSerializable(typeof(MuxReport))]
public partial class MediaJson : JsonSerializerContext;

public sealed class MediaReader(string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
{
    private const string Containers = "mov,matroska,webm,avi,mpegts,ogg,flv";
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public async Task<VideoInfo> InspectAsync(string path, CancellationToken cancellationToken = default)
        => (await IndexAsync(path, cancellationToken)).Info;

    public async Task<FrameImage> GetFrameAsync(string path, FrameRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.AssetId)) throw new ArgumentException("Asset ID is required.");
        if (request.Timestamp.Ticks < 0 || !request.Timestamp.TimeBase.IsValid)
            throw new ArgumentException("Timestamp must be nonnegative with a positive time base.");
        if (request.MaxWidth is < 16 or > 1920) throw new ArgumentException("Maximum width must be between 16 and 1920.");
        path = Path.GetFullPath(path);
        var index = await IndexAsync(path, cancellationToken);
        var info = index.Info;
        int selected = -1;
        for (int i = 0; i < index.Frames.Length; i++)
        {
            var frame = index.Frames[i];
            var start = checked(frame.Pts - info.StartTicks);
            if (request.Timestamp.CompareTo(new(start, info.TimeBase)) >= 0 &&
                request.Timestamp.CompareTo(new(checked(start + frame.Duration), info.TimeBase)) < 0)
            { selected = i; break; }
        }
        if (selected < 0) throw new ArgumentOutOfRangeException(nameof(request), "No displayed frame covers the requested timestamp.");
        var filter = FormattableString.Invariant($"select=eq(n\\,{selected}),scale=w='min({request.MaxWidth},iw)':h=-1,format=rgb24");
        var result = await ToolProcess.RunAsync(ffmpeg,
            ["-v", "error", "-nostdin", "-xerror", "-protocol_whitelist", "file,pipe", "-format_whitelist", Containers,
             "-noautorotate", "-i", path, "-map", $"0:{info.StreamIndex}", "-vf", filter,
             "-frames:v", "1", "-fps_mode", "passthrough", "-c:v", "png", "-f", "image2pipe", "pipe:1"],
            cancellationToken: cancellationToken);
        if (await FingerprintAsync(path, cancellationToken) != info.Sha256)
            throw new InvalidDataException("Source changed during frame extraction; retry against a stable file.");
        var png = result.Output;
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(PngSignature))
            throw new InvalidDataException("Decoder did not produce a PNG frame.");
        var chosen = index.Frames[selected];
        return new(new(request.AssetId, info.Sha256, request.Timestamp,
            new(checked(chosen.Pts - info.StartTicks), info.TimeBase), new(chosen.Duration, info.TimeBase),
            info.StartTicks, selected, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)), "image/png", "FFmpeg default conversion to RGB24; no HDR tone mapping"), png);
    }

    internal async Task<VideoIndex> IndexAsync(string path, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Local media file does not exist.");
        var fingerprint = await FingerprintAsync(path, cancellationToken);
        var result = await ToolProcess.RunAsync(ffprobe,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-format_whitelist", Containers,
             "-select_streams", "V:0", "-show_streams", "-show_frames", "-show_entries",
             "stream=index,codec_name,width,height,time_base,start_pts,sample_aspect_ratio,color_transfer:stream_tags=rotate:stream_side_data=rotation:frame=best_effort_timestamp,duration,width,height",
             "-of", "json", "-i", path], timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);
        using var document = JsonDocument.Parse(result.Output);
        var streams = document.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() != 1) throw new InvalidDataException("A usable video stream is required.");
        var stream = streams[0];
        var width = stream.GetProperty("width").GetInt32();
        var height = stream.GetProperty("height").GetInt32();
        if (width <= 0 || height <= 0 || (long)width * height > 33_177_600)
            throw new InvalidDataException("Video dimensions exceed the initial slice's 8K pixel limit.");
        if (stream.TryGetProperty("sample_aspect_ratio", out var sar) && sar.GetString() is not ("1:1" or "N/A" or "0:1"))
            throw new NotSupportedException("Non-square pixels are not supported by this first frame-reader slice.");
        if ((stream.TryGetProperty("side_data_list", out var sideData) && sideData.EnumerateArray().Any(s =>
            s.TryGetProperty("rotation", out var rotation) && rotation.GetDouble() != 0)) ||
            (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate) && rotate.GetString() != "0"))
            throw new NotSupportedException("Rotated video is not supported by this first frame-reader slice.");
        if (stream.TryGetProperty("color_transfer", out var transfer) && transfer.GetString() is "smpte2084" or "arib-std-b67")
            throw new NotSupportedException("HDR tone mapping is not yet supported.");
        var ratio = stream.GetProperty("time_base").GetString()!.Split('/');
        var timeBase = new TimeBase(long.Parse(ratio[0], CultureInfo.InvariantCulture), long.Parse(ratio[1], CultureInfo.InvariantCulture));
        if (!timeBase.IsValid) throw new InvalidDataException("Invalid stream time base.");
        var frames = document.RootElement.GetProperty("frames");
        if (frames.GetArrayLength() is 0 or > 100_000)
            throw new NotSupportedException("Frame index must contain 1 to 100,000 frames in this slice.");
        var decoded = frames.EnumerateArray().Select(frame =>
        {
            if (frame.GetProperty("width").GetInt32() != width || frame.GetProperty("height").GetInt32() != height)
                throw new NotSupportedException("Changing frame dimensions are not yet supported.");
            return new DecodedFrame(frame.GetProperty("best_effort_timestamp").GetInt64(),
                frame.TryGetProperty("duration", out var duration) ? duration.GetInt64() : 0);
        }).ToArray();
        for (int i = 0; i < decoded.Length; i++)
        {
            var duration = decoded[i].Duration;
            if (i + 1 < decoded.Length)
            {
                var interval = checked(decoded[i + 1].Pts - decoded[i].Pts);
                if (interval <= 0) throw new NotSupportedException("Frame timestamps must increase strictly.");
                duration = duration > 0 ? Math.Min(duration, interval) : interval;
            }
            if (duration <= 0) throw new NotSupportedException("Final frame duration is unavailable.");
            decoded[i] = decoded[i] with { Duration = duration };
        }
        var start = stream.TryGetProperty("start_pts", out var startPts) ? startPts.GetInt64() : decoded[0].Pts;
        if (decoded[0].Pts < start) throw new NotSupportedException("Frames preceding the stream origin are not yet supported.");
        var total = checked(decoded[^1].Pts - start + decoded[^1].Duration);
        if (await FingerprintAsync(path, cancellationToken) != fingerprint)
            throw new InvalidDataException("Source changed during inspection; retry against a stable file.");
        return new(new(fingerprint, stream.GetProperty("index").GetInt32(), stream.GetProperty("codec_name").GetString()!,
            width, height, timeBase, start, total, decoded.Length), decoded);
    }

    public static async Task<string> FingerprintAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
