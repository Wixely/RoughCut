using System.Globalization;
using System.Text.Json;
using RoughCut.Application;
using RoughCut.Core;
using RoughCut.Speech.Whisper;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
var token = cancellation.Token;
try
{
    switch (args)
    {
        case [] or ["help"] or ["--help"]:
            Console.WriteLine("""
                RoughCut local speech CLI
                  model <model-path>
                  transcribe <project.json> <asset-id> <expected-revision> <model-path> [language] [chunk-seconds]

                The pinned base.en model is downloaded and verified when absent. Audio stays local.
                """);
            break;
        case ["model", var modelPath]:
            using (var transcriber = new WhisperLocalSpeechTranscriber(Path.GetFullPath(modelPath)))
                await transcriber.EnsureModelAsync(token);
            Console.WriteLine("{\"ready\":true}");
            break;
        case ["transcribe", var projectPath, var assetId, var expectedRevision, var modelPath, .. var options]
            when options.Length <= 2:
            {
                var fullProjectPath = Path.GetFullPath(projectPath);
                var workspace = new WorkspaceBoundary(Path.GetDirectoryName(fullProjectPath)!);
                var language = options.FirstOrDefault() ?? "en";
                var chunkSeconds = options.Length == 2
                    ? int.Parse(options[1], CultureInfo.InvariantCulture)
                    : LocalSpeechProcessor.DefaultChunkSeconds;
                using var transcriber = new WhisperLocalSpeechTranscriber(Path.GetFullPath(modelPath), language);
                var operations = new RoughCutOperations(workspace,
                    ToolSettings.Default.Ffmpeg, ToolSettings.Default.Ffprobe);
                var project = await operations.TranscribeLocalAsync(Path.GetFileName(fullProjectPath), assetId,
                    long.Parse(expectedRevision, CultureInfo.InvariantCulture), transcriber, chunkSeconds, token);
                Console.WriteLine(JsonSerializer.Serialize(project, ProjectJson.Default.EditProject));
                break;
            }
        default:
            Console.Error.WriteLine("Unknown command or arguments. Run roughcut-speech help.");
            return 2;
    }
    return 0;
}
catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or
    NotSupportedException or ProjectValidationException or RevisionConflictException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
