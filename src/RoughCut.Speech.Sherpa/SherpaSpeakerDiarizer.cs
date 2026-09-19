using System.Globalization;
using System.Security.Cryptography;
using RoughCut.Application;
using RoughCut.Core;
using RoughCut.Media;
using SherpaOnnx;

namespace RoughCut.Speech.Sherpa;

public sealed class SherpaSpeakerDiarizer : ISpeakerDiarizer
{
    public const string Provider = "sherpa-onnx";
    public const string RuntimeVersion = "1.13.8";
    public const int MaxDurationSeconds = 10 * 60;

    private readonly string _segmentationModel;
    private readonly string _embeddingModel;
    private readonly int _numSpeakers;
    private readonly float _threshold;
    private readonly string _ffmpeg;

    public SherpaSpeakerDiarizer(string segmentationModel, string embeddingModel,
        int numSpeakers = 0, float threshold = 0.5f, string ffmpeg = "ffmpeg")
    {
        _segmentationModel = RequireModel(segmentationModel, "Segmentation");
        _embeddingModel = RequireModel(embeddingModel, "Embedding");
        if (numSpeakers is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(numSpeakers));
        if (threshold is <= 0 or >= 1 || !float.IsFinite(threshold)) throw new ArgumentOutOfRangeException(nameof(threshold));
        _numSpeakers = numSpeakers;
        _threshold = threshold;
        _ffmpeg = ffmpeg;
    }

    public async Task<DiarizationSubmission> DiarizeAsync(EditProject project, string assetId, string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ProjectValidator.EnsureValid(project);
        var asset = project.Assets.SingleOrDefault(item => item.Id == assetId && item.Kind is "video" or "audio")
            ?? throw new KeyNotFoundException("Diarization asset was not found in the project.");
        var durationSeconds = new MediaTime(asset.Duration, project.TimeBase);
        if (durationSeconds.CompareTo(new(MaxDurationSeconds, new(1, 1))) > 0)
            throw new NotSupportedException("Local diarization is limited to 10 minutes per source in this runtime slice.");

        var segmentationHash = await FingerprintAsync(_segmentationModel, cancellationToken);
        var embeddingHash = await FingerprintAsync(_embeddingModel, cancellationToken);
        var maximumBytes = checked((int)(Math.Min(MaxDurationSeconds,
            decimal.ToInt64(decimal.Ceiling((decimal)asset.Duration * project.TimeBase.Numerator / project.TimeBase.Denominator))) * 16000L * 2 + 4096));
        var decoded = await ToolProcess.RunAsync(_ffmpeg,
            ["-v", "error", "-nostdin", "-xerror", "-protocol_whitelist", "file", "-i", sourcePath,
             "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"],
            outputLimit: maximumBytes, timeout: TimeSpan.FromMinutes(10), cancellationToken: cancellationToken);
        if (decoded.Output.Length == 0 || decoded.Output.Length % 2 != 0)
            throw new InvalidDataException("Decoded diarization audio is empty or malformed.");
        var samples = new float[decoded.Output.Length / 2];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(decoded.Output.AsSpan(index * 2, 2)) / 32768f;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = _segmentationModel;
            config.Segmentation.Pyannote.WindowShiftRatio = 0.1f;
            config.Embedding.Model = _embeddingModel;
            if (_numSpeakers > 0) config.Clustering.NumClusters = _numSpeakers;
            else config.Clustering.Threshold = _threshold;
            config.MinDurationOn = 0.3f;
            config.MinDurationOff = 0.5f;
            var diarizer = new OfflineSpeakerDiarization(config);
            if (diarizer.SampleRate != 16000)
                throw new InvalidDataException("Decoded audio sample rate does not match the diarization model.");
            var nativeSegments = await Task.Run(() => diarizer.Process(samples), CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            var turns = nativeSegments.Select(segment => new DiarizationTurn($"speaker-{segment.Speaker}",
                    SecondsToTicks(segment.Start, project.TimeBase, asset.Duration),
                    SecondsToTicks(segment.End, project.TimeBase, asset.Duration)))
                .Where(turn => turn.Start < turn.End).OrderBy(turn => turn.Start).ThenBy(turn => turn.End)
                .ThenBy(turn => turn.SpeakerKey, StringComparer.Ordinal).ToArray();
            if (turns.Length > DiarizationPlanner.MaxTurns)
                throw new InvalidDataException("Diarization returned too many turns.");
            if (!string.Equals(segmentationHash, await FingerprintAsync(_segmentationModel, cancellationToken), StringComparison.Ordinal) ||
                !string.Equals(embeddingHash, await FingerprintAsync(_embeddingModel, cancellationToken), StringComparison.Ordinal))
                throw new InvalidDataException("A diarization model changed during inference.");
            var model = $"pyannote-segmentation-3.0:{segmentationHash[..12]}+3dspeaker-eres2net:{embeddingHash[..12]}@{RuntimeVersion}";
            return new(assetId, Provider, model, turns);
        }
        finally { Array.Clear(samples); }
    }

    private static long SecondsToTicks(float seconds, TimeBase timeBase, long maximum)
    {
        if (!float.IsFinite(seconds) || seconds < 0) throw new InvalidDataException("Diarization returned an invalid timestamp.");
        var ticks = decimal.ToInt64(decimal.Round((decimal)seconds * timeBase.Denominator / timeBase.Numerator,
            0, MidpointRounding.AwayFromZero));
        return Math.Clamp(ticks, 0, maximum);
    }

    private static string RequireModel(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{name} model path is required.");
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"{name} model was not found.", path);
        return path;
    }

    private static async Task<string> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
