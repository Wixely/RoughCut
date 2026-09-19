using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoughCut.Core;

public static class DiarizationPlanner
{
    public const int MaxTurns = 10_000;

    public static DiarizationPlanResult Plan(EditProject project, DiarizationSubmission submission)
    {
        ProjectValidator.EnsureValid(project);
        if (string.IsNullOrWhiteSpace(submission.Provider) || submission.Provider.Length > 128 ||
            string.IsNullOrWhiteSpace(submission.Model) || submission.Model.Length > 256)
            throw new ArgumentException("Diarization provider and model are required and bounded.");
        if (submission.Turns is null || submission.Turns.Length > MaxTurns)
            throw new ArgumentException($"Diarization must contain at most {MaxTurns} turns.");
        var asset = project.Assets.SingleOrDefault(item => item.Id == submission.AssetId && item.Kind is "video" or "audio")
            ?? throw new KeyNotFoundException("Diarization asset was not found in the project.");

        var ordered = submission.Turns.OrderBy(item => item.Start).ThenBy(item => item.End)
            .ThenBy(item => item.SpeakerKey, StringComparer.Ordinal).ToArray();
        foreach (var turn in ordered)
        {
            if (string.IsNullOrWhiteSpace(turn.SpeakerKey) || turn.SpeakerKey.Length > 128)
                throw new ArgumentException("Diarization speaker keys must contain 1 to 128 characters.");
            if (turn.Start < 0 || turn.End <= turn.Start || turn.End > asset.Duration)
                throw new ArgumentException("Diarization turns must be positive intervals inside the source asset.");
        }
        var speakerKeys = ordered.Select(item => item.SpeakerKey).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (speakerKeys.Length > 1000) throw new ArgumentException("Diarization must contain at most 1000 speaker keys.");

        var prior = project.Diarization is { } existing && existing.AssetId == asset.Id &&
            existing.Provider == submission.Provider && existing.Model == submission.Model ? existing : null;
        var mappings = prior?.Speakers.ToDictionary(item => item.Key, StringComparer.Ordinal)
            ?? new Dictionary<string, DiarizationSpeaker>(StringComparer.Ordinal);
        var speakers = project.Speakers.ToList();
        var usedIds = speakers.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var key in speakerKeys)
        {
            if (mappings.TryGetValue(key, out var known) && usedIds.Contains(known.SpeakerId)) continue;
            var id = StableSpeakerId(asset.Id, submission.Provider, submission.Model, key, usedIds);
            speakers.Add(new(id, $"Speaker {speakers.Count + 1}"));
            mappings[key] = new(key, id);
            usedIds.Add(id);
        }

        var inferred = 0;
        var unknown = 0;
        var preserved = 0;
        var speech = project.Speech.Select(segment =>
        {
            if (segment.AssetId != asset.Id) return segment;
            if (segment.Assignment == "corrected")
            {
                preserved++;
                return segment;
            }
            var candidates = ordered.Where(turn => turn.Start < segment.End && turn.End > segment.Start).ToArray();
            if (candidates.Length == 0)
            {
                unknown++;
                return segment with { SpeakerIds = [], Assignment = "unknown", Overlap = false };
            }
            var concurrentKeys = ConcurrentKeys(candidates, segment.Start, segment.End);
            if (concurrentKeys.Length > 8)
                throw new NotSupportedException("A speech segment cannot contain more than eight concurrent diarized speakers.");
            string[] keys;
            var overlap = concurrentKeys.Length > 1;
            if (overlap) keys = concurrentKeys;
            else
            {
                keys = [candidates.GroupBy(item => item.SpeakerKey, StringComparer.Ordinal)
                    .Select(group => new { Key = group.Key, Duration = group.Sum(turn =>
                        Math.Min(segment.End, turn.End) - Math.Max(segment.Start, turn.Start)) })
                    .OrderByDescending(item => item.Duration).ThenBy(item => item.Key, StringComparer.Ordinal)
                    .First().Key];
            }
            var assignedIds = keys.Select(key => mappings[key].SpeakerId).Distinct(StringComparer.Ordinal).ToArray();
            inferred++;
            return segment with
            {
                SpeakerIds = assignedIds,
                Assignment = "inferred",
                Overlap = overlap && assignedIds.Length > 1
            };
        }).ToArray();

        var canonical = submission with { Turns = ordered };
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical,
            ProjectJson.Default.DiarizationSubmission)));
        var revision = checked(project.Revision + 1);
        var diarization = new DiarizationProvenance(asset.Id, asset.Sha256, submission.Provider,
            submission.Model, hash, mappings.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(), revision);
        var planned = project with { Revision = revision, Speakers = [.. speakers], Speech = speech, Diarization = diarization };
        ProjectValidator.EnsureValid(planned);
        return new(planned, inferred, unknown, preserved);
    }

    private static string[] ConcurrentKeys(DiarizationTurn[] turns, long start, long end)
    {
        var boundaries = turns.SelectMany(item => new[] { Math.Max(start, item.Start), Math.Min(end, item.End) })
            .Where(value => value >= start && value <= end).Distinct().Order().ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < boundaries.Length; index++)
        {
            if (boundaries[index] >= boundaries[index + 1]) continue;
            var active = turns.Where(item => item.Start < boundaries[index + 1] && item.End > boundaries[index])
                .Select(item => item.SpeakerKey).Distinct(StringComparer.Ordinal).ToArray();
            if (active.Length > 1) foreach (var key in active) keys.Add(key);
        }
        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    private static string StableSpeakerId(string assetId, string provider, string model, string key, HashSet<string> used)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{assetId}\n{provider}\n{model}\n{key}")));
        for (var length = 12; length <= digest.Length; length += 4)
        {
            var candidate = "speaker-" + digest[..length];
            if (!used.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("Unable to allocate a stable speaker ID.");
    }
}
