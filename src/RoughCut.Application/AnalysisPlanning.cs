using RoughCut.Core;

namespace RoughCut.Application;

public interface IContentAnalyzer
{
    Task<AnalysisSubmission> AnalyzeAsync(EditProject project, string assetId, string prompt,
        CancellationToken cancellationToken = default);
}

public static class AnalysisPlanner
{
    public static AnalysisPlanResult Plan(EditProject project, AnalysisSubmission submission,
        string prompt, string policy)
    {
        ProjectValidator.EnsureValid(project);
        if (policy is not ("review" or "auto-high-certainty"))
            throw new ArgumentException("Analysis policy must be review or auto-high-certainty.");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 4000)
            throw new ArgumentException("Analysis prompt must contain 1 to 4000 characters.");
        if (string.IsNullOrWhiteSpace(submission.Provider) || submission.Provider.Length > 128 ||
            string.IsNullOrWhiteSpace(submission.Model) || submission.Model.Length > 256)
            throw new ArgumentException("Analysis provider and model are required and bounded.");
        if (submission.Evidence is null || submission.Observations is null)
            throw new ArgumentException("Analysis collections cannot be null.");
        var asset = project.Assets.SingleOrDefault(item => item.Id == submission.AssetId && item.Kind is "video" or "audio")
            ?? throw new KeyNotFoundException("Analysis asset was not found in the project.");
        var revision = checked(project.Revision + 1);
        var provenance = new AnalysisProvenance(asset.Id, asset.Sha256, submission.Provider, submission.Model,
            prompt, policy, revision);
        var candidate = project with
        {
            Revision = revision,
            Prompt = prompt,
            Evidence = submission.Evidence,
            Observations = submission.Observations,
            Proposals = [],
            Analysis = provenance
        };
        ProjectValidator.EnsureValid(candidate);
        if (candidate.Evidence.Any(item => item.AssetId != asset.Id) || candidate.Observations.Any(item => item.AssetId != asset.Id))
            throw new ArgumentException("One analysis submission must describe exactly one source asset.");

        var proposals = new List<EditorialProposal>();
        foreach (var observation in candidate.Observations)
        {
            foreach (var clip in candidate.Timeline.Where(item => item.AssetId == observation.AssetId))
            {
                var start = Math.Max(clip.In, observation.Start);
                var end = Math.Min(clip.Out, observation.End);
                if (start >= end) continue;
                var decision = observation.RecommendedAction switch
                {
                    "retain" => "retain",
                    "remove" when observation.Certainty == "high" && policy == "auto-high-certainty" => "remove",
                    _ => "review"
                };
                proposals.Add(new($"proposal-{proposals.Count + 1}", observation.Id, clip.Id, start, end,
                    observation.RecommendedAction, decision, observation.Summary, observation.Certainty,
                    observation.EvidenceIds));
            }
        }
        candidate = candidate with { Proposals = proposals.ToArray() };
        ProjectValidator.EnsureValid(candidate);
        return new(candidate, proposals.Count(item => item.Decision == "remove"),
            proposals.Count(item => item.Decision == "review"), proposals.Count(item => item.Decision == "retain"));
    }

    public static EditProject Apply(EditProject project, string[]? proposalIds = null)
    {
        ProjectValidator.EnsureValid(project);
        if (project.Analysis is not { } analysis || analysis.ProposalRevision != project.Revision)
            throw new RevisionConflictException();
        if (proposalIds is { Length: > 1000 }) throw new ArgumentException("At most 1000 proposals can be applied.");
        if (proposalIds is not null && proposalIds.Distinct(StringComparer.Ordinal).Count() != proposalIds.Length)
            throw new ArgumentException("Proposal IDs must be unique.");
        EditorialProposal[] selected;
        if (proposalIds is null or { Length: 0 })
            selected = project.Proposals.Where(item => item.Decision == "remove").ToArray();
        else
        {
            var requested = proposalIds.ToHashSet(StringComparer.Ordinal);
            selected = project.Proposals.Where(item => requested.Contains(item.Id)).ToArray();
            if (selected.Length != requested.Count) throw new KeyNotFoundException("An analysis proposal was not found.");
            if (selected.Any(item => item.RecommendedAction != "remove"))
                throw new ArgumentException("Only proposals recommending removal can be explicitly applied.");
        }
        if (selected.Length == 0) throw new InvalidOperationException("No removal proposals were selected by the analysis policy.");

        var removals = selected.GroupBy(item => item.ClipId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Merge(group.Select(item => (item.In, item.Out))), StringComparer.Ordinal);
        var usedIds = project.Timeline.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var timeline = new List<TimelineClip>();
        foreach (var clip in project.Timeline)
        {
            if (!removals.TryGetValue(clip.Id, out var intervals))
            {
                timeline.Add(clip);
                continue;
            }
            var cursor = clip.In;
            var part = 0;
            foreach (var interval in intervals)
            {
                if (cursor < interval.Start) timeline.Add(Part(clip, cursor, interval.Start, ref part, usedIds));
                cursor = Math.Max(cursor, interval.End);
            }
            if (cursor < clip.Out) timeline.Add(Part(clip, cursor, clip.Out, ref part, usedIds));
        }
        var edited = project with { Revision = checked(project.Revision + 1), Timeline = timeline.ToArray(), Proposals = [] };
        ProjectValidator.EnsureValid(edited);
        return edited;
    }

    private static (long Start, long End)[] Merge(IEnumerable<(long Start, long End)> source)
    {
        var ordered = source.OrderBy(item => item.Start).ThenBy(item => item.End).ToArray();
        var merged = new List<(long Start, long End)>();
        foreach (var interval in ordered)
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End) merged.Add(interval);
            else merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.End));
        }
        return merged.ToArray();
    }

    private static TimelineClip Part(TimelineClip source, long start, long end, ref int part, HashSet<string> usedIds)
    {
        string id;
        if (part++ == 0) id = source.Id;
        else
        {
            var suffix = part;
            do { id = $"{source.Id}-analysis-{suffix++}"; } while (!usedIds.Add(id));
        }
        return source with { Id = id, In = start, Out = end };
    }
}
