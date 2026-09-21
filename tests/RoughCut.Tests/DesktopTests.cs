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
