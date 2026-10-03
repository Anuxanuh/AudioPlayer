using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AudioPlayer.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Track
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = "";
    public string? LyricsPath { get; set; }
    [JsonIgnore] public string Title => Path.GetFileNameWithoutExtension(FilePath);
    [JsonIgnore] public string Format => Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
    [JsonIgnore] public string Folder => Path.GetDirectoryName(FilePath) ?? "";
}

public sealed class Playlist : ObservableObject
{
    private string _name = "我的播放列表";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public ObservableCollection<Track> Tracks { get; set; } = new();
}

public enum PlayMode { Sequential, Reverse, Shuffle, RepeatOne, Single }

public sealed class PlayerSettings : ObservableObject
{
    public Dictionary<string, bool> EnabledPlugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    private bool _CloseToTray = true;
    public bool CloseToTray { get => _CloseToTray; set => Set(ref _CloseToTray, value); }
    private bool _DesktopLyrics = false;
    public bool DesktopLyrics { get => _DesktopLyrics; set => Set(ref _DesktopLyrics, value); }
    private bool _LockLyrics = false;
    public bool LockLyrics { get => _LockLyrics; set => Set(ref _LockLyrics, value); }
    private bool _VerticalLyrics;
    public bool VerticalLyrics { get => _VerticalLyrics; set => Set(ref _VerticalLyrics, value); }
    private double _FontSize = 34;
    public double FontSize { get => _FontSize; set => Set(ref _FontSize, value); }
    private string _FontFamily = "Microsoft YaHei UI";
    public string FontFamily { get => _FontFamily; set => Set(ref _FontFamily, value); }
    private string _LyricColor = "#FFFFD166";
    public string LyricColor { get => _LyricColor; set => Set(ref _LyricColor, value); }
    private double _LyricOpacity = 1;
    public double LyricOpacity { get => _LyricOpacity; set => Set(ref _LyricOpacity, value); }
    private double? _LyricLeft = null;
    public double? LyricLeft { get => _LyricLeft; set => Set(ref _LyricLeft, value); }
    private double? _LyricTop = null;
    public double? LyricTop { get => _LyricTop; set => Set(ref _LyricTop, value); }
    private double _LyricOffsetSeconds = 0;
    public double LyricOffsetSeconds { get => _LyricOffsetSeconds; set => Set(ref _LyricOffsetSeconds, value); }
    private double _Volume = 0.65;
    public double Volume { get => _Volume; set => Set(ref _Volume, value); }
    private double _PlaybackRate = 1;
    public double PlaybackRate { get => _PlaybackRate; set => Set(ref _PlaybackRate, value); }
    private int _RecognitionParallelism = 2;
    public int RecognitionParallelism { get => _RecognitionParallelism; set => Set(ref _RecognitionParallelism, value); }
    private bool _AutoRecognizePlaying;
    public bool AutoRecognizePlaying { get => _AutoRecognizePlaying; set => Set(ref _AutoRecognizePlaying, value); }
    private PlayMode _Mode = PlayMode.Sequential;
    public PlayMode Mode { get => _Mode; set => Set(ref _Mode, value); }
    private bool _RepeatPlaylist = true;
    public bool RepeatPlaylist { get => _RepeatPlaylist; set => Set(ref _RepeatPlaylist, value); }
    private string _PythonPath = "python";
    public string PythonPath { get => _PythonPath; set => Set(ref _PythonPath, value); }
    private string _ModelPath = "";
    public string ModelPath { get => _ModelPath; set => Set(ref _ModelPath, value); }
    private string _ModelsDirectory = "";
    public string ModelsDirectory { get => _ModelsDirectory; set => Set(ref _ModelsDirectory, value); }
    private string _ModelId = "tiny";
    public string ModelId { get => _ModelId; set => Set(ref _ModelId, value); }
    private string _Language = "auto";
    public string Language { get => _Language; set => Set(ref _Language, value); }
    private bool _UseCuda = false;
    public bool UseCuda { get => _UseCuda; set => Set(ref _UseCuda, value); }
    private bool _SpeechVad = false;
    public bool SpeechVad { get => _SpeechVad; set => Set(ref _SpeechVad, value); }
    private bool _RecognizeToSimplified = true;
    public bool RecognizeToSimplified { get => _RecognizeToSimplified; set => Set(ref _RecognizeToSimplified, value); }
}

public sealed class PlayerState
{
    public PlayerSettings Settings { get; set; } = new();
    public ObservableCollection<Playlist> Playlists { get; set; } = new();
    public Guid? SelectedPlaylistId { get; set; }
}
