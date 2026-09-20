using CupriFace.Shell;
using CupriFace.Media;
using CupriFace.Media.Decoding;
using RoughCut.Desktop;
using RoughCut.Media;
using SkiaSharp;

try
{
    switch (args)
    {
        case [] or ["help"] or ["--help"]:
            Console.WriteLine("""
                RoughCut desktop review
                  review <project.json> [pre-rendered-review.webm]
                  snapshot <project.json> <new-output.png>
                  probe-playback <project.json> <seconds>
                """);
            return 0;
        case ["review", var projectPath, .. var reviewOptions] when reviewOptions.Length <= 1:
            {
                var session = await DesktopReviewSession.LoadAsync(projectPath);
                DesktopPlaybackController? playback = null;
                if (!DesktopPlaybackController.Available)
                    session.PlaybackUnavailable("CupriFace native playback decoders are unavailable");
                else
                {
                    playback = new();
                    if (reviewOptions.FirstOrDefault() is { } reviewProxy)
                        session.UsePlaybackPreview(reviewProxy);
                }
                DesktopHost.Run(new RoughCutReviewApp(session, playback), document =>
                {
                    if (playback is null) return;
                    document.UseVideo(playback);
                });
                return 0;
            }
        case ["snapshot", var projectPath, var outputPath]:
            {
                outputPath = Path.GetFullPath(outputPath);
                if (File.Exists(outputPath)) throw new IOException("Snapshot output already exists.");
                var session = await DesktopReviewSession.LoadAsync(projectPath);
                await session.InitializePreviewAsync();
                using var document = new RoughCutReviewApp(session).CreateDocument();
                using var image = document.RenderToImage(1280, 800, new SKColor(0x0b, 0x0f, 0x17));
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                data.SaveTo(stream);
                Console.WriteLine(outputPath);
                return 0;
            }
        case ["probe-playback", var projectPath, var secondsText]:
            return await ProbePlaybackAsync(projectPath, secondsText);
        default:
            Console.Error.WriteLine("Unknown command or arguments. Run roughcut-desktop help.");
            return 2;
    }
}
catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or
    InvalidOperationException or KeyNotFoundException or RoughCut.Core.ProjectValidationException or
    RoughCut.Core.RevisionConflictException or MediaToolException or DesktopPlaybackUnavailableException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

static async Task<int> ProbePlaybackAsync(string projectPath, string secondsText)
{
    if (!double.TryParse(secondsText, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds is < 0.5 or > 10)
        throw new ArgumentException("Probe seconds must be from 0.5 through 10.");
    if (!NativeDecoders.Available)
        throw new InvalidOperationException("CupriFace native playback decoders are unavailable.");

    var proxy = await new DesktopPlaybackProxyBuilder().PrepareAsync(projectPath);
    Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
    var sink = SdlAudioSink.TryCreate() ?? throw new InvalidOperationException("SDL audio sink is unavailable.");
    using var player = (WebmPlayer)new WebmVideoBackend(new NativeDecoders(), sink).Open(new VideoSource(proxy.Path));
    if (!sink.DeviceOpen) throw new InvalidOperationException("SDL dummy audio device did not open.");
    var target = Math.Min(seconds, Math.Max(0.5, player.Duration - 0.1));
    player.Play();
    var started = System.Diagnostics.Stopwatch.StartNew();
    double? initialLag = null;
    var worstGrowth = 0d;
    while (player.Position < target && player.Playing && started.Elapsed < TimeSpan.FromSeconds(target + 5))
    {
        await Task.Delay(25);
        var lag = player.AudioLagSeconds;
        if (double.IsNaN(lag)) continue;
        if (player.Position >= 0.25 && initialLag is null) initialLag = lag;
        if (initialLag is not null) worstGrowth = Math.Max(worstGrowth, Math.Abs(lag - initialLag.Value));
    }
    player.Pause();
    if (player.Position < target || player.FramesDecoded < 2 || initialLag is null ||
        worstGrowth > 0.040 || player.AudioUnderruns > 2)
        throw new InvalidDataException("Playback did not advance with bounded A/V drift and underruns.");
    Console.WriteLine(FormattableString.Invariant(
        $"playback ok: {player.Position:0.000}s, {player.FramesDecoded} frames, drift {worstGrowth * 1000:0.0} ms, {player.AudioUnderruns} underruns"));
    return 0;
}
