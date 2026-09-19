using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoughCut.Core;
using RoughCut.Media;

internal static class ExportTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string[] args, string root)
    {
        var project = new EditProject
        {
            ProjectId = "export-fixture",
            TimeBase = new(1, 1000),
            Assets = [new("source-1", "video", "export source.mkv", new string('a', 64), 4000, 160, 96, "video/x-matroska")],
            Timeline = [new("clip-1", "source-1", 0, 4000)]
        };
        EditOperation[] operations =
        [
            new("split", "clip-1", At: 1000, NewClipId: "middle"),
            new("split", "middle", At: 2000, NewClipId: "later"),
            new("remove", "middle"),
            new("trim", "later", In: 2000, Out: 3000),
            new("reorder", Order: ["later", "clip-1"]),
            new("export-mode", Mode: "copy-only")
        ];
        const string srt = "1\n00:00:00,500 --> 00:00:01,500\nCrosses a removal\n\n2\n00:00:01,100 --> 00:00:01,800\nRemoved text\n\n3\n00:00:02,000 --> 00:00:02,700\nLater text\n";
        await check("Edit batch applies split/remove/trim/reorder atomically", async () =>
        {
            var edited = TimelineEditor.Apply(project, operations);
            Assert(edited.Revision == 2 && edited.Timeline.Length == 2 && edited.Timeline[0].In == 2000 && edited.Timeline[0].Out == 3000, "Edit mapping incorrect.");
            Assert(project.Timeline.Length == 1 && project.Timeline[0].Out == 4000, "Original project mutated.");
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(project, [new("remove", "clip-1"), new("trim", "missing", In: 0, Out: 1)])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(project, [new("reorder", Order: ["missing"])])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(project, [new("remove", "clip-1", Mode: "copy-only")])));
            Assert(project.Timeline.Length == 1, "Failed batch mutated the project.");
        });
        await check("SRT import retains provenance; cuts split, drop and reorder cues", async () =>
        {
            var track = new CaptionTrack("source-1", "captions.srt", new string('b', 64), new(1, 1000), Captions.ParseSrt(srt));
            var edited = TimelineEditor.Apply(project with { Captions = track }, operations);
            var output = Captions.Retime(edited);
            Assert(output.Length == 2 && output[0].Text == "Later text" && output[1].Text == "Crosses a removal", "Caption removal/reorder failed.");
            Assert(output[0].Start.CompareTo(new(0, new(1, 1000))) == 0 && output[1].Start.CompareTo(new(1500, new(1, 1000))) == 0 &&
                output[1].End.CompareTo(new(2000, new(1, 1000))) == 0, "Caption clipping/retiming incorrect.");
            var split = TimelineEditor.Apply(project with { Captions = track }, [new("split", "clip-1", At: 1000, NewClipId: "rest")]);
            Assert(Captions.Retime(split).Count(c => c.CueId == "cue-1") == 2, "Crossing cue was not split.");
            var roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(edited, ProjectJson.Default.EditProject), ProjectJson.Default.EditProject)!;
            Assert(roundTrip.Captions!.SourceSha256 == track.SourceSha256 && Captions.WriteSrt(output).Contains("00:00:01,500 --> 00:00:02,000"), "Caption provenance/serialization failed.");
            await Throws<InvalidDataException>(() => Task.FromResult(Captions.ParseSrt("1\n00:00:02,000 --> 00:00:01,000\nInvalid")));
            await Throws<InvalidDataException>(() => Task.FromResult(Captions.ParseSrt("1\n00:61:00,000 --> 01:00:00,000\nInvalid")));
        });
        await check("Caption arithmetic preserves submillisecond timing", () =>
        {
            var precise = project with
            {
                TimeBase = new(1, 30000),
                Assets = [project.Assets[0] with { Duration = 120000 }],
                Timeline = [new("cut", "source-1", 1001, 30030)],
                Captions = new("source-1", "captions.srt", new string('b', 64), new(1, 1000), [new("cue", 0, 1000, "text")])
            };
            var output = Captions.Retime(precise);
            Assert(output[0].End.CompareTo(new(28999, new(1, 30000))) == 0, "Rational caption timing lost.");
            Assert(Captions.WriteSrt(output).Contains("00:00:00,000 --> 00:00:00,967"), "SRT rounding policy incorrect.");
            return Task.CompletedTask;
        });
        if (!args.Contains("--media", StringComparer.Ordinal)) return;
        var ffmpeg = Environment.GetEnvironmentVariable("ROUGHCUT_FFMPEG") ?? "ffmpeg";
        var ffprobe = Environment.GetEnvironmentVariable("ROUGHCUT_FFPROBE") ?? "ffprobe";
        var source = Path.Combine(root, "export source.mkv");
        var captionsPath = Path.Combine(root, "captions.srt");
        var projectPath = Path.Combine(root, "export-project.json");
        var store = new ProjectStore();
        var planner = new ExportPlanner(ffmpeg, ffprobe);
        var exporter = new ExportEngine(ffmpeg, ffprobe);
        await check("Generate identified PNG/PCM export fixture", async () =>
        {
            // Six visible binary bars identify every frame without an external font dependency.
            var identifier = "drawbox=x=0:y=0:w=96:h=12:color=black:t=fill";
            for (int bit = 0; bit < 6; bit++)
                identifier += FormattableString.Invariant($",drawbox=x={bit * 16}:y=0:w=12:h=12:color=white:t=fill:enable='mod(floor(n/{1 << bit}),2)'");
            await ToolProcess.RunAsync(ffmpeg,
                ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=10:duration=4",
                 "-f", "lavfi", "-i", "aevalsrc=0.2*sin(2*PI*(200*t+30*t*t)):s=48000:d=4", "-map", "0:v:0", "-map", "1:a:0",
                 "-vf", identifier,
                 "-c:v", "png", "-pix_fmt", "rgb24", "-threads:v", "1", "-af", "asetnsamples=n=4800:p=0", "-c:a", "pcm_s16le", "-n", source]);
            await File.WriteAllTextAsync(captionsPath, srt, new UTF8Encoding(false));
            project = project with
            {
                Assets = [project.Assets[0] with { Sha256 = await MediaReader.FingerprintAsync(source) }],
                Captions = new("source-1", "captions.srt", await MediaReader.FingerprintAsync(captionsPath), new(1, 1000), Captions.ParseSrt(srt))
            };
            await store.SaveAsync(projectPath, project, 0);
            project = TimelineEditor.Apply(project, operations);
            await store.SaveAsync(projectPath, project, 1);
        });
        await check("Strict copy preflight proves packet-aligned independent streams", async () =>
        {
            var plan = await planner.PreflightAsync(project, projectPath);
            Assert(plan.Supported, string.Join("; ", plan.Issues.Select(i => i.Message)));
            Assert(!plan.RequiresEncoding && plan.Streams.All(s => s.Action == "copy") && plan.Clips[0].FirstFrame == 20 &&
                plan.Clips[0].FirstSample == 96000 && plan.Clips[0].RequestedIn == plan.Clips[0].ResolvedIn, "Copy decisions or mapping incorrect.");
        });
        await check("Strict export preserves payloads, decoded frames, PCM and retimed captions", async () =>
        {
            var before = await MediaReader.FingerprintAsync(source);
            var destination = Path.Combine(root, "copy result");
            var report = await exporter.ExportAsync(projectPath, destination);
            Assert(report.Validation.DecodedFrames == 20 && report.Validation.AudioSamples == 96000 && report.Validation.Joins == 1 &&
                report.Validation.PacketPayloadsMatch && report.Validation.MaximumAudioTimestampErrorMicroseconds == 0, "Copy validation evidence incorrect.");
            Assert(report.Captions.Length == 2 && report.Plan.Clips[0].OutputOut == 1000, "Caption or edit report incorrect.");
            Assert(await MediaReader.FingerprintAsync(source) == before, "Source was modified.");
            Assert(Directory.EnumerateFiles(destination).Count() == 3 && File.Exists(Path.Combine(destination, "captions.srt")), "Export bundle incomplete or contains intermediate files.");
            var restored = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(destination, "export.json")), ProjectJson.Default.ExportReport)!;
            Assert(restored.OutputSha256 == await MediaReader.FingerprintAsync(Path.Combine(destination, "video.mkv")), "Export report hash incorrect.");
            // Independent fixture oracle: decode visible binary frame IDs across the join.
            var pixels = await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-i", Path.Combine(destination, "video.mkv"),
                "-map", "0:v:0", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
            for (int frame = 0; frame < 20; frame++)
            {
                int identifier = 0;
                for (int bit = 0; bit < 6; bit++)
                {
                    int pixel = frame * 160 * 96 * 3 + (4 * 160 + bit * 16 + 4) * 3;
                    if (pixels.Output[pixel] > 200) identifier |= 1 << bit;
                }
                Assert(identifier == (frame < 10 ? frame + 20 : frame - 10), "Independent frame-ID oracle found wrong order or content at the join.");
            }
            await Throws<IOException>(() => exporter.ExportAsync(projectPath, destination));
        });
        await check("Crop requires encoding and strict mode rejects it", async () =>
        {
            var cropped = project with { Timeline = project.Timeline.Select(c => c with { Crop = new(8, 4, 80, 48) }).ToArray() };
            var strict = await planner.PreflightAsync(cropped, projectPath);
            Assert(!strict.Supported && strict.Issues.Any(i => i.Code == "unsupported-copy-only"), "Copy-only crop was allowed.");
            cropped = cropped with { ProjectId = "crop-fixture", Revision = 1, ExportMode = "prefer-stream-copy" };
            var path = Path.Combine(root, "crop-project.json");
            await store.SaveAsync(path, cropped, 0);
            var plan = await planner.PreflightAsync(cropped, path);
            Assert(plan.Supported && plan.Streams[0].Action == "encode" && plan.Streams[1].Action == "copy", "Crop did not preserve copyable audio.");
            var destination = Path.Combine(root, "crop result");
            await Throws<ExportRejectedException>(() => exporter.ExportAsync(path, destination));
            Assert(!Directory.Exists(destination), "Unapproved encoding created output.");
            var report = await exporter.ExportAsync(path, destination, allowEncoding: true);
            Assert(report.Plan.Width == 80 && report.Plan.Height == 48 && report.Validation.VideoContentMatches && report.Validation.AudioContentMatches, "Crop export failed validation.");
        });
        await check("Unaligned cuts, active voice replacements and image clips are rejected", async () =>
        {
            var unaligned = project with { Timeline = [new("bad", "source-1", 50, 1000)] };
            Assert(!(await planner.PreflightAsync(unaligned, projectPath)).Supported, "Unaligned cut accepted.");
            var voices = project with
            {
                Speakers = [new("s", "Speaker")],
                Speech = [new("s1", "source-1", 0, 1000, "text", ["s"], "corrected")],
                Voices = [new("v", "s", "qwen-tts", "voice")],
                Replacements = [new("r", "s1", "v", "text")]
            };
            Assert((await planner.PreflightAsync(voices, projectPath)).Issues[0].Code == "unsupported-voice-replacement", "Voice rendering silently omitted.");
            var image = project with
            {
                Assets = [.. project.Assets, new("image", "image", "image.png", new string('c', 64), 0, 160, 96, "image/png")],
                Timeline = [new("still", "image", 0, 1000, Audio: "silence")]
            };
            Assert(!(await planner.PreflightAsync(image, projectPath)).Supported, "Image rendering silently omitted.");
            Assert(!(await planner.PreflightAsync(project with { Timeline = [project.Timeline[0] with { Crop = new(0, 0, 80, 48) }, project.Timeline[1]] }, projectPath)).Supported,
                "Inconsistent crop dimensions accepted.");
        });
        await check("H.264 copy is rejected while explicit encoding validates B-frame cuts", async () =>
        {
            var h264Path = Path.Combine(root, "h264.mkv");
            await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-i", source, "-map", "0:v:0", "-map", "0:a:0",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "40", "-bf", "3", "-threads:v", "1", "-c:a", "copy", "-n", h264Path]);
            var h264 = project with { ProjectId = "h264", Revision = 1, Assets = [project.Assets[0] with { Path = "h264.mkv", Sha256 = await MediaReader.FingerprintAsync(h264Path) }] };
            var strict = await planner.PreflightAsync(h264, projectPath);
            Assert(!strict.Supported && strict.Issues.Any(i => i.Code == "unsupported-copy-only"), "H.264 copy was inferred safe from flags.");
            h264 = h264 with { ExportMode = "exact-edit" };
            var path = Path.Combine(root, "h264-project.json");
            await store.SaveAsync(path, h264, 0);
            var report = await exporter.ExportAsync(path, Path.Combine(root, "h264 result"), allowEncoding: true);
            Assert(report.Plan.Streams[0].Action == "encode" && report.Validation.DecodedFrames == 20, "Encoded H.264 cut failed validation.");
        });
        await check("PCM cuts inside packets require encoding while PNG can stay copied", async () =>
        {
            var unalignedAudioPath = Path.Combine(root, "pcm-blocks.mkv");
            await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-i", source, "-map", "0:v:0", "-map", "0:a:0",
                "-c:v", "copy", "-af", "asetnsamples=n=1024:p=0", "-c:a", "pcm_s16le", "-n", unalignedAudioPath]);
            var pcm = project with { ProjectId = "pcm", Revision = 1, Assets = [project.Assets[0] with { Path = "pcm-blocks.mkv", Sha256 = await MediaReader.FingerprintAsync(unalignedAudioPath) }] };
            Assert(!(await planner.PreflightAsync(pcm, projectPath)).Supported, "Unaligned PCM copy allowed.");
            pcm = pcm with { ExportMode = "prefer-stream-copy" };
            var path = Path.Combine(root, "pcm-project.json");
            await store.SaveAsync(path, pcm, 0);
            var report = await exporter.ExportAsync(path, Path.Combine(root, "pcm result"), allowEncoding: true);
            Assert(report.Plan.Streams[0].Action == "copy" && report.Plan.Streams[1].Action == "encode" &&
                report.Validation.AudioSamples == 96000 && report.Validation.AudioContentMatches, "Per-stream copy/encode decisions or audio samples incorrect.");
        });
        await check("Changed sources/captions fail preflight and cancellation leaves no export", async () =>
        {
            var changed = project with { Assets = [project.Assets[0] with { Sha256 = new string('0', 64) }] };
            Assert((await planner.PreflightAsync(changed, projectPath)).Issues[0].Code == "source-changed", "Changed source accepted.");
            Assert((await planner.PreflightAsync(project with { Captions = project.Captions! with { SourceSha256 = new string('0', 64) } }, projectPath)).Issues[0].Code == "captions-changed", "Changed captions accepted.");
            var destination = Path.Combine(root, "cancelled result");
            await Throws<OperationCanceledException>(() => exporter.ExportAsync(projectPath, destination, cancellationToken: new(true)));
            Assert(!Directory.Exists(destination) && !Directory.EnumerateDirectories(root, ".roughcut-export-*").Any(), "Cancelled or failed export leaked artifacts.");
        });
        await check("Failed media tool and in-progress cancellation discard staged output", async () =>
        {
            var before = await MediaReader.FingerprintAsync(source);
            var failed = Path.Combine(root, "failed result");
            // FFprobe can inspect the input but cannot execute an FFmpeg export command.
            await Throws<MediaToolException>(() => new ExportEngine(ffprobe, ffprobe).ExportAsync(projectPath, failed));
            Assert(!Directory.Exists(failed) && !Directory.EnumerateDirectories(root, ".roughcut-export-*").Any(), "Tool failure left a published or staged output.");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var destination = Path.Combine(root, "mid-export cancellation");
            var running = exporter.ExportAsync(projectPath, destination, cancellationToken: cancellation.Token);
            while (!running.IsCompleted && !Directory.EnumerateDirectories(root, ".roughcut-export-*").Any())
                await Task.Delay(10);
            Assert(!running.IsCompleted, "Export completed before the cancellation check reached staging.");
            cancellation.Cancel();
            await Throws<OperationCanceledException>(() => running);
            Assert(!Directory.Exists(destination) && !Directory.EnumerateDirectories(root, ".roughcut-export-*").Any(), "In-progress cancellation left an output.");
            Assert(await MediaReader.FingerprintAsync(source) == before, "Failure or cancellation modified source media.");
        });
        var cliOption = Array.IndexOf(args, "--cli");
        if (cliOption >= 0)
        {
            var cli = Path.GetFullPath(args[cliOption + 1]);
            Task<ToolResult> RunCli(params string[] arguments) => cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? ToolProcess.RunAsync("dotnet", new[] { cli }.Concat(arguments)) : ToolProcess.RunAsync(cli, arguments);
            await check("CLI caption import, edit, preflight, export and stale-edit rejection", async () =>
            {
                var path = Path.Combine(root, "cli-export.json");
                await RunCli("create", source, path);
                await RunCli("captions", path, "source-1", captionsPath, "1");
                var edits = Path.Combine(root, "edits.json");
                await File.WriteAllTextAsync(edits, JsonSerializer.Serialize(operations, ProjectJson.Default.EditOperationArray));
                await RunCli("edit", path, edits, "2");
                var planResult = await RunCli("preflight", path);
                var plan = JsonSerializer.Deserialize(planResult.Output, ProjectJson.Default.ExportPlan)!;
                Assert(plan.Supported && plan.Revision == 3, "CLI preflight failed.");
                var destination = Path.Combine(root, "cli export result");
                var result = await RunCli("export", path, destination);
                var report = JsonSerializer.Deserialize(result.Output, ProjectJson.Default.ExportReport)!;
                Assert(report.Validation.DecodedFrames == 20 && report.Captions.Length == 2, "CLI export missing validated media/captions.");
                await Throws<MediaToolException>(() => RunCli("edit", path, edits, "2"));
                await Throws<MediaToolException>(() => RunCli("export", path, destination));
                await Throws<MediaToolException>(() => RunCli("preflight", Path.Combine(root, "missing-project.json")));
                Assert((await store.LoadAsync(path)).Revision == 3, "Failed edit changed revision.");
                EditOperation[] cropOperations = [new("crop", "later", Crop: new(8, 4, 80, 48)),
                    new("crop", "clip-1", Crop: new(8, 4, 80, 48)), new("export-mode", Mode: "prefer-stream-copy")];
                await File.WriteAllTextAsync(edits, JsonSerializer.Serialize(cropOperations, ProjectJson.Default.EditOperationArray));
                await RunCli("edit", path, edits, "3");
                var encodedDestination = Path.Combine(root, "cli encoded result");
                await Throws<MediaToolException>(() => RunCli("export", path, encodedDestination));
                Assert(!Directory.Exists(encodedDestination), "CLI encoded output without explicit permission.");
                var encoded = await RunCli("export", path, encodedDestination, "--allow-encode");
                var encodedReport = JsonSerializer.Deserialize(encoded.Output, ProjectJson.Default.ExportReport)!;
                Assert(encodedReport.Plan.Revision == 4 && encodedReport.Plan.Width == 80 && encodedReport.Plan.Streams[0].Action == "encode" &&
                    encodedReport.Validation.VideoContentMatches, "CLI crop/encoding policy failed.");
            });
        }
    }
}
