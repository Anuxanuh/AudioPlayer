using Serilog.Events;

namespace AudioPlayer.Services;

public sealed partial class AudioService
{
    private const int MaximumRecoveryAttempts = 2;
    private readonly bool _automaticRecovery;
    private int _recoveryAttempts;
    private bool _recovering, _restartOnResume;
    private long? _healthySince;

    private void PlaybackAdvanced(long now)
    {
        _restartOnResume = false;
        if (_recovering)
        {
            Log.Information("Playback recovery confirmed; media={MediaId}; attempt={RecoveryAttempt}; positionSeconds={PositionSeconds}", MediaId, _recoveryAttempts, Position.TotalSeconds);
            _recovering = false;
        }
        _healthySince ??= now;
        // A brief tick after reopening must not reset the retry budget and cause an endless loop.
        if (_diagnosticTime.GetElapsedTime(_healthySince.Value, now).TotalSeconds >= 30) _recoveryAttempts = 0;
    }

    private bool RecoverIfStalled(long now, double position, double sampleGapMs)
    {
        _healthySince = null;
        double idleSeconds = _diagnosticTime.GetElapsedTime(_lastProgress, now).TotalSeconds;
        if (idleSeconds >= 5) _restartOnResume = true;
        if (!_automaticRecovery || _player is null || !IsReady || !IsPlaying || idleSeconds < 8) return false;
        // Allow slow buffering and a delayed UI dispatcher to catch up before replacing the decoder.
        if (sampleGapMs >= 5000 || (_player.IsBuffering && idleSeconds < 60)) return false;
        RecoverPlayback(position, "no-progress");
        return true;
    }

    private void RecoverPlayback(double position, string reason)
    {
        if (_sourcePath is not { } path || _player is null) return;
        if (_recoveryAttempts >= MaximumRecoveryAttempts)
        {
            RecoveryFailed("音频跳转后仍无法继续播放，已停止自动重试。请重新打开音频或检查文件与 Windows 解码器。");
            return;
        }
        long previousMedia = MediaId;
        _recoveryAttempts++;
        WriteDiagnostic("recovery-started", LogEventLevel.Warning);
        Log.Warning("Reopening stalled audio; previousMedia={PreviousMediaId}; attempt={RecoveryAttempt}; reason={Reason}; targetSeconds={TargetSeconds}; rate={Rate}",
            previousMedia, _recoveryAttempts, reason, position, _playbackRate);
        try
        {
            OpenCore(path, position, recovering: true);
            Log.Information("Playback recovery opening; previousMedia={PreviousMediaId}; media={MediaId}; attempt={RecoveryAttempt}", previousMedia, MediaId, _recoveryAttempts);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Playback recovery could not reopen audio; previousMedia={PreviousMediaId}", previousMedia);
            RecoveryFailed("重新打开音频失败：" + ex.Message);
        }
    }

    private void RecoveryFailed(string message)
    {
        WriteDiagnostic("recovery-failed", LogEventLevel.Error);
        Log.Error("Playback recovery stopped; media={MediaId}; attempt={RecoveryAttempt}; reason={Reason}", MediaId, _recoveryAttempts, message);
        Stop();
        Failed?.Invoke(message);
    }
}
