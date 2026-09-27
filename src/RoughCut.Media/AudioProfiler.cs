using System.Globalization;
using RoughCut.Core;

namespace RoughCut.Media;

/// What one window of sound measures. Levels are dBFS; `LowBandDb` is the level below the band split and
/// `LowBandShare` its fraction of the whole, which separates material carrying bass and drums from speech
/// and room tone far better than level alone; `SilentFraction` is how much of the window sits below the
/// silence floor. The share means little where a window is near silence — the filter carries energy across
/// a transition, so a window with almost no input of its own can read high — so read it beside `LevelDb`.
public sealed record AudioWindow(long Ticks, double Seconds, double LevelDb, double PeakDb,
    double LowBandDb, double LowBandShare, double SilentFraction);

/// Measurements, not verdicts. RoughCut says what the sound does; the caller decides what that means and
/// where the sections are, exactly as it decides which rendition to download and where to cut.
public sealed record AudioProfile(int SchemaVersion, string AssetId, long FromTicks, long ToTicks,
    long WindowTicks, int BandSplitHz, double SilenceFloorDb, AudioWindow[] Windows);

/// Reads the shape of a source's sound so a caller can find its sections. One decode pass at a low sample
/// rate, bounded in time and size, with the band split applied here rather than in a second pass.
public sealed class AudioProfiler(string ffmpeg = "ffmpeg")
{
    public const int SampleRate = 8000;
    public const int DefaultBandSplitHz = 200;
    public const double SilenceFloorDb = -60;
    public const int MaxWindows = 4000;
    private const long MaxDecodedBytes = 256L * 1024 * 1024;
    private const string Containers = "mov,matroska,webm,avi,mpegts,ogg,flv,mp4";

    public async Task<AudioProfile> ProfileAsync(EditProject project, string projectPath, string assetId,
        long fromTicks, long toTicks, long windowTicks, int bandSplitHz = DefaultBandSplitHz,
        CancellationToken cancellationToken = default)
    {
        var asset = project.Assets.SingleOrDefault(item => item.Id == assetId && item.Kind is "video" or "audio")
            ?? throw new KeyNotFoundException("Audio-bearing asset was not found in the project.");
        if (fromTicks < 0 || toTicks <= fromTicks || fromTicks > asset.Duration)
            throw new ArgumentOutOfRangeException(nameof(fromTicks), "The range must be a nonempty interval inside the source.");
        toTicks = Math.Min(toTicks, asset.Duration);
        if (windowTicks <= 0) throw new ArgumentOutOfRangeException(nameof(windowTicks), "The window must be positive.");
        if (bandSplitHz is < 20 or > 2000) throw new ArgumentOutOfRangeException(nameof(bandSplitHz), "The band split must be between 20 Hz and 2 kHz.");
        var windows = (toTicks - fromTicks + windowTicks - 1) / windowTicks;
        if (windows > MaxWindows)
            throw new ArgumentException($"That range and window ask for {windows} measurements; the limit is {MaxWindows}. Widen the window or shorten the range.");

        var path = ProjectFiles.Resolve(projectPath, asset.Path);
        var staging = Path.Combine(Path.GetTempPath(), "roughcut-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var decoded = Path.Combine(staging, "audio.pcm");
            await ToolProcess.RunAsync(ffmpeg,
                ["-v", "error", "-nostdin", "-xerror", "-n", "-protocol_whitelist", "file",
                 "-format_whitelist", Containers, "-i", path,
                 "-ss", Text(fromTicks, project.TimeBase), "-to", Text(toTicks, project.TimeBase),
                 "-map", "0:a:0", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
                 "-f", "s16le", "-fs", MaxDecodedBytes.ToString(CultureInfo.InvariantCulture), decoded],
                timeout: TimeSpan.FromMinutes(30), cancellationToken: cancellationToken);
            return Measure(assetId, fromTicks, toTicks, windowTicks, bandSplitHz, project.TimeBase, decoded);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                foreach (var file in Directory.EnumerateFiles(staging)) File.Delete(file);
                Directory.Delete(staging);
            }
        }
    }

    /// Streams the decoded samples so a long range costs no more memory than one window.
    internal static AudioProfile Measure(string assetId, long fromTicks, long toTicks, long windowTicks,
        int bandSplitHz, TimeBase timeBase, string decodedPath)
    {
        var perWindow = (int)Math.Max(1, Math.Round(Seconds(windowTicks, timeBase) * SampleRate));
        var lowPass = new LowPass(bandSplitHz, SampleRate);
        var floor = Math.Pow(10, SilenceFloorDb / 20) * short.MaxValue;
        var results = new List<AudioWindow>();
        using var stream = File.OpenRead(decodedPath);
        var buffer = new byte[perWindow * 2];
        var index = 0;
        while (true)
        {
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var samples = read / 2;
            if (samples == 0) break;
            double total = 0, low = 0, peak = 0;
            var quiet = 0;
            var subWindow = Math.Max(1, SampleRate / 50);        // 20 ms
            double subTotal = 0;
            var subCount = 0;
            var subWindows = 0;
            for (var position = 0; position < samples; position++)
            {
                double sample = BitConverter.ToInt16(buffer, position * 2);
                total += sample * sample;
                peak = Math.Max(peak, Math.Abs(sample));
                var filtered = lowPass.Next(sample);
                low += filtered * filtered;
                subTotal += sample * sample;
                if (++subCount == subWindow)
                {
                    if (Math.Sqrt(subTotal / subCount) < floor) quiet++;
                    subWindows++;
                    subTotal = 0;
                    subCount = 0;
                }
            }
            if (subCount > 0)
            {
                if (Math.Sqrt(subTotal / subCount) < floor) quiet++;
                subWindows++;
            }
            var ticks = fromTicks + index * windowTicks;
            // A share above one is the filter's own ringing decaying into a window whose input is almost
            // nothing; it is clamped rather than reported, and the low-band level is given as well.
            results.Add(new(ticks, Seconds(ticks, timeBase), Decibels(Math.Sqrt(total / samples)),
                Decibels(peak), Decibels(Math.Sqrt(low / samples)),
                total > 0 ? Math.Clamp(low / total, 0, 1) : 0,
                subWindows > 0 ? (double)quiet / subWindows : 0));
            index++;
            if (read < buffer.Length) break;
        }
        return new(1, assetId, fromTicks, toTicks, windowTicks, bandSplitHz, SilenceFloorDb, results.ToArray());
    }

    private static double Decibels(double amplitude) =>
        amplitude > 0 ? 20 * Math.Log10(amplitude / short.MaxValue) : -120;

    private static double Seconds(long ticks, TimeBase timeBase) =>
        (double)ticks * timeBase.Numerator / timeBase.Denominator;

    private static string Text(long ticks, TimeBase timeBase) =>
        Seconds(ticks, timeBase).ToString("0.######", CultureInfo.InvariantCulture);

    /// A second-order Butterworth low-pass, which is enough to split a band without pulling in a filter
    /// library or paying for a second decode.
    private sealed class LowPass
    {
        private readonly double _a0, _a1, _a2, _b1, _b2;
        private double _x1, _x2, _y1, _y2;

        public LowPass(double cutoffHz, int sampleRate)
        {
            var c = 1 / Math.Tan(Math.PI * cutoffHz / sampleRate);
            var denominator = 1 + Math.Sqrt(2) * c + c * c;
            _a0 = 1 / denominator;
            _a1 = 2 * _a0;
            _a2 = _a0;
            _b1 = 2 * (1 - c * c) / denominator;
            _b2 = (1 - Math.Sqrt(2) * c + c * c) / denominator;
        }

        public double Next(double sample)
        {
            var output = _a0 * sample + _a1 * _x1 + _a2 * _x2 - _b1 * _y1 - _b2 * _y2;
            _x2 = _x1;
            _x1 = sample;
            _y2 = _y1;
            _y1 = output;
            return output;
        }
    }
}
