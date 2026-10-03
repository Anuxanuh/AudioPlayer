using System.Windows.Media;

namespace AudioPlayer.Services;

public sealed class AudioService : IDisposable
{
    private MediaPlayer? _player;
    private double _volume = 0.65;
    private double _playbackRate = 1;
    private bool _ended;
    public bool IsPlaying { get; private set; }
    public bool IsReady { get; private set; }
    public TimeSpan Duration => _player is { NaturalDuration.HasTimeSpan: true } ? _player.NaturalDuration.TimeSpan : TimeSpan.Zero;
    public TimeSpan Position => _player?.Position ?? TimeSpan.Zero;
    public double Volume { get => _volume; set { _volume = Math.Clamp(value, 0, 1); if (_player is not null) _player.Volume = _volume; } }
    public double PlaybackRate
    {
        get => _playbackRate;
        set { _playbackRate = double.IsFinite(value) ? Math.Clamp(value, 0.5, 3) : 1; if (_player is not null && IsReady) _player.SpeedRatio = _playbackRate; }
    }
    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;

    public void Open(string path)
    {
        Stop();
        Log.Information("Opening audio {Audio}; rate={Rate}", path, _playbackRate);
        var player = new MediaPlayer { Volume = _volume };
        _player = player;
        player.MediaOpened += (_, _) =>
        {
            if (_player != player) return;
            Log.Information("Audio opened; duration={Duration}; rate={Rate}", Duration, _playbackRate);
            IsReady = true; IsPlaying = true; player.SpeedRatio = _playbackRate; player.Play(); Opened?.Invoke();
        };
        player.MediaEnded += (_, _) =>
        {
            if (_player != player) return;
            Log.Information("Audio ended");
            IsPlaying = false; _ended = true; Ended?.Invoke();
        };
        player.MediaFailed += (_, args) =>
        {
            if (_player != player) return;
            Log.Error(args.ErrorException, "Audio decoder failed for {Audio}", path);
            Stop(); Failed?.Invoke(args.ErrorException.Message);
        };
        player.Open(new Uri(Path.GetFullPath(path), UriKind.Absolute));
    }

    public void Toggle()
    {
        if (_player is null || !IsReady) return;
        if (IsPlaying) _player.Pause();
        else { if (_ended || (Duration > TimeSpan.Zero && Position >= Duration)) _player.Position = TimeSpan.Zero; _ended = false; _player.Play(); }
        IsPlaying = !IsPlaying;
        Log.Information("Playback state changed; playing={Playing}; position={Position}", IsPlaying, Position);
    }

    public void Seek(double seconds)
    {
        if (_player is not null && IsReady) { _player.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Duration.TotalSeconds)); _ended = false; }
    }

    public void Stop()
    {
        var previous = _player; _player = null;
        IsPlaying = false; IsReady = false; _ended = false;
        previous?.Close();
    }
    public void Dispose() => Stop();
}
