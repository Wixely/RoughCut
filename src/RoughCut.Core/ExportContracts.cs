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
public sealed record ExportReport(int ReportVersion, ExportPlan Plan, string SourceSha256,
    string OutputSha256, string FfmpegVersion, string FfprobeVersion, ExportValidation Validation,
    OutputCaption[] Captions, string CaptionRounding = "SRT start floors and end ceilings to milliseconds; JSON retains exact rational timing.");
