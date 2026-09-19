using System.Globalization;
using System.Text;
using System.Text.Json;
using RoughCut.Core;

namespace RoughCut.Media;

public sealed class ExportEngine(string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
{
    private const long MaxArtifactBytes = 256L * 1024 * 1024;
    private static readonly TimeBase Milliseconds = new(1, 1000);

    public async Task<ExportReport> ExportAsync(string projectPath, string outputDirectory, bool allowEncoding = false,
        CancellationToken cancellationToken = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (File.Exists(outputDirectory) || Directory.Exists(outputDirectory))
            throw new IOException("Output directory already exists; choose a new destination.");
        var project = await new ProjectStore().LoadAsync(projectPath, cancellationToken);
        var prepared = await new ExportPlanner(ffmpeg, ffprobe).PrepareAsync(project, projectPath, cancellationToken);
        var plan = prepared.Plan;
        if (!plan.Supported || prepared.Source is not { } source) throw new ExportRejectedException(plan);
        if (plan.RequiresEncoding && !allowEncoding)
            throw new ExportRejectedException(plan with { Supported = false, Issues = [new("encoding-not-authorized", "export", "Preflight requires slower encoding; rerun export with --allow-encode after reviewing the plan.")] });
        var parent = Path.GetDirectoryName(outputDirectory)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".roughcut-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var videoList = new StringBuilder("ffconcat version 1.0\n");
            var audioList = new StringBuilder("ffconcat version 1.0\n");
            var copyVideo = plan.Streams.Single(s => s.Kind == "video").Action == "copy";
            var copyAudio = plan.Streams.Single(s => s.Kind == "audio").Action == "copy";
            for (int i = 0; i < plan.Clips.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clip = plan.Clips[i];
                var start = Seconds(clip.ResolvedIn, project.TimeBase);
                var duration = Seconds(clip.ResolvedOut - clip.ResolvedIn, project.TimeBase);
                var videoName = $"video-{i}.mkv";
                var audioName = $"audio-{i}.mka";
                var videoArgs = BaseArguments();
                AddInput(videoArgs, source.Path);
                videoArgs.AddRange(["-map", $"0:{source.Video.Info.StreamIndex}", "-an"]);
                if (copyVideo) videoArgs.AddRange(["-ss", start, "-t", duration, "-c:v", "copy"]);
                else videoArgs.AddRange(["-vf", VideoFilter(clip), "-fps_mode", "passthrough", "-c:v", "ffv1", "-level", "3", "-g", "1", "-pix_fmt", "bgr0", "-threads:v", "1"]);
                AddOutput(videoArgs, Path.Combine(staging, videoName), RemainingBudget(staging));
                await ToolProcess.RunAsync(ffmpeg, videoArgs, cancellationToken: cancellationToken);
                var audioArgs = BaseArguments();
                AddInput(audioArgs, source.Path);
                audioArgs.AddRange(["-map", $"0:{source.AudioIndex}", "-vn"]);
                // Output-side selection scans packets instead of inheriting video-keyframe seek preroll.
                if (copyAudio) audioArgs.AddRange(["-ss", start, "-t", duration, "-c:a", "copy"]);
                else
                {
                    var samplesPerFrame = TimeMath.ExactTicks(new(source.Video.Frames[0].Duration, source.Video.Info.TimeBase), new(1, source.SampleRate));
                    audioArgs.AddRange(["-af", FormattableString.Invariant($"atrim=start_sample={clip.FirstSample}:end_sample={clip.EndSample},asetpts=N/SR/TB,asetnsamples=n={samplesPerFrame}:p=0"), "-c:a", "pcm_s16le"]);
                }
                AddOutput(audioArgs, Path.Combine(staging, audioName), RemainingBudget(staging));
                await ToolProcess.RunAsync(ffmpeg, audioArgs, cancellationToken: cancellationToken);
                // Only generated ASCII filenames enter the concat syntax. User paths stay in argument arrays.
                videoList.Append("file ").Append(videoName).Append("\nduration ").Append(duration).Append('\n');
                audioList.Append("file ").Append(audioName).Append("\nduration ").Append(duration).Append('\n');
            }
            RemainingBudget(staging);
            var videoConcat = Path.Combine(staging, "video.ffconcat");
            var audioConcat = Path.Combine(staging, "audio.ffconcat");
            await File.WriteAllTextAsync(videoConcat, videoList.ToString(), new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(audioConcat, audioList.ToString(), new UTF8Encoding(false), cancellationToken);
            var output = Path.Combine(staging, "video.mkv");
            var mux = BaseArguments();
            foreach (var input in new[] { videoConcat, audioConcat })
                mux.AddRange(["-protocol_whitelist", "file", "-format_whitelist", "concat,matroska,webm", "-f", "concat", "-safe", "1", "-i", input]);
            mux.AddRange(["-map", "0:v:0", "-map", "1:a:0", "-c", "copy"]);
            AddOutput(mux, output, MaxArtifactBytes);
            await ToolProcess.RunAsync(ffmpeg, mux, cancellationToken: cancellationToken);
            if (new FileInfo(output).Length > MaxArtifactBytes)
                throw new InvalidDataException("Export exceeded the 256 MiB output budget.");
            var validation = await ValidateOutputAsync(source, project, plan, output, staging, cancellationToken);
            if (!string.Equals(await MediaReader.FingerprintAsync(source.Path, cancellationToken), source.Video.Info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Source changed during export; output was not published.");
            if (project.Captions is { } track && !string.Equals(await MediaReader.FingerprintAsync(ProjectFiles.Resolve(projectPath, track.SourcePath), cancellationToken), track.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Caption source changed during export.");
            var captions = Captions.Retime(project);
            var report = new ExportReport(1, plan, source.Video.Info.Sha256, await MediaReader.FingerprintAsync(output, cancellationToken),
                await VersionAsync(ffmpeg, cancellationToken), await VersionAsync(ffprobe, cancellationToken), validation, captions);
            await File.WriteAllTextAsync(Path.Combine(staging, "export.json"), JsonSerializer.Serialize(report, ProjectJson.Default.ExportReport), cancellationToken);
            if (project.Captions is not null)
                await File.WriteAllTextAsync(Path.Combine(staging, "captions.srt"), Captions.WriteSrt(captions), new UTF8Encoding(false), cancellationToken);
            foreach (var file in Directory.EnumerateFiles(staging))
                if (Path.GetFileName(file) is not ("video.mkv" or "export.json" or "captions.srt")) File.Delete(file);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, outputDirectory); // All three artifacts become visible together, with no overwrite.
            return report;
        }
        finally
        {
            // Only direct files created inside this unique staging directory are removed; no recursive deletion.
            if (Directory.Exists(staging))
            {
                foreach (var file in Directory.EnumerateFiles(staging)) File.Delete(file);
                Directory.Delete(staging);
            }
        }
    }

    private async Task<ExportValidation> ValidateOutputAsync(ExportSource source, EditProject project, ExportPlan plan,
        string output, string staging, CancellationToken cancellationToken)
    {
        var actual = await new ExportProbe(ffmpeg, ffprobe).ReadAsync(output, inspectPngPackets: false, cancellationToken);
        var frameCount = plan.Clips.Sum(c => c.EndFrame - c.FirstFrame);
        var sampleCount = plan.Clips.Sum(c => c.EndSample - c.FirstSample);
        if (actual.Video.Frames.Length != frameCount || actual.AudioSamples != sampleCount ||
            actual.Video.Info.Width != plan.Width || actual.Video.Info.Height != plan.Height ||
            actual.Channels != source.Channels || actual.SampleRate != source.SampleRate ||
            actual.Video.Info.Codec != plan.Streams.Single(s => s.Kind == "video").OutputCodec ||
            new MediaTime(actual.Video.Info.DurationTicks, actual.Video.Info.TimeBase).CompareTo(new(plan.Duration, project.TimeBase)) != 0)
            throw new InvalidDataException("Export frame/sample counts, dimensions, codec or duration do not match the plan.");
        int frameIndex = 0;
        foreach (var clip in plan.Clips)
        {
            for (int i = clip.FirstFrame; i < clip.EndFrame; i++)
            {
                var expected = TimeMath.Add(TimeMath.Subtract(new(source.Video.Frames[i].Pts, source.Video.Info.TimeBase),
                    new(clip.ResolvedIn, project.TimeBase)), new(clip.OutputIn, project.TimeBase));
                if (expected.CompareTo(new(actual.Video.Frames[frameIndex++].Pts, actual.Video.Info.TimeBase)) != 0)
                    throw new InvalidDataException("Export presentation timestamps differ from the resolved mapping.");
            }
        }
        var expectedHashes = new List<string>();
        foreach (var clip in plan.Clips)
            expectedHashes.AddRange(await FrameHashesAsync(source.Path, source.Video.Info.StreamIndex, VideoFilter(clip), cancellationToken));
        var actualHashes = await FrameHashesAsync(output, actual.Video.Info.StreamIndex, "format=rgb24", cancellationToken);
        if (expectedHashes.Count != frameCount || !expectedHashes.SequenceEqual(actualHashes))
            throw new InvalidDataException("Decoded export frames differ from the requested retained/cropped frames.");
        foreach (var stream in plan.Streams.Where(s => s.Action == "copy"))
        {
            var sourcePackets = stream.Kind == "video" ? source.VideoPackets : source.AudioPackets;
            var outputPackets = stream.Kind == "video" ? actual.VideoPackets : actual.AudioPackets;
            var timeBase = stream.Kind == "video" ? source.Video.Info.TimeBase : source.AudioTimeBase;
            var expectedPackets = plan.Clips.SelectMany(c => sourcePackets.Where(p =>
                new MediaTime(p.Pts, timeBase).CompareTo(new(c.ResolvedIn, project.TimeBase)) >= 0 &&
                new MediaTime(p.Pts, timeBase).CompareTo(new(c.ResolvedOut, project.TimeBase)) < 0)).ToArray();
            if (!expectedPackets.Select(p => (p.Hash, p.Size)).SequenceEqual(outputPackets.Select(p => (p.Hash, p.Size))))
                throw new InvalidDataException("Stream-copy packet payloads changed or packets were lost/duplicated.");
        }
        var sourcePcm = Path.Combine(staging, "source.pcm");
        var outputPcm = Path.Combine(staging, "output.pcm");
        await DecodePcmAsync(source.Path, source.AudioIndex, sourcePcm, cancellationToken);
        await DecodePcmAsync(output, actual.AudioIndex, outputPcm, cancellationToken);
        int sampleBytes = source.Channels * 2;
        if (new FileInfo(sourcePcm).Length != source.AudioSamples * sampleBytes || new FileInfo(outputPcm).Length != sampleCount * sampleBytes)
            throw new InvalidDataException("Decoded PCM sample count differs from packet metadata.");
        await using var sourceStream = File.OpenRead(sourcePcm);
        await using var outputStream = File.OpenRead(outputPcm);
        var expectedBuffer = new byte[65536];
        var actualBuffer = new byte[65536];
        foreach (var clip in plan.Clips)
        {
            sourceStream.Position = clip.FirstSample * sampleBytes;
            long remaining = (clip.EndSample - clip.FirstSample) * sampleBytes;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, expectedBuffer.Length);
                await sourceStream.ReadExactlyAsync(expectedBuffer.AsMemory(0, count), cancellationToken);
                await outputStream.ReadExactlyAsync(actualBuffer.AsMemory(0, count), cancellationToken);
                if (!expectedBuffer.AsSpan(0, count).SequenceEqual(actualBuffer.AsSpan(0, count)))
                    throw new InvalidDataException("Export audio samples differ from the requested source intervals.");
                remaining -= count;
            }
        }
        return new(frameCount, sampleCount, plan.Clips.Length - 1, true, true, true, actual.MaximumAudioTimestampErrorMicroseconds);
    }

    private async Task<string[]> FrameHashesAsync(string path, int index, string filter, CancellationToken cancellationToken)
    {
        var args = BaseArguments();
        AddInput(args, path);
        args.AddRange(["-map", $"0:{index}", "-an", "-vf", filter, "-fps_mode", "passthrough", "-c:v", "rawvideo",
            "-pix_fmt", "rgb24", "-f", "framehash", "-hash", "sha256", "pipe:1"]);
        var result = await ToolProcess.RunAsync(ffmpeg, args, cancellationToken: cancellationToken);
        return Encoding.UTF8.GetString(result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#')).Select(line => line.Split(',')[^1].Trim()).ToArray();
    }

    private async Task DecodePcmAsync(string path, int index, string destination, CancellationToken cancellationToken)
    {
        var args = BaseArguments();
        AddInput(args, path);
        args.AddRange(["-map", $"0:{index}", "-vn", "-c:a", "pcm_s16le", "-f", "s16le", "-fs", "16777216", destination]);
        await ToolProcess.RunAsync(ffmpeg, args, cancellationToken: cancellationToken);
    }

    private static string VideoFilter(ResolvedClip clip)
    {
        var filter = FormattableString.Invariant($"trim=start_frame={clip.FirstFrame}:end_frame={clip.EndFrame},setpts=PTS-STARTPTS,format=rgb24");
        if (clip.Crop is { } crop) filter += FormattableString.Invariant($",crop=w={crop.Width}:h={crop.Height}:x={crop.X}:y={crop.Y}:exact=1");
        return filter;
    }

    private static List<string> BaseArguments() => ["-v", "error", "-nostdin", "-xerror", "-n"];
    private static void AddInput(List<string> args, string path) => args.AddRange(["-protocol_whitelist", "file", "-format_whitelist", "matroska,webm", "-noautorotate", "-i", path]);
    private static void AddOutput(List<string> args, string path, long maxBytes) => args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-fs", maxBytes.ToString(CultureInfo.InvariantCulture), "-f", "matroska", path]);
    private static string Seconds(long ticks, TimeBase timeBase) => (TimeMath.ExactTicks(new(ticks, timeBase), Milliseconds) / 1000m).ToString("0.000", CultureInfo.InvariantCulture);
    private static long RemainingBudget(string staging)
    {
        long remaining = MaxArtifactBytes - Directory.EnumerateFiles(staging).Sum(f => new FileInfo(f).Length);
        if (remaining <= 0) throw new InvalidDataException("Export intermediate files exceeded the 256 MiB budget.");
        return remaining;
    }
    private static async Task<string> VersionAsync(string executable, CancellationToken cancellationToken)
    {
        var result = await ToolProcess.RunAsync(executable, ["-version"], cancellationToken: cancellationToken);
        return Encoding.UTF8.GetString(result.Output).Split('\n')[0].Trim();
    }
}

public sealed class ExportRejectedException(ExportPlan plan) : Exception("Export preflight rejected the request.")
{
    public ExportPlan Plan { get; } = plan;
}
