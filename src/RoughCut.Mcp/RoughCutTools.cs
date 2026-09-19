using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoughCut.Application;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Mcp;

[McpServerToolType]
public sealed class RoughCutTools(RoughCutOperations operations, ExportJobManager jobs)
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

    [McpServerTool(Name = "roughcut_acquire_url")]
    [Description("Acquire one HTTP(S) video plus available subtitles through a configured standalone yt-dlp into a new bounded workspace directory.")]
    public Task<CallToolResult> AcquireAsync(string sourceUrl, string destinationDirectory,
        CancellationToken cancellationToken, string? denoPath = null)
        => TextAsync(() => operations.AcquireAsync(sourceUrl, destinationDirectory, denoPath, cancellationToken),
            ApplicationJson.Default.AcquisitionResult);

    [McpServerTool(Name = "roughcut_import_image")]
    [Description("Decode and validate a bounded base64 PNG, store it as a content-addressed portable project asset, and advance the expected revision.")]
    public Task<CallToolResult> ImportImageAsync(string projectPath, string assetId, string base64Png,
        long expectedRevision, CancellationToken cancellationToken, string provider = "mcp-client", string modelVersion = "unspecified")
        => TextAsync(() => operations.ImportPngAsync(projectPath, assetId, base64Png, expectedRevision,
            provider, modelVersion, cancellationToken), ProjectJson.Default.EditProject);

    [McpServerTool(Name = "roughcut_preflight_export", ReadOnly = true)]
    [Description("Resolve exact export boundaries and return explicit supported/unsupported stream actions without writing output.")]
    public Task<CallToolResult> PreflightAsync(string projectPath, CancellationToken cancellationToken)
        => TextAsync(() => operations.PreflightAsync(projectPath, cancellationToken), ProjectJson.Default.ExportPlan);

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
        IOException or ArgumentException or JsonException or NotSupportedException or KeyNotFoundException or
        InvalidOperationException or OverflowException => exception.Message,
        OperationCanceledException => "Operation cancelled.",
        _ => "Operation failed; check workspace inputs and media-tool availability."
    };
}
