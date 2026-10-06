using AudioPlayer.Models;
using AudioPlayer.Services;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace AudioPlayer.ViewModels;

public sealed record ModeOption(PlayMode Value, string Label);
public sealed record PlaybackRateOption(double Value, string Label);

public sealed class PlayerViewModel : ObservableObject
{
    public PlayerState State { get; }
    public PlayerSettings Settings => State.Settings;
    public IReadOnlyList<TranslationLanguage> TranslationLanguages => LyricTranslation.Languages;
    public IReadOnlyList<LyricDisplayOption> LyricDisplayModes => LyricTranslation.DisplayModes;
    private string _translationHint = "";
    public string TranslationHint { get => _translationHint; set => Set(ref _translationHint, value); }
    public ObservableCollection<TranslationItem> TranslationQueue { get; } = new();
    private bool _translationBusy, _overwriteTranslation, _translationModelBusy;
    public bool TranslationBusy { get => _translationBusy; set { if (Set(ref _translationBusy, value)) Raise(nameof(CanTranslate)); } }
    public bool CanTranslate => !TranslationBusy;
    public bool OverwriteTranslation { get => _overwriteTranslation; set => Set(ref _overwriteTranslation, value); }
    public bool TranslationModelBusy { get => _translationModelBusy; set { if (Set(ref _translationModelBusy, value)) Raise(nameof(CanDownloadTranslationModel)); } }
    public bool CanDownloadTranslationModel => !TranslationModelBusy;
    private string _translationStatus = "加入音频或 LRC，原文语言自动识别，输出同目录语言 LRC。", _translationModelStatus = "翻译在本机完成；首次使用请下载或选择 M2M100 模型。";
    public string TranslationStatus { get => _translationStatus; set => Set(ref _translationStatus, value); }
    public string TranslationModelStatus { get => _translationModelStatus; set => Set(ref _translationModelStatus, value); }
    private double _translationPercent, _translationModelPercent;
    public double TranslationPercent { get => _translationPercent; set => Set(ref _translationPercent, value); }
    public double TranslationModelPercent { get => _translationModelPercent; set => Set(ref _translationModelPercent, value); }
    private IReadOnlyList<LocalModel> _models = Array.Empty<LocalModel>();
    public IReadOnlyList<LocalModel> Models { get => _models; set => Set(ref _models, value); }
    public IReadOnlyList<int> ParallelismOptions { get; } = new[] { 1, 2, 3, 4 };
    public IReadOnlyList<PlaybackRateOption> PlaybackRates { get; } = new[] { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3 }.Select(rate => new PlaybackRateOption(rate, $"{rate:0.##}×")).ToArray();
    private string _liveRecognitionStatus = "";
    public string LiveRecognitionStatus { get => _liveRecognitionStatus; set => Set(ref _liveRecognitionStatus, value); }
    private string _modelSummary = "";
    public string ModelSummary { get => _modelSummary; set => Set(ref _modelSummary, value); }
    private bool _environmentBusy;
    public bool EnvironmentBusy { get => _environmentBusy; set { if (Set(ref _environmentBusy, value)) Raise(nameof(CanInspectEnvironment)); } }
    public bool CanInspectEnvironment => !EnvironmentBusy;
    private ImageSource? _cover;
    public ImageSource? Cover { get => _cover; set { if (Set(ref _cover, value)) Raise(nameof(HasCover)); } }
    public bool HasCover => Cover is not null;
    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }
    public PlayerViewModel(PlayerState state)
    {
        State = state;
        _selectedPlaylist = state.Playlists.FirstOrDefault(p => p.Id == state.SelectedPlaylistId) ?? state.Playlists[0];
    }
    public IReadOnlyList<ModeOption> Modes { get; } = new[]
    {
        new ModeOption(PlayMode.Sequential, "顺序播放"), new ModeOption(PlayMode.Reverse, "逆序播放"),
        new ModeOption(PlayMode.Shuffle, "随机播放"), new ModeOption(PlayMode.RepeatOne, "单曲循环"),
        new ModeOption(PlayMode.Single, "单曲播放")
    };
    private Playlist _selectedPlaylist;
    public Playlist SelectedPlaylist { get => _selectedPlaylist; set { if (value is not null && Set(ref _selectedPlaylist, value)) { State.SelectedPlaylistId = value.Id; Raise(nameof(TrackCount)); } } }
    private Track? _selectedTrack;
    public Track? SelectedTrack { get => _selectedTrack; set => Set(ref _selectedTrack, value); }
    public string TrackCount => $"{SelectedPlaylist.Tracks.Count} 首音频";
    public void RefreshCount() => Raise(nameof(TrackCount));
    private string _currentTitle = "让喜欢的声音，陪你一会儿";
    public string CurrentTitle { get => _currentTitle; set => Set(ref _currentTitle, value); }
    private string _currentInfo = "添加音频或拖入文件，开始聆听";
    public string CurrentInfo { get => _currentInfo; set => Set(ref _currentInfo, value); }
    private string _currentLyric = "此刻，静待声音";
    public string CurrentLyric { get => _currentLyric; set => Set(ref _currentLyric, value); }
    private string _nextLyric = "支持同名 LRC 歌词，也可以在本机生成";
    public string NextLyric { get => _nextLyric; set => Set(ref _nextLyric, value); }
    private IReadOnlyList<LyricLine> _lyricLines = Array.Empty<LyricLine>();
    public IReadOnlyList<LyricLine> LyricLines { get => _lyricLines; set { if (Set(ref _lyricLines, value)) Raise(nameof(HasLyrics)); } }
    public bool HasLyrics => LyricLines.Count > 0;
    private int _currentLyricIndex = -1;
    public int CurrentLyricIndex { get => _currentLyricIndex; set => Set(ref _currentLyricIndex, value); }
    private string _positionText = "00:00";
    public string PositionText { get => _positionText; set => Set(ref _positionText, value); }
    private string _durationText = "00:00";
    public string DurationText { get => _durationText; set => Set(ref _durationText, value); }
    private string _playLabel = "播放";
    public string PlayLabel { get => _playLabel; set => Set(ref _playLabel, value); }
    private string _status = "就绪 · 音频与识别均在本机处理";
    public string Status { get => _status; set => Set(ref _status, value); }
    private string _recognitionStatus = "选择一首音频，将人声转换为带时间戳的歌词。";
    public string RecognitionStatus { get => _recognitionStatus; set => Set(ref _recognitionStatus, value); }
    private double _recognitionPercent;
    public double RecognitionPercent { get => _recognitionPercent; set => Set(ref _recognitionPercent, value); }
    private bool _recognitionBusy;
    public bool RecognitionBusy { get => _recognitionBusy; set { if (Set(ref _recognitionBusy, value)) Raise(nameof(CanRecognize)); } }
    public bool CanRecognize => !RecognitionBusy;
    private bool _batchRunning;
    public bool BatchRunning { get => _batchRunning; set => Set(ref _batchRunning, value); }
    private bool _recognitionPaused;
    public bool RecognitionPaused { get => _recognitionPaused; set { if (Set(ref _recognitionPaused, value)) Raise(nameof(PauseRecognitionLabel)); } }
    public string PauseRecognitionLabel => RecognitionPaused ? "继续识别" : "暂停识别";
    public ObservableCollection<RecognitionItem> RecognitionQueue { get; } = new();
    private bool _overwriteLyrics;
    public bool OverwriteLyrics { get => _overwriteLyrics; set => Set(ref _overwriteLyrics, value); }
    private string _recognitionTarget = "尚未选择音频";
    public string RecognitionTarget { get => _recognitionTarget; set => Set(ref _recognitionTarget, value); }
}
