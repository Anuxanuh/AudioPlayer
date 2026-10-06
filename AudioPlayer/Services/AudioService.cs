using System.Windows.Media;

namespace AudioPlayer.Services;

public sealed partial class AudioService : IDisposable
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
        set
        {
            double previous = _playbackRate;
            _playbackRate = double.IsFinite(value) ? Math.Clamp(value, 0.5, 3) : 1;
            if (_player is not null && IsReady) _player.SpeedRatio = _playbackRate;
            if (previous != _playbackRate && _player is not null) BeginObservation("rate-change");
        }
    }
    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;

    public void Open(string path)
    {
        Stop();
        MediaId = Interlocked.Increment(ref _nextMediaId);
        Log.Information("Opening audio {Audio}; media={MediaId}; rate={Rate}", path, MediaId, _playbackRate);
        var player = new MediaPlayer { Volume = _volume };
        _player = player;
        long mediaId = MediaId;
        StartDiagnostics(path);
        player.BufferingStarted += (_, _) => MediaDiagnosticEvent(player, mediaId, "buffering-started");
        player.BufferingEnded += (_, _) => MediaDiagnosticEvent(player, mediaId, "buffering-ended");
        player.MediaOpened += (_, _) =>
        {
            if (!IsCurrentEvent(player, mediaId, "opened")) return;
            Log.Information("Audio opened; media={MediaId}; duration={Duration}; rate={Rate}; openMs={OpenMs}", MediaId, Duration, _playbackRate, MediaElapsedMs);
            IsReady = true; IsPlaying = true; player.SpeedRatio = _playbackRate; player.Play();
            BeginObservation("open-play");
            Opened?.Invoke();
        };
        player.MediaEnded += (_, _) =>
        {
            if (!IsCurrentEvent(player, mediaId, "ended")) return;
            IsPlaying = false; _ended = true;
            WriteDiagnostic("ended");
            Ended?.Invoke();
        };
        player.MediaFailed += (_, args) =>
        {
            if (!IsCurrentEvent(player, mediaId, "failed")) return;
            WriteDiagnostic("failed");
            Log.Error(args.ErrorException, "Audio decoder failed for {Audio}; media={MediaId}; hresult={HResult}", path, MediaId, args.ErrorException.HResult);
            Stop(); Failed?.Invoke(args.ErrorException.Message);
        };
        player.Open(new Uri(Path.GetFullPath(path), UriKind.Absolute));
    }

    public void Toggle(string source = "play-button")
    {
        if (_player is null || !IsReady)
        {
            Log.Information("Playback toggle ignored; media={MediaId}; source={Source}; ready={Ready}; hasPlayer={HasPlayer}", MediaId, source, IsReady, _player is not null);
            return;
        }
        WriteDiagnostic(IsPlaying ? "pause-requested" : "play-requested");
        if (IsPlaying) _player.Pause();
        else { if (_ended || (Duration > TimeSpan.Zero && Position >= Duration)) _player.Position = TimeSpan.Zero; _ended = false; _player.Play(); }
        IsPlaying = !IsPlaying;
        BeginObservation(IsPlaying ? "resume" : "pause");
        Log.Information("Playback command issued; media={MediaId}; command={CommandId}; source={Source}; requestedPlaying={RequestedPlaying}; position={Position}", MediaId, _commandId, source, IsPlaying, Position);
    }

    public void Seek(double seconds, string source = "seek")
    {
        if (_player is not null && IsReady)
        {
            double target = Math.Clamp(seconds, 0, Duration.TotalSeconds);
            Log.Information("Audio seek requested; media={MediaId}; nextCommand={CommandId}; source={Source}; fromSeconds={FromSeconds}; requestedSeconds={RequestedSeconds}; targetSeconds={TargetSeconds}; durationSeconds={DurationSeconds}",
                MediaId, _commandId + 1, source, Position.TotalSeconds, seconds, target, Duration.TotalSeconds);
            _player.Position = TimeSpan.FromSeconds(target); _ended = false;
            BeginObservation("seek");
        }
        else Log.Information("Audio seek ignored; media={MediaId}; source={Source}; requestedSeconds={RequestedSeconds}; ready={Ready}", MediaId, source, seconds, IsReady);
    }

    public void Stop()
    {
        WriteDiagnostic("close-requested");
        _diagnosticTimer.Stop();
        var previous = _player; _player = null;
        IsPlaying = false; IsReady = false; _ended = false;
        previous?.Close();
        if (previous is not null) Log.Information("Audio close command returned; media={MediaId}", MediaId);
    }
    public void Dispose() => Stop();
}
