using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Serilog.Core;

namespace AudioPlayer.Services;

public static class AppLogging
{
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "logs");
    private static Timer? _hourlyTimer;
    private static string _lastHour = "";
    private static readonly Stopwatch Uptime = new();

    public static Logger CreateLogger(string directory)
    {
        Directory.CreateDirectory(directory);
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.File(Path.Combine(directory, "AudioPlayer-.log"),
                rollingInterval: RollingInterval.Hour,
                retainedFileCountLimit: null, retainedFileTimeLimit: TimeSpan.FromDays(7),
                fileSizeLimitBytes: null, rollOnFileSizeLimit: false,
                buffered: false, shared: true, encoding: new UTF8Encoding(false),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [PID:{ProcessId}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
    public static void Initialize()
    {
        Log.Logger = CreateLogger(DirectoryPath);
        Uptime.Restart();
        Log.Information("Application starting; version={Version}; OS={OS}; runtime={Runtime}; architecture={Architecture}; base={BaseDirectory}",
            typeof(AppLogging).Assembly.GetName().Version, RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture, AppContext.BaseDirectory);
        Log.Information("Application build identity; informationalVersion={InformationalVersion}; moduleId={ModuleId}; osVersion={OsVersion}; processors={ProcessorCount}; playbackDiagnostics={PlaybackDiagnostics}",
            typeof(AppLogging).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
            typeof(AppLogging).Assembly.ManifestModule.ModuleVersionId, Environment.OSVersion.VersionString, Environment.ProcessorCount, 2);
        _lastHour = DateTime.Now.ToString("yyyyMMddHH");
        // Roll and apply retention even while idle; at most one event per hour.
        _hourlyTimer = new Timer(_ =>
        {
            string hour = DateTime.Now.ToString("yyyyMMddHH");
            if (hour == _lastHour) return;
            _lastHour = hour;
            Log.Information("Application hourly status; uptime={Uptime}; memoryMB={MemoryMB}", Uptime.Elapsed, Environment.WorkingSet / 1024 / 1024);
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }
    public static void Close()
    {
        Interlocked.Exchange(ref _hourlyTimer, null)?.Dispose();
        Log.CloseAndFlush();
    }
}
