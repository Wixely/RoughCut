using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Desktop;

/// A playable copy of one source, keyed by that source's fingerprint. It is built once and reused for
/// every revision, because editing changes which parts of the source are shown, never the source itself.
public sealed record SourceProxy(string Path, string AssetId, string SourceSha256, double DurationSeconds, long Length);

public sealed class SourceProxyBuilder(string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
{
    public const long MaxProxyBytes = 512L * 1024 * 1024;
    private const int MaxHeight = 720;

    /// Transcodes to the only format the desktop player decodes. Nothing here is an export: the result is
    /// a disposable preview, never validated frame by frame and never published as output.
    public async Task<SourceProxy> PrepareAsync(string projectPath, EditProject project, string assetId,
        CancellationToken token = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        var asset = project.Assets.SingleOrDefault(item => item.Id == assetId && item.Kind == "video")
            ?? throw new DesktopPlaybackUnavailableException("The project has no video asset to preview.");
        var source = ProjectFiles.Resolve(projectPath, asset.Path);
        if (!File.Exists(source)) throw new DesktopPlaybackUnavailableException("The source media is missing.");

        var cache = Path.Combine(Path.GetDirectoryName(projectPath)!, ".roughcut-preview");
        Directory.CreateDirectory(cache);
        var output = Path.Combine(cache, $"source-{asset.Sha256}.webm");
        if (File.Exists(output)) return await InspectAsync(output, asset, token);

        var temporary = Path.Combine(cache, ".source-" + Guid.NewGuid().ToString("N") + ".webm");
        try
        {
            // Scaled to an even-sized preview height; realtime VP9 keeps a long source tolerable.
            var height = Math.Min(MaxHeight, asset.Height);
            await ToolProcess.RunAsync(ffmpeg,
                ["-v", "error", "-nostdin", "-xerror", "-i", source,
                 "-map", "0:v:0", "-map", "0:a:0?", "-vf", $"scale=-2:{height}:flags=bicubic",
                 "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "5", "-row-mt", "1",
                 "-b:v", "0", "-crf", "34", "-g", "120", "-pix_fmt", "yuv420p",
                 "-c:a", "libopus", "-b:a", "96k", "-f", "webm", temporary],
                timeout: TimeSpan.FromMinutes(20), cancellationToken: token);
            token.ThrowIfCancellationRequested();
            var proxy = await InspectAsync(temporary, asset, token);
            try { File.Move(temporary, output); }
            catch (IOException) when (File.Exists(output)) { }
            return File.Exists(output) ? await InspectAsync(output, asset, token) : proxy;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<SourceProxy> InspectAsync(string path, MediaAsset asset, CancellationToken token)
    {
        var length = new FileInfo(path).Length;
        if (length is <= 0 or > MaxProxyBytes)
            throw new DesktopPlaybackUnavailableException("The preview copy is empty or exceeds its 512 MiB bound.");
        var info = await new MediaReader(ffmpeg, ffprobe).ProbeAsync(path, token);
        if (info.Codec != "vp9")
            throw new DesktopPlaybackUnavailableException("The preview copy is not the VP9 the player decodes.");
        var seconds = (double)info.DurationTicks * info.TimeBase.Numerator / info.TimeBase.Denominator;
        return new(path, asset.Id, asset.Sha256, seconds, length);
    }
}
