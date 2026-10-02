namespace AudioPlayer.Services;

/// <summary>Cooperative pause signal; the Python worker retains model and segment iterators.</summary>
public sealed class RecognitionPauseControl : IDisposable
{
    public string SignalPath { get; } = Path.Combine(Path.GetTempPath(), "shengyu-pause-" + Guid.NewGuid().ToString("N"));
    public bool IsPaused { get; private set; }
    public void Pause() { File.WriteAllText(SignalPath, "pause"); IsPaused = true; }
    public void Resume() { if (File.Exists(SignalPath)) File.Delete(SignalPath); IsPaused = false; }
    public void Dispose() => Resume();
}
