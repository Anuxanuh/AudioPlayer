using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using AudioPlayer.Plugin.Abstractions;

namespace AudioPlayer.Plugin.Bilibili;

public sealed class BilibiliPlugin : IPlayerPlugin
{
    private BilibiliPage? _page;
    public FrameworkElement CreatePage(PluginContext context) => _page = new BilibiliPage(context);
    public Task StopAsync() => _page?.StopAsync() ?? Task.CompletedTask;
    public void Dispose() => _page?.Dispose();
}

public sealed class Episode : INotifyPropertyChanged
{
    public int Index { get; init; }
    public string Title { get; init; } = "";
    public string Label => $"P{Index:00}  ·  {Title}";
    public string Duration { get; init; } = "";
    private bool _selected = true;
    public bool Selected { get => _selected; set { _selected = value; Changed(); } }
    private string _status = "待下载";
    public string Status { get => _status; set { _status = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed record Quality(int Id, string Label);

public sealed class BilibiliPage : UserControl, IDisposable
{
    private readonly PluginContext _context;
    private readonly SessionStore _session;
    private readonly WorkerClient _worker;
    private readonly ObservableCollection<Episode> _episodes = new();
    private readonly TextBox _bv = new() { MinWidth = 210, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _output = new() { MinWidth = 150, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _quality = new() { MinWidth = 170, DisplayMemberPath = "Label", VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _video = new() { Content = "下载视频并合并音频", VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _subtitles = new() { Content = "同时下载字幕", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _account = new() { Text = "未登录 · 可下载游客可用的内容", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _title = new() { Text = "输入 BV 号，读取视频与分 P", FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 44, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { Text = "默认只下载音频。勾选视频后会下载音视频并用 FFmpeg 合并。", TextWrapping = TextWrapping.Wrap, MaxHeight = 58, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 5, Margin = new Thickness(0, 10, 0, 10) };
    private readonly ListBox _list = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 75 };
    private readonly Button _cancel;
    private readonly List<UIElement> _editing = new();
    private CancellationTokenSource? _operation;
    private Task? _task;
    private LoginWindow? _login;
    private string? _parsedBv;
    private Episode? _selectionAnchor;
    private bool _anchorChecked;
    private bool _disposed;

    public BilibiliPage(PluginContext context)
    {
        _context = context; _session = new(context.DataDirectory); _worker = new(context);
        _status.SetBinding(ToolTipProperty, new Binding("Text") { Source = _status });
        _output.Text = Path.Combine(context.ApplicationDirectory, "Downloads", "Bilibili");
        if (File.Exists(_session.FilePath)) _account.Text = "已保存登录状态 · 下载时校验权限，失效时请重新登录";
        var layout = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) layout.RowDefinitions.Add(new RowDefinition { Height = height });
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        top.Children.Add(new TextBlock { Text = "Bilibili 下载", FontSize = 26, FontWeight = FontWeights.SemiBold });
        var loginRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
        loginRow.Children.Add(Action("扫码登录 Bilibili…", Login));
        loginRow.Children.Add(Action("退出登录", Logout));
        loginRow.Children.Add(_account); top.Children.Add(loginRow);
        var inputRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var inspect = Action("读取视频", Inspect); DockPanel.SetDock(inspect, Dock.Right); inputRow.Children.Add(inspect);
        _bv.ToolTip = "BV 号或 Bilibili 视频链接"; inputRow.Children.Add(_bv); top.Children.Add(inputRow); layout.Children.Add(top);
        var settings = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; Grid.SetRow(settings, 1);
        var options = new WrapPanel(); _video.Margin = _subtitles.Margin = new Thickness(0, 8, 20, 8);
        options.Children.Add(_video); options.Children.Add(_subtitles); settings.Children.Add(options);
        var qualityRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        qualityRow.Children.Add(new TextBlock { Text = "视频清晰度", Margin = new Thickness(0, 8, 10, 0) }); qualityRow.Children.Add(_quality);
        qualityRow.Children.Add(Action("查询当前分 P 清晰度", RefreshQuality)); settings.Children.Add(qualityRow);
        settings.Children.Add(new TextBlock { Text = "清晰度来自当前账号可获取的流；各分 P 可用清晰度可能不同。音频下载不受此选项影响。", Foreground = Brushes.DimGray, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var outputRow = new DockPanel { Margin = new Thickness(0, 10, 0, 8) }; var browse = Action("保存位置…", Browse); DockPanel.SetDock(browse, Dock.Right); outputRow.Children.Add(browse); outputRow.Children.Add(_output); settings.Children.Add(outputRow);
        var selection = new DockPanel(); var choices = new StackPanel { Orientation = Orientation.Horizontal };
        choices.Children.Add(Action("全选", () => SetChecks(_ => true)));
        choices.Children.Add(Action("全不选", () => SetChecks(_ => false)));
        choices.Children.Add(Action("反选", () => SetChecks(episode => !episode.Selected)));
        DockPanel.SetDock(choices, Dock.Right); selection.Children.Add(choices); selection.Children.Add(_title); settings.Children.Add(selection); layout.Children.Add(settings);
        _list.ItemsSource = _episodes;
        _list.ToolTip = "Ctrl + 单击：切换单集勾选；Shift + 单击：按起点状态勾选或取消连续范围；空格：切换当前集。";
        _list.PreviewMouseLeftButtonDown += EpisodeMouseDown;
        _list.PreviewKeyDown += EpisodeKeyDown;
        _list.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler(EpisodeCheckClick));
        _list.ItemTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid Margin="4,7"><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="70"/><ColumnDefinition Width="190"/></Grid.ColumnDefinitions>
                <CheckBox Content="{Binding Label}" IsChecked="{Binding Selected, Mode=TwoWay}" VerticalAlignment="Center"><CheckBox.ContentTemplate><DataTemplate><TextBlock Text="{Binding}" TextWrapping="Wrap"/></DataTemplate></CheckBox.ContentTemplate></CheckBox>
                <TextBlock Grid.Column="1" Text="{Binding Duration}" Foreground="#586D69" VerticalAlignment="Center" Margin="6,0"/>
                <TextBlock Grid.Column="2" Text="{Binding Status}" Foreground="#176E61" TextWrapping="Wrap" VerticalAlignment="Center"/>
              </Grid>
            </DataTemplate>
            """);
        Grid.SetRow(_list, 2); layout.Children.Add(_list);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(footer, 3);
        var buttons = new WrapPanel(); var start = Action("开始下载", Download); start.Background = new SolidColorBrush(Color.FromRgb(23, 110, 97)); start.Foreground = Brushes.White; buttons.Children.Add(start);
        _cancel = new Button { Content = "取消下载", Padding = new Thickness(14, 8, 14, 8), IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) }; _cancel.Click += (_, _) => _operation?.Cancel(); buttons.Children.Add(_cancel);
        buttons.Children.Add(Action("打开保存目录", OpenDirectory)); footer.Children.Add(buttons); footer.Children.Add(_progress); footer.Children.Add(_status); layout.Children.Add(footer);
        _editing.AddRange([_bv, _output, _quality, _video, _subtitles, _list]);
        Content = layout;
    }
    private Button Action(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { _status.Text = ex.Message; } }; _editing.Add(button); return button;
    }
    private void SetChecks(Func<Episode, bool> selected)
    {
        foreach (var episode in _episodes) episode.Selected = selected(episode);
        _selectionAnchor = null;
    }
    private void ApplyEpisodeSelection(Episode episode, ModifierKeys modifiers, bool toggle = true)
    {
        if (!_list.IsEnabled || !_episodes.Contains(episode)) return;
        int anchor = _selectionAnchor is null ? -1 : _episodes.IndexOf(_selectionAnchor);
        int current = _episodes.IndexOf(episode);
        if ((modifiers & ModifierKeys.Shift) != 0 && anchor >= 0)
        {
            for (int i = Math.Min(anchor, current); i <= Math.Max(anchor, current); i++)
                _episodes[i].Selected = _anchorChecked;
        }
        else
        {
            if (toggle) episode.Selected = !episode.Selected;
            _selectionAnchor = episode;
            _anchorChecked = episode.Selected;
        }
        _list.SelectedItem = episode;
    }
    private void EpisodeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(_list, source) is not ListBoxItem { Content: Episode episode } row) return;
        bool onCheckBox = false;
        for (DependencyObject? node = source; node is not null && node != row; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is CheckBox) { onCheckBox = true; break; }
        var modifiers = Keyboard.Modifiers;
        if (onCheckBox || (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
        {
            // Handle before CheckBox/ListBox so a range click never toggles its endpoint twice.
            e.Handled = true;
            ApplyEpisodeSelection(episode, modifiers);
            row.Focus();
        }
        else
        {
            // A plain row click selects the episode for the quality query without changing its check.
            _selectionAnchor = episode;
            _anchorChecked = episode.Selected;
        }
    }
    private void EpisodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || _list.SelectedItem is not Episode episode) return;
        e.Handled = true;
        if (!e.IsRepeat) ApplyEpisodeSelection(episode, Keyboard.Modifiers);
    }
    private void EpisodeCheckClick(object sender, RoutedEventArgs e)
    {
        // Keyboard/UI Automation activation has already updated the two-way IsChecked binding.
        if (e.OriginalSource is CheckBox { DataContext: Episode episode })
            ApplyEpisodeSelection(episode, Keyboard.Modifiers, toggle: false);
    }
    private void Start(Func<CancellationToken, Task> operation)
    {
        if (_operation is not null || _disposed) return;
        _task = RunAsync(operation);
    }
    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.ShutdownToken);
        _operation = cancellation; foreach (var control in _editing) control.IsEnabled = false; _cancel.IsEnabled = true; _progress.Value = 0;
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { _status.Text = "已取消，已完成文件保留；重新下载时可继续未完成的下载片段。"; _context.Log("info", "操作已取消"); }
        catch (Exception ex) { _status.Text = ex.Message; _context.Log("error", ex.Message); }
        finally { _operation = null; _progress.IsIndeterminate = false; foreach (var control in _editing) control.IsEnabled = true; _cancel.IsEnabled = false; }
    }
    private IReadOnlyList<SessionCookie> Cookies()
    {
        try { return _session.Load(); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or JsonException)
        { throw new InvalidOperationException("登录凭据无法读取；移动到其他电脑后请重新登录。", ex); }
    }
    private void Login()
    {
        _login = new LoginWindow(_context, _session) { Owner = Window.GetWindow(this) };
        _login.ShowDialog();
        if (_login.LoggedIn) { _account.Text = "已登录 · " + _login.AccountName; _quality.ItemsSource = null; _status.Text = "登录成功，请重新读取视频以更新清晰度和字幕权限。"; _context.Log("info", "用户完成 Bilibili 登录"); }
        _login = null;
    }
    private void Logout()
    {
        _session.Clear();
        _account.Text = "已退出登录"; _quality.ItemsSource = null;
        _status.Text = "已清除本插件保存的登录凭据。";
        _context.Log("info", "用户退出 Bilibili 登录");
    }
    private void Inspect() => Start(async token =>
    {
        _selectionAnchor = null; _episodes.Clear(); _parsedBv = null; _quality.ItemsSource = null; _progress.IsIndeterminate = true; _status.Text = "正在读取视频与分 P…";
        await _worker.RunAsync(new { action = "inspect", bv = _bv.Text.Trim(), cookies = Cookies() }, OnEvent, token);
        _status.Text = "分 P 已默认全选。选择保存内容后开始下载。";
    });
    private void RefreshQuality() => Start(async token =>
    {
        if (_parsedBv is null) throw new InvalidOperationException("请先读取视频。");
        var page = (_list.SelectedItem as Episode) ?? _episodes.FirstOrDefault(e => e.Selected) ?? _episodes.First();
        _progress.IsIndeterminate = true;
        await _worker.RunAsync(new { action = "formats", bv = _parsedBv, page = page.Index, cookies = Cookies() }, OnEvent, token);
        _status.Text = $"已更新 P{page.Index} 的可用清晰度。";
    });
    private void Download() => Start(async token =>
    {
        if (_parsedBv is null) throw new InvalidOperationException("请先读取视频。");
        var pages = _episodes.Where(e => e.Selected).Select(e => new { index = e.Index, title = e.Title }).ToArray();
        if (pages.Length == 0) throw new InvalidOperationException("请至少勾选一个分 P。");
        if (_video.IsChecked == true && _quality.SelectedItem is not Quality) throw new InvalidOperationException("请先查询并选择可用的视频清晰度。");
        if (string.IsNullOrWhiteSpace(_output.Text)) throw new InvalidOperationException("请选择保存文件夹。");
        foreach (var e in _episodes.Where(e => e.Selected)) e.Status = "排队中";
        _context.Log("info", $"下载开始；BV={_parsedBv}；分P数={pages.Length}；视频={_video.IsChecked == true}");
        await _worker.RunAsync(new { action = "download", bv = _parsedBv, pages, output = Path.GetFullPath(_output.Text),
            video = _video.IsChecked == true, subtitles = _subtitles.IsChecked == true, quality = (_quality.SelectedItem as Quality)?.Id ?? 0,
            ffmpeg = WorkerClient.DependencyDirectory(_context, "ffmpeg"), cookies = Cookies() }, OnEvent, token);
    });
    private void OnEvent(JsonElement data)
    {
        string kind = data.GetProperty("type").GetString()!;
        if (kind == "catalog")
        {
            _selectionAnchor = null;
            _parsedBv = data.GetProperty("bv").GetString(); _title.Text = data.GetProperty("title").GetString();
            foreach (var page in data.GetProperty("pages").EnumerateArray())
                _episodes.Add(new Episode { Index = page.GetProperty("index").GetInt32(), Title = page.GetProperty("title").GetString() ?? "", Duration = TimeSpan.FromSeconds(page.GetProperty("duration").GetDouble()).ToString(@"hh\:mm\:ss") });
            _list.SelectedIndex = 0;
        }
        else if (kind == "formats")
        {
            var old = _quality.SelectedItem as Quality;
            var qualities = data.GetProperty("qualities").EnumerateArray().Select(q => new Quality(q.GetProperty("id").GetInt32(), q.GetProperty("label").GetString() ?? "")).ToArray();
            _quality.ItemsSource = qualities; _quality.SelectedItem = qualities.FirstOrDefault(q => q.Id == old?.Id) ?? qualities.FirstOrDefault();
        }
        else if (kind is "item" or "progress")
        {
            var episode = _episodes.FirstOrDefault(e => e.Index == data.GetProperty("index").GetInt32());
            if (episode is not null) episode.Status = data.GetProperty("message").GetString() ?? "";
            if (kind == "progress") _progress.Value = data.GetProperty("percent").GetDouble();
            else _context.Log(data.GetProperty("state").GetString() == "failed" ? "error" : "info", $"P{episode?.Index}：{episode?.Status}");
            _status.Text = $"P{episode?.Index}：{episode?.Status}";
        }
        else if (kind == "complete")
        {
            _progress.Value = 100;
            _status.Text = $"完成：成功 {data.GetProperty("completed")}，跳过 {data.GetProperty("skipped")}，失败 {data.GetProperty("failed")}。";
            _context.Log("info", _status.Text);
        }
        else if (kind is "warning" or "error") _status.Text = data.GetProperty("message").GetString();
    }
    private void Browse() { var dialog = new OpenFolderDialog { Title = "选择下载保存文件夹" }; if (dialog.ShowDialog(Window.GetWindow(this)) == true) _output.Text = dialog.FolderName; }
    private void OpenDirectory() { string directory = Path.GetFullPath(_output.Text); Directory.CreateDirectory(directory); Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
    public async Task StopAsync() { _operation?.Cancel(); if (_login is not null) await _login.StopAsync(); if (_task is not null) await _task; }
    public void Dispose() { _disposed = true; _operation?.Cancel(); _login?.Close(); }
}
