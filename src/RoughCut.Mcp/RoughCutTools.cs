using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoughCut.Application;
using RoughCut.Core;
using RoughCut.Speech.Whisper;
using RoughCut.Speech.Sherpa;
using RoughCut.Media;

namespace RoughCut.Mcp;

[McpServerToolType]
public sealed class RoughCutTools(RoughCutOperations operations, ExportJobManager jobs, SpeechSettings speech,
    DiarizationSettings diarization, QwenSettings qwen)
{
    [McpServerTool(Name = "roughcut_read_project", ReadOnly = true)]
    [Description("Read and validate a workspace-relative RoughCut project, including its current revision.")]
    public Task<CallToolResult> ReadProjectAsync([Description("Workspace-relative project JSON path.")] string projectPath,
        CancellationToken cancellationToken) => TextAsync(() => operations.ReadProjectAsync(projectPath, cancellationToken), ProjectJson.Default.EditProject);

    [McpServerTool(Name = "roughcut_inspect_video", ReadOnly = true)]
    [Description("Inspect a workspace-relative local video with bounded FFprobe processing.")]
    public Task<CallToolResult> InspectVideoAsync([Description("Workspace-relative video path.")] string mediaPath,
        CancellationToken cancellationToken) => TextAsync(() => operations.InspectAsync(mediaPath, cancellationToken), MediaJson.Default.VideoInfo);

    [McpServerTool(Name = "roughcut_create_project")]
    [Description("Create a portable project for a video already inside the configured workspace.")]
    public Task<CallToolResult> CreateProjectAsync(string mediaPath, string projectPath, CancellationToken cancellationToken)
        => TextAsync(() => operations.CreateProjectAsync(mediaPath, projectPath, cancellationToken), ProjectJson.Default.EditProject);

    [McpServerTool(Name = "roughcut_get_frame", ReadOnly = true)]
    [Description("Return an actual PNG image from a valid source timestamp plus precise requested/actual timing metadata.")]
    public async Task<CallToolResult> GetFrameAsync(string projectPath, string assetId, long timestampTicks,
        long timeBaseNumerator, long timeBaseDenominator, int maxWidth, CancellationToken cancellationToken)
    {
        try
        {
            var frame = await operations.GetFrameAsync(projectPath, assetId, timestampTicks,
                timeBaseNumerator, timeBaseDenominator, maxWidth, cancellationToken);
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = JsonSerializer.Serialize(frame.Info, ProjectJson.Default.FrameInfo) },
                    ImageContentBlock.FromBytes(frame.Png, "image/png")
                ]
            };
        }
        catch (Exception exception) { return Error(exception); }
    }

    [McpServerTool(Name = "roughcut_get_timeline_frame", ReadOnly = true)]
    [Description("Return the rendered PNG at a timeline timestamp for an exact project revision, including cuts, order, crop and fitted images.")]
    public async Task<CallToolResult> GetTimelineFrameAsync(string projectPath, long expectedRevision, long timelineTicks,
        int maxWidth, CancellationToken cancellationToken)
    {
        try
        {
            var frame = await operations.GetTimelineFrameAsync(projectPath, expectedRevision, timelineTicks, maxWidth, cancellationToken);
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = JsonSerializer.Serialize(frame.Info, ProjectJson.Default.TimelineFrameInfo) },
                    ImageContentBlock.FromBytes(frame.Png, "image/png")
                ]
            };
        }
        catch (Exception exception) { return Error(exception); }
    }

    [McpServerTool(Name = "roughcut_apply_edits")]
    [Description("Apply one atomic edit batch when expectedRevision matches; stale writers receive a revision conflict.")]
    public Task<CallToolResult> ApplyEditsAsync(string projectPath, long expectedRevision, EditOperation[] edits,
        CancellationToken cancellationToken) => TextAsync(
            () => operations.ApplyEditsAsync(projectPath, expectedRevision, edits, cancellationToken), ProjectJson.Default.EditProject);

    [McpServerTool(Name = "roughcut_import_captions")]
    [Description("Import a bounded UTF-8 SRT already inside the workspace and advance the project revision.")]
    public Task<CallToolResult> ImportCaptionsAsync(string projectPath, string assetId, string captionPath,
        long expectedRevision, CancellationToken cancellationToken) => TextAsync(
            () => operations.ImportCaptionsAsync(projectPath, assetId, captionPath, expectedRevision, cancellationToken), ProjectJson.Default.EditProject);

    [McpServerTool(Name = "roughcut_select_captions")]
    [Description("Assess bounded project-local SRT candidates by provenance, language and coverage, then revision-safely select the recommendation or an explicit override.")]
    public Task<CallToolResult> SelectCaptionsAsync(string projectPath, string assetId, CaptionCandidate[] candidates,
        long expectedRevision, string preferredLanguage, CancellationToken cancellationToken, string? overrideCandidateId = null)
        => TextAsync(() => operations.SelectCaptionsAsync(projectPath, assetId, candidates, expectedRevision,
            preferredLanguage, overrideCandidateId, cancellationToken), ProjectJson.Default.CaptionSelectionResult);

    [McpServerTool(Name = "roughcut_save_analysis")]
    [Description("Validate and persist provider-neutral source-time evidence and observations, then create conservative revision-bound editorial proposals. Uncertain removals remain review items unless the explicit auto-high-certainty policy applies.")]
    public Task<CallToolResult> SaveAnalysisAsync(string projectPath, long expectedRevision, string prompt,
        string policy, AnalysisSubmission submission, CancellationToken cancellationToken)
        => TextAsync(() => operations.SaveAnalysisAsync(projectPath, expectedRevision, prompt, policy, submission, cancellationToken),
            ProjectJson.Default.AnalysisPlanResult);

    [McpServerTool(Name = "roughcut_apply_analysis")]
    [Description("Apply removal decisions from the exact current analysis revision. Omit proposalIds to apply only auto-approved removals; supply IDs for an explicit reviewed selection.")]
    public Task<CallToolResult> ApplyAnalysisAsync(string projectPath, long expectedRevision,
        CancellationToken cancellationToken, string[]? proposalIds = null)
        => TextAsync(() => operations.ApplyAnalysisAsync(projectPath, expectedRevision, proposalIds, cancellationToken),
            ProjectJson.Default.EditProject);

    [McpServerTool(Name = "roughcut_edit_speakers")]
    [Description("Atomically add, rename, assign or merge stable speaker labels and persist correction history against the expected revision.")]
    public Task<CallToolResult> EditSpeakersAsync(string projectPath, long expectedRevision, SpeakerEdit[] edits,
        CancellationToken cancellationToken) => TextAsync(
            () => operations.ApplySpeakerEditsAsync(projectPath, expectedRevision, edits, cancellationToken),
            ProjectJson.Default.EditProject);

    [McpServerTool(Name = "roughcut_save_diarization")]
    [Description("Validate and persist provider-neutral speaker turns, assign stable project speaker IDs to uncorrected transcript segments, preserve manual corrections, and record source/model provenance.")]
    public Task<CallToolResult> SaveDiarizationAsync(string projectPath, long expectedRevision,
        DiarizationSubmission submission, CancellationToken cancellationToken) => TextAsync(
            () => operations.SaveDiarizationAsync(projectPath, expectedRevision, submission, cancellationToken),
            ProjectJson.Default.DiarizationPlanResult);

    [McpServerTool(Name = "roughcut_diarize_local")]
    [Description("Run configured local sherpa-onnx diarization for one workspace source, then persist stable speaker assignments and model/source provenance against the expected revision.")]
    public async Task<CallToolResult> DiarizeLocalAsync(string projectPath, string assetId, long expectedRevision,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(diarization.SegmentationModelPath) ||
                string.IsNullOrWhiteSpace(diarization.EmbeddingModelPath))
                throw new InvalidOperationException("Local diarization requires ROUGHCUT_DIARIZATION_SEGMENTATION_MODEL and ROUGHCUT_DIARIZATION_EMBEDDING_MODEL on the MCP host.");
            var provider = new IsolatedSherpaSpeakerDiarizer(diarization.WorkerPath,
                diarization.SegmentationModelPath, diarization.EmbeddingModelPath,
                diarization.SpeakerCount, diarization.Threshold);
            var result = await operations.DiarizeAsync(projectPath, assetId, expectedRevision, provider, cancellationToken);
            return new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result, ProjectJson.Default.DiarizationPlanResult) }] };
        }
        catch (Exception exception) { return Error(exception); }
    }

    [McpServerTool(Name = "roughcut_plan_voice_replacement")]
    [Description("Create a Qwen TTS voice mapping and reversible exact or bounded time-stretch replacement request for corrected, non-overlapping isolated dialogue.")]
    public Task<CallToolResult> PlanVoiceReplacementAsync(string projectPath, long expectedRevision,
        VoicePlanSubmission submission, CancellationToken cancellationToken) => TextAsync(
            () => operations.PlanVoiceAsync(projectPath, expectedRevision, submission, cancellationToken),
            ProjectJson.Default.EditProject);

    [McpServerTool(Name = "roughcut_import_voice_preview")]
    [Description("Import bounded base64 PCM WAVE output from a Qwen TTS runtime as a content-addressed preview with model/runtime/text/audio/duration provenance.")]
    public Task<CallToolResult> ImportVoicePreviewAsync(string projectPath, long expectedRevision,
        string replacementId, string base64Wav, string runtime, CancellationToken cancellationToken) => TextAsync(
            () => operations.ImportVoicePreviewAsync(projectPath, expectedRevision, replacementId, base64Wav, runtime, cancellationToken),
            ProjectJson.Default.EditProject);

    [McpServerTool(Name = "roughcut_synthesize_voice")]
    [Description("Call the configured loopback Qwen TTS service for a requested replacement, validate bounded PCM WAVE output, and persist a revision-safe preview with runtime provenance.")]
    public async Task<CallToolResult> SynthesizeVoiceAsync(string projectPath, long expectedRevision,
        string replacementId, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(qwen.Endpoint))
                throw new InvalidOperationException("Live Qwen synthesis requires ROUGHCUT_QWEN_ENDPOINT on the MCP host.");
            using var synthesizer = new QwenSpeechClient(qwen.Endpoint, qwen.ApiKey, TimeSpan.FromSeconds(qwen.TimeoutSeconds));
            var project = await operations.SynthesizeVoiceAsync(projectPath, expectedRevision,
                replacementId, synthesizer, cancellationToken);
            return new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(project, ProjectJson.Default.EditProject) }] };
        }
        catch (Exception exception) { return Error(exception); }
    }

    [McpServerTool(Name = "roughcut_get_voice_preview", ReadOnly = true)]
    [Description("Return generated voice preview audio and exact requested/actual duration metadata for an expected project revision.")]
    public async Task<CallToolResult> GetVoicePreviewAsync(string projectPath, long expectedRevision,
        string replacementId, CancellationToken cancellationToken)
    {
        try
        {
            var preview = await operations.GetVoicePreviewAsync(projectPath, expectedRevision, replacementId, cancellationToken);
            return new()
            {
                Content =
                [
                    new TextContentBlock { Text = JsonSerializer.Serialize(preview.Info, ProjectJson.Default.VoicePreviewInfo) },
                    AudioContentBlock.FromBytes(preview.Wav, "audio/wav")
                ]
            };
        }
        catch (Exception exception) { return Error(exception); }
    }

    [McpServerTool(Name = "roughcut_set_voice_replacement_state")]
    [Description("Apply or revert a voice preview. Exact fit requires equal duration; time-stretch accepts 0.8x to 1.25x and renders only through encoding-authorized export.")]
    public Task<CallToolResult> SetVoiceReplacementStateAsync(string projectPath, long expectedRevision,
        string replacementId, string state, CancellationToken cancellationToken) => TextAsync(
            () => operations.SetVoiceStateAsync(projectPath, expectedRevision, replacementId, state, cancellationToken),
            ProjectJson.Default.EditProject);

    [McpServerTool(Name = "roughcut_list_source_formats", ReadOnly = true)]
    [Description("List the renditions a source URL offers — identifier, size, bitrate, resolution and codecs — without downloading any of them. Choose one and pass its identifier to an acquisition, or pass a policy name: highest, medium or lowest bitrate within the download bound.")]
    public Task<CallToolResult> ListSourceFormatsAsync(string sourceUrl, CancellationToken cancellationToken,
        string? denoPath = null)
        => TextAsync(() => operations.ListSourceFormatsAsync(sourceUrl, denoPath ?? ToolSettings.Default.Deno,
            cancellationToken), ApplicationJson.Default.SourceFormatList);

    [McpServerTool(Name = "roughcut_acquire_url")]
    [Description("Acquire one HTTP(S) video plus available subtitles through a configured standalone yt-dlp into a new bounded workspace directory.")]
    public Task<CallToolResult> AcquireAsync(string sourceUrl, string destinationDirectory,
        CancellationToken cancellationToken, string? denoPath = null,
        [Description("A format identifier from roughcut_list_source_formats, two joined by '+', or a policy: highest, medium or lowest. Defaults to medium.")]
        string? format = null)
        => TextAsync(() => operations.AcquireAsync(sourceUrl, destinationDirectory,
            denoPath ?? ToolSettings.Default.Deno, cancellationToken, format),
            ApplicationJson.Default.AcquisitionResult);

    [McpServerTool(Name = "roughcut_create_project_from_url")]
    [Description("Acquire one HTTP(S) video plus its subtitles, create a project beside the downloaded media and select the best caption track, in one bounded step. Use this instead of composing acquire, create and select_captions by hand; the yt-dlp subtitle and format policy is fixed by RoughCut.")]
    public Task<CallToolResult> CreateProjectFromUrlAsync(string sourceUrl, string destinationDirectory,
        CancellationToken cancellationToken, string? denoPath = null, string preferredLanguage = "en",
        [Description("A format identifier from roughcut_list_source_formats, two joined by '+', or a policy: highest, medium or lowest. Defaults to medium.")]
        string? format = null)
        => TextAsync(() => operations.CreateProjectFromUrlAsync(sourceUrl, destinationDirectory,
            denoPath ?? ToolSettings.Default.Deno, preferredLanguage, cancellationToken, format),
            ApplicationJson.Default.UrlProjectResult);

    [McpServerTool(Name = "roughcut_fetch_speech_model")]
    [Description("Fetch the pinned base.en speech model if it is not already here, checking it against its published size and SHA-256, and report where it sits. Transcription refuses when the model is absent rather than downloading it unannounced, so call this once first.")]
    public async Task<CallToolResult> FetchSpeechModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.GetFullPath(speech.ModelPath!);
            using var transcriber = new WhisperLocalSpeechTranscriber(path, speech.Language);
            await transcriber.EnsureModelAsync(cancellationToken);
            var file = new FileInfo(path);
            return new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(
                new SpeechModelStatus(path, file.Length, true), McpJson.Default.SpeechModelStatus) }] };
        }
        catch (Exception exception) { return Error(exception); }
    }

    [McpServerTool(Name = "roughcut_transcribe_local")]
    [Description("Transcribe one project media asset locally with the configured pinned Whisper base.en model, persist timed speech/captions, and advance the expected revision.")]
    public async Task<CallToolResult> TranscribeLocalAsync(string projectPath, string assetId, long expectedRevision,
        CancellationToken cancellationToken,
        [Description("Seconds of audio before each chunk given to the model as context and then discarded, 0 to half the chunk. Models invent a fragment at the start of a window, so without it every chunk boundary fabricates speech. Defaults to 3.")]
        int? overlapSeconds = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(speech.ModelPath))
                throw new InvalidOperationException("Local STT has no model path; set ROUGHCUT_STT_MODEL or call roughcut_fetch_speech_model.");
            // The model is fetched deliberately, never as a surprise inside a transcribe: a caller who has
            // not got one is told how to get it rather than waiting on an unannounced download.
            if (!File.Exists(Path.GetFullPath(speech.ModelPath)))
                throw new InvalidOperationException(
                    $"No speech model at {speech.ModelPath}; call roughcut_fetch_speech_model, or set ROUGHCUT_STT_MODEL to one you already have.");
            using var transcriber = new WhisperLocalSpeechTranscriber(Path.GetFullPath(speech.ModelPath), speech.Language);
            var project = await operations.TranscribeLocalAsync(projectPath, assetId, expectedRevision,
                transcriber, speech.ChunkSeconds, cancellationToken, overlapSeconds);
            return new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(project, ProjectJson.Default.EditProject) }] };
        }
        catch (Exception exception) { return Error(exception); }
    }

    [McpServerTool(Name = "roughcut_create_project_folder")]
    [Description("Create one edit in a folder of its own: the chosen workspace video is copied into a new folder and a project file is created beside it, so the media, the project and its generated assets sit together. Use this rather than roughcut_create_project when the media is not already where the edit should live.")]
    public Task<CallToolResult> CreateProjectFolderAsync(string mediaPath, string folderPath,
        CancellationToken cancellationToken)
        => TextAsync(() => operations.CreateProjectFolderAsync(mediaPath, folderPath, null, cancellationToken),
            ApplicationJson.Default.FolderProjectResult);

    [McpServerTool(Name = "roughcut_import_image")]
    [Description("Decode and validate a bounded base64 PNG, store it as a content-addressed portable project asset, and advance the expected revision.")]
    public Task<CallToolResult> ImportImageAsync(string projectPath, string assetId, string base64Png,
        long expectedRevision, CancellationToken cancellationToken, string provider = "mcp-client", string modelVersion = "unspecified")
        => TextAsync(() => operations.ImportPngAsync(projectPath, assetId, base64Png, expectedRevision,
            provider, modelVersion, cancellationToken), ProjectJson.Default.EditProject);

    [McpServerTool(Name = "roughcut_profile_audio", ReadOnly = true)]
    [Description("Measure a source's sound over a range, one window at a time: level and peak in dBFS, the share of energy below a band split, and how much of the window is silent. Music carrying bass and drums shows a far higher low-band share than speech or room tone at the same level, so these separate sections a caller must otherwise guess at. Measurements only — deciding what they mean is the caller's.")]
    public Task<CallToolResult> ProfileAudioAsync(string projectPath, string assetId, long fromTicks,
        long toTicks, long windowTicks, CancellationToken cancellationToken,
        [Description("Band split in Hz, 20 to 2000. 200 Hz separates bass and drums from speech.")]
        int bandSplitHz = 200)
        => TextAsync(() => operations.ProfileAudioAsync(projectPath, assetId, fromTicks, toTicks, windowTicks,
            bandSplitHz, cancellationToken), MediaJson.Default.AudioProfile);

    [McpServerTool(Name = "roughcut_list_cut_points", ReadOnly = true)]
    [Description("List where a stream copy may begin or end near a time in one source: the keyframe anchors behind and ahead, how far each sits from the time you asked for, whether audio packets align there, and the source's own keyframe spacing. Read from a bounded window, so it is fast on long sources. Use it to decide between copying at an anchor and cutting exactly, which re-encodes the material from the preceding anchor up to the cut.")]
    public Task<CallToolResult> ListCutPointsAsync(string projectPath, string assetId, long atTicks,
        CancellationToken cancellationToken,
        [Description("Optional window in project ticks. Omitted, the window comes from the source's own keyframe spacing.")]
        long? windowTicks = null)
        => TextAsync(() => operations.ListCutPointsAsync(projectPath, assetId, atTicks, windowTicks, cancellationToken),
            MediaJson.Default.CutPoints);

    [McpServerTool(Name = "roughcut_preflight_export", ReadOnly = true)]
    [Description("Resolve exact export boundaries and return explicit supported/unsupported stream actions without writing output.")]
    public Task<CallToolResult> PreflightAsync(string projectPath, CancellationToken cancellationToken)
        => TextAsync(() => operations.PreflightAsync(projectPath, cancellationToken), ProjectJson.Default.ExportPlan);

    [McpServerTool(Name = "roughcut_preflight_delivery", ReadOnly = true)]
    [Description("Report whether the retained timeline can be re-encoded into one portable file, and the frame size, clips, duration and codecs it would deliver. Never writes output and never starts the media tools.")]
    public Task<CallToolResult> PreflightDeliveryAsync(string projectPath, CancellationToken cancellationToken,
        [Description("A delivery format from roughcut_list_export_formats: mp4, mkv, mov or webm. Defaults to mp4.")]
        string? format = null)
        => TextAsync(() => operations.PreflightDeliveryAsync(projectPath, cancellationToken, format), ProjectJson.Default.DeliveryPlan);

    [McpServerTool(Name = "roughcut_list_export_formats", ReadOnly = true)]
    [Description("List what this project can be exported as: the copy options that keep the source's own packets, whose cuts land on keyframes, and the re-encoded delivery formats, whose cuts land exactly. Use this rather than assuming a format is available.")]
    public Task<CallToolResult> ListExportFormatsAsync(string projectPath, CancellationToken cancellationToken)
        => TextAsync(() => operations.ListExportFormatsAsync(projectPath, cancellationToken), ApplicationJson.Default.ExportFormatList);

    [McpServerTool(Name = "roughcut_preflight_mux", ReadOnly = true)]
    [Description("Report where every cut would land if the timeline were copied rather than re-encoded: the anchor each boundary moves to, how far it moved, the copied length against the requested one, and the worst movement. Copying keeps the source's own packets untouched and is fast, but cuts land on keyframes. Use roughcut_list_cut_points first to see the anchors, and delivery instead when a cut must land exactly.")]
    public Task<CallToolResult> PreflightMuxAsync(string projectPath, CancellationToken cancellationToken,
        [Description("mkv, or mp4 where the source codecs allow it.")] string container = "mkv")
        => TextAsync(() => operations.PreflightMuxAsync(projectPath, container, cancellationToken), MediaJson.Default.MuxPlan);

    [McpServerTool(Name = "roughcut_start_mux")]
    [Description("Queue one durable job that copies the retained timeline into a new container without re-encoding it. Every retained packet is the source's own; cuts land on the anchors the preflight names. Output must be a new workspace-relative directory.")]
    public CallToolResult StartMux(string projectPath, string outputDirectory, string container = "mkv")
        => Run(() => Text(jobs.Start(projectPath, outputDirectory, allowEncoding: false, mode: "mux", container: container)));

    [McpServerTool(Name = "roughcut_start_delivery")]
    [Description("Queue one durable delivery job that re-encodes the timeline into the chosen format. Unlike the strict export this never copies source packets; it claims a faithful edit, not an untouched copy. Output must be a new workspace-relative directory.")]
    public CallToolResult StartDelivery(string projectPath, string outputDirectory,
        [Description("mp4 (H.264/AAC, the default), mkv, mov, or webm (VP9/Opus). To keep the source's own packets instead, use roughcut_start_mux.")]
        string format = "mp4")
        => Run(() => Text(jobs.Start(projectPath, outputDirectory, allowEncoding: true, mode: "delivery", format: format)));

    [McpServerTool(Name = "roughcut_start_export")]
    [Description("Queue one bounded, durable export job. Output must be a new workspace-relative directory.")]
    public CallToolResult StartExport(string projectPath, string outputDirectory, bool allowEncoding = false)
        => Run(() => Text(jobs.Start(projectPath, outputDirectory, allowEncoding)));

    [McpServerTool(Name = "roughcut_get_job", ReadOnly = true)]
    [Description("Read the durable state of an export job.")]
    public CallToolResult GetJob(string jobId) => Run(() => Text(jobs.Get(jobId)));

    [McpServerTool(Name = "roughcut_cancel_job")]
    [Description("Request cancellation of a queued or running export job.")]
    public CallToolResult CancelJob(string jobId) => Run(() => Text(jobs.Cancel(jobId)));

    private static async Task<CallToolResult> TextAsync<T>(Func<Task<T>> action, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try { return new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(await action(), type) }] }; }
        catch (Exception exception) { return Error(exception); }
    }

    private static CallToolResult Run(Func<CallToolResult> action)
    {
        try { return action(); }
        catch (Exception exception) { return Error(exception); }
    }

    private static CallToolResult Text(ExportJob job) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(job, ApplicationJson.Default.ExportJob) }]
    };

    private static CallToolResult Error(Exception exception) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = SafeMessage(exception) }]
    };

    private static string SafeMessage(Exception exception) => exception switch
    {
        RevisionConflictException => "Project revision conflict; reload the project and retry against its current revision.",
        ExportRejectedException rejected => "Export rejected: " + string.Join("; ", rejected.Plan.Issues.Select(issue => issue.Message)),
        DeliveryRejectedException rejected => "Delivery rejected: " + string.Join("; ", rejected.Plan.Issues.Select(issue => issue.Message)),
        MuxRejectedException rejected => "Copy rejected: " + string.Join("; ", rejected.Plan.Issues.Select(issue => issue.Message)),
        IOException or ArgumentException or JsonException or NotSupportedException or KeyNotFoundException or
        InvalidOperationException or OverflowException or HttpRequestException or TimeoutException => exception.Message,
        OperationCanceledException => "Operation cancelled.",
        _ => "Operation failed; check workspace inputs and media-tool availability."
    };
}
