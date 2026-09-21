using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoughCut.Desktop;

public sealed record RecentProject(string Path, string ProjectId, string OpenedUtc);

public sealed record RecentProjectList
{
    public int SchemaVersion { get; init; } = 1;
    public RecentProject[] Projects { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RecentProjectList))]
internal partial class RecentProjectJson : JsonSerializerContext;

// Per-user window state, deliberately outside project JSON and outside the repository.
public sealed class RecentProjects(string? storePath = null)
{
    public const int MaxEntries = 10;
    private const int MaxStoreBytes = 64 * 1024;

    private readonly string _storePath = storePath ?? DefaultStorePath;

    public static string DefaultStorePath =>
        Environment.GetEnvironmentVariable("ROUGHCUT_RECENT_PROJECTS") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RoughCut", "recent-projects.json");

    public string StorePath => _storePath;

    // A damaged or unreadable history must never stop the window opening.
    public IReadOnlyList<RecentProject> Load()
    {
        try
        {
            var file = new FileInfo(_storePath);
            if (!file.Exists || file.Length is 0 or > MaxStoreBytes) return [];
            var stored = JsonSerializer.Deserialize(File.ReadAllText(_storePath), RecentProjectJson.Default.RecentProjectList);
            return stored?.Projects
                .Where(entry => entry is { Path.Length: > 0 } && File.Exists(entry.Path))
                .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntries)
                .ToArray() ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    public IReadOnlyList<RecentProject> Record(string projectPath, string projectId)
    {
        projectPath = Path.GetFullPath(projectPath);
        var entry = new RecentProject(projectPath, projectId, DateTimeOffset.UtcNow.ToString("O"));
        var projects = new[] { entry }
            .Concat(Load().Where(item => !string.Equals(item.Path, projectPath, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxEntries)
            .ToArray();
        Save(projects);
        return projects;
    }

    public IReadOnlyList<RecentProject> Forget(string projectPath)
    {
        var projects = Load()
            .Where(item => !string.Equals(item.Path, Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Save(projects);
        return projects;
    }

    private void Save(RecentProject[] projects)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var temporary = _storePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new RecentProjectList { Projects = projects }, RecentProjectJson.Default.RecentProjectList));
            File.Move(temporary, _storePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Remembering recent projects is a convenience; failing to write one must not interrupt review.
        }
    }
}
