using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Selector = System.Windows.Controls.Primitives.Selector;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AudioPlayer.Models;
using AudioPlayer.Services;
using AudioPlayer.ViewModels;
using AudioPlayer.Views;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AudioPlayer;

public partial class MainWindow : Window
{
    private const string AudioFilter = "音频文件|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac;*.aiff;*.aif|所有文件|*.*";
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac", ".aiff", ".aif" };
    private readonly StateStore _store;
    private readonly PluginManager _plugins = new();
    private readonly PlayerViewModel _view;
    private readonly AudioService _audio = new();
    private readonly PlaybackQueue _queue = new();
    private readonly TranscriptionService _transcriber = new();
    private readonly TranscriptionService _liveTranscriber;
    private readonly CoverArtService _covers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private System.Drawing.Icon? _applicationIcon;
    private bool _refreshingModels;
    private long _coverRequest;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly bool _integration;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayLyrics;
    private Forms.ToolStripMenuItem? _trayLock;
    private LyricsWindow? _lyricsWindow;
    private LrcDocument _lyrics = new(Array.Empty<LyricLine>());
    private Track? _current;
    private CancellationTokenSource? _recognitionCancellation;
    private Task? _recognitionTask;
    private RecognitionPauseControl? _batchPause;
    private readonly HashSet<string> _activeBatchPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LyricLine> _streamingLines = new();
    private CancellationTokenSource? _liveCancellation;
    private Task? _liveTask;
    private long _liveRequest;
    private string? _liveAttemptedPath;
    private bool _drawerOpen;
    private int _drawerTransition;
    private bool _ready, _exiting, _allowClose, _updatingSeek, _seeking, _importing, _trayHintShown, _disposed;

    public MainWindow() : this(new StateStore(), true) { }

    // Integration can be disabled for deterministic XAML/render tests without a notification icon.
    public MainWindow(StateStore store, bool integration, TranscriptionService? liveTranscriber = null)
    {
        _store = store;
        _liveTranscriber = liveTranscriber ?? new TranscriptionService();
        _integration = integration;
        _view = new PlayerViewModel(store.Load());
        LocalEngineLocator.ApplyDefaults(_view.Settings);
        InitializeComponent();
        DataContext = _view;
        FullLyrics.SeekRequested += SeekFromLyrics;
        ReloadModels();
        FontPicker.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(f => f).ToArray();
        _audio.Opened += () =>
        {
            _updatingSeek = true;
            SeekSlider.Maximum = Math.Max(1, _audio.Duration.TotalSeconds);
            SeekSlider.IsEnabled = _audio.Duration > TimeSpan.Zero;
            _updatingSeek = false;
            _view.DurationText = FormatTime(_audio.Duration);
            _view.Status = "正在播放 · " + _current?.Title;
            UpdatePlaybackUi();
            EnsureLiveRecognition();
        };
        _audio.Ended += () => Navigate(false, true);
        _audio.Failed += message =>
        {
            _view.Status = "无法播放：" + message + "（请检查文件或 Windows 解码器）";
            SeekSlider.IsEnabled = false;
            UpdatePlaybackUi();
        };
        _clock.Tick += (_, _) => Tick();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveState(); };
        _view.Settings.PropertyChanged += Settings_PropertyChanged;
        _ready = true;
        _plugins.Start(Path.Combine(AppContext.BaseDirectory, "plugins"), _store.DirectoryPath, _view.Settings.EnabledPlugins, () => _view.Settings.PythonPath, ScheduleSave);
        PluginSettingsList.ItemsSource = _plugins.Entries;
        PluginSummary.Text = _plugins.Entries.Count == 0 ? "未发现插件。将插件文件夹放入程序目录的 plugins 后重启。" : $"已发现 {_plugins.Entries.Count} 个插件，默认关闭。更改开关后重启播放器生效。";
        if (_plugins.Errors.Count > 0) PluginSummary.Text += "\n" + string.Join("\n", _plugins.Errors);
        foreach (var plugin in _plugins.Entries.Where(p => p.Page is not null))
            MainTabs.Items.Add(new TabItem { Header = new TextBlock { Text = plugin.Name, TextWrapping = TextWrapping.Wrap }, Content = plugin.Page });
        if (_integration)
        {
            CreateTray();
            Application.Current.SessionEnding += SessionEnding;
        }
        ApplySettings();
        _clock.Start();
        if (store.LoadWarning is not null) _view.Status = store.LoadWarning;
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_ready || _exiting) return;
        if (e.PropertyName is nameof(PlayerSettings.DesktopLyrics) or nameof(PlayerSettings.LockLyrics) or nameof(PlayerSettings.VerticalLyrics) or nameof(PlayerSettings.RecognizeToSimplified))
            Log.Information("Setting changed; name={Setting}; desktopLyrics={DesktopLyrics}; locked={Locked}; vertical={Vertical}; simplified={Simplified}",
                e.PropertyName, _view.Settings.DesktopLyrics, _view.Settings.LockLyrics, _view.Settings.VerticalLyrics, _view.Settings.RecognizeToSimplified);
        if (e.PropertyName == nameof(PlayerSettings.ModelsDirectory)) ReloadModels();
        if (e.PropertyName == nameof(PlayerSettings.ModelPath) && !_refreshingModels)
            _view.Settings.ModelId = _view.Models.FirstOrDefault(m => m.DirectoryPath == _view.Settings.ModelPath)?.Id ?? _view.Settings.ModelId;
        if (e.PropertyName == nameof(PlayerSettings.AutoRecognizePlaying))
        {
            CancelLiveRecognition(); _liveAttemptedPath = null;
            if (!_view.Settings.AutoRecognizePlaying) _view.LiveRecognitionStatus = "";
            else EnsureLiveRecognition();
        }
        if (!_refreshingModels && e.PropertyName is nameof(PlayerSettings.ModelPath) or nameof(PlayerSettings.PythonPath) or nameof(PlayerSettings.UseCuda) or nameof(PlayerSettings.Language))
        { CancelLiveRecognition(); _liveAttemptedPath = null; EnsureLiveRecognition(); }
        if (e.PropertyName is not nameof(PlayerSettings.LyricLeft) and not nameof(PlayerSettings.LyricTop)) ApplySettings();
        ScheduleSave();
    }

    private void Settings_SourceUpdated(object sender, DataTransferEventArgs e) { /* Settings notify through INotifyPropertyChanged. */ }

    private void Cover_Click(object sender, RoutedEventArgs e) => SetLyricsDrawer(!_drawerOpen);
    private void CloseDrawer_Click(object sender, RoutedEventArgs e) => SetLyricsDrawer(false);
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _drawerOpen) { SetLyricsDrawer(false); e.Handled = true; }
    }
    private void SetLyricsDrawer(bool open)
    {
        if (_drawerOpen == open) return;
        _drawerOpen = open;
        int transition = ++_drawerTransition;
        double height = Math.Max(1, BodyHost.ActualHeight);
        double from = LyricsDrawer.Visibility == Visibility.Visible ? DrawerTransform.Y : height;
        LyricsDrawer.Visibility = Visibility.Visible;
        LyricsDrawer.IsHitTestVisible = open;
        MainTabs.IsEnabled = !open;
        CoverButton.ToolTip = open ? "收起封面与歌词（Esc）" : "展开封面与歌词";
        System.Windows.Automation.AutomationProperties.SetName(CoverButton, open ? "收起封面与歌词" : "展开封面与歌词");
        var animation = new DoubleAnimation(from, open ? 0 : height, TimeSpan.FromMilliseconds(280))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) =>
        {
            if (transition != _drawerTransition) return;
            DrawerTransform.BeginAnimation(TranslateTransform.YProperty, null);
            DrawerTransform.Y = 0;
            if (!open) LyricsDrawer.Visibility = Visibility.Collapsed;
        };
        DrawerTransform.BeginAnimation(TranslateTransform.YProperty, animation);
        if (open) { CloseDrawerButton.Focus(); FullLyrics.ResumeFollowing(); }
        else CoverButton.Focus();
    }

    private void ApplySettings()
    {
        _audio.Volume = _view.Settings.Volume;
        _audio.PlaybackRate = _view.Settings.PlaybackRate;
        if (_integration)
        {
            if (_view.Settings.DesktopLyrics)
            {
                if (_lyricsWindow is null)
                {
                    var lyricsWindow = new LyricsWindow(_view.Settings);
                    _lyricsWindow = lyricsWindow;
                    lyricsWindow.PositionSaved += ScheduleSave;
                    lyricsWindow.Closed += (_, _) => { if (ReferenceEquals(_lyricsWindow, lyricsWindow)) _lyricsWindow = null; };
                }
                _lyricsWindow.ApplySettings();
                _lyricsWindow.EnsureVisible();
                UpdateLyrics();
            }
            else _lyricsWindow?.Hide();
        }
        if (_trayLyrics is not null) _trayLyrics.Checked = _view.Settings.DesktopLyrics;
        if (_trayLock is not null) _trayLock.Checked = _view.Settings.LockLyrics;
    }

    private void ScheduleSave()
    {
        if (!_ready || _exiting) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveState()
    {
        try { _store.Save(_view.State); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log.Error(ex, "Saving application settings failed"); _view.Status = "配置保存失败：" + ex.Message; }
    }

    public async void ImportPaths(IEnumerable<string> paths) => await ImportPathsAsync(paths);

    public async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        if (_importing || _exiting) return;
        _importing = true;
        Playlist destination = _view.SelectedPlaylist;
        _view.Status = "正在读取音频文件…";
        var input = paths.ToArray();
        try
        {
            var files = await Task.Run(() =>
            {
                var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in input)
                {
                    if (Directory.Exists(path))
                    {
                        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                        foreach (string file in Directory.EnumerateFiles(path, "*", options))
                            if (Extensions.Contains(Path.GetExtension(file))) result.Add(Path.GetFullPath(file));
                    }
                    else if (File.Exists(path) && Extensions.Contains(Path.GetExtension(path))) result.Add(Path.GetFullPath(path));
                }
                return result;
            });
            if (_exiting || !_view.State.Playlists.Contains(destination)) return;
            var known = destination.Tracks.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            foreach (string file in files)
                if (known.Add(file)) { destination.Tracks.Add(new Track { FilePath = file }); count++; }
            _view.RefreshCount();
            _queue.Reset();
            _view.Status = $"已向「{destination.Name}」添加 {count} 首音频（重复文件已跳过）";
            ScheduleSave();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Log.Warning(ex, "Importing audio files failed"); _view.Status = "读取文件失败：" + ex.Message; }
        finally { _importing = false; }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择音频文件", Filter = AudioFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) await ImportPathsAsync(dialog.FileNames);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择音频文件夹（包含子文件夹）", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await ImportPathsAsync(dialog.FolderNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await ImportPathsAsync(paths);
    }

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        string? name = PromptName("新建播放列表", "新的播放列表");
        if (name is null) return;
        var playlist = new Playlist { Name = name };
        _view.State.Playlists.Add(playlist);
        _view.SelectedPlaylist = playlist;
        ScheduleSave();
    }

    private void RenamePlaylist_Click(object sender, RoutedEventArgs e)
    {
        string? name = PromptName("重命名播放列表", _view.SelectedPlaylist.Name);
        if (name is null) return;
        _view.SelectedPlaylist.Name = name;
        ScheduleSave();
    }

    private string? PromptName(string title, string initial)
    {
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 18), MaxLength = 100 };
        var ok = new Button { Content = "保存", IsDefault = true, Padding = new Thickness(22, 8, 22, 8) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(10, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "列表名称" }); panel.Children.Add(input); panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Owner = this, Width = 410, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel, ShowInTaskbar = false
        };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        Playlist playlist = _view.SelectedPlaylist;
        if (MessageBox.Show(this, $"删除「{playlist.Name}」播放列表？音频文件会保留。", "删除列表", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_view.State.Playlists.Count == 1) _view.State.Playlists.Add(new Playlist());
        _view.SelectedPlaylist = _view.State.Playlists.First(p => p != playlist);
        _view.State.Playlists.Remove(playlist);
        ScheduleSave();
    }

    private void Playlist_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || e.AddedItems.Count == 0) return;
        StopCurrent();
        _view.SelectedTrack = null;
        _queue.Reset();
        _view.RefreshCount();
        ScheduleSave();
    }

    private void Track_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_view.RecognitionBusy && TrackList.SelectedItem is Track track && _view.RecognitionQueue.Count == 0)
            _view.RecognitionTarget = "未加入队列 · 可识别选中音频：" + track.Title;
    }

    private void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_view.SelectedTrack is not { } track) return;
        if (_current?.Id == track.Id) StopCurrent();
        _view.SelectedPlaylist.Tracks.Remove(track);
        _queue.Reset(); _view.RefreshCount(); ScheduleSave();
    }

    private void MoveTrack(int delta)
    {
        if (_view.SelectedTrack is not { } track) return;
        var tracks = _view.SelectedPlaylist.Tracks;
        int index = tracks.IndexOf(track), target = index + delta;
        if (index < 0 || target < 0 || target >= tracks.Count) return;
        tracks.Move(index, target); _queue.Reset(); ScheduleSave();
    }
    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveTrack(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveTrack(1);

    private void Track_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(TrackList, e.OriginalSource as DependencyObject) is ListBoxItem item && item.DataContext is Track track)
        { _queue.Reset(); PlayTrack(track); }
    }
    private void Track_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _view.SelectedTrack is { } track) { _queue.Reset(); PlayTrack(track); e.Handled = true; }
        else if (e.Key == Key.Delete) { RemoveTrack_Click(sender, e); e.Handled = true; }
    }

    private void PlayTrack(Track track)
    {
        CancelLiveRecognition(); _liveAttemptedPath = null; _streamingLines.Clear(); _view.LiveRecognitionStatus = "";
        _view.LyricLines = Array.Empty<LyricLine>(); _view.CurrentLyricIndex = -1;
        if (!File.Exists(track.FilePath)) { StopCurrent(); _view.Status = "文件已移动或不存在：" + track.FilePath; return; }
        _current = track;
        _view.SelectedTrack = track;
        _view.CurrentTitle = track.Title;
        _view.CurrentInfo = track.Format + " · " + track.Folder;
        LoadCover(track);
        _view.Status = "正在打开 · " + track.Title;
        _view.PositionText = "00:00"; _view.DurationText = "00:00";
        _updatingSeek = true; SeekSlider.Value = 0; SeekSlider.IsEnabled = false; _updatingSeek = false;
        LoadLyrics(track);
        try { _audio.Open(track.FilePath); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        { Log.Error(ex, "Opening audio failed; path={Path}", track.FilePath); _audio.Stop(); _view.Status = "打开音频失败：" + ex.Message; }
        UpdatePlaybackUi();
    }

    private void StopCurrent()
    {
        CancelLiveRecognition(); _liveAttemptedPath = null; _streamingLines.Clear(); _view.LiveRecognitionStatus = "";
        _audio.Stop(); _current = null; _lyrics = new(Array.Empty<LyricLine>());
        _coverRequest++; _view.Cover = null;
        _view.CurrentTitle = "让喜欢的声音，陪你一会儿";
        _view.CurrentInfo = "添加音频或拖入文件，开始聆听";
        _view.PositionText = "00:00"; _view.DurationText = "00:00";
        _updatingSeek = true; SeekSlider.Value = 0; SeekSlider.IsEnabled = false; _updatingSeek = false;
        UpdateLyrics(); UpdatePlaybackUi();
    }

    private void Play_Click(object? sender, RoutedEventArgs? e)
    {
        if (_current is not null && _audio.IsReady) _audio.Toggle();
        else
        {
            var track = _view.SelectedTrack ?? _queue.Next(_view.SelectedPlaylist.Tracks.ToArray(), null, _view.Settings.Mode, false, false, _view.Settings.RepeatPlaylist);
            if (track is null) { _view.Status = "请先添加音频文件。"; return; }
            PlayTrack(track);
        }
        UpdatePlaybackUi();
    }
    private void Previous_Click(object? sender, RoutedEventArgs? e) => Navigate(true, false);
    private void Next_Click(object? sender, RoutedEventArgs? e) => Navigate(false, false);

    private void Navigate(bool backwards, bool automatic)
    {
        Track? next = _queue.Next(_view.SelectedPlaylist.Tracks.ToArray(), _current, _view.Settings.Mode, backwards, automatic, _view.Settings.RepeatPlaylist);
        if (next is not null) PlayTrack(next);
        else
        {
            _view.Status = automatic ? "播放结束" : "播放列表为空";
            UpdatePlaybackUi();
        }
    }

    private void UpdatePlaybackUi()
    {
        _view.IsPlaying = _audio.IsPlaying;
        _view.PlayLabel = _audio.IsPlaying ? "暂停" : "播放";
        if (_tray is not null)
        {
            string text = "声屿 · " + (_current?.Title ?? "Audio Player");
            _tray.Text = text.Length > 63 ? text[..60] + "…" : text;
        }
    }

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    private void Tick()
    {
        if (_exiting) return;
        if (_integration && _view.Settings.DesktopLyrics && _lyricsWindow is null) ApplySettings();
        if (!_seeking)
        {
            _updatingSeek = true;
            SeekSlider.Value = Math.Min(SeekSlider.Maximum, _audio.Position.TotalSeconds);
            _updatingSeek = false;
            _view.PositionText = FormatTime(_audio.Position);
        }
        UpdateLyrics();
    }
    private void Seek_MouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;
    private void SeekFromLyrics(TimeSpan timestamp)
    {
        if (!_audio.IsReady) return;
        double seconds = Math.Clamp(timestamp.TotalSeconds - _view.Settings.LyricOffsetSeconds, 0, _audio.Duration.TotalSeconds);
        _audio.Seek(seconds);
        _updatingSeek = true; SeekSlider.Value = seconds; _updatingSeek = false;
        _view.PositionText = FormatTime(TimeSpan.FromSeconds(seconds));
        UpdateLyrics();
        Log.Information("Seek from lyrics; timestamp={Timestamp}; offset={Offset}; targetSeconds={TargetSeconds}; playing={Playing}", timestamp, _view.Settings.LyricOffsetSeconds, seconds, _audio.IsPlaying);
    }
    private void Seek_MouseUp(object sender, MouseButtonEventArgs e) => FinishSeek();
    private void Seek_LostCapture(object sender, MouseEventArgs e) => FinishSeek();
    private void FinishSeek()
    {
        if (!_seeking) return;
        _seeking = false;
        _audio.Seek(SeekSlider.Value);
        UpdateLyrics();
    }
    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updatingSeek) return;
        _view.PositionText = FormatTime(TimeSpan.FromSeconds(e.NewValue));
        if (!_seeking) _audio.Seek(e.NewValue);
    }

    private void LoadLyrics(Track track)
    {
        _lyrics = new(Array.Empty<LyricLine>());
        string candidate = track.LyricsPath is { } custom && File.Exists(custom) ? custom : Path.ChangeExtension(track.FilePath, ".lrc");
        if (File.Exists(candidate))
        {
            CancelLiveRecognition(); _streamingLines.Clear();
            try { _lyrics = LrcDocument.Load(candidate); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { Log.Warning(ex, "Reading lyrics failed; path={Path}", candidate); _view.Status = "读取歌词失败：" + ex.Message; }
        }
        UpdateLyrics();
    }

    private static bool HasLyricsFile(Track track) =>
        (track.LyricsPath is { } custom && File.Exists(custom)) || File.Exists(Path.ChangeExtension(track.FilePath, ".lrc"));

    private void CancelLiveRecognition()
    {
        _liveRequest++;
        _liveCancellation?.Cancel();
    }
    private void EnsureLiveRecognition()
    {
        if (_exiting || !_view.Settings.AutoRecognizePlaying || !_audio.IsReady || _current is not { } track || HasLyricsFile(track)) return;
        if (_activeBatchPaths.Contains(track.FilePath))
        { CancelLiveRecognition(); _view.LiveRecognitionStatus = "边听边识别 · 使用当前批量任务的逐段字幕"; return; }
        if (string.Equals(_liveAttemptedPath, track.FilePath, StringComparison.OrdinalIgnoreCase)) return;
        _liveAttemptedPath = track.FilePath;
        _streamingLines.Clear();
        _lyrics = new(Array.Empty<LyricLine>());
        var settings = new PlayerSettings
        {
            PythonPath = _view.Settings.PythonPath, ModelPath = _view.Settings.ModelPath, Language = _view.Settings.Language,
            UseCuda = _view.Settings.UseCuda, SpeechVad = _view.Settings.SpeechVad,
            RecognizeToSimplified = _view.Settings.RecognizeToSimplified
        };
        var previous = _liveTask;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _liveCancellation = cancellation;
        long request = ++_liveRequest;
        _liveTask = RecognizeWhilePlayingAsync(track, settings, previous, request, cancellation);
        UpdateLyrics();
    }
    private async Task RecognizeWhilePlayingAsync(Track track, PlayerSettings settings, Task? previous, long request, CancellationTokenSource cancellation)
    {
        bool Current() => !_exiting && request == _liveRequest && _current?.Id == track.Id && _view.Settings.AutoRecognizePlaying && !cancellation.IsCancellationRequested;
        try
        {
            // Rapid track changes wait for the old worker to release its model before loading another.
            if (previous is not null) await previous;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Current() || HasLyricsFile(track)) return;
            _view.LiveRecognitionStatus = "边听边识别 · 正在加载模型，首段字幕生成后自动显示";
            var progress = new Progress<RecognitionProgress>(p => { if (Current()) _view.LiveRecognitionStatus = $"边听边识别 · {p.Percent:F0}% · {p.Message}"; });
            string output = Path.ChangeExtension(track.FilePath, ".lrc");
            await _liveTranscriber.RunAsync(settings, track.FilePath, output, progress, cancellation.Token,
                segment => { if (Current()) ApplyStreamingSegment(track.FilePath, segment); }, overwrite: false);
            if (!Current()) return;
            AttachGeneratedLyrics(track.FilePath, output);
            _view.LiveRecognitionStatus = "边听边识别完成 · 已保存同名 LRC";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error(ex, "Live recognition failed; audio={Audio}", track.FilePath); if (Current()) _view.LiveRecognitionStatus = "边听边识别未完成：" + ex.Message; }
        finally
        {
            if (ReferenceEquals(_liveCancellation, cancellation)) _liveCancellation = null;
            cancellation.Dispose();
        }
    }
    private void ApplyStreamingSegment(string audio, RecognizedSegment segment)
    {
        if (_exiting || !_view.Settings.AutoRecognizePlaying || _current is not { } track || !string.Equals(track.FilePath, audio, StringComparison.OrdinalIgnoreCase) || HasLyricsFile(track)) return;
        if (string.IsNullOrWhiteSpace(segment.Text)) return;
        var line = new LyricLine(TimeSpan.FromSeconds(segment.Start), segment.Text);
        if (_streamingLines.Contains(line)) return;
        _streamingLines.Add(line);
        _streamingLines.Add(new(TimeSpan.FromSeconds(segment.End), ""));
        _lyrics = new LrcDocument(_streamingLines.OrderBy(l => l.Time).ToArray());
        UpdateLyrics();
    }

    private void UpdateLyrics()
    {
        _view.LyricLines = _lyrics.Lines;
        if (_lyrics.Lines.Count == 0)
        {
            _view.CurrentLyricIndex = -1;
            bool recognizing = _view.Settings.AutoRecognizePlaying && (_liveCancellation is not null || (_current is not null && _activeBatchPaths.Contains(_current.FilePath)));
            _view.CurrentLyric = recognizing ? "正在识别当前音频…" : _current is null ? "此刻，静待声音" : "纯粹聆听，也很好";
            _view.NextLyric = "支持同名 LRC 歌词，也可以在本机生成";
            _lyricsWindow?.SetText(recognizing ? "正在识别…" : _current?.Title ?? "声屿 · 桌面歌词");
            return;
        }
        int index = _lyrics.FindLine(_audio.Position + TimeSpan.FromSeconds(_view.Settings.LyricOffsetSeconds));
        _view.CurrentLyricIndex = index;
        string text = index >= 0 ? _lyrics.Lines[index].Text : "";
        _view.CurrentLyric = string.IsNullOrWhiteSpace(text) ? "♪" : text;
        _view.NextLyric = _lyrics.Lines.Skip(index + 1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Text))?.Text ?? "";
        _lyricsWindow?.SetText(text);
    }

    private void LoadLyrics_Click(object sender, RoutedEventArgs e)
    {
        Track? track = _current ?? _view.SelectedTrack;
        if (track is null) { _view.Status = "请先选择或播放一首音频。"; return; }
        var dialog = new OpenFileDialog { Filter = "LRC 歌词|*.lrc", Title = "选择歌词文件" };
        if (dialog.ShowDialog(this) != true) return;
        track.LyricsPath = dialog.FileName;
        if (_current?.Id == track.Id) LoadLyrics(track);
        _view.Status = "已关联歌词：" + Path.GetFileName(dialog.FileName);
        ScheduleSave();
    }

    private void ChooseColor_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.ColorDialog { FullOpen = true };
        try { var color = (Color)ColorConverter.ConvertFromString(_view.Settings.LyricColor); dialog.Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B); }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException) { }
        if (dialog.ShowDialog(new NativeWindowOwner(this)) == Forms.DialogResult.OK)
            _view.Settings.LyricColor = $"#FF{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    private void ResetLyricsPosition_Click(object sender, RoutedEventArgs e)
    {
        _view.Settings.LyricLeft = null; _view.Settings.LyricTop = null;
        _lyricsWindow?.Place(true); _lyricsWindow?.EnsureVisible(refreshDisplay: true); ScheduleSave();
    }
    private void BrowsePython_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 Python 可执行文件", Filter = "Python 可执行文件|python.exe;python3.exe|可执行文件|*.exe" };
        if (dialog.ShowDialog(this) == true) _view.Settings.PythonPath = dialog.FileName;
    }
    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择包含多个模型子文件夹的 models 目录" };
        if (dialog.ShowDialog(this) == true)
        {
            bool single = ModelCatalog.IsComplete(dialog.FolderName);
            _view.Settings.ModelsDirectory = single ? Path.GetDirectoryName(dialog.FolderName)! : dialog.FolderName;
            ReloadModels();
            if (single) _view.Settings.ModelPath = dialog.FolderName;
        }
    }

    private void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        // Commit an edited repository path before scanning it.
        ModelsDirectoryInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        ReloadModels();
    }
    private void Model_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingModels && ModelPicker.SelectedItem is LocalModel selected) _view.Settings.ModelPath = selected.DirectoryPath;
    }
    private void OnlineModels_Click(object sender, RoutedEventArgs e)
    {
        var window = new ModelManagerWindow(_view.Settings) { Owner = this };
        window.ModelsChanged += ReloadModels;
        window.ShowDialog();
        ReloadModels(); ScheduleSave();
    }
    private void ReloadModels()
    {
        if (_refreshingModels) return;
        _refreshingModels = true;
        string previous = _view.Settings.ModelPath;
        string previousId = _view.Settings.ModelId;
        try
        {
            string root = ModelCatalog.NormalizeRoot(_view.Settings.ModelsDirectory);
            var models = ModelCatalog.Discover(root);
            _view.Settings.ModelsDirectory = root;
            // Replace the complete snapshot at once. Selection changes while replacing ItemsSource
            // must not overwrite the configured model with a transient null/first entry.
            _view.Models = models;
            var selected = models.FirstOrDefault(m => m.DirectoryPath.Equals(previous, StringComparison.OrdinalIgnoreCase)) ?? models.FirstOrDefault(m => m.Id == previousId) ?? models.FirstOrDefault();
            _view.Settings.ModelPath = selected?.DirectoryPath ?? "";
            Log.Information("Model catalog refreshed; root={Root}; count={Count}; selected={Selected}", root, models.Count, selected?.Id);
            ModelPicker.SetCurrentValue(Selector.SelectedValueProperty, selected?.DirectoryPath);
            if (selected is not null) _view.Settings.ModelId = selected.Id;
            _view.ModelSummary = $"已发现 {models.Count} 个完整本地模型 · 已排除仅英语模型，可选择多语言模型识别中文。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Log.Warning(ex, "Reading model catalog failed"); _view.ModelSummary = "模型目录读取失败：" + ex.Message; }
        finally { _refreshingModels = false; }
    }

    private async void LoadCover(Track track)
    {
        long request = ++_coverRequest;
        _view.Cover = null;
        try
        {
            var metadata = await _covers.LoadAsync(track.FilePath);
            if (_exiting || request != _coverRequest || _current?.Id != track.Id) return;
            _view.Cover = metadata.Cover;
            var parts = new[] { metadata.Artist, metadata.Album, track.Format }.Where(s => !string.IsNullOrWhiteSpace(s));
            _view.CurrentInfo = string.Join(" · ", parts);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        { Log.Warning(ex, "Reading audio cover failed; path={Path}", track.FilePath); if (request == _coverRequest) _view.Status = "封面无法读取，音频仍可继续播放：" + ex.Message; }
    }

    private async void InspectEnvironment_Click(object sender, RoutedEventArgs e)
    {
        if (_view.EnvironmentBusy) return;
        _view.EnvironmentBusy = true;
        _view.Status = "正在检测本地 Python、显卡与 CUDA 运行库…";
        try
        {
            var report = await new EnvironmentInspectionService().InspectAsync(_view.Settings.PythonPath, _lifetime.Token);
            if (_exiting) return;
            _view.Status = report.Summary;
            new EnvironmentWindow(report) { Owner = this }.ShowDialog();
        }
        catch (OperationCanceledException) { }
        finally { _view.EnvironmentBusy = false; }
    }

    private void ChooseRecognition_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "批量选择待识别音频", Filter = AudioFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) AddRecognitionTargets(dialog.FileNames);
    }
    private void UseSelectedRecognition_Click(object sender, RoutedEventArgs e)
    {
        if ((_view.SelectedTrack ?? _current) is { } track) AddRecognitionTargets(new[] { track.FilePath });
        else _view.RecognitionStatus = "请先在「我的音乐」中选择一首音频。";
    }
    private void UsePlaylistRecognition_Click(object sender, RoutedEventArgs e) => AddRecognitionTargets(_view.SelectedPlaylist.Tracks.Select(t => t.FilePath));
    private void RemoveRecognition_Click(object sender, RoutedEventArgs e)
    {
        if (_view.RecognitionBusy) return;
        foreach (var item in RecognitionList.SelectedItems.Cast<RecognitionItem>().ToArray()) _view.RecognitionQueue.Remove(item);
        RefreshRecognitionCount();
    }
    private void ClearRecognition_Click(object sender, RoutedEventArgs e)
    {
        if (_view.RecognitionBusy) return;
        _view.RecognitionQueue.Clear(); RefreshRecognitionCount();
    }
    public void AddRecognitionTargets(IEnumerable<string> paths)
    {
        if (_view.RecognitionBusy) return;
        var known = _view.RecognitionQueue.Select(item => item.AudioPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path);
            if (known.Add(full)) _view.RecognitionQueue.Add(new RecognitionItem(full));
        }
        RefreshRecognitionCount();
    }
    private void RefreshRecognitionCount() => _view.RecognitionTarget = $"队列中 {_view.RecognitionQueue.Count} 个音频 · 自动保存为同文件夹、同名 .lrc";

    private async void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (_view.RecognitionBusy) return;
        if (_view.RecognitionQueue.Count == 0 && (_view.SelectedTrack ?? _current) is { } track) AddRecognitionTargets(new[] { track.FilePath });
        if (_view.RecognitionQueue.Count == 0) { _view.RecognitionStatus = "请先把音频加入识别队列。"; return; }
        _recognitionTask = RecognizeBatchAsync();
        await _recognitionTask;
    }
    private async Task RecognizeBatchAsync()
    {
        var items = _view.RecognitionQueue.ToArray();
        _activeBatchPaths.Clear();
        foreach (var item in items) _activeBatchPaths.Add(item.AudioPath);
        if (_current is not null && _activeBatchPaths.Contains(_current.FilePath))
        { CancelLiveRecognition(); _streamingLines.Clear(); if (_view.Settings.AutoRecognizePlaying) _view.LiveRecognitionStatus = "边听边识别 · 使用批量任务字幕"; }
        foreach (var item in items) { item.State = RecognitionState.Pending; item.Percent = 0; item.Status = "等待识别"; }
        _view.RecognitionBusy = true; _view.RecognitionPercent = 0;
        _view.BatchRunning = true; _view.RecognitionPaused = false;
        _view.RecognitionStatus = $"正在加载模型 · 最多 {_view.Settings.RecognitionParallelism} 个任务并发…";
        using var pause = new RecognitionPauseControl();
        _batchPause = pause;
        using var cancellation = new CancellationTokenSource();
        _recognitionCancellation = cancellation;
        try
        {
            if (_liveTask is not null && _current is not null && _activeBatchPaths.Contains(_current.FilePath)) await _liveTask;
            var result = await new BatchTranscriptionService().RunAsync(_view.Settings, items.Select(i => i.AudioPath).ToArray(), _view.OverwriteLyrics, p =>
            {
                var item = items[p.Index];
                if (p.Segment is not null) { ApplyStreamingSegment(item.AudioPath, p.Segment); return; }
                item.State = p.State; item.Percent = p.Percent; item.Status = p.Message;
                _view.RecognitionPercent = items.Average(i => i.Percent);
                _view.RecognitionStatus = $"[{p.Index + 1}/{items.Length}] {item.Name} · {p.Message}";
                if (p.State == RecognitionState.Completed) AttachGeneratedLyrics(item.AudioPath, item.OutputPath);
            }, cancellation.Token, pause);
            _view.RecognitionPercent = 100;
            _view.RecognitionStatus = $"批量完成：成功 {result.Completed}，跳过 {result.Skipped}，失败 {result.Failed}。歌词位于各音频的同一文件夹。";
        }
        catch (OperationCanceledException)
        {
            foreach (var item in items.Where(i => i.State is RecognitionState.Pending or RecognitionState.Running or RecognitionState.Paused)) { item.State = RecognitionState.Cancelled; item.Status = "已取消"; }
            _view.RecognitionStatus = $"已取消剩余任务，保留已生成的 {items.Count(i => i.State == RecognitionState.Completed)} 个 LRC；未完成项不会替换已有歌词。";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Batch recognition interrupted");
            foreach (var item in items.Where(i => i.State is RecognitionState.Pending or RecognitionState.Running or RecognitionState.Paused)) { item.State = RecognitionState.Failed; item.Status = ex.Message; }
            _view.RecognitionStatus = "批量识别中断：" + ex.Message;
        }
        finally
        {
            _activeBatchPaths.Clear(); _batchPause = null; _recognitionCancellation = null;
            _view.RecognitionBusy = false; _view.BatchRunning = false; _view.RecognitionPaused = false;
        }
    }
    private void PauseRecognition_Click(object sender, RoutedEventArgs e)
    {
        if (_batchPause is null) return;
        try
        {
            if (_batchPause.IsPaused) { _batchPause.Resume(); _view.RecognitionPaused = false; _view.RecognitionStatus = "继续识别，保留已完成进度。"; Log.Information("Batch recognition resume requested"); }
            else { _batchPause.Pause(); _view.RecognitionPaused = true; _view.RecognitionStatus = "已请求暂停；当前计算片段结束后暂停，点击继续可接着识别。"; Log.Information("Batch recognition pause requested"); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error(ex, "Pause control failed"); _view.RecognitionStatus = "暂停控制失败：" + ex.Message; }
    }
    private void AttachGeneratedLyrics(string audio, string output)
    {
        foreach (Track track in _view.State.Playlists.SelectMany(p => p.Tracks).Where(t => string.Equals(t.FilePath, audio, StringComparison.OrdinalIgnoreCase))) track.LyricsPath = output;
        if (_current is not null && string.Equals(_current.FilePath, audio, StringComparison.OrdinalIgnoreCase))
        {
            LoadLyrics(_current);
            if (_view.Settings.AutoRecognizePlaying) _view.LiveRecognitionStatus = "识别完成 · 已载入同名 LRC";
        }
        ScheduleSave();
    }
    private async void CheckRecognition_Click(object sender, RoutedEventArgs e)
    {
        if (_view.RecognitionBusy) return;
        _recognitionTask = RecognizeAsync(null, null);
        await _recognitionTask;
    }
    private async Task RecognizeAsync(string? audio, string? output)
    {
        _view.RecognitionBusy = true; _view.RecognitionPercent = 0; _view.RecognitionStatus = "正在准备本地识别…";
        using var cancellation = new CancellationTokenSource();
        _recognitionCancellation = cancellation;
        try
        {
            var progress = new Progress<RecognitionProgress>(p =>
            {
                if (_exiting || cancellation.IsCancellationRequested || !_view.RecognitionBusy) return;
                _view.RecognitionPercent = p.Percent; _view.RecognitionStatus = p.Message;
            });
            await _transcriber.RunAsync(_view.Settings, audio, output, progress, cancellation.Token);
            if (_exiting) return;
            _view.RecognitionPercent = 100;
            _view.RecognitionStatus = output is null ? "本地环境检查通过，模型已成功加载。" : "歌词已生成：" + output;
            if (audio is not null && output is not null)
            {
                foreach (Track track in _view.State.Playlists.SelectMany(p => p.Tracks).Where(t => string.Equals(t.FilePath, audio, StringComparison.OrdinalIgnoreCase)))
                    track.LyricsPath = output;
                if (_current is not null && string.Equals(_current.FilePath, audio, StringComparison.OrdinalIgnoreCase)) LoadLyrics(_current);
                ScheduleSave();
            }
        }
        catch (OperationCanceledException) { _view.RecognitionStatus = "已取消识别，已有歌词文件未修改。"; }
        catch (Exception ex) { Log.Error(ex, "Recognition request failed; audio={Audio}; checkOnly={CheckOnly}", audio, audio is null); _view.RecognitionStatus = "识别未完成：" + ex.Message; }
        finally { _recognitionCancellation = null; _view.RecognitionBusy = false; }
    }
    private void CancelRecognition_Click(object sender, RoutedEventArgs e)
    {
        _recognitionCancellation?.Cancel();
        _view.RecognitionStatus = "正在停止识别进程…";
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开声屿", null, (_, _) => RestoreWindow());
        menu.Items.Add("播放 / 暂停", null, (_, _) => Play_Click(null, null));
        menu.Items.Add("上一首", null, (_, _) => Navigate(true, false));
        menu.Items.Add("下一首", null, (_, _) => Navigate(false, false));
        menu.Items.Add(new Forms.ToolStripSeparator());
        _trayLyrics = new Forms.ToolStripMenuItem("显示桌面歌词");
        _trayLyrics.Click += (_, _) => _view.Settings.DesktopLyrics = !_view.Settings.DesktopLyrics;
        menu.Items.Add(_trayLyrics);
        _trayLock = new Forms.ToolStripMenuItem("锁定桌面歌词");
        _trayLock.Click += (_, _) => _view.Settings.LockLyrics = !_view.Settings.LockLyrics;
        menu.Items.Add(_trayLock);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出声屿", null, async (_, _) => await ExitAsync());
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/AudioPlayer;component/Assets/player.ico")).Stream)
            _applicationIcon = new System.Drawing.Icon(stream);
        _tray = new Forms.NotifyIcon { Icon = _applicationIcon, Text = "声屿 · Audio Player", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => RestoreWindow();
    }

    private void RestoreWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    public async Task ActivateFromLaunch(string[] paths)
    {
        if (_exiting) return;
        RestoreWindow();
        foreach (Window child in OwnedWindows) if (child.IsVisible) child.Activate();
        if (paths.Length > 0) await ImportPathsAsync(paths);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (!_integration) { DisposeResources(); return; }
        e.Cancel = true;
        if (_exiting) return;
        if (_view.Settings.CloseToTray && _tray is not null)
        {
            Hide(); SaveState();
            if (!_trayHintShown)
            {
                _tray.ShowBalloonTip(2500, "声屿仍在运行", "双击托盘图标恢复窗口，右键菜单可以退出。", Forms.ToolTipIcon.Info);
                _trayHintShown = true;
            }
            return;
        }
        await ExitAsync();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _lifetime.Cancel();
        _clock.Stop(); _saveTimer.Stop();
        await _plugins.StopAsync();
        _recognitionCancellation?.Cancel();
        if (_recognitionTask is not null) await _recognitionTask;
        CancelLiveRecognition();
        if (_liveTask is not null) await _liveTask;
        SaveState();
        DisposeResources();
        _allowClose = true;
        Close();
        Application.Current.Shutdown();
    }

    private void SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _exiting = true; _allowClose = true;
        _recognitionCancellation?.Cancel();
        SaveState(); DisposeResources();
    }

    private void DisposeResources()
    {
        if (_disposed) return;
        _disposed = true;
        _plugins.Dispose();
        _lifetime.Cancel();
        CancelLiveRecognition();
        _clock.Stop(); _saveTimer.Stop(); _audio.Dispose();
        _view.Settings.PropertyChanged -= Settings_PropertyChanged;
        if (_integration) Application.Current.SessionEnding -= SessionEnding;
        _lyricsWindow?.Close(); _lyricsWindow = null;
        if (_tray is not null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); _tray = null; }
        _applicationIcon?.Dispose(); _applicationIcon = null;
    }

    private sealed class NativeWindowOwner : Forms.IWin32Window
    {
        public IntPtr Handle { get; }
        public NativeWindowOwner(Window window) => Handle = new WindowInteropHelper(window).Handle;
    }
}
