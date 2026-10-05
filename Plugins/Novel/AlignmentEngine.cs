using System.IO;
using System.Text.RegularExpressions;
using AudioPlayer.Plugin.Abstractions;

namespace AudioPlayer.Plugin.Novel;

public sealed class StartAnchorException : Exception
{
    public StartAnchorException() : base("第一首现存音频的标题和正文均无法可靠匹配。请在章节列表选中起始章节，再点击“设为异常起点”。") { }
}
public sealed record TextMatch(int Chapter, double Confidence, string Evidence);

public sealed class ChapterTextIndex
{
    private readonly NovelBook _book;
    private readonly string[] _body;
    private readonly Dictionary<ulong, List<int>> _grams = new();
    private readonly Dictionary<int, List<int>> _numbers = new();
    private readonly Dictionary<string, List<int>> _titles = new();
    private readonly Regex? _customTitle;
    private readonly Regex _spokenTitle = new(@"^\s*第?\s*(?<number>[0-9零〇一二两三四五六七八九十百千万IVXLCDMivxlcdm]+)\s*[章集回卷]\s*(?<title>.{0,60})$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(150));
    public ChapterTextIndex(NovelBook book, CancellationToken token = default)
    {
        _book = book; _body = new string[book.Chapters.Count]; _customTitle = NovelParser.Rule(book.Parser.ChapterPattern, book.Parser);
        for (int i = 0; i < _body.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var chapter = book.Chapters[i];
            string title = NovelParser.Normalize(chapter.Title);
            if (!_titles.TryGetValue(title, out var titles)) _titles[title] = titles = new(); titles.Add(i);
            if (NovelParser.ParseNumber(chapter.Number) is int number) { if (!_numbers.TryGetValue(number, out var list)) _numbers[number] = list = new(); list.Add(i); }
            _body[i] = NovelParser.Normalize(chapter.Body);
            foreach (ulong gram in Grams(_body[i]).Where((_, offset) => offset % 2 == 0).Distinct())
            {
                if (!_grams.TryGetValue(gram, out var postings)) _grams[gram] = postings = new();
                if (postings.Count <= 80) postings.Add(i); // Common boilerplate is not a useful anchor.
            }
        }
    }
    private static IEnumerable<ulong> Grams(string text)
    {
        for (int i = 0; i + 3 < text.Length; i++) yield return (ulong)text[i] << 48 | (ulong)text[i + 1] << 32 | (ulong)text[i + 2] << 16 | text[i + 3];
    }
    public TextMatch? Match(string text, int previous = -1, bool titleOnly = false)
    {
        string normalized = NovelParser.Normalize(text);
        if (_titles.TryGetValue(normalized, out var exactTitles) && exactTitles.Count == 1) return new(exactTitles[0], 1, "完整章节标题：" + text);
        var title = _customTitle?.Match(text);
        if (title?.Success != true) title = _spokenTitle.Match(text.Normalize(System.Text.NormalizationForm.FormKC));
        if (title.Success && NovelParser.ParseNumber(title.Groups["number"].Value) is int number && _numbers.TryGetValue(number, out var numbered))
        {
            string name = NovelParser.Normalize(title.Groups["title"].Value);
            var exactNames = name.Length >= 2 ? numbered.Where(i => NovelParser.Normalize(_book.Chapters[i].Title).EndsWith(name, StringComparison.Ordinal)).ToArray() : Array.Empty<int>();
            if (exactNames.Length == 1) return new(exactNames[0], 1, "章节标题与名称：" + text);
            if (numbered.Count == 1) return new(numbered[0], 0.97, "章节序号锚点：" + text);
            var nearby = numbered.Where(i => i >= previous && i <= previous + 1).ToArray();
            if (previous >= 0 && nearby.Length == 1) return new(nearby[0], 0.88, "同卷相邻章节序号：" + text);
        }
        if (titleOnly) return null;
        string query = normalized;
        if (query.Length < 8) return null;
        if (query.Length > 200) query = query[..200];
        var queryGrams = Grams(query).Distinct().ToArray();
        var votes = new Dictionary<int, int>();
        foreach (ulong gram in queryGrams)
            if (_grams.TryGetValue(gram, out var entries) && entries.Count <= 80)
                foreach (int i in entries) votes[i] = votes.GetValueOrDefault(i) + 1;
        var candidates = votes.OrderByDescending(x => x.Value).Take(8).Select(x => x.Key).ToList();
        if (previous >= 0) for (int i = previous; i <= Math.Min(previous + 1, _body.Length - 1); i++) if (!candidates.Contains(i)) candidates.Add(i);
        var scored = candidates.Select(i => (Chapter: i, Score: Similarity(query, _body[i], queryGrams))).OrderByDescending(x => x.Score).ToArray();
        if (scored.Length == 0 || scored[0].Score < 0.56 || (scored.Length > 1 && scored[0].Score - scored[1].Score < 0.08)) return null;
        var best = scored[0];
        return new(best.Chapter, best.Score, $"正文匹配 {best.Score:P0} · 小说第 {_book.Chapters[best.Chapter].SourceLine} 行 · {text[..Math.Min(text.Length, 70)]}");
    }
    private static double Similarity(string query, string body, ulong[] grams)
    {
        if (body.Contains(query, StringComparison.Ordinal)) return 1;
        if (body.Length < 8) return 0;
        var starts = new HashSet<int>();
        for (int offset = 0; offset + 3 < query.Length; offset += 3)
        {
            int found = body.IndexOf(query.Substring(offset, 4), StringComparison.Ordinal);
            if (found >= 0) starts.Add(Math.Clamp(found - offset, 0, body.Length - 1));
            if (starts.Count >= 10) break;
        }
        double best = 0;
        foreach (int start in starts)
        {
            string window = body.Substring(start, Math.Min(query.Length + 4, body.Length - start));
            var shared = Grams(window).ToHashSet();
            best = Math.Max(best, 2.0 * grams.Count(shared.Contains) / Math.Max(1, grams.Length + shared.Count));
        }
        return best;
    }
}

public static class AlignmentEngine
{
    public static NovelMap Generate(NovelBook book, PlaybackSnapshot playlist, IProgress<string>? progress = null, int? manualStart = null, CancellationToken token = default)
    {
        if (playlist.Tracks.Count == 0) throw new InvalidOperationException("当前播放列表为空。");
        var map = new NovelMap { Book = book, PlaylistId = playlist.PlaylistId, PlaylistName = playlist.PlaylistName };
        progress?.Report("正在建立小说正文索引…");
        var index = new ChapterTextIndex(book, token);
        bool first = true; int previousLast = -1;
        foreach (var track in playlist.Tracks)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"对齐 {map.Audio.Count + 1}/{playlist.Tracks.Count} · {track.Title}");
            var audio = new AudioEntry { TrackId = track.Id, Path = track.FilePath, Order = map.Audio.Count, Missing = !File.Exists(track.FilePath) }; map.Audio.Add(audio);
            if (audio.Missing) { map.Warnings.Add(track.Title + "：音频文件缺失，无法读取字幕"); continue; }
            string lrc = Path.ChangeExtension(track.FilePath, ".lrc");
            if (!File.Exists(lrc)) { if (first) throw new InvalidDataException("第一首现存音频缺少同名 LRC：" + lrc); map.Warnings.Add(track.Title + "：缺少同名 LRC，标记未知区间"); map.Gaps.Add(new(-1, -1, "未知区间", track.Title + " 无字幕")); continue; }
            var lines = SubtitleReader.Read(lrc, map.Warnings);
            if (lines.Count == 0) throw new InvalidDataException("LRC 没有有效时间戳：" + lrc);
            try { using var tag = TagLib.File.Create(track.FilePath); if (tag.Properties.Duration.TotalSeconds > 0) audio.Duration = tag.Properties.Duration.TotalSeconds; } catch (Exception ex) when (ex is IOException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException) { }
            int hint = previousLast;
            if (first)
            {
                var opening = lines.Take(40).ToArray();
                var anchor = opening.Select(l => index.Match(l.Text, titleOnly: true)).FirstOrDefault(m => m is not null)
                    ?? opening.Select(l => index.Match(l.Text)).FirstOrDefault(m => m is not null);
                if (anchor is null && manualStart is null) throw new StartAnchorException();
                hint = anchor?.Chapter ?? Math.Clamp(manualStart!.Value, 0, book.Chapters.Count - 1);
                first = false;
            }
            int firstChapter = -1, lastChapter = -1;
            for (int n = 0; n < lines.Count; n++)
            {
                token.ThrowIfCancellationRequested(); var line = lines[n];
                var match = index.Match(line.Text, hint);
                if (match is null && manualStart is int start && map.Audio.Count(a => !a.Missing) == 1 && n == 0) match = new(Math.Clamp(start, 0, book.Chapters.Count - 1), 1, "用户手动指定异常起点");
                if (match is null) continue;
                hint = match.Chapter;
                if (firstChapter < 0) firstChapter = hint;
                lastChapter = hint;
                double supportedLength = Math.Clamp(NovelParser.Normalize(line.Text).Length / 2.0 + 6, 8, 45);
                double end = n + 1 < lines.Count ? lines[n + 1].Time : audio.Duration ?? line.Time + supportedLength;
                end = Math.Min(end, line.Time + supportedLength);
                if (audio.Duration is double duration) end = Math.Min(end, duration);
                if (end <= line.Time) continue;
                string id = book.Chapters[hint].Id;
                var last = map.Segments.LastOrDefault();
                if (last is not null && last.TrackId == track.Id && last.ChapterId == id && Math.Abs(last.End - line.Time) < 0.05)
                { last.End = end; last.Confidence = Math.Min(last.Confidence, match.Confidence); }
                else map.Segments.Add(new() { ChapterId = id, TrackId = track.Id, AudioPath = track.FilePath, Start = line.Time, End = end, Confidence = match.Confidence, Evidence = match.Evidence });
            }
            if (lastChapter < 0) map.Gaps.Add(new(-1, -1, "未知区间", track.Title + "：没有可靠正文或标题锚点，不连续推断"));
            else
            {
                if (previousLast >= 0 && firstChapter > previousLast + 1) map.Gaps.Add(new(previousLast + 1, firstChapter - 1, "可能缺失", $"相邻现存音频从第 {previousLast + 1} 章跳到第 {firstChapter + 1} 章，原音频名称未知"));
                if (previousLast >= 0 && firstChapter < previousLast) map.Warnings.Add(track.Title + "：章节顺序回退，请校对演播顺序或重复内容");
                previousLast = lastChapter;
            }
        }
        if (first) throw new InvalidOperationException("播放列表中的音频全部缺失。");
        map.Warnings = map.Warnings.Distinct().ToList();
        return map;
    }
    public static bool Reconcile(NovelMap map, PlaybackSnapshot snapshot)
    {
        var paths = snapshot.Tracks.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed = map.Audio.Count != snapshot.Tracks.Count || !map.Audio.Select(a => a.Path).SequenceEqual(snapshot.Tracks.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (var audio in map.Audio)
        {
            audio.Missing = !paths.Contains(audio.Path) || !File.Exists(audio.Path);
            if (audio.Missing) changed = true;
        }
        var missing = map.Audio.Where(a => a.Missing).Select(a => a.TrackId).ToHashSet();
        foreach (var segment in map.Segments) segment.Missing = missing.Contains(segment.TrackId);
        return changed;
    }
}

public sealed class MapLookup
{
    private readonly Dictionary<string, ChapterSegment[]> _tracks;
    public MapLookup(NovelMap map) => _tracks = map.Segments.GroupBy(s => s.AudioPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Start).ToArray(), StringComparer.OrdinalIgnoreCase);
    public ChapterSegment? Locate(string? audioPath, double position)
    {
        if (audioPath is null || !_tracks.TryGetValue(audioPath, out var segments)) return null;
        int lo = 0, hi = segments.Length - 1, found = -1;
        while (lo <= hi) { int mid = (lo + hi) / 2; if (segments[mid].Start <= position) { found = mid; lo = mid + 1; } else hi = mid - 1; }
        return found >= 0 && position < segments[found].End ? segments[found] : null;
    }
}
