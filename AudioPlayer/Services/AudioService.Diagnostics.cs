using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog.Events;

namespace AudioPlayer.Services;

public sealed partial class AudioService
{
    private static long _nextMediaId;
    private readonly TimeProvider _diagnosticTime;
    private readonly DispatcherTimer _diagnosticTimer;
    private long _mediaStarted, _commandStarted, _lastProgress, _lastSample, _lastWarning, _commandId;
    private double _lastPosition;
    private bool _progressObserved, _stalled, _diagnosticReadFailed;
    private string _operation = "opening";
    public long MediaId { get; private set; }
    private double MediaElapsedMs => _diagnosticTime.GetElapsedTime(_mediaStarted).TotalMilliseconds;

    public AudioService() : this(TimeProvider.System) { }
    internal AudioService(TimeProvider diagnosticTime)
    {
        _diagnosticTime = diagnosticTime;
        _diagnosticTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _diagnosticTimer.Tick += (_, _) => ObservePlayback();
    }

    private void StartDiagnostics(string path)
    {
        _mediaStarted = _diagnosticTime.GetTimestamp();
        _diagnosticReadFailed = false;
        _commandId = 0;
        BeginObservation("opening");
        // Metadata only: never scan the audio payload for diagnostics.
        try
        {
            var file = new FileInfo(path);
            Log.Information("Audio source metadata; media={MediaId}; bytes={FileBytes}; modifiedUtc={ModifiedUtc}; extension={Extension}", MediaId, file.Length, file.LastWriteTimeUtc, file.Extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Log.Warning(ex, "Audio source metadata unavailable; media={MediaId}", MediaId); }
        _diagnosticTimer.Start();
    }

    private void BeginObservation(string operation)
    {
        _operation = operation;
        _commandId++;
        _commandStarted = _lastProgress = _lastSample = _lastWarning = _diagnosticTime.GetTimestamp();
        _progressObserved = _stalled = false;
        try { _lastPosition = Position.TotalSeconds; }
        catch (Exception ex) { DiagnosticReadFailed(ex); }
        WriteDiagnostic("command-issued");
    }

    private bool IsCurrentEvent(MediaPlayer player, long mediaId, string name)
    {
        if (_player == player) return true;
        Log.Information("Stale audio event ignored; event={PlaybackEvent}; media={MediaId}; currentMedia={CurrentMediaId}", name, mediaId, MediaId);
        return false;
    }

    private void MediaDiagnosticEvent(MediaPlayer player, long mediaId, string name)
    {
        if (IsCurrentEvent(player, mediaId, name)) WriteDiagnostic(name);
    }

    // Observe only: a diagnostic must not seek, pause, reopen, or change the user's playback intent.
    internal void ObservePlayback()
    {
        if (_player is null) return;
        try
        {
            long now = _diagnosticTime.GetTimestamp();
            double sampleGapMs = _diagnosticTime.GetElapsedTime(_lastSample, now).TotalMilliseconds;
            _lastSample = now;
            if (sampleGapMs >= 5000) WriteDiagnostic("observation-delayed", LogEventLevel.Warning, sampleGapMs);
            if (!IsReady)
            {
                if (_diagnosticTime.GetElapsedTime(_lastWarning, now).TotalSeconds >= (_stalled ? 30 : 5))
                {
                    WriteDiagnostic("opening-pending", LogEventLevel.Warning, sampleGapMs);
                    _lastWarning = now; _stalled = true;
                }
                return;
            }
            if (!IsPlaying) return;
            double position = Position.TotalSeconds;
            if (position > _lastPosition + 0.02)
            {
                if (!_progressObserved || _stalled)
                    WriteDiagnostic(_stalled ? "progress-resumed" : "progress-observed", LogEventLevel.Information, sampleGapMs);
                _progressObserved = true; _stalled = false; _lastProgress = now;
            }
            else if (_diagnosticTime.GetElapsedTime(_lastProgress, now).TotalSeconds >= 5 &&
                (!_stalled || _diagnosticTime.GetElapsedTime(_lastWarning, now).TotalSeconds >= 30))
            {
                WriteDiagnostic("progress-stalled", LogEventLevel.Warning, sampleGapMs);
                _stalled = true; _lastWarning = now;
            }
            _lastPosition = position;
        }
        catch (Exception ex) { DiagnosticReadFailed(ex); }
    }

    private void WriteDiagnostic(string name, LogEventLevel level = LogEventLevel.Information, double sampleGapMs = 0)
    {
        var player = _player;
        if (player is null) return;
        try
        {
            long now = _diagnosticTime.GetTimestamp();
            using var process = Process.GetCurrentProcess();
            var state = new
            {
                Ready = IsReady, RequestedPlaying = IsPlaying, Ended = _ended,
                PositionSeconds = player.Position.TotalSeconds, DurationSeconds = Duration.TotalSeconds,
                RequestedRate = _playbackRate, NativeRate = player.SpeedRatio,
                Buffering = player.IsBuffering, BufferingProgress = player.BufferingProgress,
                DownloadProgress = player.DownloadProgress, player.HasAudio, player.HasVideo,
                player.Volume, player.IsMuted,
                PrivateMemoryMB = process.PrivateMemorySize64 / 1048576,
                WorkingSetMB = process.WorkingSet64 / 1048576
            };
            Log.Write(level, "Playback diagnostic; event={PlaybackEvent}; media={MediaId}; command={CommandId}; operation={Operation}; mediaAgeMs={MediaAgeMs}; commandAgeMs={CommandAgeMs}; noProgressMs={NoProgressMs}; sampleGapMs={SampleGapMs}; state={@PlaybackState}",
                name, MediaId, _commandId, _operation, _diagnosticTime.GetElapsedTime(_mediaStarted, now).TotalMilliseconds,
                _diagnosticTime.GetElapsedTime(_commandStarted, now).TotalMilliseconds,
                _diagnosticTime.GetElapsedTime(_lastProgress, now).TotalMilliseconds, sampleGapMs, state);
        }
        catch (Exception ex) { DiagnosticReadFailed(ex); }
    }

    private void DiagnosticReadFailed(Exception ex)
    {
        if (_diagnosticReadFailed) return;
        _diagnosticReadFailed = true;
        Log.Warning(ex, "Playback diagnostic snapshot unavailable; media={MediaId}; operation={Operation}", MediaId, _operation);
    }
}
