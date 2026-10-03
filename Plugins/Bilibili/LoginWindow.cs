using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioPlayer.Plugin.Abstractions;

namespace AudioPlayer.Plugin.Bilibili;

/// <summary>Official passport QR protocol; no browser profile or password collection.</summary>
public sealed class LoginWindow : Window
{
    private readonly Image _qr = new() { Width = 292, Height = 292, Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { Text = "正在获取登录二维码…", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button _refresh = new() { Content = "刷新二维码", Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly PluginContext _context;
    private readonly SessionStore _store;
    private CancellationTokenSource? _attempt;
    private Task? _task;
    private bool _closed;
    public bool LoggedIn { get; private set; }
    public string AccountName { get; private set; } = "";
    public LoginWindow(PluginContext context, SessionStore store)
    {
        _context = context; _store = store;
        Title = "Bilibili 扫码登录"; Width = 430; Height = 555; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.White;
        var layout = new StackPanel { Margin = new Thickness(24) };
        layout.Children.Add(new TextBlock { Text = "使用 Bilibili App 扫码", FontSize = 21, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) });
        layout.Children.Add(_qr); layout.Children.Add(_status); layout.Children.Add(_refresh); Content = layout;
        RenderOptions.SetBitmapScalingMode(_qr, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_qr, EdgeMode.Aliased);
        Loaded += async (_, _) => await RefreshAsync();
        _refresh.Click += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _closed = true; _attempt?.Cancel(); };
    }
    private async Task RefreshAsync()
    {
        if (_closed || !_refresh.IsEnabled) return;
        _refresh.IsEnabled = false;
        _attempt?.Cancel();
        if (_task is not null) await _task;
        if (_closed) return;
        _qr.Source = null; _status.Text = "正在获取登录二维码…";
        _task = LoginAsync();
        _refresh.IsEnabled = true;
    }
    private async Task LoginAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.ShutdownToken);
        _attempt = cancellation;
        try
        {
            await new WorkerClient(_context).RunAsync(new { action = "login" }, data =>
            {
                if (_closed || cancellation.IsCancellationRequested) return;
                switch (data.GetProperty("type").GetString())
                {
                    case "qr":
                        _qr.Source = DrawCode(data.GetProperty("matrix"));
                        _status.Text = "请用 Bilibili App 扫码，并在手机上确认登录。";
                        break;
                    case "login_status": _status.Text = data.GetProperty("message").GetString(); break;
                    case "login":
                        var cookies = JsonSerializer.Deserialize<SessionCookie[]>(data.GetProperty("cookies"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                        if (!cookies.Any(c => c.Name == "SESSDATA")) throw new InvalidOperationException("扫码成功但未收到完整登录凭据，请重新扫码。");
                        _store.Save(cookies);
                        AccountName = data.GetProperty("name").GetString() ?? "Bilibili 用户";
                        LoggedIn = true;
                        _status.Text = "登录成功";
                        break;
                }
            }, cancellation.Token);
            if (LoggedIn && !_closed) Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) { _qr.Source = null; _status.Text = ex.Message; } }
        finally { if (ReferenceEquals(_attempt, cancellation)) _attempt = null; }
    }
    public static DrawingImage DrawCode(JsonElement matrix)
    {
        var rows = matrix.EnumerateArray().ToArray();
        if (rows.Length < 21 || rows.Length > 200 || rows.Any(r => r.GetArrayLength() != rows.Length)) throw new InvalidOperationException("二维码数据不完整。");
        var drawing = new DrawingGroup();
        using (var canvas = drawing.Open())
        {
            canvas.DrawRectangle(Brushes.White, null, new Rect(0, 0, rows.Length, rows.Length));
            for (int y = 0; y < rows.Length; y++)
            {
                int x = 0;
                foreach (var cell in rows[y].EnumerateArray()) { if (cell.GetBoolean()) canvas.DrawRectangle(Brushes.Black, null, new Rect(x, y, 1, 1)); x++; }
            }
        }
        drawing.Freeze(); return new DrawingImage(drawing);
    }
    public Task StopAsync() { _attempt?.Cancel(); if (!_closed) Close(); return _task ?? Task.CompletedTask; }
}
