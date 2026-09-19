using System.Security.Cryptography;
using System.Text;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Application;

public sealed class RoughCutOperations(WorkspaceBoundary workspace, string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
{
    private readonly ProjectStore _store = new();
    private readonly MediaReader _media = new(ffmpeg, ffprobe);

    public Task<EditProject> ReadProjectAsync(string projectPath, CancellationToken token = default)
        => _store.LoadAsync(workspace.Resolve(projectPath), token);

    public Task<VideoInfo> InspectAsync(string mediaPath, CancellationToken token = default)
        => _media.InspectAsync(workspace.Resolve(mediaPath), token);

    public async Task<EditProject> CreateProjectAsync(string mediaPath, string projectPath, CancellationToken token = default)
    {
        var source = workspace.Resolve(mediaPath);
        var destination = workspace.Resolve(projectPath, mustExist: false);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("Project destination already exists.");
        var relative = Path.GetRelativePath(Path.GetDirectoryName(destination)!, source).Replace('\\', '/');
        if (!ProjectValidator.IsPortablePath(relative))
            throw new ArgumentException("Media must be inside the project directory.");
        var info = await _media.InspectAsync(source, token);
        var project = new EditProject
        {
            TimeBase = info.TimeBase,
            Assets = [new("source-1", "video", relative, info.Sha256, info.DurationTicks, info.Width, info.Height, "application/octet-stream")],
            Timeline = [new("clip-1", "source-1", 0, info.DurationTicks)]
        };
        await _store.SaveAsync(destination, project, 0, token);
        return project;
    }

    public async Task<FrameImage> GetFrameAsync(string projectPath, string assetId, long timestampTicks,
        long timeBaseNumerator, long timeBaseDenominator, int maxWidth, CancellationToken token = default)
    {
        var absoluteProject = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(absoluteProject, token);
        var asset = project.Assets.SingleOrDefault(item => item.Id == assetId && item.Kind == "video")
            ?? throw new KeyNotFoundException("Video asset was not found in the project.");
        var frame = await _media.GetFrameAsync(ProjectFiles.Resolve(absoluteProject, asset.Path),
            new(assetId, new(timestampTicks, new(timeBaseNumerator, timeBaseDenominator)), maxWidth), token);
        if (!string.Equals(frame.Info.SourceSha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source media changed after the project was created.");
        return frame;
    }

    public async Task<EditProject> ApplyEditsAsync(string projectPath, long expectedRevision,
        EditOperation[] edits, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var edited = TimelineEditor.Apply(project, edits);
        await _store.SaveAsync(path, edited, expectedRevision, token);
        return edited;
    }

    public async Task<EditProject> ImportCaptionsAsync(string projectPath, string assetId, string captionPath,
        long expectedRevision, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var source = workspace.Resolve(captionPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var relative = Path.GetRelativePath(Path.GetDirectoryName(path)!, source).Replace('\\', '/');
        var bytes = await ProjectFiles.ReadBoundedAsync(ProjectFiles.Resolve(path, relative), Captions.MaxBytes, token);
        var cues = Captions.ParseSrt(new UTF8Encoding(false, true).GetString(bytes));
        var edited = project with
        {
            Revision = checked(expectedRevision + 1),
            Captions = new(assetId, relative, Convert.ToHexStringLower(SHA256.HashData(bytes)), new(1, 1000), cues)
        };
        await _store.SaveAsync(path, edited, expectedRevision, token);
        return edited;
    }

    public async Task<EditProject> ImportPngAsync(string projectPath, string assetId, string base64Data,
        long expectedRevision, string provider, string modelVersion, CancellationToken token = default)
    {
        const int maxBytes = 8 * 1024 * 1024;
        if (string.IsNullOrWhiteSpace(assetId) || assetId.Length > 128)
            throw new ArgumentException("Image asset ID must contain 1 to 128 characters.");
        if (string.IsNullOrWhiteSpace(base64Data) || base64Data.Length > ((maxBytes + 2) / 3) * 4)
            throw new InvalidDataException("Encoded image exceeds the 8 MiB PNG limit.");
        byte[] png;
        try { png = Convert.FromBase64String(base64Data); }
        catch (FormatException) { throw new InvalidDataException("Image data is not valid base64."); }
        if (png.Length > maxBytes) throw new InvalidDataException("Image exceeds the 8 MiB PNG limit.");
        var dimensions = PngImage.ReadDimensions(png);
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        if (project.Assets.Any(asset => asset.Id == assetId)) throw new ArgumentException("Image asset ID already exists.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(png));
        var relative = $"assets/images/{hash}.png";
        var destination = ProjectFiles.Resolve(path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var createdAssetFile = false;
        if (File.Exists(destination))
        {
            if (!string.Equals(await MediaReader.FingerprintAsync(destination, token), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Existing image asset does not match its content hash.");
        }
        else
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await output.WriteAsync(png, token);
                    output.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: false);
                createdAssetFile = true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var edited = project with
        {
            Revision = checked(expectedRevision + 1),
            Assets = [.. project.Assets, new(assetId, "image", relative, hash, 0, dimensions.Width, dimensions.Height, "image/png")],
            Provenance = [.. project.Provenance, new(assetId, provider, modelVersion)]
        };
        try { await _store.SaveAsync(path, edited, expectedRevision, token); }
        catch
        {
            var referenced = false;
            try { referenced = (await _store.LoadAsync(path, CancellationToken.None)).Assets.Any(asset => asset.Sha256 == hash); }
            catch (Exception) { referenced = true; }
            if (createdAssetFile && !referenced && File.Exists(destination)) File.Delete(destination);
            throw;
        }
        return edited;
    }

    public async Task<ExportPlan> PreflightAsync(string projectPath, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        return await new ExportPlanner(ffmpeg, ffprobe).PreflightAsync(await _store.LoadAsync(path, token), path, token);
    }

    public Task<TimelineFrame> GetTimelineFrameAsync(string projectPath, long expectedRevision, long timelineTicks,
        int maxWidth, CancellationToken token = default)
        => new TimelinePreviewer(ffmpeg, ffprobe).GetFrameAsync(workspace.Resolve(projectPath), expectedRevision,
            timelineTicks, maxWidth, token);
}
