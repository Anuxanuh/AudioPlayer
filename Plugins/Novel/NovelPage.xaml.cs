using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AudioPlayer.Plugin.Abstractions;
using Microsoft.Win32;

namespace AudioPlayer.Plugin.Novel;

public sealed class NovelPlugin : IPlayerPlugin
{
    private NovelPage? _page;
    public FrameworkElement CreatePage(PluginContext context) => _page = new(context);
    public Task StopAsync() => _page?.StopAsync() ?? Task.CompletedTask;
    public void Dispose() => _page?.Dispose();
}
public sealed record ChapterGroup(string Title, IReadOnlyList<Chapter> Chapters);

public partial class NovelPage : UserControl, IDisposable
{
    private readonly PluginContext _context;
    private readonly IPlaybackHost? _host;
    private readonly NovelStore _store;
    private NovelSettings _settings;
    private NovelBook? _book, _preview;
    private NovelMap? _map;
    private MapLookup? _lookup;
    private bool _hasCache, _busy, _disposed, _refreshingAvailability;
    private Guid _playlistId;
    private Chapter? _selected, _editingChapter;
    private List<ChapterGroup> _groups = new();
    private readonly TrackingState _tracking = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private DateTime _nextAvailability;
    private CancellationTokenSource? _work;
    private Task? _operation;
    private int? _manualStart;
    private readonly ObservableCollection<ChapterSegment> _segmentEdits = new();
    public NovelPage(PluginContext context)
    {
        _context = context; _host = context.Playback; _store = new(context.DataDirectory, context.ApplicationDirectory);
        string? warning = null;
        try { _settings = _store.LoadSettings(); }
        catch (Exception ex) { _settings = new(); warning = "设置无法读取，使用默认值：" + ex.Message; context.Log("error", warning); }
        InitializeComponent();
        NumberBox.ItemsSource = new[] { "自动", "阿拉伯数字", "中文数字", "罗马数字" };
        EncodingBox.ItemsSource = new[] { "自动", "utf-8", "gbk", "gb18030", "utf-16", "utf-16BE", "big5" };
        PopulateSettings(); EditSegmentsGrid.ItemsSource = _segmentEdits;
        _timer.Tick += Timer_Tick;
        if (warning is not null) SetStatus(warning);
        if (_host is null) { Toolbar.IsEnabled = Tabs.IsEnabled = false; SetStatus("当前宿主没有播放接口，请升级到随此插件发布的新版声屿。"); }
    }
    private void SetStatus(string text) { Status.Text = text; Status.ToolTip = text; }
    private BookBinding Binding => _settings.Bindings.TryGetValue(_playlistId, out var binding) ? binding : _settings.Bindings[_playlistId] = new();
    private void PopulateSettings()
    {
        var p = _settings.Parser;
        AutoLocateBox.IsChecked = _settings.AutoLocate; VolumesBox.IsChecked = _settings.GroupByVolume; GroupSizeBox.Text = _settings.GroupSize.ToString();
        ChapterRuleBox.Text = p.ChapterPattern; UnitsBox.Text = p.Units; NumberBox.SelectedItem = p.NumberFormat; HasTitleBox.IsChecked = p.HasTitle;
        VolumeRuleBox.Text = p.VolumePattern; SpecialRuleBox.Text = p.SpecialPattern; IgnoreRuleBox.Text = p.IgnorePattern; EncodingBox.SelectedItem = p.Encoding; CacheFolderBox.Text = _settings.CacheDirectory;
    }
    private void ReadSettings()
    {
        if (!int.TryParse(GroupSizeBox.Text, out int size) || size < 1 || size > 1000) throw new InvalidDataException("分组章节数请输入 1—1000。");
        var parser = new ParseSettings { ChapterPattern = ChapterRuleBox.Text, Units = UnitsBox.Text, NumberFormat = NumberBox.SelectedItem as string ?? "自动", HasTitle = HasTitleBox.IsChecked == true,
            VolumePattern = VolumeRuleBox.Text, SpecialPattern = SpecialRuleBox.Text, IgnorePattern = IgnoreRuleBox.Text, Encoding = EncodingBox.SelectedItem as string ?? "自动" };
        _ = NovelParser.Rule(parser.ChapterPattern, parser); _ = NovelParser.Rule(parser.VolumePattern, parser); _ = NovelParser.Rule(parser.SpecialPattern, parser); _ = NovelParser.Rule(parser.IgnorePattern, parser);
        _settings.Parser = parser; _settings.AutoLocate = AutoLocateBox.IsChecked == true; _settings.GroupByVolume = VolumesBox.IsChecked == true; _settings.GroupSize = size;
        _settings.CacheDirectory = string.IsNullOrWhiteSpace(CacheFolderBox.Text) ? "" : _store.Relative(_store.Resolve(CacheFolderBox.Text.Trim()));
        _store.SaveSettings(_settings);
    }
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || _host is null) return;
        _timer.Start(); await RunAsync(LoadBoundAsync);
    }
    private void Page_Unloaded(object sender, RoutedEventArgs e) => _timer.Stop();
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _disposed || _host is null) return;
        _busy = true; Tabs.IsEnabled = false;
        foreach (var button in Toolbar.Children.OfType<Button>()) button.IsEnabled = ReferenceEquals(button, CancelButton);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.ShutdownToken);
        _work = cancellation;
        try { _operation = action(cancellation.Token); await _operation; }
        catch (OperationCanceledException) { if (!_disposed) SetStatus("任务已取消，原缓存保留。"); }
        catch (StartAnchorException ex) { if (!_disposed) { ManualStartButton.IsEnabled = true; SetStatus(ex.Message); } }
        catch (Exception ex) { _context.Log("error", "小说插件：" + ex.Message); if (!_disposed) SetStatus(ex.Message); }
        finally
        {
            _work = null; _busy = false;
            if (!_disposed) { Tabs.IsEnabled = true; foreach (var button in Toolbar.Children.OfType<Button>()) button.IsEnabled = !ReferenceEquals(button, CancelButton); }
        }
    }
    private async Task LoadBoundAsync(CancellationToken token)
    {
        var snapshot = _host!.GetSnapshot(); _playlistId = snapshot.PlaylistId; PlaylistLabel.Text = snapshot.PlaylistName;
        _book = _preview = null; _map = null; _lookup = null; _hasCache = false; _selected = _editingChapter = null; _manualStart = null; ManualStartButton.IsEnabled = false;
        ChapterTree.ItemsSource = null; PreviewGrid.ItemsSource = null; BodyEditor.Clear(); ClearChapterDetails();
        if (!_settings.Bindings.TryGetValue(_playlistId, out var binding)) { CacheStatus.Text = "此播放列表尚未绑定小说"; SetStatus("请选择小说文本；不会自动绑定其他播放列表的小说。"); return; }
        SetStatus("正在加载小说与预览…");
        var loaded = await Task.Run(() =>
        {
            NovelMap? cache = null; NovelBook? parsed = null; string warning = "";
            if (!string.IsNullOrWhiteSpace(binding.CachePath))
                try { cache = _store.LoadCache(binding.CachePath, snapshot.PlaylistId); }
                catch (Exception ex) { warning = "缓存无法读取：" + ex.Message; }
            try
            {
                parsed = NovelParser.ParseFile(_store.Resolve(binding.SourcePath), _settings.Parser, token);
                if (!string.IsNullOrWhiteSpace(binding.PreviewPath))
                {
                    try { var corrected = _store.LoadPreview(binding.PreviewPath); if (corrected.ContentHash == parsed.ContentHash) parsed = corrected; }
                    catch (Exception ex) when (ex is not OperationCanceledException) { warning += " 校对预览无法读取：" + ex.Message; }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { if (cache is null) throw; warning += " 小说源文本无法读取，继续使用缓存：" + ex.Message; }
            bool mismatch = cache is not null && AlignmentEngine.Reconcile(cache, snapshot);
            return (cache, parsed, warning, mismatch);
        }, token);
        token.ThrowIfCancellationRequested();
        _map = loaded.cache; _hasCache = _map is not null; _book = _map?.Book ?? loaded.parsed;
        if (_book is null) throw new InvalidDataException("没有可用的小说或缓存。");
        _preview = NovelStore.Clone(loaded.parsed ?? _book);
        _lookup = _map is null ? null : new(_map);
        RebuildTree(); RefreshPreview(); UpdateAvailability(); ResetTracking();
        CacheStatus.Text = _hasCache ? $"缓存优先 · {_map!.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 自动跟踪已启用" + (loaded.mismatch ? " · 播放列表有变动，缓存保留，可重新生成" : "") : "无缓存 · 只在手动定位或解析后自动定位时匹配";
        if (_map is not null && loaded.parsed is not null && _map.Book.ContentHash != loaded.parsed.ContentHash) loaded.warning += " 源文本已改变：章节页仍使用缓存，解析预览显示新文本，可应用并重建。";
        SetStatus($"已加载 {_book.Chapters.Count} 章。" + loaded.warning);
        if (_settings.AutoLocate) await LocateAsync(token);
    }
    private async Task SelectBookAsync(string path, CancellationToken token)
    {
        var snapshot = _host!.GetSnapshot();
        var parsed = await Task.Run(() => NovelParser.ParseFile(path, NovelStore.Clone(_settings.Parser), token), token);
        token.ThrowIfCancellationRequested();
        if (snapshot.PlaylistId != _host.GetSnapshot().PlaylistId) throw new OperationCanceledException();
        string previewPath = await Task.Run(() => _store.SavePreview(snapshot.PlaylistId, parsed), token);
        token.ThrowIfCancellationRequested();
        if (snapshot.PlaylistId != _host.GetSnapshot().PlaylistId) throw new OperationCanceledException();
        _playlistId = snapshot.PlaylistId; PlaylistLabel.Text = snapshot.PlaylistName;
        _book = parsed; _preview = NovelStore.Clone(parsed); _manualStart = null; ManualStartButton.IsEnabled = false;
        Binding.SourcePath = _store.Relative(path); Binding.PreviewPath = previewPath; Binding.CachePath = "";
        InvalidateMap(); RebuildTree(); RefreshPreview();
        SetStatus($"已解析 {parsed.Chapters.Count} 章，可在“解析预览与校对”检查标题、顺序和分卷。");
        if (_settings.AutoLocate) await LocateAsync(token);
    }
    private async Task SavePreviewAsync(CancellationToken token)
    {
        if (_book is null) return;
        string path = await Task.Run(() => _store.SavePreview(_playlistId, _book), token);
        token.ThrowIfCancellationRequested(); Binding.PreviewPath = path; _store.SaveSettings(_settings);
    }
    private void InvalidateMap()
    {
        _map = null; _lookup = null; _hasCache = false; Binding.CachePath = ""; _store.SaveSettings(_settings);
        CacheStatus.Text = "无缓存 · 点击定位进行匹配，或生成缓存启用自动跟踪";
        _tracking.Reset(null); ClearChapterDetails();
        if (_book is not null) foreach (var chapter in _book.Chapters) chapter.IsCurrent = false;
        CacheStatus.ToolTip = null; UpdateAvailability();
    }
    private void ClearChapterDetails()
    {
        _selected = null; ChapterTitle.Text = ChapterProgress.Text = ""; ChapterBody.Clear();
        SegmentsGrid.ItemsSource = null; _segmentEdits.Clear();
    }
    private async Task BuildMapAsync(bool persist, CancellationToken token)
    {
        if (_book is null) throw new InvalidOperationException("请先选择小说。");
        var snapshot = _host!.GetSnapshot();
        if (snapshot.PlaylistId != _playlistId) throw new OperationCanceledException("播放列表已改变。");
        var progress = new Progress<string>(message => { if (!_disposed && !token.IsCancellationRequested) SetStatus(message); });
        var map = await Task.Run(() => AlignmentEngine.Generate(NovelStore.Clone(_book), snapshot, progress, _manualStart, token), token);
        token.ThrowIfCancellationRequested();
        if (_host.GetSnapshot().PlaylistId != _playlistId) throw new OperationCanceledException();
        if (persist)
        {
            string path = await Task.Run(() => _store.SaveCache(map, _settings.CacheDirectory), token);
            token.ThrowIfCancellationRequested(); Binding.CachePath = path; _store.SaveSettings(_settings);
        }
        string? selectedId = _selected?.Id;
        _map = map; _lookup = new(map); _hasCache = persist; _book = map.Book; RebuildTree(); UpdateAvailability(); ResetTracking();
        if (_book.Chapters.FirstOrDefault(c => c.Id == selectedId) is { } selected) ShowChapter(selected);
        CacheStatus.Text = persist ? $"缓存已生成 · {map.Segments.Count} 个片段 · 自动跟踪已启用" : "本次为临时定位，不自动跟踪；生成缓存后启用跟踪";
        SetStatus($"已匹配 {map.Segments.Count} 个片段；{map.Gaps.Count} 个可能缺失或未知区间。" + string.Join("；", map.Warnings.Take(3)));
        CacheStatus.ToolTip = string.Join("\n", map.Gaps.Select(g => $"{g.Status}：{g.Evidence}").Concat(map.Warnings));
    }
    private async Task LocateAsync(CancellationToken token)
    {
        if (_map is null) await BuildMapAsync(false, token);
        LocateNow(true); ResetTracking();
    }
    private void LocateNow(bool report)
    {
        var snapshot = _host!.GetSnapshot(); var segment = _lookup?.Locate(snapshot.CurrentFilePath, snapshot.PositionSeconds);
        if (segment is null)
        {
            if (report) SetStatus(snapshot.CurrentTrackId is null ? "当前没有正在播放的音频。" : "当前进度处于未匹配区间（可能是片头、广告、音乐或识别不足），未强行映射。");
            return;
        }
        var chapter = _book?.Chapters.FirstOrDefault(c => c.Id == segment.ChapterId);
        if (chapter is null) return;
        if (segment.Missing)
        {
            var nearest = NearestAvailable(chapter.Order);
            if (nearest is not null) Highlight(nearest);
            SetStatus("当前片段的音频已缺失。" + (nearest is null ? "没有可用章节。" : "已定位到最近可用章节：" + nearest.Title));
            return;
        }
        Highlight(chapter); UpdateProgress(snapshot);
        if (report) SetStatus("已定位：" + chapter.Title + " · " + segment.Evidence);
    }
    private void ResetTracking()
    {
        var snapshot = _host!.GetSnapshot(); _tracking.Reset(_lookup?.Locate(snapshot.CurrentFilePath, snapshot.PositionSeconds)?.ChapterId);
    }
    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _host is null) return;
        var snapshot = _host.GetSnapshot();
        if (snapshot.PlaylistId != _playlistId)
        {
            _work?.Cancel(); if (!_busy) await RunAsync(LoadBoundAsync); return;
        }
        if (_busy) return;
        var current = _lookup?.Locate(snapshot.CurrentFilePath, snapshot.PositionSeconds);
        if (_tracking.Update(_hasCache, current?.ChapterId)) LocateNow(false);
        UpdateProgress(snapshot);
        if (_map is not null && !_refreshingAvailability && DateTime.UtcNow >= _nextAvailability)
        {
            _refreshingAvailability = true; _nextAvailability = DateTime.UtcNow.AddSeconds(4); var map = _map;
            try
            {
                bool mismatch = await Task.Run(() => AlignmentEngine.Reconcile(map, snapshot));
                if (!_disposed && ReferenceEquals(map, _map))
                {
                    UpdateAvailability();
                    if (mismatch && _hasCache) CacheStatus.Text = "播放列表或文件有变动 · 缓存仍可用，缺失已标记；可手动重新生成";
                }
            }
            catch (Exception ex) { if (!_disposed) SetStatus("检查音频状态失败：" + ex.Message); }
            finally { _refreshingAvailability = false; }
        }
    }
    private void UpdateProgress(PlaybackSnapshot snapshot)
    {
        var segment = _lookup?.Locate(snapshot.CurrentFilePath, snapshot.PositionSeconds);
        if (_selected is not null && segment?.ChapterId == _selected.Id)
            ChapterProgress.Text = $"当前片段 {TimeSpan.FromSeconds(Math.Max(0, snapshot.PositionSeconds - segment.Start)):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(segment.End - segment.Start):hh\\:mm\\:ss} · 音频 {TimeSpan.FromSeconds(snapshot.PositionSeconds):hh\\:mm\\:ss}";
    }
    private void RebuildTree()
    {
        if (_book is null) { ChapterTree.ItemsSource = null; return; }
        NovelParser.Group(_book, _settings.GroupByVolume, _settings.GroupSize);
        _groups = _book.Chapters.GroupBy(c => c.Group).Select(g => new ChapterGroup(g.Key, g.ToArray())).ToList();
        ChapterTree.ItemsSource = _groups;
    }
    private void UpdateAvailability()
    {
        if (_book is null) return;
        var segments = _map?.Segments.ToLookup(s => s.ChapterId);
        foreach (var chapter in _book.Chapters)
        {
            var mapped = segments?[chapter.Id].ToArray() ?? Array.Empty<ChapterSegment>();
            string status = _map is null ? "未定位" : mapped.Length == 0 ? (_map.Gaps.Any(g => g.FirstOrder >= 0 && chapter.Order >= g.FirstOrder && chapter.Order <= g.LastOrder) ? "可能缺失（推断）" : "未覆盖 / 未知") : mapped.All(s => s.Missing) ? "音频缺失" : mapped.Any(s => s.Missing) ? "部分音频缺失" : $"{mapped.Length} 个片段";
            if (status != chapter.Availability) { chapter.Availability = status; chapter.Changed(nameof(Chapter.Availability)); }
        }
    }
    private void Highlight(Chapter chapter)
    {
        if (_book is null) return;
        foreach (var c in _book.Chapters.Where(c => c.IsCurrent)) c.IsCurrent = false;
        chapter.IsCurrent = true; ShowChapter(chapter);
        var group = _groups.First(g => g.Chapters.Contains(chapter));
        ChapterTree.UpdateLayout();
        if (ChapterTree.ItemContainerGenerator.ContainerFromItem(group) is TreeViewItem container)
        {
            container.IsExpanded = true; container.UpdateLayout();
            if (container.ItemContainerGenerator.ContainerFromItem(chapter) is TreeViewItem item) { item.IsSelected = true; item.BringIntoView(); }
        }
    }
    private void ShowChapter(Chapter chapter)
    {
        _selected = chapter; ChapterTitle.Text = chapter.Title; ChapterBody.Text = chapter.Body; ChapterProgress.Text = chapter.Volume + " · " + chapter.Availability;
        var segments = _map?.Segments.Where(s => s.ChapterId == chapter.Id).ToArray() ?? Array.Empty<ChapterSegment>();
        SegmentsGrid.ItemsSource = segments; _segmentEdits.Clear(); foreach (var s in segments) _segmentEdits.Add(NovelStore.Clone(s));
    }
    private void Chapter_Selected(object sender, RoutedPropertyChangedEventArgs<object> e) { if (e.NewValue is Chapter chapter) ShowChapter(chapter); }
    private Chapter? NearestAvailable(int order) => _book?.Chapters.Where(c => _map?.Segments.Any(s => s.ChapterId == c.Id && !s.Missing) == true).OrderBy(c => Math.Abs(c.Order - order)).FirstOrDefault();
    private async Task PlaySegmentAsync(ChapterSegment segment, CancellationToken token)
    {
        var snapshot = _host!.GetSnapshot();
        if (snapshot.PlaylistId != _playlistId) throw new InvalidOperationException("播放列表已改变。");
        var track = snapshot.Tracks.FirstOrDefault(t => string.Equals(t.FilePath, segment.AudioPath, StringComparison.OrdinalIgnoreCase));
        if (track is null || segment.Missing) throw new InvalidOperationException("此片段音频缺失或已从播放列表删除。");
        await _host.PlayAsync(_playlistId, track.Id, segment.Start, token); SetStatus("正在播放：" + segment.FileName + " · " + segment.Range);
    }
    private async void PlayChapter_Click(object sender, RoutedEventArgs e)
    {
        _context.Log("info", $"PlayChapter clicked; busy={_busy}; disposed={_disposed}; playlist={_playlistId}; chapter={_selected?.Id}; title={_selected?.Title}");
        await RunAsync(async token =>
        {
            if (_selected is null) throw new InvalidOperationException("请先选择章节。");
            string id = _selected.Id; int order = _selected.Order;
            if (_map is null) await BuildMapAsync(false, token);
            var segment = _map!.Segments.FirstOrDefault(s => s.ChapterId == id && !s.Missing);
            if (segment is not null) await PlaySegmentAsync(segment, token);
            else { var nearest = NearestAvailable(order); if (nearest is not null) Highlight(nearest); SetStatus(nearest is null ? "没有可播放片段。" : "本章音频不可用，已选中最近可用章节；点击播放本章继续。"); }
        });
    }
    private async void PlaySegment_Click(object sender, RoutedEventArgs e) => await RunAsync(token => SegmentsGrid.SelectedItem is ChapterSegment s ? PlaySegmentAsync(s, token) : throw new InvalidOperationException("请选择一个片段。"));
    private async void ChooseBook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择小说文本", Filter = "文本小说|*.txt;*.text;*.md|所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await RunAsync(token => SelectBookAsync(dialog.FileName, token));
    }
    private async void Locate_Click(object sender, RoutedEventArgs e) => await RunAsync(LocateAsync);
    private async void Generate_Click(object sender, RoutedEventArgs e) => await RunAsync(token => BuildMapAsync(true, token));
    private void Cancel_Click(object sender, RoutedEventArgs e) => _work?.Cancel();
    private async void ManualStart_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (_selected is null) throw new InvalidOperationException("请先在章节列表选择起始章节。");
        _manualStart = _selected.Order; await BuildMapAsync(false, token); LocateNow(true); ManualStartButton.IsEnabled = false;
    });
    private async void ClearCache_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        string path = Binding.CachePath; await Task.Run(() => _store.ClearCache(path, _playlistId), token); InvalidateMap(); SetStatus("当前绑定缓存已清除，自动跟踪已停止；小说绑定保留。");
    });
    private async void Rollback_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (string.IsNullOrWhiteSpace(Binding.CachePath)) throw new InvalidOperationException("当前没有绑定缓存。");
        var snapshot = _host!.GetSnapshot();
        var map = await Task.Run(() => _store.LoadCache(Binding.CachePath, _playlistId, true), token);
        await Task.Run(() => { AlignmentEngine.Reconcile(map, snapshot); _store.SaveMap(Binding.CachePath, map); }, token);
        token.ThrowIfCancellationRequested();
        _map = map; _lookup = new(map); _hasCache = true; _book = map.Book; _preview = NovelStore.Clone(_book); Binding.SourcePath = _store.Relative(_book.SourcePath);
        await SavePreviewAsync(token); ClearChapterDetails(); RebuildTree(); RefreshPreview(); UpdateAvailability(); ResetTracking();
        CacheStatus.Text = "已回滚缓存 · 自动跟踪已启用"; CacheStatus.ToolTip = string.Join("\n", map.Gaps.Select(g => g.Evidence).Concat(map.Warnings));
        SetStatus("已回滚上一版缓存；再次回滚可恢复刚才的版本。");
    });
    private void RefreshPreview() { _editingChapter = null; BodyEditor.Clear(); PreviewGrid.ItemsSource = _preview?.Chapters; }
    private void CommitBody() { if (_editingChapter is not null) _editingChapter.Body = BodyEditor.Text; PreviewGrid.CommitEdit(); PreviewGrid.CommitEdit(DataGridEditingUnit.Row, true); }
    private void Preview_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_editingChapter is not null) _editingChapter.Body = BodyEditor.Text;
        _editingChapter = PreviewGrid.SelectedItem as Chapter; BodyEditor.Text = _editingChapter?.Body ?? "";
    }
    private async void ApplyPreview_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        CommitBody(); if (_preview is null) throw new InvalidOperationException("请先选择小说。");
        if (_preview.Chapters.Any(c => string.IsNullOrWhiteSpace(c.Title))) throw new InvalidDataException("章节标题不能为空。");
        var corrected = NovelStore.Clone(_preview); corrected.Chapters = corrected.Chapters.OrderBy(c => c.Order).ToList();
        NovelParser.Group(corrected, _settings.GroupByVolume, _settings.GroupSize);
        Guid playlistId = _playlistId;
        string path = await Task.Run(() => _store.SavePreview(playlistId, corrected), token);
        token.ThrowIfCancellationRequested();
        if (playlistId != _host!.GetSnapshot().PlaylistId) throw new OperationCanceledException();
        _book = corrected; Binding.PreviewPath = path; _manualStart = null; ManualStartButton.IsEnabled = false;
        InvalidateMap(); RebuildTree(); _preview = NovelStore.Clone(_book); RefreshPreview(); SetStatus("章节校对已保存，分组标题同步更新；请重新生成映射。");
    });
    private void UndoPreview_Click(object sender, RoutedEventArgs e) { _preview = _book is null ? null : NovelStore.Clone(_book); RefreshPreview(); SetStatus("已撤销未应用的章节修改。"); }
    private void MovePreview(int delta)
    {
        CommitBody(); if (_preview is null || _editingChapter is null) return;
        int index = _preview.Chapters.IndexOf(_editingChapter), target = index + delta; if (target < 0 || target >= _preview.Chapters.Count) return;
        var chapter = _editingChapter; _preview.Chapters.RemoveAt(index); _preview.Chapters.Insert(target, chapter);
        for (int i = 0; i < _preview.Chapters.Count; i++) _preview.Chapters[i].Order = i;
        RefreshPreview(); PreviewGrid.SelectedItem = chapter;
    }
    private void MoveUp_Click(object sender, RoutedEventArgs e) => MovePreview(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MovePreview(1);
    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        CommitBody(); if (_preview is null || _editingChapter is null) return;
        int index = _preview.Chapters.IndexOf(_editingChapter); if (index + 1 >= _preview.Chapters.Count) return;
        var next = _preview.Chapters[index + 1]; _editingChapter.Body += "\n\n" + next.Title + "\n" + next.Body; _preview.Chapters.Remove(next);
        RefreshPreview();
    }
    private void Split_Click(object sender, RoutedEventArgs e)
    {
        int split = BodyEditor.CaretIndex; CommitBody(); if (_preview is null || _editingChapter is null) return;
        if (split <= 0 || split >= _editingChapter.Body.Length) { SetStatus("请把正文光标放在要拆分的位置（不能位于开头或结尾）。"); return; }
        int index = _preview.Chapters.IndexOf(_editingChapter);
        var next = new Chapter { Title = _editingChapter.Title + "（拆分）", Volume = _editingChapter.Volume, VolumeNumber = _editingChapter.VolumeNumber, Body = _editingChapter.Body[split..], SourceLine = _editingChapter.SourceLine };
        _editingChapter.Body = _editingChapter.Body[..split]; _preview.Chapters.Insert(index + 1, next);
        for (int i = 0; i < _preview.Chapters.Count; i++) _preview.Chapters[i].Order = i;
        RefreshPreview(); PreviewGrid.SelectedItem = next;
    }
    private void RenameVolume_Click(object sender, RoutedEventArgs e)
    {
        CommitBody(); if (_editingChapter is null || _preview is null) return;
        string old = _editingChapter.Volume; var input = new TextBox { Text = old, Margin = new Thickness(16) };
        var save = new Button { Content = "应用卷名", IsDefault = true, Margin = new Thickness(16) };
        var panel = new StackPanel(); panel.Children.Add(input); panel.Children.Add(save);
        var dialog = new Window { Title = "修改分卷标题原文", Owner = Window.GetWindow(this), Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        save.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() == true) { foreach (var c in _preview.Chapters.Where(c => c.Volume == old)) c.Volume = input.Text.Trim(); PreviewGrid.Items.Refresh(); }
    }
    private void AddSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _host is null) { SetStatus("请先选择章节。"); return; }
        var snapshot = _host.GetSnapshot();
        if (snapshot.CurrentTrackId is not Guid id || snapshot.CurrentFilePath is null) { SetStatus("请先播放要关联的音频。"); return; }
        if (!snapshot.Tracks.Any(t => t.Id == id)) { SetStatus("当前音频不属于此播放列表，请先播放列表内的音频。"); return; }
        if (snapshot.DurationSeconds <= snapshot.PositionSeconds) { SetStatus("请等待音频加载，或把播放进度移到音频结束之前。"); return; }
        _segmentEdits.Add(new() { ChapterId = _selected.Id, TrackId = id, AudioPath = snapshot.CurrentFilePath, Start = snapshot.PositionSeconds, End = snapshot.DurationSeconds, Confidence = 1, Evidence = "用户手动校对" });
    }
    private void DeleteSegment_Click(object sender, RoutedEventArgs e) { if (EditSegmentsGrid.SelectedItem is ChapterSegment s) _segmentEdits.Remove(s); }
    private async void SaveSegments_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        EditSegmentsGrid.CommitEdit(); EditSegmentsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_selected is null || _book is null) throw new InvalidOperationException("请先选择章节。");
        var snapshot = _host!.GetSnapshot();
        var map = _map is null ? new NovelMap { Book = NovelStore.Clone(_book), PlaylistId = _playlistId, PlaylistName = snapshot.PlaylistName, Audio = snapshot.Tracks.Select((t, i) => new AudioEntry { TrackId = t.Id, Path = t.FilePath, Order = i }).ToList() } : NovelStore.Clone(_map);
        map.Segments.RemoveAll(s => s.ChapterId == _selected.Id);
        foreach (var segment in _segmentEdits)
        {
            var copy = NovelStore.Clone(segment); copy.Confidence = 1; copy.Evidence = "用户手动校对";
            if (map.Audio.All(a => a.TrackId != copy.TrackId)) map.Audio.Add(new() { TrackId = copy.TrackId, Path = copy.AudioPath, Order = map.Audio.Count });
            map.Segments.Add(copy);
        }
        map.Segments = map.Segments.OrderBy(s => map.Audio.FindIndex(a => a.TrackId == s.TrackId)).ThenBy(s => s.Start).ToList(); map.CreatedUtc = DateTime.UtcNow;
        string path = await Task.Run(() => { AlignmentEngine.Reconcile(map, snapshot); return _store.SaveCache(map, _settings.CacheDirectory); }, token);
        token.ThrowIfCancellationRequested(); Binding.CachePath = path; _store.SaveSettings(_settings); _map = map; _lookup = new(map); _hasCache = true; UpdateAvailability(); ResetTracking();
        ShowChapter(_selected); CacheStatus.Text = "校对缓存已保存 · 自动跟踪已启用"; SetStatus("片段校对已保存，旧版缓存可回滚。");
    });
    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await RunAsync(_ => { ReadSettings(); RebuildTree(); SetStatus("显示与解析设置已保存；点击重新解析以应用章节规则。"); return Task.CompletedTask; });
    private async void Reparse_Click(object sender, RoutedEventArgs e) => await RunAsync(async token => { ReadSettings(); if (_book is null) throw new InvalidOperationException("请先选择小说。"); await SelectBookAsync(_book.SourcePath, token); });
    private void CacheFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择本地缓存存储目录" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) CacheFolderBox.Text = dialog.FolderName;
    }
    public async Task StopAsync() { _timer.Stop(); _work?.Cancel(); if (_operation is not null) try { await _operation; } catch (OperationCanceledException) { } catch (Exception) { } }
    public void Dispose() { _disposed = true; _timer.Stop(); _work?.Cancel(); }
}
