using System.Globalization;
using System.Text.Json;
using RoughCut.Application;
using RoughCut.Core;
using RoughCut.Media;
using RoughCut.Speech.Sherpa;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
try
{
    switch (args)
    {
        case [] or ["help"] or ["--help"]:
            Console.WriteLine("""
                RoughCut local diarization CLI
                  diarize <project.json> <asset-id> <expected-revision> <segmentation.onnx> <embedding.onnx> [speaker-count]

                Audio and inference stay local. Zero or omitted speaker-count uses threshold-based clustering.
                """);
            break;
        case ["diarize", var projectPath, var assetId, var expectedRevision, var segmentationModel,
              var embeddingModel, .. var options] when options.Length <= 1:
            {
                var fullProjectPath = Path.GetFullPath(projectPath);
                var count = options.Length == 0 ? 0 : int.Parse(options[0], CultureInfo.InvariantCulture);
                var operations = new RoughCutOperations(new WorkspaceBoundary(Path.GetDirectoryName(fullProjectPath)!),
                    Environment.GetEnvironmentVariable("ROUGHCUT_FFMPEG") ?? "ffmpeg",
                    Environment.GetEnvironmentVariable("ROUGHCUT_FFPROBE") ?? "ffprobe");
                var worker = System.Reflection.Assembly.GetEntryAssembly()?.Location
                    ?? throw new InvalidOperationException("Diarization worker location is unavailable.");
                var diarizer = new IsolatedSherpaSpeakerDiarizer(worker, segmentationModel, embeddingModel, count);
                var result = await operations.DiarizeAsync(Path.GetFileName(fullProjectPath), assetId,
                    long.Parse(expectedRevision, CultureInfo.InvariantCulture), diarizer, cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(result, ProjectJson.Default.DiarizationPlanResult));
                break;
            }
        case ["__worker", var projectPath, var assetId, var sourcePath, var segmentationModel, var embeddingModel,
              var speakerCount, var threshold]:
            {
                var project = await new ProjectStore().LoadAsync(Path.GetFullPath(projectPath), cancellation.Token);
                var diarizer = new SherpaSpeakerDiarizer(segmentationModel, embeddingModel,
                    int.Parse(speakerCount, CultureInfo.InvariantCulture),
                    float.Parse(threshold, CultureInfo.InvariantCulture),
                    Environment.GetEnvironmentVariable("ROUGHCUT_FFMPEG") ?? "ffmpeg");
                var submission = await diarizer.DiarizeAsync(project, assetId, Path.GetFullPath(sourcePath), cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(submission, ProjectJson.Default.DiarizationSubmission));
                break;
            }
        default:
            Console.Error.WriteLine("Unknown command or arguments. Run roughcut-diarization help.");
            return 2;
    }
    return 0;
}
catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or
    NotSupportedException or ProjectValidationException or RevisionConflictException or System.Text.Json.JsonException or
    MediaToolException or OperationCanceledException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
