using System.Windows;

namespace AudioPlayer.Plugin.Abstractions;

/// <summary>Version 1 desktop plugin contract. Plugin UI runs on the host dispatcher.</summary>
public interface IPlayerPlugin : IDisposable
{
    FrameworkElement CreatePage(PluginContext context);
    Task StopAsync();
}

public sealed record PluginContext(string ApplicationDirectory, string PluginDirectory, string DataDirectory,
    Func<string> GetPythonPath, Action<string, string> Log, CancellationToken ShutdownToken)
{
    // Optional capability keeps the API 1 constructor compatible with existing plugins.
    public IPlaybackHost? Playback { get; init; }
}

public sealed record PlaylistAudio(Guid Id, string FilePath, string Title);
public sealed record PlaybackSnapshot(Guid PlaylistId, string PlaylistName, IReadOnlyList<PlaylistAudio> Tracks,
    Guid? CurrentTrackId, string? CurrentFilePath, double PositionSeconds, double DurationSeconds, bool IsPlaying);

/// <summary>Call on the host dispatcher. Snapshots are immutable and contain no file-system scans.</summary>
public interface IPlaybackHost
{
    PlaybackSnapshot GetSnapshot();
    Task PlayAsync(Guid playlistId, Guid trackId, double seconds, CancellationToken cancellationToken);
}
