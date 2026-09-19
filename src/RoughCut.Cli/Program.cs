using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoughCut.Core;
using RoughCut.Media;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var token = cancellation.Token;
var store = new ProjectStore();
var ffmpeg = Environment.GetEnvironmentVariable("ROUGHCUT_FFMPEG") ?? "ffmpeg";
var ffprobe = Environment.GetEnvironmentVariable("ROUGHCUT_FFPROBE") ?? "ffprobe";
var media = new MediaReader(ffmpeg, ffprobe);
try
{
    switch (args)
    {
        case [] or ["help"] or ["--help"]:
            Console.WriteLine("""
                RoughCut development CLI
                  validate <project.json>
                  map <project.json>
                  save <candidate.json> <project.json> <expected-revision>
                  inspect <local-video>
                  create <local-video> <new-project.json>
                  frame <local-video> <seconds> <new-output.png>
                  edit <project.json> <operations.json> <expected-revision>
                  captions <project.json> <asset-id> <source.srt> <expected-revision>
                  preflight <project.json>
                  export <project.json> <new-output-directory> [--allow-encode]

                Frame requests use source-relative presentation time. JSON metadata goes to stdout.
                Export is bounded to the documented Matroska video/PCM fixture matrix.
                Speech inference and timed image rendering are not implemented yet.
                Set ROUGHCUT_FFMPEG / ROUGHCUT_FFPROBE to override executable locations.
                """);
            break;
        case ["validate", var path]:
            await store.LoadAsync(path, token);
            Console.WriteLine("{\"valid\":true}");
            break;
        case ["map", var path]:
            Console.WriteLine(JsonSerializer.Serialize(ProjectValidator.MapTimeline(await store.LoadAsync(path, token)), ProjectJson.Default.TimelineMappingArray));
            break;
        case ["save", var candidate, var destination, var expected]:
            await store.SaveAsync(destination, await store.LoadAsync(candidate, token), long.Parse(expected, CultureInfo.InvariantCulture), token);
            Console.WriteLine("{\"saved\":true}");
            break;
        case ["edit", var path, var operationsPath, var expected]:
            {
                var project = await store.LoadAsync(path, token);
                var revision = long.Parse(expected, CultureInfo.InvariantCulture);
                if (project.Revision != revision) throw new RevisionConflictException();
                var operations = JsonSerializer.Deserialize(await ProjectFiles.ReadBoundedAsync(operationsPath, ProjectStore.MaxDocumentBytes, token), ProjectJson.Default.EditOperationArray)
                    ?? throw new InvalidDataException("Edit operations cannot be null.");
                var edited = TimelineEditor.Apply(project, operations);
                await store.SaveAsync(path, edited, revision, token);
                Console.WriteLine(JsonSerializer.Serialize(edited, ProjectJson.Default.EditProject));
                break;
            }
        case ["captions", var path, var assetId, var sourcePath, var expected]:
            {
                var project = await store.LoadAsync(path, token);
                var revision = long.Parse(expected, CultureInfo.InvariantCulture);
                if (project.Revision != revision) throw new RevisionConflictException();
                var relative = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFullPath(sourcePath)).Replace('\\', '/');
                var bytes = await ProjectFiles.ReadBoundedAsync(ProjectFiles.Resolve(path, relative), Captions.MaxBytes, token);
                var cues = Captions.ParseSrt(new UTF8Encoding(false, true).GetString(bytes));
                var edited = project with { Revision = checked(revision + 1), Captions = new(assetId, relative, Convert.ToHexStringLower(SHA256.HashData(bytes)), new(1, 1000), cues) };
                await store.SaveAsync(path, edited, revision, token);
                Console.WriteLine(JsonSerializer.Serialize(edited, ProjectJson.Default.EditProject));
                break;
            }
        case ["preflight", var path]:
            {
                var plan = await new ExportPlanner(ffmpeg, ffprobe).PreflightAsync(await store.LoadAsync(path, token), path, token);
                Console.WriteLine(JsonSerializer.Serialize(plan, ProjectJson.Default.ExportPlan));
                return plan.Supported ? 0 : 2;
            }
        case ["export", var path, var destination, .. var options] when options.Length == 0 || options is ["--allow-encode"]:
            {
                var report = await new ExportEngine(ffmpeg, ffprobe).ExportAsync(path, destination, options.Length == 1, token);
                Console.WriteLine(JsonSerializer.Serialize(report, ProjectJson.Default.ExportReport));
                break;
            }
        case ["inspect", var path]:
            Console.WriteLine(JsonSerializer.Serialize(await media.InspectAsync(path, token), MediaJson.Default.VideoInfo));
            break;
        case ["create", var source, var destination]:
            {
                var relative = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(destination))!, Path.GetFullPath(source)).Replace('\\', '/');
                if (!ProjectValidator.IsPortablePath(relative))
                    throw new ArgumentException("Keep the source inside the project directory (for example media/source.mp4).");
                if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Project destination must differ from source media.");
                var info = await media.InspectAsync(source, token);
                var project = new EditProject
                {
                    TimeBase = info.TimeBase,
                    Assets = [new("source-1", "video", relative, info.Sha256, info.DurationTicks, info.Width, info.Height, "application/octet-stream")],
                    Timeline = [new("clip-1", "source-1", 0, info.DurationTicks)]
                };
                await store.SaveAsync(destination, project, 0, token);
                Console.WriteLine(JsonSerializer.Serialize(project, ProjectJson.Default.EditProject));
                break;
            }
        case ["frame", var path, var secondsText, var destination]:
            {
                var seconds = decimal.Parse(secondsText, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                if (seconds < 0 || seconds * 1_000_000 != decimal.Truncate(seconds * 1_000_000))
                    throw new ArgumentException("Seconds must be nonnegative with at most six fractional digits.");
                destination = Path.GetFullPath(destination);
                if (File.Exists(destination)) throw new IOException("Output already exists; choose a new output path.");
                var frame = await media.GetFrameAsync(path, new("source-1", new(checked((long)(seconds * 1_000_000)), TimeBase.Microseconds)), token);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, frame.Png, token);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporaryPath, destination, overwrite: false);
                }
                finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                Console.WriteLine(JsonSerializer.Serialize(frame.Info, ProjectJson.Default.FrameInfo));
                break;
            }
        default: Console.Error.WriteLine("Unknown command or arguments. Run roughcut help."); return 2;
    }
    return 0;
}
catch (ProjectValidationException exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(exception.Issues, ProjectJson.Default.ValidationIssueArray));
    return 2;
}
catch (ExportRejectedException exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(exception.Plan, ProjectJson.Default.ExportPlan));
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation cancelled or media-tool time limit reached.");
    return 130;
}
catch (Exception exception) when (exception is IOException or ArgumentException or JsonException or
    NotSupportedException or RevisionConflictException or MediaToolException or OverflowException or
    FormatException or KeyNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
{
    // Filesystem and tool diagnostics can contain private paths or media metadata.
    Console.Error.WriteLine(exception switch
    {
        RevisionConflictException => "Project changed; reload before saving.",
        MediaToolException or NotSupportedException => exception.Message,
        JsonException => "Invalid project JSON; check field names, types and required values.",
        _ => "Operation failed; check arguments, file access, output collisions and media-tool availability."
    });
    return 2;
}
