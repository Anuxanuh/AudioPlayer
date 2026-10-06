using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPlayer.Services;
using Serilog;

internal static partial class Program
{
    private static void FreezeNativePlayback(AudioService audio, DiagnosticClock clock)
    {
        Private<MediaPlayer>(audio, "_player").Pause();
        Delay(150);
        clock.Advance(1); audio.ObservePlayback();
    }

    private static void TriggerPlaybackRecovery(AudioService audio, DiagnosticClock clock)
    {
        FreezeNativePlayback(audio, clock);
        long media = audio.MediaId;
        for (int i = 0; i < 10 && audio.HasSource && audio.MediaId == media; i++)
        { clock.Advance(1); audio.ObservePlayback(); }
        Assert(!audio.HasSource || audio.MediaId != media, "A non-buffering decoder with no progress must be replaced or report exhausted recovery");
    }

    private static void PlaybackRecovery()
    {
        string path = Path.Combine(_root, "恢复测试.wav"), other = Path.Combine(_root, "切换目标.wav");
        WriteWave(path, 60); WriteWave(other, 60);
        var capture = new PlaybackLogCapture(); var previousLog = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        Log.Logger = logger;
        try
        {
            var clock = new DiagnosticClock();
            using var audio = new AudioService(clock) { Volume = 0, PlaybackRate = 2 };
            string? error = null; audio.Failed += message => error = message;
            void Ready() { Pump(() => audio.IsReady || error is not null); Assert(error is null, "Recovery fixture failed: " + error); }
            void Advanced(double target)
            {
                Delay(250); clock.Advance(1); audio.ObservePlayback();
                Assert(audio.IsPlaying && audio.Position.TotalSeconds > target + 0.1, "Recovered playback must actually advance from the requested position");
            }

            audio.Open(path, 10); Ready();
            Assert(audio.Position.TotalSeconds >= 10 && audio.Position.TotalSeconds < 10.5, "Initial chapter position must be applied before playback starts");
            Advanced(10);
            FreezeNativePlayback(audio, clock);
            double frozen = audio.Position.TotalSeconds;
            long originalMedia = audio.MediaId;
            for (int i = 0; i < 9 && audio.MediaId == originalMedia; i++) { clock.Advance(1); audio.ObservePlayback(); }
            Assert(audio.MediaId != originalMedia, "Stalled native playback must not keep receiving Play on the broken instance");
            Ready();
            Assert(Math.Abs(audio.Position.TotalSeconds - frozen) < 0.5 && audio.PlaybackRate == 2 && audio.Volume == 0, "Recovery must preserve exact seek target, speed and volume");
            Advanced(frozen);
            Assert(capture.Events.Any(e => e.MessageTemplate.Text.StartsWith("Playback recovery confirmed")), "Recovery must be confirmed by measured progress");

            TriggerPlaybackRecovery(audio, clock);
            audio.Toggle("pause-during-recovery"); Ready();
            double paused = audio.Position.TotalSeconds; long pausedMedia = audio.MediaId;
            Delay(200); clock.Advance(60); audio.ObservePlayback();
            Assert(!audio.IsPlaying && audio.MediaId == pausedMedia && Math.Abs(audio.Position.TotalSeconds - paused) < 0.05, "Pause during reopening must survive MediaOpened and suppress automatic recovery");

            audio.Open(path, 10); Ready(); Advanced(10);
            TriggerPlaybackRecovery(audio, clock);
            audio.Seek(25, "new-target-during-recovery"); Ready(); Advanced(25);

            TriggerPlaybackRecovery(audio, clock);
            audio.Open(other, 5); Ready(); Advanced(5);
            Assert(Private<MediaPlayer>(audio, "_player").Source.LocalPath == other, "An older recovery must never restore the previous song");

            TriggerPlaybackRecovery(audio, clock);
            audio.Stop(); Delay(350); clock.Advance(60); audio.ObservePlayback();
            Assert(!audio.HasSource && !audio.IsPlaying && !audio.IsReady && !Private<DispatcherTimer>(audio, "_diagnosticTimer").IsEnabled, "Stop/dispose must cancel a pending reopen");

            audio.Open(path, 12); Ready(); Advanced(12);
            FreezeNativePlayback(audio, clock);
            for (int i = 0; i < 5; i++) { clock.Advance(1); audio.ObservePlayback(); }
            long stalledMedia = audio.MediaId;
            audio.Toggle("pause-after-stall"); audio.Toggle("resume-after-stall");
            Assert(audio.MediaId != stalledMedia, "Manual resume after a known stall must reopen the decoder");
            Ready(); Advanced(12);

            audio.Open(path, 10); Ready();
            for (int i = 0; i < 2; i++) { TriggerPlaybackRecovery(audio, clock); Ready(); }
            TriggerPlaybackRecovery(audio, clock);
            Assert(error is not null && !audio.IsPlaying && !audio.HasSource && capture.Named("recovery-failed").Length == 1, "Persistent failure must stop after two retries and notify the UI");
            int events = capture.Events.Count;
            clock.Advance(120); audio.ObservePlayback();
            Assert(capture.Events.Count == events, "An exhausted recovery must not loop forever");
            File.WriteAllLines(Path.Combine(_root, "playback-recovery.log"), capture.Events.Select(e => e.RenderMessage()));
        }
        finally { Log.Logger = previousLog; }
    }
}
