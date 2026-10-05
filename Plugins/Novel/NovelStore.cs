using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AudioPlayer.Plugin.Novel;

public sealed class NovelStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _root, _application;
    public NovelStore(string directory, string application) { _root = directory; _application = Path.GetFullPath(application); }
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    public string Resolve(string path) => string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path, _application);
    public string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string relative = Path.GetRelativePath(_application, Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative) ? relative : path;
    }
    public NovelSettings LoadSettings()
    {
        string path = Path.Combine(_root, "settings.json");
        if (!File.Exists(path)) return new();
        var settings = Read<NovelSettings>(path);
        settings.Parser ??= new(); settings.Bindings ??= new(); settings.GroupSize = Math.Clamp(settings.GroupSize, 1, 1000);
        return settings;
    }
    public void SaveSettings(NovelSettings settings) => Write(Path.Combine(_root, "settings.json"), settings);
    public NovelBook LoadPreview(string path)
    {
        var book = Read<NovelBook>(Resolve(path)); book.SourcePath = Resolve(book.SourcePath); return book;
    }
    public string SavePreview(Guid playlistId, NovelBook book)
    {
        string path = Path.Combine(_root, "previews", playlistId.ToString("N") + ".json");
        var copy = Clone(book); copy.SourcePath = Relative(copy.SourcePath); Write(path, copy); return Relative(path);
    }
    public string SaveCache(NovelMap map, string cacheDirectory)
    {
        Validate(map);
        string directory = string.IsNullOrWhiteSpace(cacheDirectory) ? Path.Combine(_root, "cache") : Resolve(cacheDirectory);
        string path = Path.Combine(directory, "novel-map-" + map.PlaylistId.ToString("N") + "-" + map.Book.ContentHash[..Math.Min(16, map.Book.ContentHash.Length)] + ".json");
        SaveMap(path, map); return Relative(path);
    }
    public NovelMap LoadCache(string path, Guid playlistId, bool backup = false)
    {
        var map = Read<NovelMap>(Resolve(path) + (backup ? ".bak" : ""));
        Validate(map);
        if (map.PlaylistId != playlistId) throw new InvalidDataException("缓存所属播放列表不匹配。");
        map.Book.SourcePath = Resolve(map.Book.SourcePath);
        foreach (var entry in map.Audio) entry.Path = Resolve(entry.Path);
        foreach (var segment in map.Segments) segment.AudioPath = Resolve(segment.AudioPath);
        return map;
    }
    public void SaveMap(string path, NovelMap map)
    {
        Validate(map); var copy = Clone(map);
        copy.Book.SourcePath = Relative(copy.Book.SourcePath);
        foreach (var entry in copy.Audio) entry.Path = Relative(entry.Path);
        foreach (var segment in copy.Segments) segment.AudioPath = Relative(segment.AudioPath);
        Write(Resolve(path), copy);
    }
    public void ClearCache(string path, Guid playlistId)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string resolved = Resolve(path);
        if (!Path.GetFileName(resolved).StartsWith("novel-map-" + playlistId.ToString("N") + "-", StringComparison.Ordinal) || !resolved.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("缓存文件名不属于本插件的当前播放列表，未删除。");
        if (File.Exists(resolved)) File.Delete(resolved);
        if (File.Exists(resolved + ".bak")) File.Delete(resolved + ".bak");
    }
    public static void Validate(NovelMap map)
    {
        if (map.Version != 1) throw new InvalidDataException($"缓存版本 {map.Version} 暂不支持，请保留文件并使用兼容版本。");
        var chapters = map.Book.Chapters.Select(c => c.Id).ToHashSet();
        if (chapters.Count == 0 || chapters.Count != map.Book.Chapters.Count) throw new InvalidDataException("缓存章节编号为空或重复。");
        var tracks = map.Audio.Select(a => a.TrackId).ToHashSet();
        foreach (var segment in map.Segments)
            if (!chapters.Contains(segment.ChapterId) || !tracks.Contains(segment.TrackId) || !double.IsFinite(segment.Start) || !double.IsFinite(segment.End) || segment.Start < 0 || segment.End <= segment.Start)
                throw new InvalidDataException("片段需关联有效章节与音频，时间必须满足 0 ≤ 开始 < 结束。");
        foreach (var group in map.Segments.GroupBy(s => s.TrackId))
        {
            double end = -1;
            foreach (var segment in group.OrderBy(s => s.Start)) { if (segment.Start < end - 0.001) throw new InvalidDataException("同一音频的片段时间有重叠，请先校对。"); end = segment.End; }
        }
    }
    private static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidDataException("缓存过大，无法加载。");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Json) ?? throw new InvalidDataException("文件内容为空。");
    }
    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Cache presence controls tracking. AutoLocate only controls the one post-parse lookup.</summary>
public sealed class TrackingState
{
    private string? _previous;
    public void Reset(string? currentChapter) => _previous = currentChapter;
    public bool Update(bool hasCache, string? currentChapter)
    {
        bool changed = currentChapter != _previous; _previous = currentChapter;
        return hasCache && changed && currentChapter is not null;
    }
}
