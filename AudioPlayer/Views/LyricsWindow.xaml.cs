using System.Runtime.InteropServices;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AudioPlayer.Models;

namespace AudioPlayer.Views;

public partial class LyricsWindow : Window
{
    private readonly PlayerSettings _settings;
    private bool _placing;
    private bool _vertical;
    private string _text = "声屿 · 桌面歌词";
    public event Action? PositionSaved;
    public LyricsWindow(PlayerSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        ApplySettings();
    }
    public void SetText(string text)
    {
        if (_text == text) return;
        _text = text;
        LyricText.Text = text;
        if (_vertical) { BuildVerticalText(); Place(); }
    }
    public void ApplySettings()
    {
        LyricText.FontSize = _settings.FontSize;
        LyricText.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(_settings.FontFamily) ? "Microsoft YaHei UI" : _settings.FontFamily);
        try { LyricText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.LyricColor)); }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException) { LyricText.Foreground = Brushes.Gold; }
        Opacity = _settings.LyricOpacity;
        Hint.Visibility = _settings.LockLyrics ? Visibility.Collapsed : Visibility.Visible;
        _vertical = _settings.VerticalLyrics;
        LyricText.Visibility = _vertical ? Visibility.Collapsed : Visibility.Visible;
        VerticalView.Visibility = _vertical ? Visibility.Visible : Visibility.Collapsed;
        Hint.Text = _vertical ? "拖动移动 · 可在托盘锁定" : "拖动移动歌词 · 在设置或托盘菜单中锁定 / 隐藏";
        BuildVerticalText();
        Place();
        ApplyMouseMode();
    }
    private void BuildVerticalText()
    {
        VerticalColumns.Children.Clear();
        if (!_vertical) return;
        double lineHeight = _settings.FontSize * 1.25;
        int perColumn = Math.Max(1, (int)((VerticalHeight - 80) / lineHeight));
        var glyphs = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(_text.Replace("\r", "").Replace("\n", " "));
        while (enumerator.MoveNext()) glyphs.Add(enumerator.GetTextElement());
        foreach (var column in glyphs.Chunk(perColumn))
        {
            VerticalColumns.Children.Add(new TextBlock
            {
                Text = string.Join("\n", column), TextAlignment = TextAlignment.Center, FlowDirection = FlowDirection.LeftToRight,
                FontSize = LyricText.FontSize, FontFamily = LyricText.FontFamily, FontWeight = FontWeights.SemiBold,
                Foreground = LyricText.Foreground, LineHeight = lineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Top
            });
        }
    }
    private double VerticalHeight => Math.Min(640, Math.Max(160, SystemParameters.WorkArea.Height - 60));
    public void Place(bool reset = false)
    {
        _placing = true;
        Height = _vertical ? VerticalHeight : Math.Min(180, SystemParameters.WorkArea.Height);
        Width = _vertical ? Math.Min(Math.Max(150, VerticalColumns.Children.Count * (_settings.FontSize * 1.25 + 12) + 36), SystemParameters.WorkArea.Width) : Math.Min(920, SystemParameters.WorkArea.Width);
        double defaultLeft = _vertical ? SystemParameters.WorkArea.Right - Width - 30 : SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        double defaultTop = _vertical ? SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2 : SystemParameters.WorkArea.Bottom - Height - 30;
        double left = !reset && _settings.LyricLeft is double x && double.IsFinite(x) ? x : defaultLeft;
        double top = !reset && _settings.LyricTop is double y && double.IsFinite(y) ? y : defaultTop;
        Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
        Top = Math.Clamp(top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
        _placing = false;
        SavePosition();
    }
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_settings.LockLyrics && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Window_LocationChanged(object? sender, EventArgs e) { if (!_placing && IsLoaded) SavePosition(); }
    private void SavePosition() { _settings.LyricLeft = Left; _settings.LyricTop = Top; PositionSaved?.Invoke(); }
    private void Window_SourceInitialized(object? sender, EventArgs e) => ApplyMouseMode();
    private void ApplyMouseMode()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        const int index = -20;
        const long transparent = 0x20, toolWindow = 0x80, noActivate = 0x08000000;
        long style = GetWindowLongPtr(handle, index).ToInt64() | toolWindow | noActivate;
        style = _settings.LockLyrics ? style | transparent : style & ~transparent;
        SetWindowLongPtr(handle, index, new nint(style));
    }
    private static nint GetWindowLongPtr(nint hwnd, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new nint(GetWindowLong32(hwnd, index));
    private static nint SetWindowLongPtr(nint hwnd, int index, nint value) => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new nint(SetWindowLong32(hwnd, index, value.ToInt32()));
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(nint hwnd, int index, int value);
}
