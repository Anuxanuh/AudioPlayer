using System.Runtime.InteropServices;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPlayer.Models;
using AudioPlayer.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AudioPlayer.Views;

public partial class LyricsWindow : Window
{
    private readonly PlayerSettings _settings;
    private readonly DispatcherTimer _visibilityTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private HwndSource? _source;
    private bool _closed, _recovering, _dragging, _recoveryQueued;
    private bool _placing;
    private bool _vertical;
    private string _text = "声屿 · 桌面歌词";
    public event Action? PositionSaved;
    public LyricsWindow(PlayerSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        ApplySettings();
        _visibilityTimer.Tick += (_, _) => EnsureVisible();
        Loaded += (_, _) => { _visibilityTimer.Start(); EnsureVisible(); };
        Closed += (_, _) =>
        {
            _closed = true; _visibilityTimer.Stop();
            Log.Information("Desktop lyrics window closed; enabled={Enabled}", _settings.DesktopLyrics);
            if (_source is not null)
            {
                _source.RemoveHook(WindowMessage);
                SystemEvents.DisplaySettingsChanged -= DisplayChanged;
                SystemEvents.PowerModeChanged -= PowerChanged;
                SystemEvents.SessionSwitch -= SessionChanged;
            }
        };
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
        foreach (string paragraph in _text.Replace("\r", "").Split('\n'))
        {
            var glyphs = new List<string>();
            var enumerator = StringInfo.GetTextElementEnumerator(paragraph);
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
    }
    private IReadOnlyList<Rect> WorkAreas()
    {
        var transform = _source?.CompositionTarget?.TransformFromDevice;
        if (transform is null) return new[] { SystemParameters.WorkArea };
        return Forms.Screen.AllScreens.Select(s =>
        {
            var a = s.WorkingArea;
            return Rect.Transform(new Rect(a.Left, a.Top, a.Width, a.Height), transform.Value);
        }).ToArray();
    }
    private Rect CurrentWorkArea
    {
        get
        {
            var areas = WorkAreas();
            double left = _settings.LyricLeft is double x && double.IsFinite(x) ? x : SystemParameters.WorkArea.Left;
            double top = _settings.LyricTop is double y && double.IsFinite(y) ? y : SystemParameters.WorkArea.Top;
            var position = DesktopLyricsPlacement.Constrain(new Rect(left, top, Math.Max(1, Width), Math.Max(1, Height)), areas);
            return areas.FirstOrDefault(a => a.Contains(position), SystemParameters.WorkArea);
        }
    }
    private double VerticalHeight => Math.Min(640, Math.Max(160, CurrentWorkArea.Height - 60));
    public void Place(bool reset = false)
    {
        if (_placing || _closed) return;
        _placing = true;
        try
        {
            Rect area = reset ? SystemParameters.WorkArea : CurrentWorkArea;
            Height = _vertical ? Math.Min(640, Math.Max(160, area.Height - 60)) : Math.Min(180, area.Height);
            Width = _vertical ? Math.Min(Math.Max(150, VerticalColumns.Children.Count * (_settings.FontSize * 1.25 + 12) + 36), area.Width) : Math.Min(920, area.Width);
            double defaultLeft = _vertical ? area.Right - Width - 30 : area.Left + (area.Width - Width) / 2;
            double defaultTop = _vertical ? area.Top + (area.Height - Height) / 2 : area.Bottom - Height - 30;
            double left = !reset && _settings.LyricLeft is double x && double.IsFinite(x) ? x : defaultLeft;
            double top = !reset && _settings.LyricTop is double y && double.IsFinite(y) ? y : defaultTop;
            var bounds = DesktopLyricsPlacement.Constrain(new Rect(left, top, Width, Height), reset ? new[] { area } : WorkAreas());
            Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
            LyricText.Width = Math.Max(1, Width - 36);
        }
        finally { _placing = false; }
        if (_source is not null) SavePosition();
    }
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_settings.LockLyrics && e.ButtonState == MouseButtonState.Pressed)
        {
            _dragging = true;
            try { DragMove(); }
            finally { _dragging = false; Place(); }
        }
    }
    private void Window_LocationChanged(object? sender, EventArgs e) { if (!_placing && IsLoaded) SavePosition(); }
    private void SavePosition() { _settings.LyricLeft = Left; _settings.LyricTop = Top; PositionSaved?.Invoke(); }
    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (_source is not null)
        {
            // This small transparent overlay uses software rendering to avoid driver/RDP redraw loss.
            _source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            _source.AddHook(WindowMessage);
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionChanged;
        }
        Place(); ApplyMouseMode();
    }

    public void EnsureVisible(bool refreshDisplay = false)
    {
        if (_closed || _recovering || _dragging || !_settings.DesktopLyrics) return;
        _recovering = true;
        try
        {
            bool recovered = !IsVisible || WindowState != WindowState.Normal;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            if (!IsVisible) Show();
            nint handle = new WindowInteropHelper(this).Handle;
            if (handle == 0) return;
            recovered |= !IsWindowVisible(handle) || IsIconic(handle) || (GetWindowLongPtr(handle, -20).ToInt64() & 0x8) == 0;
            if (IsIconic(handle)) ShowWindow(handle, 4); // SW_SHOWNOACTIVATE
            if (refreshDisplay) { BuildVerticalText(); Place(); }
            recovered |= EnsureNativeBounds(handle);
            ApplyMouseMode();
            Topmost = true;
            // WPF's IsVisible/Topmost can stay true after native shell operations hide/reorder an HWND.
            // Restore the HWND without activating it or changing keyboard focus.
            SetWindowPos(handle, new nint(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040 | 0x0200);
            if (recovered || refreshDisplay)
            {
                LyricText.InvalidateVisual(); VerticalColumns.InvalidateVisual(); InvalidateVisual();
                LogRecovery(refreshDisplay ? "display/session refresh" : "hidden/minimized/off-screen/native topmost recovery");
            }
        }
        finally { _recovering = false; }
    }
    private bool EnsureNativeBounds(nint handle)
    {
        if (!GetWindowRect(handle, out var native) || native.Right <= native.Left || native.Bottom <= native.Top) return false;
        var desired = new Rect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
        // Compare HWND and monitor rectangles in the same native coordinate space, also on mixed-DPI screens.
        var areas = Forms.Screen.AllScreens.Select(s => new Rect(s.WorkingArea.Left, s.WorkingArea.Top, s.WorkingArea.Width, s.WorkingArea.Height)).ToArray();
        var bounds = DesktopLyricsPlacement.Constrain(desired, areas);
        if (bounds == desired) return false;
        SetWindowPos(handle, 0, (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, 0x0004 | 0x0010 | 0x0200);
        SavePosition();
        return true;
    }
    private void QueueRecovery()
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_closed || _recoveryQueued) return;
            _recoveryQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            { _recoveryQueued = false; EnsureVisible(refreshDisplay: true); }));
        }));
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x007E or 0x001A or 0x02E0) QueueRecovery(); // display/work area/DPI
        return 0;
    }
    private void DisplayChanged(object? sender, EventArgs e) => QueueRecovery();
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) QueueRecovery(); }
    private void SessionChanged(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect) QueueRecovery();
    }
    private void LogRecovery(string reason)
    {
        Log.Information("Desktop lyrics recovered; reason={Reason}; bounds={Left},{Top},{Width},{Height}; dpi={Dpi}; monitors={Monitors}; locked={Locked}; nativeVisible={Visible}; software=true",
            reason, Left, Top, Width, Height, VisualTreeHelper.GetDpi(this).PixelsPerInchX,
            Forms.Screen.AllScreens.Select(s => s.WorkingArea.ToString()).ToArray(), _settings.LockLyrics,
            IsWindowVisible(new WindowInteropHelper(this).Handle));
    }
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
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
