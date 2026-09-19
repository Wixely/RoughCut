using RoughCut.Core;
using RoughCut.Media;

internal static class DesktopTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
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
            var info = await new MediaReader().InspectAsync(source);
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
