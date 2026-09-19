using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using RoughCut.Application;
using RoughCut.Core;
using RoughCut.Media;

var failures = 0;
var passed = 0;
var testRoot = Path.GetFullPath(Path.Combine("artifacts", "tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(testRoot);

async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
        if (exception is MediaToolException mediaError)
            Console.Error.WriteLine(mediaError.Diagnostic.Replace(testRoot, "<test-artifacts>", StringComparison.OrdinalIgnoreCase));
        failures++;
    }
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static EditProject Fixture() => new()
{
    ProjectId = "fixture",
    TimeBase = new(1, 1000),
    Assets =
    [
        new("video", "video", "media/source.mkv", new string('a', 64), 10000, 320, 240, "video/x-matroska"),
        new("image", "image", "media/still.png", new string('b', 64), 0, 320, 240, "image/png"),
        new("audio", "audio", "media/voice.wav", new string('c', 64), 1000, 0, 0, "audio/wav")
    ],
    Timeline = [new("later", "video", 5000, 8000, new(0, 0, 160, 120)), new("earlier", "video", 1000, 2000),
        new("still", "image", 0, 2000, Audio: "silence")],
    Speakers = [new("speaker-1", "Speaker 1")],
    Speech = [new("speech-1", "video", 1000, 2000, "Synthetic speech", ["speaker-1"], "corrected")],
    Transcription = new("video", new string('a', 64), "fixture-stt", "fixture-model", "en", 30),
    Voices = [new("voice-1", "speaker-1", "qwen-tts", "configured-voice")],
    Replacements = [new("replacement-1", "speech-1", "voice-1", "Synthetic speech", "audio", "applied", "exact")],
    Synthesis = [new("replacement-1", "qwen-tts", "unspecified", "fixture-only", "configured-voice", "Auto",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Synthetic speech"))), new string('c', 64), 1000, 1000, "exact")],
    Provenance = [new("audio", "qwen-tts", "fixture-only", "video")]
};
var store = new ProjectStore();

await Check("Exact rational comparison does not overflow", () =>
{
    Assert(new MediaTime(1, new(1, 3)).CompareTo(new(333333, TimeBase.Microseconds)) > 0, "Rational precision lost.");
    Assert(new MediaTime(long.MaxValue, new(long.MaxValue, 1)).CompareTo(new(long.MaxValue, new(1, 1))) > 0, "Comparison overflowed.");
    return Task.CompletedTask;
});
await Check("JSON preserves speech, voices, images, crop and order", async () =>
{
    var project = Fixture();
    var path = Path.Combine(testRoot, "roundtrip.json");
    await store.SaveAsync(path, project, 0);
    var restored = await store.LoadAsync(path);
    Assert(JsonSerializer.Serialize(project, ProjectJson.Default.EditProject) == JsonSerializer.Serialize(restored, ProjectJson.Default.EditProject), "Round trip changed project meaning.");
    var map = ProjectValidator.MapTimeline(restored);
    Assert(map[0].SourceIn == 5000 && map[1].OutputIn == 3000 && map[2].OutputOut == 6000, "Reorder mapping incorrect.");
});
await Check("Invalid versions, ranges, crops and references are rejected", () =>
{
    EditProject[] invalid =
    [
        Fixture() with { SchemaVersion = 2 },
        Fixture() with { TimeBase = new(0, 1) },
        Fixture() with { Timeline = [new("bad", "absent", 0, 100)] },
        Fixture() with { Timeline = [new("bad", "video", 10000, 10001)] },
        Fixture() with { Timeline = [new("bad", "video", 1, 1)] },
        Fixture() with { Timeline = [new("bad", "video", 0, 100, new(300, 0, 30, 10))] },
        Fixture() with { Timeline = [new("bad", "image", 1, 100)] },
        Fixture() with { Timeline = [new("bad", "video", 0, 100), new("bad", "video", 0, 100)] },
        Fixture() with { Voices = [new("bad", "unknown", "qwen-tts", "voice")] },
        Fixture() with { Speech = [new("bad", "video", 0, 100, "", ["missing"])] },
        Fixture() with { Assets = [Fixture().Assets[0] with { Path = "../private.mp4" }] },
        Fixture() with { Assets = [Fixture().Assets[0] with { Sha256 = "invalid" }] },
        Fixture() with { Timeline = [new("huge1", "image", 0, long.MaxValue, Audio: "silence"), new("huge2", "image", 0, 1, Audio: "silence")] }
    ];
    foreach (var project in invalid) Assert(ProjectValidator.Validate(project).Length > 0, "Invalid fixture accepted.");
    return Task.CompletedTask;
});
await Check("Portable paths reject Windows and Unix escape forms", () =>
{
    foreach (var path in new[] { "../secret", "/secret", "C:/secret", "file://secret", "media\\source.mp4", "media/../secret", "media/name:stream", "media//file" })
        Assert(!ProjectValidator.IsPortablePath(path), "Unsafe path accepted.");
    Assert(ProjectValidator.IsPortablePath("media/a clip.mp4"), "Portable path with spaces rejected.");
    return Task.CompletedTask;
});
await Check("Unknown fields and null entries fail without silent data loss", async () =>
{
    var path = Path.Combine(testRoot, "malformed.json");
    await File.WriteAllTextAsync(path, "{\"futureFeature\":true}");
    await Throws<JsonException>(async () => await store.LoadAsync(path));
    await File.WriteAllTextAsync(path, "{\"assets\":[null]}");
    await Throws<ProjectValidationException>(async () => await store.LoadAsync(path));
    await File.WriteAllTextAsync(path, "{\"assets\":null}");
    await Throws<JsonException>(async () => await store.LoadAsync(path));
});
await Check("Stale and cancelled saves preserve the prior project", async () =>
{
    var path = Path.Combine(testRoot, "revision.json");
    var project = Fixture();
    await store.SaveAsync(path, project, 0);
    await store.SaveAsync(path, project with { Revision = 2, Prompt = "winner" }, 1);
    await Throws<RevisionConflictException>(() => store.SaveAsync(path, project with { Revision = 2, Prompt = "stale" }, 1));
    await Throws<OperationCanceledException>(() => store.SaveAsync(path, project with { Revision = 3 }, 2, new(true)));
    Assert((await store.LoadAsync(path)).Prompt == "winner", "Prior document damaged.");
    Assert(!Directory.EnumerateFiles(testRoot, "*.tmp").Any(), "Temporary save leaked.");
});
await Check("Two writers cannot both replace the same revision", async () =>
{
    var path = Path.Combine(testRoot, "race.json");
    await store.SaveAsync(path, Fixture(), 0);
    async Task<bool> Write(string prompt)
    {
        try { await store.SaveAsync(path, Fixture() with { Revision = 2, Prompt = prompt }, 1); return true; }
        catch (Exception e) when (e is IOException or RevisionConflictException) { return false; }
    }
    var results = await Task.WhenAll(Write("a"), Write("b"));
    Assert(results.Count(x => x) == 1 && (await store.LoadAsync(path)).Revision == 2, "Concurrent writers both succeeded.");
});
await Check("Oversized project rejected", async () =>
{
    await Throws<InvalidDataException>(() => store.SaveAsync(Path.Combine(testRoot, "large.json"), Fixture() with { Prompt = new('x', ProjectStore.MaxDocumentBytes) }, 0));
});
await Check("Caption selection records quality provenance and allows explicit override", async () =>
{
    var projectPath = Path.Combine(testRoot, "caption-selection.json");
    await store.SaveAsync(projectPath, Fixture() with { Revision = 1, Captions = null }, 0);
    await File.WriteAllTextAsync(Path.Combine(testRoot, "manual-en.srt"), "1\n00:00:00,000 --> 00:00:05,000\nManual captions\n");
    await File.WriteAllTextAsync(Path.Combine(testRoot, "automatic-en.srt"), "1\n00:00:00,000 --> 00:00:09,000\nAutomatic captions\n");
    await File.WriteAllTextAsync(Path.Combine(testRoot, "invalid.srt"), "not srt");
    CaptionCandidate[] candidates =
    [
        new("auto-en", "automatic-en.srt", "automatic", "en"),
        new("manual-en", "manual-en.srt", "manual", "en"),
        new("invalid", "invalid.srt", "supplied", "en")
    ];
    var operations = new RoughCutOperations(new WorkspaceBoundary(testRoot));
    var recommended = await operations.SelectCaptionsAsync("caption-selection.json", "video", candidates, 1, "en");
    Assert(recommended.SelectedId == "manual-en" && !recommended.ExplicitOverride &&
        recommended.Project.Captions is { SourceKind: "manual", Selection: "recommended", CoverageBasisPoints: 5000 } &&
        recommended.Candidates.Single(item => item.Id == "invalid").Valid == false,
        "Caption recommendation did not preserve quality/provenance evidence.");
    var overridden = await operations.SelectCaptionsAsync("caption-selection.json", "video", candidates, 2, "en", "auto-en");
    Assert(overridden.SelectedId == "auto-en" && overridden.ExplicitOverride &&
        overridden.Project.Captions is { SourceKind: "automatic", Selection: "override", CoverageBasisPoints: 9000 },
        "Explicit caption override was not persisted.");
    await Throws<RevisionConflictException>(() => operations.SelectCaptionsAsync("caption-selection.json", "video", candidates, 2, "en"));
});
await Check("yt-dlp acquisition is bounded, staged and strips URL secrets from provenance", async () =>
{
    var fake = new TestAcquisitionTool();
    var result = await new YtDlpAcquirer(new WorkspaceBoundary(testRoot), "fake-yt-dlp", fake)
        .AcquireAsync("https://example.test/watch?id=private-token#fragment", "acquired");
    Assert(result.Source == "https://example.test/watch" && result.MediaPath == "source.mkv" &&
        result.Captions is [{ Language: "en", SourceKind: "manual", CueCount: 1 }] &&
        File.Exists(Path.Combine(testRoot, "acquired", "acquisition.json")) &&
        fake.DownloadArguments is not null && fake.DownloadArguments.Contains("--ignore-config") &&
        fake.DownloadArguments.Contains("--no-js-runtimes") && fake.DownloadArguments.Contains("--no-remote-components") &&
        fake.DownloadArguments.Contains("--no-playlist") && fake.DownloadArguments[^2] == "--",
        "Acquisition safety policy or portable manifest is incorrect.");
    await Throws<ArgumentException>(() => new YtDlpAcquirer(new WorkspaceBoundary(testRoot), "fake", fake)
        .AcquireAsync("file:///private/video", "invalid-acquisition"));
    await Throws<OperationCanceledException>(() => new YtDlpAcquirer(new WorkspaceBoundary(testRoot), "fake", new TestAcquisitionTool())
        .AcquireAsync("https://example.test/cancel", "cancelled-acquisition", cancellationToken: new(true)));
    Assert(!Directory.Exists(Path.Combine(testRoot, "cancelled-acquisition")) &&
        !Directory.EnumerateDirectories(testRoot, ".roughcut-acquire-*").Any(), "Cancelled acquisition leaked staged output.");
});
await Check("Analysis proposals retain uncertainty and apply only revision-bound removals", async () =>
{
    var submission = new AnalysisSubmission("video", "fixture-agent", "labelled-v1",
    [
        new("evidence-ad", "video", 1200, 1600, "transcript", "A labelled sponsorship statement.", ["speech-1"], [1300]),
        new("evidence-ad-overlap", "video", 1400, 1800, "metadata", "A labelled sponsor chapter overlaps the statement.", [], []),
        new("evidence-uncertain", "video", 1700, 1900, "frame", "A possible transition with weak evidence.", [], [1800]),
        new("evidence-topic", "video", 5200, 5600, "frame", "The requested main topic continues.", [], [5300])
    ],
    [
        new("observation-ad", "video", 1200, 1600, "sponsorship", "Labelled advertisement fixture.", "high", "remove", ["evidence-ad"]),
        new("observation-ad-overlap", "video", 1400, 1800, "sponsorship", "Overlapping labelled advertisement evidence.", "high", "remove", ["evidence-ad-overlap"]),
        new("observation-uncertain", "video", 1700, 1900, "possible-ad", "Insufficient evidence for automatic removal.", "low", "remove", ["evidence-uncertain"]),
        new("observation-topic", "video", 5200, 5600, "topic", "Requested content should be retained.", "high", "retain", ["evidence-topic"])
    ]);
    var review = AnalysisPlanner.Plan(Fixture(), submission, "Remove advertisements", "review");
    Assert(review.RemoveDecisions == 0 && review.ReviewDecisions == 3 && review.RetainDecisions == 1,
        "Review policy did not preserve removal decisions for review.");
    var automatic = AnalysisPlanner.Plan(Fixture(), submission, "Remove advertisements", "auto-high-certainty");
    Assert(automatic.RemoveDecisions == 2 && automatic.ReviewDecisions == 1 && automatic.RetainDecisions == 1 &&
        automatic.Project.Analysis is { Provider: "fixture-agent", ProposalRevision: 2 },
        "Automatic policy did not limit removal to high-certainty evidence.");
    var applied = AnalysisPlanner.Apply(automatic.Project);
    Assert(applied.Revision == 3 && applied.Proposals.Length == 0 &&
        applied.Timeline.Any(clip => clip.Id == "earlier" && clip.In == 1000 && clip.Out == 1200) &&
        applied.Timeline.Any(clip => clip.AssetId == "video" && clip.In == 1800 && clip.Out == 2000),
        "Automatic proposal application did not merge overlapping removals or preserve surrounding material.");
    var uncertainId = automatic.Project.Proposals.Single(item => item.ObservationId == "observation-uncertain").Id;
    var explicitlyApplied = AnalysisPlanner.Apply(automatic.Project, [uncertainId]);
    Assert(explicitlyApplied.Timeline.Any(clip => clip.In == 1000 && clip.Out == 1700) &&
        explicitlyApplied.Timeline.Any(clip => clip.In == 1900 && clip.Out == 2000),
        "Explicit reviewed removal did not apply the selected source interval.");
    var manuallyEdited = TimelineEditor.Apply(automatic.Project, [new("trim", "later", In: 5200, Out: 7800)]);
    Assert(manuallyEdited.Proposals.Length == 0, "A timeline edit did not invalidate prior proposals.");
    await Throws<ProjectValidationException>(() => Task.FromResult(AnalysisPlanner.Plan(Fixture(),
        submission with { Observations = [submission.Observations[0] with { EvidenceIds = ["missing"] }] },
        "Remove advertisements", "review")));
    var uncertainIndex = Array.FindIndex(automatic.Project.Proposals,
        proposal => proposal.ObservationId == "observation-uncertain");
    var tamperedProposals = automatic.Project.Proposals.ToArray();
    tamperedProposals[uncertainIndex] = tamperedProposals[uncertainIndex] with { Decision = "remove" };
    await Throws<ProjectValidationException>(() => Task.Run(() => ProjectValidator.EnsureValid(
        automatic.Project with { Proposals = tamperedProposals })));
    await Throws<RevisionConflictException>(() => Task.FromResult(AnalysisPlanner.Apply(
        automatic.Project with { Revision = 3 })));
});

await Check("Speaker corrections and voice previews are revisioned and reversible", async () =>
{
    var path = Path.Combine(testRoot, "voice-project.json");
    var fixture = Fixture();
    var project = fixture with
    {
        ProjectId = "voice-workflow",
        Assets = [fixture.Assets[0]],
        Timeline = [new("clip", "video", 0, 3000)],
        Voices = [],
        Replacements = [],
        Synthesis = [],
        Provenance = []
    };
    await store.SaveAsync(path, project, 0);
    var operations = new RoughCutOperations(new WorkspaceBoundary(testRoot));
    var corrected = await operations.ApplySpeakerEditsAsync(Path.GetFileName(path), 1,
    [
        new("add", SpeakerId: "speaker-2", Label: "Guest", Reason: "split reviewed speaker"),
        new("assign", SegmentIds: ["speech-1"], SpeakerIds: ["speaker-2"], Reason: "reviewed segment")
    ]);
    Assert(corrected.Speech.Single().SpeakerIds.SequenceEqual(["speaker-2"]) && corrected.SpeakerCorrections.Length == 2,
        "Speaker assignment or correction history was not persisted.");
    var planned = await operations.PlanVoiceAsync(Path.GetFileName(path), 2,
        new(new("voice-2", "speaker-2", "qwen-tts", "aiden", "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice", "English"),
            new("replace-1", "speech-1", "voice-2", "Replacement text")));
    Assert(planned.Replacements.Single().State == "requested", "Voice request was not planned.");
    var imported = await operations.ImportVoicePreviewAsync(Path.GetFileName(path), 3, "replace-1",
        Convert.ToBase64String(TestAudio.PcmWave()), "fixture-qwen-runtime");
    Assert(imported.Replacements.Single().State == "preview" && imported.Synthesis.Single().RequestedDuration == 1000 &&
        imported.Synthesis.Single().ActualDuration == 1000, "Voice preview provenance or exact duration is wrong.");
    var preview = await operations.GetVoicePreviewAsync(Path.GetFileName(path), 4, "replace-1");
    Assert(preview.Wav.SequenceEqual(TestAudio.PcmWave()) && preview.Info.State == "preview", "Voice preview did not round-trip.");
    var applied = await operations.SetVoiceStateAsync(Path.GetFileName(path), 4, "replace-1", "applied");
    Assert(applied.Replacements.Single().State == "applied", "Voice preview was not applied.");
    var reverted = await operations.SetVoiceStateAsync(Path.GetFileName(path), 5, "replace-1", "reverted");
    Assert(reverted.Replacements.Single().State == "reverted", "Voice replacement was not reverted.");
    var mismatched = reverted with
    {
        Replacements = [reverted.Replacements.Single() with { State = "preview" }],
        Synthesis = [reverted.Synthesis.Single() with { ActualDuration = 500 }]
    };
    await Throws<NotSupportedException>(() => Task.FromResult(VoicePlanner.SetState(mismatched, "replace-1", "applied")));
    await Throws<InvalidDataException>(() => Task.Run(() => WaveAudio.Inspect("not-wave"u8)));
    await Throws<RevisionConflictException>(() => operations.SetVoiceStateAsync(Path.GetFileName(path), 5, "replace-1", "applied"));
});

if (args.Contains("--media", StringComparer.Ordinal))
{
    var ffmpeg = Environment.GetEnvironmentVariable("ROUGHCUT_FFMPEG") ?? "ffmpeg";
    var reader = new MediaReader(ffmpeg, Environment.GetEnvironmentVariable("ROUGHCUT_FFPROBE") ?? "ffprobe");
    var source = Path.Combine(testRoot, "synthetic clip.mkv");
    await Check("Generate reproducible lossless interframe fixture", async () =>
    {
        await ToolProcess.RunAsync(ffmpeg,
            ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=10:duration=2",
             "-c:v", "libx264", "-qp", "0", "-g", "20", "-threads", "1", "-an", "-y", source]);
    });
    await Check("Analysis persistence verifies source evidence and applies conservative decisions", async () =>
    {
        var info = await reader.InspectAsync(source);
        var analysisPath = Path.Combine(testRoot, "analysis-project.json");
        var analysisProject = new EditProject
        {
            ProjectId = "analysis-fixture",
            TimeBase = info.TimeBase,
            Assets = [new("source-1", "video", Path.GetFileName(source), info.Sha256, info.DurationTicks,
                info.Width, info.Height, "video/x-matroska")],
            Timeline = [new("clip-1", "source-1", 0, info.DurationTicks)]
        };
        await store.SaveAsync(analysisPath, analysisProject, 0);
        var end = TimeMath.ExactTicks(new(700, new(1, 1000)), info.TimeBase);
        var start = TimeMath.ExactTicks(new(300, new(1, 1000)), info.TimeBase);
        var frame = TimeMath.ExactTicks(new(350, new(1, 1000)), info.TimeBase);
        var submission = new AnalysisSubmission("source-1", "fixture-agent", "labelled-v1",
            [new("frame-evidence", "source-1", start, end, "frame", "Labelled interruption.", [], [frame])],
            [new("interruption", "source-1", start, end, "advertisement", "Remove labelled interruption.",
                "high", "remove", ["frame-evidence"])]);
        var operations = new RoughCutOperations(new WorkspaceBoundary(testRoot), ffmpeg);
        var planned = await operations.SaveAnalysisAsync("analysis-project.json", 1, "Remove advertisements",
            "auto-high-certainty", submission);
        Assert(planned.Project.Revision == 2 && planned.RemoveDecisions == 1 && planned.Project.Proposals.Length == 1,
            "Analysis plan was not persisted with an automatic high-certainty decision.");
        var applied = await operations.ApplyAnalysisAsync("analysis-project.json", 2);
        Assert(applied.Revision == 3 && applied.Proposals.Length == 0 && applied.Timeline.Length == 2 &&
            applied.Timeline[0].In == 0 && applied.Timeline[0].Out == start &&
            applied.Timeline[1].In == end && applied.Timeline[1].Out == info.DurationTicks,
            "Persisted analysis decision did not remove exactly the labelled interval.");
    });
    await Check("Local speech processing chunks bounded PCM and preserves timed provenance", async () =>
    {
        var audio = Path.Combine(testRoot, "speech.wav");
        await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=12",
            "-c:a", "pcm_s16le", "-y", audio]);
        var speechProject = Fixture() with
        {
            TimeBase = new(1, 1000),
            Assets = [.. Fixture().Assets, new("speech-source", "audio", "speech.wav", await MediaReader.FingerprintAsync(audio), 12000, 0, 0, "audio/wav")]
        };
        var transcriber = new TestSpeechTranscriber();
        var report = await new LocalSpeechProcessor(ffmpeg).TranscribeAsync(speechProject,
            Path.Combine(testRoot, "speech-project.json"), "speech-source", transcriber, chunkSeconds: 5);
        Assert(report.Chunks == 3 && report.Provider == "test-local" && report.Model == "timed-fixture" &&
            report.Segments.Select(segment => (segment.Start, segment.End)).SequenceEqual(new[] { (0L, 5000L), (5000L, 10000L), (10000L, 12000L) }) &&
            transcriber.MaximumBytes <= 5 * LocalSpeechProcessor.SampleRate * sizeof(short),
            "Local STT chunking or timed provenance is incorrect.");
        var speechProjectPath = Path.Combine(testRoot, "speech-project.json");
        await store.SaveAsync(speechProjectPath, speechProject with
        {
            Revision = 1,
            Speech = [],
            Transcription = null,
            Voices = [],
            Replacements = [],
            Synthesis = [],
            Captions = null
        }, 0);
        var saved = await new RoughCutOperations(new WorkspaceBoundary(testRoot), ffmpeg)
            .TranscribeLocalAsync("speech-project.json", "speech-source", 1, transcriber, chunkSeconds: 5);
        Assert(saved.Revision == 2 && saved.Speech.Length == 3 && saved.Transcription is { Provider: "test-local", ChunkSeconds: 5 } &&
            saved.Captions is { SourceKind: "local-stt", Selection: "recommended" } &&
            File.Exists(Path.Combine(testRoot, saved.Captions.SourcePath.Replace('/', Path.DirectorySeparatorChar))),
            "Local STT results were not persisted with portable caption/provenance data.");
        await Throws<NotSupportedException>(() => new LocalSpeechProcessor(ffmpeg).TranscribeAsync(
            speechProject with { Assets = [.. speechProject.Assets.Select(asset => asset.Id == "speech-source" ? asset with { Duration = LocalSpeechProcessor.MaxDurationSeconds * 1000L + 1 } : asset)] },
            Path.Combine(testRoot, "speech-project.json"), "speech-source", transcriber));
    });
    await Check("Frame between keyframes has exact time and expected pixels", async () =>
    {
        var before = await MediaReader.FingerprintAsync(source);
        var frame = await reader.GetFrameAsync(source, new("video", new(350000, TimeBase.Microseconds)));
        Assert(frame.Info.FrameIndex == 3 && frame.Info.Actual.CompareTo(new(3, new(1, 10))) == 0, "Wrong display frame.");
        var expected = await ToolProcess.RunAsync(ffmpeg,
            ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=10:duration=2",
             "-vf", "select=eq(n\\,3),format=rgb24", "-frames:v", "1", "-c:v", "png", "-f", "image2pipe", "pipe:1"]);
        Assert(frame.Png.SequenceEqual(expected.Output), "Decoded frame differs from synthetic source pixels.");
        Assert(await MediaReader.FingerprintAsync(source) == before, "Source was modified.");
        await File.WriteAllBytesAsync(Path.Combine(testRoot, "frame.png"), frame.Png);
    });
    await Check("Frame boundaries, final frame and out-of-range requests", async () =>
    {
        var boundary = await reader.GetFrameAsync(source, new("video", new(400000, TimeBase.Microseconds)));
        Assert(boundary.Info.FrameIndex == 4, "Half-open boundary selected previous frame.");
        var final = await reader.GetFrameAsync(source, new("video", new(1999999, TimeBase.Microseconds)));
        Assert(final.Info.FrameIndex == 19, "Last frame missing.");
        await Throws<ArgumentOutOfRangeException>(async () => await reader.GetFrameAsync(source, new("video", new(2000000, TimeBase.Microseconds))));
        await Throws<ArgumentException>(async () => await reader.GetFrameAsync(source, new("video", new(-1, TimeBase.Microseconds))));
    });
    await Check("Nonzero stream origin maps to source-relative time", async () =>
    {
        var offset = Path.Combine(testRoot, "offset.mkv");
        await ToolProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-i", source, "-c", "copy", "-output_ts_offset", "5", "-y", offset]);
        var frame = await reader.GetFrameAsync(offset, new("video", new(350000, TimeBase.Microseconds)));
        Assert(frame.Info.StreamStartTicks > 0 && frame.Info.FrameIndex == 3 && frame.Info.Actual.CompareTo(new(3, new(1, 10))) == 0, "Stream origin not normalized.");
    });
    await Check("Media cancellation and subprocess output bounds", async () =>
    {
        await Throws<OperationCanceledException>(async () => await reader.InspectAsync(source, new(true)));
        await Throws<InvalidDataException>(async () => await ToolProcess.RunAsync(ffmpeg,
            ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=160x96:duration=1", "-f", "rawvideo", "pipe:1"], outputLimit: 32));
        await Throws<OperationCanceledException>(async () => await ToolProcess.RunAsync(ffmpeg,
            ["-v", "error", "-nostdin", "-re", "-f", "lavfi", "-i", "testsrc2=size=160x96:duration=10", "-f", "null", "-"], timeout: TimeSpan.FromMilliseconds(200)));
    });
    await Check("Variable frame timestamps and explicit display gaps", async () =>
    {
        var variable = Path.Combine(testRoot, "variable.mkv");
        await ToolProcess.RunAsync(ffmpeg,
            ["-v", "error", "-nostdin", "-i", source, "-vf", "setpts=if(lt(N\\,3)\\,PTS\\,PTS+0.2/TB)",
             "-fps_mode", "vfr", "-c:v", "ffv1", "-y", variable]);
        var frame = await reader.GetFrameAsync(variable, new("video", new(550000, TimeBase.Microseconds)));
        Assert(frame.Info.FrameIndex == 3 && frame.Info.Actual.CompareTo(new(1, new(1, 2))) == 0, "VFR request was treated as nominal FPS.");
        await Throws<ArgumentOutOfRangeException>(async () => await reader.GetFrameAsync(variable, new("video", new(350000, TimeBase.Microseconds))));
    });

    var cliOption = Array.IndexOf(args, "--cli");
    if (cliOption >= 0)
    {
        if (cliOption + 1 >= args.Length) throw new ArgumentException("--cli requires an executable or DLL path.");
        var cli = Path.GetFullPath(args[cliOption + 1]);
        Task<ToolResult> RunCli(params string[] arguments) => cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? ToolProcess.RunAsync("dotnet", new[] { cli }.Concat(arguments))
            : ToolProcess.RunAsync(cli, arguments);
        await Check("CLI create, validate, inspect, map, revision save and frame", async () =>
        {
            var projectPath = Path.Combine(testRoot, "cli-project.json");
            await RunCli("create", source, projectPath);
            await RunCli("validate", projectPath);
            await RunCli("inspect", source);
            await RunCli("map", projectPath);
            var project = await store.LoadAsync(projectPath);
            var candidate = Path.Combine(testRoot, "candidate.json");
            await File.WriteAllTextAsync(candidate, JsonSerializer.Serialize(project with { Revision = 2, Prompt = "Test edit" }, ProjectJson.Default.EditProject));
            await RunCli("save", candidate, projectPath, "1");
            Assert((await store.LoadAsync(projectPath)).Revision == 2, "CLI save did not advance revision.");
            var framePath = Path.Combine(testRoot, "cli-frame.png");
            var result = await RunCli("frame", source, "0.35", framePath);
            var frame = JsonSerializer.Deserialize(result.Output, ProjectJson.Default.FrameInfo)!;
            Assert(frame.FrameIndex == 3 && File.Exists(framePath), "CLI frame result missing.");
            var before = await MediaReader.FingerprintAsync(framePath);
            await Throws<MediaToolException>(() => RunCli("frame", source, "0.45", framePath));
            Assert(await MediaReader.FingerprintAsync(framePath) == before, "CLI overwrote an existing output.");
            await Throws<MediaToolException>(() => RunCli("save", candidate, projectPath, "1"));
            await Throws<MediaToolException>(() => RunCli("frame", source, "2", Path.Combine(testRoot, "outside.png")));
            var captions = Path.Combine(testRoot, "cli-captions.srt");
            await File.WriteAllTextAsync(captions, "1\n00:00:00,000 --> 00:00:01,000\nCLI captions\n");
            var captionCandidates = Path.Combine(testRoot, "cli-caption-candidates.json");
            await File.WriteAllTextAsync(captionCandidates, JsonSerializer.Serialize(
                new CaptionCandidate[] { new("cli-manual", "cli-captions.srt", "manual", "en") }, ProjectJson.Default.CaptionCandidateArray));
            var selected = await RunCli("captions-select", projectPath, "source-1", captionCandidates, "2", "en");
            var selection = JsonSerializer.Deserialize(selected.Output, ProjectJson.Default.CaptionSelectionResult)!;
            Assert(selection.SelectedId == "cli-manual" && selection.Project.Revision == 3, "CLI caption assessment failed.");
            var analysisSubmission = Path.Combine(testRoot, "cli-analysis.json");
            var submission = new AnalysisSubmission("source-1", "fixture-agent", "labelled-v1",
                [new("cli-evidence", "source-1", 300, 700, "frame", "Labelled interruption.", [], [350])],
                [new("cli-observation", "source-1", 300, 700, "advertisement", "Reviewed removal.",
                    "medium", "remove", ["cli-evidence"])]);
            await File.WriteAllTextAsync(analysisSubmission,
                JsonSerializer.Serialize(submission, ProjectJson.Default.AnalysisSubmission));
            var analysed = await RunCli("analyse", projectPath, analysisSubmission, "3", "review", "Remove advertisements");
            var plan = JsonSerializer.Deserialize(analysed.Output, ProjectJson.Default.AnalysisPlanResult)!;
            Assert(plan.Project.Revision == 4 && plan.RemoveDecisions == 0 && plan.ReviewDecisions == 1,
                "CLI analysis did not retain a reviewed removal.");
            await RunCli("analysis-apply", projectPath, "4", plan.Project.Proposals.Single().Id);
            var analysisApplied = await store.LoadAsync(projectPath);
            Assert(analysisApplied.Revision == 5 && analysisApplied.Proposals.Length == 0 && analysisApplied.Timeline.Length == 2,
                "CLI did not apply the explicitly reviewed proposal.");

            var voiceProjectPath = Path.Combine(testRoot, "cli-voice-project.json");
            await store.SaveAsync(voiceProjectPath, new EditProject
            {
                ProjectId = "cli-voice",
                TimeBase = new(1, 1000),
                Assets = [new("source", "video", "synthetic clip.mkv", await MediaReader.FingerprintAsync(source), 2000, 160, 96, "video/x-matroska")],
                Timeline = [new("clip", "source", 0, 2000)],
                Speakers = [new("speaker-1", "Speaker 1")],
                Speech = [new("speech-1", "source", 0, 1000, "Replacement text", ["speaker-1"], "corrected")]
            }, 0);
            var speakerEdits = Path.Combine(testRoot, "cli-speakers.json");
            await File.WriteAllTextAsync(speakerEdits, """[{"action":"rename","speakerId":"speaker-1","label":"Host","reason":"reviewed label"}]""");
            await RunCli("speaker-edit", voiceProjectPath, speakerEdits, "1");
            var voicePlan = Path.Combine(testRoot, "cli-voice-plan.json");
            await File.WriteAllTextAsync(voicePlan, """{"mapping":{"id":"voice-1","speakerId":"speaker-1","provider":"qwen-tts","voice":"aiden","model":"Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice","language":"English"},"replacement":{"id":"replacement-1","segmentId":"speech-1","mappingId":"voice-1","text":"Replacement text","fitPolicy":"exact","backgroundPolicy":"require-isolated-dialogue"}}""");
            await RunCli("voice-plan", voiceProjectPath, voicePlan, "2");
            var wavPath = Path.Combine(testRoot, "qwen-preview.wav");
            await File.WriteAllBytesAsync(wavPath, TestAudio.PcmWave());
            await RunCli("voice-preview-import", voiceProjectPath, "replacement-1", wavPath, "3", "fixture-qwen-runtime");
            var previewPath = Path.Combine(testRoot, "cli-preview.wav");
            await RunCli("voice-preview", voiceProjectPath, "4", "replacement-1", previewPath);
            Assert((await File.ReadAllBytesAsync(previewPath)).SequenceEqual(TestAudio.PcmWave()), "CLI voice preview bytes changed.");
            await RunCli("voice-state", voiceProjectPath, "4", "replacement-1", "applied");
            await RunCli("voice-state", voiceProjectPath, "5", "replacement-1", "reverted");
            Assert((await store.LoadAsync(voiceProjectPath)).Replacements.Single().State == "reverted",
                "CLI voice workflow did not preserve reversible state.");
        });
    }
}

await ExportTests.RunAsync(Check, args, testRoot);
await McpTests.RunAsync(Check, args, testRoot);

Console.WriteLine($"{passed} passed; {failures} failed.");
return failures == 0 ? 0 : 1;

sealed class TestSpeechTranscriber : ILocalSpeechTranscriber
{
    public int MaximumBytes { get; private set; }

    public Task<LocalSpeechResult> TranscribePcm16kMonoAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
    {
        MaximumBytes = Math.Max(MaximumBytes, pcm.Length);
        var duration = pcm.Length * 1000L / (LocalSpeechProcessor.SampleRate * sizeof(short));
        return Task.FromResult(new LocalSpeechResult("test-local", "timed-fixture", "en",
            [new(0, duration, "synthetic speech")]));
    }
}

sealed class TestAcquisitionTool : IAcquisitionTool
{
    public IReadOnlyList<string>? DownloadArguments { get; private set; }

    public async Task<ToolResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Contains("--version")) return new("2026.09.01\n"u8.ToArray(), "");
        DownloadArguments = arguments;
        var index = arguments.IndexOf("--paths");
        var staging = arguments[index + 1];
        await File.WriteAllBytesAsync(Path.Combine(staging, "source.mkv"), "synthetic media"u8.ToArray(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(staging, "source.info.json"),
            "{\"id\":\"fixture-id\",\"title\":\"Fixture title\",\"extractor\":\"fixture\",\"subtitles\":{\"en\":[]}}", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(staging, "source.en.srt"),
            "1\n00:00:00,000 --> 00:00:01,000\nCaption\n", cancellationToken);
        return new([], "");
    }
}

static class ListTestExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++) if (values[index] == value) return index;
        return -1;
    }
}
