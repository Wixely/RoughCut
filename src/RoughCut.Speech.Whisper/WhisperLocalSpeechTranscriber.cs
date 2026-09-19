using System.Security.Cryptography;
using RoughCut.Application;
using Whisper.net;
using Whisper.net.Ggml;

namespace RoughCut.Speech.Whisper;

public sealed class WhisperLocalSpeechTranscriber(string modelPath, string language = "en")
    : ILocalSpeechTranscriber, IDisposable
{
    public const long BaseEnglishModelBytes = 147_964_211;
    public const string BaseEnglishModelSha256 = "A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;

    public async Task EnsureModelAsync(CancellationToken cancellationToken = default)
    {
        if (await IsValidModelAsync(cancellationToken)) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await IsValidModelAsync(cancellationToken)) return;
            if (File.Exists(modelPath))
                throw new InvalidDataException("Existing Whisper model failed its pinned size or SHA-256 check; remove it or choose another path.");
            var directory = Path.GetDirectoryName(Path.GetFullPath(modelPath))!;
            Directory.CreateDirectory(directory);
            var temporary = modelPath + ".download";
            try
            {
                await using var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                    GgmlType.BaseEn, cancellationToken: cancellationToken);
                await using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                    FileShare.None, 81_920, useAsync: true))
                {
                    await source.CopyToAsync(destination, cancellationToken);
                    destination.Flush(flushToDisk: true);
                }
                if (!await IsValidModelAsync(temporary, cancellationToken))
                    throw new InvalidDataException("Downloaded Whisper model failed its pinned size or SHA-256 check.");
                File.Move(temporary, modelPath, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    public async Task<LocalSpeechResult> TranscribePcm16kMonoAsync(ReadOnlyMemory<byte> pcm,
        CancellationToken cancellationToken = default)
    {
        if (pcm.Length == 0 || pcm.Length % sizeof(short) != 0)
            throw new ArgumentException("Whisper input must contain complete signed 16-bit samples.", nameof(pcm));
        await EnsureModelAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _factory ??= WhisperFactory.FromPath(modelPath);
            using var processor = _factory.CreateBuilder().WithLanguage(language).Build();
            using var wave = CreateWave(pcm);
            var segments = new List<LocalSpeechSegment>();
            var durationMilliseconds = checked((long)pcm.Length * 1000 / (LocalSpeechProcessor.SampleRate * sizeof(short)));
            await foreach (var segment in processor.ProcessAsync(wave, cancellationToken))
            {
                var text = segment.Text.Trim();
                if (text.Length == 0) continue;
                var start = Math.Max(0, checked((long)segment.Start.TotalMilliseconds));
                var end = Math.Min(durationMilliseconds, checked((long)segment.End.TotalMilliseconds));
                if (start >= end) continue;
                segments.Add(new(start, end, text));
            }
            return new("whisper.net", "base.en", language, segments.ToArray());
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> IsValidModelAsync(CancellationToken cancellationToken)
        => await IsValidModelAsync(modelPath, cancellationToken);

    private static async Task<bool> IsValidModelAsync(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != BaseEnglishModelBytes) return false;
        await using var stream = file.OpenRead();
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(hash, BaseEnglishModelSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static MemoryStream CreateWave(ReadOnlyMemory<byte> pcm)
    {
        var stream = new MemoryStream(44 + pcm.Length);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + pcm.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(LocalSpeechProcessor.SampleRate);
            writer.Write(LocalSpeechProcessor.SampleRate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(pcm.Length);
            writer.Write(pcm.Span);
        }
        stream.Position = 0;
        return stream;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }
}
