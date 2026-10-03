using System.Windows;

namespace AudioPlayer.Plugin.Abstractions;

/// <summary>Version 1 desktop plugin contract. Plugin UI runs on the host dispatcher.</summary>
public interface IPlayerPlugin : IDisposable
{
    FrameworkElement CreatePage(PluginContext context);
    Task StopAsync();
}

public sealed record PluginContext(string ApplicationDirectory, string PluginDirectory, string DataDirectory,
    Func<string> GetPythonPath, Action<string, string> Log, CancellationToken ShutdownToken);
