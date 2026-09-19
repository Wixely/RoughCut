using RoughCut.Core;

namespace RoughCut.Application;

public interface ISpeakerDiarizer
{
    Task<DiarizationSubmission> DiarizeAsync(EditProject project, string assetId, string sourcePath,
        CancellationToken cancellationToken = default);
}
