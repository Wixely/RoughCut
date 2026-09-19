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
}
