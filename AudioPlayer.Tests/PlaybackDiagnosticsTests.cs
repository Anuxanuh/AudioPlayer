using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPlayer.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

internal static partial class Program
{
    private sealed class DiagnosticClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public void Advance(double seconds) => _milliseconds += (long)(seconds * 1000);
    }

    private sealed class PlaybackLogCapture : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
        public LogEvent[] Named(string name) => Events.Where(e => e.Properties.TryGetValue("PlaybackEvent", out var value) && value is ScalarValue { Value: string s } && s == name).ToArray();
    }

    private static void PlaybackDiagnostics()
    {
        string path = Path.Combine(_root, "播放诊断.wav"); WriteWave(path, 30);
        var capture = new PlaybackLogCapture();
        var previousLog = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        Log.Logger = logger;
        try
        {
            var clock = new DiagnosticClock();
            using var audio = new AudioService(clock, automaticRecovery: false) { Volume = 0 };
            string? error = null;
            audio.Failed += message => error = message;
            audio.Open(path); Pump(() => audio.IsReady || error is not null);
            Assert(error is null, "Diagnostic fixture must open: " + error);
            long firstMedia = audio.MediaId;
            Delay(250); clock.Advance(1); audio.ObservePlayback();
            Assert(capture.Named("progress-observed").Length == 1, "Actual advancing playback must be confirmed once");

            // Simulate the reported mismatch: stop the native player while application intent remains playing.
            var native = Private<MediaPlayer>(audio, "_player");
            InvokeResult(audio, "MediaDiagnosticEvent", native, firstMedia, "buffering-started");
            InvokeResult(audio, "MediaDiagnosticEvent", native, firstMedia, "buffering-ended");
            Assert(capture.Named("buffering-started").Length == 1 && capture.Named("buffering-ended").Length == 1, "Buffering callbacks must write correlated snapshots");
            native.Pause(); Delay(150); clock.Advance(1); audio.ObservePlayback();
            int baseline = capture.Named("progress-stalled").Length;
            clock.Advance(4); audio.ObservePlayback();
            Assert(capture.Named("progress-stalled").Length == baseline, "Short waits must not be reported as stalls");
            clock.Advance(1); audio.ObservePlayback();
            var stalled = capture.Named("progress-stalled");
            Assert(stalled.Length == baseline + 1 && stalled[^1].Level == LogEventLevel.Warning, "Five seconds without progress must warn");
            Assert(audio.IsPlaying, "Observations must not change playback intent");
            var state = (StructureValue)stalled[^1].Properties["PlaybackState"];
            Assert(state.Properties.Any(p => p.Name == "RequestedPlaying" && p.Value is ScalarValue { Value: true }) &&
                state.Properties.Any(p => p.Name == "Buffering") && state.Properties.Any(p => p.Name == "PlayerRate") &&
                state.Properties.Any(p => p.Name == "PositionSeconds") && state.Properties.Any(p => p.Name == "PrivateMemoryMB"), "Stall snapshots must contain actionable state");
            for (int i = 0; i < 29; i++) { clock.Advance(1); audio.ObservePlayback(); }
            Assert(capture.Named("progress-stalled").Length == baseline + 1, "Persistent stalls must not spam the log");
            clock.Advance(1); audio.ObservePlayback();
            Assert(capture.Named("progress-stalled").Length == baseline + 2, "Persistent stalls must include a 30-second follow-up");

            native.Play(); Delay(250); clock.Advance(1); audio.ObservePlayback();
            Assert(capture.Named("progress-resumed").Length == 1, "Recovery must be recorded with the same media ID");
            Assert((long)((ScalarValue)capture.Named("progress-resumed")[0].Properties["MediaId"]).Value! == firstMedia, "Recovery must correlate to the stalled media");
            int stableCount = capture.Events.Count;
            Delay(100); clock.Advance(1); audio.ObservePlayback();
            Assert(capture.Events.Count == stableCount, "Healthy playback must not write periodic snapshots");

            audio.Toggle("diagnostic-test");
            int warnings = capture.Named("progress-stalled").Length;
            clock.Advance(60); audio.ObservePlayback();
            audio.Seek(15, "diagnostic-test"); clock.Advance(60); audio.ObservePlayback();
            Assert(capture.Named("progress-stalled").Length == warnings && !audio.IsPlaying, "Pause and seek while paused must not create false stall reports or resume playback");
            audio.Toggle("diagnostic-test"); Delay(200); clock.Advance(1); audio.ObservePlayback();
            Assert(capture.Named("progress-observed").Length == 2, "Resume begins a fresh progress observation");

            audio.Open(path); Pump(() => audio.IsReady);
            Assert(audio.MediaId != firstMedia, "Every native player instance needs a new media ID");
            InvokeResult(audio, "MediaDiagnosticEvent", native, firstMedia, "buffering-started");
            Assert(capture.Named("buffering-started").Length == 2 && capture.Named("buffering-started")[^1].MessageTemplate.Text.StartsWith("Stale audio event ignored"), "Old media callbacks must be identifiable and must not snapshot the new media");
            Delay(200); clock.Advance(1); audio.ObservePlayback();
            audio.Stop();
            stableCount = capture.Events.Count;
            clock.Advance(120); audio.ObservePlayback(); Delay(1100);
            Assert(capture.Events.Count == stableCount && !Private<DispatcherTimer>(audio, "_diagnosticTimer").IsEnabled, "Stop must stop observation and prevent stale diagnostics");

            // A delayed MediaOpened must still be diagnosable, even with no plugin request timeout.
            audio.Open(path);
            clock.Advance(5); audio.ObservePlayback();
            Assert(capture.Named("opening-pending").Length == 1, "Opening without a ready callback must produce a pending warning");
            audio.Stop();
            Assert(capture.Named("observation-delayed").Length > 0, "Delayed UI sampling must be distinguishable from decoder stalls");
            File.WriteAllLines(Path.Combine(_root, "playback-diagnostics.log"), capture.Events.Select(e => e.RenderMessage()));
        }
        finally { Log.Logger = previousLog; }
    }
}
