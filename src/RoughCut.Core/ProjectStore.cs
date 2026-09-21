using System.Text.Json;

namespace RoughCut.Core;

public sealed class ProjectStore
{
    public const int MaxDocumentBytes = 4 * 1024 * 1024;

    public async Task<EditProject> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaxDocumentBytes) throw new InvalidDataException("Project exceeds the 4 MiB document limit.");
        // Bound the read as well as the initial length check, in case another program grows the file.
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(block, cancellationToken)) != 0)
        {
            if (buffer.Length + count > MaxDocumentBytes) throw new InvalidDataException("Project exceeds the 4 MiB document limit.");
            buffer.Write(block, 0, count);
        }
        var project = JsonSerializer.Deserialize(buffer.ToArray(), ProjectJson.Default.EditProject)
            ?? throw new InvalidDataException("Project cannot be null.");
        project = WithoutAbsentCollections(project);
        ProjectValidator.EnsureValid(project);
        return project;
    }

    /// An omitted optional collection deserializes as null rather than the declared empty default, so a
    /// project that simply leaves one out would otherwise fail deep inside validation. An explicit null is
    /// still rejected by the deserializer, so anything null here was absent and means empty.
    private static EditProject WithoutAbsentCollections(EditProject project) => project with
    {
        // Omitted scalars with a declared default mean that default, not an invalid empty value.
        Prompt = project.Prompt ?? "",
        ExportMode = project.ExportMode ?? "prefer-stream-copy",
        Assets = project.Assets ?? [],
        Timeline = project.Timeline ?? [],
        Speakers = project.Speakers ?? [],
        SpeakerCorrections = project.SpeakerCorrections ?? [],
        Speech = project.Speech ?? [],
        Evidence = project.Evidence ?? [],
        Observations = project.Observations ?? [],
        Proposals = project.Proposals ?? [],
        Voices = project.Voices ?? [],
        Replacements = project.Replacements ?? [],
        Synthesis = project.Synthesis ?? [],
        Provenance = project.Provenance ?? []
    };

    // Expected revision 0 creates a new project; updates require the next revision.
    // All cooperating writers hold the same lock through compare-and-replace.
    public async Task SaveAsync(string path, EditProject project, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ProjectValidator.EnsureValid(project);
        if (expectedRevision < 0 || expectedRevision == long.MaxValue || project.Revision != expectedRevision + 1)
            throw new InvalidDataException("New revision must equal expected revision plus one.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(project, ProjectJson.Default.EditProject);
        if (bytes.Length > MaxDocumentBytes) throw new InvalidDataException("Project exceeds the 4 MiB document limit.");
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(path))
        {
            var current = await LoadAsync(path, cancellationToken);
            if (current.Revision != expectedRevision || current.ProjectId != project.ProjectId)
                throw new RevisionConflictException();
        }
        else if (expectedRevision != 0) throw new RevisionConflictException();
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedRevision == 0) File.Move(temporaryPath, path, overwrite: false);
            else File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}

public sealed class RevisionConflictException() : Exception("Project changed; reload it before saving.");
