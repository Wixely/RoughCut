namespace RoughCut.Core;

public sealed record StreamDecision(string Kind, string InputCodec, string OutputCodec, string Action, string Reason);
public sealed record ResolvedVoiceReplacement(string ReplacementId, string AssetId, long SourceIn, long SourceOut,
    long FirstSample, long EndSample, string FitPolicy, long InputSamples, int InputSampleRate, int InputChannels);
public sealed record ResolvedClip(string ClipId, string AssetId, long RequestedIn, long RequestedOut,
    long ResolvedIn, long ResolvedOut, long OutputIn, long OutputOut, int FirstFrame, int EndFrame,
    long FirstSample, long EndSample, Crop? Crop, ResolvedVoiceReplacement[] VoiceReplacements);
public sealed record ExportPlan(string ProjectId, long Revision, string ProjectSha256, string Mode,
    TimeBase TimeBase, bool Supported, bool RequiresEncoding, ValidationIssue[] Issues,
    StreamDecision[] Streams, ResolvedClip[] Clips, int Width, int Height, long Duration,
    string Policy = "matroska-lossless-timeline-v3");
public sealed record ExportValidation(int DecodedFrames, long AudioSamples, int Joins,
    bool VideoContentMatches, bool AudioContentMatches, bool PacketPayloadsMatch,
    long MaximumAudioTimestampErrorMicroseconds);
/// Delivery re-encodes the retained timeline into a portable file. It is deliberately not the strict
/// copy path: it claims a faithful edit and a checked duration, never an untouched copy of the source.
public sealed record DeliveryClip(string ClipId, string AssetId, long SourceIn, long SourceOut,
    long OutputIn, long OutputOut, Crop? Crop, string Fit, string Audio);
public sealed record DeliveryPlan(string ProjectId, long Revision, string ProjectSha256, bool Supported,
    ValidationIssue[] Issues, DeliveryClip[] Clips, int Width, int Height, long Duration,
    string VideoCodec = "h264", string AudioCodec = "aac", string Container = "mp4",
    string Policy = "delivery-encode-v1");
/// SilencedAssets names sources that asked for their own audio but carry none, so the delivered file
/// holds generated silence there. It is reported rather than rejected, and never inferred as an edit.
public sealed record DeliveryReport(int ReportVersion, DeliveryPlan Plan, string[] SourceSha256,
    string[] SilencedAssets, string OutputSha256, string FfmpegVersion, double ExpectedSeconds,
    double ActualSeconds, long OutputBytes, OutputCaption[] Captions,
    string Claim = "Cuts, ordering, crops and captions follow the timeline; every frame and sample is re-encoded.");

public sealed record ExportReport(int ReportVersion, ExportPlan Plan, string SourceSha256,
    string OutputSha256, string FfmpegVersion, string FfprobeVersion, ExportValidation Validation,
    OutputCaption[] Captions, string CaptionRounding = "SRT start floors and end ceilings to milliseconds; JSON retains exact rational timing.");
