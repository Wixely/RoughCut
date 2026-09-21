using CupriFace;
using CupriFace.Binding;
using CupriFace.Interaction;
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
    private long _playbackGeneration;
    private CropDragState? _cropDrag;

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
        document.OnClick(".move-earlier", _ => StartCommand(() => Session.MoveSelectedClipAsync(-1),
            seekAfter: true, rebuildPlayback: true));
        document.OnClick(".move-later", _ => StartCommand(() => Session.MoveSelectedClipAsync(1),
            seekAfter: true, rebuildPlayback: true));
        document.OnClick(".undo", _ => StartCommand(() => UndoRedoAsync(redo: false), seekAfter: true, rebuildPlayback: true));
        document.OnClick(".redo", _ => StartCommand(() => UndoRedoAsync(redo: true), seekAfter: true, rebuildPlayback: true));
        document.OnClick(".reload", _ => StartCommand(ReloadAsync, seekAfter: true, rebuildPlayback: true));
        document.OnClick(".exact-preview", _ => StartExactPreview());
        document.OnPointer("data-crop-drag", HandleCropPointer);
        document.OnClick(".recent-open", e => OpenProject(Required(e, "data-path")));
        document.OnClick(".open-path", _ => OpenOrCreate(_model.OpenPath));
        document.OnClick(".browse", _ => Browse(create: false));
        document.OnClick(".new-project", _ => Browse(create: true));
        document.OnClick(".new-url", _ => CreateFromUrl(_model.OpenUrl));
        document.OnClick(".close-project", _ => ShowLauncher("Choose another project to review."));
        document.OnFileDrop(drop =>
        {
            if (drop.Files.FirstOrDefault(file => !file.IsDirectory)?.Path is { } dropped) OpenOrCreate(dropped);
        });
        if (_session is null) ShowLauncher();
        else StartReview();
    }

    private int _clipIndex;

    /// Called each presented frame. While the preview copy plays, keep it inside the current clip and jump
    /// at boundaries, which is what makes cuts and reordering visible without rendering a new video.
    public override PresentInfo Present(float width, float height)
    {
        if (_session is not null && playback is { Playing: true } && Session.Playback is null &&
            Session.Mapping is { Length: > 0 } mapping)
        {
            var step = TimelinePlayback.Advance(mapping, Session.Project.TimeBase, _clipIndex, playback.PositionSeconds);
            _clipIndex = step.ClipIndex;
            if (step.Ended) playback.Pause();
            else if (step.SeekSeconds is { } seek) playback.Seek(seek);
        }
        return base.Present(width, height);
    }

    private void StartReview()
    {
        if (Session.Preview is null)
            StartLatest(Session.InitializePreviewAsync, seekAfter: true,
                startPlaybackAfter: playback is not null && Session.Playback is null);
        else if (playback is not null && Session.Playback is null)
            StartPlaybackPreparation();
    }

    private void ShowLauncher(string? message = null)
    {
        CancelPlaybackPreparation();
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
                if (chosen is null) ShowLauncher();
                else if (create) CreateProject(chosen);
                else OpenProject(chosen);
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                ShowLauncher(exception.Message);
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

    private void OpenProject(string projectPath) => Start("Opening project…",
        "Finish the current operation before opening another project.", async () =>
        {
            var opened = await DesktopReviewSession.LoadAsync(Clean(projectPath));
            _session = opened;
            _recent.Record(opened.ProjectPath, opened.Project.ProjectId);
        });

    private void CreateFromUrl(string sourceUrl)
    {
        sourceUrl = Clean(sourceUrl);
        if (sourceUrl.Length == 0)
        {
            _model.OpenMessage = "Enter a video URL to download.";
            _document?.Refresh();
            return;
        }
        Start("Downloading the video and its subtitles… this can take several minutes.",
            "Finish the current operation before downloading a video.", async () =>
            {
                var created = await DesktopReviewSession.CreateFromUrlAsync(sourceUrl);
                _session = created;
                _recent.Record(created.ProjectPath, created.Project.ProjectId);
            });
    }

    private void CreateProject(string mediaPath) => Start("Creating a project for that video…",
        "Finish the current operation before creating a project.", async () =>
        {
            var created = await DesktopReviewSession.CreateAsync(Clean(mediaPath));
            _session = created;
            _recent.Record(created.ProjectPath, created.Project.ProjectId);
        });

    private void Start(string progress, string busy, Func<Task> action)
    {
        _model.OpenMessage = progress;
        _model.Status = progress;
        _document?.Refresh();
        if (_work.StartCommand(action, exception =>
        {
            if (exception is null)
            {
                _model.LauncherClass = "hidden";
                Complete(null, seekAfter: false);
                StartReview();
            }
            else
            {
                _session = null;
                _model.OpenMessage = exception.Message;
                Complete(exception, seekAfter: false);
            }
        })) return;
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
        if (!_work.StartLatest(action, exception =>
        {
            Complete(exception, seekAfter);
            if (exception is null && startPlaybackAfter) StartPlaybackPreparation();
        }))
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
        if (!_work.StartCommand(action, exception =>
        {
            Complete(exception, seekAfter);
            if (exception is null && rebuildPlayback && playback is not null) StartPlaybackPreparation();
        }))
        {
            _model.Status = "An edit is already being saved.";
            _document?.Refresh();
        }
    }

    /// Builds the validated export-backed render on request. This is the only place the desktop renders
    /// video, and it is explicit rather than a side effect of editing.
    private void StartExactPreview() => StartPlaybackPreparation(exact: true);

    private void StartPlaybackPreparation() => StartPlaybackPreparation(exact: false);

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
            if (failure is not null) Session.PlaybackUnavailable(failure.Message);
            Complete(failure, seekAfter: false);
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
        Rebuild(preserveStatus: true);
        _document?.Refresh();
        if (exception is null && seekAfter && _session is not null && playback is not null)
        {
            var step = Session.SelectedPlaybackStep();
            _clipIndex = step.ClipIndex;
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
        // The exact still already shows the true crop. Scaling the player to match would distort it, because
        // the preview box cannot take the crop's aspect ratio in this layout engine; see the desktop guide.
        _model.PlaybackCropStyle = "width:100%;height:100%";
        _model.PlaybackFit = "contain";
        _model.PlaybackStatus = Session.PlaybackStatus;
        _model.SourcePreviewDataUri = Session.SourcePreview is null ? "" :
            "data:image/png;base64," + Convert.ToBase64String(Session.SourcePreview.Png);
        _model.Selection = Session.Selection;
        _model.Crop = Session.Crop;
        var selectedClip = Session.SelectedClipId is null ? null :
            project.Timeline.Single(item => item.Id == Session.SelectedClipId);
        var selectedAsset = selectedClip is null ? null : project.Assets.Single(item => item.Id == selectedClip.AssetId);
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
        _model.Clips = ProjectValidator.MapTimeline(project).Take(200).Select(item => new ClipRow
        {
            Id = item.ClipId,
            Range = $"{FormatTime(item.OutputIn, project.TimeBase)}–{FormatTime(item.OutputOut, project.TimeBase)}",
            Source = item.AssetId,
            CssClass = item.ClipId == Session.SelectedClipId ? "selected" : ""
        }).ToArray();
        _model.Evidence = project.Proposals.Take(100).Select(proposal =>
        {
            var observation = project.Observations.First(item => item.Id == proposal.ObservationId);
            return new EvidenceRow { Id = observation.Id, Decision = proposal.Decision, Summary = observation.Summary };
        }).ToArray();
        _model.Truncation = project.Speech.Length > 500 ? $"Showing 500 of {project.Speech.Length} transcript rows" : "";
        if (!preserveStatus) _model.Status = "Ready";
    }

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
                <div class="recent-empty {{RecentEmptyClass}}">No projects opened yet on this computer.</div>
                <div class="recent-list"><button class="recent-open" data-repeat="Recent" data-path="{{Path}}"><strong>{{Name}}</strong><span>{{Folder}}</span><small>{{ProjectId}}</small></button></div>
                <div class="launcher-url"><cupri-textfield value="{{OpenUrl}}" placeholder="https://… video URL, downloaded with its subtitles"></cupri-textfield><cupri-button class="new-url">Fetch</cupri-button></div>
                <div class="launcher-actions {{BrowseClass}}"><cupri-button class="new-project" variant="ghost">New from a local video…</cupri-button><cupri-button class="browse" variant="ghost">Open a project…</cupri-button></div>
                <div class="launcher-open"><cupri-textfield value="{{OpenPath}}" placeholder="Path to a project.json or a video"></cupri-textfield><cupri-button class="open-path" variant="ghost">Go</cupri-button></div>
              </div>
            </section>
            <section class="workspace {{WorkspaceClass}}">
              <div class="stage-column">
                <div class="preview-card">
                  <div class="preview"><div class="preview-crop" style="{{PlaybackCropStyle}}"><cupri-video src="{{PlaybackUri}}" poster="{{PreviewDataUri}}" fit="{{PlaybackFit}}" controls label="Project timeline playback"></cupri-video></div></div>
                  <div class="preview-actions"><cupri-button class="exact-preview {{ExactClass}}" variant="ghost">Render exact preview</cupri-button></div>
                  <div class="preview-meta"><strong>{{Selection}}</strong><span>{{Crop}}</span><span>{{PlaybackStatus}}</span></div>
                </div>
                <div class="timeline-card">
                  <div class="section-title">Timeline</div>
                  <div class="timeline"><button class="clip {{CssClass}}" data-repeat="Clips" data-id="{{Id}}"><strong>{{Id}}</strong><span>{{Range}}</span><small>{{Source}}</small></button></div>
                  <div class="clip-editor {{ClipEditorClass}}">
                    <div class="clip-summary">{{ClipSummary}} · {{SplitSummary}}</div>
                    <div class="clip-controls"><label><span>IN</span><cupri-textfield value="{{TrimIn}}"></cupri-textfield></label><label><span>OUT</span><cupri-textfield value="{{TrimOut}}"></cupri-textfield></label><div class="clip-actions"><cupri-button class="apply-trim">Apply trim</cupri-button><cupri-button class="split-clip {{SplitClass}}" variant="ghost">Split</cupri-button><cupri-button class="move-earlier {{MoveEarlierClass}}" variant="ghost">Earlier</cupri-button><cupri-button class="move-later {{MoveLaterClass}}" variant="ghost">Later</cupri-button></div></div>
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
        .workspace.hidden,.launcher.hidden,.actions .hidden,.recent-empty.hidden { display:none; }
        .launcher { flex:1; min-height:0; display:flex; align-items:center; justify-content:center; padding:24px; }
        .launcher-card { width:660px; height:470px; display:flex; flex-direction:column; background:var(--panel); border:1px solid var(--line); border-radius:12px; padding:18px 18px 16px; }
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
        .stage-column { min-width:0; display:grid; grid-template-rows:minmax(300px,1fr) 190px 130px; gap:14px; }
        .preview-card,.timeline-card,.evidence-card,.review-panel { background:var(--panel); border:1px solid var(--line); border-radius:12px; }
        .preview-card { min-height:350px; padding:12px; display:flex; flex-direction:column; }
        .preview { flex:1; min-height:300px; display:flex; align-items:center; justify-content:center; background:#05070b; border-radius:8px; overflow:hidden; }
        .preview { position:relative; }
        .preview-crop { position:absolute; }
        .preview cupri-video { width:100%; height:100%; }
        .preview-actions { display:flex; justify-content:flex-end; padding-top:8px; } .preview-actions .hidden { display:none; }
        .preview-meta { padding:10px 4px 0; display:flex; justify-content:space-between; gap:12px; color:var(--muted); font-size:12px; } .preview-meta strong { color:var(--text); }
        .section-title { padding:12px 14px 8px; color:var(--muted); text-transform:uppercase; letter-spacing:1.2px; font-size:11px; font-weight:bold; }
        .timeline { display:flex; gap:6px; padding:0 12px 12px; overflow:hidden; }
        .clip { min-width:112px; flex:1; padding:9px; border:0; border-radius:7px; background:var(--panel2); border-top:3px solid var(--accent); display:flex; flex-direction:column; gap:3px; text-align:left; cursor:pointer; }
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
    public string PlaybackCropStyle { get; set; } = "width:100%;height:100%";
    public string PlaybackFit { get; set; } = "contain";
    public string ExactClass { get; set; } = "";
    public string PlaybackStatus { get; set; } = "";
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
    public string CropEditorClass { get; set; } = "hidden";
    public string ClipEditorClass { get; set; } = "hidden";
    public string ClipSummary { get; set; } = "";
    public string SplitSummary { get; set; } = "";
    public string SplitClass { get; set; } = "hidden";
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

[CupriBindable] public sealed partial class RecentRow { public string Path { get; set; } = ""; public string Name { get; set; } = ""; public string Folder { get; set; } = ""; public string ProjectId { get; set; } = ""; }
[CupriBindable] public sealed partial class SpeakerRow { public string Id { get; set; } = ""; public string Label { get; set; } = ""; public string CssClass { get; set; } = ""; }
[CupriBindable] public sealed partial class SpeechRow { public string Id { get; set; } = ""; public string Time { get; set; } = ""; public string Speaker { get; set; } = ""; public string Text { get; set; } = ""; public string Badge { get; set; } = ""; public string CssClass { get; set; } = ""; }
[CupriBindable] public sealed partial class ClipRow { public string Id { get; set; } = ""; public string Range { get; set; } = ""; public string Source { get; set; } = ""; public string CssClass { get; set; } = ""; }
[CupriBindable] public sealed partial class EvidenceRow { public string Id { get; set; } = ""; public string Decision { get; set; } = ""; public string Summary { get; set; } = ""; }
