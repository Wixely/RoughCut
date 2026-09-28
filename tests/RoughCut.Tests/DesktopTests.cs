using RoughCut.Core;
using RoughCut.Media;

internal static class DesktopTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string[] args, string root)
    {
        var option = Array.IndexOf(args, "--desktop");
        if (option < 0) return;
        if (option + 1 >= args.Length) throw new ArgumentException("--desktop requires an executable or DLL path.");
        var desktop = Path.GetFullPath(args[option + 1]);

        await check("Desktop snapshot renders a revision-aware timeline preview", async () =>
        {
            var source = Path.Combine(root, "export source.mkv");
            var info = await new MediaReader(RoughCut.Application.ToolSettings.Default.Ffmpeg,
                RoughCut.Application.ToolSettings.Default.Ffprobe).InspectAsync(source);
            var duration = Math.Min(info.DurationTicks, 4000);
            var projectPath = Path.Combine(root, "desktop-preview-project.json");
            await new ProjectStore().SaveAsync(projectPath, new EditProject
            {
                ProjectId = "desktop-preview",
                TimeBase = info.TimeBase,
                Assets = [new("source", "video", "export source.mkv", info.Sha256, info.DurationTicks,
                    info.Width, info.Height, "video/x-matroska")],
                Timeline = [new("clip", "source", 0, duration)],
                Speakers = [new("speaker-1", "Host")],
                Speech = [new("speech", "source", 0, Math.Min(duration, 1000), "Review this retained segment.",
                    ["speaker-1"], "corrected")]
            }, 0);
            var output = Path.Combine(root, "desktop-preview.png");
            var arguments = new[] { "snapshot", projectPath, output };
            var result = desktop.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? await ToolProcess.RunAsync("dotnet", new[] { desktop }.Concat(arguments), timeout: TimeSpan.FromMinutes(2))
                : await ToolProcess.RunAsync(desktop, arguments, timeout: TimeSpan.FromMinutes(2));
            var dimensions = PngImage.ReadDimensions(await File.ReadAllBytesAsync(output));
            Assert(dimensions.Width == 1280 && dimensions.Height == 800 && new FileInfo(output).Length > 10_000 &&
                System.Text.Encoding.UTF8.GetString(result.Output).Contains(output, StringComparison.OrdinalIgnoreCase),
                "Desktop snapshot did not contain the expected rendered review surface.");
        });

        await check("Desktop crop editing persists with undo and redo", async () =>
        {
            var projectPath = Path.Combine(root, "desktop-preview-project.json");
            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            await session.InitializePreviewAsync();
            var asset = session.Project.Assets.Single(item => item.Id == "source");
            var crop = new Crop(8, 4, asset.Width - 16, asset.Height - 8);
            await session.ApplyCropAsync(crop);
            await session.UndoAsync();
            await session.RedoAsync();
            var saved = await new ProjectStore().LoadAsync(projectPath);
            Assert(saved.Revision == 4 && saved.Timeline.Single().Crop == crop && session.SourcePreview is not null &&
                session.Preview?.Info.Crop == crop && session.CanUndo && !session.CanRedo && session.Playback is null,
                "Desktop crop history or exact-frame refresh did not preserve the revisioned edit.");
        });

        await check("Desktop trim, split and reorder persist with undo and redo", async () =>
        {
            var source = Path.Combine(root, "export source.mkv");
            var info = await new MediaReader(RoughCut.Application.ToolSettings.Default.Ffmpeg,
                RoughCut.Application.ToolSettings.Default.Ffprobe).InspectAsync(source);
            var middle = info.DurationTicks / 2;
            var projectPath = Path.Combine(root, "desktop-timeline-project.json");
            await new ProjectStore().SaveAsync(projectPath, new EditProject
            {
                ProjectId = "desktop-timeline",
                TimeBase = info.TimeBase,
                Assets = [new("source", "video", "export source.mkv", info.Sha256, info.DurationTicks,
                    info.Width, info.Height, "video/x-matroska")],
                Timeline = [new("a", "source", 0, info.DurationTicks)],
                Speakers = [new("speaker-1", "Host")],
                Speech = [new("speech", "source", middle, info.DurationTicks, "Split at this retained frame.",
                    ["speaker-1"], "corrected")]
            }, 0);
            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            await session.InitializePreviewAsync();
            var at = session.Preview?.Info.SourceActual?.Ticks ?? throw new Exception("Expected a selected source frame.");
            Assert(at > 0 && at < info.DurationTicks, "The selected speech frame was not strictly inside the clip.");

            await session.SplitSelectedClipAsync();
            Assert(session.Project.Timeline.Select(item => item.Id).SequenceEqual(["a", "a-2"]) &&
                session.Project.Timeline[0].Out == at && session.Project.Timeline[1].In == at &&
                session.SelectedClipId == "a-2", "Splitting at the selected frame did not divide the clip.");

            var trimmed = info.DurationTicks - 1;
            await session.TrimSelectedClipAsync(at, trimmed);
            Assert(session.Project.Timeline[1].Out == trimmed, "The trim did not shorten the selected clip.");

            await session.MoveSelectedClipAsync(-1);
            Assert(session.Project.Timeline.Select(item => item.Id).SequenceEqual(["a-2", "a"]),
                "Reordering did not move the selected clip earlier.");

            for (var undone = 0; undone < 3; undone++) await session.UndoAsync();
            var restored = await new ProjectStore().LoadAsync(projectPath);
            Assert(restored.Revision == 7 && restored.Timeline.Length == 1 && restored.Timeline[0].Id == "a" &&
                restored.Timeline[0].In == 0 && restored.Timeline[0].Out == info.DurationTicks &&
                !session.CanUndo && session.CanRedo && session.Playback is null,
                "Undoing the timeline edits did not restore the original clip in one revision each.");

            for (var redone = 0; redone < 3; redone++) await session.RedoAsync();
            var reapplied = await new ProjectStore().LoadAsync(projectPath);
            Assert(reapplied.Revision == 10 && reapplied.Timeline.Select(item => item.Id).SequenceEqual(["a-2", "a"]) &&
                reapplied.Timeline[0].In == at && reapplied.Timeline[0].Out == trimmed &&
                reapplied.Timeline[1].Out == at && session.CanUndo && !session.CanRedo,
                "Redoing the timeline edits did not reproduce the split, trim and order.");
        });

        await check("Desktop clip removal persists with undo and redo", async () =>
        {
            var source = Path.Combine(root, "export source.mkv");
            var info = await new MediaReader(RoughCut.Application.ToolSettings.Default.Ffmpeg,
                RoughCut.Application.ToolSettings.Default.Ffprobe).InspectAsync(source);
            var third = info.DurationTicks / 3;
            var projectPath = Path.Combine(root, "desktop-removal-project.json");
            await new ProjectStore().SaveAsync(projectPath, new EditProject
            {
                ProjectId = "desktop-removal",
                TimeBase = info.TimeBase,
                Assets = [new("source", "video", "export source.mkv", info.Sha256, info.DurationTicks,
                    info.Width, info.Height, "video/x-matroska")],
                // Every video clip shares one output canvas, so the crop is the same on all three.
                Timeline = [new("first", "source", 0, third, new(8, 4, info.Width - 16, info.Height - 8)),
                    new("advert", "source", third, third * 2, new(8, 4, info.Width - 16, info.Height - 8)),
                    new("last", "source", third * 2, info.DurationTicks, new(8, 4, info.Width - 16, info.Height - 8))]
            }, 0);
            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            await session.InitializePreviewAsync();
            await session.SelectClipAsync("advert");
            var removed = session.Project.Timeline.Single(clip => clip.Id == "advert");

            await session.RemoveSelectedClipAsync();
            Assert(session.Project.Timeline.Select(clip => clip.Id).SequenceEqual(["first", "last"]),
                "Removing the selected clip did not cut it out of the timeline.");

            await session.UndoAsync();
            Assert(session.Project.Timeline.Select(clip => clip.Id).SequenceEqual(["first", "advert", "last"]) &&
                session.Project.Timeline[1] == removed,
                "Undo did not put the removed clip back exactly where it was.");

            await session.RedoAsync();
            var saved = await new ProjectStore().LoadAsync(projectPath);
            Assert(saved.Timeline.Select(clip => clip.Id).SequenceEqual(["first", "last"]) && saved.Revision == 4,
                "Redo did not remove the clip again in one revision each.");

            // The last clip cannot be removed: nothing in the window could add a clip back to an empty timeline.
            await session.SelectClipAsync("first");
            await session.RemoveSelectedClipAsync();
            await session.SelectClipAsync("last");
            await Throws<InvalidOperationException>(() => session.RemoveSelectedClipAsync());
            Assert(session.Project.Timeline.Length == 1, "The last clip was removed.");

            var app = new RoughCut.Desktop.RoughCutReviewApp(session);
            using var document = app.CreateDocument();
            document.Refresh();
            var model = (RoughCut.Desktop.ReviewModel)app.Model;
            Assert(model.RemoveClass == "hidden", "Remove was offered for the only clip on the timeline.");
        });

        await check("Supplied review proxy survives edits and reports that it is behind", async () =>
        {
            var projectPath = Path.Combine(root, "desktop-timeline-project.json");
            var previewPath = Path.Combine(root, "supplied-review.webm");
            await File.WriteAllBytesAsync(previewPath, new byte[64]);
            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            await session.InitializePreviewAsync();
            session.UsePlaybackPreview(previewPath);
            var supplied = session.Playback ?? throw new Exception("The supplied review proxy was not accepted.");
            Assert(supplied.Path == previewPath && supplied.Revision == session.Project.Revision &&
                session.PlaybackStatus.Contains("not validated export", StringComparison.Ordinal) &&
                !session.PlaybackStatus.Contains("does not show edits", StringComparison.Ordinal),
                "A current supplied proxy was not reported as an unvalidated render.");

            await session.MoveSelectedClipAsync(1);
            Assert(ReferenceEquals(session.Playback, supplied) &&
                session.PlaybackStatus.Contains("does not show edits", StringComparison.Ordinal),
                "An edit discarded the supplied review proxy instead of marking it behind.");

            await session.PreparePlaybackAsync();
            Assert(ReferenceEquals(session.Playback, supplied),
                "Playback preparation replaced the supplied review proxy.");

            await session.UndoAsync();
            Assert(ReferenceEquals(session.Playback, supplied), "Undo discarded the supplied review proxy.");

            await session.ReloadAsync();
            Assert(ReferenceEquals(session.Playback, supplied) &&
                session.PlaybackStatus.Contains("does not show edits", StringComparison.Ordinal),
                "Reloading discarded the supplied review proxy.");

            var empty = Path.Combine(root, "empty-review.webm");
            await File.WriteAllBytesAsync(empty, []);
            Throws<InvalidDataException>(() => session.UsePlaybackPreview(empty));
            Throws<ArgumentException>(() => session.UsePlaybackPreview(projectPath));
            Throws<FileNotFoundException>(() => session.UsePlaybackPreview(Path.Combine(root, "absent-review.webm")));
            Assert(ReferenceEquals(session.Playback, supplied),
                "A rejected review proxy replaced the accepted one.");
        });

        await check("Review window exports the timeline to a deliverable MP4 beside the project", async () =>
        {
            var projectPath = Path.Combine(root, "desktop-timeline-project.json");
            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            await session.InitializePreviewAsync();
            var revision = session.Project.Revision;
            var report = await session.DeliverAsync();
            var expected = Path.Combine(root, "desktop-timeline-project-export", "video.mp4");
            Assert(session.DeliveredPath == expected && File.Exists(expected) && report.Plan.Revision == revision,
                "The window did not publish an MP4 beside the project for the current revision.");
            Assert(report.Plan.Clips.Length == session.Project.Timeline.Length &&
                session.DeliveryStatus.Contains("Exported", StringComparison.Ordinal) &&
                session.DeliveryStatus.Contains("MiB", StringComparison.Ordinal),
                "The export did not report what it published.");
            Assert(session.Project.Revision == revision && (await new ProjectStore().LoadAsync(projectPath)).Revision == revision,
                "Exporting changed the project.");

            // The window shows the outcome and offers the action; a second export never overwrites the first.
            var app = new RoughCut.Desktop.RoughCutReviewApp(session);
            using var document = app.CreateDocument();
            document.Refresh();
            var model = (RoughCut.Desktop.ReviewModel)app.Model;
            Assert(model.ExportStatus == session.DeliveryStatus && model.ExportLabel == "Export MP4" &&
                document.DebugDump(1280, 800).Contains("Export MP4", StringComparison.Ordinal),
                "The review window did not offer the export action or show its outcome.");

            await session.DeliverAsync();
            Assert(session.DeliveredPath == Path.Combine(root, "desktop-timeline-project-export-2", "video.mp4") &&
                File.Exists(expected), "A second export overwrote or reused the first bundle.");

            await Throws<OperationCanceledException>(() => session.DeliverAsync(new(true)));
            Assert(!Directory.EnumerateDirectories(root, ".roughcut-delivery-*").Any() &&
                session.DeliveryStatus.Contains("canceled", StringComparison.OrdinalIgnoreCase),
                "A cancelled export left staged artifacts or did not say it published nothing.");

            // An unrenderable timeline is refused with its reason before any encode runs.
            var imageProject = Path.Combine(root, "desktop-image-project.json");
            var timeline = await new ProjectStore().LoadAsync(Path.Combine(root, "image-project.json"));
            await new ProjectStore().SaveAsync(imageProject, timeline with { ProjectId = "desktop-image", Revision = 1 }, 0);
            var images = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(imageProject);
            await Throws<InvalidOperationException>(() => images.DeliverAsync());
            Assert(images.DeliveryStatus.Contains("video clips only", StringComparison.Ordinal),
                "A timeline delivery cannot render was refused without saying why.");
        });

        await check("Recent projects are ordered, bounded, deduplicated and damage tolerant", async () =>
        {
            var storePath = Path.Combine(root, "recent", "recent-projects.json");
            var store = new RoughCut.Desktop.RecentProjects(storePath);
            Assert(store.Load().Count == 0, "An absent history was not empty.");

            var paths = new List<string>();
            for (var index = 0; index < RoughCut.Desktop.RecentProjects.MaxEntries + 3; index++)
            {
                var path = Path.Combine(root, $"recent-{index}.json");
                await File.WriteAllTextAsync(path, "{}");
                paths.Add(path);
                store.Record(path, $"project-{index}");
            }
            var loaded = store.Load();
            Assert(loaded.Count == RoughCut.Desktop.RecentProjects.MaxEntries,
                "The history was not bounded to its maximum entry count.");
            Assert(loaded[0].Path == paths[^1] && loaded[0].ProjectId == $"project-{paths.Count - 1}",
                "The most recently opened project was not first.");

            store.Record(paths[^3], "project-reopened");
            loaded = store.Load();
            Assert(loaded[0].Path == paths[^3] && loaded.Count(item => item.Path == paths[^3]) == 1,
                "Reopening a project did not move a single entry to the front.");

            File.Delete(paths[^1]);
            Assert(store.Load().All(item => item.Path != paths[^1]), "A deleted project stayed in the history.");
            Assert(store.Forget(paths[^3]).All(item => item.Path != paths[^3]), "Forgetting a project did not remove it.");

            await File.WriteAllTextAsync(storePath, "{ this is not json");
            Assert(store.Load().Count == 0, "A damaged history was not tolerated.");
            await File.WriteAllTextAsync(storePath, "{\"schemaVersion\":1,\"projects\":[]}");
            Assert(store.Record(paths[0], "project-0").Count == 1, "The history did not recover after damage.");
        });

        await check("Launcher lists recent projects until one is open", async () =>
        {
            var projectPath = Path.Combine(root, "desktop-timeline-project.json");
            var storePath = Path.Combine(root, "launcher-recent.json");
            new RoughCut.Desktop.RecentProjects(storePath).Record(projectPath, "desktop-timeline");
            var launcherApp = new RoughCut.Desktop.RoughCutReviewApp(
                null, null, new RoughCut.Desktop.RecentProjects(storePath));
            using var launcher = launcherApp.CreateDocument();
            launcher.Refresh();
            var model = (RoughCut.Desktop.ReviewModel)launcherApp.Model;
            Assert(model.LauncherClass == "" && model.WorkspaceClass == "hidden" && model.Recent.Length == 1 &&
                model.Recent[0].Path == Path.GetFullPath(projectPath) &&
                model.Recent[0].Name == "desktop-timeline-project.json" &&
                model.Recent[0].ProjectId == "desktop-timeline",
                "The launcher did not offer the recorded project while no project was open.");
            Assert(launcher.DebugDump(1280, 800).Contains("desktop-timeline-project.json", StringComparison.Ordinal),
                "The recent project was not rendered.");

            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            var reviewApp = new RoughCut.Desktop.RoughCutReviewApp(session);
            using var review = reviewApp.CreateDocument();
            review.Refresh();
            var opened = (RoughCut.Desktop.ReviewModel)reviewApp.Model;
            Assert(opened.LauncherClass == "hidden" && opened.WorkspaceClass == "" &&
                opened.ProjectTitle == "desktop-timeline-project.json",
                "An opened project still showed the launcher.");
        });

        await check("An edit from a local video lands in one folder, copied in with progress", async () =>
        {
            var media = Path.Combine(root, "folder-edit-source.mkv");
            File.Copy(Path.Combine(root, "export source.mkv"), media, overwrite: true);
            var folder = Path.Combine(root, "folder-edits", "My Edit");
            var fractions = new List<double>();
            var session = await RoughCut.Desktop.DesktopReviewSession.CreateInFolderAsync(media, default, folder,
                new Progress<double>(value => { lock (fractions) fractions.Add(value); }));

            // Everything the edit needs is in the one folder: the project file and its own copy of the video.
            Assert(session.ProjectPath == Path.Combine(folder, "project.json") && File.Exists(session.ProjectPath) &&
                File.Exists(Path.Combine(folder, "folder-edit-source.mkv")),
                "The edit folder does not hold both the project and its copy of the video.");
            Assert(session.Project.Assets[0].Path == "folder-edit-source.mkv" &&
                RoughCut.Core.ProjectValidator.IsPortablePath(session.Project.Assets[0].Path),
                "The copied video is not referenced by a portable path beside the project.");
            Assert(File.Exists(media), "Creating the edit moved the person's own video instead of copying it.");
            for (var wait = 0; wait < 100; wait++)
            {
                lock (fractions) if (fractions.Count > 0 && Math.Abs(fractions[^1] - 1) < 1e-9) break;
                await Task.Delay(20);
            }
            lock (fractions)
                Assert(fractions.Count > 0 && Math.Abs(fractions[^1] - 1) < 1e-9,
                    "Copying the video into the edit folder reported no progress.");
            // A folder that is already an edit is refused rather than being written into twice.
            await Throws<IOException>(() => RoughCut.Desktop.DesktopReviewSession.CreateInFolderAsync(media, default, folder));
        });

        await check("The rendition picker keeps its controls apart and inside the card", () =>
        {
            // A real source offers dozens of renditions, and the first build of this list took the whole card:
            // the progress bar, its label and Cancel were then drawn on top of the rows. The card cannot lay
            // one control over another, whatever the source offers.
            int[] heights = [2160, 1440, 1080, 720, 480, 360, 240, 144, 96, 72];
            string[] codecs = ["vp09.00.50.08", "av01.0.12M.08", "avc1.640028"];
            var many = Enumerable.Range(0, 42).Select(index => new RoughCut.Application.SourceFormat(
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture), "mp4",
                    index % 7 == 0 ? "audio" : "video", 1920, heights[index % heights.Length], 60,
                    codecs[index % codecs.Length], index % 7 == 0 ? "opus" : "none",
                    9000 - index * 100, 40L * 1024 * 1024))
                .ToArray();
            var offered = new RoughCut.Application.SourceFormatList(1, "https://example.test/many",
                "A source with a long name that wraps across two lines in the card", 852, 512L * 1024 * 1024,
                many, ["highest", "medium", "lowest"]);

            var app = new RoughCut.Desktop.RoughCutReviewApp(null, null,
                new RoughCut.Desktop.RecentProjects(Path.Combine(root, "picker-recent.json")));
            using var document = app.CreateDocument();
            app.OfferFormats("https://example.test/many", offered);
            var model = (RoughCut.Desktop.ReviewModel)app.Model;
            // Exactly the state a person saw: the list still showing while a download reports progress over
            // it. The window now puts the list away once a rendition is chosen, but the card has to hold both
            // at once regardless, because that is what a long list plus a progress row asks of it.
            model.ProgressClass = "";
            model.CancelClass = "";
            model.ProgressLabel = "Downloading · 12%";
            model.ProgressFillStyle = "width:12%";
            document.Refresh();

            using var dump = System.Text.Json.JsonDocument.Parse(document.DebugDump(1280, 800));
            var card = FindNodeCore(dump.RootElement.GetProperty("tree"), "launcher-card");
            Assert(card.ValueKind != System.Text.Json.JsonValueKind.Undefined, "The launcher card was not rendered.");
            var cardBox = Box(card);

            // Every control the card holds, as laid out. Text and the bar's own fill are skipped: a fill sits
            // inside its track by design, and text sits inside the control that owns it.
            var controls = new List<(string What, float[] Box)>();
            Collect(card);
            void Collect(System.Text.Json.JsonElement node)
            {
                if (node.TryGetProperty("class", out var classes) && node.TryGetProperty("box", out _))
                {
                    var names = (classes.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (names.Any(name => name is "formats-head" or "formats-folder" or "format-pick" or
                        "launcher-url" or "progress" or "launcher-actions" or "launcher-open" or "recent-list"))
                    {
                        var box = Box(node);
                        if (box[2] > 1 && box[3] > 1) controls.Add((names[0], box));
                    }
                }
                if (node.TryGetProperty("children", out var children))
                    foreach (var child in children.EnumerateArray()) Collect(child);
            }

            var outside = controls.Where(control => control.What != "format-pick" &&
                (control.Box[1] < cardBox[1] - 0.5f ||
                 control.Box[1] + control.Box[3] > cardBox[1] + cardBox[3] + 0.5f)).ToArray();
            Assert(outside.Length == 0,
                $"A control fell outside the launcher card: {string.Join("; ", outside.Select(control => control.What))}");

            var overlaps = new List<string>();
            for (var first = 0; first < controls.Count; first++)
                for (var second = first + 1; second < controls.Count; second++)
                {
                    var one = controls[first];
                    var other = controls[second];
                    var vertical = Math.Min(one.Box[1] + one.Box[3], other.Box[1] + other.Box[3]) -
                        Math.Max(one.Box[1], other.Box[1]);
                    var horizontal = Math.Min(one.Box[0] + one.Box[2], other.Box[0] + other.Box[2]) -
                        Math.Max(one.Box[0], other.Box[0]);
                    if (vertical > 0.5f && horizontal > 0.5f)
                        overlaps.Add($"{one.What} over {other.What}");
                }
            Assert(overlaps.Count == 0, $"Launcher controls overlap: {string.Join("; ", overlaps)}");
            return Task.CompletedTask;
        });

        await check("Cancelling a fetch stops it and leaves no half-made edit", async () =>
        {
            // The coordinator is what the Cancel button reaches, so it is exercised directly: a command that
            // cannot finish on its own is started, cancelled, and has to end as cancelled.
            var coordinator = new RoughCut.Desktop.DesktopWorkCoordinator();
            Assert(!coordinator.CancelCommand(), "Cancelling with nothing running claimed to cancel something.");
            var running = new TaskCompletionSource();
            Exception? outcome = null;
            var finished = new TaskCompletionSource();
            Assert(coordinator.StartCancellableCommand(async token =>
            {
                running.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }, exception => { outcome = exception; finished.TrySetResult(); }),
                "A cancellable command would not start.");
            await running.Task;
            Assert(coordinator.CancelCommand(), "The running command could not be cancelled.");
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(outcome is OperationCanceledException,
                $"A cancelled command reported {outcome?.GetType().Name ?? "success"} instead of cancellation.");

            // And the work itself leaves nothing: a copy cancelled part way through takes its folder with it.
            var media = Path.Combine(root, "cancel-source.mkv");
            File.Copy(Path.Combine(root, "export source.mkv"), media, overwrite: true);
            var folder = Path.Combine(root, "cancelled-edit");
            using var cancellation = new CancellationTokenSource();
            await Throws<OperationCanceledException>(() => RoughCut.Desktop.DesktopReviewSession.CreateInFolderAsync(
                media, cancellation.Token, folder, new Progress<double>(_ => cancellation.Cancel())));
            Assert(!Directory.Exists(folder), "A cancelled edit left its folder behind.");
            Assert(File.Exists(media), "A cancelled edit removed the person's own video.");
        });

        await check("The window offers export formats and defaults to copying the source", async () =>
        {
            var projectPath = Path.Combine(root, "desktop-timeline-project.json");
            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            var app = new RoughCut.Desktop.RoughCutReviewApp(session);
            using var document = app.CreateDocument();
            document.Refresh();
            var model = (RoughCut.Desktop.ReviewModel)app.Model;

            // Copying is the default, and the button says so before anything is chosen.
            Assert(model.ExportLabel.Contains("Original", StringComparison.Ordinal) ||
                model.ExportLabel == "Export MP4",
                $"The export button does not name what it would produce: {model.ExportLabel}.");
            Assert(model.ExportMenuClass == "hidden", "The format menu was open before it was asked for.");

            var formats = await session.ListExportFormatsAsync();
            Assert(formats.Options[0].Copy && formats.Options.Any(option => !option.Copy) &&
                formats.Options.Count(option => option.Copy) >= 1,
                "The window would offer no copy option, or no re-encoded one.");
            Assert(formats.Options.All(option => option.Label.Length > 0 && option.Extension.StartsWith('.')),
                "An offered format is unlabelled or has no extension.");
        });

        await check("The window follows an outside change to the project", async () =>
        {
            var projectPath = Path.Combine(root, "desktop-follow-project.json");
            File.Copy(Path.Combine(root, "desktop-timeline-project.json"), projectPath, overwrite: true);
            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            var app = new RoughCut.Desktop.RoughCutReviewApp(session);
            using var document = app.CreateDocument();
            document.Refresh();
            var model = (RoughCut.Desktop.ReviewModel)app.Model;
            var started = session.Project.Revision;

            // Exactly what an agent's edit over MCP looks like from here: a new revision on disk, written by
            // somebody else. The window is expected to notice and follow it.
            var store = new RoughCut.Core.ProjectStore();
            var outside = await store.LoadAsync(projectPath);
            await store.SaveAsync(projectPath, outside with
            {
                Revision = started + 1,
                Timeline = [outside.Timeline[0] with { Out = outside.Timeline[0].In + 100 }]
            }, started);

            var followed = false;
            for (var wait = 0; wait < 200 && !followed; wait++)
            {
                await Task.Delay(25);
                // A live window applies posted changes on its next frame; a headless one is pumped here.
                app.PumpPendingChanges();
                document.Refresh();
                followed = ((RoughCut.Desktop.ReviewModel)app.Model).Revision.Contains(
                    (started + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            }
            Assert(followed, $"The window did not follow an outside edit; it still shows revision {model.Revision}.");
        });

        await check("New projects are created beside their video without overwriting", async () =>
        {
            var media = Path.Combine(root, "new-project-source.mkv");
            File.Copy(Path.Combine(root, "export source.mkv"), media, overwrite: true);
            var session = await RoughCut.Desktop.DesktopReviewSession.CreateAsync(media);

            var expected = Path.Combine(root, "new-project-source.json");
            Assert(session.ProjectPath == expected && File.Exists(expected),
                "The project was not created beside its video with the video's name.");
            var project = session.Project;
            Assert(project.Revision == 1 && project.Assets.Length == 1 && project.Timeline.Length == 1,
                "A new project did not start at revision 1 with one asset and one clip.");
            var asset = project.Assets[0];
            Assert(asset.Kind == "video" && asset.Path == "new-project-source.mkv" &&
                RoughCut.Core.ProjectValidator.IsPortablePath(asset.Path) && asset.Duration > 0,
                "The new project did not reference its video by a portable relative path.");
            Assert(project.Timeline[0].In == 0 && project.Timeline[0].Out == asset.Duration,
                "The first clip did not cover the whole source.");
            Assert(RoughCut.Core.ProjectValidator.Validate(project).Length == 0, "The new project did not validate.");

            // A second project for the same video must not overwrite the first.
            var second = await RoughCut.Desktop.DesktopReviewSession.CreateAsync(media);
            Assert(second.ProjectPath == Path.Combine(root, "new-project-source-2.json") && File.Exists(expected),
                "Creating a second project overwrote or reused the first.");

            await Throws<FileNotFoundException>(() =>
                RoughCut.Desktop.DesktopReviewSession.CreateAsync(Path.Combine(root, "absent-source.mkv")));
            await Throws<MediaToolException>(() =>
                RoughCut.Desktop.DesktopReviewSession.CreateAsync(Path.Combine(root, "launcher-recent.json")));
        });

        await check("Native file picker opens a real dialog and reports cancellation", async () =>
        {
            Assert(RoughCut.Desktop.NativeFileDialog.Available == OperatingSystem.IsWindows() ||
                OperatingSystem.IsLinux(), "File picker availability did not match the platform.");
            if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
                return; // A real dialog needs an interactive Windows desktop.

            const string title = "RoughCut file dialog probe";
            var workingDirectory = Environment.CurrentDirectory;
            var filter = new RoughCut.Desktop.FileFilter("RoughCut projects", "json");
            var opened = Task.Run(() => RoughCut.Desktop.NativeFileDialog.OpenFile(title, filter, root, 0));
            var dialog = nint.Zero;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline && (dialog = FindDialog(title)) == 0) await Task.Delay(50);
            var found = dialog != 0;
            if (found) CloseDialog(dialog);
            var selected = await opened.WaitAsync(TimeSpan.FromSeconds(20));
            Assert(found, "The Windows common dialog never appeared with the requested title.");
            Assert(selected is null, "Cancelling the file dialog did not report an empty selection.");
            // GetOpenFileName moves the process working directory; every later relative path depends on this.
            Assert(Environment.CurrentDirectory == workingDirectory,
                "The file dialog left the process working directory changed.");
        });

        await check("The transport reads the timeline, not the source copy behind it", async () =>
        {
            // Pure mapping first: a timeline that keeps one second out of each half of a four-second source.
            var project = new EditProject
            {
                ProjectId = "transport",
                TimeBase = new(1, 1000),
                Assets = [new("source", "video", "export source.mkv", new string('a', 64), 4000, 160, 96, "video/x-matroska")],
                Timeline = [new("second", "source", 1000, 2000), new("fourth", "source", 3000, 4000)]
            };
            var map = RoughCut.Core.ProjectValidator.MapTimeline(project);
            var duration = RoughCut.Desktop.TimelineTransport.Duration(map, project.TimeBase);
            Assert(duration == 2, "The transport reported the source duration instead of the timeline's.");
            // Source second 3.5 is timeline second 1.5: the second clip, half a second in.
            var position = RoughCut.Desktop.TimelinePlayback.OutputSeconds(map, project.TimeBase, 1, 3.5);
            Assert(Math.Abs(position - 1.5) < 1e-9 &&
                Math.Abs(RoughCut.Desktop.TimelineTransport.Fraction(position, duration) - 0.75) < 1e-9,
                "The transport did not place the playing source position on the timeline.");
            Assert(RoughCut.Desktop.TimelineTransport.Format(position) == "0:01.500" &&
                RoughCut.Desktop.TimelineTransport.Format(3725.5) == "1:02:05.500" &&
                RoughCut.Desktop.TimelineTransport.Format(-1) == "0:00.000",
                "The transport formatted timeline time incorrectly.");

            // Pressing the track seeks in timeline seconds and clamps to its ends.
            Assert(RoughCut.Desktop.TimelineTransport.Seek(120, 20, 200, duration) == 1 &&
                RoughCut.Desktop.TimelineTransport.Seek(5, 20, 200, duration) == 0 &&
                RoughCut.Desktop.TimelineTransport.Seek(900, 20, 200, duration) == 2 &&
                RoughCut.Desktop.TimelineTransport.Seek(120, 20, 0, duration) == 0,
                "Scrubbing the transport did not map the press onto the timeline.");

            // Then the window, over real media: the transport replaces the player's source-relative bar.
            var source = Path.Combine(root, "export source.mkv");
            var info = await new MediaReader(RoughCut.Application.ToolSettings.Default.Ffmpeg,
                RoughCut.Application.ToolSettings.Default.Ffprobe).InspectAsync(source);
            var quarter = info.DurationTicks / 4;
            var projectPath = Path.Combine(root, "desktop-transport-project.json");
            await new ProjectStore().SaveAsync(projectPath, new EditProject
            {
                ProjectId = "desktop-transport",
                TimeBase = info.TimeBase,
                Assets = [new("source", "video", "export source.mkv", info.Sha256, info.DurationTicks,
                    info.Width, info.Height, "video/x-matroska")],
                Timeline = [new("second", "source", quarter, quarter * 2), new("fourth", "source", quarter * 3, quarter * 4)]
            }, 0);
            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            await session.InitializePreviewAsync();
            var app = new RoughCut.Desktop.RoughCutReviewApp(session, new RoughCut.Desktop.DesktopPlaybackController());
            using var document = app.CreateDocument();
            document.Refresh();
            var model = (RoughCut.Desktop.ReviewModel)app.Model;
            Assert(model.TransportClass == "" && model.TransportDuration == "0:02.000" &&
                model.TransportPosition == "0:00.000" && model.TransportLabel == "Play",
                $"The transport did not show the timeline: {model.TransportPosition} / {model.TransportDuration}");
            var dump = document.DebugDump(1280, 800);
            Assert(dump.Contains("transport-track", StringComparison.Ordinal) &&
                !dump.Contains("cupri-video-bar", StringComparison.Ordinal),
                "The window kept the player's own source-relative control bar.");

            // The transport adds a row inside the preview card, which pushed the cards below it off the
            // window until the picture's floor came down. Measure it rather than trusting the stylesheet.
            using var tree = System.Text.Json.JsonDocument.Parse(dump);
            var transport = FindBox(tree.RootElement.GetProperty("tree"), "transport-track");
            var evidence = FindBox(tree.RootElement.GetProperty("tree"), "evidence-card");
            var preview = FindBox(tree.RootElement.GetProperty("tree"), "preview-card");
            Assert(transport[2] > 200 && transport[0] >= preview[0] - 0.5f &&
                transport[0] + transport[2] <= preview[0] + preview[2] + 0.5f,
                $"The scrub track does not sit inside the preview card: x={transport[0]:0.#} w={transport[2]:0.#}");
            Assert(evidence[1] + evidence[3] <= 800.5f,
                $"The stage column overflows the window: the evidence card ends at {evidence[1] + evidence[3]:0.#} of 800.");
        });

        await check("Review panel controls stay inside their panel", async () =>
        {
            var projectPath = Path.Combine(root, "desktop-timeline-project.json");
            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            await session.InitializePreviewAsync();
            var app = new RoughCut.Desktop.RoughCutReviewApp(session);
            using var document = app.CreateDocument();
            document.Refresh();
            using var dump = System.Text.Json.JsonDocument.Parse(document.DebugDump(1280, 800));

            // CupriFace text fields keep an intrinsic width and overflow a narrower cell rather than shrink,
            // which silently pushed crop fields and the rename button outside the panel until measured.
            var panel = FindNodeCore(dump.RootElement.GetProperty("tree"), "review-panel");
            Assert(panel.ValueKind != System.Text.Json.JsonValueKind.Undefined, "The review panel was not rendered.");
            var bounds = Box(panel);
            var overflowing = new List<string>();
            Inspect(panel);
            void Inspect(System.Text.Json.JsonElement node)
            {
                if (node.TryGetProperty("box", out _))
                {
                    var box = Box(node);
                    if (box[0] < bounds[0] - 0.5f || box[0] + box[2] > bounds[0] + bounds[2] + 0.5f)
                        overflowing.Add($"{Describe(node)} x={box[0]:0.#} right={box[0] + box[2]:0.#}");
                }
                if (node.TryGetProperty("children", out var children))
                    foreach (var child in children.EnumerateArray()) Inspect(child);
            }
            Assert(overflowing.Count == 0,
                $"Review panel content escaped its bounds ({bounds[0]:0.#}–{bounds[0] + bounds[2]:0.#}): " +
                string.Join("; ", overflowing.Take(6)));
        });

        await check("Timeline playback approximates cuts and reordering over one source copy", () =>
        {
            // Output order [1s,2s) then [3s,4s): a removed middle and a source that plays out of order.
            var project = new EditProject
            {
                ProjectId = "approximation",
                TimeBase = new(1, 1000),
                Assets = [new("source", "video", "source.mkv", new string('a', 64), 4000, 160, 96, "video/x-matroska")],
                Timeline = [new("second", "source", 1000, 2000), new("fourth", "source", 3000, 4000)]
            };
            var map = RoughCut.Core.ProjectValidator.MapTimeline(project);
            var timeBase = project.TimeBase;
            Assert(RoughCut.Desktop.TimelinePlayback.CanApproximate(project), "A single-source timeline was refused.");

            // Output second 0 is source second 1; output 1.5 is source 3.5 in the second clip.
            var start = RoughCut.Desktop.TimelinePlayback.Locate(map, timeBase, 0);
            Assert(start.ClipIndex == 0 && Math.Abs(start.SeekSeconds!.Value - 1.0) < 1e-6,
                "The timeline start did not map to its source position.");
            var later = RoughCut.Desktop.TimelinePlayback.Locate(map, timeBase, 1.5);
            Assert(later.ClipIndex == 1 && Math.Abs(later.SeekSeconds!.Value - 3.5) < 1e-6,
                "A later output time did not map into the second clip.");

            // Inside the first clip nothing happens; reaching its end jumps to the next clip's source start.
            Assert(RoughCut.Desktop.TimelinePlayback.Advance(map, timeBase, 0, 1.5) is { SeekSeconds: null, Ended: false, ClipIndex: 0 },
                "Playback jumped while still inside a clip.");
            var jump = RoughCut.Desktop.TimelinePlayback.Advance(map, timeBase, 0, 2.0);
            Assert(jump.ClipIndex == 1 && jump.SeekSeconds is { } target && Math.Abs(target - 3.0) < 1e-6 && !jump.Ended,
                "Reaching a clip boundary did not skip the removed material.");
            Assert(RoughCut.Desktop.TimelinePlayback.Advance(map, timeBase, 1, 4.0).Ended,
                "The end of the last clip did not stop playback.");
            Assert(Math.Abs(RoughCut.Desktop.TimelinePlayback.OutputSeconds(map, timeBase, 1, 3.25) - 1.25) < 1e-6,
                "A source position did not report its output time.");

            // Scrubbing the player's own bar lands anywhere in the source. Landing inside another clip
            // adopts that clip rather than marching forward, so seeking backwards is not undone.
            var scrubbedBack = RoughCut.Desktop.TimelinePlayback.Advance(map, timeBase, 1, 1.25);
            Assert(scrubbedBack.ClipIndex == 0 && scrubbedBack.SeekSeconds is null && !scrubbedBack.Ended,
                "Scrubbing back into an earlier clip was dragged forward again.");
            var scrubbedToCut = RoughCut.Desktop.TimelinePlayback.Advance(map, timeBase, 1, 2.5);
            Assert(scrubbedToCut.ClipIndex == 1 && scrubbedToCut.SeekSeconds is { } resumed &&
                Math.Abs(resumed - 3.0) < 1e-6, "Scrubbing into removed material did not move to retained material.");

            // Contiguous clips need no seek, so an untouched timeline plays straight through.
            var whole = new EditProject
            {
                ProjectId = "contiguous",
                TimeBase = new(1, 1000),
                Assets = [new("source", "video", "source.mkv", new string('a', 64), 4000, 160, 96, "video/x-matroska")],
                Timeline = [new("a", "source", 0, 2000), new("b", "source", 2000, 4000)]
            };
            var joined = RoughCut.Core.ProjectValidator.MapTimeline(whole);
            Assert(RoughCut.Desktop.TimelinePlayback.Advance(joined, timeBase, 0, 2.0) is { SeekSeconds: null, ClipIndex: 1 },
                "Contiguous clips forced an unnecessary seek.");

            // An image-only or multi-source timeline cannot be approximated from one video copy.
            Assert(!RoughCut.Desktop.TimelinePlayback.CanApproximate(project with
            {
                Assets = [.. project.Assets, new("other", "video", "other.mkv", new string('b', 64), 4000, 160, 96, "video/x-matroska")],
                Timeline = [.. project.Timeline, new("extra", "other", 0, 1000)]
            }), "A multi-source timeline was accepted for approximation.");
            return Task.CompletedTask;
        });

        await check("Desktop background work stays responsive and supersedes stale selections", async () =>
        {
            var coordinator = new RoughCut.Desktop.DesktopWorkCoordinator();
            var staleCompleted = false;
            var latestCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Assert(coordinator.StartLatest(token => Task.Delay(Timeout.InfiniteTimeSpan, token),
                _ => staleCompleted = true), "The initial selection was not accepted.");
            Assert(coordinator.StartLatest(_ => Task.CompletedTask, exception =>
            {
                if (exception is null) latestCompleted.TrySetResult();
                else latestCompleted.TrySetException(exception);
            }), "The replacement selection was not accepted.");
            Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "Starting background frame work blocked the caller.");
            await latestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            Assert(!staleCompleted, "A canceled frame request replaced the newer selection.");

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var commandCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert(coordinator.StartCommand(() => release.Task, exception =>
            {
                if (exception is null) commandCompleted.TrySetResult();
                else commandCompleted.TrySetException(exception);
            }), "The edit command was not accepted.");
            Assert(!coordinator.StartCommand(() => Task.CompletedTask, _ => { }),
                "Concurrent revision writes were not rejected.");
            release.TrySetResult();
            await commandCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });

        await check("Desktop crop dragging clamps moves and corner resizing", () =>
        {
            var start = new Crop(20, 10, 100, 60);
            Assert(RoughCut.Desktop.CropDragGeometry.Update(start, RoughCut.Desktop.CropDragMode.Move,
                    500, -500, 160, 96) == new Crop(60, 0, 100, 60),
                "Moving the crop did not retain its size at the source boundary.");
            Assert(RoughCut.Desktop.CropDragGeometry.Update(start, RoughCut.Desktop.CropDragMode.NorthWest,
                    -500, 500, 160, 96) == new Crop(0, 69, 120, 1),
                "North-west resizing did not clamp to a valid crop.");
            Assert(RoughCut.Desktop.CropDragGeometry.Update(start, RoughCut.Desktop.CropDragMode.NorthEast,
                    500, -500, 160, 96) == new Crop(20, 0, 140, 70),
                "North-east resizing did not clamp to the frame.");
            Assert(RoughCut.Desktop.CropDragGeometry.Update(start, RoughCut.Desktop.CropDragMode.SouthWest,
                    500, 500, 160, 96) == new Crop(119, 10, 1, 86),
                "South-west resizing did not retain a positive width.");
            Assert(RoughCut.Desktop.CropDragGeometry.Update(start, RoughCut.Desktop.CropDragMode.SouthEast,
                    500, 500, 160, 96) == new Crop(20, 10, 140, 86),
                "South-east resizing did not clamp to the frame.");
            return Task.CompletedTask;
        });

        await check("A cropped clip plays cropped, without stretching the picture", async () =>
        {
            // One scale drives both boxes, so the visible box takes the crop's shape and the picture
            // inside it keeps the source's. Preview 800x400, source 160x96, crop 80x48 at (8,4).
            var crop = new Crop(8, 4, 80, 48);
            var paused = RoughCut.Desktop.PreviewCropGeometry.ForCrop(800, 400, 160, 96, crop, playing: false);
            Assert(paused.ContainerStyle == "left:66.67px;top:0px;width:666.67px;height:400px" &&
                paused.VideoStyle == "left:0;top:0;width:100%;height:100%" && paused.Fit == "contain",
                $"A paused cropped clip did not letterbox its exact frame: {paused.ContainerStyle} / {paused.VideoStyle}");
            var playing = RoughCut.Desktop.PreviewCropGeometry.ForCrop(800, 400, 160, 96, crop, playing: true);
            Assert(playing.ContainerStyle == paused.ContainerStyle &&
                playing.VideoStyle == "left:-66.67px;top:-33.33px;width:1333.33px;height:800px" &&
                playing.Fit == "fill",
                $"A playing cropped clip did not enlarge and offset the source: {playing.VideoStyle}");
            // The enlarged picture keeps the source aspect ratio, which is what stops it stretching.
            Assert(Math.Abs(1333.33 / 800 - 160 / 96d) < 0.01, "The enlarged picture would distort the source.");

            const string whole = "left:0;top:0;width:100%;height:100%";
            Assert(RoughCut.Desktop.PreviewCropGeometry.ForCrop(800, 400, 160, 96, null, true).ContainerStyle == whole &&
                RoughCut.Desktop.PreviewCropGeometry.ForCrop(0, 0, 160, 96, crop, true).ContainerStyle == whole &&
                RoughCut.Desktop.PreviewCropGeometry.ForCrop(800, 400, 160, 96, new(8, 4, 200, 48), true).ContainerStyle == whole &&
                RoughCut.Desktop.PreviewCropGeometry.ForCrop(800, 400, 0, 0, crop, true).ContainerStyle == whole,
                "An uncroppable or unmeasured preview did not fall back to the whole frame.");

            // In the window, the measured box takes the crop's shape once the view has been laid out.
            var projectPath = Path.Combine(root, "desktop-preview-project.json");
            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            await session.InitializePreviewAsync();
            var clip = session.Project.Timeline.Single();
            var active = clip.Crop ?? throw new Exception("Expected the prior crop edit.");
            var app = new RoughCut.Desktop.RoughCutReviewApp(session);
            using var document = app.CreateDocument();
            document.Refresh();
            var model = (RoughCut.Desktop.ReviewModel)app.Model;
            Assert(model.PlaybackCropStyle == whole, "The crop was applied before the view had been measured.");
            // The measurement reads the laid-out document, so a frame has to have been laid out first.
            document.DebugDump(1280, 800);
            app.Present(1280, 800);
            var shaped = model.PlaybackCropStyle;
            Assert(shaped != whole && model.PlaybackFit == "contain",
                $"The measured preview did not take the crop's shape: {shaped}");
            using var debug = System.Text.Json.JsonDocument.Parse(document.DebugDump(1280, 800));
            var box = FindBox(debug.RootElement.GetProperty("tree"), "preview-crop");
            var preview = FindBox(debug.RootElement.GetProperty("tree"), "preview");
            Assert(Math.Abs(box[2] / box[3] - active.Width / (double)active.Height) < 0.01,
                $"The rendered crop box is not the crop's shape: {box[2]:0.#}x{box[3]:0.#} for {active.Width}x{active.Height}.");
            Assert(box[0] >= preview[0] - 0.5f && box[1] >= preview[1] - 0.5f &&
                box[0] + box[2] <= preview[0] + preview[2] + 0.5f && box[1] + box[3] <= preview[1] + preview[3] + 0.5f,
                "The crop box does not sit inside the preview area.");
        });

        await check("Clip boundary geometry moves one edge and stays inside the source", () =>
        {
            const long source = 4000;
            var minimum = RoughCut.Desktop.ClipDragGeometry.MinimumTicks(new(1, 1000));
            Assert(minimum == 20, "The shortest draggable clip should be twenty milliseconds at a 1/1000 time base.");
            Assert(RoughCut.Desktop.ClipDragGeometry.MinimumTicks(new(1, 25)) == 1,
                "A coarse time base should fall back to one tick.");

            Assert(RoughCut.Desktop.ClipDragGeometry.Update(1000, 3000, RoughCut.Desktop.ClipEdge.Start,
                    250, source, minimum) == (1250L, 3000L),
                "Dragging the start did not move only the in point.");
            Assert(RoughCut.Desktop.ClipDragGeometry.Update(1000, 3000, RoughCut.Desktop.ClipEdge.End,
                    -250.4, source, minimum) == (1000L, 2750L),
                "Dragging the end did not move only the out point.");

            // Neither edge may pass the other, leave the source, or produce a clip too short to see.
            Assert(RoughCut.Desktop.ClipDragGeometry.Update(1000, 3000, RoughCut.Desktop.ClipEdge.Start,
                    -5000, source, minimum) == (0L, 3000L) &&
                RoughCut.Desktop.ClipDragGeometry.Update(1000, 3000, RoughCut.Desktop.ClipEdge.Start,
                    5000, source, minimum) == (2980L, 3000L) &&
                RoughCut.Desktop.ClipDragGeometry.Update(1000, 3000, RoughCut.Desktop.ClipEdge.End,
                    5000, source, minimum) == (1000L, 4000L) &&
                RoughCut.Desktop.ClipDragGeometry.Update(1000, 3000, RoughCut.Desktop.ClipEdge.End,
                    -5000, source, minimum) == (1000L, 1020L),
                "A boundary drag escaped the source or inverted the clip.");
            Assert(RoughCut.Desktop.ClipDragGeometry.Update(1000, 3000, RoughCut.Desktop.ClipEdge.End,
                    double.NaN, source, minimum) == (1000L, 3000L),
                "An unmeasurable drag changed the clip.");
            Throws<ArgumentOutOfRangeException>(() => RoughCut.Desktop.ClipDragGeometry.Update(
                3000, 1000, RoughCut.Desktop.ClipEdge.End, 10, source, minimum));
            Throws<ArgumentOutOfRangeException>(() => RoughCut.Desktop.ClipDragGeometry.Update(
                0, 5000, RoughCut.Desktop.ClipEdge.End, 10, source, minimum));
            return Task.CompletedTask;
        });

        await check("Dragging a clip boundary persists one revisioned trim", async () =>
        {
            var source = Path.Combine(root, "export source.mkv");
            var info = await new MediaReader(RoughCut.Application.ToolSettings.Default.Ffmpeg,
                RoughCut.Application.ToolSettings.Default.Ffprobe).InspectAsync(source);
            var half = info.DurationTicks / 2;
            var projectPath = Path.Combine(root, "desktop-boundary-project.json");
            await new ProjectStore().SaveAsync(projectPath, new EditProject
            {
                ProjectId = "desktop-boundary",
                TimeBase = info.TimeBase,
                Assets = [new("source", "video", "export source.mkv", info.Sha256, info.DurationTicks,
                    info.Width, info.Height, "video/x-matroska")],
                // A long clip and a short one, so the row's shares can be told apart.
                Timeline = [new("long", "source", 0, half), new("short", "source", half, half + half / 4)]
            }, 0);
            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            await session.InitializePreviewAsync();
            using var document = new RoughCut.Desktop.RoughCutReviewApp(session).CreateDocument();
            document.Refresh();
            using var debug = System.Text.Json.JsonDocument.Parse(document.DebugDump(1280, 800));
            var longBox = FindBox(debug.RootElement.GetProperty("tree"), "clip selected");
            var shortBox = FindBox(debug.RootElement.GetProperty("tree"), "clip ");
            Assert(longBox[2] > shortBox[2] + 20,
                $"Clips do not take their share of the timeline: {longBox[2]:0.#} against {shortBox[2]:0.#}.");

            // Drag the long clip's end boundary left by a tenth of its width: a tenth of its duration.
            var edge = FindBox(debug.RootElement.GetProperty("tree"), "clip-edge end");
            var x = edge[0] + edge[2] / 2;
            var y = edge[1] + edge[3] / 2;
            var moved = longBox[2] / 10;
            var revision = session.Project.Revision;
            Assert(document.DispatchPointer(1, CupriFace.Interaction.PointerPhase.Down, x, y),
                $"The clip boundary did not capture the pointer; its handle measured {edge[2]:0.#}x{edge[3]:0.#}.");
            Assert(document.DispatchPointer(1, CupriFace.Interaction.PointerPhase.Move, x - moved, y) &&
                document.DispatchPointer(1, CupriFace.Interaction.PointerPhase.Up, x - moved, y),
                "The captured boundary drag was not handled through release.");
            var expected = half - (long)Math.Round(half / 10.0, MidpointRounding.AwayFromZero);
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (session.Project.Revision == revision && DateTime.UtcNow < timeout) await Task.Delay(25);
            var saved = await new ProjectStore().LoadAsync(projectPath);
            Assert(saved.Revision == revision + 1 && saved.Timeline[0].In == 0 &&
                Math.Abs(saved.Timeline[0].Out - expected) <= 2 && saved.Timeline[1].In == half,
                $"The drag did not trim only the dragged edge: {saved.Timeline[0].Out} against {expected}.");

            await session.UndoAsync();
            Assert(session.Project.Timeline[0].Out == half && !session.CanUndo,
                "Undo did not restore the boundary the drag moved.");
        });

        await check("Desktop crop handle drag persists one revisioned edit", async () =>
        {
            var projectPath = Path.Combine(root, "desktop-preview-project.json");
            var session = await RoughCut.Desktop.DesktopReviewSession.LoadAsync(projectPath);
            await session.InitializePreviewAsync();
            var revision = session.Project.Revision;
            var start = session.Project.Timeline.Single().Crop ?? throw new Exception("Expected the prior crop edit.");
            using var document = new RoughCut.Desktop.RoughCutReviewApp(session).CreateDocument();
            document.Refresh();
            using var debug = System.Text.Json.JsonDocument.Parse(document.DebugDump(1280, 800));
            var box = FindBox(debug.RootElement.GetProperty("tree"), "crop-handle se");
            var x = box[0] + box[2] / 2;
            var y = box[1] + box[3] / 2;
            Assert(document.DispatchPointer(1, CupriFace.Interaction.PointerPhase.Down, x, y),
                "The south-east crop handle did not capture the pointer.");
            Assert(document.DispatchPointer(1, CupriFace.Interaction.PointerPhase.Move, x - 15, y - 8) &&
                document.DispatchPointer(1, CupriFace.Interaction.PointerPhase.Up, x - 15, y - 8),
                "The captured crop drag was not handled through release.");
            var expected = new Crop(start.X, start.Y, start.Width - 10, start.Height - 5);
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (session.Preview?.Info.Crop != expected && DateTime.UtcNow < timeout) await Task.Delay(25);
            var saved = await new ProjectStore().LoadAsync(projectPath);
            Assert(saved.Revision == revision + 1 && saved.Timeline.Single().Crop == expected &&
                session.Preview?.Info.Crop == expected && session.CanUndo,
                "The crop handle did not persist exactly one revisioned edit and refresh its preview.");
        });

        await check("Desktop playback proxy decodes with bounded audio drift", async () =>
        {
            var projectPath = Path.Combine(root, "desktop-preview-project.json");
            var arguments = new[] { "probe-playback", projectPath, "1.0" };
            var result = desktop.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? await ToolProcess.RunAsync("dotnet", new[] { desktop }.Concat(arguments), timeout: TimeSpan.FromMinutes(3))
                : await ToolProcess.RunAsync(desktop, arguments, timeout: TimeSpan.FromMinutes(3));
            var output = System.Text.Encoding.UTF8.GetString(result.Output);
            Assert(output.Contains("playback ok:", StringComparison.Ordinal) &&
                output.Contains("0 underruns", StringComparison.Ordinal),
                "Desktop playback did not report decoded synchronized video and audio.");
        });
    }

    // "#32770" is the Windows dialog window class; the probe drives a real dialog rather than mocking it.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static nint FindDialog(string title) => FindWindowW("#32770", title);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CloseDialog(nint dialog) => PostMessageW(dialog, 0x0010, 0, 0);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowW(string? className, string? windowName);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    private static float[] Box(System.Text.Json.JsonElement node) =>
        node.GetProperty("box").EnumerateArray().Select(item => item.GetSingle()).ToArray();

    private static string Describe(System.Text.Json.JsonElement node) =>
        node.TryGetProperty("class", out var css) && css.GetString() is { Length: > 0 } name ? "." + name
            : node.TryGetProperty("tag", out var tag) ? tag.GetString() ?? "?" : "?";

    private static System.Text.Json.JsonElement FindNodeCore(System.Text.Json.JsonElement node, string cssClass)
    {
        if (node.TryGetProperty("class", out var css) && css.GetString() == cssClass) return node;
        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
            {
                var found = FindNodeCore(child, cssClass);
                if (found.ValueKind != System.Text.Json.JsonValueKind.Undefined) return found;
            }
        return default;
    }

    private static float[] FindBox(System.Text.Json.JsonElement node, string cssClass)
    {
        if (node.TryGetProperty("class", out var className) && className.GetString() == cssClass)
            return node.GetProperty("box").EnumerateArray().Select(item => item.GetSingle()).ToArray();
        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
                try { return FindBox(child, cssClass); }
                catch (KeyNotFoundException) { }
        throw new KeyNotFoundException($"Rendered node with class '{cssClass}' was not found.");
    }
}
