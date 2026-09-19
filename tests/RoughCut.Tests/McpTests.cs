using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RoughCut.Application;
using RoughCut.Core;

internal static class McpTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string[] args, string root)
    {
        var option = Array.IndexOf(args, "--mcp");
        if (option < 0) return;
        if (option + 1 >= args.Length) throw new ArgumentException("--mcp requires an executable or DLL path.");
        var server = Path.GetFullPath(args[option + 1]);
        var command = server.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : server;
        var arguments = server.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new[] { server, "--workspace", root }
            : new[] { "--workspace", root };

        await check("MCP stdio lists the bounded RoughCut tool surface", async () =>
        {
            await using var client = await CreateClientAsync(command, arguments);
            var tools = await client.ListToolsAsync();
            var names = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            string[] expected = ["roughcut_read_project", "roughcut_inspect_video", "roughcut_create_project",
                "roughcut_get_frame", "roughcut_get_timeline_frame", "roughcut_apply_edits", "roughcut_import_captions", "roughcut_select_captions",
                "roughcut_save_analysis", "roughcut_apply_analysis",
                "roughcut_edit_speakers", "roughcut_plan_voice_replacement", "roughcut_import_voice_preview",
                "roughcut_synthesize_voice", "roughcut_get_voice_preview", "roughcut_set_voice_replacement_state",
                "roughcut_import_image", "roughcut_acquire_url", "roughcut_preflight_export",
                "roughcut_transcribe_local", "roughcut_start_export", "roughcut_get_job", "roughcut_cancel_job"];
            Assert(expected.All(names.Contains), "MCP tool list is incomplete.");
        });

        await check("MCP corrects speakers and carries a reversible voice preview", async () =>
        {
            var path = Path.Combine(root, "mcp-voice-project.json");
            var project = new EditProject
            {
                ProjectId = "mcp-voice",
                TimeBase = new(1, 1000),
                Assets = [new("source", "video", "source.mkv", new string('a', 64), 3000, 160, 96, "video/x-matroska")],
                Timeline = [new("clip", "source", 0, 3000)],
                Speakers = [new("speaker-1", "Speaker 1")],
                Speech = [new("speech-1", "source", 0, 1000, "Replacement text", ["speaker-1"], "corrected")]
            };
            await new ProjectStore().SaveAsync(path, project, 0);
            using var qwenServer = new TestQwenServer(TestAudio.PcmWave());
            var previousQwenEndpoint = Environment.GetEnvironmentVariable("ROUGHCUT_QWEN_ENDPOINT");
            McpClient client;
            Environment.SetEnvironmentVariable("ROUGHCUT_QWEN_ENDPOINT", qwenServer.Endpoint);
            try { client = await CreateClientAsync(command, arguments); }
            finally { Environment.SetEnvironmentVariable("ROUGHCUT_QWEN_ENDPOINT", previousQwenEndpoint); }
            await using var configuredClient = client;
            var corrected = await client.CallToolAsync("roughcut_edit_speakers", new Dictionary<string, object?>
            {
                ["projectPath"] = "mcp-voice-project.json",
                ["expectedRevision"] = 1L,
                ["edits"] = JsonDocument.Parse("""[{"action":"rename","speakerId":"speaker-1","label":"Host","reason":"reviewed label"}]""").RootElement.Clone()
            });
            Assert(corrected.IsError != true, "MCP speaker correction failed.");
            var planned = await client.CallToolAsync("roughcut_plan_voice_replacement", new Dictionary<string, object?>
            {
                ["projectPath"] = "mcp-voice-project.json",
                ["expectedRevision"] = 2L,
                ["submission"] = JsonDocument.Parse("""{"mapping":{"id":"voice-1","speakerId":"speaker-1","provider":"qwen-tts","voice":"aiden","model":"Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice","language":"English"},"replacement":{"id":"replacement-1","segmentId":"speech-1","mappingId":"voice-1","text":"Replacement text","fitPolicy":"exact","backgroundPolicy":"require-isolated-dialogue"}}""").RootElement.Clone()
            });
            Assert(planned.IsError != true, "MCP voice plan failed.");
            var imported = await client.CallToolAsync("roughcut_synthesize_voice", new Dictionary<string, object?>
            {
                ["projectPath"] = "mcp-voice-project.json",
                ["expectedRevision"] = 3L,
                ["replacementId"] = "replacement-1"
            });
            Assert(imported.IsError != true && qwenServer.StatusRequests == 1 && qwenServer.SynthesisRequests == 1,
                "MCP live Qwen synthesis failed.");
            var preview = await client.CallToolAsync("roughcut_get_voice_preview", new Dictionary<string, object?>
            {
                ["projectPath"] = "mcp-voice-project.json",
                ["expectedRevision"] = 4L,
                ["replacementId"] = "replacement-1"
            });
            Assert(preview.IsError != true && preview.Content.OfType<AudioContentBlock>().Single().DecodedData.Span.SequenceEqual(TestAudio.PcmWave()),
                "MCP did not return decodable voice preview audio.");
            var applied = await client.CallToolAsync("roughcut_set_voice_replacement_state", new Dictionary<string, object?>
            {
                ["projectPath"] = "mcp-voice-project.json",
                ["expectedRevision"] = 4L,
                ["replacementId"] = "replacement-1",
                ["state"] = "applied"
            });
            Assert(applied.IsError != true, "MCP voice preview apply failed.");
            var reverted = await client.CallToolAsync("roughcut_set_voice_replacement_state", new Dictionary<string, object?>
            {
                ["projectPath"] = "mcp-voice-project.json",
                ["expectedRevision"] = 5L,
                ["replacementId"] = "replacement-1",
                ["state"] = "reverted"
            });
            Assert(reverted.IsError != true, "MCP voice replacement revert failed.");
        });

        await check("MCP returns a decodable frame image and exact timing metadata", async () =>
        {
            await using var client = await CreateClientAsync(command, arguments);
            var result = await client.CallToolAsync("roughcut_get_frame", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["assetId"] = "source-1",
                ["timestampTicks"] = 350L,
                ["timeBaseNumerator"] = 1L,
                ["timeBaseDenominator"] = 1000L,
                ["maxWidth"] = 1280
            });
            Assert(result.IsError != true, "Frame tool returned an error.");
            var image = result.Content.OfType<ImageContentBlock>().Single();
            Assert(image.MimeType == "image/png" && image.DecodedData.Span[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "MCP client did not receive a PNG image content block.");
            var metadata = result.Content.OfType<TextContentBlock>().Single().Text;
            using var document = JsonDocument.Parse(metadata);
            var requested = document.RootElement.GetProperty("requested");
            var actual = document.RootElement.GetProperty("actual");
            var requestedBase = requested.GetProperty("timeBase");
            var actualBase = actual.GetProperty("timeBase");
            var requestedSeconds = (decimal)requested.GetProperty("ticks").GetInt64() * requestedBase.GetProperty("numerator").GetInt64() / requestedBase.GetProperty("denominator").GetInt64();
            var actualSeconds = (decimal)actual.GetProperty("ticks").GetInt64() * actualBase.GetProperty("numerator").GetInt64() / actualBase.GetProperty("denominator").GetInt64();
            Assert(requestedSeconds == 0.35m && actualSeconds == 0.3m,
                "Frame timing metadata does not preserve requested/actual times.");
            var imported = await client.CallToolAsync("roughcut_import_image", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["assetId"] = "generated-still",
                ["base64Png"] = Convert.ToBase64String(image.DecodedData.Span),
                ["expectedRevision"] = 2L,
                ["provider"] = "fixture-agent",
                ["modelVersion"] = "synthetic"
            });
            Assert(imported.IsError != true, "MCP image import failed.");
            using var importedProject = JsonDocument.Parse(imported.Content.OfType<TextContentBlock>().Single().Text);
            var asset = importedProject.RootElement.GetProperty("assets").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "generated-still");
            Assert(asset.GetProperty("kind").GetString() == "image" && asset.GetProperty("path").GetString()!.StartsWith("assets/images/", StringComparison.Ordinal),
                "Imported MCP image is not a portable project asset.");
            Assert(File.Exists(Path.Combine(root, asset.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar))),
                "Imported image file was not saved.");
            var invalid = await client.CallToolAsync("roughcut_import_image", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["assetId"] = "invalid-still",
                ["base64Png"] = "not-base64",
                ["expectedRevision"] = 3L
            });
            Assert(invalid.IsError == true, "Malformed incoming image was accepted.");
            var inserted = await client.CallToolAsync("roughcut_apply_edits", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["expectedRevision"] = 3L,
                ["edits"] = JsonDocument.Parse("""[{"action":"insert-image","clipId":"generated-hold","assetId":"generated-still","duration":1000,"beforeClipId":"clip-1","fit":"contain"},{"action":"export-mode","mode":"prefer-stream-copy"}]""").RootElement.Clone()
            });
            Assert(inserted.IsError != true, "MCP timed-image insertion failed.");
            var timeline = await client.CallToolAsync("roughcut_get_timeline_frame", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["expectedRevision"] = 4L,
                ["timelineTicks"] = 1500L,
                ["maxWidth"] = 1280
            });
            Assert(timeline.IsError != true && timeline.Content.OfType<ImageContentBlock>().Single().DecodedData.Span.SequenceEqual(image.DecodedData.Span),
                "MCP timeline preview did not return the inserted image pixels.");
            using var timelineMetadata = JsonDocument.Parse(timeline.Content.OfType<TextContentBlock>().Single().Text);
            Assert(timelineMetadata.RootElement.GetProperty("revision").GetInt64() == 4 &&
                timelineMetadata.RootElement.GetProperty("clipId").GetString() == "generated-hold", "MCP timeline preview metadata is not revision-aware.");
            var staleTimeline = await client.CallToolAsync("roughcut_get_timeline_frame", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["expectedRevision"] = 3L,
                ["timelineTicks"] = 1500L,
                ["maxWidth"] = 1280
            });
            Assert(staleTimeline.IsError == true, "MCP timeline preview accepted a stale revision.");
        });

        await check("MCP enforces workspace paths and project revisions", async () =>
        {
            await using var client = await CreateClientAsync(command, arguments);
            var escaped = await client.CallToolAsync("roughcut_inspect_video", new Dictionary<string, object?> { ["mediaPath"] = "../outside.mkv" });
            Assert(escaped.IsError == true, "Workspace escape was accepted.");
            var stale = await client.CallToolAsync("roughcut_apply_edits", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["expectedRevision"] = 0L,
                ["edits"] = JsonDocument.Parse("[]").RootElement.Clone()
            });
            Assert(stale.IsError == true && stale.Content.OfType<TextContentBlock>().Single().Text.Contains("revision conflict", StringComparison.OrdinalIgnoreCase),
                "Stale MCP edit was not reported as a revision conflict.");
            var unconfiguredSpeech = await client.CallToolAsync("roughcut_transcribe_local", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["assetId"] = "source-1",
                ["expectedRevision"] = 4L
            });
            Assert(unconfiguredSpeech.IsError == true && unconfiguredSpeech.Content.OfType<TextContentBlock>().Single().Text.Contains("ROUGHCUT_STT_MODEL", StringComparison.Ordinal),
                "Unconfigured local STT did not return an actionable error.");
        });

        await check("MCP caption assessment recommends and records a source", async () =>
        {
            await File.WriteAllTextAsync(Path.Combine(root, "manual-en.srt"), "1\n00:00:00,000 --> 00:00:01,000\nManual text\n");
            await File.WriteAllTextAsync(Path.Combine(root, "automatic-en.srt"), "1\n00:00:00,000 --> 00:00:02,000\nAutomatic text\n");
            await using var client = await CreateClientAsync(command, arguments);
            var selected = await client.CallToolAsync("roughcut_select_captions", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["assetId"] = "source-1",
                ["expectedRevision"] = 4L,
                ["preferredLanguage"] = "en",
                ["candidates"] = JsonDocument.Parse("""[{"id":"automatic","path":"automatic-en.srt","sourceKind":"automatic","language":"en"},{"id":"manual","path":"manual-en.srt","sourceKind":"manual","language":"en"}]""").RootElement.Clone()
            });
            Assert(selected.IsError != true, "MCP caption selection failed.");
            using var response = JsonDocument.Parse(selected.Content.OfType<TextContentBlock>().Single().Text);
            Assert(response.RootElement.GetProperty("selectedId").GetString() == "manual" &&
                response.RootElement.GetProperty("project").GetProperty("captions").GetProperty("selection").GetString() == "recommended",
                "MCP caption recommendation or provenance is incorrect.");
        });

        await check("MCP persists evidence-backed analysis and retains review decisions", async () =>
        {
            await using var client = await CreateClientAsync(command, arguments);
            var planned = await client.CallToolAsync("roughcut_save_analysis", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["expectedRevision"] = 5L,
                ["prompt"] = "Remove advertisements",
                ["policy"] = "review",
                ["submission"] = JsonDocument.Parse("""{"assetId":"source-1","provider":"fixture-agent","model":"labelled-v1","evidence":[{"id":"evidence-1","assetId":"source-1","start":0,"end":500,"kind":"frame","summary":"Possible interruption.","speechSegmentIds":[],"frameTicks":[100]}],"observations":[{"id":"observation-1","assetId":"source-1","start":0,"end":500,"label":"possible-ad","summary":"Uncertain material requires review.","certainty":"low","recommendedAction":"remove","evidenceIds":["evidence-1"]}]}""").RootElement.Clone()
            });
            Assert(planned.IsError != true, "MCP analysis planning failed.");
            using var response = JsonDocument.Parse(planned.Content.OfType<TextContentBlock>().Single().Text);
            Assert(response.RootElement.GetProperty("removeDecisions").GetInt32() == 0 &&
                response.RootElement.GetProperty("reviewDecisions").GetInt32() == 1 &&
                response.RootElement.GetProperty("project").GetProperty("revision").GetInt64() == 6,
                "MCP review policy did not retain uncertain material.");
            var apply = await client.CallToolAsync("roughcut_apply_analysis", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["expectedRevision"] = 6L
            });
            Assert(apply.IsError == true && apply.Content.OfType<TextContentBlock>().Single().Text.Contains("No removal proposals", StringComparison.Ordinal),
                "MCP automatically applied a proposal requiring review.");
        });

        await check("MCP image export jobs complete, persist and cancel safely", async () =>
        {
            await using var client = await CreateClientAsync(command, arguments);
            var completed = ReadJob(await client.CallToolAsync("roughcut_start_export", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["outputDirectory"] = "mcp-image-export",
                ["allowEncoding"] = true
            }));
            for (var attempt = 0; attempt < 400; attempt++)
            {
                await Task.Delay(25);
                completed = ReadJob(await client.CallToolAsync("roughcut_get_job", new Dictionary<string, object?> { ["jobId"] = completed.JobId }));
                if (completed.Status is "cancelled" or "failed" or "succeeded") break;
            }
            Assert(completed.Status == "succeeded" && File.Exists(Path.Combine(root, "mcp-image-export", "video.mkv")),
                $"MCP timed-image export did not publish a validated bundle: {completed.Status}: {completed.Message}");
            var started = await client.CallToolAsync("roughcut_start_export", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["outputDirectory"] = "mcp-cancelled-export",
                ["allowEncoding"] = true
            });
            var job = ReadJob(started);
            Assert(job.Status is "queued" or "running", "Export job did not start.");
            await client.CallToolAsync("roughcut_cancel_job", new Dictionary<string, object?> { ["jobId"] = job.JobId });
            for (var attempt = 0; attempt < 200; attempt++)
            {
                await Task.Delay(25);
                job = ReadJob(await client.CallToolAsync("roughcut_get_job", new Dictionary<string, object?> { ["jobId"] = job.JobId }));
                if (job.Status is "cancelled" or "failed" or "succeeded") break;
            }
            Assert(job.Status == "cancelled", "Export cancellation did not reach a durable cancelled state.");
            Assert(File.Exists(Path.Combine(root, ".roughcut", "jobs", job.JobId + ".json")), "Durable job checkpoint is missing.");
            Assert(!Directory.Exists(Path.Combine(root, "mcp-cancelled-export")), "Cancelled MCP export was published.");
            using var reloaded = new ExportJobManager(new WorkspaceBoundary(root));
            Assert(reloaded.Get(job.JobId).Status == "cancelled", "Job checkpoint did not reload with its terminal state.");
        });
    }

    private static Task<McpClient> CreateClientAsync(string command, string[] arguments)
        => McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "RoughCut MCP tests",
            Command = command,
            Arguments = arguments,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            ShutdownTimeout = TimeSpan.FromSeconds(10)
        }));

    private static ExportJob ReadJob(CallToolResult result)
    {
        Assert(result.IsError != true, "Job tool returned an error.");
        return JsonSerializer.Deserialize(result.Content.OfType<TextContentBlock>().Single().Text, ApplicationJson.Default.ExportJob)
            ?? throw new Exception("Job response was empty.");
    }
}
