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
        var assets = project.Assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
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
            var fittedVoices = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var voice in plan.Clips.SelectMany(clip => clip.VoiceReplacements).DistinctBy(item => item.ReplacementId))
            {
                var fitted = Path.Combine(staging, $"voice-{fittedVoices.Count}.mka");
                await FitVoiceAsync(project, projectPath, source, voice, fitted, staging, cancellationToken);
                fittedVoices.Add(voice.ReplacementId, fitted);
            }
            for (int i = 0; i < plan.Clips.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clip = plan.Clips[i];
                var timelineClip = project.Timeline.Single(item => item.Id == clip.ClipId);
                var asset = assets[clip.AssetId];
                var start = Seconds(clip.ResolvedIn, project.TimeBase);
                var duration = Seconds(clip.ResolvedOut - clip.ResolvedIn, project.TimeBase);
                var videoName = $"video-{i}.mkv";
                var audioName = $"audio-{i}.mka";
                var videoArgs = BaseArguments();
                if (asset.Kind == "video")
                {
                    AddInput(videoArgs, source.Path);
                    videoArgs.AddRange(["-map", $"0:{source.Video.Info.StreamIndex}", "-an"]);
                    if (copyVideo) videoArgs.AddRange(["-ss", start, "-t", duration, "-c:v", "copy"]);
                    else videoArgs.AddRange(["-vf", VideoFilter(clip, timelineClip, asset, plan.Width, plan.Height), "-fps_mode", "passthrough",
                        "-c:v", "ffv1", "-level", "3", "-g", "1", "-pix_fmt", "bgr0", "-threads:v", "1"]);
                }
                else
                {
                    AddImageInput(videoArgs, ProjectFiles.Resolve(projectPath, asset.Path), source);
                    videoArgs.AddRange(["-map", "0:v:0", "-an", "-vf", VisualFilters.ForClip(timelineClip, asset, plan.Width, plan.Height),
                        "-frames:v", (clip.EndFrame - clip.FirstFrame).ToString(CultureInfo.InvariantCulture), "-fps_mode", "passthrough",
                        "-c:v", "ffv1", "-level", "3", "-g", "1", "-pix_fmt", "bgr0", "-threads:v", "1"]);
                }
                AddOutput(videoArgs, Path.Combine(staging, videoName), RemainingBudget(staging));
                await ToolProcess.RunAsync(ffmpeg, videoArgs, cancellationToken: cancellationToken);
                var audioArgs = BaseArguments();
                var samplesPerFrame = TimeMath.ExactTicks(new(source.Video.Frames[0].Duration, source.Video.Info.TimeBase), new(1, source.SampleRate));
                if (asset.Kind == "video")
                {
                    AddInput(audioArgs, source.Path);
                    if (clip.VoiceReplacements.Length == 0)
                    {
                        audioArgs.AddRange(["-map", $"0:{source.AudioIndex}", "-vn"]);
                        // Output-side selection scans packets instead of inheriting video-keyframe seek preroll.
                        if (copyAudio) audioArgs.AddRange(["-ss", start, "-t", duration, "-c:a", "copy"]);
                        else audioArgs.AddRange(["-af", FormattableString.Invariant($"atrim=start_sample={clip.FirstSample}:end_sample={clip.EndSample},asetpts=N/SR/TB,asetnsamples=n={samplesPerFrame}:p=0"), "-c:a", "pcm_s16le"]);
                    }
                    else
                    {
                        foreach (var voice in clip.VoiceReplacements) AddInput(audioArgs, fittedVoices[voice.ReplacementId]);
                        audioArgs.AddRange(["-filter_complex", VoiceClipFilter(clip, source, samplesPerFrame),
                            "-map", "[voiceout]", "-vn", "-c:a", "pcm_s16le"]);
                    }
                }
                else
                {
                    var layout = source.Channels == 1 ? "mono" : "stereo";
                    audioArgs.AddRange(["-f", "lavfi", "-i", $"anullsrc=r={source.SampleRate}:cl={layout}", "-map", "0:a:0", "-vn",
                        "-af", FormattableString.Invariant($"atrim=end_sample={clip.EndSample},asetpts=N/SR/TB,asetnsamples=n={samplesPerFrame}:p=0"), "-c:a", "pcm_s16le"]);
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
            var validation = await ValidateOutputAsync(source, project, projectPath, plan, output, staging, cancellationToken);
            if (!string.Equals(await MediaReader.FingerprintAsync(source.Path, cancellationToken), source.Video.Info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Source changed during export; output was not published.");
            if (project.Captions is { } track && !string.Equals(await MediaReader.FingerprintAsync(ProjectFiles.Resolve(projectPath, track.SourcePath), cancellationToken), track.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Caption source changed during export.");
            foreach (var image in project.Timeline.Select(clip => assets[clip.AssetId]).Where(asset => asset.Kind == "image").DistinctBy(asset => asset.Id))
                if (!string.Equals(await MediaReader.FingerprintAsync(ProjectFiles.Resolve(projectPath, image.Path), cancellationToken), image.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Image source changed during export.");
            foreach (var voice in plan.Clips.SelectMany(clip => clip.VoiceReplacements).DistinctBy(item => item.AssetId))
            {
                var asset = assets[voice.AssetId];
                if (!string.Equals(await MediaReader.FingerprintAsync(ProjectFiles.Resolve(projectPath, asset.Path), cancellationToken), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Generated voice source changed during export.");
            }
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

    private async Task<ExportValidation> ValidateOutputAsync(ExportSource source, EditProject project, string projectPath, ExportPlan plan,
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
        var assets = project.Assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        foreach (var clip in plan.Clips)
        {
            var asset = assets[clip.AssetId];
            for (int i = clip.FirstFrame; i < clip.EndFrame; i++)
            {
                var expected = asset.Kind == "video"
                    ? TimeMath.Add(TimeMath.Subtract(new(source.Video.Frames[i].Pts, source.Video.Info.TimeBase),
                        new(clip.ResolvedIn, project.TimeBase)), new(clip.OutputIn, project.TimeBase))
                    : TimeMath.Add(new(clip.OutputIn, project.TimeBase),
                        new(checked((long)i * source.Video.Frames[0].Duration), source.Video.Info.TimeBase));
                if (expected.CompareTo(new(actual.Video.Frames[frameIndex++].Pts, actual.Video.Info.TimeBase)) != 0)
                    throw new InvalidDataException("Export presentation timestamps differ from the resolved mapping.");
            }
        }
        var expectedHashes = new List<string>();
        foreach (var clip in plan.Clips)
        {
            var timelineClip = project.Timeline.Single(item => item.Id == clip.ClipId);
            var asset = assets[clip.AssetId];
            if (asset.Kind == "video")
                expectedHashes.AddRange(await FrameHashesAsync(source.Path, source.Video.Info.StreamIndex,
                    VideoFilter(clip, timelineClip, asset, plan.Width, plan.Height), cancellationToken));
            else
            {
                var hash = await ImageFrameHashAsync(ProjectFiles.Resolve(projectPath, asset.Path), timelineClip, asset, plan, source, cancellationToken);
                expectedHashes.AddRange(Enumerable.Repeat(hash, clip.EndFrame - clip.FirstFrame));
            }
        }
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
        var fittedPcm = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var voice in plan.Clips.SelectMany(clip => clip.VoiceReplacements).DistinctBy(item => item.ReplacementId))
        {
            var path = Path.Combine(staging, $"validation-voice-{fittedPcm.Count}.pcm");
            var fitted = Path.Combine(staging, $"voice-{fittedPcm.Count}.mka");
            await DecodePcmAsync(fitted, 0, path, cancellationToken);
            if (new FileInfo(path).Length != (voice.EndSample - voice.FirstSample) * sampleBytes)
                throw new InvalidDataException("Fitted voice sample count differs from its speech interval.");
            fittedPcm.Add(voice.ReplacementId, path);
        }
        async Task CompareAsync(Stream expected, long samples, string message)
        {
            long remaining = samples * sampleBytes;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, expectedBuffer.Length);
                await expected.ReadExactlyAsync(expectedBuffer.AsMemory(0, count), cancellationToken);
                await outputStream.ReadExactlyAsync(actualBuffer.AsMemory(0, count), cancellationToken);
                if (!expectedBuffer.AsSpan(0, count).SequenceEqual(actualBuffer.AsSpan(0, count)))
                    throw new InvalidDataException(message);
                remaining -= count;
            }
        }
        foreach (var clip in plan.Clips)
        {
            var image = assets[clip.AssetId].Kind == "image";
            if (image)
            {
                long remaining = (clip.EndSample - clip.FirstSample) * sampleBytes;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(remaining, actualBuffer.Length);
                    await outputStream.ReadExactlyAsync(actualBuffer.AsMemory(0, count), cancellationToken);
                    if (actualBuffer.AsSpan(0, count).ContainsAnyExcept((byte)0))
                        throw new InvalidDataException("Timed image audio is not silent.");
                    remaining -= count;
                }
                continue;
            }
            long cursor = clip.FirstSample;
            foreach (var voice in clip.VoiceReplacements)
            {
                sourceStream.Position = cursor * sampleBytes;
                await CompareAsync(sourceStream, voice.FirstSample - cursor,
                    "Export audio samples outside voice replacements differ from the requested source intervals.");
                await using (var replacement = File.OpenRead(fittedPcm[voice.ReplacementId]))
                {
                    await CompareAsync(replacement, voice.EndSample - voice.FirstSample,
                        "Export voice samples differ from the fitted generated preview.");
                }
                cursor = voice.EndSample;
            }
            sourceStream.Position = cursor * sampleBytes;
            await CompareAsync(sourceStream, clip.EndSample - cursor,
                "Export audio samples outside voice replacements differ from the requested source intervals.");
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

    private async Task FitVoiceAsync(EditProject project, string projectPath, ExportSource source,
        ResolvedVoiceReplacement voice, string destination, string staging, CancellationToken cancellationToken)
    {
        var asset = project.Assets.Single(item => item.Id == voice.AssetId);
        var synthesis = project.Synthesis.Single(item => item.ReplacementId == voice.ReplacementId);
        var targetSamples = voice.EndSample - voice.FirstSample;
        var layout = source.Channels == 1 ? "mono" : "stereo";
        var filter = FormattableString.Invariant($"aresample={source.SampleRate},aformat=sample_fmts=s16:sample_rates={source.SampleRate}:channel_layouts={layout}");
        if (voice.FitPolicy == "time-stretch")
        {
            var tempo = (decimal)synthesis.ActualDuration / synthesis.RequestedDuration;
            filter += ",atempo=" + tempo.ToString("0.################", CultureInfo.InvariantCulture);
        }
        filter += FormattableString.Invariant($",atrim=end_sample={targetSamples},apad=whole_len={targetSamples},atrim=end_sample={targetSamples},asetpts=N/SR/TB");
        var args = BaseArguments();
        AddWaveInput(args, ProjectFiles.Resolve(projectPath, asset.Path));
        args.AddRange(["-map", "0:a:0", "-vn", "-af", filter, "-c:a", "pcm_s16le"]);
        AddOutput(args, destination, RemainingBudget(staging));
        await ToolProcess.RunAsync(ffmpeg, args, cancellationToken: cancellationToken);
        var check = Path.Combine(staging, $"fit-check-{Guid.NewGuid():N}.pcm");
        await DecodePcmAsync(destination, 0, check, cancellationToken);
        try
        {
            if (new FileInfo(check).Length != targetSamples * source.Channels * 2)
                throw new InvalidDataException("Fitted voice does not contain the exact requested sample count.");
        }
        finally { File.Delete(check); }
    }

    private static string VoiceClipFilter(ResolvedClip clip, ExportSource source, long samplesPerFrame)
    {
        var filters = new List<string>();
        var inputs = new StringBuilder();
        long cursor = clip.FirstSample;
        int part = 0;
        for (var index = 0; index < clip.VoiceReplacements.Length; index++)
        {
            var voice = clip.VoiceReplacements[index];
            if (voice.FirstSample > cursor)
            {
                filters.Add(FormattableString.Invariant($"[0:{source.AudioIndex}]atrim=start_sample={cursor}:end_sample={voice.FirstSample},asetpts=N/SR/TB[p{part}]"));
                inputs.Append("[p").Append(part++).Append(']');
            }
            filters.Add(FormattableString.Invariant($"[{index + 1}:a:0]atrim=end_sample={voice.EndSample - voice.FirstSample},asetpts=N/SR/TB[p{part}]"));
            inputs.Append("[p").Append(part++).Append(']');
            cursor = voice.EndSample;
        }
        if (cursor < clip.EndSample)
        {
            filters.Add(FormattableString.Invariant($"[0:{source.AudioIndex}]atrim=start_sample={cursor}:end_sample={clip.EndSample},asetpts=N/SR/TB[p{part}]"));
            inputs.Append("[p").Append(part++).Append(']');
        }
        filters.Add(FormattableString.Invariant($"{inputs}concat=n={part}:v=0:a=1,asetnsamples=n={samplesPerFrame}:p=0[voiceout]"));
        return string.Join(';', filters);
    }

    private async Task<string> ImageFrameHashAsync(string path, TimelineClip timelineClip, MediaAsset asset,
        ExportPlan plan, ExportSource source, CancellationToken cancellationToken)
    {
        var args = BaseArguments();
        AddImageInput(args, path, source);
        args.AddRange(["-map", "0:v:0", "-an", "-vf", VisualFilters.ForClip(timelineClip, asset, plan.Width, plan.Height),
            "-frames:v", "1", "-c:v", "rawvideo", "-pix_fmt", "rgb24", "-f", "framehash", "-hash", "sha256", "pipe:1"]);
        var result = await ToolProcess.RunAsync(ffmpeg, args, cancellationToken: cancellationToken);
        return Encoding.UTF8.GetString(result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => !line.StartsWith('#')).Split(',')[^1].Trim();
    }

    private static string VideoFilter(ResolvedClip clip, TimelineClip timelineClip, MediaAsset asset, int width, int height)
    {
        return FormattableString.Invariant($"trim=start_frame={clip.FirstFrame}:end_frame={clip.EndFrame},setpts=PTS-STARTPTS,") +
            VisualFilters.ForClip(timelineClip, asset, width, height);
    }

    private static List<string> BaseArguments() => ["-v", "error", "-nostdin", "-xerror", "-n"];
    private static void AddInput(List<string> args, string path) => args.AddRange(["-protocol_whitelist", "file", "-format_whitelist", "matroska,webm", "-noautorotate", "-i", path]);
    private static void AddWaveInput(List<string> args, string path) => args.AddRange(["-protocol_whitelist", "file", "-format_whitelist", "wav", "-i", path]);
    private static void AddImageInput(List<string> args, string path, ExportSource source)
    {
        var frameDuration = source.Video.Frames[0].Duration;
        var rate = FormattableString.Invariant($"{source.Video.Info.TimeBase.Denominator}/{checked(frameDuration * source.Video.Info.TimeBase.Numerator)}");
        args.AddRange(["-protocol_whitelist", "file", "-f", "image2", "-loop", "1", "-framerate", rate, "-i", path]);
    }
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
