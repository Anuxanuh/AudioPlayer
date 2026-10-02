using System.Configuration;
using System.Data;
using System.Windows;

namespace AudioPlayer;
/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private readonly bool _startMainWindow;
    private Services.SingleInstanceService? _instance;
    public App() : this(true) { }
    // Test hosts load the real application resources but manage their own windows.
    public App(bool startMainWindow) => _startMainWindow = startMainWindow;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!_startMainWindow) return;
        if (e.Args.FirstOrDefault() == "--portable-check")
        {
            Shutdown(await Services.PortableVerification.RunAsync(e.Args.Skip(1).FirstOrDefault()));
            return;
        }
        _instance = new Services.SingleInstanceService();
        if (!_instance.IsPrimary)
        {
            bool activated = await _instance.ActivateExistingAsync(e.Args);
            if (!activated) MessageBox.Show("播放器已在运行，但窗口暂时无法唤回。请稍后重试或从托盘打开。", "声屿");
            Shutdown(activated ? 0 : 1);
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        _instance.StartListening(args => Dispatcher.InvokeAsync(() => window.ActivateFromLaunch(args)).Task.Unwrap());
        window.Show();
        if (e.Args.Length > 0) window.ImportPaths(e.Args);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}

