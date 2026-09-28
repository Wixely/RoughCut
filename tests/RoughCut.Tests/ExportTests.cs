using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoughCut.Application;
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

    private static async Task<string> SaveAsync(ProjectStore store, string root, string name, EditProject project)
    {
        var path = Path.Combine(root, name);
        await store.SaveAsync(path, project, 0);
        return path;
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
        await check("Edit batch applies split/remove/trim/reorder atomically; set-range restores source material", async () =>
        {
            var edited = TimelineEditor.Apply(project, operations);
            Assert(edited.Revision == 2 && edited.Timeline.Length == 2 && edited.Timeline[0].In == 2000 && edited.Timeline[0].Out == 3000, "Edit mapping incorrect.");
            Assert(project.Timeline.Length == 1 && project.Timeline[0].Out == 4000, "Original project mutated.");
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(project, [new("remove", "clip-1"), new("trim", "missing", In: 0, Out: 1)])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(project, [new("reorder", Order: ["missing"])])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(project, [new("remove", "clip-1", Mode: "copy-only")])));
            Assert(project.Timeline.Length == 1, "Failed batch mutated the project.");
            var restored = TimelineEditor.Apply(edited, [new("set-range", "later", In: 1000, Out: 4000)]);
            Assert(restored.Timeline[0].In == 1000 && restored.Timeline[0].Out == 4000 && edited.Timeline[0].Out == 3000,
                "Set-range did not restore retained source material without mutating the prior revision.");
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(edited, [new("trim", "later", In: 1000, Out: 4000)])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(edited, [new("set-range", "later", In: 0, Out: 5000)])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(edited, [new("set-range", "later", In: 2000, Out: 2000)])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(edited, [new("set-range", "missing", In: 0, Out: 1)])));
        });
        await check("Insert-clip restores a removed clip exactly, and refuses anything else", async () =>
        {
            var full = project with
            {
                Assets = [.. project.Assets, new("still", "image", "still.png", new string('b', 64), 0, 160, 96, "image/png")],
                Timeline = [new("first", "source-1", 0, 1000), new("middle", "source-1", 1000, 2000, new(8, 4, 80, 48), "cover", "silence"),
                    new("last", "source-1", 2000, 3000)]
            };
            var removed = TimelineEditor.Apply(full, [new("remove", "middle")]);
            Assert(removed.Timeline.Select(clip => clip.Id).SequenceEqual(["first", "last"]), "Remove did not drop the clip.");
            var restored = TimelineEditor.Apply(removed, [new("insert-clip", "middle", In: 1000, Out: 2000,
                Crop: new(8, 4, 80, 48), AssetId: "source-1", BeforeClipId: "last", Fit: "cover", Audio: "silence")]);
            Assert(restored.Timeline.SequenceEqual(full.Timeline), "Insert-clip did not restore the exact clip in its place.");
            Assert(TimelineEditor.Apply(TimelineEditor.Apply(full, [new("remove", "last")]),
                [new("insert-clip", "last", In: 2000, Out: 3000, AssetId: "source-1")]).Timeline.Select(clip => clip.Id)
                .SequenceEqual(["first", "middle", "last"]), "Insert-clip did not append a clip removed from the end.");

            // Anything that is not an exact restoration of retained source material is refused.
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(removed,
                [new("insert-clip", "first", In: 0, Out: 1000, AssetId: "source-1")])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(removed,
                [new("insert-clip", "middle", In: 1000, Out: 9000, AssetId: "source-1")])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(removed,
                [new("insert-clip", "middle", In: 2000, Out: 2000, AssetId: "source-1")])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(removed,
                [new("insert-clip", "middle", In: 0, Out: 1000, AssetId: "still")])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(removed,
                [new("insert-clip", "middle", In: 0, Out: 1000, AssetId: "missing")])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(removed,
                [new("insert-clip", "middle", In: 0, Out: 1000, AssetId: "source-1", BeforeClipId: "absent")])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(removed,
                [new("insert-clip", "middle", In: 0, Out: 1000, AssetId: "source-1", Audio: "music")])));
            await Throws<ArgumentException>(() => Task.FromResult(TimelineEditor.Apply(removed,
                [new("insert-clip", "middle", In: 0, Out: 1000, AssetId: "source-1", At: 5)])));
            Assert(removed.Timeline.Length == 2, "A refused insertion changed the timeline.");
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
        var ffmpeg = RoughCut.Application.ToolSettings.Default.Ffmpeg;
        var ffprobe = RoughCut.Application.ToolSettings.Default.Ffprobe;
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
        await check("Unaligned cuts and image-only timelines are rejected", async () =>
        {
            var unaligned = project with { Timeline = [new("bad", "source-1", 50, 1000)] };
            Assert(!(await planner.PreflightAsync(unaligned, projectPath)).Supported, "Unaligned cut accepted.");
            var image = project with
            {
                Assets = [.. project.Assets, new("image", "image", "image.png", new string('c', 64), 0, 160, 96, "image/png")],
                Timeline = [new("still", "image", 0, 1000, Audio: "silence")]
            };
            Assert(!(await planner.PreflightAsync(image, projectPath)).Supported, "Image rendering silently omitted.");
            Assert(!(await planner.PreflightAsync(project with { Timeline = [project.Timeline[0] with { Crop = new(0, 0, 80, 48) }, project.Timeline[1]] }, projectPath)).Supported,
                "Inconsistent crop dimensions accepted.");
        });
        await check("Applied voice replacement is fitted, rendered and sample-validated", async () =>
        {
            var voiceBytes = TestAudio.PcmWave(24000, 520);
            var voicePath = Path.Combine(root, "voice.wav");
            await File.WriteAllBytesAsync(voicePath, voiceBytes);
            var voiceHash = Convert.ToHexStringLower(SHA256.HashData(voiceBytes));
            var voiceProject = project with
            {
                ProjectId = "voice-render",
                Revision = 1,
                ExportMode = "prefer-stream-copy",
                Assets = [.. project.Assets, new("voice-audio", "audio", "voice.wav", voiceHash, 520, 0, 0, "audio/wav")],
                Speakers = [new("speaker", "Speaker")],
                Speech = [new("speech-1", "source-1", 0, 500, "First replacement", ["speaker"], "corrected"),
                    new("speech-2", "source-1", 500, 1000, "Second replacement", ["speaker"], "corrected")],
                Voices = [new("voice", "speaker", "qwen-tts", "aiden", "fixture-model", "English")],
                Replacements = [new("replacement-1", "speech-1", "voice", "First replacement", "voice-audio", "applied", "time-stretch"),
                    new("replacement-2", "speech-2", "voice", "Second replacement", "voice-audio", "applied", "time-stretch")],
                Synthesis = [new("replacement-1", "qwen-tts", "fixture-model", "fixture-runtime", "aiden", "English",
                    Convert.ToHexStringLower(SHA256.HashData("First replacement"u8.ToArray())), voiceHash, 500, 520, "time-stretch"),
                    new("replacement-2", "qwen-tts", "fixture-model", "fixture-runtime", "aiden", "English",
                    Convert.ToHexStringLower(SHA256.HashData("Second replacement"u8.ToArray())), voiceHash, 500, 520, "time-stretch")],
                Provenance = [new("voice-audio", "qwen-tts", "fixture-model", "source-1")]
            };
            var path = Path.Combine(root, "voice-render-project.json");
            await store.SaveAsync(path, voiceProject, 0);
            var plan = await planner.PreflightAsync(voiceProject, path);
            Assert(plan.Supported && plan.RequiresEncoding && plan.Streams[0].Action == "copy" &&
                plan.Streams[1].Action == "encode" && plan.Clips.Single(clip => clip.ClipId == "clip-1").VoiceReplacements.Length == 2 &&
                plan.Clips.Single(clip => clip.ClipId == "clip-1").VoiceReplacements.All(item => item.FitPolicy == "time-stretch"),
                "Voice preflight did not preserve the fitted source-to-output mapping.");
            var strict = await planner.PreflightAsync(voiceProject with { ExportMode = "copy-only" }, path);
            Assert(!strict.Supported && strict.Issues.Any(issue => issue.Code == "unsupported-copy-only"),
                "Strict copy-only accepted replacement rendering.");
            Assert(DeliveryExporter.Plan(voiceProject, path).Issues.Single().Code == "unsupported-replacements",
                "Delivery silently dropped an applied voice replacement instead of refusing it.");
            var partial = voiceProject with { Timeline = [new("partial", "source-1", 500, 1000)] };
            Assert((await planner.PreflightAsync(partial, path)).Issues.Any(issue => issue.Code == "unsupported-voice-edit"),
                "A partially retained voice interval was rendered.");
            var destination = Path.Combine(root, "voice result");
            await Throws<ExportRejectedException>(() => exporter.ExportAsync(path, destination));
            var report = await exporter.ExportAsync(path, destination, allowEncoding: true);
            Assert(report.Validation.AudioContentMatches && report.Validation.AudioSamples == 96000 &&
                report.Plan.Streams.Single(stream => stream.Kind == "audio").Action == "encode",
                "Voice replacement export did not validate exact fitted samples.");
            var pcm = await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-i", Path.Combine(destination, "video.mkv"),
                "-map", "0:a:0", "-f", "s16le", "-c:a", "pcm_s16le", "pipe:1"]);
            Assert(pcm.Output.AsSpan(0, 96000).ContainsAnyExcept((byte)0) &&
                !pcm.Output.AsSpan(96000, 96000).ContainsAnyExcept((byte)0),
                "Rendered timeline did not retain source audio then replace the selected interval.");
            await File.AppendAllTextAsync(voicePath, "changed");
            Assert((await planner.PreflightAsync(voiceProject, path)).Issues.Any(issue => issue.Code == "voice-audio-changed"),
                "Changed generated voice audio was accepted.");
        });
        await check("Timed image insertion previews and exports fitted pixels with silence", async () =>
        {
            var imagePath = Path.Combine(root, "inserted.png");
            await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=0x2050d0:size=80x96:d=1",
                "-frames:v", "1", "-c:v", "png", "-pix_fmt", "rgb24", "-threads:v", "1", "-n", imagePath]);
            var baseProject = project with
            {
                ProjectId = "image-timeline",
                Revision = 1,
                ExportMode = "prefer-stream-copy",
                Assets = [.. project.Assets, new("inserted-image", "image", "inserted.png", await MediaReader.FingerprintAsync(imagePath), 0, 80, 96, "image/png")],
                Provenance = [new("inserted-image", "test-fixture", "synthetic")]
            };
            var path = Path.Combine(root, "image-project.json");
            await store.SaveAsync(path, baseProject, 0);
            var withImage = TimelineEditor.Apply(baseProject,
                [new("insert-image", ClipId: "still", AssetId: "inserted-image", Duration: 1000, BeforeClipId: "clip-1", Fit: "contain")]);
            await store.SaveAsync(path, withImage, 1);
            Assert(withImage.Timeline.Select(clip => clip.Id).SequenceEqual(new[] { "later", "still", "clip-1" }) &&
                withImage.Timeline[1].Audio == "silence", "Image insertion order or audio policy is incorrect.");
            var plan = await planner.PreflightAsync(withImage, path);
            Assert(plan.Supported && plan.RequiresEncoding && plan.Duration == 3000 &&
                plan.Streams.All(stream => stream.Action == "encode"), "Timed-image preflight did not select complete lossless rendering.");
            var strict = await planner.PreflightAsync(withImage with { ExportMode = "copy-only" }, path);
            Assert(!strict.Supported && strict.Issues.Any(issue => issue.Code == "unsupported-copy-only"), "Strict copy-only accepted an active image.");
            var previewer = new TimelinePreviewer(ffmpeg, ffprobe);
            var preview = await previewer.GetFrameAsync(path, 2, 1500);
            Assert(preview.Info.AssetKind == "image" && preview.Info.ClipId == "still" && preview.Info.Actual.Ticks == 1500 &&
                preview.Info.Width == 160 && preview.Info.Height == 96, "Timeline preview did not resolve the inserted image revision and canvas.");
            var coverPath = Path.Combine(root, "cover-project.json");
            var coverProject = withImage with
            {
                ProjectId = "image-cover",
                Revision = 1,
                Timeline = [.. withImage.Timeline.Select(clip => clip.Id == "still" ? clip with { Fit = "cover" } : clip)]
            };
            await store.SaveAsync(coverPath, coverProject, 0);
            var coverPreview = await previewer.GetFrameAsync(coverPath, 1, 1500);
            var containPreviewPath = Path.Combine(root, "contain-preview.png");
            var coverPreviewPath = Path.Combine(root, "cover-preview.png");
            await File.WriteAllBytesAsync(containPreviewPath, preview.Png);
            await File.WriteAllBytesAsync(coverPreviewPath, coverPreview.Png);
            var containRaw = await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-i", containPreviewPath, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
            var coverRaw = await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-i", coverPreviewPath, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
            Assert(containRaw.Output.AsSpan(0, 3).ToArray().All(value => value == 0) &&
                coverRaw.Output.AsSpan(0, 3).ToArray().Any(value => value != 0), "Contain padding and cover cropping were not applied distinctly.");
            await Throws<RevisionConflictException>(() => previewer.GetFrameAsync(path, 1, 1500));
            var cliOption = Array.IndexOf(args, "--cli");
            if (cliOption >= 0)
            {
                var cli = Path.GetFullPath(args[cliOption + 1]);
                var cliPreview = Path.Combine(root, "cli-image-preview.png");
                var cliResult = cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? await ToolProcess.RunAsync("dotnet", [cli, "timeline-frame", path, "2", "1.5", cliPreview])
                    : await ToolProcess.RunAsync(cli, ["timeline-frame", path, "2", "1.5", cliPreview]);
                var cliInfo = JsonSerializer.Deserialize(cliResult.Output, ProjectJson.Default.TimelineFrameInfo)!;
                Assert(cliInfo.ClipId == "still" && File.Exists(cliPreview), "CLI timeline preview did not return the inserted image.");
            }
            var destination = Path.Combine(root, "image result");
            await Throws<ExportRejectedException>(() => exporter.ExportAsync(path, destination));
            var report = await exporter.ExportAsync(path, destination, allowEncoding: true);
            Assert(report.Validation.DecodedFrames == 30 && report.Validation.AudioSamples == 144000 &&
                report.Validation.VideoContentMatches && report.Validation.AudioContentMatches &&
                report.Captions[1].Start.CompareTo(new(2500, new(1, 1000))) == 0,
                "Timed-image export or shifted captions failed validation.");
            var rendered = await new MediaReader(ffmpeg, ffprobe).GetFrameAsync(Path.Combine(destination, "video.mkv"),
                new("output", new(1500, new(1, 1000))));
            Assert(preview.Png.SequenceEqual(rendered.Png), "Timeline preview pixels differ from the exported inserted image.");
            await File.AppendAllTextAsync(imagePath, "changed");
            Assert((await planner.PreflightAsync(withImage, path)).Issues.Any(issue => issue.Code == "image-changed"),
                "Changed image asset was accepted.");
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
        await check("Copy anchors are read from a bounded window, not a whole-file index", async () =>
        {
            // Ten seconds at 10 fps with a keyframe every two seconds, so the anchors are known in advance.
            var anchored = Path.Combine(root, "anchored.mkv");
            await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-f", "lavfi",
                "-i", "testsrc2=size=160x96:rate=10:duration=10", "-f", "lavfi",
                "-i", "sine=frequency=440:sample_rate=48000:duration=10", "-map", "0:v:0", "-map", "1:a:0",
                "-c:v", "libx264", "-g", "20", "-keyint_min", "20", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
                "-threads:v", "1", "-c:a", "libopus", "-b:a", "96k", "-n", anchored]);
            var project = new EditProject
            {
                ProjectId = "anchors",
                TimeBase = new(1, 1000),
                Assets = [new("source", "video", "anchored.mkv", await MediaReader.FingerprintAsync(anchored),
                    10000, 160, 96, "video/x-matroska")],
                Timeline = [new("clip", "source", 0, 10000)]
            };
            var path = Path.Combine(root, "anchors-project.json");
            await store.SaveAsync(path, project, 0);

            var reader = new CutPointReader(ffprobe);
            var points = await reader.ReadAsync(project, path, "source", 5300);
            Assert(Math.Abs(points.KeyframeIntervalSeconds - 2) < 0.15,
                $"The source's own keyframe spacing was not measured: {points.KeyframeIntervalSeconds:0.###}s.");
            Assert(points.Before is { Ticks: 4000, OffsetTicks: -1300 } && points.After is { Ticks: 6000, OffsetTicks: 700 },
                "The anchors either side of the requested time are wrong: " +
                $"{points.Before?.Ticks} and {points.After?.Ticks}.");
            Assert(points.Anchors.Length >= 3 && points.Anchors.All(anchor => anchor.Ticks % 2000 == 0) &&
                points.Anchors.Select(anchor => anchor.Ticks).SequenceEqual(points.Anchors.Select(anchor => anchor.Ticks).Order()),
                "The anchors are not the source's keyframes in order.");
            // Alignment is reported where audio was read and left unknown outside it, rather than guessed.
            Assert(points.AudioPacketSeconds > 0 &&
                points.Anchors.Where(anchor => anchor.Seconds >= 4 && anchor.Seconds <= 6).All(anchor => anchor.AudioAligned == true) &&
                points.Anchors.Any(anchor => anchor.AudioAligned is null),
                "Audio packet alignment was not reported, or was claimed outside the sampled audio.");
            Assert(points.Guidance.Contains("re-encoding", StringComparison.Ordinal),
                "The report does not say what cutting exactly would cost.");

            // A request exactly on an anchor offers it as both neighbours: copying there costs nothing.
            var exact = await reader.ReadAsync(project, path, "source", 6000);
            Assert(exact.Before is { Ticks: 6000, OffsetTicks: 0 } && exact.After is { Ticks: 6000, OffsetTicks: 0 },
                "A time already on a keyframe was not reported as a free cut.");

            // An explicit window is honoured. A window too narrow to reach the next keyframe still reports
            // the one behind, because reading an interval starts at the keyframe preceding it: copying can
            // always begin there, and only the anchor ahead is out of view.
            var narrow = await reader.ReadAsync(project, path, "source", 5300, 1000);
            Assert(narrow.WindowTicks == 1000 && narrow.Before is { Ticks: 4000 } && narrow.After is null,
                "A narrow window did not report the anchor behind, or invented one ahead.");
            await Throws<ArgumentOutOfRangeException>(() => reader.ReadAsync(project, path, "source", 20000));
            await Throws<KeyNotFoundException>(() => reader.ReadAsync(project, path, "absent", 1000));
        });

        await check("The audio profile measures level, band split and silence", async () =>
        {
            // Two seconds of 60 Hz, two of silence, two of 2 kHz: the band split must separate the tones
            // and the silence floor must find the gap, whatever the levels happen to be.
            var profiled = Path.Combine(root, "profiled.mkv");
            await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-f", "lavfi",
                "-i", "testsrc2=size=160x96:rate=10:duration=6", "-f", "lavfi",
                "-i", "aevalsrc='if(lt(t,2),0.5*sin(2*PI*60*t),if(lt(t,4),0,0.5*sin(2*PI*2000*t)))':s=48000:d=6",
                "-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-threads:v", "1",
                "-c:a", "libopus", "-b:a", "96k", "-n", profiled]);
            var project = new EditProject
            {
                ProjectId = "profile",
                TimeBase = new(1, 1000),
                Assets = [new("source", "video", "profiled.mkv", await MediaReader.FingerprintAsync(profiled),
                    6000, 160, 96, "video/x-matroska")],
                Timeline = [new("clip", "source", 0, 6000)]
            };
            var path = Path.Combine(root, "profile-project.json");
            await store.SaveAsync(path, project, 0);

            var profile = await new AudioProfiler(ffmpeg).ProfileAsync(project, path, "source", 0, 6000, 1000);
            Assert(profile.Windows.Length == 6 && profile.BandSplitHz == 200 && profile.SilenceFloorDb == -60,
                $"The profile did not cover the range in one-second windows: {profile.Windows.Length}.");
            var low = profile.Windows[0];
            // The second of the two silent seconds: the first still carries the codec's ringing from the
            // tone before it, which is exactly why the share is unreliable near silence.
            var silent = profile.Windows[3];
            var high = profile.Windows[4];
            Assert(low.LowBandShare > 0.9 && high.LowBandShare < 0.05,
                $"The band split did not separate 60 Hz from 2 kHz: {low.LowBandShare:0.###} against {high.LowBandShare:0.###}.");
            // A lossy codec does not reproduce digital silence exactly, so the written gap reads as mostly
            // rather than entirely silent; what matters is that it is nothing like the tones either side.
            Assert(silent.SilentFraction > 0.7 && low.SilentFraction < 0.05 && high.SilentFraction < 0.05,
                $"Silence was not found where it was written: {silent.SilentFraction:0.##}.");
            Assert(low.LevelDb > -20 && high.LevelDb > -20 && silent.LevelDb < -60 &&
                low.PeakDb > low.LevelDb && silent.PeakDb < low.PeakDb - 20,
                $"Levels or peaks do not describe loud tones against a silent gap: {low.LevelDb:0.#}/{silent.LevelDb:0.#}/{high.LevelDb:0.#} dB.");
            Assert(low.LowBandDb > silent.LowBandDb + 20 && high.LowBandDb < low.LowBandDb - 20,
                "The low-band level does not follow the tones.");
            Assert(profile.Windows.All(window => window.LowBandShare is >= 0 and <= 1),
                "A band share outside zero to one was reported; the filter's own ringing must be clamped.");

            // Measurements only where they can be made: the range, window and band are all bounded.
            await Throws<ArgumentException>(() => new AudioProfiler(ffmpeg).ProfileAsync(project, path, "source", 0, 6000, 1));
            await Throws<ArgumentOutOfRangeException>(() => new AudioProfiler(ffmpeg).ProfileAsync(project, path, "source", 4000, 2000, 1000));
            await Throws<ArgumentOutOfRangeException>(() => new AudioProfiler(ffmpeg).ProfileAsync(project, path, "source", 0, 6000, 1000, 5));
            await Throws<KeyNotFoundException>(() => new AudioProfiler(ffmpeg).ProfileAsync(project, path, "absent", 0, 6000, 1000));
        });

        await check("Copying carries the source's own packets and starts every segment on a keyframe", async () =>
        {
            // The anchored fixture keeps a keyframe every two seconds, so the snapping is known in advance.
            var anchored = Path.Combine(root, "anchored.mkv");
            var project = new EditProject
            {
                ProjectId = "copy",
                TimeBase = new(1, 1000),
                Assets = [new("source", "video", "anchored.mkv", await MediaReader.FingerprintAsync(anchored),
                    10000, 160, 96, "video/x-matroska")],
                Timeline = [new("first", "source", 3000, 7000), new("second", "source", 8000, 10000)]
            };
            var path = Path.Combine(root, "copy-project.json");
            await store.SaveAsync(path, project, 0);
            var exporter = new MuxExporter(ffmpeg, ffprobe);

            var plan = await exporter.PlanAsync(project, path);
            Assert(plan.Supported && plan.VideoCodec == "h264" && plan.AudioCodec == "opus" && plan.Container == "mkv",
                "The copy plan did not report the source's own codecs.");
            // Only the start moves: 3000 sits between anchors and takes the earlier one; 8000 is already an
            // anchor; both ends stay exactly where they were asked for.
            Assert(plan.Segments[0] is { CopyIn: 2000, CopyOut: 7000, InOffsetTicks: -1000, OutOffsetTicks: 0 } &&
                plan.Segments[1] is { CopyIn: 8000, CopyOut: 10000, InOffsetTicks: 0, OutOffsetTicks: 0 } &&
                plan.WorstOffsetTicks == 1000 && plan.CopiedDuration == 7000,
                "The copy plan moved the wrong boundaries: " +
                string.Join("; ", plan.Segments.Select(segment => $"{segment.ClipId} {segment.CopyIn}-{segment.CopyOut}")));

            var destination = Path.Combine(root, "copied result");
            var report = await exporter.ExportAsync(path, destination);
            var output = Path.Combine(destination, "video.mkv");
            Assert(File.Exists(output) && File.Exists(Path.Combine(destination, "mux.json")) &&
                report.OutputSha256 == await MediaReader.FingerprintAsync(output) &&
                report.SourceSha256 == project.Assets[0].Sha256,
                "The copied bundle is incomplete or does not describe itself.");
            Assert(Math.Abs(report.ActualSeconds - 7) < 0.3,
                $"The copied output is {report.ActualSeconds:0.###}s where seven seconds were copied.");

            // Every packet is the source's own: the codecs are unchanged and the picture starts on a
            // keyframe. It may start a fraction of a second in, because the audio packets copied whole
            // around the cut can begin before the first picture does.
            var probe = await ToolProcess.RunAsync(ffprobe, ["-v", "error", "-select_streams", "v:0",
                "-show_entries", "packet=pts_time,flags", "-read_intervals", "%+1", "-of", "csv=p=0", output]);
            var first = Encoding.UTF8.GetString(probe.Output).Split('\n')[0].Split(',');
            Assert(first[1].Contains('K', StringComparison.Ordinal) &&
                double.Parse(first[0], System.Globalization.CultureInfo.InvariantCulture) <= 0.5,
                $"The copied output does not begin on a keyframe promptly: {string.Join(",", first)}.");
            // Two segments meet once, so the muxer may move a few audio packets there; it must say so.
            Assert(report.JoinAdjustments < 200,
                $"The join moved {report.JoinAdjustments} packets, which is more than meeting once should cost.");

            // What copying cannot do, it refuses rather than quietly rendering.
            var cropped = project with { ProjectId = "copy-crop", Timeline = [project.Timeline[0] with { Crop = new(0, 0, 80, 48) }] };
            Assert((await exporter.PlanAsync(cropped, path)).Issues.Single().Code == "unsupported-crop",
                "A crop was accepted by a path that copies packets.");
            Assert((await exporter.PlanAsync(project, path, "mp4")).Issues.Any(issue => issue.Code == "unsupported-container") == false,
                "H.264 was refused for MP4, which carries it.");
            await Throws<IOException>(() => exporter.ExportAsync(path, destination));
        });

        await check("Delivery renders a timeline the strict matrix refuses, in order and at the claimed length", async () =>
        {
            // H.264 video with AAC audio in MP4: ordinary acquired material, outside the validated copy matrix.
            var deliverySource = Path.Combine(root, "delivery source.mp4");
            await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-i", source, "-map", "0:v:0", "-map", "0:a:0",
                "-c:v", "libx264", "-crf", "18", "-pix_fmt", "yuv420p", "-threads:v", "1", "-c:a", "aac", "-b:a", "128k", "-n", deliverySource]);
            var delivered = project with
            {
                ProjectId = "delivery",
                Revision = 1,
                Assets = [project.Assets[0] with { Path = "delivery source.mp4", MediaType = "video/mp4", Sha256 = await MediaReader.FingerprintAsync(deliverySource) }]
            };
            var path = Path.Combine(root, "delivery-project.json");
            await store.SaveAsync(path, delivered, 0);
            var strict = await planner.PreflightAsync(delivered, path);
            Assert(!strict.Supported, "The strict path accepted AAC audio it cannot prove it copied.");
            var plan = DeliveryExporter.Plan(delivered, path);
            Assert(plan.Supported && plan.Width == 160 && plan.Height == 96 && plan.Duration == 2000 &&
                plan.Clips.Select(clip => clip.ClipId).SequenceEqual(new[] { "later", "clip-1" }), "Delivery preflight mapped the timeline incorrectly.");
            var before = await MediaReader.FingerprintAsync(deliverySource);
            var destination = Path.Combine(root, "delivery result");
            var report = await new DeliveryExporter(ffmpeg, ffprobe).ExportAsync(path, destination);
            var output = Path.Combine(destination, "video.mp4");
            Assert(report.ExpectedSeconds == 2 && Math.Abs(report.ActualSeconds - 2) <= 0.05 && report.SilencedAssets.Length == 0 &&
                report.OutputSha256 == await MediaReader.FingerprintAsync(output) && report.OutputBytes == new FileInfo(output).Length,
                "Delivery report does not describe the published file.");
            Assert(Directory.EnumerateFiles(destination).Count() == 3 && File.Exists(Path.Combine(destination, "captions.srt")) &&
                report.Captions.Length == 2, "Delivery bundle is incomplete or retained intermediate files.");
            Assert(await MediaReader.FingerprintAsync(deliverySource) == before, "Delivery modified its source.");
            // Independent oracle: the visible binary frame IDs survive the encode and prove the cut and the order.
            var pixels = await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-i", output,
                "-map", "0:v:0", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
            Assert(pixels.Output.Length == 20 * 160 * 96 * 3, "Delivered file does not hold the twenty retained frames.");
            for (int frame = 0; frame < 20; frame++)
            {
                int identifier = 0;
                for (int bit = 0; bit < 6; bit++)
                {
                    int pixel = frame * 160 * 96 * 3 + (4 * 160 + bit * 16 + 4) * 3;
                    if (pixels.Output[pixel] > 128) identifier |= 1 << bit;
                }
                Assert(identifier == (frame < 10 ? frame + 20 : frame - 10), "Delivered frames are in the wrong order or come from the wrong interval.");
            }
            await Throws<IOException>(() => new DeliveryExporter(ffmpeg, ffprobe).ExportAsync(path, destination));
            var changed = Path.Combine(root, "delivery-changed.json");
            await store.SaveAsync(changed, delivered with { ProjectId = "delivery-changed", Assets = [delivered.Assets[0] with { Sha256 = new string('0', 64) }] }, 0);
            await Throws<InvalidDataException>(() => new DeliveryExporter(ffmpeg, ffprobe).ExportAsync(changed, Path.Combine(root, "delivery changed result")));
            Assert(!Directory.Exists(Path.Combine(root, "delivery changed result")) &&
                !Directory.EnumerateDirectories(root, ".roughcut-delivery-*").Any(), "A rejected delivery left a published or staged output.");
        });
        await check("Every offered export format is produced, probed and named", async () =>
        {
            var deliverySource = Path.Combine(root, "delivery source.mp4");
            var asset = new MediaAsset("source-1", "video", "delivery source.mp4",
                await MediaReader.FingerprintAsync(deliverySource), 4000, 160, 96, "video/mp4");
            var formatProject = new EditProject
            {
                ProjectId = "delivery-formats",
                TimeBase = new(1, 1000),
                Assets = [asset],
                Timeline = [new("clip-1", "source-1", 1000, 2000)]
            };
            var path = Path.Combine(root, "delivery-formats-project.json");
            await store.SaveAsync(path, formatProject, 0);

            // What a caller may offer comes from RoughCut, so a menu cannot drift from the exporters. Copies
            // come first and name the source's own codec; MP4 is offered because H.264 belongs in one.
            var formats = await new RoughCutOperations(new WorkspaceBoundary(root), ffmpeg, ffprobe)
                .ListExportFormatsAsync("delivery-formats-project.json");
            Assert(formats.Options[0] is { Name: "original-mkv", Copy: true, Extension: ".mkv" } &&
                formats.Options[0].Label.Contains("h264", StringComparison.Ordinal) &&
                formats.Options[1] is { Name: "original-mp4", Copy: true } &&
                formats.Options.Count(option => !option.Copy) == DeliveryTarget.All.Count,
                "The offered export formats are wrong or in the wrong order.");
            Assert(DeliveryTarget.Parse(null) == DeliveryTarget.Mp4 && DeliveryTarget.Parse(".webm") == DeliveryTarget.WebM,
                "Delivery target parsing does not accept the names it publishes.");
            await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(DeliveryTarget.Parse("avi")));

            // Each encoded target is produced and probed, because a target claims codecs it must then carry.
            foreach (var target in DeliveryTarget.All)
            {
                var destination = Path.Combine(root, "delivery format " + target.Name);
                var plan = DeliveryExporter.Plan(formatProject, path, target);
                Assert(plan is { Supported: true } && plan.Container == target.Container &&
                    plan.VideoCodec == target.VideoCodec && plan.AudioCodec == target.AudioCodec,
                    $"The {target.Name} plan does not describe the target it was given.");
                var report = await new DeliveryExporter(ffmpeg, ffprobe).ExportAsync(path, destination, default, target);
                var output = Path.Combine(destination, target.FileName);
                Assert(File.Exists(output) && report.Plan.Container == target.Container,
                    $"The {target.Name} bundle does not hold {target.FileName}.");
                var probe = await ToolProcess.RunAsync(ffprobe, ["-v", "error", "-show_entries",
                    "stream=codec_name:format=format_name", "-of", "csv=p=0", "-i", output]);
                var reported = Encoding.UTF8.GetString(probe.Output).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert(reported.Contains(target.VideoCodec) && reported.Contains(target.AudioCodec),
                    $"The {target.Name} file does not carry the codecs it promised: {string.Join(" ", reported)}.");
                Assert(Math.Abs(report.ActualSeconds - 1) <= 0.3,
                    $"The {target.Name} file is {report.ActualSeconds:0.000}s rather than the second the timeline claims.");
            }
        });

        await check("Delivery crops, silences and refuses what it cannot render faithfully", async () =>
        {
            var deliverySource = Path.Combine(root, "delivery source.mp4");
            var asset = new MediaAsset("source-1", "video", "delivery source.mp4",
                await MediaReader.FingerprintAsync(deliverySource), 4000, 160, 96, "video/mp4");
            var cropped = new EditProject
            {
                ProjectId = "delivery-crop",
                TimeBase = new(1, 1000),
                Assets = [asset],
                Timeline = [new("cropped", "source-1", 1000, 2000, new(8, 4, 81, 48)), new("silent", "source-1", 2000, 3000, Audio: "silence")]
            };
            var path = Path.Combine(root, "delivery-crop-project.json");
            await store.SaveAsync(path, cropped, 0);
            var destination = Path.Combine(root, "delivery crop result");
            var report = await new DeliveryExporter(ffmpeg, ffprobe).ExportAsync(path, destination);
            // An odd crop width cannot be encoded as H.264; delivery rounds it down rather than inventing a column.
            Assert(report.Plan.Width == 80 && report.Plan.Height == 48 && report.Captions.Length == 0 &&
                !File.Exists(Path.Combine(destination, "captions.srt")), "Delivery did not fit the timeline to an encodable frame.");
            var probe = await ToolProcess.RunAsync(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height",
                "-of", "csv=p=0", "-i", Path.Combine(destination, "video.mp4")]);
            Assert(Encoding.UTF8.GetString(probe.Output).Trim() == "80,48", "Delivered frame size differs from the plan.");
            var pcm = await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-i", Path.Combine(destination, "video.mp4"),
                "-map", "0:a:0", "-f", "s16le", "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2", "pipe:1"]);
            // Lossy audio never decodes to exact zeroes, so silence is judged by peak amplitude, not equality.
            int Peak(ReadOnlySpan<byte> samples)
            {
                var peak = 0;
                for (var offset = 0; offset + 1 < samples.Length; offset += 2)
                    peak = Math.Max(peak, Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(samples[offset..])));
                return peak;
            }
            Assert(Peak(pcm.Output.AsSpan(0, 96000)) > 2000 && Peak(pcm.Output.AsSpan(280000)) < 256,
                "A clip asking for silence did not deliver silence, or source audio was lost.");
            var image = cropped with
            {
                ProjectId = "delivery-image",
                Assets = [asset, new("still", "image", "inserted.png", new string('c', 64), 0, 80, 96, "image/png")],
                Timeline = [new("hold", "still", 0, 1000, Audio: "silence")]
            };
            Assert(DeliveryExporter.Plan(image, path).Issues.Single().Code == "unsupported-timeline", "Delivery silently omitted a timed image.");
            var missing = cropped with { ProjectId = "delivery-missing", Assets = [asset with { Path = "absent.mp4" }] };
            Assert(DeliveryExporter.Plan(missing, path).Issues.Single().Code == "missing-media", "Delivery accepted missing media.");
            var imagePath = await SaveAsync(store, root, "delivery-image-project.json", image);
            await Throws<DeliveryRejectedException>(() => new DeliveryExporter(ffmpeg, ffprobe).ExportAsync(
                imagePath, Path.Combine(root, "delivery image result")));
            Assert(!Directory.Exists(Path.Combine(root, "delivery image result")), "A refused delivery created output.");
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
            await check("CLI caption import, edit, preflight, export, delivery and stale-edit rejection", async () =>
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
                var deliveryPlanResult = await RunCli("preflight-delivery", Path.Combine(root, "delivery-project.json"));
                var deliveryPlan = JsonSerializer.Deserialize(deliveryPlanResult.Output, ProjectJson.Default.DeliveryPlan)!;
                Assert(deliveryPlan.Supported && deliveryPlan.Clips.Length == 2, "CLI delivery preflight failed.");
                var deliveryDestination = Path.Combine(root, "cli delivery result");
                var deliveryResult = await RunCli("deliver", Path.Combine(root, "delivery-project.json"), deliveryDestination);
                var deliveryReport = JsonSerializer.Deserialize(deliveryResult.Output, ProjectJson.Default.DeliveryReport)!;
                Assert(deliveryReport.ExpectedSeconds == 2 && File.Exists(Path.Combine(deliveryDestination, "video.mp4")),
                    "CLI delivery did not publish a bundle.");
                await Throws<MediaToolException>(() => RunCli("deliver", Path.Combine(root, "delivery-project.json"), deliveryDestination));
                var encoded = await RunCli("export", path, encodedDestination, "--allow-encode");
                var encodedReport = JsonSerializer.Deserialize(encoded.Output, ProjectJson.Default.ExportReport)!;
                Assert(encodedReport.Plan.Revision == 4 && encodedReport.Plan.Width == 80 && encodedReport.Plan.Streams[0].Action == "encode" &&
                    encodedReport.Validation.VideoContentMatches, "CLI crop/encoding policy failed.");
            });
        }
    }
}
