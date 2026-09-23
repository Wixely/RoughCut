using System.Text.Json;
using System.Text.Json.Serialization;
using RoughCut.Media;

namespace RoughCut.Application;

public sealed record ExportJob(
    int SchemaVersion, string JobId, string Status, DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc, DateTimeOffset? FinishedUtc, int ProgressPercent,
    string ProjectPath, string OutputDirectory, bool AllowEncoding, string? Message,
    // Absent in checkpoints written before delivery existed, which read back as the strict path they used.
    string Mode = "strict");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ExportJob))]
[JsonSerializable(typeof(AcquisitionResult))]
[JsonSerializable(typeof(UrlProjectResult))]
public partial class ApplicationJson : JsonSerializerContext;

public sealed class ExportJobManager : IDisposable
{
    private const int MaxJobs = 100;
    private readonly WorkspaceBoundary _workspace;
    private readonly string _jobRoot;
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, ExportJob> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private bool _disposed;

    public ExportJobManager(WorkspaceBoundary workspace, string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
    {
        _workspace = workspace;
        _ffmpeg = ffmpeg;
        _ffprobe = ffprobe;
        _jobRoot = Path.Combine(workspace.Root, ".roughcut", "jobs");
        Directory.CreateDirectory(_jobRoot);
        LoadCheckpoints();
    }

    public ExportJob Start(string projectPath, string outputDirectory, bool allowEncoding, string mode = "strict")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mode is not ("strict" or "delivery")) throw new ArgumentException("Export mode must be strict or delivery.");
        _workspace.Resolve(projectPath);
        var output = _workspace.Resolve(outputDirectory, mustExist: false);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Output destination already exists.");
        lock (_sync)
        {
            if (_jobs.Count >= MaxJobs) throw new InvalidOperationException("Job retention limit reached; remove completed checkpoints before starting more work.");
            var job = new ExportJob(1, Guid.NewGuid().ToString("N"), "queued", DateTimeOffset.UtcNow,
                null, null, 0, Normalize(projectPath), Normalize(outputDirectory), allowEncoding,
                "Waiting for the export worker.", mode);
            var cancellation = new CancellationTokenSource();
            _jobs.Add(job.JobId, job);
            _cancellations.Add(job.JobId, cancellation);
            Save(job);
            _ = RunAsync(job.JobId, cancellation.Token);
            return job;
        }
    }

    public ExportJob Get(string jobId)
    {
        lock (_sync) return _jobs.TryGetValue(jobId, out var job) ? job : throw new KeyNotFoundException("Export job was not found.");
    }

    public ExportJob Cancel(string jobId)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) throw new KeyNotFoundException("Export job was not found.");
            if (job.Status is "queued" or "running")
            {
                _cancellations[jobId].Cancel();
                job = job with { Message = "Cancellation requested." };
                _jobs[jobId] = job;
                Save(job);
            }
            return job;
        }
    }

    private async Task RunAsync(string jobId, CancellationToken token)
    {
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                var job = Update(jobId, current => current with
                {
                    Status = "running",
                    StartedUtc = DateTimeOffset.UtcNow,
                    ProgressPercent = 10,
                    Message = current.Mode == "delivery"
                        ? "Re-encoding the requested timeline into a delivery file."
                        : "Exporting and validating the requested timeline."
                });
                var delivery = job.Mode == "delivery";
                var projectPath = _workspace.Resolve(job.ProjectPath);
                var destination = _workspace.Resolve(job.OutputDirectory, mustExist: false);
                if (delivery) await new DeliveryExporter(_ffmpeg, _ffprobe).ExportAsync(projectPath, destination, token);
                else await new ExportEngine(_ffmpeg, _ffprobe).ExportAsync(projectPath, destination, job.AllowEncoding, token);
                Update(jobId, current => current with
                {
                    Status = "succeeded",
                    FinishedUtc = DateTimeOffset.UtcNow,
                    ProgressPercent = 100,
                    Message = delivery ? "Delivery bundle published." : "Validated export bundle published."
                });
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException)
        {
            Update(jobId, current => current with
            {
                Status = "cancelled",
                FinishedUtc = DateTimeOffset.UtcNow,
                Message = "Export cancelled; no completed bundle was published."
            });
        }
        catch (Exception exception)
        {
            Update(jobId, current => current with
            {
                Status = "failed",
                FinishedUtc = DateTimeOffset.UtcNow,
                Message = SafeMessage(exception)
            });
        }
        finally
        {
            lock (_sync)
            {
                if (_cancellations.Remove(jobId, out var source)) source.Dispose();
            }
        }
    }

    private ExportJob Update(string id, Func<ExportJob, ExportJob> change)
    {
        lock (_sync)
        {
            var updated = change(_jobs[id]);
            _jobs[id] = updated;
            Save(updated);
            return updated;
        }
    }

    private void LoadCheckpoints()
    {
        foreach (var path in Directory.EnumerateFiles(_jobRoot, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(MaxJobs))
        {
            try
            {
                var job = JsonSerializer.Deserialize(File.ReadAllBytes(path), ApplicationJson.Default.ExportJob);
                if (job is null || job.SchemaVersion != 1 || Path.GetFileNameWithoutExtension(path) != job.JobId) continue;
                if (job.Status is "queued" or "running")
                {
                    job = job with { Status = "failed", FinishedUtc = DateTimeOffset.UtcNow, Message = "Host stopped before this job completed; start a new export to retry." };
                    Save(job);
                }
                _jobs[job.JobId] = job;
            }
            catch (JsonException) { }
        }
    }

    private void Save(ExportJob job)
    {
        var destination = Path.Combine(_jobRoot, job.JobId + ".json");
        var temporary = destination + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, job, ApplicationJson.Default.ExportJob);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
    private static string SafeMessage(Exception exception) => exception switch
    {
        ExportRejectedException rejected => "Export rejected: " + string.Join("; ", rejected.Plan.Issues.Select(issue => issue.Message)),
        DeliveryRejectedException rejected => "Delivery rejected: " + string.Join("; ", rejected.Plan.Issues.Select(issue => issue.Message)),
        IOException or ArgumentException or NotSupportedException or InvalidDataException => exception.Message,
        _ => "Export failed; check the project, media tools and available disk space."
    };

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var cancellation in _cancellations.Values) cancellation.Cancel();
        }
    }
}
