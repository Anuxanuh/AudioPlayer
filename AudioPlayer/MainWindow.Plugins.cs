using AudioPlayer.Plugin.Abstractions;

namespace AudioPlayer;

public partial class MainWindow
{
    public sealed class PluginPlaybackHost(MainWindow window) : IPlaybackHost
    {
        private long _request;
        public PlaybackSnapshot GetSnapshot()
        {
            window.Dispatcher.VerifyAccess();
            var playlist = window._view.SelectedPlaylist;
            return new(playlist.Id, playlist.Name, playlist.Tracks.Select(t => new PlaylistAudio(t.Id, t.FilePath, t.Title)).ToArray(),
                window._current?.Id, window._current?.FilePath, window._audio.Position.TotalSeconds, window._audio.Duration.TotalSeconds, window._audio.IsPlaying);
        }
        public async Task PlayAsync(Guid playlistId, Guid trackId, double seconds, CancellationToken cancellationToken)
        {
            window.Dispatcher.VerifyAccess();
            cancellationToken.ThrowIfCancellationRequested();
            if (window._exiting) throw new InvalidOperationException("播放器正在退出。");
            if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
            var playlist = window._view.State.Playlists.FirstOrDefault(p => p.Id == playlistId) ?? throw new InvalidOperationException("播放列表已删除。");
            var track = playlist.Tracks.FirstOrDefault(t => t.Id == trackId) ?? throw new InvalidOperationException("音频已从播放列表删除，请重新定位。");
            if (!File.Exists(track.FilePath)) throw new FileNotFoundException("音频文件缺失。", track.FilePath);
            long request = ++_request;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Log.Information("Plugin playback request started; request={RequestId}; playlist={Playlist}; track={Track}; targetSeconds={TargetSeconds}; previousTrack={PreviousTrack}; previousMedia={PreviousMediaId}",
                request, playlistId, trackId, seconds, window._current?.Id, window._audio.MediaId);
            try
            {
                window._view.SelectedPlaylist = playlist;
                bool opening = window._current?.Id != track.Id || !window._audio.IsReady;
                if (opening)
                {
                    var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Ready() { if (window._current?.Id == track.Id) opened.TrySetResult(); else opened.TrySetException(new InvalidOperationException("当前音频已改变。")); }
                    void Failed(string message) => opened.TrySetException(new InvalidOperationException(message));
                    window._audio.Opened += Ready; window._audio.Failed += Failed;
                    try
                    {
                        window._queue.Reset(); window.PlayTrackAt(track, seconds);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window._lifetime.Token);
                        await opened.Task.WaitAsync(TimeSpan.FromSeconds(20), linked.Token);
                    }
                    finally { window._audio.Opened -= Ready; window._audio.Failed -= Failed; }
                }
                if (request != _request || window._current?.Id != track.Id || window._exiting) throw new OperationCanceledException("播放请求已被替换。");
                cancellationToken.ThrowIfCancellationRequested();
                if (!opening)
                {
                    window._audio.Seek(seconds, $"plugin-request:{request}");
                    if (!window._audio.IsPlaying) window._audio.Toggle($"plugin-request:{request}");
                }
                window.UpdatePlaybackUi(); window.UpdateLyrics();
                Log.Information("Plugin playback jump commands issued; request={RequestId}; media={MediaId}; playlist={Playlist}; track={Track}; seconds={Seconds}; elapsedMs={ElapsedMs}; progressConfirmed=false",
                    request, window._audio.MediaId, playlistId, trackId, seconds, elapsed.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                Log.Information("Plugin playback request canceled; request={RequestId}; media={MediaId}; currentTrack={CurrentTrack}; elapsedMs={ElapsedMs}", request, window._audio.MediaId, window._current?.Id, elapsed.Elapsed.TotalMilliseconds);
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Plugin playback request failed; request={RequestId}; media={MediaId}; currentTrack={CurrentTrack}; elapsedMs={ElapsedMs}", request, window._audio.MediaId, window._current?.Id, elapsed.Elapsed.TotalMilliseconds);
                throw;
            }
        }
    }
}
