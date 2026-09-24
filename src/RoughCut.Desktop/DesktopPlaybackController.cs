using CupriFace.Media;
using CupriFace.Media.Decoding;

namespace RoughCut.Desktop;

public sealed class DesktopPlaybackController : IVideoBackend
{
    private IVideoPlayer? _player;

    public static bool Available => NativeDecoders.Available;
    public bool IsOpen => _player is not null;
    public bool Playing => _player?.Playing == true;
    public double PositionSeconds => _player?.Position ?? 0;
    public double DurationSeconds => _player?.Duration ?? 0;
    public string Diagnostics => _player?.DiagnosticsSummary ?? "Playback is not open";

    public IVideoPlayer Open(VideoSource source)
    {
        _player = new WebmVideoBackend(new NativeDecoders(), SdlAudioSink.TryCreate()).Open(source);
        return _player;
    }

    public bool Muted
    {
        get => _player?.Muted == true;
        set { if (_player is not null) _player.Muted = value; }
    }

    public void Play() => _player?.Play();

    public void Seek(double seconds)
    {
        if (_player is not null) _player.Position = Math.Clamp(seconds, 0, _player.Duration);
    }

    public void Pause() => _player?.Pause();
}
