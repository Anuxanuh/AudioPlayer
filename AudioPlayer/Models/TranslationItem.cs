namespace AudioPlayer.Models;

public sealed class TranslationItem(Track track) : ObservableObject
{
    public Track Track { get; } = new() { FilePath = Path.GetFullPath(track.FilePath), LyricsPath = track.LyricsPath };
    public string Name => Path.GetFileName(Track.FilePath);
    public string Folder => Path.GetDirectoryName(Track.FilePath) ?? "";
    private string _status = "等待翻译";
    public string Status { get => _status; set => Set(ref _status, value); }
    private double _percent;
    public double Percent { get => _percent; set => Set(ref _percent, value); }
    private RecognitionState _state;
    public RecognitionState State { get => _state; set => Set(ref _state, value); }
}
