using System.Windows.Media;

namespace AudioPlayer.Services;

public sealed partial class AudioService : IDisposable
{
    private MediaPlayer? _player;
    private double _volume = 0.65;
    private double _playbackRate = 1;
    private bool _ended;
    private string? _sourcePath;
    private double _pendingPosition;
    private bool _playWhenOpened;
    public bool HasSource => _player is not null;
    public bool IsPlaying { get; private set; }
    public bool IsReady { get; private set; }
    public TimeSpan Duration => _player is { NaturalDuration.HasTimeSpan: true } ? _player.NaturalDuration.TimeSpan : TimeSpan.Zero;
    public TimeSpan Position => _player is null ? TimeSpan.Zero : !IsReady ? TimeSpan.FromSeconds(_pendingPosition) : _player.Position;
    public double Volume { get => _volume; set { _volume = Math.Clamp(value, 0, 1); if (_player is not null) _player.Volume = _volume; } }
    public double PlaybackRate
    {
        get => _playbackRate;
        set
        {
            double previous = _playbackRate;
            _playbackRate = double.IsFinite(value) ? Math.Clamp(value, 0.5, 3) : 1;
            if (previous != _playbackRate && _player is not null && IsReady) _player.SpeedRatio = _playbackRate;
            if (previous != _playbackRate && _player is not null) BeginObservation("rate-change");
        }
    }
    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;

    public void Open(string path) => Open(path, 0);

    public void Open(string path, double initialSeconds)
    {
        if (!double.IsFinite(initialSeconds) || initialSeconds < 0) throw new ArgumentOutOfRangeException(nameof(initialSeconds));
        _recoveryAttempts = 0;
        OpenCore(path, initialSeconds, recovering: false);
    }

    private void OpenCore(string path, double initialSeconds, bool recovering)
    {
        ClosePlayer();
        _sourcePath = path;
        _pendingPosition = initialSeconds;
        _playWhenOpened = true;
        IsPlaying = true;
        _recovering = recovering;
        _restartOnResume = false;
        _healthySince = null;
        MediaId = Interlocked.Increment(ref _nextMediaId);
        Log.Information("Opening audio {Audio}; media={MediaId}; rate={Rate}; initialSeconds={InitialSeconds}; recoveryAttempt={RecoveryAttempt}", path, MediaId, _playbackRate, initialSeconds, _recoveryAttempts);
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
            try
            {
                IsReady = true;
                // Apply the initial seek while paused, before issuing a non-zero playback rate.
                ApplyPosition(_pendingPosition, _playWhenOpened);
                IsPlaying = _playWhenOpened;
                BeginObservation(IsPlaying ? "open-position-play" : "open-position-paused");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Audio initial positioning failed; media={MediaId}; targetSeconds={TargetSeconds}", MediaId, _pendingPosition);
                Stop(); Failed?.Invoke(ex.Message); return;
            }
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
        if (_player is null)
        {
            Log.Information("Playback toggle ignored; media={MediaId}; source={Source}; ready={Ready}; hasPlayer={HasPlayer}", MediaId, source, IsReady, _player is not null);
            return;
        }
        if (!IsReady)
        {
            _playWhenOpened = !_playWhenOpened;
            IsPlaying = _playWhenOpened;
            WriteDiagnostic("opening-play-intent-changed");
            return;
        }
        WriteDiagnostic(IsPlaying ? "pause-requested" : "play-requested");
        if (IsPlaying) { _player.Pause(); _healthySince = null; }
        else
        {
            if (_automaticRecovery && _restartOnResume) { RecoverPlayback(Position.TotalSeconds, "resume-stalled"); return; }
            if (_ended || (Duration > TimeSpan.Zero && Position >= Duration)) ApplyPosition(0, play: true);
            else _player.Play();
            _ended = false;
        }
        IsPlaying = !IsPlaying;
        _playWhenOpened = IsPlaying;
        BeginObservation(IsPlaying ? "resume" : "pause");
        Log.Information("Playback command issued; media={MediaId}; command={CommandId}; source={Source}; requestedPlaying={RequestedPlaying}; position={Position}", MediaId, _commandId, source, IsPlaying, Position);
    }

    public void Seek(double seconds, string source = "seek")
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (_player is not null)
        {
            double target = IsReady ? Math.Clamp(seconds, 0, Duration.TotalSeconds) : Math.Max(0, seconds);
            Log.Information("Audio seek requested; media={MediaId}; nextCommand={CommandId}; source={Source}; fromSeconds={FromSeconds}; requestedSeconds={RequestedSeconds}; targetSeconds={TargetSeconds}; durationSeconds={DurationSeconds}",
                MediaId, _commandId + 1, source, Position.TotalSeconds, seconds, target, Duration.TotalSeconds);
            _pendingPosition = target; _ended = false; _recoveryAttempts = 0; _healthySince = null;
            if (!IsReady) { WriteDiagnostic("opening-seek-updated"); return; }
            if (_automaticRecovery && _restartOnResume && IsPlaying) { RecoverPlayback(target, "seek-stalled"); return; }
            ApplyPosition(target, IsPlaying);
            BeginObservation("seek");
        }
        else Log.Information("Audio seek ignored; media={MediaId}; source={Source}; requestedSeconds={RequestedSeconds}; ready={Ready}", MediaId, source, seconds, IsReady);
    }

    public void Stop()
    {
        ClosePlayer();
        _sourcePath = null; _pendingPosition = 0; _playWhenOpened = false;
        _recovering = _restartOnResume = false; _recoveryAttempts = 0; _healthySince = null;
    }

    private void ApplyPosition(double seconds, bool play)
    {
        if (_player is null) return;
        _pendingPosition = Math.Clamp(seconds, 0, Duration.TotalSeconds);
        _player.Pause();
        _player.Position = TimeSpan.FromSeconds(_pendingPosition);
        _player.SpeedRatio = _playbackRate;
        if (play) _player.Play();
    }

    private void ClosePlayer()
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
