using DnaX.MCPFab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoughCut.Application;
using RoughCut.Mcp;

// Two ways in, one server. MCPHub manages this process over HTTP on its own port and probes /healthz, the way
// it manages every other server in the estate; `--stdio` keeps the transport a client launches directly,
// which is how an agent host without a hub, and this repository's own checks, speak to it.
return args.Contains("--stdio", StringComparer.OrdinalIgnoreCase)
    ? await RunStdioAsync(args)
    : await RunHttpAsync(args);

// The shared host: configuration chain, Kestrel binding, Windows service detection, Serilog, authentication,
// /healthz, the banner and the shutdown path all come from MCPFab, so this server behaves like its siblings
// rather than inventing its own version of each.
async Task<int> RunHttpAsync(string[] arguments)
{
    try
    {
        var builder = McpFabHost.CreateBuilder(arguments, new McpFabProduct(
            ServerServices.ProductName, ServerServices.EnvironmentPrefix,
            DefaultPort: ServerServices.DefaultPort, LogFilePrefix: ServerServices.LogFilePrefix));

        var workspace = ServerServices.ResolveWorkspace(arguments, builder.Configuration);
        ServerServices.Register(builder.Services, workspace);
        builder.Mcp.WithTools<RoughCutTools>(ServerServices.ToolSchema());

        builder.Banner((services, banner) =>
        {
            var tools = ToolSettings.Default;
            banner.Line("Workspace", workspace);
            banner.Line("FFmpeg", tools.Ffmpeg);
            banner.Line("yt-dlp", tools.YtDlp);
            var speech = services.GetRequiredService<SpeechSettings>();
            banner.Line("Speech model", File.Exists(speech.ModelPath ?? "")
                ? speech.ModelPath! : (speech.ModelPath ?? "(none)") + " (absent - call roughcut_fetch_speech_model)");
        });

        builder.Health(services =>
        {
            // What a hub can act on: whether the tools this server fronts can actually run. A missing media
            // tool is the difference between a server that answers and one that answers usefully.
            var tools = ToolSettings.Default;
            var speech = services.GetRequiredService<SpeechSettings>();
            return new System.Text.Json.Nodes.JsonObject
            {
                ["workspace"] = workspace,
                ["workspaceExists"] = Directory.Exists(workspace),
                ["ffmpeg"] = File.Exists(tools.Ffmpeg) || Path.GetFileName(tools.Ffmpeg) == tools.Ffmpeg,
                ["ffprobe"] = File.Exists(tools.Ffprobe) || Path.GetFileName(tools.Ffprobe) == tools.Ffprobe,
                ["ytDlp"] = File.Exists(tools.YtDlp) || Path.GetFileName(tools.YtDlp) == tools.YtDlp,
                ["speechModel"] = File.Exists(speech.ModelPath ?? "")
            };
        });

        return await builder.Build().RunAsync();
    }
    catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
}

// The transport a client launches and owns. Nothing may be written to standard output but the JSON-RPC
// stream, so logging is cleared rather than merely quietened.
async Task<int> RunStdioAsync(string[] arguments)
{
    try
    {
        var workspace = ServerServices.ResolveWorkspace(arguments);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = arguments });
        builder.Logging.ClearProviders();
        ServerServices.Register(builder.Services, workspace);
        builder.Services.AddMcpServer().WithStdioServerTransport()
            .WithTools<RoughCutTools>(ServerServices.ToolSchema());
        await builder.Build().RunAsync();
        return 0;
    }
    catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
}
