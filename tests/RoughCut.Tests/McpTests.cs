using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RoughCut.Application;

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
                "roughcut_get_frame", "roughcut_apply_edits", "roughcut_import_captions", "roughcut_import_image", "roughcut_preflight_export",
                "roughcut_start_export", "roughcut_get_job", "roughcut_cancel_job"];
            Assert(expected.All(names.Contains), "MCP tool list is incomplete.");
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
        });

        await check("MCP export jobs persist checkpoints and cancel without publishing", async () =>
        {
            await using var client = await CreateClientAsync(command, arguments);
            var started = await client.CallToolAsync("roughcut_start_export", new Dictionary<string, object?>
            {
                ["projectPath"] = "export-project.json",
                ["outputDirectory"] = "mcp-cancelled-export",
                ["allowEncoding"] = false
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
