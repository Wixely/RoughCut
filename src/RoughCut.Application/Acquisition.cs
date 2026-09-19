using System.Security.Cryptography;
using System.Text.Json;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Application;

public sealed record AcquiredCaption(string Path, string Language, string SourceKind, string Sha256, int CueCount);
public sealed record AcquisitionResult(int SchemaVersion, string Source, string Extractor, string MediaId, string Title,
    string MediaPath, string MediaSha256, string InfoPath, AcquiredCaption[] Captions, string YtDlpVersion);

public interface IAcquisitionTool
{
    Task<ToolResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class AcquisitionTool : IAcquisitionTool
{
    public Task<ToolResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => ToolProcess.RunAsync(executable, arguments, outputLimit: 1024 * 1024,
            timeout: TimeSpan.FromMinutes(15), cancellationToken: cancellationToken);
}

public sealed class YtDlpAcquirer(WorkspaceBoundary workspace, string executable = "yt-dlp", IAcquisitionTool? tool = null)
{
    private const long MaxAggregateBytes = 600L * 1024 * 1024;
    private readonly IAcquisitionTool _tool = tool ?? new AcquisitionTool();

    public async Task<AcquisitionResult> AcquireAsync(string sourceUrl, string destinationDirectory,
        string? denoPath = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Source must be an HTTP(S) URL without embedded credentials.");
        var destination = workspace.Resolve(destinationDirectory, mustExist: false);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Acquisition destination already exists.");
        if (denoPath is not null)
        {
            denoPath = Path.GetFullPath(denoPath);
            if (!File.Exists(denoPath)) throw new FileNotFoundException("Configured Deno executable does not exist.");
        }
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".roughcut-acquire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var arguments = BuildArguments(uri, staging, denoPath);
            await _tool.RunAsync(executable, arguments, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var version = System.Text.Encoding.UTF8.GetString((await _tool.RunAsync(executable, ["--ignore-config", "--version"], cancellationToken)).Output).Trim();
            var result = await InspectAsync(uri, staging, version, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(staging, "acquisition.json"),
                JsonSerializer.Serialize(result, ApplicationJson.Default.AcquisitionResult), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return result;
        }
        finally { Cleanup(staging); }
    }

    internal static string[] BuildArguments(Uri source, string staging, string? denoPath)
    {
        var arguments = new List<string>
        {
            "--ignore-config", "--no-js-runtimes", "--no-remote-components", "--no-playlist", "--max-filesize", "536870912",
            "--no-overwrites", "--no-progress", "--newline", "--write-info-json", "--write-subs", "--write-auto-subs",
            "--sub-langs", "en,-live_chat", "--sub-format", "srt/best", "--convert-subs", "srt",
            "--merge-output-format", "mkv", "--remux-video", "mkv", "--paths", staging,
            "--output", "source.%(ext)s"
        };
        if (denoPath is not null) arguments.AddRange(["--js-runtimes", "deno:" + denoPath]);
        arguments.Add("--");
        arguments.Add(source.AbsoluteUri);
        return arguments.ToArray();
    }

    private static async Task<AcquisitionResult> InspectAsync(Uri source, string staging, string version,
        CancellationToken cancellationToken)
    {
        if (Directory.EnumerateDirectories(staging).Any()) throw new InvalidDataException("Acquisition produced an unexpected directory.");
        var files = Directory.EnumerateFiles(staging).ToArray();
        if (files.Length is 0 or > 64 || files.Sum(file => new FileInfo(file).Length) > MaxAggregateBytes)
            throw new InvalidDataException("Acquisition output exceeded file-count or size limits.");
        var infoPath = Path.Combine(staging, "source.info.json");
        var infoBytes = await ProjectFiles.ReadBoundedAsync(infoPath, 4 * 1024 * 1024, cancellationToken);
        using var info = JsonDocument.Parse(infoBytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = info.RootElement;
        var mediaId = RequiredText(root, "id", 256);
        var title = RequiredText(root, "title", 1024);
        var extractor = RequiredText(root, "extractor", 128);
        var manualLanguages = Languages(root, "subtitles");
        var automaticLanguages = Languages(root, "automatic_captions");
        var media = files.SingleOrDefault(file => Path.GetFileName(file).StartsWith("source.", StringComparison.Ordinal) &&
            !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Acquisition did not produce exactly one media file.");
        var captions = new List<AcquiredCaption>();
        foreach (var caption in files.Where(file => Path.GetFileName(file).StartsWith("source.", StringComparison.Ordinal) &&
            file.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)).OrderBy(file => file, StringComparer.Ordinal))
        {
            var bytes = await ProjectFiles.ReadBoundedAsync(caption, Captions.MaxBytes, cancellationToken);
            var cues = Captions.ParseSrt(new System.Text.UTF8Encoding(false, true).GetString(bytes));
            var name = Path.GetFileNameWithoutExtension(caption);
            var language = name["source.".Length..];
            var sourceKind = manualLanguages.Contains(language) ? "manual" :
                automaticLanguages.Contains(language) ? "automatic" : "supplied";
            captions.Add(new(Path.GetFileName(caption), language, sourceKind, Convert.ToHexStringLower(SHA256.HashData(bytes)), cues.Length));
        }
        var safeSource = new UriBuilder(source) { Query = "", Fragment = "" }.Uri.AbsoluteUri;
        return new(1, safeSource, extractor, mediaId, title, Path.GetFileName(media),
            await MediaReader.FingerprintAsync(media, cancellationToken), "source.info.json", captions.ToArray(), version);
    }

    private static string RequiredText(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } value || string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new InvalidDataException($"Acquisition metadata field '{name}' is missing or invalid.");
        return value;
    }

    private static HashSet<string> Languages(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
            return new(StringComparer.OrdinalIgnoreCase);
        return property.EnumerateObject().Select(item => item.Name).Where(language => language.Length <= 35)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void Cleanup(string staging)
    {
        if (!Directory.Exists(staging)) return;
        foreach (var file in Directory.EnumerateFiles(staging)) File.Delete(file);
        if (!Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging);
    }
}
