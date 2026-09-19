using System.Security.Cryptography;
using System.Text;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Application;

public sealed record VoicePreviewAudio(VoicePreviewInfo Info, byte[] Wav);

public static class VoiceWorkflow
{
    public static (EditProject Project, byte[] Wav, string RelativePath, bool Created) ImportPreview(
        EditProject project, string projectPath, string replacementId, byte[] wav, string runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime) || runtime.Length > 256)
            throw new ArgumentException("Synthesis runtime must contain 1 to 256 characters.");
        var replacement = project.Replacements.SingleOrDefault(item => item.Id == replacementId)
            ?? throw new KeyNotFoundException("Voice replacement was not found.");
        if (replacement.State != "requested") throw new InvalidOperationException("Voice replacement already has a generated preview.");
        var mapping = project.Voices.Single(item => item.Id == replacement.MappingId);
        var segment = project.Speech.Single(item => item.Id == replacement.SegmentId);
        var info = WaveAudio.Inspect(wav);
        var actualDuration = TimeMath.ExactTicks(new(info.Samples, new(1, info.SampleRate)), project.TimeBase);
        var requestedDuration = checked(segment.End - segment.Start);
        var hash = Convert.ToHexStringLower(SHA256.HashData(wav));
        var assetId = $"voice-{hash[..16]}";
        var relative = $"assets/audio/{hash}.wav";
        var asset = project.Assets.SingleOrDefault(item => item.Id == assetId);
        if (asset is not null && !string.Equals(asset.Sha256, hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Generated audio asset ID collision.");
        var destination = ProjectFiles.Resolve(projectPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var created = false;
        if (!File.Exists(destination))
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(wav);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination, overwrite: false);
                created = true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        else
        {
            var existing = File.ReadAllBytes(destination);
            if (existing.Length > WaveAudio.MaxBytes ||
                !string.Equals(Convert.ToHexStringLower(SHA256.HashData(existing)), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Existing voice preview does not match its content hash.");
        }
        var replacements = project.Replacements.ToArray();
        var replacementIndex = Array.FindIndex(replacements, item => item.Id == replacementId);
        replacements[replacementIndex] = replacement with { GeneratedAssetId = assetId, State = "preview" };
        var edited = project with
        {
            Revision = checked(project.Revision + 1),
            Assets = asset is null ? [.. project.Assets, new(assetId, "audio", relative, hash, actualDuration, 0, 0, "audio/wav")] : project.Assets,
            Replacements = replacements,
            Synthesis = [.. project.Synthesis, new(replacement.Id, mapping.Provider, mapping.Model, runtime,
                mapping.Voice, mapping.Language,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(replacement.Text))), hash,
                requestedDuration, actualDuration, replacement.FitPolicy)],
            Provenance = asset is null ? [.. project.Provenance, new(assetId, mapping.Provider, mapping.Model,
                segment.AssetId)] : project.Provenance
        };
        ProjectValidator.EnsureValid(edited);
        return (edited, wav, relative, created);
    }
}
