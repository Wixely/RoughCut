using System.Text.RegularExpressions;

namespace RoughCut.Application;

/// Where an edit lives and what it is called. One edit is one directory: the media, the project file and
/// every asset the project generates sit together, so the whole edit can be moved, backed up or deleted as
/// one thing — and a project's stored paths stay portable, which they cannot be if the media lives elsewhere.
public static partial class ProjectFolder
{
    public const string DefaultProjectFileName = "project.json";

    /// How much is read and written at a time while copying. Large enough that a big file is not spent in
    /// syscalls, small enough that progress moves visibly and cancellation is noticed promptly.
    private const int CopyBufferBytes = 1024 * 1024;

    [GeneratedRegex(@"[^A-Za-z0-9 _.\-()\[\]]")]
    private static partial Regex Unwanted();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Runs();

    /// A directory name taken from what a person would call the video — a title from the source, or a file
    /// name — reduced to something every filesystem accepts. Reserved device names and trailing dots and
    /// spaces are what Windows actually refuses, so those are handled rather than assumed away.
    public static string NameFrom(string? title, string fallback = "project")
    {
        var cleaned = Runs().Replace(Unwanted().Replace(title ?? "", " "), " ").Trim().TrimEnd('.');
        if (cleaned.Length > 80) cleaned = cleaned[..80].Trim();
        if (cleaned.Length == 0) return fallback;
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7",
            "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        return reserved.Contains(cleaned, StringComparer.OrdinalIgnoreCase) ? cleaned + "-project" : cleaned;
    }

    /// The first name in `root` that is not taken, so asking for the same video twice does not overwrite the
    /// first edit of it. Returns a full path; nothing is created here.
    public static string Unused(string root, string name, int limit = 1000)
    {
        name = NameFrom(name);
        for (var suffix = 1; suffix <= limit; suffix++)
        {
            var candidate = Path.Combine(root, suffix == 1 ? name
                : name + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException($"There are already {limit} folders named after '{name}'.");
    }

    /// Copies a file, reporting the fraction completed, and leaves nothing behind if it is cancelled or
    /// fails: the destination is written under a temporary name and moved into place only once complete, so
    /// a half-copied video is never mistaken for the source.
    public static async Task CopyAsync(string source, string destination, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (!File.Exists(source)) throw new FileNotFoundException("The file to copy does not exist.", source);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("Copy destination already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".partial";
        var total = new FileInfo(source).Length;
        try
        {
            await using (var reading = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferBytes, useAsync: true))
            await using (var writing = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                CopyBufferBytes, useAsync: true))
            {
                var buffer = new byte[CopyBufferBytes];
                long copied = 0;
                progress?.Report(0);
                while (true)
                {
                    var read = await reading.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    await writing.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    // A zero-length file reports one rather than dividing by its own size.
                    progress?.Report(total <= 0 ? 1 : Math.Min(1, (double)copied / total));
                }
                await writing.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            progress?.Report(1);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
