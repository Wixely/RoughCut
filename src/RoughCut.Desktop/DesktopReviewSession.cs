using RoughCut.Application;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Desktop;

public sealed class DesktopReviewSession
{
    private const long MaxReviewProxyBytes = 128L * 1024 * 1024;
    private readonly RoughCutOperations _operations;
    private readonly string _projectName;
    private readonly Stack<ReviewChange> _undo = new();
    private readonly Stack<ReviewChange> _redo = new();
    private string _selectionLabel = "Timeline start";
    private DesktopPlaybackProxy? _suppliedPreview;

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

    /// Where projects are written when nobody says otherwise; the shared default, so a second interface and
    /// an agent put edits in the same place this window does.
    public static string ProjectsRoot => ProjectFolder.Root;

    public static ToolSettings Tools => ToolSettings.Default;

    private static RoughCutOperations Operations(string directory) =>
        new(new WorkspaceBoundary(directory), Tools.Ffmpeg, Tools.Ffprobe, Tools.YtDlp);

    /// What the source offers, without downloading any of it, so a person can pick a quality themselves
    /// rather than accepting whatever a policy would have chosen.
    public static Task<SourceFormatList> ListFormatsAsync(string sourceUrl, CancellationToken token = default)
    {
        var root = ProjectsRoot;
        Directory.CreateDirectory(root);
        return new YtDlpAcquirer(new WorkspaceBoundary(root), Tools.YtDlp).ListFormatsAsync(sourceUrl, Tools.Deno, token);
    }

    /// Downloads one URL with its subtitles through the bounded yt-dlp policy, then creates and opens a
    /// project for it. Everything lands in one folder: the media, the project file and its assets.
    ///
    /// `format` is a rendition identifier or a bitrate policy; `folder` is where the edit should live, and
    /// when it is absent a folder is generated from the video's own title under the projects root.
    public static async Task<DesktopReviewSession> CreateFromUrlAsync(string sourceUrl,
        CancellationToken token = default, string? format = null, string? folder = null, string? title = null,
        IProgress<double>? progress = null)
    {
        var destination = folder is { Length: > 0 }
            ? Path.GetFullPath(folder)
            : ProjectFolder.Unused(ProjectsRoot, ProjectFolder.NameFrom(title, DateTime.Now.ToString(
                "yyyy-MM-dd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)));
        var root = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("An edit folder must sit inside a containing directory.");
        Directory.CreateDirectory(root);
        var result = await Operations(root).CreateProjectFromUrlAsync(sourceUrl, Path.GetFileName(destination),
            Tools.Deno, token: token, format: format, progress: progress);
        return await LoadAsync(result.ProjectPath, token);
    }

    private static string UnusedDirectory(string root, string name)
    {
        for (var suffix = 1; suffix <= 1000; suffix++)
        {
            var candidate = Path.Combine(root, suffix == 1 ? name :
                name + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Too many acquisitions already exist for that moment.");
    }

    /// Creates an edit in a folder of its own from a local video: the video is copied in beside the project
    /// file, so one directory holds the whole edit and its stored paths stay portable. The person's own file
    /// is left exactly where it was.
    ///
    /// When `folder` is absent the folder is named after the video, under the projects root.
    public static async Task<DesktopReviewSession> CreateInFolderAsync(string mediaPath,
        CancellationToken token = default, string? folder = null, IProgress<double>? progress = null)
    {
        mediaPath = Path.GetFullPath(mediaPath);
        if (!File.Exists(mediaPath)) throw new FileNotFoundException("The chosen video does not exist.", mediaPath);
        var destination = folder is { Length: > 0 }
            ? Path.GetFullPath(folder)
            : ProjectFolder.Unused(ProjectsRoot, Path.GetFileNameWithoutExtension(mediaPath));
        var root = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("An edit folder must sit inside a containing directory.");
        Directory.CreateDirectory(root);
        // The workspace has to contain both the video and the new folder, so it is rooted at whichever
        // directory holds them both rather than at either one of them.
        var shared = CommonRoot(Path.GetDirectoryName(mediaPath)!, root);
        var operations = Operations(shared);
        var result = await operations.CreateProjectFolderAsync(
            Path.GetRelativePath(shared, mediaPath).Replace('\\', '/'),
            Path.GetRelativePath(shared, destination).Replace('\\', '/'), progress, token);
        return await LoadAsync(result.ProjectPath, token);
    }

    /// The deepest directory containing both paths, so a workspace can reach the person's video and the new
    /// edit folder without either being reachable through "..".
    private static string CommonRoot(string first, string second)
    {
        var left = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar);
        var right = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar);
        while (!right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(left);
            if (string.IsNullOrEmpty(parent))
                throw new ArgumentException("The video and the edit folder are on different drives; choose a folder on the same drive.");
            left = parent.TrimEnd(Path.DirectorySeparatorChar);
        }
        return left;
    }

    /// Creates a project beside its video, because an asset must live inside the project directory
    /// for its stored path to stay portable. Returns the session for the new project.
    public static async Task<DesktopReviewSession> CreateAsync(string mediaPath, CancellationToken token = default)
    {
        mediaPath = Path.GetFullPath(mediaPath);
        if (!File.Exists(mediaPath)) throw new FileNotFoundException("The chosen video does not exist.", mediaPath);
        var directory = Path.GetDirectoryName(mediaPath)
            ?? throw new ArgumentException("The chosen video has no containing directory.");
        var projectPath = UnusedProjectPath(directory, Path.GetFileNameWithoutExtension(mediaPath));
        await Operations(directory).CreateProjectAsync(Path.GetFileName(mediaPath), Path.GetFileName(projectPath), token);
        return await LoadAsync(projectPath, token);
    }

    private static string UnusedProjectPath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "project";
        for (var suffix = 1; suffix <= 1000; suffix++)
        {
            var candidate = Path.Combine(directory, suffix == 1 ? name + ".json" :
                name + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Too many projects already exist for that video name.");
    }

    public static async Task<DesktopReviewSession> LoadAsync(string projectPath, CancellationToken token = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        var operations = Operations(Path.GetDirectoryName(projectPath)!);
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

    /// The source copy the player shows. Editing never rebuilds it, because the source does not change.
    public SourceProxy? PlaybackCopy { get; private set; }

    private EditProject? _mappedProject;
    private TimelineMapping[] _mapping = [];

    /// Clips in output order, for approximating cuts and reordering over that one copy. Recomputed when the
    /// project instance changes, and empty rather than throwing when a project cannot be mapped at all.
    public TimelineMapping[] Mapping
    {
        get
        {
            if (ReferenceEquals(_mappedProject, Project)) return _mapping;
            _mapping = ProjectValidator.Validate(Project).Length == 0 ? ProjectValidator.MapTimeline(Project) : [];
            _mappedProject = Project;
            return _mapping;
        }
    }

    public bool CanApproximate => TimelinePlayback.CanApproximate(Project);

    /// Prepares the single preview copy of the source. No video is rendered for an edit: cuts, ordering and
    /// crop are approximated over this copy, and only export produces a validated artifact.
    public async Task PreparePlaybackAsync(CancellationToken token = default)
    {
        // An explicitly supplied render is this session's playback: never replace it.
        if (_suppliedPreview is not null)
        {
            RestoreSuppliedPreview();
            return;
        }

        if (!CanApproximate)
        {
            PlaybackCopy = null;
            PlaybackStatus = "Preview needs a timeline built from exactly one video source.";
            throw new DesktopPlaybackUnavailableException(PlaybackStatus);
        }
        var assetId = Project.Timeline[0].AssetId;
        if (PlaybackCopy?.SourceSha256 == Project.Assets.Single(item => item.Id == assetId).Sha256)
        {
            PlaybackStatus = PreviewStatus();
            return;
        }
        PlaybackStatus = "Preparing a preview copy of the source…";
        try
        {
            var preview = await new SourceProxyBuilder(Tools.Ffmpeg, Tools.Ffprobe)
                .PrepareAsync(ProjectPath, Project, assetId, token);
            token.ThrowIfCancellationRequested();
            PlaybackCopy = preview;
            PlaybackStatus = PreviewStatus();
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or
            InvalidOperationException or KeyNotFoundException or ProjectValidationException or
            MediaToolException or DesktopPlaybackUnavailableException)
        {
            if (!token.IsCancellationRequested)
            {
                PlaybackCopy = null;
                PlaybackStatus = exception.Message;
            }
            throw;
        }
    }

    /// Builds the validated export-backed proxy on request, to confirm exactly what export would produce.
    public async Task PrepareExactPlaybackAsync(CancellationToken token = default)
    {
        var revision = Project.Revision;
        Playback = null;
        PlaybackStatus = "Rendering the exact validated timeline…";
        try
        {
            var playback = await new DesktopPlaybackProxyBuilder(Tools.Ffmpeg, Tools.Ffprobe).PrepareAsync(ProjectPath, token);
            token.ThrowIfCancellationRequested();
            if (Project.Revision != revision)
                throw new OperationCanceledException("The timeline changed while playback was being prepared.", token);
            Playback = playback;
            PlaybackStatus = $"Exact validated timeline · {playback.Length / 1024d / 1024d:0.0} MiB";
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or
            InvalidOperationException or KeyNotFoundException or ProjectValidationException or RevisionConflictException or
            MediaToolException or ExportRejectedException or DesktopPlaybackUnavailableException)
        {
            if (!token.IsCancellationRequested && Project.Revision == revision) PlaybackStatus = exception.Message;
            throw;
        }
    }

    private string PreviewStatus() => PlaybackCopy is null ? "No preview copy is available" :
        $"Approximated timeline over a preview copy · {PlaybackCopy.Length / 1024d / 1024d:0.0} MiB";

    public string DeliveryStatus { get; private set; } = "Export writes a new folder beside the project";
    public string? DeliveredPath { get; private set; }

    /// What this project can be exported as. The copy options come first and are the default: keeping the
    /// source's own packets is what a cut-down usually wants, and it finishes in seconds.
    public Task<ExportFormatList> ListExportFormatsAsync(CancellationToken token = default) =>
        Operations(Path.GetDirectoryName(ProjectPath)!).ListExportFormatsAsync(Path.GetFileName(ProjectPath), token);

    /// Exports the saved timeline into a new folder beside the project, in the format the person chose. A
    /// copy keeps the source's packets and moves each cut to the nearest anchor behind it; a delivery format
    /// re-encodes and lands the cuts exactly. Either way the project itself is never changed by exporting it.
    public async Task<string> ExportAsync(string formatName, IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        var choice = ExportFormats.Resolve(formatName);
        var copy = choice.Copy;
        var container = choice.Container;
        var target = choice.Target;
        var destination = UnusedDirectory(Path.GetDirectoryName(ProjectPath)!,
            Path.GetFileNameWithoutExtension(ProjectPath) + "-export");
        DeliveryStatus = copy
            ? $"Copying the timeline into {container.ToUpperInvariant()} from revision {Project.Revision}…"
            : FormattableString.Invariant($"Exporting {target!.Label} from revision {Project.Revision}…");
        try
        {
            string published;
            double seconds;
            long bytes;
            if (copy)
            {
                var report = await new MuxExporter(Tools.Ffmpeg, Tools.Ffprobe)
                    .ExportAsync(ProjectPath, destination, container, token, progress);
                published = Path.Combine(destination, "video." + container);
                seconds = report.ActualSeconds;
                bytes = report.OutputBytes;
                // A copy cannot land every cut where it was asked for, so the window says so rather than
                // implying the boundaries are exact.
                DeliveryStatus = FormattableString.Invariant(
                        $"Copied {Path.GetFileName(destination)} · {bytes / 1024d / 1024d:0.0} MiB · {seconds:0.0}s · ") +
                    FormattableString.Invariant(
                        $"cuts moved by up to {Seconds(report.Plan.WorstOffsetTicks):0.000}s to reach a keyframe");
            }
            else
            {
                var report = await new DeliveryExporter(Tools.Ffmpeg, Tools.Ffprobe)
                    .ExportAsync(ProjectPath, destination, token, target, progress);
                published = Path.Combine(destination, target!.FileName);
                seconds = report.ActualSeconds;
                bytes = report.OutputBytes;
                DeliveryStatus = $"Exported {Path.Combine(Path.GetFileName(destination), target.FileName)} · " +
                    FormattableString.Invariant(
                        $"{bytes / 1024d / 1024d:0.0} MiB · {seconds:0.0}s from revision {report.Plan.Revision}");
            }
            DeliveredPath = published;
            return published;
        }
        catch (OperationCanceledException)
        {
            DeliveryStatus = "Export canceled; nothing was published.";
            throw;
        }
        catch (DeliveryRejectedException rejected)
        {
            DeliveryStatus = string.Join(" ", rejected.Plan.Issues.Select(issue => issue.Message));
            throw;
        }
        catch (MuxRejectedException rejected)
        {
            DeliveryStatus = string.Join(" ", rejected.Plan.Issues.Select(issue => issue.Message));
            throw;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or
            InvalidOperationException or KeyNotFoundException or ProjectValidationException or MediaToolException)
        {
            DeliveryStatus = exception.Message;
            throw;
        }
    }

    private double Seconds(long ticks) => (double)ticks * Project.TimeBase.Numerator / Project.TimeBase.Denominator;

    /// Renders the saved timeline to a deliverable H.264/AAC MP4 in a new folder beside the project. This is
    /// the only file the window produces for somebody else to watch, and it is always an explicit action.
    /// It re-encodes: it delivers the edit, not an untouched copy of the source.
    public async Task<DeliveryReport> DeliverAsync(CancellationToken token = default)
    {
        // Preflight starts no process, so an unrenderable timeline is refused with its reason immediately
        // rather than after an encode.
        var plan = DeliveryExporter.Plan(Project, ProjectPath);
        if (!plan.Supported)
        {
            DeliveryStatus = string.Join(" ", plan.Issues.Select(issue => issue.Message));
            throw new InvalidOperationException(DeliveryStatus);
        }
        var destination = UnusedDirectory(Path.GetDirectoryName(ProjectPath)!,
            Path.GetFileNameWithoutExtension(ProjectPath) + "-export");
        DeliveryStatus = FormattableString.Invariant(
            $"Exporting {plan.Width}×{plan.Height} MP4 from revision {plan.Revision}…");
        try
        {
            var report = await new DeliveryExporter(Tools.Ffmpeg, Tools.Ffprobe).ExportAsync(ProjectPath, destination, token);
            DeliveredPath = Path.Combine(destination, "video.mp4");
            var name = Path.Combine(Path.GetFileName(destination), "video.mp4");
            DeliveryStatus = FormattableString.Invariant(
                $"Exported {name} · {report.OutputBytes / 1024d / 1024d:0.0} MiB · {report.ActualSeconds:0.0}s from revision {report.Plan.Revision}");
            return report;
        }
        catch (OperationCanceledException)
        {
            DeliveryStatus = "Export canceled; nothing was published.";
            throw;
        }
        catch (DeliveryRejectedException rejected)
        {
            DeliveryStatus = string.Join(" ", rejected.Plan.Issues.Select(issue => issue.Message));
            throw;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or
            InvalidOperationException or KeyNotFoundException or ProjectValidationException or MediaToolException)
        {
            DeliveryStatus = exception.Message;
            throw;
        }
    }

    public void PlaybackUnavailable(string message) => InvalidatePlayback(message);

    public void UsePlaybackPreview(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Pre-rendered review proxy does not exist.", path);
        if (!string.Equals(Path.GetExtension(path), ".webm", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pre-rendered desktop review playback must be a WebM file.");
        var length = new FileInfo(path).Length;
        if (length is <= 0 or > MaxReviewProxyBytes)
            throw new InvalidDataException("Pre-rendered review proxy exceeds its 128 MiB bound or is empty.");
        var duration = ProjectValidator.MapTimeline(Project).LastOrDefault()?.OutputOut ?? 0;
        _suppliedPreview = new(path, "external-review-preview", Project.Revision,
            duration * Project.TimeBase.Numerator / (double)Project.TimeBase.Denominator, length);
        RestoreSuppliedPreview();
    }

    // Edits and reloads keep the supplied render rather than clearing it, and say when it has fallen behind.
    private void RestoreSuppliedPreview()
    {
        var preview = _suppliedPreview ?? throw new InvalidOperationException("No review proxy was supplied.");
        Playback = preview;
        PlaybackStatus = $"Pre-rendered review proxy (not validated export) · {preview.Length / 1024d / 1024d:0.0} MiB" +
            (Project.Revision == preview.Revision ? "" :
                $" · does not show edits since revision {preview.Revision}");
    }

    public double SelectedTimelineSeconds => Preview is null ? 0 :
        (double)Preview.Info.Actual.Ticks * Preview.Info.Actual.TimeBase.Numerator /
        Preview.Info.Actual.TimeBase.Denominator;

    /// Where the player should seek for the current selection. An exact render is in timeline time; the
    /// preview copy is in source time, so a timeline position has to be mapped back through the clips.
    public PlaybackStep SelectedPlaybackStep() => Playback is not null || Mapping.Length == 0
        ? new(0, SelectedTimelineSeconds, false)
        : TimelinePlayback.Locate(Mapping, Project.TimeBase, SelectedTimelineSeconds);

    public async Task SelectSegmentAsync(string segmentId, CancellationToken token = default)
    {
        var segment = Project.Speech.Single(item => item.Id == segmentId);
        await SelectSourceAsync(segment.AssetId, segment.Start, segment.Text, token);
        token.ThrowIfCancellationRequested();
        SelectedSegmentId = segment.Id;
        if (segment.SpeakerIds.Length == 1) SelectedSpeakerId = segment.SpeakerIds[0];
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
        await RefreshAfterTimelineAsync(selected, token);
    }

    // Uses set-range rather than trim so the reviewer can restore retained source material and undo stays exact.
    public Task TrimSelectedClipAsync(long inTicks, long outTicks, CancellationToken token = default) =>
        TrimClipAsync(RequireSelectedVideoClip("trimmed").Id, inTicks, outTicks, token);

    /// Trims a named clip, for the timeline's draggable boundaries: the drag knows which edge of which clip
    /// it moved, not which clip happens to be selected.
    public async Task TrimClipAsync(string clipId, long inTicks, long outTicks, CancellationToken token = default)
    {
        var clip = Project.Timeline.SingleOrDefault(item => item.Id == clipId)
            ?? throw new InvalidOperationException("That clip is no longer on the timeline.");
        var asset = Project.Assets.Single(item => item.Id == clip.AssetId);
        if (asset.Kind != "video") throw new InvalidOperationException("Only video clips can be trimmed in desktop review.");
        if (inTicks < 0 || inTicks >= outTicks || outTicks > asset.Duration)
            throw new InvalidOperationException("A trim must keep a nonempty interval inside the source.");
        if (clip.In == inTicks && clip.Out == outTicks) return;
        var selected = Preview?.Info;
        await ApplyTimelineAsync([new("set-range", clip.Id, In: inTicks, Out: outTicks)], token);
        _undo.Push(new RangeChange(clip.Id, clip.In, clip.Out, inTicks, outTicks));
        _redo.Clear();
        await RefreshAfterTimelineAsync(selected, token);
    }

    public async Task SplitSelectedClipAsync(CancellationToken token = default)
    {
        var clip = RequireSelectedVideoClip("split");
        if (Preview?.Info.SourceActual is not { } source)
            throw new InvalidOperationException("Select a source frame before splitting the clip.");
        if (source.Ticks <= clip.In || source.Ticks >= clip.Out)
            throw new InvalidOperationException("Splitting requires a frame strictly inside the selected clip.");
        var newClipId = NextClipId(clip.Id);
        var selected = Preview?.Info;
        await ApplyTimelineAsync([new("split", clip.Id, At: source.Ticks, NewClipId: newClipId)], token);
        _undo.Push(new SplitChange(clip.Id, clip.In, clip.Out, source.Ticks, newClipId));
        _redo.Clear();
        await RefreshAfterTimelineAsync(selected, token);
    }

    /// Removing is paired with insert-clip, which puts the exact clip back where it was, so the reviewer
    /// can cut material out and still undo it. A timeline always keeps at least one clip: an empty one has
    /// nothing to preview, and nothing in the window could add a clip back.
    public async Task RemoveSelectedClipAsync(CancellationToken token = default)
    {
        var clip = RequireSelectedVideoClip("removed");
        if (Project.Timeline.Length <= 1)
            throw new InvalidOperationException("The timeline must keep at least one clip; trim this one instead.");
        var index = Array.FindIndex(Project.Timeline, item => item.Id == clip.Id);
        var before = index + 1 < Project.Timeline.Length ? Project.Timeline[index + 1].Id : null;
        var selected = Preview?.Info;
        await ApplyTimelineAsync([new("remove", clip.Id)], token);
        _undo.Push(new RemovalChange(clip, before));
        _redo.Clear();
        await RefreshAfterTimelineAsync(selected, token);
    }

    public async Task MoveSelectedClipAsync(int offset, CancellationToken token = default)
    {
        var clipId = SelectedClipId ?? throw new InvalidOperationException("Select a clip before reordering it.");
        var before = Project.Timeline.Select(item => item.Id).ToArray();
        var index = Array.IndexOf(before, clipId);
        var target = checked(index + offset);
        if (index < 0 || target < 0 || target >= before.Length)
            throw new InvalidOperationException("The selected clip is already at that end of the timeline.");
        var after = before.ToArray();
        (after[index], after[target]) = (after[target], after[index]);
        var selected = Preview?.Info;
        await ApplyTimelineAsync([new("reorder", Order: after)], token);
        _undo.Push(new OrderChange(before, after));
        _redo.Clear();
        await RefreshAfterTimelineAsync(selected, token);
    }

    public async Task UndoAsync(CancellationToken token = default)
    {
        if (!_undo.TryPeek(out var change)) return;
        var selected = Preview?.Info;
        var timelineChanged = await ApplyChangeAsync(change, forward: false, token);
        _undo.Pop();
        _redo.Push(change);
        if (timelineChanged) await RefreshAfterTimelineAsync(selected, token);
    }

    public async Task RedoAsync(CancellationToken token = default)
    {
        if (!_redo.TryPeek(out var change)) return;
        var selected = Preview?.Info;
        var timelineChanged = await ApplyChangeAsync(change, forward: true, token);
        _redo.Pop();
        _undo.Push(change);
        if (timelineChanged) await RefreshAfterTimelineAsync(selected, token);
    }

    public async Task ReloadAsync(CancellationToken token = default)
    {
        var selectedSegmentId = SelectedSegmentId;
        Project = await _operations.ReadProjectAsync(_projectName, token);
        InvalidatePlayback("Project reloaded · preparing current timeline playback");
        _undo.Clear();
        _redo.Clear();
        if (SelectedSpeakerId is not null && Project.Speakers.All(item => item.Id != SelectedSpeakerId))
            SelectedSpeakerId = Project.Speakers.FirstOrDefault()?.Id;
        if (selectedSegmentId is not null && Project.Speech.Any(item => item.Id == selectedSegmentId))
            await SelectSegmentAsync(selectedSegmentId, token);
        else
        {
            SelectedSegmentId = null;
            PlaybackCopy = null;
            await InitializePreviewAsync(token);
        }
    }

    private async Task SelectSourceAsync(string assetId, long sourceTicks, string label, CancellationToken token)
    {
        var mapping = FindMapping(assetId, sourceTicks)
            ?? throw new InvalidOperationException("The selected source evidence is not retained on the current timeline.");
        var resolvedSource = Math.Clamp(sourceTicks, mapping.SourceIn, mapping.SourceOut - 1);
        var timelineTicks = checked(mapping.OutputIn + resolvedSource - mapping.SourceIn);
        var preview = await _operations.GetTimelineFrameAsync(_projectName, Project.Revision, timelineTicks, 960, token);
        var asset = Project.Assets.Single(item => item.Id == assetId);
        var sourcePreview = asset.Kind == "video"
            ? await _operations.GetFrameAsync(_projectName, assetId, resolvedSource,
                Project.TimeBase.Numerator, Project.TimeBase.Denominator, 360, token)
            : null;
        token.ThrowIfCancellationRequested();
        Preview = preview;
        SourcePreview = sourcePreview;
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

    private TimelineClip RequireSelectedVideoClip(string action)
    {
        var clipId = SelectedClipId ?? throw new InvalidOperationException($"Select a video clip before it can be {action}.");
        var clip = Project.Timeline.Single(item => item.Id == clipId);
        if (Project.Assets.Single(item => item.Id == clip.AssetId).Kind != "video")
            throw new InvalidOperationException($"Only video clips can be {action} in desktop review.");
        return clip;
    }

    private string NextClipId(string clipId)
    {
        for (var suffix = 2; suffix <= 1000; suffix++)
        {
            var candidate = clipId + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Project.Timeline.All(item => item.Id != candidate)) return candidate;
        }
        throw new InvalidOperationException("Too many timeline clips already derive from the selected clip ID.");
    }

    private Task ApplyCropCoreAsync(string clipId, Crop? crop, CancellationToken token) =>
        ApplyTimelineAsync([new("crop", clipId, Crop: crop)], token);

    private async Task ApplyTimelineAsync(EditOperation[] operations, CancellationToken token)
    {
        Project = await _operations.ApplyEditsAsync(_projectName, Project.Revision, operations, token);
        InvalidatePlayback("Timeline changed · rebuild playback to review the edit");
    }

    private void InvalidatePlayback(string message)
    {
        if (_suppliedPreview is not null)
        {
            RestoreSuppliedPreview();
            return;
        }
        // The preview copy is of the source, so an edit never invalidates it: only the clip mapping moves.
        // Any exact render is revision-bound and is dropped rather than left claiming to match.
        Playback = null;

        PlaybackStatus = PlaybackCopy is not null && CanApproximate ? PreviewStatus() : message;
    }

    private async Task RefreshAfterTimelineAsync(TimelineFrameInfo? selected, CancellationToken token)
    {
        if (selected?.SourceActual is { } source && FindMapping(selected.AssetId, source.Ticks) is not null)
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
            case RangeChange range:
                await ApplyTimelineAsync([new("set-range", range.ClipId,
                    In: forward ? range.AfterIn : range.BeforeIn,
                    Out: forward ? range.AfterOut : range.BeforeOut)], token);
                return true;
            case SplitChange split:
                // Undo joins the halves in one transactional batch, so the timeline never persists a partial split.
                await ApplyTimelineAsync(forward
                    ? [new("split", split.ClipId, At: split.At, NewClipId: split.NewClipId)]
                    : [new("remove", split.NewClipId), new("set-range", split.ClipId, In: split.In, Out: split.Out)],
                    token);
                return true;
            case RemovalChange removal:
                await ApplyTimelineAsync(forward
                    ? [new("remove", removal.Clip.Id)]
                    : [new("insert-clip", removal.Clip.Id, In: removal.Clip.In, Out: removal.Clip.Out,
                        Crop: removal.Clip.Crop, AssetId: removal.Clip.AssetId, BeforeClipId: removal.BeforeClipId,
                        Fit: removal.Clip.Fit, Audio: removal.Clip.Audio)], token);
                return true;
            case OrderChange order:
                await ApplyTimelineAsync([new("reorder", Order: forward ? order.After : order.Before)], token);
                return true;
        }
        return false;
    }

    private abstract record ReviewChange;
    private sealed record LabelChange(string SpeakerId, string Before, string After) : ReviewChange;
    private sealed record CropChange(string ClipId, Crop? Before, Crop? After) : ReviewChange;
    private sealed record RangeChange(string ClipId, long BeforeIn, long BeforeOut, long AfterIn, long AfterOut) : ReviewChange;
    private sealed record SplitChange(string ClipId, long In, long Out, long At, string NewClipId) : ReviewChange;
    private sealed record OrderChange(string[] Before, string[] After) : ReviewChange;
    private sealed record RemovalChange(TimelineClip Clip, string? BeforeClipId) : ReviewChange;
}
