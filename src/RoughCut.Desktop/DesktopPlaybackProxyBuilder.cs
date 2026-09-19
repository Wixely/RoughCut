using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Desktop;

public sealed record DesktopPlaybackProxy(string Path, string ProjectSha256, long Revision,
    double DurationSeconds, long Length);

public sealed class DesktopPlaybackProxyBuilder(string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
{
    private const long MaxProxyBytes = 128L * 1024 * 1024;

    public async Task<DesktopPlaybackProxy> PrepareAsync(string projectPath, CancellationToken token = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        var project = await new ProjectStore().LoadAsync(projectPath, token);
        var plan = await new ExportPlanner(ffmpeg, ffprobe).PreflightAsync(project, projectPath, token);
        if (!plan.Supported)
            throw new DesktopPlaybackUnavailableException(string.Join(" ", plan.Issues.Select(issue => issue.Message)));

        var cache = Path.Combine(Path.GetDirectoryName(projectPath)!, ".roughcut-preview");
        Directory.CreateDirectory(cache);
        var output = Path.Combine(cache, $"{plan.ProjectSha256}-{plan.Revision}.webm");
        var durationSeconds = Seconds(plan.Duration, plan.TimeBase);
        if (File.Exists(output)) return await ValidateNewAsync(output, plan, durationSeconds, token);

        var exportDirectory = Path.Combine(cache, ".export-" + Guid.NewGuid().ToString("N"));
        var temporary = Path.Combine(cache, ".proxy-" + Guid.NewGuid().ToString("N") + ".webm");
        try
        {
            await new ExportEngine(ffmpeg, ffprobe).ExportAsync(projectPath, exportDirectory,
                allowEncoding: true, cancellationToken: token);
            var source = Path.Combine(exportDirectory, "video.mkv");
            var arguments = new List<string>
            {
                "-v", "error", "-nostdin", "-xerror", "-i", source,
                "-map", "0:v:0", "-map", "0:a:0", "-c:v", "libvpx-vp9",
                "-deadline", "realtime", "-cpu-used", "4", "-row-mt", "1",
                "-g", "60", "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k",
                "-f", "webm", temporary
            };
            await ToolProcess.RunAsync(ffmpeg, arguments, timeout: TimeSpan.FromMinutes(2), cancellationToken: token);
            var validated = await ValidateNewAsync(temporary, plan, durationSeconds, token);
            try { File.Move(temporary, output); }
            catch (IOException) when (File.Exists(output)) { }
            return File.Exists(output) ? await ValidateNewAsync(output, plan, durationSeconds, token) : validated;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            DeleteExportBundle(exportDirectory);
        }
    }

    private async Task<DesktopPlaybackProxy> ValidateNewAsync(string path, ExportPlan plan,
        double durationSeconds, CancellationToken token)
    {
        var result = ValidateCached(path, plan, durationSeconds);
        var info = await new MediaReader(ffmpeg, ffprobe).InspectAsync(path, token);
        var actualDuration = Seconds(info.DurationTicks, info.TimeBase);
        if (info.Codec != "vp9" || info.Width != plan.Width || info.Height != plan.Height ||
            Math.Abs(actualDuration - durationSeconds) > 0.050)
            throw new InvalidDataException("Playback proxy codec, dimensions or duration differ from the validated timeline.");
        return result;
    }

    private static DesktopPlaybackProxy ValidateCached(string path, ExportPlan plan, double durationSeconds)
    {
        var length = new FileInfo(path).Length;
        if (length is <= 0 or > MaxProxyBytes)
            throw new InvalidDataException("Playback proxy exceeds its 128 MiB bound or is empty.");
        return new(path, plan.ProjectSha256, plan.Revision, durationSeconds, length);
    }

    private static double Seconds(long ticks, TimeBase timeBase) =>
        (double)ticks * timeBase.Numerator / timeBase.Denominator;

    private static void DeleteExportBundle(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var name in new[] { "video.mkv", "export.json", "captions.srt" })
        {
            var file = Path.Combine(path, name);
            if (File.Exists(file)) File.Delete(file);
        }
        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }
}

public sealed class DesktopPlaybackUnavailableException(string message) : Exception(message);
