using CupriFace.Shell;
using CupriFace.Media;
using CupriFace.Media.Decoding;
using RoughCut.Desktop;
using RoughCut.Media;
using SkiaSharp;

// A window that vanishes tells nobody anything. Record what actually happened, wherever it happened.
AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception, "unhandled");
TaskScheduler.UnobservedTaskException += (_, e) => { ReportCrash(e.Exception, "background"); e.SetObserved(); };
// Some failures are caught and turned into a bare message by the shell before they reach the handlers
// above. ROUGHCUT_TRACE=1 records every exception as it is thrown, which is what a stack needs.
if (Environment.GetEnvironmentVariable("ROUGHCUT_TRACE") == "1")
    AppDomain.CurrentDomain.FirstChanceException += (_, e) => ReportCrash(e.Exception, "first-chance");

try
{
    switch (args)
    {
        case ["help"] or ["--help"]:
            Console.WriteLine("""
                RoughCut desktop review
                  (no arguments)                                  open the window and pick a project
                  review [project.json] [pre-rendered.webm]       open a project directly
                  snapshot [project.json] <new-output.png>        render the window headlessly
                  snapshot --url <url> <new-output.png>           render the rendition picker for a real URL
                  probe-playback <project.json> <seconds>         measure decoded playback drift
                """);
            return 0;
        case [] or ["review"]:
            return RunWindow(null, null);
        case ["review", var projectPath, .. var reviewOptions] when reviewOptions.Length <= 1:
            return RunWindow(await DesktopReviewSession.LoadAsync(projectPath), reviewOptions.FirstOrDefault());
        case ["snapshot", var launcherOutput]:
            return Snapshot(launcherOutput, null);
        case ["snapshot", "--url", var sourceUrl, var pickerOutput]:
            {
                // Asks the real source what it offers, then renders the picker holding that answer.
                var offered = await DesktopReviewSession.ListFormatsAsync(sourceUrl);
                return Snapshot(pickerOutput, null, app => app.OfferFormats(sourceUrl, offered));
            }
        case ["snapshot", var projectPath, var outputPath]:
            {
                var session = await DesktopReviewSession.LoadAsync(projectPath);
                await session.InitializePreviewAsync();
                return Snapshot(outputPath, session);
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
    RoughCut.Core.RevisionConflictException or MediaToolException or DesktopPlaybackUnavailableException or
    System.Text.Json.JsonException or UnauthorizedAccessException)
{
    // Report what is wrong with the project, not just that something was.
    Console.Error.WriteLine(FailureText.Describe(exception, args.Length > 1 ? args[1] : null));
    return 2;
}

static void ReportCrash(Exception? exception, string origin)
{
    var detail = exception?.ToString() ?? "An unidentified failure ended the process.";
    Console.Error.WriteLine($"RoughCut hit an {origin} failure and is closing:");
    Console.Error.WriteLine(detail);
    try
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoughCut");
        Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, "crash.log");
        File.AppendAllText(log,
            $"{DateTimeOffset.Now:O} [{origin}] {detail}{Environment.NewLine}{Environment.NewLine}");
        Console.Error.WriteLine($"Recorded in {log}");
    }
    catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException)
    {
        // Reporting a crash must never cause one.
    }
}

static int Snapshot(string outputPath, DesktopReviewSession? session, Action<RoughCutReviewApp>? arrange = null)
{
    outputPath = Path.GetFullPath(outputPath);
    if (File.Exists(outputPath)) throw new IOException("Snapshot output already exists.");
    var app = new RoughCutReviewApp(session);
    using var document = app.CreateDocument();
    arrange?.Invoke(app);
    // The view measures itself from a laid-out document — the cropped playback box among other things — so
    // the snapshot lays out, presents once and only then renders what the window would show.
    document.BuildDisplayList(1280, 800);
    app.Present(1280, 800);
    using var image = document.RenderToImage(1280, 800, new SKColor(0x0b, 0x0f, 0x17));
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    data.SaveTo(stream);
    Console.WriteLine(outputPath);
    return 0;
}

static int RunWindow(DesktopReviewSession? session, string? reviewProxy)
{
    DesktopPlaybackController? playback = null;
    if (!DesktopPlaybackController.Available)
        session?.PlaybackUnavailable("CupriFace native playback decoders are unavailable");
    else
    {
        playback = new();
        if (reviewProxy is not null) session?.UsePlaybackPreview(reviewProxy);
    }
    var recent = new RecentProjects();
    if (session is not null) recent.Record(session.ProjectPath, session.Project.ProjectId);
    try
    {
        DesktopHost.Run(new RoughCutReviewApp(session, playback, recent), document =>
        {
            if (playback is null) return;
            document.UseVideo(playback);
        });
    }
    catch (Exception exception)
    {
        // The window loop is where a failure is least visible, so record it with its stack before leaving.
        ReportCrash(exception, "window");
        return 2;
    }
    return 0;
}

static async Task<int> ProbePlaybackAsync(string projectPath, string secondsText)
{
    if (!double.TryParse(secondsText, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds is < 0.5 or > 10)
        throw new ArgumentException("Probe seconds must be from 0.5 through 10.");
    if (!NativeDecoders.Available)
        throw new InvalidOperationException("CupriFace native playback decoders are unavailable.");

    var proxy = await new DesktopPlaybackProxyBuilder(DesktopReviewSession.Tools.Ffmpeg, DesktopReviewSession.Tools.Ffprobe).PrepareAsync(projectPath);
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
