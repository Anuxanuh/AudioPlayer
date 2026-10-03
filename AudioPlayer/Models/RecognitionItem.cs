namespace AudioPlayer.Models;

public enum RecognitionState { Pending, Running, Paused, Completed, Skipped, Failed, Cancelled }

public sealed class RecognitionItem(string path) : ObservableObject
{
    public string AudioPath { get; } = Path.GetFullPath(path);
    public string OutputPath => Path.ChangeExtension(AudioPath, ".lrc");
    public string Name => Path.GetFileName(AudioPath);
    public string Folder => Path.GetDirectoryName(AudioPath) ?? "";
    private string _status = "等待识别";
    public string Status { get => _status; set => Set(ref _status, value); }
    private double _percent;
    public double Percent { get => _percent; set => Set(ref _percent, value); }
    private RecognitionState _state;
    public RecognitionState State { get => _state; set => Set(ref _state, value); }
}
