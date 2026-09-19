using System.Numerics;

namespace RoughCut.Core;

public static class VoicePlanner
{
    public static EditProject Plan(EditProject project, VoicePlanSubmission submission)
    {
        var mapping = submission.Mapping;
        var request = submission.Replacement;
        if (mapping.Provider != "qwen-tts") throw new ArgumentException("Voice provider must be qwen-tts.");
        if (!project.Speakers.Any(item => item.Id == mapping.SpeakerId)) throw new KeyNotFoundException("Voice speaker was not found.");
        var existingById = project.Voices.SingleOrDefault(item => item.Id == mapping.Id);
        var existingBySpeaker = project.Voices.SingleOrDefault(item => item.SpeakerId == mapping.SpeakerId);
        if (existingById is not null && existingById != mapping)
            throw new ArgumentException("Voice mapping ID already exists with different settings.");
        if (existingBySpeaker is not null && existingBySpeaker != mapping)
            throw new ArgumentException("Speaker already has a different voice mapping.");
        if (request.MappingId != mapping.Id) throw new ArgumentException("Replacement must reference the submitted voice mapping.");
        if (project.Replacements.Any(item => item.Id == request.Id)) throw new ArgumentException("Replacement ID already exists.");
        var segment = project.Speech.SingleOrDefault(item => item.Id == request.SegmentId)
            ?? throw new KeyNotFoundException("Speech segment was not found.");
        if (segment.Overlap || segment.SpeakerIds.Length != 1 || segment.SpeakerIds[0] != mapping.SpeakerId)
            throw new NotSupportedException("Voice replacement requires one corrected, non-overlapping speaker assignment.");
        if (segment.Assignment != "corrected") throw new NotSupportedException("Confirm the speaker assignment before requesting replacement.");
        if (request.FitPolicy is not ("exact" or "time-stretch"))
            throw new NotSupportedException("Voice fit policy must be exact or time-stretch.");
        if (request.BackgroundPolicy != "require-isolated-dialogue")
            throw new NotSupportedException("The bounded voice workflow requires isolated dialogue; background mixing is not implemented.");

        var edited = project with
        {
            Revision = checked(project.Revision + 1),
            Voices = existingById is null ? [.. project.Voices, mapping] : project.Voices,
            Replacements = [.. project.Replacements, new(request.Id, request.SegmentId, request.MappingId,
                request.Text, null, "requested", request.FitPolicy, request.BackgroundPolicy)]
        };
        ProjectValidator.EnsureValid(edited);
        return edited;
    }

    public static EditProject SetState(EditProject project, string replacementId, string state)
    {
        if (state is not ("applied" or "reverted")) throw new ArgumentException("Voice replacement state must be applied or reverted.");
        var index = Array.FindIndex(project.Replacements, item => item.Id == replacementId);
        if (index < 0) throw new KeyNotFoundException("Voice replacement was not found.");
        var replacement = project.Replacements[index];
        if (replacement.GeneratedAssetId is null || replacement.State is not ("preview" or "applied" or "reverted"))
            throw new InvalidOperationException("Import a generated preview before applying or reverting it.");
        var provenance = project.Synthesis.SingleOrDefault(item => item.ReplacementId == replacement.Id)
            ?? throw new InvalidOperationException("Voice preview provenance is missing.");
        if (state == "applied" && replacement.FitPolicy == "exact" && provenance.RequestedDuration != provenance.ActualDuration)
            throw new NotSupportedException("Exact-duration replacement cannot be applied because generated audio duration differs from the speech interval.");
        if (state == "applied" && replacement.FitPolicy == "time-stretch" &&
            !VoiceFitPolicy.IsWithinLimit(provenance.RequestedDuration, provenance.ActualDuration))
            throw new NotSupportedException("Time-stretch replacement exceeds the supported 0.8x to 1.25x tempo range.");
        var replacements = project.Replacements.ToArray();
        replacements[index] = replacement with { State = state };
        var edited = project with { Revision = checked(project.Revision + 1), Replacements = replacements };
        ProjectValidator.EnsureValid(edited);
        return edited;
    }
}

public static class VoiceFitPolicy
{
    public static bool IsWithinLimit(long requestedDuration, long actualDuration) =>
        requestedDuration > 0 && actualDuration > 0 &&
        (BigInteger)actualDuration * 5 >= (BigInteger)requestedDuration * 4 &&
        (BigInteger)actualDuration * 4 <= (BigInteger)requestedDuration * 5;
}
