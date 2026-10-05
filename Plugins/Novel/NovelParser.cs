using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AudioPlayer.Plugin.Novel;

public static class NovelParser
{
    public static string NumberPattern(string format) => format switch
    {
        "阿拉伯数字" => "[0-9０-９]+", "中文数字" => "[零〇一二两三四五六七八九十百千万亿]+",
        "罗马数字" => "[IVXLCDMivxlcdmⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩⅪⅫ]+", _ => "[0-9０-９零〇一二两三四五六七八九十百千万亿IVXLCDMivxlcdmⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩⅪⅫ]+"
    };
    public static Regex? Rule(string pattern, ParseSettings settings)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        return new Regex(pattern.Replace("{number}", NumberPattern(settings.NumberFormat)).Replace("{units}", Regex.Escape(settings.Units)), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));
    }
    public static (string Text, string Encoding) ReadText(string path, string encoding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new InvalidDataException("文本超过 128 MiB，请先拆分成较小的小说文本。");
        var bytes = File.ReadAllBytes(path);
        if (encoding != "自动") return (Encoding.GetEncoding(encoding, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes).TrimStart('\uFEFF'), encoding);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
            string text = reader.ReadToEnd(); return (text, reader.CurrentEncoding.WebName);
        }
        try { return (new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'), "utf-8"); }
        catch (DecoderFallbackException) { return (Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes), "gbk"); }
    }
    public static NovelBook ParseFile(string path, ParseSettings settings, CancellationToken token = default)
    {
        var source = ReadText(path, settings.Encoding);
        return Parse(source.Text, settings, Path.GetFullPath(path), source.Encoding, token);
    }
    public static NovelBook Parse(string text, ParseSettings settings, string path = "", string encoding = "utf-8", CancellationToken token = default)
    {
        var chapterRule = Rule(settings.ChapterPattern, settings) ?? throw new InvalidDataException("章节规则不能为空。");
        var volumeRule = Rule(settings.VolumePattern, settings); var specialRule = Rule(settings.SpecialPattern, settings); var ignoreRule = Rule(settings.IgnorePattern, settings);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var book = new NovelBook { SourcePath = path, ContentHash = hash, Encoding = encoding, Parser = settings };
        string volume = "", volumeNumber = "";
        Chapter? chapter = null; var body = new StringBuilder();
        void Finish() { if (chapter is not null) chapter.Body = body.ToString().Trim(); body.Clear(); }
        using var reader = new StringReader(text);
        string? line; int lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            token.ThrowIfCancellationRequested(); lineNumber++;
            if (ignoreRule?.IsMatch(line) == true) continue;
            var volumeMatch = volumeRule?.Match(line);
            if (volumeMatch?.Success == true)
            {
                Finish(); chapter = null; volume = line.Trim(); volumeNumber = volumeMatch.Groups["number"].Value; continue;
            }
            var match = chapterRule.Match(line);
            bool ordinary = match.Success && (settings.HasTitle || string.IsNullOrWhiteSpace(match.Groups["title"].Value));
            if (ordinary || specialRule?.IsMatch(line) == true)
            {
                Finish();
                chapter = new Chapter { Id = hash[..16] + "-" + lineNumber, Order = book.Chapters.Count, Number = ordinary ? match.Groups["number"].Value : "",
                    Title = line.Trim(), Volume = volume, VolumeNumber = volumeNumber, SourceLine = lineNumber };
                book.Chapters.Add(chapter);
            }
            else if (chapter is not null) body.AppendLine(line);
        }
        Finish();
        if (book.Chapters.Count == 0) throw new InvalidDataException("未识别到章节，请调整章节规则、序号格式或文本编码后重新解析。");
        return book;
    }
    public static int? ParseNumber(string text)
    {
        text = text.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
        if (int.TryParse(text, out int value)) return value;
        if (Regex.IsMatch(text, "^[IVXLCDM]+$"))
        {
            int sum = 0, previous = 0;
            foreach (char c in text.Reverse()) { int n = c switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, _ => 1000 }; sum += n < previous ? -n : n; previous = Math.Max(previous, n); }
            return sum;
        }
        if (text.Length == 0 || !Regex.IsMatch(text, "^[零〇一二两三四五六七八九十百千万亿]+$")) return null;
        long total = 0, section = 0, digit = 0;
        foreach (char c in text)
        {
            int n = "零一二三四五六七八九".IndexOf(c); if (c == '〇') n = 0; if (c == '两') n = 2;
            if (n >= 0) { digit = digit * 10 + n; continue; }
            int unit = c switch { '十' => 10, '百' => 100, '千' => 1000, '万' => 10000, _ => 100000000 };
            if (unit < 10000) { section += Math.Max(1, digit) * unit; digit = 0; }
            else if (unit == 10000) { total += (section + digit) * unit; section = digit = 0; }
            else { total = (total + section + digit) * unit; section = digit = 0; }
            if (total > int.MaxValue || section > int.MaxValue || digit > int.MaxValue) return null;
        }
        long result = total + section + digit; return result <= int.MaxValue ? (int)result : null;
    }
    public static string Normalize(string text) => new(text.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    public static void Group(NovelBook book, bool volumes, int size)
    {
        size = Math.Clamp(size, 1, 1000);
        bool hasVolumes = volumes && book.Chapters.Any(c => !string.IsNullOrWhiteSpace(c.Volume));
        for (int i = 0; i < book.Chapters.Count; i++)
        {
            var c = book.Chapters[i]; c.Order = i;
            c.Group = hasVolumes ? (string.IsNullOrWhiteSpace(c.Volume) ? "未分卷" : c.Volume) : $"第 {i / size * size + 1}—{Math.Min((i / size + 1) * size, book.Chapters.Count)} 章";
            c.Changed(nameof(Chapter.Label)); c.Changed(nameof(Chapter.Group));
        }
    }
}

public sealed record Subtitle(double Time, string Text);
public static class SubtitleReader
{
    private static readonly Regex Stamp = new(@"\[(\d{1,5}):([0-5]\d)(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(150));
    public static List<Subtitle> Read(string path, List<string> warnings)
    {
        string text = NovelParser.ReadText(path, "自动").Text;
        var offset = Regex.Match(text, @"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150));
        double shift = offset.Success && double.TryParse(offset.Groups[1].Value, CultureInfo.InvariantCulture, out var o) ? Math.Clamp(o / 1000, -86400, 86400) : 0;
        var lines = new List<Subtitle>();
        foreach (string line in text.Split('\n'))
        {
            var matches = Stamp.Matches(line);
            if (matches.Count == 0) { if (Regex.IsMatch(line, @"^\s*\[\d")) warnings.Add(Path.GetFileName(path) + "：忽略异常时间戳"); continue; }
            string content = Stamp.Replace(line, "").Trim();
            foreach (Match m in matches)
            {
                double t = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
                if (m.Groups[3].Success) t += double.Parse("0." + m.Groups[3].Value, CultureInfo.InvariantCulture);
                lines.Add(new(Math.Max(0, t + shift), content));
            }
        }
        return lines.GroupBy(l => l.Time).OrderBy(g => g.Key).Select(g => new Subtitle(g.Key, string.Join(" ", g.Select(x => x.Text)))).ToList();
    }
}
