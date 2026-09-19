namespace RoughCut.Core;

public static class SpeakerEditor
{
    public static EditProject Apply(EditProject project, SpeakerEdit[] edits)
    {
        if (edits.Length is 0 or > 100) throw new ArgumentException("Provide 1 to 100 speaker edits.");
        var speakers = project.Speakers.ToList();
        var speech = project.Speech.ToList();
        var voices = project.Voices.ToList();
        var corrections = project.SpeakerCorrections.ToList();
        var revision = checked(project.Revision + 1);

        for (var index = 0; index < edits.Length; index++)
        {
            var edit = edits[index] ?? throw new ArgumentException("Speaker edits cannot contain null entries.");
            if (string.IsNullOrWhiteSpace(edit.Reason) || edit.Reason.Length > 500)
                throw new ArgumentException("Each speaker edit needs a reason of at most 500 characters.");
            var correctionId = $"speaker-correction-{revision}-{index + 1}";
            switch (edit.Action)
            {
                case "add":
                    RequireId(edit.SpeakerId, "New speaker ID");
                    RequireLabel(edit.Label);
                    if (speakers.Any(item => item.Id == edit.SpeakerId)) throw new ArgumentException("Speaker ID already exists.");
                    speakers.Add(new(edit.SpeakerId!, edit.Label!));
                    corrections.Add(new(correctionId, "add", [], [], [edit.SpeakerId!], null, edit.Label, edit.Reason, revision));
                    break;

                case "rename":
                    RequireId(edit.SpeakerId, "Speaker ID");
                    RequireLabel(edit.Label);
                    var speakerIndex = speakers.FindIndex(item => item.Id == edit.SpeakerId);
                    if (speakerIndex < 0) throw new KeyNotFoundException("Speaker was not found.");
                    var previous = speakers[speakerIndex];
                    speakers[speakerIndex] = previous with { Label = edit.Label! };
                    corrections.Add(new(correctionId, "rename", [], [previous.Id], [previous.Id], previous.Label, edit.Label, edit.Reason, revision));
                    break;

                case "assign":
                    var segmentIds = edit.SegmentIds ?? [];
                    var assigned = edit.SpeakerIds ?? [];
                    if (segmentIds.Length is 0 or > 100 || segmentIds.Distinct(StringComparer.Ordinal).Count() != segmentIds.Length)
                        throw new ArgumentException("Assign 1 to 100 unique speech segment IDs.");
                    if (assigned.Length > 8 || assigned.Distinct(StringComparer.Ordinal).Count() != assigned.Length ||
                        assigned.Any(id => !speakers.Any(item => item.Id == id)))
                        throw new ArgumentException("Assigned speakers must be unique existing IDs, with at most eight speakers per segment.");
                    if (assigned.Length > 1 && !edit.Overlap) throw new ArgumentException("Multiple assigned speakers require overlap=true.");
                    foreach (var segmentId in segmentIds)
                    {
                        var speechIndex = speech.FindIndex(item => item.Id == segmentId);
                        if (speechIndex < 0) throw new KeyNotFoundException("Speech segment was not found.");
                        var segment = speech[speechIndex];
                        speech[speechIndex] = segment with
                        {
                            SpeakerIds = [.. assigned],
                            Assignment = assigned.Length == 0 ? "unknown" : "corrected",
                            Overlap = assigned.Length > 1 && edit.Overlap
                        };
                        corrections.Add(new($"{correctionId}-{segmentId}", "assign", [segmentId],
                            segment.SpeakerIds, [.. assigned], null, null, edit.Reason, revision));
                    }
                    break;

                case "merge":
                    RequireId(edit.SpeakerId, "Source speaker ID");
                    RequireId(edit.TargetSpeakerId, "Target speaker ID");
                    if (edit.SpeakerId == edit.TargetSpeakerId) throw new ArgumentException("Merge speakers must differ.");
                    var sourceSpeaker = speakers.SingleOrDefault(item => item.Id == edit.SpeakerId)
                        ?? throw new KeyNotFoundException("Source speaker was not found.");
                    var targetSpeaker = speakers.SingleOrDefault(item => item.Id == edit.TargetSpeakerId)
                        ?? throw new KeyNotFoundException("Target speaker was not found.");
                    var sourceVoice = voices.SingleOrDefault(item => item.SpeakerId == sourceSpeaker.Id);
                    var targetVoice = voices.SingleOrDefault(item => item.SpeakerId == targetSpeaker.Id);
                    if (sourceVoice is not null && targetVoice is not null)
                        throw new InvalidOperationException("Remove one voice mapping before merging speakers that both have mappings.");
                    var affected = new List<string>();
                    for (var speechIndex = 0; speechIndex < speech.Count; speechIndex++)
                    {
                        var segment = speech[speechIndex];
                        if (!segment.SpeakerIds.Contains(sourceSpeaker.Id)) continue;
                        affected.Add(segment.Id);
                        var mergedIds = segment.SpeakerIds.Select(id => id == sourceSpeaker.Id ? targetSpeaker.Id : id)
                            .Distinct(StringComparer.Ordinal).ToArray();
                        speech[speechIndex] = segment with { SpeakerIds = mergedIds, Assignment = "corrected", Overlap = mergedIds.Length > 1 };
                    }
                    if (sourceVoice is not null)
                    {
                        var voiceIndex = voices.IndexOf(sourceVoice);
                        voices[voiceIndex] = sourceVoice with { SpeakerId = targetSpeaker.Id };
                    }
                    speakers.Remove(sourceSpeaker);
                    corrections.Add(new(correctionId, "merge", [.. affected], [sourceSpeaker.Id], [targetSpeaker.Id],
                        sourceSpeaker.Label, targetSpeaker.Label, edit.Reason, revision));
                    break;

                default: throw new ArgumentException("Speaker edit action must be add, rename, assign or merge.");
            }
        }

        var edited = project with
        {
            Revision = revision,
            Speakers = [.. speakers],
            Speech = [.. speech],
            Voices = [.. voices],
            SpeakerCorrections = [.. corrections]
        };
        ProjectValidator.EnsureValid(edited);
        return edited;
    }

    private static void RequireId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) throw new ArgumentException($"{name} must contain 1 to 128 characters.");
    }

    private static void RequireLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200) throw new ArgumentException("Speaker label must contain 1 to 200 characters.");
    }
}
