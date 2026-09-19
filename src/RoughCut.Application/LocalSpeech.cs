using System.Globalization;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Application;

public sealed record LocalSpeechResult(string Provider, string Model, string Language, LocalSpeechSegment[] Segments);
public sealed record LocalSpeechSegment(long StartMilliseconds, long EndMilliseconds, string Text);
public sealed record SpeechTranscriptionReport(string AssetId, string Provider, string Model, string Language,
    int Chunks, SpeechSegment[] Segments);

public interface ILocalSpeechTranscriber
{
    Task<LocalSpeechResult> TranscribePcm16kMonoAsync(ReadOnlyMemory<byte> pcm,
        CancellationToken cancellationToken = default);
}

public sealed class LocalSpeechProcessor(string ffmpeg = "ffmpeg")
{
    public const int SampleRate = 16_000;
    public const int MaxDurationSeconds = 2 * 60 * 60;
    public const int DefaultChunkSeconds = 30;

    public async Task<SpeechTranscriptionReport> TranscribeAsync(EditProject project, string projectPath,
        string assetId, ILocalSpeechTranscriber transcriber, int chunkSeconds = DefaultChunkSeconds,
        CancellationToken cancellationToken = default)
    {
        ProjectValidator.EnsureValid(project);
        if (chunkSeconds is < 5 or > DefaultChunkSeconds) throw new ArgumentOutOfRangeException(nameof(chunkSeconds));
        var asset = project.Assets.SingleOrDefault(item => item.Id == assetId && item.Kind is "video" or "audio")
            ?? throw new KeyNotFoundException("Timed media asset was not found in the project.");
        var durationMs = TimeMath.ExactTicks(new(asset.Duration, project.TimeBase), new(1, 1000));
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
        for (long chunkStart = 0; chunkStart < durationMs; chunkStart += chunkSeconds * 1000L)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkDuration = Math.Min(chunkSeconds * 1000L, durationMs - chunkStart);
            var expectedBytes = checked((int)(chunkDuration * SampleRate * sizeof(short) / 1000));
            var result = await ToolProcess.RunAsync(ffmpeg,
                ["-v", "error", "-nostdin", "-xerror", "-ss", Seconds(chunkStart), "-t", Seconds(chunkDuration),
                 "-protocol_whitelist", "file", "-i", source, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
                 "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"],
                outputLimit: expectedBytes + 4096, timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
            if (result.Output.Length != expectedBytes || result.Output.Length % sizeof(short) != 0)
                throw new InvalidDataException("Decoded speech chunk has an invalid PCM length.");
            var transcript = await transcriber.TranscribePcm16kMonoAsync(result.Output, cancellationToken);
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
                    item.EndMilliseconds > chunkDuration || string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > 8000)
                    throw new InvalidDataException("Speech engine returned an invalid timed segment.");
                var start = TimeMath.ExactTicks(new(checked(chunkStart + item.StartMilliseconds), new(1, 1000)), project.TimeBase);
                var end = TimeMath.ExactTicks(new(checked(chunkStart + item.EndMilliseconds), new(1, 1000)), project.TimeBase);
                segments.Add(new($"stt-{segments.Count + 1}", assetId, start, end, item.Text.Trim(), [], "unknown"));
            }
            chunks++;
        }
        if (!string.Equals(await MediaReader.FingerprintAsync(source, cancellationToken), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Speech source changed during transcription.");
        return new(assetId, provider ?? "unknown", model ?? "unknown", language ?? "und", chunks, segments.ToArray());
    }

    private static string Seconds(long milliseconds) => (milliseconds / 1000m).ToString("0.000", CultureInfo.InvariantCulture);
}
