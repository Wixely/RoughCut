using CupriFace;
using CupriFace.Binding;
using CupriFace.Interaction;
using RoughCut.Application;
using RoughCut.Core;
using SkiaSharp;

namespace RoughCut.Desktop;

public sealed class RoughCutReviewApp : CupriApp
{
    private readonly ReviewModel _model = new();
    private readonly DesktopWorkCoordinator _work = new();
    private readonly Lock _playbackSync = new();
    private readonly SemaphoreSlim _playbackGate = new(1, 1);
    private readonly DesktopPlaybackController? playback;
    private readonly RecentProjects _recent;
    private DesktopReviewSession? _session;
    private CupriDocument? _document;
    private CancellationTokenSource? _playbackCancellation;
    private CancellationTokenSource? _exportCancellation;
    /// What the person picked in the launcher: the source they asked about and the renditions it offers, so
    /// choosing a quality does not have to ask the source a second time.
    private SourceFormatList? _offered;
    private string _pendingUrl = "";
    /// The export format the button will use. A copy is the default: it keeps the source's own packets and
    /// finishes in seconds, which is what a cut-down usually wants.
    private string _exportFormat = "original-mkv";
    private FileSystemWatcher? _projectWatcher;
    private int _watcherPending;
    private long _playbackGeneration;
    private CropDragState? _cropDrag;
    private TransportDragState? _transportDrag;
    private ClipDragState? _clipDrag;
    private long _transportShownAt;
    private (float Width, float Height) _previewBox;
    private string _previewKey = "";
    private string _transportPosition = "";

    public RoughCutReviewApp(DesktopReviewSession? session = null,
        DesktopPlaybackController? playback = null, RecentProjects? recent = null)
    {
        _session = session;
        this.playback = playback;
        _recent = recent ?? new RecentProjects();
    }

    // Review handlers only run while a project is open; a null session here would be a wiring mistake.
    private DesktopReviewSession Session => _session ??
        throw new InvalidOperationException("Open a project before reviewing it.");

    private sealed record CropDragState(CropDragMode Mode, Crop Start, float PointerX, float PointerY,
        int SourceWidth, int SourceHeight, double Scale);

    private sealed record TransportDragState(float TrackX, float TrackWidth);

    private sealed record ClipDragState(string ClipId, ClipEdge Edge, long StartIn, long StartOut,
        long SourceDuration, long MinimumTicks, double TicksPerPixel, float PointerX, long In, long Out);

    public override string Title => "RoughCut Review";
    private static readonly byte[] ApplicationIcon = LoadIcon();
    public override byte[] Icon => ApplicationIcon;

    private static byte[] LoadIcon()
    {
        using var stream = typeof(RoughCutReviewApp).Assembly.GetManifestResourceStream("RoughCut.Desktop.Icon.png")
            ?? throw new InvalidOperationException("The application icon resource is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
    public override int Width => 1280;
    public override int Height => 800;
    public override bool DarkWindowChrome => true;
    public override SKColor Background => new(0x0b, 0x0f, 0x17);
    public override object Model => _model;
    public override string Html => Markup;
    public override string Css => Styles;

    public override void Configure(CupriDocument document)
    {
        _document = document;
        Rebuild();
        document.Refresh();
        document.OnClick(".speech-row", e => StartLatest(token =>
            Session.SelectSegmentAsync(Required(e, "data-id"), token), seekAfter: true));
        document.OnClick(".evidence-row", e => StartLatest(token =>
            Session.SelectEvidenceAsync(Required(e, "data-id"), token), seekAfter: true));
        document.OnClick(".clip", e => StartLatest(token =>
            Session.SelectClipAsync(Required(e, "data-id"), token), seekAfter: true));
        document.OnClick(".speaker-row", e => StartCommand(() =>
        {
            Session.SelectSpeaker(Required(e, "data-id"));
            return Task.CompletedTask;
        }));
        document.OnClick(".save-label", _ => StartCommand(() => Session.RenameSelectedSpeakerAsync(_model.SelectedLabel)));
        document.OnClick(".apply-crop", _ => StartCommand(ApplyCropAsync, seekAfter: true, rebuildPlayback: true));
        document.OnClick(".reset-crop", _ => StartCommand(ResetCropAsync, seekAfter: true, rebuildPlayback: true));
        document.OnClick(".apply-trim", _ => StartCommand(ApplyTrimAsync, seekAfter: true, rebuildPlayback: true));
        document.OnClick(".split-clip", _ => StartCommand(() => Session.SplitSelectedClipAsync(),
            seekAfter: true, rebuildPlayback: true));
        document.OnClick(".remove-clip", _ => StartCommand(() => Session.RemoveSelectedClipAsync(),
            seekAfter: true, rebuildPlayback: true));
        document.OnClick(".move-earlier", _ => StartCommand(() => Session.MoveSelectedClipAsync(-1),
            seekAfter: true, rebuildPlayback: true));
        document.OnClick(".move-later", _ => StartCommand(() => Session.MoveSelectedClipAsync(1),
            seekAfter: true, rebuildPlayback: true));
        document.OnClick(".undo", _ => StartCommand(() => UndoRedoAsync(redo: false), seekAfter: true, rebuildPlayback: true));
        document.OnClick(".redo", _ => StartCommand(() => UndoRedoAsync(redo: true), seekAfter: true, rebuildPlayback: true));
        document.OnClick(".reload", _ => StartCommand(ReloadAsync, seekAfter: true, rebuildPlayback: true));
        document.OnClick(".exact-preview", _ => StartExactPreview());
        document.OnClick(".export", _ => ToggleExport());
        document.OnClick(".transport-play", _ => TogglePlay());
        document.OnClick(".transport-mute", _ => ToggleMute());
        document.OnPointer("data-transport", HandleTransportPointer);
        document.OnPointer("data-crop-drag", HandleCropPointer);
        document.OnPointer("data-clip-drag", HandleClipPointer);
        document.OnClick(".recent-open", e => OpenProject(Required(e, "data-path")));
        document.OnClick(".open-path", _ => OpenOrCreate(_model.OpenPath));
        document.OnClick(".browse", _ => Browse(create: false));
        document.OnClick(".new-project", _ => Browse(create: true));
        document.OnClick(".new-url", _ => ListFormats(_model.OpenUrl));
        document.OnClick(".format-pick", e => DownloadChosen(Required(e, "data-format")));
        document.OnClick(".choose-folder", _ => ChooseFolder());
        document.OnClick(".cancel-work", _ => CancelWork());
        document.OnClick(".export-choose", _ => ToggleExportMenu());
        document.OnClick(".export-format", e => ChooseExportFormat(Required(e, "data-format")));
        document.OnClick(".close-project", _ => ShowLauncher("Choose another project to review."));
        document.OnFileDrop(drop =>
        {
            if (drop.Files.FirstOrDefault(file => !file.IsDirectory)?.Path is { } dropped) OpenOrCreate(dropped);
        });
        if (_session is null) ShowLauncher();
        else StartReview();
    }

    private int _clipIndex;
    private bool _ended;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _pending = new();

    /// Refresh rebuilds the document, so changing the model or refreshing from a work thread races the
    /// renderer and can leave it reading a half-built document ("Document has no &lt;body&gt;"). Background
    /// work queues its view changes here and Present applies them on the thread that renders.
    private void Post(Action change)
    {
        _pending.Enqueue(change);
        // Headless callers drive Present themselves; a live window pumps on its next frame.
    }

    /// Applies queued view changes. Public so headless tests and renders can pump without a window.
    public void PumpPendingChanges()
    {
        while (_pending.TryDequeue(out var change))
        {
            try { change(); }
            catch (Exception exception)
            {
                _model.Status = exception.Message;
                _document?.Refresh();
            }
        }
    }

    /// Called each presented frame. While the preview copy plays, keep it inside the current clip and jump
    /// at boundaries, which is what makes cuts and reordering visible without rendering a new video.
    public override PresentInfo Present(float width, float height)
    {
        PumpPendingChanges();
        // Read the session once: it can be cleared from another thread, and a throw here would end the
        // process on the render path rather than surface anywhere a person could read it.
        var session = _session;
        try
        {
            if (session is not null && playback is { Playing: true } && session.Playback is null &&
                session.Mapping is { Length: > 0 } mapping)
            {
                var timeBase = session.Project.TimeBase;
                if (_ended)
                {
                    // Playing again after the timeline finished starts it over. Without this the position
                    // is still past the last clip, so play would stop again immediately.
                    _ended = false;
                    _clipIndex = 0;
                    playback.Seek(TimelinePlayback.Seconds(mapping[0].SourceIn, timeBase));
                }
                else
                {
                    var step = TimelinePlayback.Advance(mapping, timeBase, _clipIndex, playback.PositionSeconds);
                    _clipIndex = step.ClipIndex;
                    if (step.Ended)
                    {
                        playback.Pause();
                        _ended = true;
                    }
                    else if (step.SeekSeconds is { } seek) playback.Seek(seek);
                }
            }
        }
        catch (Exception exception)
        {
            _model.Status = "Playback stopped: " + exception.Message;
            playback?.Pause();
        }
        UpdateTransport();
        UpdatePreviewCrop();
        return base.Present(width, height);
    }

    /// A cropped clip has to play cropped, and that needs the preview's real size: the crop is applied by
    /// enlarging and offsetting the picture inside a box shaped like the crop. The box is measured from the
    /// laid-out document, and only a change in that size, the crop or the playing state costs a refresh.
    private void UpdatePreviewCrop()
    {
        if (_session is null || _document?.Root is not { } root) return;
        if (FindByClass(root, "preview") is not { Width: > 0, Height: > 0 } preview) return;
        var playing = playback?.Playing == true;
        var key = FormattableString.Invariant(
            $"{preview.Width:0.#}x{preview.Height:0.#}|{CropKey()}|{playing}|{Session.Playback is not null}");
        if (key == _previewKey) return;
        _previewKey = key;
        _previewBox = (preview.Width, preview.Height);
        ApplyPreviewCrop();
        _document.Refresh();
    }

    private string CropKey()
    {
        var clip = Session.Project.Timeline.FirstOrDefault(item => item.Id == Session.SelectedClipId);
        return clip?.Crop is { } crop
            ? FormattableString.Invariant($"{crop.X},{crop.Y},{crop.Width},{crop.Height}") : "none";
    }

    private static CupriFace.Dom.RenderNode? FindByClass(CupriFace.Dom.RenderNode node, string cssClass)
    {
        if (node.Element?.ClassList.Contains(cssClass) == true) return node;
        foreach (var child in node.Children)
            if (FindByClass(child, cssClass) is { } found) return found;
        return null;
    }

    /// The transport is redrawn while the picture moves, which needs a document refresh. It is throttled to
    /// ten a second and skipped when the reading has not changed, so a still picture costs nothing.
    private void UpdateTransport()
    {
        if (_session is null || playback is not { IsOpen: true } || _transportDrag is not null) return;
        var now = Environment.TickCount64;
        if (now - _transportShownAt < 100) return;
        _transportShownAt = now;
        var position = TimelineTransport.Format(TimelinePositionSeconds());
        if (position == _transportPosition) return;
        _transportPosition = position;
        ApplyTransport();
        _document?.Refresh();
    }

    private void StartReview()
    {
        WatchProject(Session.ProjectPath);
        if (Session.Preview is null)
            StartLatest(Session.InitializePreviewAsync, seekAfter: true,
                startPlaybackAfter: playback is not null && Session.Playback is null);
        else if (playback is not null && Session.Playback is null)
            StartPlaybackPreparation();
    }

    private void ShowLauncher(string? message = null)
    {
        CancelPlaybackPreparation();
        StopWatchingProject();
        _session = null;
        _model.Recent = ToRecentRows(_recent.Load());
        _model.RecentEmptyClass = _model.Recent.Length == 0 ? "" : "hidden";
        _model.BrowseClass = NativeFileDialog.Available ? "" : "hidden";
        _model.OpenMessage = message ?? (_model.Recent.Length == 0
            ? "Paste a video URL, start from a local video, or drop a project or video file here."
            : "Choose a recent project, paste a video URL, or drop a project or video file here.");
        Rebuild(preserveStatus: true);
        _document?.Refresh();
    }

    private static readonly FileFilter ProjectFilter = new("RoughCut projects", "json");
    private static readonly FileFilter VideoFilter =
        new("Video files", "mp4", "m4v", "mov", "mkv", "webm", "avi", "ts", "m2ts", "ogv", "flv");

    private void Browse(bool create)
    {
        // Read the owner window on the UI thread, then block only the dialog's own thread.
        var owner = NativeFileDialog.ActiveWindow();
        var start = _model.Recent.FirstOrDefault()?.Folder;
        _model.OpenMessage = create ? "Choosing a video…" : "Choosing a project…";
        _document?.Refresh();
        _ = Task.Run(() =>
        {
            try
            {
                var chosen = create
                    ? NativeFileDialog.OpenFile("Choose a video for a new RoughCut project", VideoFilter, start, owner)
                    : NativeFileDialog.OpenFile("Open a RoughCut project", ProjectFilter, start, owner);
                Post(() =>
                {
                    if (chosen is null) ShowLauncher();
                    else if (create) CreateProject(chosen);
                    else OpenProject(chosen);
                });
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                Post(() => ShowLauncher(exception.Message));
            }
        });
    }

    // A project file is opened; anything else is treated as the video for a new project, so dropping
    // or typing either kind does the obvious thing even where no native picker exists.
    private void OpenOrCreate(string path)
    {
        path = Clean(path);
        if (path.Length == 0)
        {
            _model.OpenMessage = "Enter a project or video path.";
            _document?.Refresh();
            return;
        }
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) OpenProject(path);
        else CreateProject(path);
    }

    /// An agent editing the same project over MCP saves a new revision to disk. The window follows it, so a
    /// person watching sees the edit appear instead of holding a stale timeline until they press Reload.
    ///
    /// Only a revision that differs from the open one is adopted: the window's own saves land on disk too,
    /// and reloading those would undo the selection for no reason. The watcher is deliberately a hint that
    /// something changed rather than a description of what changed.
    private void WatchProject(string projectPath)
    {
        StopWatchingProject();
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        if (directory is null || !Directory.Exists(directory)) return;
        var watcher = new FileSystemWatcher(directory, Path.GetFileName(projectPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        void Changed(object sender, FileSystemEventArgs e) => ProjectFileChanged();
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Renamed += (_, _) => ProjectFileChanged();
        watcher.EnableRaisingEvents = true;
        _projectWatcher = watcher;
    }

    private void StopWatchingProject()
    {
        var watcher = _projectWatcher;
        _projectWatcher = null;
        watcher?.Dispose();
    }

    /// A save arrives as several filesystem events, and the file is briefly a temporary being renamed into
    /// place, so this coalesces them and gives the writer a moment to finish before reading.
    private void ProjectFileChanged()
    {
        if (Interlocked.Exchange(ref _watcherPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            Interlocked.Exchange(ref _watcherPending, 0);
            var session = _session;
            if (session is null) return;
            long revision;
            try { revision = (await new ProjectStore().LoadAsync(session.ProjectPath)).Revision; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                ProjectValidationException or UnauthorizedAccessException)
            {
                // A half-written or briefly invalid file is not news; the next event will bring the truth.
                return;
            }
            if (revision == session.Project.Revision) return;
            Post(() =>
            {
                if (_session is null) return;
                if (_work.IsBusy)
                {
                    // Something of the person's own is mid-flight. Ask again shortly rather than abandoning
                    // the change; the debounce makes this a poll of a few hundred milliseconds, not a spin.
                    ProjectFileChanged();
                    return;
                }
                _model.Status = FormattableString.Invariant($"Following an outside change to revision {revision}…");
                _document?.Refresh();
                StartCommand(ReloadAsync, seekAfter: true, rebuildPlayback: true);
            });
        });
    }

    private void OpenProject(string projectPath) => Start("Opening project…",
        "Finish the current operation before opening another project.", async token =>
        {
            var opened = await DesktopReviewSession.LoadAsync(Clean(projectPath), token);
            _session = opened;
            _recent.Record(opened.ProjectPath, opened.Project.ProjectId);
        }, Clean(projectPath));

    /// Asks the source what it offers and shows it, so a person picks the quality themselves. Nothing is
    /// downloaded here: this is the step that used to be an agent's job.
    private void ListFormats(string sourceUrl)
    {
        sourceUrl = Clean(sourceUrl);
        if (sourceUrl.Length == 0)
        {
            _model.OpenMessage = "Enter a video URL to download.";
            _document?.Refresh();
            return;
        }
        HideFormats();
        ShowProgress("Reading the source…", indeterminate: true);
        Start("Asking the source what it offers…", "Finish the current operation before fetching a video.",
            async token =>
            {
                var offered = await DesktopReviewSession.ListFormatsAsync(sourceUrl, token);
                Post(() =>
                {
                    _offered = offered;
                    _pendingUrl = sourceUrl;
                    ShowFormats(offered);
                });
            });
    }

    /// Shows a rendition list as though **Fetch** had just returned it. Used by the headless snapshot, so
    /// the picker's layout can be rendered and looked at without a window or a pointer.
    public void OfferFormats(string sourceUrl, SourceFormatList offered)
    {
        _pendingUrl = sourceUrl;
        _offered = offered;
        _model.LauncherClass = "";
        _model.WorkspaceClass = "hidden";
        ShowFormats(offered);
    }

    private void ShowFormats(SourceFormatList offered)
    {
        _model.FormatsClass = "";
        _model.PickingHiddenClass = "hidden";
        _model.FormatsListClass = "";
        _model.FormatsTitle = offered.Title;
        var sizes = SourceFormatPresentation.SizeCount(offered);
        _model.FormatsHint = FormattableString.Invariant(
            $"{offered.DurationSeconds / 60:0.0} min · best of {offered.Formats.Length}") +
            (sizes > VisibleFormats ? FormattableString.Invariant($", largest {VisibleFormats} sizes") : "") +
            FormattableString.Invariant($" · max {offered.MaxBytes / 1024d / 1024d:0} MiB");
        _model.Formats = SourceFormatPresentation.BestOfEachSize(offered, VisibleFormats)
            .Select(format => new FormatRow
            {
                Id = format.Id,
                Label = format.Kind switch
                {
                    "muxed" => FormattableString.Invariant($"{format.Height}p · {SourceFormatPresentation.CodecName(format.VideoCodec)} + {SourceFormatPresentation.CodecName(format.AudioCodec)}"),
                    "audio" => FormattableString.Invariant($"Audio only · {SourceFormatPresentation.CodecName(format.AudioCodec)}"),
                    _ => FormattableString.Invariant($"{format.Height}p · {SourceFormatPresentation.CodecName(format.VideoCodec)}")
                },
                Detail = FormattableString.Invariant(
                        $"{format.Id} · {format.Extension}{(format.Fps > 0 ? FormattableString.Invariant($" · {format.Fps:0.#} fps") : "")}") +
                    FormattableString.Invariant(
                        $" · {format.BitrateKbps:0} kbps{(format.Kind == "video" ? " · sound added from the best audio" : "")}"),
                Size = format.Bytes > 0
                    ? FormattableString.Invariant($"{format.Bytes / 1024d / 1024d:0.0} MiB")
                    : "size not stated"
            }).ToArray();
        // Where the edit will live, generated from the video's own title so a person need not invent a name.
        _model.EditFolder = ProjectFolder.Unused(DesktopReviewSession.ProjectsRoot, offered.Title);
        _model.OpenMessage = "Choose a quality, or edit the folder first.";
        HideProgress();
        _document?.Refresh();
    }

    /// How many renditions the card can show without laying one over another. The list has room for this
    /// many rows and no more; the rest are counted in the hint rather than drawn where they cannot be seen.
    private const int VisibleFormats = 8;

    private void HideFormats()
    {
        _model.FormatsClass = "hidden";
        _model.PickingHiddenClass = "";
        _model.FormatsListClass = "";
        _model.Formats = [];
        _offered = null;
    }

    /// The folder the edit will live in. A person can type it, and this picks it with the platform's own
    /// browser; either way it is one folder holding the media, the project and its assets.
    private void ChooseFolder()
    {
        var owner = NativeFileDialog.ActiveWindow();
        var current = Clean(_model.EditFolder);
        var start = current.Length > 0 ? Path.GetDirectoryName(current) : DesktopReviewSession.ProjectsRoot;
        _ = Task.Run(() =>
        {
            string? chosen = null;
            try { chosen = NativeFileDialog.OpenFolder("Choose where this edit should live", start, owner); }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or
                UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) { }
            if (chosen is null) return;
            Post(() =>
            {
                // The browser returns a containing folder, so the edit still gets a folder of its own inside
                // it rather than scattering its files among whatever is already there.
                var name = _offered?.Title ?? Path.GetFileName(Clean(_model.EditFolder));
                _model.EditFolder = ProjectFolder.Unused(chosen, name);
                _document?.Refresh();
            });
        });
    }

    private void DownloadChosen(string formatId)
    {
        if (_offered is null || _pendingUrl.Length == 0) return;
        var folder = Clean(_model.EditFolder);
        var title = _offered.Title;
        // A picture-only rendition is listed as "sound added from the best audio", so the request carries
        // that audio rather than failing on a rendition the window itself offered.
        formatId = SourceFormatPolicy.WithAudio(_offered, formatId);
        // The choice has been made, so the list steps aside for the download it started.
        _model.FormatsListClass = "hidden";
        ShowProgress("Starting the download…", cancellable: true);
        Start($"Downloading {formatId} into {Path.GetFileName(folder)}…",
            "Finish the current operation before downloading a video.", async token =>
            {
                var created = await DesktopReviewSession.CreateFromUrlAsync(_pendingUrl, token, formatId, folder,
                    title, ProgressReporter("Downloading"));
                _session = created;
                _recent.Record(created.ProjectPath, created.Project.ProjectId);
                Post(HideFormats);
            });
    }

    /// A local video becomes an edit in its own folder, with the video copied in beside the project, so
    /// everything belonging to the edit stays together. The person's own file is left where it is.
    private void CreateProject(string mediaPath)
    {
        mediaPath = Clean(mediaPath);
        var folder = ProjectFolder.Unused(DesktopReviewSession.ProjectsRoot,
            Path.GetFileNameWithoutExtension(mediaPath));
        _model.EditFolder = folder;
        ShowProgress("Copying the video…", cancellable: true);
        Start($"Copying the video into {Path.GetFileName(folder)}…",
            "Finish the current operation before creating a project.", async token =>
            {
                var created = await DesktopReviewSession.CreateInFolderAsync(mediaPath, token, folder,
                    ProgressReporter("Copying"));
                _session = created;
                _recent.Record(created.ProjectPath, created.Project.ProjectId);
            }, mediaPath);
    }

    private void Start(string progress, string busy, Func<CancellationToken, Task> action, string? path = null)
    {
        _model.OpenMessage = progress;
        _model.Status = progress;
        _document?.Refresh();
        if (_work.StartCancellableCommand(action, exception => Post(() =>
        {
            if (exception is OperationCanceledException)
            {
                // A cancelled fetch publishes nothing, so the launcher is exactly as it was.
                _session = null;
                HideProgress();
                ShowLauncher("Cancelled; nothing was downloaded.");
                _model.Status = "Cancelled.";
                _document?.Refresh();
                return;
            }
            if (exception is null)
            {
                _model.LauncherClass = "hidden";
                Complete(null, seekAfter: false);
                // Opening succeeded but the review view may still fail to build; stay on a readable screen.
                if (_session is not null) StartReview();
            }
            else
            {
                // A project that will not open leaves the launcher visible with the reason, rather than
                // dropping into an empty workspace or ending the process.
                _session = null;
                HideProgress();
                var described = FailureText.Describe(exception, path);
                ShowLauncher(described);
                _model.Status = described;
                _document?.Refresh();
            }
        }))) return;
        _model.OpenMessage = busy;
        _document?.Refresh();
    }

    private static string Clean(string? path) => path?.Trim().Trim('"') ?? "";

    private static RecentRow[] ToRecentRows(IReadOnlyList<RecentProject> projects) =>
        projects.Select(item => new RecentRow
        {
            Path = item.Path,
            Name = Path.GetFileName(item.Path),
            Folder = Path.GetDirectoryName(item.Path) ?? "",
            ProjectId = item.ProjectId
        }).ToArray();

    private async Task ReloadAsync()
    {
        await Session.ReloadAsync();
    }

    private async Task ApplyCropAsync()
    {
        await Session.ApplyCropAsync(new(ParseCrop(_model.CropX, "X"), ParseCrop(_model.CropY, "Y"),
            ParseCrop(_model.CropWidth, "width"), ParseCrop(_model.CropHeight, "height")));
    }

    private async Task ResetCropAsync()
    {
        await Session.ApplyCropAsync(null);
    }

    private async Task ApplyTrimAsync()
    {
        await Session.TrimSelectedClipAsync(ParseTicks(_model.TrimIn, "in"), ParseTicks(_model.TrimOut, "out"));
    }

    private async Task UndoRedoAsync(bool redo)
    {
        if (redo) await Session.RedoAsync();
        else await Session.UndoAsync();
    }

    private void StartLatest(Func<CancellationToken, Task> action, bool seekAfter = false,
        bool startPlaybackAfter = false)
    {
        _model.Status = "Loading frame…";
        _document?.Refresh();
        if (!_work.StartLatest(action, exception => Post(() =>
        {
            Complete(exception, seekAfter);
            if (exception is null && startPlaybackAfter) StartPlaybackPreparation();
        })))
        {
            _model.Status = "Finish the current edit before selecting another frame.";
            _document?.Refresh();
        }
    }

    private void StartCommand(Func<Task> action, bool seekAfter = false, bool rebuildPlayback = false)
    {
        if (rebuildPlayback) CancelPlaybackPreparation();
        _model.Status = "Saving revision…";
        _document?.Refresh();
        if (!_work.StartCommand(action, exception => Post(() =>
        {
            Complete(exception, seekAfter);
            if (exception is null && rebuildPlayback && playback is not null) StartPlaybackPreparation();
        })))
        {
            _model.Status = "An edit is already being saved.";
            _document?.Refresh();
        }
    }

    /// Timeline seconds the player is showing. An exact render is already the timeline; the source copy is
    /// not, so its position is mapped back through the clip that is playing.
    private double TimelinePositionSeconds()
    {
        if (_session is null || playback is null) return 0;
        if (Session.Playback is not null) return playback.PositionSeconds;
        var mapping = Session.Mapping;
        return mapping.Length == 0 ? 0
            : TimelinePlayback.OutputSeconds(mapping, Session.Project.TimeBase, _clipIndex, playback.PositionSeconds);
    }

    private double TimelineDurationSeconds()
    {
        if (_session is null) return 0;
        if (Session.Playback is not null) return playback?.DurationSeconds ?? 0;
        return TimelineTransport.Duration(Session.Mapping, Session.Project.TimeBase);
    }

    /// Seeks to a timeline second, which for the source copy means finding the clip that covers it.
    private void SeekTimeline(double seconds)
    {
        if (_session is null || playback is null) return;
        _ended = false;
        if (Session.Playback is not null)
        {
            playback.Seek(seconds);
            return;
        }
        var mapping = Session.Mapping;
        if (mapping.Length == 0) return;
        var step = TimelinePlayback.Locate(mapping, Session.Project.TimeBase, seconds);
        _clipIndex = step.ClipIndex;
        if (step.SeekSeconds is { } source) playback.Seek(source);
    }

    private void TogglePlay()
    {
        if (playback is not { IsOpen: true }) return;
        if (playback.Playing) playback.Pause();
        else
        {
            // Pressing play on a finished edit starts it over, rather than stopping again on the next frame.
            if (_ended) SeekTimeline(0);
            playback.Play();
        }
        ApplyTransport();
        _document?.Refresh();
    }

    private void ToggleMute()
    {
        if (playback is not { IsOpen: true }) return;
        playback.Muted = !playback.Muted;
        ApplyTransport();
        _document?.Refresh();
    }

    private bool HandleTransportPointer(MultiPointerEvent pointer)
    {
        if (_session is null || playback is not { IsOpen: true }) return false;
        if (pointer.Phase == PointerPhase.Down)
        {
            // The event carries no layout, so the track is found by hit test and its box kept for the drag.
            if (pointer.Pointers.Count != 1 || _document?.HitTest(pointer.X, pointer.Y) is not { } node) return false;
            var track = Ancestor(node, "data-transport");
            if (track is null || track.Width <= 0) return false;
            _transportDrag = new(track.X, track.Width);
        }
        if (_transportDrag is not { } drag) return true;
        if (pointer.Phase == PointerPhase.Cancel)
        {
            _transportDrag = null;
            return true;
        }
        var seconds = TimelineTransport.Seek(pointer.X, drag.TrackX, drag.TrackWidth, TimelineDurationSeconds());
        SeekTimeline(seconds);
        if (pointer.Phase == PointerPhase.Up) _transportDrag = null;
        _transportPosition = TimelineTransport.Format(seconds);
        ApplyTransport();
        _document?.Refresh();
        return true;
    }

    private static CupriFace.Dom.RenderNode? Ancestor(CupriFace.Dom.RenderNode node, string attribute)
    {
        for (var current = node; current is not null; current = current.Parent)
            if (current.Element?.HasAttribute(attribute) == true) return current;
        return null;
    }

    /// Builds the validated export-backed render on request. This is the only place the desktop renders
    /// video, and it is explicit rather than a side effect of editing.
    private void StartExactPreview() => StartPlaybackPreparation(exact: true);

    private void StartPlaybackPreparation() => StartPlaybackPreparation(exact: false);

    /// Work a person waits on shows how far it has got. The bar is driven by the tool's own reported
    /// position — yt-dlp's percentage, FFmpeg's output time — never by a timer pretending to be progress.
    private void ShowProgress(string label, bool indeterminate = false, bool cancellable = false)
    {
        _model.ProgressClass = "";
        _model.ProgressLabel = label;
        _model.ProgressFillStyle = indeterminate ? "width:100%;opacity:0.35" : "width:0%";
        _model.CancelClass = cancellable ? "" : "hidden";
        _document?.Refresh();
    }

    private void HideProgress()
    {
        _model.ProgressClass = "hidden";
        _model.ProgressLabel = "";
        _model.ProgressFillStyle = "width:0%";
        _model.CancelClass = "hidden";
    }

    /// Stops the download or copy in flight. Nothing is published by a cancelled fetch: the acquisition
    /// stages into its own directory and a copy is written under a temporary name, so both disappear.
    private void CancelWork()
    {
        _model.ProgressLabel = "Stopping…";
        _model.CancelClass = "hidden";
        _document?.Refresh();
        if (_work.CancelCommand()) return;
        // Nothing was running, so say so rather than leaving "Stopping…" on screen forever.
        HideProgress();
        _model.Status = "There was nothing to cancel.";
        _document?.Refresh();
    }

    /// Refreshes at most once per whole percent: the tools report far more often than a person can read,
    /// and every refresh re-renders the document.
    private IProgress<double> ProgressReporter(string what)
    {
        var lastPercent = -1;
        return new Progress<double>(fraction =>
        {
            var percent = (int)Math.Clamp(fraction * 100, 0, 100);
            if (percent == lastPercent) return;
            lastPercent = percent;
            Post(() =>
            {
                _model.ProgressClass = "";
                _model.ProgressLabel = FormattableString.Invariant($"{what} · {percent}%");
                _model.ProgressFillStyle = FormattableString.Invariant($"width:{percent}%");
                _document?.Refresh();
            });
        });
    }

    /// The formats this project can be exported as, asked of RoughCut rather than listed here, so the menu
    /// cannot offer something the exporters cannot produce.
    private void ToggleExportMenu()
    {
        if (_model.ExportMenuClass.Length == 0)
        {
            _model.ExportMenuClass = "hidden";
            _document?.Refresh();
            return;
        }
        if (_session is null) return;
        StartLatest(async token =>
        {
            var formats = await Session.ListExportFormatsAsync(token);
            Post(() =>
            {
                _model.ExportFormats = formats.Options.Select(option => new ExportFormatRow
                {
                    Name = option.Name,
                    Label = option.Label,
                    Detail = option.Copy
                        ? "Keeps the source's own packets · fast · cuts move to the nearest keyframe"
                        : FormattableString.Invariant($"Re-encodes every frame · {option.VideoCodec} + {option.AudioCodec} · cuts land exactly"),
                    CssClass = option.Name == _exportFormat ? "chosen" : ""
                }).ToArray();
                // A project whose copy options changed underneath the selection falls back to the first.
                if (_model.ExportFormats.All(row => row.Name != _exportFormat) && formats.Options.Length > 0)
                    ChooseExportFormat(formats.Options[0].Name);
                _model.ExportMenuClass = "";
                _document?.Refresh();
            });
        });
    }

    private void ChooseExportFormat(string formatName)
    {
        _exportFormat = formatName;
        var chosen = _model.ExportFormats.FirstOrDefault(row => row.Name == formatName);
        foreach (var row in _model.ExportFormats) row.CssClass = row.Name == formatName ? "chosen" : "";
        _model.ExportLabel = chosen is null ? ExportAction : "Export " + ShortLabel(chosen.Label);
        _model.ExportMenuClass = "hidden";
        _document?.Refresh();
    }

    /// The part of a format's label that fits on a button: "Original: av1 (.mkv)" becomes "Original .mkv".
    private static string ShortLabel(string label)
    {
        var extension = label.LastIndexOf('.');
        var suffix = extension < 0 ? "" : label[extension..].TrimEnd(')');
        return label.StartsWith("Original", StringComparison.Ordinal) ? "Original " + suffix : label.Split(' ')[0];
    }

    /// Exporting runs for as long as the encode takes, so the same button cancels it. A cancelled export
    /// publishes nothing, and the project is never changed by exporting it.
    private void ToggleExport()
    {
        CancellationTokenSource? running;
        lock (_playbackSync) running = _exportCancellation;
        if (running is not null)
        {
            _model.Status = "Canceling the export…";
            _document?.Refresh();
            running.Cancel();
            return;
        }
        if (_session is null) return;
        var cancellation = new CancellationTokenSource();
        lock (_playbackSync) _exportCancellation = cancellation;
        var chosen = _exportFormat;
        _model.ExportLabel = "Cancel export";
        _model.Status = "Exporting the timeline…";
        _model.ExportStatus = "Exporting…";
        _model.ExportMenuClass = "hidden";
        ShowProgress("Exporting · 0%");
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try { await Session.ExportAsync(chosen, ProgressReporter("Exporting"), cancellation.Token); }
            catch (Exception exception) { failure = exception; }
            lock (_playbackSync)
            {
                if (ReferenceEquals(_exportCancellation, cancellation)) _exportCancellation = null;
            }
            cancellation.Dispose();
            Post(() =>
            {
                HideProgress();
                ChooseExportFormat(chosen);
                // The export's own outcome is shown beside the preview; the status line carries any failure.
                Complete(failure is OperationCanceledException ? null : failure, seekAfter: false);
            });
        });
    }

    private const string ExportAction = "Export MP4";

    private void StartPlaybackPreparation(bool exact)
    {
        CancellationTokenSource cancellation;
        long generation;
        lock (_playbackSync)
        {
            _playbackCancellation?.Cancel();
            cancellation = _playbackCancellation = new();
            generation = ++_playbackGeneration;
        }
        _model.Status = exact ? "Rendering the exact validated timeline…" : "Preparing playback in the background…";
        _document?.Refresh();
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            var canceled = false;
            var entered = false;
            try
            {
                await _playbackGate.WaitAsync(cancellation.Token);
                entered = true;
                if (exact) await Session.PrepareExactPlaybackAsync(cancellation.Token);
                else await Session.PreparePlaybackAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { canceled = true; }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (entered) _playbackGate.Release();
            }
            var current = false;
            lock (_playbackSync)
            {
                current = generation == _playbackGeneration && ReferenceEquals(_playbackCancellation, cancellation);
                if (current) _playbackCancellation = null;
            }
            cancellation.Dispose();
            if (canceled || !current) return;
            Post(() =>
            {
                if (failure is not null) _session?.PlaybackUnavailable(failure.Message);
                Complete(failure, seekAfter: false);
            });
        });
    }

    private void CancelPlaybackPreparation()
    {
        lock (_playbackSync)
        {
            _playbackCancellation?.Cancel();
            _playbackCancellation = null;
            ++_playbackGeneration;
        }
    }

    private void Complete(Exception? exception, bool seekAfter)
    {
        _model.Status = exception is null ? "Ready" : exception.Message;
        // Complete runs on background completion callbacks, where an escaping exception would end the
        // process with no message. A view that cannot be rebuilt is reported, never fatal.
        try { Rebuild(preserveStatus: true); }
        catch (Exception failure)
        {
            _model.Status = "This project cannot be shown: " + failure.Message;
            _model.WorkspaceClass = "hidden";
        }
        _document?.Refresh();
        if (exception is null && seekAfter && _session is not null && playback is not null)
        {
            // A new selection is a deliberate move, so the timeline is no longer finished.
            var step = Session.SelectedPlaybackStep();
            _clipIndex = step.ClipIndex;
            _ended = false;
            if (step.SeekSeconds is { } seconds) playback.Seek(seconds);
        }
    }

    private bool HandleCropPointer(MultiPointerEvent pointer)
    {
        if (pointer.Phase == PointerPhase.Down)
        {
            if (_work.IsBusy || _cropDrag is not null || pointer.Pointers.Count != 1 ||
                !TryCropContext(out var asset, out var initialCrop) || !TryDragMode(pointer.Value, out var mode))
                return false;
            var scale = Math.Min(320d / asset.Width, 150d / asset.Height);
            _cropDrag = new(mode, initialCrop, pointer.X, pointer.Y, asset.Width, asset.Height, scale);
            _model.Status = "Drag the crop and release to save.";
            return true;
        }

        if (_cropDrag is not { } drag) return true;
        if (pointer.Phase == PointerPhase.Cancel)
        {
            SetPendingCrop(drag.Start);
            _cropDrag = null;
            _model.Status = "Crop drag canceled.";
            return true;
        }

        var deltaX = (int)Math.Round((pointer.X - drag.PointerX) / drag.Scale,
            MidpointRounding.AwayFromZero);
        var deltaY = (int)Math.Round((pointer.Y - drag.PointerY) / drag.Scale,
            MidpointRounding.AwayFromZero);
        var crop = CropDragGeometry.Update(drag.Start, drag.Mode, deltaX, deltaY,
            drag.SourceWidth, drag.SourceHeight);
        SetPendingCrop(crop);
        if (pointer.Phase == PointerPhase.Up)
        {
            _cropDrag = null;
            if (crop == drag.Start)
                _model.Status = "Ready";
            else
                StartCommand(() => Session.ApplyCropAsync(crop), seekAfter: true, rebuildPlayback: true);
        }
        return true;
    }

    /// A boundary drag moves one edge of one clip. The clip's own rendered width gives the scale, so the
    /// mapping stays right whatever the layout does to a very short clip.
    private bool HandleClipPointer(MultiPointerEvent pointer)
    {
        if (_session is null) return false;
        if (pointer.Phase == PointerPhase.Down)
        {
            if (_work.IsBusy || _clipDrag is not null || pointer.Pointers.Count != 1 ||
                pointer.Element?.GetAttribute("data-id") is not { Length: > 0 } clipId ||
                pointer.Value is not ("start" or "end") ||
                Session.Project.Timeline.FirstOrDefault(item => item.Id == clipId) is not { } clip ||
                Session.Project.Assets.FirstOrDefault(item => item.Id == clip.AssetId) is not { Kind: "video" } asset ||
                _document?.HitTest(pointer.X, pointer.Y) is not { } node ||
                Ancestor(node, "data-clip-box") is not { Width: > 0 } body)
                return false;
            var timeBase = Session.Project.TimeBase;
            _clipDrag = new(clipId, pointer.Value == "start" ? ClipEdge.Start : ClipEdge.End,
                clip.In, clip.Out, asset.Duration, ClipDragGeometry.MinimumTicks(timeBase),
                (clip.Out - clip.In) / (double)body.Width, pointer.X, clip.In, clip.Out);
            _model.Status = "Drag the clip boundary and release to save.";
            _document?.Refresh();
            return true;
        }

        if (_clipDrag is not { } drag) return false;
        if (pointer.Phase == PointerPhase.Cancel)
        {
            _clipDrag = null;
            _model.Status = "Boundary drag canceled.";
            _document?.Refresh();
            return true;
        }
        var (start, end) = ClipDragGeometry.Update(drag.StartIn, drag.StartOut, drag.Edge,
            (pointer.X - drag.PointerX) * drag.TicksPerPixel, drag.SourceDuration, drag.MinimumTicks);
        _clipDrag = drag with { In = start, Out = end };
        // The pending interval is shown while dragging; only release spends a revision on it.
        _model.ClipSummary = FormattableString.Invariant(
            $"{drag.ClipId} · pending source {start}–{end}");
        _model.TrimIn = start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model.TrimOut = end.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (pointer.Phase == PointerPhase.Up)
        {
            _clipDrag = null;
            if (start == drag.StartIn && end == drag.StartOut)
            {
                _model.Status = "Ready";
                Rebuild(preserveStatus: true);
            }
            else StartCommand(() => Session.TrimClipAsync(drag.ClipId, start, end),
                seekAfter: true, rebuildPlayback: true);
        }
        _document?.Refresh();
        return true;
    }

    private bool TryCropContext(out MediaAsset asset, out Crop crop)
    {
        if (_session is null)
        {
            asset = null!;
            crop = null!;
            return false;
        }
        var project = Session.Project;
        var clip = Session.SelectedClipId is null ? null :
            project.Timeline.FirstOrDefault(item => item.Id == Session.SelectedClipId);
        asset = clip is null ? null! : project.Assets.FirstOrDefault(item => item.Id == clip.AssetId)!;
        if (clip is null || asset is null || asset.Kind != "video")
        {
            crop = null!;
            return false;
        }
        crop = clip.Crop ?? new(0, 0, asset.Width, asset.Height);
        return true;
    }

    private void SetPendingCrop(Crop crop)
    {
        if (!TryCropContext(out var asset, out _)) return;
        _model.CropX = crop.X.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model.CropY = crop.Y.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model.CropWidth = crop.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model.CropHeight = crop.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model.CropBoxStyle = CropBoxStyle(crop, asset);
        _model.Crop = $"Pending crop x={crop.X}, y={crop.Y}, {crop.Width}×{crop.Height}";
    }

    private static bool TryDragMode(string value, out CropDragMode mode)
    {
        mode = value switch
        {
            "move" => CropDragMode.Move,
            "nw" => CropDragMode.NorthWest,
            "ne" => CropDragMode.NorthEast,
            "sw" => CropDragMode.SouthWest,
            "se" => CropDragMode.SouthEast,
            _ => (CropDragMode)(-1)
        };
        return (int)mode >= 0;
    }

    private void Rebuild(bool preserveStatus = false)
    {
        _model.LauncherClass = _session is null ? "" : "hidden";
        _model.WorkspaceClass = _session is null ? "hidden" : "";
        if (_session is null)
        {
            _model.ProjectTitle = "No project open";
            _model.Revision = "—";
            if (!preserveStatus) _model.Status = "Ready";
            return;
        }
        var project = Session.Project;
        _model.ProjectTitle = Path.GetFileName(Session.ProjectPath);
        _model.Revision = project.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model.PreviewDataUri = Session.Preview is null ? "" :
            "data:image/png;base64," + Convert.ToBase64String(Session.Preview.Png);
        // The exact render wins when one has been asked for; otherwise the player shows the source copy.
        var playbackPath = Session.Playback?.Path ?? Session.PlaybackCopy?.Path;
        _model.PlaybackUri = playbackPath is null ? "" : new Uri(playbackPath).AbsoluteUri;
        _model.ExactClass = Session.Playback is null ? "" : "hidden";
        ApplyPreviewCrop();
        _model.PlaybackStatus = Session.PlaybackStatus;
        _model.ExportStatus = Session.DeliveryStatus;
        ApplyTransport();
        _model.SourcePreviewDataUri = Session.SourcePreview is null ? "" :
            "data:image/png;base64," + Convert.ToBase64String(Session.SourcePreview.Png);
        _model.Selection = Session.Selection;
        _model.Crop = Session.Crop;
        var selectedClip = Session.SelectedClipId is null ? null :
            project.Timeline.FirstOrDefault(item => item.Id == Session.SelectedClipId);
        var selectedAsset = selectedClip is null ? null : project.Assets.FirstOrDefault(item => item.Id == selectedClip.AssetId);
        var editable = selectedClip is not null && selectedAsset?.Kind == "video";
        var crop = editable ? selectedClip!.Crop ?? new Crop(0, 0, selectedAsset!.Width, selectedAsset.Height) : new(0, 0, 1, 1);
        _model.CropEditorClass = editable ? "" : "hidden";
        _model.CropX = crop.X.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model.CropY = crop.Y.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model.CropWidth = crop.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model.CropHeight = crop.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model.CropCanvasStyle = editable ? CanvasStyle(selectedAsset!) : "";
        _model.CropBoxStyle = editable ? CropBoxStyle(crop, selectedAsset!) : "";
        var clipIndex = selectedClip is null ? -1 : Array.FindIndex(project.Timeline, item => item.Id == selectedClip.Id);
        _model.ClipEditorClass = selectedClip is null ? "hidden" : "";
        _model.ClipSummary = selectedClip is null ? "" : FormattableString.Invariant(
            $"{selectedClip.Id} · clip {clipIndex + 1} of {project.Timeline.Length} · source {selectedClip.In}–{selectedClip.Out}");
        _model.TrimIn = (selectedClip?.In ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model.TrimOut = (selectedClip?.Out ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Only offer edits the current selection can actually apply, so no click spends a revision on a rejection.
        var splitAt = selectedClip is not null && selectedAsset?.Kind == "video" &&
            Session.Preview?.Info.SourceActual is { } source && source.Ticks > selectedClip.In &&
            source.Ticks < selectedClip.Out ? source.Ticks : (long?)null;
        _model.SplitClass = splitAt is null ? "hidden" : "";
        _model.SplitSummary = splitAt is { } tick
            ? FormattableString.Invariant($"Split at source tick {tick}")
            : "Select an interior source frame to split this clip";
        // Removing the only clip would leave nothing to preview and nothing the window could put back.
        _model.RemoveClass = selectedClip is not null && selectedAsset?.Kind == "video" && project.Timeline.Length > 1 ? "" : "hidden";
        _model.MoveEarlierClass = clipIndex > 0 ? "" : "hidden";
        _model.MoveLaterClass = clipIndex >= 0 && clipIndex < project.Timeline.Length - 1 ? "" : "hidden";
        _model.Speakers = project.Speakers.Select(item => new SpeakerRow
        {
            Id = item.Id,
            Label = item.Label,
            CssClass = item.Id == Session.SelectedSpeakerId ? "selected" : ""
        }).ToArray();
        _model.SelectedLabel = project.Speakers.FirstOrDefault(item => item.Id == Session.SelectedSpeakerId)?.Label ?? "";
        _model.Segments = project.Speech.Take(500).Select(item => new SpeechRow
        {
            Id = item.Id,
            Time = FormatTime(item.Start, project.TimeBase),
            Speaker = item.SpeakerIds.Length == 0 ? "Unknown" : string.Join(" + ", item.SpeakerIds.Select(id =>
                project.Speakers.FirstOrDefault(speaker => speaker.Id == id)?.Label ?? id)),
            Text = item.Text,
            Badge = item.Overlap ? "overlap" : item.Assignment,
            CssClass = item.Id == Session.SelectedSegmentId ? "selected" : ""
        }).ToArray();
        // Mapping is prepared by the session, so an unopenable project cannot throw from the view.
        // Clips take their share of the row, so the timeline reads as the edit's shape and a boundary
        // drag moves a distance that matches what it changes.
        var mapped = Session.Mapping.Take(200).ToArray();
        var total = mapped.Sum(item => (double)(item.OutputOut - item.OutputIn));
        _model.Clips = mapped.Select(item => new ClipRow
        {
            Id = item.ClipId,
            Range = $"{FormatTime(item.OutputIn, project.TimeBase)}–{FormatTime(item.OutputOut, project.TimeBase)}",
            Source = item.AssetId,
            CssClass = item.ClipId == Session.SelectedClipId ? "selected" : "",
            Style = FormattableString.Invariant(
                $"flex-grow:{(total > 0 ? (item.OutputOut - item.OutputIn) / total * mapped.Length : 1):0.###}")
        }).ToArray();
        // A proposal whose observation is missing is skipped rather than allowed to break the whole view.
        _model.Evidence = project.Proposals.Take(100)
            .Select(proposal => (proposal, observation: project.Observations.FirstOrDefault(item => item.Id == proposal.ObservationId)))
            .Where(pair => pair.observation is not null)
            .Select(pair => new EvidenceRow
            {
                Id = pair.observation!.Id,
                Decision = pair.proposal.Decision,
                Summary = pair.observation.Summary
            }).ToArray();
        _model.Truncation = project.Speech.Length > 500 ? $"Showing 500 of {project.Speech.Length} transcript rows" : "";
        if (!preserveStatus) _model.Status = "Ready";
    }

    /// An exact render already contains the cropped pixels, so only the source copy is cropped by the view.
    private void ApplyPreviewCrop()
    {
        var clip = _session is null ? null :
            Session.Project.Timeline.FirstOrDefault(item => item.Id == Session.SelectedClipId);
        var asset = clip is null || _session is null ? null :
            Session.Project.Assets.FirstOrDefault(item => item.Id == clip.AssetId);
        var crop = _session is not null && Session.Playback is null ? clip?.Crop : null;
        var layout = PreviewCropGeometry.ForCrop(_previewBox.Width, _previewBox.Height,
            asset?.Width ?? 0, asset?.Height ?? 0, crop, playback?.Playing == true);
        _model.PlaybackCropStyle = layout.ContainerStyle;
        _model.PlaybackVideoStyle = layout.VideoStyle;
        _model.PlaybackFit = layout.Fit;
    }

    /// Writes the transport in timeline time, so the numbers under the picture describe the edit rather
    /// than the source copy the player is actually decoding.
    private void ApplyTransport()
    {
        var duration = TimelineDurationSeconds();
        // Shown whenever a timeline can be located, so a headless render shows the window a person sees.
        // Its controls are inert when no decoder opened a player, which the playback status already reports.
        _model.TransportClass = _session is not null && duration > 0 ? "" : "hidden";
        var position = _transportDrag is null && playback is { IsOpen: true } ? TimelinePositionSeconds() : ParsedPosition();
        _model.TransportPosition = TimelineTransport.Format(position);
        _model.TransportDuration = TimelineTransport.Format(duration);
        _model.TransportLabel = playback?.Playing == true ? "Pause" : "Play";
        _model.TransportMuteLabel = playback?.Muted == true ? "Unmute" : "Mute";
        var fraction = TimelineTransport.Fraction(position, duration);
        _model.TransportFillStyle = FormattableString.Invariant($"width:{fraction * 100:0.###}%");
        _model.TransportThumbStyle = FormattableString.Invariant($"left:{fraction * 100:0.###}%");
    }

    private double ParsedPosition() =>
        TimeSpan.TryParse(_transportPosition, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed.TotalSeconds : 0;

    private static string Required(CupriPointerEvent e, string attribute) =>
        e.Element.GetAttribute(attribute) ?? throw new InvalidDataException($"UI element is missing {attribute}.");

    private static int ParseCrop(string value, string field) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) ? parsed : throw new ArgumentException($"Crop {field} must be a whole number.");

    private static long ParseTicks(string value, string field) =>
        long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) ? parsed : throw new ArgumentException($"Trim {field} must be a whole number of project ticks.");

    private static string CanvasStyle(MediaAsset asset)
    {
        var scale = Math.Min(320d / asset.Width, 150d / asset.Height);
        return FormattableString.Invariant($"width:{asset.Width * scale:0.###}px;height:{asset.Height * scale:0.###}px");
    }

    private static string CropBoxStyle(Crop crop, MediaAsset asset) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        "left:{0:0.###}%;top:{1:0.###}%;width:{2:0.###}%;height:{3:0.###}%",
        crop.X * 100d / asset.Width, crop.Y * 100d / asset.Height,
        crop.Width * 100d / asset.Width, crop.Height * 100d / asset.Height);

    private static string FormatTime(long ticks, TimeBase timeBase)
    {
        var seconds = (decimal)ticks * timeBase.Numerator / timeBase.Denominator;
        var span = TimeSpan.FromSeconds((double)seconds);
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss\.fff") : span.ToString(@"m\:ss\.fff");
    }

    private const string Markup = """
        <body>
          <main class="shell">
            <header class="topbar">
              <div><div class="brand">ROUGH<span>CUT</span></div><div class="project">{{ProjectTitle}} · revision {{Revision}}</div></div>
              <div class="actions"><span class="status"><span class="status-dot"></span>{{Status}}</span><cupri-button class="close-project {{WorkspaceClass}}" variant="ghost">Open another</cupri-button><cupri-button class="reload {{WorkspaceClass}}" variant="ghost">Reload</cupri-button><cupri-button class="undo {{WorkspaceClass}}">Undo</cupri-button><cupri-button class="redo {{WorkspaceClass}}">Redo</cupri-button></div>
            </header>
            <section class="launcher {{LauncherClass}}">
              <div class="launcher-card">
                <div class="launcher-title">Open or create a project</div>
                <div class="launcher-hint">{{OpenMessage}}</div>
                <div class="recent-empty {{RecentEmptyClass}} {{PickingHiddenClass}}">No projects opened yet on this computer.</div>
                <div class="recent-list {{PickingHiddenClass}}"><button class="recent-open" data-repeat="Recent" data-path="{{Path}}"><strong>{{Name}}</strong><span>{{Folder}}</span><small>{{ProjectId}}</small></button></div>
                <div class="launcher-url"><cupri-textfield value="{{OpenUrl}}" placeholder="https://… video URL, downloaded with its subtitles"></cupri-textfield><cupri-button class="new-url">Fetch</cupri-button></div>
                <div class="formats {{FormatsClass}}">
                  <div class="formats-head"><strong>{{FormatsTitle}}</strong><span>{{FormatsHint}}</span></div>
                  <div class="formats-folder"><cupri-textfield value="{{EditFolder}}" placeholder="Folder to keep this edit in"></cupri-textfield><cupri-button class="choose-folder" variant="ghost">Choose…</cupri-button></div>
                  <div class="formats-list {{FormatsListClass}}"><button class="format-pick" data-repeat="Formats" data-format="{{Id}}"><strong>{{Label}}</strong><span>{{Detail}}</span><small>{{Size}}</small></button></div>
                </div>
                <div class="progress {{ProgressClass}}"><div class="progress-track"><div class="progress-fill" style="{{ProgressFillStyle}}"></div></div><span class="progress-label">{{ProgressLabel}}</span><cupri-button class="cancel-work {{CancelClass}}" variant="ghost">Cancel</cupri-button></div>
                <div class="launcher-actions {{BrowseClass}} {{PickingHiddenClass}}"><cupri-button class="new-project" variant="ghost">New from a local video…</cupri-button><cupri-button class="browse" variant="ghost">Open a project…</cupri-button></div>
                <div class="launcher-open {{PickingHiddenClass}}"><cupri-textfield value="{{OpenPath}}" placeholder="Path to a project.json or a video"></cupri-textfield><cupri-button class="open-path" variant="ghost">Go</cupri-button></div>
              </div>
            </section>
            <section class="workspace {{WorkspaceClass}}">
              <div class="stage-column">
                <div class="preview-card">
                  <div class="preview"><div class="preview-crop" style="{{PlaybackCropStyle}}"><cupri-video src="{{PlaybackUri}}" poster="{{PreviewDataUri}}" fit="{{PlaybackFit}}" style="{{PlaybackVideoStyle}}" label="Project timeline playback"></cupri-video></div></div>
                  <div class="transport {{TransportClass}}"><cupri-button class="transport-play" variant="ghost">{{TransportLabel}}</cupri-button><div class="transport-track" data-transport="seek"><div class="transport-fill" style="{{TransportFillStyle}}"></div><span class="transport-thumb" style="{{TransportThumbStyle}}"></span></div><span class="transport-time">{{TransportPosition}} / {{TransportDuration}}</span><cupri-button class="transport-mute" variant="ghost">{{TransportMuteLabel}}</cupri-button></div>
                  <div class="preview-actions"><cupri-button class="exact-preview {{ExactClass}}" variant="ghost">Render exact preview</cupri-button><cupri-button class="export-choose" variant="ghost">Format…</cupri-button><cupri-button class="export">{{ExportLabel}}</cupri-button></div>
                  <div class="export-menu {{ExportMenuClass}}"><button class="export-format {{CssClass}}" data-repeat="ExportFormats" data-format="{{Name}}"><strong>{{Label}}</strong><span>{{Detail}}</span></button></div>
                  <div class="export-progress {{ProgressClass}}"><div class="progress-track"><div class="progress-fill" style="{{ProgressFillStyle}}"></div></div><span class="progress-label">{{ProgressLabel}}</span></div>
                  <div class="preview-meta"><strong>{{Selection}}</strong><span>{{Crop}}</span><span>{{PlaybackStatus}}</span></div>
                  <div class="export-meta">{{ExportStatus}}</div>
                </div>
                <div class="timeline-card">
                  <div class="section-title">Timeline</div>
                  <div class="timeline"><button class="clip {{CssClass}}" data-repeat="Clips" data-id="{{Id}}" data-clip-box="{{Id}}" style="{{Style}}"><span class="clip-edge start" data-clip-drag="start" data-id="{{Id}}"></span><span class="clip-body"><strong>{{Id}}</strong><span>{{Range}}</span><small>{{Source}}</small></span><span class="clip-edge end" data-clip-drag="end" data-id="{{Id}}"></span></button></div>
                  <div class="clip-editor {{ClipEditorClass}}">
                    <div class="clip-summary">{{ClipSummary}} · {{SplitSummary}}</div>
                    <div class="clip-controls"><label><span>IN</span><cupri-textfield value="{{TrimIn}}"></cupri-textfield></label><label><span>OUT</span><cupri-textfield value="{{TrimOut}}"></cupri-textfield></label><div class="clip-actions"><cupri-button class="apply-trim">Apply trim</cupri-button><cupri-button class="split-clip {{SplitClass}}" variant="ghost">Split</cupri-button><cupri-button class="remove-clip {{RemoveClass}}" variant="ghost">Remove</cupri-button><cupri-button class="move-earlier {{MoveEarlierClass}}" variant="ghost">Earlier</cupri-button><cupri-button class="move-later {{MoveLaterClass}}" variant="ghost">Later</cupri-button></div></div>
                  </div>
                </div>
                <div class="evidence-card">
                  <div class="section-title">Editorial evidence</div>
                  <div class="empty">Select an item to seek to its retained source frame.</div>
                  <button class="evidence-row" data-repeat="Evidence" data-id="{{Id}}"><span class="decision">{{Decision}}</span><span>{{Summary}}</span></button>
                </div>
              </div>
              <aside class="review-panel">
                <div class="crop-editor {{CropEditorClass}}">
                  <div class="section-title">Crop selected clip</div>
                  <div class="crop-canvas" style="{{CropCanvasStyle}}"><cupri-image src="{{SourcePreviewDataUri}}" fit="fill" alt="Uncropped source frame"></cupri-image><div class="crop-box" data-crop-drag="move" style="{{CropBoxStyle}}"><span class="crop-handle nw" data-crop-drag="nw"></span><span class="crop-handle ne" data-crop-drag="ne"></span><span class="crop-handle sw" data-crop-drag="sw"></span><span class="crop-handle se" data-crop-drag="se"></span></div></div>
                  <div class="crop-fields"><label><span>X</span><cupri-textfield value="{{CropX}}"></cupri-textfield></label><label><span>Y</span><cupri-textfield value="{{CropY}}"></cupri-textfield></label><label><span>W</span><cupri-textfield value="{{CropWidth}}"></cupri-textfield></label><label><span>H</span><cupri-textfield value="{{CropHeight}}"></cupri-textfield></label></div>
                  <div class="crop-actions"><cupri-button class="reset-crop" variant="ghost">Full frame</cupri-button><cupri-button class="apply-crop">Apply crop</cupri-button></div>
                </div>
                <div class="section-title">Speakers</div>
                <button class="speaker-row {{CssClass}}" data-repeat="Speakers" data-id="{{Id}}"><span class="avatar">●</span><span>{{Label}}</span><small>{{Id}}</small></button>
                <div class="rename"><cupri-textfield value="{{SelectedLabel}}" placeholder="Speaker label"></cupri-textfield><cupri-button class="save-label">Save label</cupri-button></div>
                <div class="section-title transcript-title">Transcript</div>
                <div class="truncation">{{Truncation}}</div>
                <div class="transcript"><button class="speech-row {{CssClass}}" data-repeat="Segments" data-id="{{Id}}"><div class="speech-head"><span>{{Time}}</span><strong>{{Speaker}}</strong><small>{{Badge}}</small></div><p>{{Text}}</p></button></div>
              </aside>
            </section>
          </main>
        </body>
        """;

    private const string Styles = """
        :root { --bg:#0b0f17; --panel:#121824; --panel2:#171f2e; --line:#283348; --text:#eef3fb; --muted:#91a0b7; --accent:#ff9f43; }
        body { margin:0; background:var(--bg); color:var(--text); font-family:Arial,sans-serif; }
        .shell { height:100vh; display:flex; flex-direction:column; }
        .topbar { height:74px; padding:0 24px; display:flex; align-items:center; justify-content:space-between; border-bottom:1px solid var(--line); background:#0e141f; }
        .brand { font-size:22px; font-weight:bold; letter-spacing:2px; } .brand span { color:var(--accent); }
        .project { color:var(--muted); font-size:12px; margin-top:5px; } .actions { display:flex; gap:8px; align-items:center; }
        .status { display:flex; align-items:center; gap:7px; color:var(--muted); font-size:11px; margin-right:8px; }
        .workspace { flex:1; min-height:0; display:grid; grid-template-columns:minmax(0,1fr) 380px; gap:14px; padding:14px; }
        .workspace.hidden,.launcher.hidden,.actions .hidden,.recent-empty.hidden,.recent-list.hidden,.launcher-open.hidden { display:none; }
        .launcher { flex:1; min-height:0; display:flex; align-items:center; justify-content:center; padding:24px; }
        .launcher-card { width:660px; height:560px; overflow:hidden; display:flex; flex-direction:column; background:var(--panel); border:1px solid var(--line); border-radius:12px; padding:18px 18px 16px; }
        .launcher-title { font-size:17px; font-weight:bold; }
        .launcher-hint { color:var(--muted); font-size:12px; margin:6px 0 10px; }
        .recent-empty { color:var(--muted); font-size:12px; padding:8px 0; }
        .recent-list { flex:1; min-height:0; overflow-y:auto; }
        .recent-open { display:flex; flex-direction:column; gap:2px; width:100%; box-sizing:border-box; margin-bottom:6px; padding:9px 11px; border:0; border-left:3px solid var(--accent); border-radius:7px; background:var(--panel2); text-align:left; cursor:pointer; }
        .recent-open:hover { background:#263249; }
        .recent-open span,.recent-open small { color:var(--muted); font-size:10px; }
        .launcher-url { display:grid; grid-template-columns:minmax(0,1fr) 96px; gap:8px; align-items:center; padding-top:12px; margin-top:8px; border-top:1px solid var(--line); }
        .launcher-actions { display:grid; grid-template-columns:1fr 1fr; gap:8px; padding-top:8px; } .launcher-actions.hidden { display:none; }
        .launcher-open { display:grid; grid-template-columns:minmax(0,1fr) 72px; gap:8px; align-items:center; padding-top:8px; }
        .stage-column { min-width:0; display:grid; grid-template-rows:minmax(300px,1fr) 190px 130px; gap:14px; min-height:0; }
        .preview-card,.timeline-card,.evidence-card,.review-panel { background:var(--panel); border:1px solid var(--line); border-radius:12px; }
        .preview-card { min-height:300px; padding:12px; display:flex; flex-direction:column; }
        .preview { flex:1; min-height:180px; display:flex; align-items:center; justify-content:center; background:#05070b; border-radius:8px; overflow:hidden; }
        .preview { position:relative; }
        /* The crop box clips the enlarged picture inside it while a cropped clip plays. */
        .preview-crop { position:absolute; overflow:hidden; }
        .preview cupri-video { position:absolute; }
        .transport { display:grid; grid-template-columns:92px minmax(0,1fr) auto 92px; gap:10px; align-items:center; padding-top:10px; } .transport.hidden { display:none; }
        /* Buttons size to their cell, which would otherwise leave a squeezed track between two wide ones. */
        .transport cupri-button { box-sizing:border-box; min-width:0; width:92px; }
        .formats { display:flex; flex-direction:column; gap:6px; padding-top:8px; } .formats.hidden { display:none; }
        .formats-head { display:flex; justify-content:space-between; align-items:baseline; gap:8px; }
        .formats-head span { color:var(--muted); font-size:10px; }
        .formats-folder { display:grid; grid-template-columns:minmax(0,1fr) 92px; gap:8px; align-items:center; }
        .formats-list { height:272px; overflow:hidden; } .formats-list.hidden { display:none; }
        .format-pick { display:grid; grid-template-columns:110px minmax(0,1fr) 92px; gap:8px; align-items:center; width:100%; height:26px; box-sizing:border-box; margin-bottom:8px; padding:0 10px; border:0; border-left:3px solid var(--accent); border-radius:6px; background:var(--panel2); text-align:left; cursor:pointer; color:inherit; }
        .format-pick:hover { background:#263249; }
        .format-pick strong { font-size:12px; }
        .format-pick span,.format-pick small { color:var(--muted); font-size:10px; }
        .format-pick small { text-align:right; }
        .progress,.export-progress { display:flex; align-items:center; gap:8px; padding-top:8px; }
        .progress.hidden,.export-progress.hidden { display:none; }
        .progress .progress-track,.export-progress .progress-track { flex:1; }
        .progress-label { color:var(--muted); font-size:10px; min-width:112px; text-align:right; }
        .export-menu { display:flex; flex-direction:column; gap:4px; margin-top:6px; } .export-menu.hidden { display:none; }
        .export-format { display:flex; flex-direction:column; gap:1px; width:100%; box-sizing:border-box; padding:6px 10px; border:0; border-left:3px solid var(--line); border-radius:6px; background:var(--panel2); text-align:left; cursor:pointer; color:inherit; }
        .export-format:hover { background:#263249; }
        .export-format.chosen { border-left-color:var(--accent); }
        .export-format span { color:var(--muted); font-size:10px; }
        .progress-track { position:relative; height:10px; border-radius:5px; background:#05070b; border:1px solid var(--line); overflow:hidden; }
        .progress-fill { position:absolute; left:0; top:0; height:100%; border-radius:5px; background:var(--accent); }
        .transport-track { position:relative; height:10px; border-radius:5px; background:#05070b; border:1px solid var(--line); cursor:pointer; }
        .transport-fill { position:absolute; left:0; top:0; height:100%; border-radius:5px; background:var(--accent); }
        .transport-thumb { position:absolute; top:-3px; width:14px; height:14px; margin-left:-7px; border-radius:50%; border:2px solid #fff; box-sizing:border-box; background:var(--accent); }
        .transport-time { color:var(--muted); font-size:11px; }
        .preview-actions { display:flex; gap:8px; justify-content:flex-end; padding-top:8px; } .preview-actions .hidden { display:none; }
        .export-meta { padding:6px 4px 0; color:var(--muted); font-size:11px; }
        .preview-meta { padding:10px 4px 0; display:flex; justify-content:space-between; gap:12px; color:var(--muted); font-size:12px; } .preview-meta strong { color:var(--text); }
        .section-title { padding:12px 14px 8px; color:var(--muted); text-transform:uppercase; letter-spacing:1.2px; font-size:11px; font-weight:bold; }
        .timeline { display:flex; gap:6px; padding:0 12px 12px; overflow:hidden; }
        /* The boundary handles are laid out beside the labels rather than over them: an absolutely
           positioned child is offset from the content box here, so it would sit on the clip's own text. */
        .clip { min-width:112px; flex:1 1 0; padding:0; border:0; border-radius:7px; background:var(--panel2); border-top:3px solid var(--accent); display:flex; align-items:stretch; text-align:left; cursor:pointer; overflow:hidden; }
        .clip-body { flex:1 1 0; min-width:0; padding:9px 6px; display:flex; flex-direction:column; gap:3px; }
        .clip-edge { flex:0 0 10px; width:10px; cursor:ew-resize; background:#ff9f4355; }
        .clip:hover .clip-edge,.clip.selected .clip-edge { background:var(--accent); }
        .clip:hover,.clip.selected { background:#263249; }
        .clip span,.clip small { color:var(--muted); font-size:10px; }
        .evidence-card { max-height:150px; overflow:hidden; padding-bottom:8px; } .empty { color:var(--muted); font-size:11px; padding:0 14px 8px; }
        /* Rows are full width inside padded scroll areas, so they must include their own padding. */
        button { color:inherit; font:inherit; } .evidence-row,.speaker-row,.speech-row { width:100%; box-sizing:border-box; border:0; text-align:left; cursor:pointer; }
        .evidence-row { display:flex; gap:8px; padding:7px 14px; background:transparent; } .decision { color:var(--accent); font-weight:bold; text-transform:uppercase; font-size:10px; }
        .review-panel { min-height:0; display:flex; flex-direction:column; overflow:hidden; }
        .clip-editor.hidden { display:none; }
        .clip-summary { color:var(--muted); font-size:10px; padding:2px 12px 5px; }
        .clip-controls { display:grid; grid-template-columns:150px 150px minmax(0,1fr); gap:10px; align-items:end; padding:0 14px 12px; }
        .clip-controls cupri-textfield { box-sizing:border-box; min-width:0; width:150px; }
        .clip-controls label span { display:block; color:var(--muted); font-size:9px; margin-bottom:2px; }
        .clip-actions { display:flex; gap:6px; justify-content:flex-end; } .clip-actions .hidden { display:none; }
        .crop-editor { padding-bottom:10px; border-bottom:1px solid var(--line); } .crop-editor.hidden { display:none; }
        .crop-canvas { position:relative; margin:0 auto 8px; background:#05070b; overflow:hidden; }
        .crop-canvas cupri-image { width:100%; height:100%; } .crop-box { position:absolute; box-sizing:border-box; border:2px solid var(--accent); background:#ff9f4322; cursor:move; }
        .crop-handle { position:absolute; width:12px; height:12px; border:2px solid #fff; border-radius:50%; background:var(--accent); box-sizing:border-box; }
        .crop-handle.nw { left:-7px; top:-7px; cursor:nwse-resize; } .crop-handle.ne { right:-7px; top:-7px; cursor:nesw-resize; }
        .crop-handle.sw { left:-7px; bottom:-7px; cursor:nesw-resize; } .crop-handle.se { right:-7px; bottom:-7px; cursor:nwse-resize; }
        /* Text fields never render narrower than about 90px, so two per row is the most this panel fits. */
        .crop-fields { display:grid; grid-template-columns:minmax(0,1fr) minmax(0,1fr); gap:6px 10px; padding:0 14px; }
        /* The component sizes content-box and will not shrink below its intrinsic width on its own. */
        .crop-fields cupri-textfield { box-sizing:border-box; min-width:0; width:165px; } .crop-fields label span { display:block; color:var(--muted); font-size:9px; margin-bottom:2px; }
        .crop-actions { display:flex; justify-content:flex-end; gap:6px; padding:10px 14px 0; }
        .speaker-row { display:grid; grid-template-columns:18px 1fr auto; gap:8px; align-items:center; padding:8px 14px; background:transparent; border-left:3px solid transparent; }
        .speaker-row:hover,.speaker-row.selected,.speech-row:hover,.speech-row.selected { background:#202a3c; } .speaker-row.selected { border-left-color:var(--accent); }
        .speaker-row small { color:var(--muted); font-size:9px; } .avatar { color:var(--accent); }
        .rename { display:grid; gap:6px; justify-items:end; padding:10px 14px 14px; border-bottom:1px solid var(--line); }
        .rename cupri-textfield { box-sizing:border-box; min-width:0; width:350px; }
        .transcript-title { padding-top:14px; } .truncation { color:var(--accent); font-size:10px; padding:0 14px 6px; }
        .transcript { flex:1; min-height:0; overflow-y:auto; padding:0 7px 10px; }
        .speech-row { padding:10px; margin-bottom:5px; border-radius:7px; background:transparent; }
        .speech-head { display:flex; gap:8px; align-items:center; font-size:10px; color:var(--muted); } .speech-head strong { color:var(--accent); } .speech-head small { margin-left:auto; }
        .speech-row p { margin:5px 0 0; line-height:1.35; font-size:13px; color:var(--text); }
        .status-dot { width:7px; height:7px; border-radius:50%; background:#49d17d; }
        """;
}

[CupriBindable]
public sealed partial class ReviewModel
{
    public string ProjectTitle { get; set; } = "";
    public string Revision { get; set; } = "";
    public string PreviewDataUri { get; set; } = "";
    public string PlaybackUri { get; set; } = "";
    public string PlaybackCropStyle { get; set; } = "left:0;top:0;width:100%;height:100%";
    public string PlaybackVideoStyle { get; set; } = "left:0;top:0;width:100%;height:100%";
    public string PlaybackFit { get; set; } = "contain";
    public string ExactClass { get; set; } = "";
    public string PlaybackStatus { get; set; } = "";
    public string ExportStatus { get; set; } = "";
    public string ExportLabel { get; set; } = "Export MP4";
    public string TransportClass { get; set; } = "hidden";
    public string TransportLabel { get; set; } = "Play";
    public string TransportMuteLabel { get; set; } = "Mute";
    public string TransportPosition { get; set; } = "0:00.000";
    public string TransportDuration { get; set; } = "0:00.000";
    public string TransportFillStyle { get; set; } = "width:0%";
    public string TransportThumbStyle { get; set; } = "left:0%";
    public string SourcePreviewDataUri { get; set; } = "";
    public string Selection { get; set; } = "";
    public string Crop { get; set; } = "";
    public string LauncherClass { get; set; } = "hidden";
    public string WorkspaceClass { get; set; } = "";
    public string RecentEmptyClass { get; set; } = "hidden";
    public string BrowseClass { get; set; } = "hidden";
    public string OpenMessage { get; set; } = "";
    public string OpenPath { get; set; } = "";
    public string OpenUrl { get; set; } = "";
    public RecentRow[] Recent { get; set; } = [];
    public string FormatsClass { get; set; } = "hidden";
    /// Hides the other ways into a project while a rendition is being chosen, so the list cannot push them
    /// out of the card.
    public string PickingHiddenClass { get; set; } = "";
    /// Hides the rendition list once one has been chosen, leaving the title, the folder and the progress.
    public string FormatsListClass { get; set; } = "";
    public string FormatsTitle { get; set; } = "";
    public string FormatsHint { get; set; } = "";
    public FormatRow[] Formats { get; set; } = [];
    public string EditFolder { get; set; } = "";
    public string ProgressClass { get; set; } = "hidden";
    public string ProgressLabel { get; set; } = "";
    public string ProgressFillStyle { get; set; } = "width:0%";
    public string CancelClass { get; set; } = "hidden";
    public string ExportMenuClass { get; set; } = "hidden";
    public ExportFormatRow[] ExportFormats { get; set; } = [];
    public string CropEditorClass { get; set; } = "hidden";
    public string ClipEditorClass { get; set; } = "hidden";
    public string ClipSummary { get; set; } = "";
    public string SplitSummary { get; set; } = "";
    public string SplitClass { get; set; } = "hidden";
    public string RemoveClass { get; set; } = "hidden";
    public string MoveEarlierClass { get; set; } = "hidden";
    public string MoveLaterClass { get; set; } = "hidden";
    public string TrimIn { get; set; } = "0";
    public string TrimOut { get; set; } = "1";
    public string CropX { get; set; } = "0";
    public string CropY { get; set; } = "0";
    public string CropWidth { get; set; } = "1";
    public string CropHeight { get; set; } = "1";
    public string CropCanvasStyle { get; set; } = "";
    public string CropBoxStyle { get; set; } = "";
    public string SelectedLabel { get; set; } = "";
    public string Status { get; set; } = "";
    public string Truncation { get; set; } = "";
    public SpeakerRow[] Speakers { get; set; } = [];
    public SpeechRow[] Segments { get; set; } = [];
    public ClipRow[] Clips { get; set; } = [];
    public EvidenceRow[] Evidence { get; set; } = [];
}

[CupriBindable] public sealed partial class FormatRow { public string Id { get; set; } = ""; public string Label { get; set; } = ""; public string Detail { get; set; } = ""; public string Size { get; set; } = ""; }
[CupriBindable] public sealed partial class ExportFormatRow { public string Name { get; set; } = ""; public string Label { get; set; } = ""; public string Detail { get; set; } = ""; public string CssClass { get; set; } = ""; }
[CupriBindable] public sealed partial class RecentRow { public string Path { get; set; } = ""; public string Name { get; set; } = ""; public string Folder { get; set; } = ""; public string ProjectId { get; set; } = ""; }
[CupriBindable] public sealed partial class SpeakerRow { public string Id { get; set; } = ""; public string Label { get; set; } = ""; public string CssClass { get; set; } = ""; }
[CupriBindable] public sealed partial class SpeechRow { public string Id { get; set; } = ""; public string Time { get; set; } = ""; public string Speaker { get; set; } = ""; public string Text { get; set; } = ""; public string Badge { get; set; } = ""; public string CssClass { get; set; } = ""; }
[CupriBindable] public sealed partial class ClipRow { public string Id { get; set; } = ""; public string Range { get; set; } = ""; public string Source { get; set; } = ""; public string CssClass { get; set; } = ""; public string Style { get; set; } = ""; }
[CupriBindable] public sealed partial class EvidenceRow { public string Id { get; set; } = ""; public string Decision { get; set; } = ""; public string Summary { get; set; } = ""; }
