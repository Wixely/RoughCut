using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Application;

public sealed record AcquiredCaption(string Path, string Language, string SourceKind, string Sha256, int CueCount);
public sealed record AcquisitionResult(int SchemaVersion, string Source, string Extractor, string MediaId, string Title,
    string MediaPath, string MediaSha256, string InfoPath, AcquiredCaption[] Captions, string YtDlpVersion,
    // Absent in manifests written before the caller could choose a rendition.
    FormatChoice? Format = null);

public interface IAcquisitionTool
{
    /// `onOutputLine` sees each line the tool writes as it writes it, so a caller can follow a download
    /// that takes minutes. It is optional: an implementation that cannot stream simply ignores it.
    Task<ToolResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, Action<string>? onOutputLine = null);
}

public sealed class AcquisitionTool : IAcquisitionTool
{
    public Task<ToolResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, Action<string>? onOutputLine = null)
        => ToolProcess.RunAsync(executable, arguments, outputLimit: 1024 * 1024,
            timeout: TimeSpan.FromMinutes(15), cancellationToken: cancellationToken, onOutputLine: onOutputLine);
}

public sealed partial class YtDlpAcquirer(WorkspaceBoundary workspace, string executable = "yt-dlp", IAcquisitionTool? tool = null)
{
    private const long MaxAggregateBytes = 600L * 1024 * 1024;
    /// What one download may fetch. Selection is held to this before anything starts, so the size bound
    /// refuses a rendition rather than aborting a download that has already written most of a file.
    public const long MaxDownloadBytes = 512L * 1024 * 1024;
    private readonly IAcquisitionTool _tool = tool ?? new AcquisitionTool();

    /// One line per progress update, prefixed so it cannot be confused with anything else yt-dlp writes.
    /// A download of separate video and audio renditions runs this to 100% once per stream, and the merge
    /// that follows reports nothing, so a caller should treat the number as "how far through the current
    /// file" rather than "how far through the job".
    public const string ProgressTemplate = "download:roughcut-progress %(progress._percent_str)s";

    [GeneratedRegex(@"roughcut-progress\s+([0-9]+(\.[0-9]+)?)%")]
    private static partial Regex ProgressPattern();

    private static void Report(string line, IProgress<double> progress)
    {
        var match = ProgressPattern().Match(line);
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var percent))
            progress.Report(Math.Clamp(percent / 100, 0, 1));
    }

    /// What the source offers, without downloading any of it. The caller picks from this, by identifier or
    /// by one of the named policies.
    public async Task<SourceFormatList> ListFormatsAsync(string sourceUrl, string? denoPath = null,
        CancellationToken cancellationToken = default)
    {
        var uri = ValidatedSource(sourceUrl);
        denoPath = ValidatedDeno(denoPath);
        var arguments = new List<string>
        {
            "--ignore-config", "--no-remote-components", "--no-playlist", "--skip-download", "--dump-single-json"
        };
        if (denoPath is not null) arguments.AddRange(["--js-runtimes", "deno:" + denoPath]);
        arguments.AddRange(["--", uri.AbsoluteUri]);
        var result = await _tool.RunAsync(executable, arguments, cancellationToken);
        using var document = JsonDocument.Parse(result.Output, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var formats = new List<SourceFormat>();
        if (root.TryGetProperty("formats", out var listed) && listed.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in listed.EnumerateArray().Take(400))
            {
                var id = Text(format, "format_id");
                if (id is null || id.Length > 32) continue;
                var video = Text(format, "vcodec") ?? "none";
                var audio = Text(format, "acodec") ?? "none";
                // Storyboards and other pictureless, soundless entries are not renditions of the media.
                if (video == "none" && audio == "none") continue;
                var kind = video != "none" && audio != "none" ? "muxed" : video != "none" ? "video" : "audio";
                formats.Add(new(id, Text(format, "ext") ?? "", kind, Number(format, "width"), Number(format, "height"),
                    Decimal(format, "fps"), video, audio, Decimal(format, "tbr"),
                    (long)Math.Max(Decimal(format, "filesize"), Decimal(format, "filesize_approx"))));
            }
        }
        if (formats.Count == 0) throw new InvalidDataException("The source declared no downloadable formats.");
        var safeSource = new UriBuilder(uri) { Query = "", Fragment = "" }.Uri.AbsoluteUri;
        return new(1, safeSource, Text(root, "title") ?? "", Decimal(root, "duration"),
            MaxDownloadBytes, formats.ToArray(), SourceFormatPolicy.Policies);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed) ? parsed : 0;

    private static double Decimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var parsed) ? parsed : 0;

    private static Uri ValidatedSource(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Source must be an HTTP(S) URL without embedded credentials.");
        return uri;
    }

    private static string? ValidatedDeno(string? denoPath)
    {
        if (denoPath is null) return null;
        denoPath = Path.GetFullPath(denoPath);
        if (!File.Exists(denoPath)) throw new FileNotFoundException("Configured Deno executable does not exist.");
        return denoPath;
    }

    /// `format` is a format identifier from ListFormatsAsync, two joined by '+', or one of the named
    /// policies. It defaults to the middle rendition rather than the largest.
    public async Task<AcquisitionResult> AcquireAsync(string sourceUrl, string destinationDirectory,
        string? denoPath = null, CancellationToken cancellationToken = default, string? format = null,
        IProgress<double>? progress = null)
    {
        var uri = ValidatedSource(sourceUrl);
        var destination = workspace.Resolve(destinationDirectory, mustExist: false);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Acquisition destination already exists.");
        denoPath = ValidatedDeno(denoPath);
        // Choosing before downloading: the rendition, its size and the reason are settled while nothing has
        // been written, so an unaffordable choice is refused instead of aborting mid-file.
        var choice = SourceFormatPolicy.Choose(
            await ListFormatsAsync(uri.AbsoluteUri, denoPath, cancellationToken), format, MaxDownloadBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".roughcut-acquire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var arguments = BuildArguments(uri, staging, denoPath, choice.Expression);
            await _tool.RunAsync(executable, arguments, cancellationToken,
                progress is null ? null : line => Report(line, progress));
            cancellationToken.ThrowIfCancellationRequested();
            var version = System.Text.Encoding.UTF8.GetString((await _tool.RunAsync(executable, ["--ignore-config", "--version"], cancellationToken)).Output).Trim();
            var result = await InspectAsync(uri, staging, version, cancellationToken) with { Format = choice };
            await File.WriteAllTextAsync(Path.Combine(staging, "acquisition.json"),
                JsonSerializer.Serialize(result, ApplicationJson.Default.AcquisitionResult), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return result;
        }
        finally { Cleanup(staging); }
    }

    internal static string[] BuildArguments(Uri source, string staging, string? denoPath, string formatExpression)
    {
        // Deliberately no --no-js-runtimes: that clears yt-dlp's default Deno entry, which silently costs
        // format availability on YouTube. yt-dlp already disables Node, QuickJS and Bun by default, so the
        // runtime stays Deno either way. --no-remote-components still stops any JavaScript being fetched.
        var arguments = new List<string>
        {
            "--ignore-config", "--no-remote-components", "--no-playlist", "--max-filesize", "536870912",
            "--no-overwrites", "--newline", "--progress", "--progress-template", ProgressTemplate, "--write-info-json", "--write-subs", "--write-auto-subs",
            "--sub-langs", "en,-live_chat", "--sub-format", "srt/best", "--convert-subs", "srt",
            "--merge-output-format", "mkv", "--remux-video", "mkv", "--paths", staging,
            "--output", "source.%(ext)s", "--format", formatExpression
        };
        // An explicit path pins which Deno runs; without one yt-dlp finds Deno on PATH or beside its executable.
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
        // A `.part` file is an aborted download, not media. Counting one as media produced an unreadable
        // project; matching it alongside the real file produced an unreadable error.
        var partial = files.Where(file => file.EndsWith(".part", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (partial.Length > 0)
            throw new InvalidDataException("The download did not complete: " +
                string.Join(", ", partial.Select(Path.GetFileName)) +
                " is a partial file, so the chosen rendition exceeded the download bound or the transfer was interrupted.");
        var candidates = files.Where(file => Path.GetFileName(file).StartsWith("source.", StringComparison.Ordinal) &&
            !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            !file.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length != 1)
            throw new InvalidDataException(candidates.Length == 0
                ? "Acquisition produced no media file."
                : "Acquisition produced more than one media file: " +
                  string.Join(", ", candidates.Select(Path.GetFileName)) + ".");
        var media = candidates[0];
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
