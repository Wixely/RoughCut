using System.Numerics;
using System.Text.Json.Serialization;

namespace RoughCut.Core;

// Seconds = ticks * numerator / denominator. Never infer timing from nominal FPS.
public readonly record struct TimeBase(long Numerator, long Denominator)
{
    public static TimeBase Microseconds => new(1, 1_000_000);
    public bool IsValid => Numerator > 0 && Denominator > 0;
}

public readonly record struct MediaTime(long Ticks, TimeBase TimeBase) : IComparable<MediaTime>
{
    public int CompareTo(MediaTime other)
    {
        if (!TimeBase.IsValid || !other.TimeBase.IsValid)
            throw new ArgumentException("Time bases must be positive.");
        return ((BigInteger)Ticks * TimeBase.Numerator * other.TimeBase.Denominator)
            .CompareTo((BigInteger)other.Ticks * other.TimeBase.Numerator * TimeBase.Denominator);
    }
}

public sealed record MediaAsset(
    string Id, string Kind, string Path, string Sha256, long Duration,
    int Width, int Height, string MediaType);
public sealed record Crop(int X, int Y, int Width, int Height);
public sealed record TimelineClip(
    string Id, string AssetId, long In, long Out, Crop? Crop = null,
    string Fit = "contain", string Audio = "source");
public sealed record Speaker(string Id, string Label);
public sealed record SpeakerEdit(string Action, string? SpeakerId = null, string? TargetSpeakerId = null,
    string? Label = null, string[]? SegmentIds = null, string[]? SpeakerIds = null,
    bool Overlap = false, string Reason = "user-correction");
public sealed record SpeakerCorrection(string Id, string Action, string[] SegmentIds,
    string[] BeforeSpeakerIds, string[] AfterSpeakerIds, string? BeforeLabel,
    string? AfterLabel, string Reason, long Revision);
public sealed record SpeechSegment(
    string Id, string AssetId, long Start, long End, string Text, string[] SpeakerIds,
    string Assignment = "unknown", bool Overlap = false);
public sealed record DiarizationTurn(string SpeakerKey, long Start, long End);
public sealed record DiarizationSubmission(string AssetId, string Provider, string Model,
    DiarizationTurn[] Turns);
public sealed record DiarizationSpeaker(string Key, string SpeakerId);
public sealed record DiarizationProvenance(string AssetId, string SourceSha256, string Provider,
    string Model, string SubmissionSha256, DiarizationSpeaker[] Speakers, long Revision);
public sealed record DiarizationPlanResult(EditProject Project, int InferredSegments,
    int UnknownSegments, int PreservedCorrections);
public sealed record TranscriptionProvenance(string AssetId, string SourceSha256, string Provider,
    string Model, string Language, int ChunkSeconds);
public sealed record AnalysisEvidence(string Id, string AssetId, long Start, long End, string Kind,
    string Summary, string[] SpeechSegmentIds, long[] FrameTicks);
public sealed record AnalysisObservation(string Id, string AssetId, long Start, long End, string Label,
    string Summary, string Certainty, string RecommendedAction, string[] EvidenceIds);
public sealed record EditorialProposal(string Id, string ObservationId, string ClipId, long In, long Out,
    string RecommendedAction, string Decision, string Reason, string Certainty, string[] EvidenceIds);
public sealed record AnalysisProvenance(string AssetId, string SourceSha256, string Provider, string Model,
    string Prompt, string Policy, long ProposalRevision);
public sealed record AnalysisSubmission(string AssetId, string Provider, string Model,
    AnalysisEvidence[] Evidence, AnalysisObservation[] Observations);
public sealed record AnalysisPlanResult(EditProject Project, int RemoveDecisions, int ReviewDecisions, int RetainDecisions);
public sealed record VoiceMapping(string Id, string SpeakerId, string Provider, string Voice,
    string Model = "unspecified", string Language = "Auto");
public sealed record VoiceReplacement(
    string Id, string SegmentId, string MappingId, string Text, string? GeneratedAssetId = null,
    string State = "requested", string FitPolicy = "exact",
    string BackgroundPolicy = "require-isolated-dialogue");
public sealed record VoiceReplacementRequest(string Id, string SegmentId, string MappingId, string Text,
    string FitPolicy = "exact", string BackgroundPolicy = "require-isolated-dialogue");
public sealed record VoicePlanSubmission(VoiceMapping Mapping, VoiceReplacementRequest Replacement);
public sealed record SynthesisProvenance(string ReplacementId, string Provider, string Model,
    string Runtime, string Voice, string Language, string TextSha256, string AudioSha256,
    long RequestedDuration, long ActualDuration, string FitPolicy);
public sealed record VoicePreviewInfo(string ReplacementId, string AssetId, string State,
    string FitPolicy, long RequestedDuration, long ActualDuration, string MediaType);
public sealed record AssetProvenance(string AssetId, string Provider, string ModelVersion, string? SourceAssetId = null);
public sealed record TimelineMapping(string ClipId, string AssetId, long SourceIn, long SourceOut, long OutputIn, long OutputOut);
public sealed record ValidationIssue(string Code, string Location, string Message);

public sealed record EditProject
{
    public int SchemaVersion { get; init; } = 1;
    public string ProjectId { get; init; } = Guid.NewGuid().ToString("N");
    public long Revision { get; init; } = 1;
    public TimeBase TimeBase { get; init; } = TimeBase.Microseconds;
    public string Prompt { get; init; } = "";
    public MediaAsset[] Assets { get; init; } = [];
    public TimelineClip[] Timeline { get; init; } = [];
    public Speaker[] Speakers { get; init; } = [];
    public SpeakerCorrection[] SpeakerCorrections { get; init; } = [];
    public SpeechSegment[] Speech { get; init; } = [];
    public TranscriptionProvenance? Transcription { get; init; }
    public DiarizationProvenance? Diarization { get; init; }
    public AnalysisEvidence[] Evidence { get; init; } = [];
    public AnalysisObservation[] Observations { get; init; } = [];
    public EditorialProposal[] Proposals { get; init; } = [];
    public AnalysisProvenance? Analysis { get; init; }
    public VoiceMapping[] Voices { get; init; } = [];
    public VoiceReplacement[] Replacements { get; init; } = [];
    public SynthesisProvenance[] Synthesis { get; init; } = [];
    public AssetProvenance[] Provenance { get; init; } = [];
    public CaptionTrack? Captions { get; init; }
    public string ExportMode { get; init; } = "prefer-stream-copy";
}

public sealed record FrameRequest(string AssetId, MediaTime Timestamp, int MaxWidth = 1280);
public sealed record FrameInfo(
    string AssetId, string SourceSha256, MediaTime Requested,
    MediaTime Actual, MediaTime Duration, long StreamStartTicks, int FrameIndex,
    int Width, int Height, string MediaType, string ColourConversion);
public sealed record TimelineFrameInfo(
    string ProjectId, long Revision, string ClipId, string AssetId, string AssetKind,
    MediaTime Requested, MediaTime Actual, MediaTime Duration, MediaTime? SourceActual,
    int Width, int Height, string MediaType, string Fit, Crop? Crop);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(EditProject))]
[JsonSerializable(typeof(ValidationIssue[]))]
[JsonSerializable(typeof(TimelineMapping[]))]
[JsonSerializable(typeof(FrameInfo))]
[JsonSerializable(typeof(TimelineFrameInfo))]
[JsonSerializable(typeof(EditOperation[]))]
[JsonSerializable(typeof(SpeakerEdit[]))]
[JsonSerializable(typeof(DiarizationSubmission))]
[JsonSerializable(typeof(DiarizationPlanResult))]
[JsonSerializable(typeof(VoiceMapping))]
[JsonSerializable(typeof(VoiceReplacementRequest))]
[JsonSerializable(typeof(VoicePlanSubmission))]
[JsonSerializable(typeof(VoicePreviewInfo))]
[JsonSerializable(typeof(CaptionCandidate[]))]
[JsonSerializable(typeof(CaptionSelectionResult))]
[JsonSerializable(typeof(AnalysisSubmission))]
[JsonSerializable(typeof(AnalysisPlanResult))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ExportPlan))]
[JsonSerializable(typeof(ExportReport))]
[JsonSerializable(typeof(DeliveryPlan))]
[JsonSerializable(typeof(DeliveryReport))]
public partial class ProjectJson : JsonSerializerContext;
