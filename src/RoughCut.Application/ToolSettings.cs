using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoughCut.Application;

public sealed record ToolPaths
{
    public int SchemaVersion { get; init; } = 1;
    public string? Ffmpeg { get; init; }
    public string? Ffprobe { get; init; }
    public string? YtDlp { get; init; }
    public string? Deno { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ToolPaths))]
internal partial class ToolPathsJson : JsonSerializerContext;

public sealed record ResolvedTool(string Name, string Value, string Source, bool Missing);

/// External executables are machine configuration, never project data. Each is resolved from an explicit
/// argument, then an environment variable, then a settings file, and otherwise left as a bare name for PATH.
public sealed class ToolSettings
{
    private const int MaxSettingsBytes = 64 * 1024;

    private readonly ToolPaths _file;

    public ToolSettings(ToolPaths? file = null, string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? DefaultSettingsPath();
        _file = file ?? Read(SettingsPath);
    }

    /// Process-wide configuration, read once. Tests construct their own instances instead.
    public static ToolSettings Default { get; } = new();

    public string SettingsPath { get; }

    public string Ffmpeg => Resolve("ROUGHCUT_FFMPEG", _file.Ffmpeg, "ffmpeg");
    public string Ffprobe => Resolve("ROUGHCUT_FFPROBE", _file.Ffprobe, "ffprobe");
    public string YtDlp => Resolve("ROUGHCUT_YTDLP", _file.YtDlp, "yt-dlp");

    /// Null means "let yt-dlp find Deno itself", which it does on PATH or beside its own executable.
    public string? Deno => Optional("ROUGHCUT_DENO", _file.Deno);

    /// Where each executable comes from and whether a configured path is actually present, for diagnostics.
    public ResolvedTool[] Describe() =>
    [
        Describe("ffmpeg", "ROUGHCUT_FFMPEG", _file.Ffmpeg, "ffmpeg"),
        Describe("ffprobe", "ROUGHCUT_FFPROBE", _file.Ffprobe, "ffprobe"),
        Describe("yt-dlp", "ROUGHCUT_YTDLP", _file.YtDlp, "yt-dlp"),
        Describe("deno", "ROUGHCUT_DENO", _file.Deno, null)
    ];

    public static string DefaultSettingsPath()
    {
        if (Environment.GetEnvironmentVariable("ROUGHCUT_TOOLS") is { Length: > 0 } configured)
            return Path.GetFullPath(configured);
        var besideExecutable = Path.Combine(AppContext.BaseDirectory, "roughcut.tools.json");
        if (File.Exists(besideExecutable)) return besideExecutable;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RoughCut", "tools.json");
    }

    // A damaged or unreadable settings file must not stop a host starting; PATH still applies.
    private static ToolPaths Read(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is 0 or > MaxSettingsBytes) return new();
            return JsonSerializer.Deserialize(File.ReadAllText(path), ToolPathsJson.Default.ToolPaths) ?? new();
        }
        catch (Exception exception) when (exception is IOException or JsonException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new();
        }
    }

    private static string Resolve(string variable, string? configured, string fallback) =>
        Optional(variable, configured) ?? fallback;

    private static string? Optional(string variable, string? configured)
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnvironment) return fromEnvironment;
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private static ResolvedTool Describe(string name, string variable, string? configured, string? fallback)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(variable);
        var (value, source) =
            !string.IsNullOrEmpty(fromEnvironment) ? (fromEnvironment, variable) :
            !string.IsNullOrWhiteSpace(configured) ? (configured, "settings file") :
            fallback is not null ? (fallback, "PATH") : ("(yt-dlp default)", "yt-dlp");
        // Only a configured absolute path can be checked; a bare name is PATH's business.
        var missing = source is not ("PATH" or "yt-dlp") && !File.Exists(Path.GetFullPath(value));
        return new(name, value, source, missing);
    }
}
