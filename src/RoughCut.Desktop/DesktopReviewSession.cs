using RoughCut.Application;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Desktop;

public sealed class DesktopReviewSession
{
    private readonly RoughCutOperations _operations;
    private readonly string _projectName;
    private readonly Stack<ReviewChange> _undo = new();
    private readonly Stack<ReviewChange> _redo = new();
    private string _selectionLabel = "Timeline start";

    private DesktopReviewSession(string projectPath, RoughCutOperations operations, EditProject project)
    {
        ProjectPath = projectPath;
        _projectName = Path.GetFileName(projectPath);
        _operations = operations;
        Project = project;
        SelectedSpeakerId = project.Speakers.FirstOrDefault()?.Id;
    }

    public string ProjectPath { get; }
    public EditProject Project { get; private set; }
    public TimelineFrame? Preview { get; private set; }
    public FrameImage? SourcePreview { get; private set; }
    public DesktopPlaybackProxy? Playback { get; private set; }
    public string? SelectedSegmentId { get; private set; }
    public string? SelectedSpeakerId { get; private set; }
    public string Selection { get; private set; } = "No frame selected";
    public string Crop { get; private set; } = "No active crop";
    public string PlaybackStatus { get; private set; } = "Playback proxy has not been prepared";
    public string? SelectedClipId => Preview?.Info.ClipId;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public static async Task<DesktopReviewSession> LoadAsync(string projectPath, CancellationToken token = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        var operations = new RoughCutOperations(new WorkspaceBoundary(Path.GetDirectoryName(projectPath)!));
        var project = await operations.ReadProjectAsync(Path.GetFileName(projectPath), token);
        return new(projectPath, operations, project);
    }

    public async Task InitializePreviewAsync(CancellationToken token = default)
    {
        var first = Project.Speech.FirstOrDefault(segment => FindMapping(segment.AssetId, segment.Start) is not null);
        if (first is not null) await SelectSegmentAsync(first.Id, token);
        else if (ProjectValidator.MapTimeline(Project).FirstOrDefault() is { } mapping)
            await SelectSourceAsync(mapping.AssetId, mapping.SourceIn, "Timeline start", token);
    }

    public async Task PreparePlaybackAsync(CancellationToken token = default)
    {
        Playback = null;
        PlaybackStatus = "Preparing synchronized playback…";
        try
        {
            Playback = await new DesktopPlaybackProxyBuilder().PrepareAsync(ProjectPath, token);
            PlaybackStatus = $"Synchronized WebM proxy · {Playback.Length / 1024d / 1024d:0.0} MiB";
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or
            InvalidOperationException or KeyNotFoundException or ProjectValidationException or RevisionConflictException or
            MediaToolException or ExportRejectedException or DesktopPlaybackUnavailableException)
        {
            PlaybackStatus = exception.Message;
            throw;
        }
    }

    public void PlaybackUnavailable(string message)
    {
        Playback = null;
        PlaybackStatus = message;
    }

    public double SelectedTimelineSeconds => Preview is null ? 0 :
        (double)Preview.Info.Actual.Ticks * Preview.Info.Actual.TimeBase.Numerator /
        Preview.Info.Actual.TimeBase.Denominator;

    public async Task SelectSegmentAsync(string segmentId, CancellationToken token = default)
    {
        var segment = Project.Speech.Single(item => item.Id == segmentId);
        SelectedSegmentId = segment.Id;
        if (segment.SpeakerIds.Length == 1) SelectedSpeakerId = segment.SpeakerIds[0];
        await SelectSourceAsync(segment.AssetId, segment.Start, segment.Text, token);
    }

    public async Task SelectEvidenceAsync(string observationId, CancellationToken token = default)
    {
        var observation = Project.Observations.Single(item => item.Id == observationId);
        await SelectSourceAsync(observation.AssetId, observation.Start, observation.Summary, token);
    }

    public async Task SelectClipAsync(string clipId, CancellationToken token = default)
    {
        var mapping = ProjectValidator.MapTimeline(Project).Single(item => item.ClipId == clipId);
        await SelectSourceAsync(mapping.AssetId, mapping.SourceIn, $"Clip {clipId}", token);
    }

    public void SelectSpeaker(string speakerId) => SelectedSpeakerId = Project.Speakers.Single(item => item.Id == speakerId).Id;

    public async Task RenameSelectedSpeakerAsync(string label, CancellationToken token = default)
    {
        if (SelectedSpeakerId is null) throw new InvalidOperationException("Select a speaker before changing its label.");
        var speaker = Project.Speakers.Single(item => item.Id == SelectedSpeakerId);
        label = label.Trim();
        if (string.Equals(label, speaker.Label, StringComparison.Ordinal)) return;
        Project = await _operations.ApplySpeakerEditsAsync(_projectName, Project.Revision,
            [new("rename", speaker.Id, Label: label, Reason: "desktop-review")], token);
        _undo.Push(new LabelChange(speaker.Id, speaker.Label, label));
        _redo.Clear();
    }

    public async Task ApplyCropAsync(Crop? crop, CancellationToken token = default)
    {
        var clipId = SelectedClipId ?? throw new InvalidOperationException("Select a video clip before changing its crop.");
        var clip = Project.Timeline.Single(item => item.Id == clipId);
        if (Project.Assets.Single(item => item.Id == clip.AssetId).Kind != "video")
            throw new InvalidOperationException("Only video clips can be cropped in desktop review.");
        if (Equals(clip.Crop, crop)) return;
        var selected = Preview?.Info;
        await ApplyCropCoreAsync(clipId, crop, token);
        _undo.Push(new CropChange(clipId, clip.Crop, crop));
        _redo.Clear();
        await RefreshAfterCropAsync(selected, token);
    }

    public async Task UndoAsync(CancellationToken token = default)
    {
        if (!_undo.TryPeek(out var change)) return;
        var selected = Preview?.Info;
        var cropChanged = await ApplyChangeAsync(change, forward: false, token);
        _undo.Pop();
        _redo.Push(change);
        if (cropChanged) await RefreshAfterCropAsync(selected, token);
    }

    public async Task RedoAsync(CancellationToken token = default)
    {
        if (!_redo.TryPeek(out var change)) return;
        var selected = Preview?.Info;
        var cropChanged = await ApplyChangeAsync(change, forward: true, token);
        _redo.Pop();
        _undo.Push(change);
        if (cropChanged) await RefreshAfterCropAsync(selected, token);
    }

    public async Task ReloadAsync(CancellationToken token = default)
    {
        var selectedSegmentId = SelectedSegmentId;
        Project = await _operations.ReadProjectAsync(_projectName, token);
        _undo.Clear();
        _redo.Clear();
        if (SelectedSpeakerId is not null && Project.Speakers.All(item => item.Id != SelectedSpeakerId))
            SelectedSpeakerId = Project.Speakers.FirstOrDefault()?.Id;
        if (selectedSegmentId is not null && Project.Speech.Any(item => item.Id == selectedSegmentId))
            await SelectSegmentAsync(selectedSegmentId, token);
        else
        {
            SelectedSegmentId = null;
            Preview = null;
            await InitializePreviewAsync(token);
        }
    }

    private async Task SelectSourceAsync(string assetId, long sourceTicks, string label, CancellationToken token)
    {
        var mapping = FindMapping(assetId, sourceTicks)
            ?? throw new InvalidOperationException("The selected source evidence is not retained on the current timeline.");
        var resolvedSource = Math.Clamp(sourceTicks, mapping.SourceIn, mapping.SourceOut - 1);
        var timelineTicks = checked(mapping.OutputIn + resolvedSource - mapping.SourceIn);
        Preview = await _operations.GetTimelineFrameAsync(_projectName, Project.Revision, timelineTicks, 960, token);
        var asset = Project.Assets.Single(item => item.Id == assetId);
        SourcePreview = asset.Kind == "video"
            ? await _operations.GetFrameAsync(_projectName, assetId, resolvedSource,
                Project.TimeBase.Numerator, Project.TimeBase.Denominator, 360, token)
            : null;
        _selectionLabel = label;
        Selection = $"{label} · timeline {timelineTicks} · source {resolvedSource}";
        var clip = Project.Timeline.Single(item => item.Id == mapping.ClipId);
        Crop = clip.Crop is null ? "Full source frame" :
            $"Crop x={clip.Crop.X}, y={clip.Crop.Y}, {clip.Crop.Width}×{clip.Crop.Height}";
    }

    private TimelineMapping? FindMapping(string assetId, long sourceTicks) =>
        ProjectValidator.MapTimeline(Project).FirstOrDefault(item => item.AssetId == assetId &&
            sourceTicks >= item.SourceIn && sourceTicks < item.SourceOut);

    private Task<EditProject> RenameAsync(string speakerId, string label, string reason, CancellationToken token) =>
        _operations.ApplySpeakerEditsAsync(_projectName, Project.Revision,
            [new("rename", speakerId, Label: label, Reason: reason)], token);

    private async Task ApplyCropCoreAsync(string clipId, Crop? crop, CancellationToken token)
    {
        Project = await _operations.ApplyEditsAsync(_projectName, Project.Revision,
            [new("crop", clipId, Crop: crop)], token);
        Playback = null;
        PlaybackStatus = "Timeline changed · rebuild playback to review the crop";
    }

    private async Task RefreshAfterCropAsync(TimelineFrameInfo? selected, CancellationToken token)
    {
        if (selected?.SourceActual is { } source)
            await SelectSourceAsync(selected.AssetId, source.Ticks, _selectionLabel, token);
        else
            await InitializePreviewAsync(token);
    }

    private async Task<bool> ApplyChangeAsync(ReviewChange change, bool forward, CancellationToken token)
    {
        switch (change)
        {
            case LabelChange label:
                Project = await RenameAsync(label.SpeakerId, forward ? label.After : label.Before,
                    forward ? "desktop-redo" : "desktop-undo", token);
                return false;
            case CropChange crop:
                await ApplyCropCoreAsync(crop.ClipId, forward ? crop.After : crop.Before, token);
                return true;
        }
        return false;
    }

    private abstract record ReviewChange;
    private sealed record LabelChange(string SpeakerId, string Before, string After) : ReviewChange;
    private sealed record CropChange(string ClipId, Crop? Before, Crop? After) : ReviewChange;
}
