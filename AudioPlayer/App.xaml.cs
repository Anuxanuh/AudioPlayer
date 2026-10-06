using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using AudioPlayer.Services;

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
    public App(bool startMainWindow)
    {
        _startMainWindow = startMainWindow;
        if (!startMainWindow) return;
        try { AppLogging.Initialize(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show("无法在程序目录创建 logs 日志文件夹：" + ex.Message, "声屿 · 日志"); }
        DispatcherUnhandledException += UiException;
        AppDomain.CurrentDomain.UnhandledException += DomainException;
        TaskScheduler.UnobservedTaskException += TaskException;
        AppDomain.CurrentDomain.ProcessExit += ProcessExit;
    }
    private static void UiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled UI exception");
        AppLogging.Close(); // Preserve diagnostics without continuing in a corrupted UI state.
    }
    private static void DomainException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled process exception; terminating={Terminating}", e.IsTerminating);
        AppLogging.Close();
    }
    private static void TaskException(object? sender, UnobservedTaskExceptionEventArgs e) => Log.Error(e.Exception, "Unobserved background task exception");
    private static void ProcessExit(object? sender, EventArgs e) => AppLogging.Close();
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!_startMainWindow) return;
        if (e.Args.FirstOrDefault() == "--portable-check")
        {
            int pythonIndex = Array.IndexOf(e.Args, "--python-path");
            string? pythonOverride = pythonIndex >= 0 && pythonIndex + 1 < e.Args.Length ? e.Args[pythonIndex + 1] : null;
            string? sample = e.Args.Length > 1 && !e.Args[1].StartsWith("--", StringComparison.Ordinal) ? e.Args[1] : null;
            Shutdown(await Services.PortableVerification.RunAsync(sample, pythonOverride));
            return;
        }
        _instance = new Services.SingleInstanceService();
        if (!_instance.IsPrimary)
        {
            bool activated = await _instance.ActivateExistingAsync(e.Args);
            Log.Information("Secondary instance activation; success={Activated}", activated);
            if (!activated) MessageBox.Show("播放器已在运行，但窗口暂时无法唤回。请稍后重试或从托盘打开。", "声屿");
            Shutdown(activated ? 0 : 1);
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        _instance.StartListening(args => Dispatcher.InvokeAsync(() => window.ActivateFromLaunch(args)).Task.Unwrap());
        window.Show();
        window.NotifyPythonEnvironment();
        if (e.Args.Length > 0) window.ImportPaths(e.Args);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        if (_startMainWindow)
        {
            Log.Information("Application exiting; exitCode={ExitCode}", e.ApplicationExitCode);
            DispatcherUnhandledException -= UiException;
            AppDomain.CurrentDomain.UnhandledException -= DomainException;
            TaskScheduler.UnobservedTaskException -= TaskException;
            AppDomain.CurrentDomain.ProcessExit -= ProcessExit;
            AppLogging.Close();
        }
        base.OnExit(e);
    }
}

