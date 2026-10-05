using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AudioPlayer.Plugin.Novel;

public sealed class ParseSettings
{
    public string ChapterPattern { get; set; } = @"^\s*第?\s*(?<number>{number})\s*[{units}]\s*(?<title>[^\r\n]{0,60})$";
    public string Units { get; set; } = "章集回";
    public string NumberFormat { get; set; } = "自动";
    public bool HasTitle { get; set; } = true;
    public string VolumePattern { get; set; } = @"^\s*(?:第\s*(?<number>{number})\s*[卷部篇]|卷\s*(?<number>{number}))\s*[^\r\n]{0,60}$";
    public string SpecialPattern { get; set; } = @"^\s*(序章|楔子|前言|序言|番外|后记|尾声)(?:\s|[：:、].*|[一二三四五六七八九十0-9].*|$).*$";
    public string IgnorePattern { get; set; } = "";
    public string Encoding { get; set; } = "自动";
}
public sealed class NovelSettings
{
    public ParseSettings Parser { get; set; } = new();
    public bool AutoLocate { get; set; } = true;
    public bool GroupByVolume { get; set; } = true;
    public int GroupSize { get; set; } = 50;
    public string CacheDirectory { get; set; } = "";
    public Dictionary<Guid, BookBinding> Bindings { get; set; } = new();
}
public sealed class BookBinding
{
    public string SourcePath { get; set; } = "";
    public string PreviewPath { get; set; } = "";
    public string CachePath { get; set; } = "";
}
public sealed class Chapter : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Order { get; set; }
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string Volume { get; set; } = "";
    public string VolumeNumber { get; set; } = "";
    public string Body { get; set; } = "";
    public int SourceLine { get; set; }
    [JsonIgnore] public string Label => $"{Order + 1:0000}  {Title}";
    [JsonIgnore] public string Group { get; set; } = "";
    [JsonIgnore] public string Availability { get; set; } = "未定位";
    private bool _current;
    [JsonIgnore] public bool IsCurrent { get => _current; set { _current = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class NovelBook
{
    public string SourcePath { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Encoding { get; set; } = "";
    public ParseSettings Parser { get; set; } = new();
    public List<Chapter> Chapters { get; set; } = new();
}
public sealed class AudioEntry
{
    public Guid TrackId { get; set; }
    public string Path { get; set; } = "";
    public int Order { get; set; }
    public double? Duration { get; set; }
    public bool Missing { get; set; }
}
public sealed class ChapterSegment
{
    public string ChapterId { get; set; } = "";
    public Guid TrackId { get; set; }
    public string AudioPath { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public double Confidence { get; set; }
    public string Evidence { get; set; } = "";
    public bool Missing { get; set; }
    [JsonIgnore] public string FileName => System.IO.Path.GetFileName(AudioPath);
    [JsonIgnore] public string Range => $"{TimeSpan.FromSeconds(Start):hh\\:mm\\:ss} → {TimeSpan.FromSeconds(End):hh\\:mm\\:ss}";
    [JsonIgnore] public string Status => Missing ? "音频缺失 / 不可播放" : "可播放";
}
public sealed record MissingRange(int FirstOrder, int LastOrder, string Status, string Evidence);
public sealed class NovelMap
{
    public int Version { get; set; } = 1;
    public Guid PlaylistId { get; set; }
    public string PlaylistName { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public NovelBook Book { get; set; } = new();
    public List<AudioEntry> Audio { get; set; } = new();
    public List<ChapterSegment> Segments { get; set; } = new();
    public List<MissingRange> Gaps { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
