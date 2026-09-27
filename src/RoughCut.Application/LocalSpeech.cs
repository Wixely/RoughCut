using System.Globalization;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Application;

public sealed record LocalSpeechResult(string Provider, string Model, string Language, LocalSpeechSegment[] Segments);
public sealed record LocalSpeechSegment(long StartMilliseconds, long EndMilliseconds, string Text);
/// `PaddedMilliseconds` is the silence added where a decoded chunk arrived shorter than the interval it
/// covers. A real source does that at chunk boundaries — seeking and codec granularity do not divide evenly
/// — and the padding is reported rather than left to be inferred from nothing.
/// `DiscardedOverlapSegments` counts what the engine reported inside a chunk's overlapping prefix and was
/// therefore not believed: that material belongs to the chunk before, which already had it in view. Engines
/// emit a spurious fragment at the start of a window, so a chunk boundary otherwise invents speech.
public sealed record SpeechTranscriptionReport(string AssetId, string Provider, string Model, string Language,
    int Chunks, SpeechSegment[] Segments, long PaddedMilliseconds = 0, int DiscardedOverlapSegments = 0,
    int OverlapSeconds = 0);

public interface ILocalSpeechTranscriber
{
    Task<LocalSpeechResult> TranscribePcm16kMonoAsync(ReadOnlyMemory<byte> pcm,
        CancellationToken cancellationToken = default);
}

public sealed class LocalSpeechProcessor(string ffmpeg = "ffmpeg")
{
    public const int SampleRate = 16_000;
    public const int MaxDurationSeconds = 2 * 60 * 60;
    /// The window handed to the engine, and the largest one that makes sense: Whisper encodes exactly
    /// thirty seconds at a time, so a longer window is silently split and the engine starts cold again in the
    /// middle of it — which is the fabrication the overlap exists to prevent. Measured: a thirty-three second
    /// window produced a fragment at exactly thirty seconds into itself in fifteen of twenty-eight windows.
    public const int DefaultChunkSeconds = 30;
    /// How much shorter than its interval a decoded chunk may arrive before it is treated as a failure
    /// rather than padded. A quarter of a second is far beyond seek and codec granularity, and far below
    /// anything that would move a transcript's timings noticeably inside a thirty-second chunk.
    public const int MaxChunkShortfallMilliseconds = 250;
    /// How much of each window is context rather than material the window is responsible for. Windows used
    /// to own everything they contained, and an engine reliably reported a fragment at the very start of each
    /// one: on a real thirteen-minute source that invented a segment at 27 of its 28 window starts, a fifth
    /// of all the speech the transcript claimed. The context is transcribed and then not believed, so a
    /// boundary sits inside material the engine has already heard rather than at the edge of its world.
    ///
    /// It is taken out of the window, never added to it. Widening the window past the engine's own frame
    /// creates a second cold start inside it, which is the problem rather than the fix.
    public const int DefaultOverlapSeconds = 3;

    public async Task<SpeechTranscriptionReport> TranscribeAsync(EditProject project, string projectPath,
        string assetId, ILocalSpeechTranscriber transcriber, int chunkSeconds = DefaultChunkSeconds,
        CancellationToken cancellationToken = default, int? overlapSeconds = null)
    {
        ProjectValidator.EnsureValid(project);
        if (chunkSeconds is < 5 or > DefaultChunkSeconds) throw new ArgumentOutOfRangeException(nameof(chunkSeconds));
        // A narrow window cannot give most of itself away as context, so the default yields to the window it
        // sits in; what is left over is the material each window is actually responsible for.
        var overlap = overlapSeconds ?? Math.Min(DefaultOverlapSeconds, chunkSeconds / 5);
        if (overlap < 0 || 2 * overlap >= chunkSeconds) throw new ArgumentOutOfRangeException(nameof(overlapSeconds));
        var asset = project.Assets.SingleOrDefault(item => item.Id == assetId && item.Kind is "video" or "audio")
            ?? throw new KeyNotFoundException("Timed media asset was not found in the project.");
        // Rounded down, not demanded exactly: a source's length is rarely a whole number of milliseconds
        // — 4,623,872 ticks of 1/12288 is 376,291.67 ms — and insisting refused to transcribe anything
        // whose time base was not millisecond-friendly, RoughCut's own delivered files among them.
        // Flooring also keeps the last chunk inside the media rather than reaching past its end.
        var durationMs = TimeMath.FloorTicks(new(asset.Duration, project.TimeBase), new(1, 1000));
        if (durationMs > MaxDurationSeconds * 1000L)
            throw new NotSupportedException("Local speech processing is limited to two hours per source.");
        var source = ProjectFiles.Resolve(projectPath, asset.Path);
        if (!string.Equals(await MediaReader.FingerprintAsync(source, cancellationToken), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Speech source fingerprint differs from the project.");
        var segments = new List<SpeechSegment>();
        string? provider = null;
        string? model = null;
        string? language = null;
        var chunks = 0;
        long padded = 0;
        var discarded = 0;
        var overlapMs = overlap * 1000L;
        // Each window is one engine frame. It owns the span between its context margins and advances by that
        // span, so every millisecond of the source is owned by exactly one window and heard by two.
        var ownedMs = (chunkSeconds - 2 * overlap) * 1000L;
        for (long chunkStart = 0; chunkStart < durationMs; chunkStart += ownedMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkEnd = Math.Min(chunkStart + ownedMs, durationMs);
            var windowStart = Math.Max(0, chunkStart - overlapMs);
            var windowDuration = Math.Min(windowStart + chunkSeconds * 1000L, durationMs) - windowStart;
            var expectedBytes = checked((int)(windowDuration * SampleRate * sizeof(short) / 1000));
            var result = await ToolProcess.RunAsync(ffmpeg,
                ["-v", "error", "-nostdin", "-xerror", "-ss", Seconds(windowStart),
                 "-protocol_whitelist", "file", "-i", source, "-t", Seconds(windowDuration), "-map", "0:a:0", "-vn", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
                 "-af", "apad", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"],
                outputLimit: expectedBytes + 4096, timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
            var shortfall = expectedBytes - result.Output.Length;
            if (result.Output.Length % sizeof(short) != 0 || shortfall < 0 ||
                shortfall > MaxChunkShortfallMilliseconds * SampleRate * sizeof(short) / 1000)
                throw new InvalidDataException($"Decoded speech chunk has {result.Output.Length} bytes; expected {expectedBytes}.");
            var pcm = result.Output;
            if (pcm.Length != expectedBytes)
            {
                // Zero-filling the tail keeps every chunk the length its timings assume.
                padded += shortfall * 1000L / (SampleRate * sizeof(short));
                Array.Resize(ref pcm, expectedBytes);
            }
            var transcript = await transcriber.TranscribePcm16kMonoAsync(pcm, cancellationToken);
            if (string.IsNullOrWhiteSpace(transcript.Provider) || string.IsNullOrWhiteSpace(transcript.Model) ||
                string.IsNullOrWhiteSpace(transcript.Language)) throw new InvalidDataException("Speech provider provenance is incomplete.");
            provider ??= transcript.Provider;
            model ??= transcript.Model;
            language ??= transcript.Language;
            if (provider != transcript.Provider || model != transcript.Model || language != transcript.Language)
                throw new InvalidDataException("Speech provider provenance changed between chunks.");
            foreach (var item in transcript.Segments)
            {
                if (item.StartMilliseconds < 0 || item.EndMilliseconds <= item.StartMilliseconds ||
                    item.EndMilliseconds > windowDuration || string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > 8000)
                    throw new InvalidDataException($"Speech engine returned an invalid timed segment ({item.StartMilliseconds}-{item.EndMilliseconds} ms in a {windowDuration} ms window).");
                var absoluteStart = checked(windowStart + item.StartMilliseconds);
                if (absoluteStart < chunkStart || absoluteStart >= chunkEnd)
                {
                    // Reported outside the chunk itself: the neighbouring chunk owns that material and sees
                    // it in full. Discarding by start also means no segment can be kept twice.
                    discarded++;
                    continue;
                }
                // A transcript boundary is a model's estimate in whole milliseconds, not a frame, and most
                // time bases cannot represent every millisecond exactly — 1/12288 only represents multiples
                // of 125. Demanding exactness here refused to transcribe anything whose time base was not
                // millisecond-friendly, including RoughCut's own delivered files. These are rounded to the
                // nearest tick, which moves a boundary by less than one tick; edit boundaries stay exact.
                var start = TimeMath.NearestTicks(new(absoluteStart, new(1, 1000)), project.TimeBase);
                var end = TimeMath.NearestTicks(new(checked(windowStart + item.EndMilliseconds), new(1, 1000)), project.TimeBase);
                if (end <= start) continue;
                segments.Add(new($"stt-{segments.Count + 1}", assetId, start, end, item.Text.Trim(), [], "unknown"));
            }
            chunks++;
        }
        if (!string.Equals(await MediaReader.FingerprintAsync(source, cancellationToken), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Speech source changed during transcription.");
        return new(assetId, provider ?? "unknown", model ?? "unknown", language ?? "und", chunks,
            segments.ToArray(), padded, discarded, overlap);
    }

    private static string Seconds(long milliseconds) => (milliseconds / 1000m).ToString("0.000", CultureInfo.InvariantCulture);

}
