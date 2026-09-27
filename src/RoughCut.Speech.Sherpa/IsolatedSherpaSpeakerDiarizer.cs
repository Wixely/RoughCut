using System.Globalization;
using System.Text.Json;
using RoughCut.Application;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Speech.Sherpa;

public sealed class IsolatedSherpaSpeakerDiarizer : ISpeakerDiarizer
{
    private readonly string _executable;
    private readonly string[] _prefixArguments;
    private readonly string _segmentationModel;
    private readonly string _embeddingModel;
    private readonly int _speakerCount;
    private readonly float _threshold;

    public IsolatedSherpaSpeakerDiarizer(string workerPath, string segmentationModel, string embeddingModel,
        int speakerCount = 0, float threshold = 0.5f)
    {
        if (string.IsNullOrWhiteSpace(workerPath)) throw new ArgumentException("Diarization worker path is required.");
        workerPath = Path.GetFullPath(workerPath);
        if (!File.Exists(workerPath)) throw new FileNotFoundException("Diarization worker was not found.", workerPath);
        if (workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            _executable = "dotnet";
            _prefixArguments = [workerPath];
        }
        else
        {
            _executable = workerPath;
            _prefixArguments = [];
        }
        _segmentationModel = Path.GetFullPath(segmentationModel);
        _embeddingModel = Path.GetFullPath(embeddingModel);
        _speakerCount = speakerCount;
        _threshold = threshold;
    }

    public async Task<DiarizationSubmission> DiarizeAsync(EditProject project, string assetId, string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"roughcut-diarization-{Guid.NewGuid():N}.json");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(project, ProjectJson.Default.EditProject);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            string[] arguments = [.. _prefixArguments, "__worker", temporary, assetId, Path.GetFullPath(sourcePath),
                _segmentationModel, _embeddingModel, _speakerCount.ToString(CultureInfo.InvariantCulture),
                _threshold.ToString("R", CultureInfo.InvariantCulture)];
            var result = await ToolProcess.RunAsync(_executable, arguments, ProjectStore.MaxDocumentBytes,
                TimeSpan.FromMinutes(11), cancellationToken: cancellationToken);
            return JsonSerializer.Deserialize(result.Output, ProjectJson.Default.DiarizationSubmission)
                ?? throw new InvalidDataException("Diarization worker returned no result.");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
