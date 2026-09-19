using System.Buffers.Binary;
using RoughCut.Core;

namespace RoughCut.Media;

public sealed record TimelineFrame(TimelineFrameInfo Info, byte[] Png);

public sealed class TimelinePreviewer(string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public async Task<TimelineFrame> GetFrameAsync(string projectPath, long expectedRevision, long timelineTicks,
        int maxWidth = 1280, CancellationToken cancellationToken = default)
    {
        if (timelineTicks < 0) throw new ArgumentOutOfRangeException(nameof(timelineTicks));
        if (maxWidth is < 16 or > 1920) throw new ArgumentException("Maximum width must be between 16 and 1920.");
        projectPath = Path.GetFullPath(projectPath);
        var project = await new ProjectStore().LoadAsync(projectPath, cancellationToken);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var maps = ProjectValidator.MapTimeline(project);
        var mapIndex = Array.FindIndex(maps, map => timelineTicks >= map.OutputIn && timelineTicks < map.OutputOut);
        if (mapIndex < 0) throw new ArgumentOutOfRangeException(nameof(timelineTicks), "No timeline clip covers the requested timestamp.");
        var map = maps[mapIndex];
        var clip = project.Timeline[mapIndex];
        var assets = project.Assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        var asset = assets[clip.AssetId];
        var videoClip = project.Timeline.FirstOrDefault(item => assets[item.AssetId].Kind == "video")
            ?? throw new NotSupportedException("Timeline preview requires one active video source to define cadence and canvas.");
        var sourceVideo = assets[videoClip.AssetId];
        var sourcePath = ProjectFiles.Resolve(projectPath, sourceVideo.Path);
        var index = await new MediaReader(ffmpeg, ffprobe).IndexAsync(sourcePath, cancellationToken);
        if (!string.Equals(index.Info.Sha256, sourceVideo.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source media changed after the project was created.");
        var frameDuration = TimeMath.ExactTicks(new(index.Frames[0].Duration, index.Info.TimeBase), project.TimeBase);
        var offset = checked(timelineTicks - map.OutputIn);
        var actualTimeline = checked(map.OutputIn + offset / frameDuration * frameDuration);
        MediaTime? sourceActual = null;
        int sourceFrame = 0;
        if (asset.Kind == "video")
        {
            var requestedSource = checked(clip.In + offset);
            sourceFrame = Array.FindIndex(index.Frames, frame =>
            {
                var start = TimeMath.ExactTicks(new(frame.Pts - index.Info.StartTicks, index.Info.TimeBase), project.TimeBase);
                var duration = TimeMath.ExactTicks(new(frame.Duration, index.Info.TimeBase), project.TimeBase);
                return requestedSource >= start && requestedSource < checked(start + duration);
            });
            if (sourceFrame < 0) throw new ArgumentOutOfRangeException(nameof(timelineTicks), "No displayed source frame covers the mapped timestamp.");
            var chosen = index.Frames[sourceFrame];
            var sourceTicks = TimeMath.ExactTicks(new(chosen.Pts - index.Info.StartTicks, index.Info.TimeBase), project.TimeBase);
            sourceActual = new(sourceTicks, project.TimeBase);
            actualTimeline = checked(map.OutputIn + sourceTicks - clip.In);
        }
        var (canvasWidth, canvasHeight) = VisualFilters.Canvas(project);
        var filter = VisualFilters.ForClip(clip, asset, canvasWidth, canvasHeight) + VisualFilters.ScaleToMaxWidth(canvasWidth, maxWidth);
        List<string> arguments = ["-v", "error", "-nostdin", "-xerror", "-noautorotate"];
        if (asset.Kind == "video")
        {
            arguments.AddRange(["-protocol_whitelist", "file,pipe", "-format_whitelist", "mov,matroska,webm,avi,mpegts,ogg,flv", "-i", ProjectFiles.Resolve(projectPath, asset.Path),
                "-map", $"0:{index.Info.StreamIndex}", "-vf", $"select=eq(n\\,{sourceFrame}),{filter}"]);
        }
        else
        {
            var imagePath = ProjectFiles.Resolve(projectPath, asset.Path);
            var imageBytes = await ProjectFiles.ReadBoundedAsync(imagePath, 8 * 1024 * 1024, cancellationToken);
            if (!string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(imageBytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Image changed after it was imported.");
            var dimensions = PngImage.ReadDimensions(imageBytes);
            if (dimensions.Width != asset.Width || dimensions.Height != asset.Height)
                throw new InvalidDataException("Image dimensions differ from the project.");
            arguments.AddRange(["-protocol_whitelist", "file", "-f", "image2", "-i", imagePath, "-map", "0:v:0", "-vf", filter]);
        }
        arguments.AddRange(["-frames:v", "1", "-c:v", "png", "-f", "image2pipe", "pipe:1"]);
        var result = await ToolProcess.RunAsync(ffmpeg, arguments, cancellationToken: cancellationToken);
        if (result.Output.Length < 24 || !result.Output.AsSpan(0, 8).SequenceEqual(PngSignature))
            throw new InvalidDataException("Timeline preview did not produce a PNG frame.");
        var width = BinaryPrimitives.ReadInt32BigEndian(result.Output.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(result.Output.AsSpan(20, 4));
        return new(new(project.ProjectId, project.Revision, clip.Id, asset.Id, asset.Kind,
            new(timelineTicks, project.TimeBase), new(actualTimeline, project.TimeBase), new(frameDuration, project.TimeBase), sourceActual,
            width, height, "image/png", clip.Fit, clip.Crop), result.Output);
    }
}
