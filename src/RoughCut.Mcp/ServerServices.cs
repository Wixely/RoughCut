using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using RoughCut.Application;
using RoughCut.Core;

namespace RoughCut.Mcp;

/// What the server is, independent of how it is spoken to. The estate's hub manages this server over HTTP on
/// its own port, and a person or a test harness can still run it over stdio; both reach exactly the same
/// tools with exactly the same settings, so a workflow proved one way holds the other.
internal static class ServerServices
{
    /// The port this server answers on when nothing says otherwise. Registered in the estate's port list;
    /// 5700 through 5721 belong to other servers.
    public const int DefaultPort = 5722;

    /// The estate names a product, an environment-variable prefix and a log file prefix together. The prefix
    /// is deliberately not `ROUGHCUT_`: that already names this project's tool locations — `ROUGHCUT_FFMPEG`
    /// and the rest — which are read directly rather than through the configuration chain.
    public const string ProductName = "RoughCutMCPSharp";
    public const string EnvironmentPrefix = "ROUGHCUTMCP_";
    public const string LogFilePrefix = "roughcutmcp";

    /// Where a caller's projects and media live. Everything this server touches is inside it: absolute paths,
    /// `..` and reparse points are refused. Named on the command line, in the environment, or in the server's
    /// own configuration file, in that order.
    public static string ResolveWorkspace(string[] args, IConfiguration? configuration = null)
    {
        var workspace = GetOption(args, "--workspace")
            ?? Environment.GetEnvironmentVariable("ROUGHCUT_WORKSPACE")
            ?? configuration?["RoughCut:Workspace"];
        if (string.IsNullOrWhiteSpace(workspace))
            throw new ArgumentException(
                "RoughCut MCP requires a workspace: pass --workspace <directory>, set ROUGHCUT_WORKSPACE, " +
                "or set RoughCut:Workspace in " + ProductName + ".json.");
        return workspace;
    }

    /// Everything the tools need, registered the same way whichever transport is serving them.
    public static void Register(IServiceCollection services, string workspace)
    {
        var boundary = new WorkspaceBoundary(workspace);
        var tools = ToolSettings.Default;
        services.AddSingleton(boundary);
        services.AddSingleton(new RoughCutOperations(boundary, tools.Ffmpeg, tools.Ffprobe, tools.YtDlp));
        services.AddSingleton(new ExportJobManager(boundary, tools.Ffmpeg, tools.Ffprobe));
        services.AddSingleton(SpeechSettingsFromEnvironment());
        services.AddSingleton(DiarizationSettingsFromEnvironment());
        services.AddSingleton(QwenSettingsFromEnvironment());
    }

    /// The tool payloads are source-generated, so the schema builder is told about them: without this a
    /// reflection-free build advertises tools whose results serialise to nothing.
    public static JsonSerializerOptions ToolSchema()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Insert(0, ProjectJson.Default);
        return options;
    }

    private static SpeechSettings SpeechSettingsFromEnvironment()
    {
        // Without an explicit path the model lives with RoughCut's own per-user state, so a caller who has
        // fetched it once needs no configuration. Transcription refuses when the file is absent rather than
        // downloading it unannounced.
        var model = Environment.GetEnvironmentVariable("ROUGHCUT_STT_MODEL") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RoughCut", "models", "ggml-base.en.bin");
        var language = Environment.GetEnvironmentVariable("ROUGHCUT_STT_LANGUAGE") ?? "en";
        var chunkSeconds = int.TryParse(Environment.GetEnvironmentVariable("ROUGHCUT_STT_CHUNK_SECONDS"), out var configuredChunk)
            ? configuredChunk : LocalSpeechProcessor.DefaultChunkSeconds;
        return new(model, language, chunkSeconds);
    }

    private static DiarizationSettings DiarizationSettingsFromEnvironment()
    {
        var speakerCount = int.TryParse(Environment.GetEnvironmentVariable("ROUGHCUT_DIARIZATION_SPEAKER_COUNT"), out var configuredCount)
            ? configuredCount : 0;
        var threshold = float.TryParse(Environment.GetEnvironmentVariable("ROUGHCUT_DIARIZATION_THRESHOLD"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var configuredThreshold)
            ? configuredThreshold : 0.5f;
        return new(Environment.GetEnvironmentVariable("ROUGHCUT_DIARIZATION_SEGMENTATION_MODEL"),
            Environment.GetEnvironmentVariable("ROUGHCUT_DIARIZATION_EMBEDDING_MODEL"),
            Environment.GetEnvironmentVariable("ROUGHCUT_DIARIZATION_WORKER") ??
                Path.Combine(AppContext.BaseDirectory, "roughcut-diarization.dll"),
            speakerCount, threshold);
    }

    private static QwenSettings QwenSettingsFromEnvironment()
    {
        var timeout = int.TryParse(Environment.GetEnvironmentVariable("ROUGHCUT_QWEN_TIMEOUT_SECONDS"), out var configured)
            ? configured : 600;
        return new(Environment.GetEnvironmentVariable("ROUGHCUT_QWEN_ENDPOINT"),
            Environment.GetEnvironmentVariable("ROUGHCUT_QWEN_API_KEY"), timeout);
    }

    private static string? GetOption(string[] values, string name)
    {
        for (var index = 0; index < values.Length; index++)
            if (string.Equals(values[index], name, StringComparison.OrdinalIgnoreCase))
                return index + 1 < values.Length ? values[index + 1] : throw new ArgumentException(name + " requires a value.");
        return null;
    }
}
