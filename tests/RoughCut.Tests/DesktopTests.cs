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
    }
}
