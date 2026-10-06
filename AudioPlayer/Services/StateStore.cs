using System.Collections.ObjectModel;
using System.Text.Json;
using AudioPlayer.Models;

namespace AudioPlayer.Services;

public sealed class StateStore
{
    public string DirectoryPath { get; }
    public string? LoadWarning { get; private set; }
    private string StatePath => Path.Combine(DirectoryPath, "state.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly PortablePaths _paths;
    public StateStore(string? directory = null, string? portableRoot = null)
    {
        _paths = new PortablePaths(portableRoot);
        DirectoryPath = directory ?? Path.Combine(_paths.Root, "data");
    }

    public PlayerState Load()
    {
        PlayerState state = new();
        if (File.Exists(StatePath))
        {
            try { state = Read(StatePath); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Reading settings failed; attempting backup; path={Path}", StatePath);
                LoadWarning = "配置读取失败，已尝试恢复上次备份。";
                try { state = Read(StatePath + ".bak"); }
                catch (Exception backupEx) when (backupEx is IOException or JsonException or UnauthorizedAccessException)
                { Log.Warning(backupEx, "Reading settings backup failed; using defaults"); }
            }
        }
        TransformPaths(state, expand: true);
        Normalize(state);
        return state;
    }

    private static PlayerState Read(string path) => JsonSerializer.Deserialize<PlayerState>(File.ReadAllText(path)) ?? throw new JsonException("配置为空");

    private static void Normalize(PlayerState state)
    {
        state.Settings ??= new();
        state.Playlists ??= new();
        var lists = new ObservableCollection<Playlist>();
        var playlistIds = new HashSet<Guid>();
        foreach (var playlist in state.Playlists.Where(p => p is not null))
        {
            if (!playlistIds.Add(playlist.Id)) playlist.Id = Guid.NewGuid();
            playlist.Name = string.IsNullOrWhiteSpace(playlist.Name) ? "未命名列表" : playlist.Name;
            var tracks = new ObservableCollection<Track>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in (playlist.Tracks ?? new()).Where(t => t is not null))
            {
                if (string.IsNullOrWhiteSpace(track.FilePath)) continue;
                try { track.FilePath = Path.GetFullPath(track.FilePath); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (!paths.Add(track.FilePath)) continue;
                if (tracks.Any(t => t.Id == track.Id)) track.Id = Guid.NewGuid();
                tracks.Add(track);
            }
            playlist.Tracks = tracks;
            lists.Add(playlist);
        }
        state.Playlists = lists;
        if (state.Playlists.Count == 0) state.Playlists.Add(new Playlist());
        var s = state.Settings;
        s.EnabledPlugins = new Dictionary<string, bool>(s.EnabledPlugins ?? new(), StringComparer.OrdinalIgnoreCase);
        s.FontSize = double.IsFinite(s.FontSize) ? Math.Clamp(s.FontSize, 16, 80) : 34;
        s.Volume = double.IsFinite(s.Volume) ? Math.Clamp(s.Volume, 0, 1) : 0.65;
        s.PlaybackRate = double.IsFinite(s.PlaybackRate) ? Math.Clamp(s.PlaybackRate, 0.5, 3) : 1;
        s.RecognitionParallelism = Math.Clamp(s.RecognitionParallelism, 1, 4);
        s.LyricOpacity = double.IsFinite(s.LyricOpacity) ? Math.Clamp(s.LyricOpacity, 0.3, 1) : 1;
        s.LyricOffsetSeconds = double.IsFinite(s.LyricOffsetSeconds) ? Math.Clamp(s.LyricOffsetSeconds, -60, 60) : 0;
        s.FontFamily = string.IsNullOrWhiteSpace(s.FontFamily) ? "Microsoft YaHei UI" : s.FontFamily;
        s.PythonPath = string.IsNullOrWhiteSpace(s.PythonPath) ? "python" : s.PythonPath;
        s.ModelPath ??= "";
        s.ModelsDirectory ??= "";
        s.ModelId = string.IsNullOrWhiteSpace(s.ModelId) ? "tiny" : s.ModelId;
        s.Language = string.IsNullOrWhiteSpace(s.Language) ? "auto" : s.Language;
        if (!Enum.IsDefined(s.Mode)) s.Mode = PlayMode.Sequential;
        if (!Enum.IsDefined(s.LyricDisplayMode)) s.LyricDisplayMode = LyricDisplayMode.Original;
        if (!LyricTranslation.IsLanguage(s.TranslationTargetLanguage)) s.TranslationTargetLanguage = "zh";
        s.TranslationModelPath ??= "";
    }

    public void Save(PlayerState state)
    {
        Directory.CreateDirectory(DirectoryPath);
        string temp = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Convert a snapshot so saving never mutates live bindings or playback paths.
            var snapshot = JsonSerializer.Deserialize<PlayerState>(JsonSerializer.Serialize(state, Options))!;
            TransformPaths(snapshot, expand: false);
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Options));
            if (File.Exists(StatePath)) File.Replace(temp, StatePath, StatePath + ".bak");
            else File.Move(temp, StatePath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void TransformPaths(PlayerState state, bool expand)
    {
        string ConvertPath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            try { return expand ? _paths.Expand(value) : _paths.Store(value); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return value; }
        }
        if (state.Settings is { } settings)
        {
            settings.PythonPath = expand ? _paths.ExpandExecutable(settings.PythonPath ?? "python") : _paths.Store(settings.PythonPath);
            settings.ModelPath = ConvertPath(settings.ModelPath);
            settings.TranslationModelPath = ConvertPath(settings.TranslationModelPath);
            settings.ModelsDirectory = ConvertPath(settings.ModelsDirectory);
        }
        if (state.Playlists is null) return;
        foreach (var playlist in state.Playlists)
        {
            if (playlist?.Tracks is null) continue;
            foreach (var track in playlist.Tracks)
            {
                if (track is null) continue;
                track.FilePath = ConvertPath(track.FilePath);
                if (track.LyricsPath is not null) track.LyricsPath = ConvertPath(track.LyricsPath);
            }
        }
    }
}
