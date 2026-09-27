using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoughCut.Application;
using RoughCut.Core;
using RoughCut.Mcp;

var workspace = GetOption(args, "--workspace") ?? Environment.GetEnvironmentVariable("ROUGHCUT_WORKSPACE");
if (string.IsNullOrWhiteSpace(workspace))
{
    Console.Error.WriteLine("RoughCut MCP requires --workspace <directory> or ROUGHCUT_WORKSPACE.");
    return 2;
}

try
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args });
    builder.Logging.ClearProviders();
    var boundary = new WorkspaceBoundary(workspace);
    var tools = ToolSettings.Default;
    var ffmpeg = tools.Ffmpeg;
    var ffprobe = tools.Ffprobe;
    var ytDlp = tools.YtDlp;
    // Without an explicit path the model lives with RoughCut's own per-user state. The transcriber
    // downloads and verifies it against its pinned size and hash on first use, so a caller that has never
    // configured anything can still transcribe rather than being told it cannot.
    var speechModel = Environment.GetEnvironmentVariable("ROUGHCUT_STT_MODEL") is { Length: > 0 } configuredModel
        ? configuredModel
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RoughCut", "models", "ggml-base.en.bin");
    var speechLanguage = Environment.GetEnvironmentVariable("ROUGHCUT_STT_LANGUAGE") ?? "en";
    var speechChunkSeconds = int.TryParse(Environment.GetEnvironmentVariable("ROUGHCUT_STT_CHUNK_SECONDS"), out var configuredChunk)
        ? configuredChunk : LocalSpeechProcessor.DefaultChunkSeconds;
    var diarizationSpeakerCount = int.TryParse(Environment.GetEnvironmentVariable("ROUGHCUT_DIARIZATION_SPEAKER_COUNT"), out var configuredSpeakerCount)
        ? configuredSpeakerCount : 0;
    var diarizationThreshold = float.TryParse(Environment.GetEnvironmentVariable("ROUGHCUT_DIARIZATION_THRESHOLD"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var configuredThreshold)
        ? configuredThreshold : 0.5f;
    var qwenTimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("ROUGHCUT_QWEN_TIMEOUT_SECONDS"), out var configuredQwenTimeout)
        ? configuredQwenTimeout : 600;
    builder.Services.AddSingleton(boundary);
    builder.Services.AddSingleton(new RoughCutOperations(boundary, ffmpeg, ffprobe, ytDlp));
    builder.Services.AddSingleton(new ExportJobManager(boundary, ffmpeg, ffprobe));
    builder.Services.AddSingleton(new SpeechSettings(speechModel, speechLanguage, speechChunkSeconds));
    builder.Services.AddSingleton(new DiarizationSettings(
        Environment.GetEnvironmentVariable("ROUGHCUT_DIARIZATION_SEGMENTATION_MODEL"),
        Environment.GetEnvironmentVariable("ROUGHCUT_DIARIZATION_EMBEDDING_MODEL"),
        Environment.GetEnvironmentVariable("ROUGHCUT_DIARIZATION_WORKER") ??
            Path.Combine(AppContext.BaseDirectory, "roughcut-diarization.dll"),
        diarizationSpeakerCount, diarizationThreshold));
    builder.Services.AddSingleton(new QwenSettings(Environment.GetEnvironmentVariable("ROUGHCUT_QWEN_ENDPOINT"),
        Environment.GetEnvironmentVariable("ROUGHCUT_QWEN_API_KEY"), qwenTimeoutSeconds));
    var toolJson = new System.Text.Json.JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
    toolJson.TypeInfoResolverChain.Insert(0, ProjectJson.Default);
    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<RoughCutTools>(toolJson);
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

static string? GetOption(string[] values, string name)
{
    for (var i = 0; i < values.Length; i++)
        if (string.Equals(values[i], name, StringComparison.OrdinalIgnoreCase))
            return i + 1 < values.Length ? values[i + 1] : throw new ArgumentException(name + " requires a value.");
    return null;
}
