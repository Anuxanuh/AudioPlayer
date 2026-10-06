using AudioPlayer.Models;
using System.Globalization;
using System.Text;

namespace AudioPlayer.Services;

public sealed record TranslationLanguage(string Code, string Name);
public sealed record LyricDisplayOption(LyricDisplayMode Value, string Label);

public static class LyricTranslation
{
    public const string ModelId = "m2m100-418M";
    public const string ModelRepository = "https://huggingface.co/michaelfeil/ct2fast-m2m100_418M";
    public static IReadOnlyList<TranslationLanguage> Languages { get; } = new[]
    {
        new TranslationLanguage("zh", "中文"), new("en", "英语"), new("ja", "日语"), new("ko", "韩语"),
        new("fr", "法语"), new("de", "德语"), new("es", "西班牙语"), new("ru", "俄语"), new("it", "意大利语"),
        new("pt", "葡萄牙语"), new("ar", "阿拉伯语"), new("hi", "印地语"), new("th", "泰语"), new("vi", "越南语"),
        new("id", "印度尼西亚语"), new("ms", "马来语"), new("tr", "土耳其语"), new("uk", "乌克兰语"),
        new("nl", "荷兰语"), new("pl", "波兰语"), new("sv", "瑞典语"), new("fi", "芬兰语")
    };
    public static IReadOnlyList<LyricDisplayOption> DisplayModes { get; } = new[]
    {
        new LyricDisplayOption(LyricDisplayMode.Original, "仅显示原文"),
        new(LyricDisplayMode.Translation, "仅显示译文"), new(LyricDisplayMode.Bilingual, "原文和译文")
    };
    public static bool IsLanguage(string? code) => Languages.Any(l => l.Code == code);
    public static string OutputPath(string audio, string language)
    {
        if (!IsLanguage(language)) throw new ArgumentException("请选择支持的目标语言。");
        return Path.ChangeExtension(Path.GetFullPath(audio), language + ".lrc");
    }
    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "models", "translation", ModelId);
    public static bool IsModelComplete(string path)
    {
        try
        {
            return new[] { "model.bin", "config.json", "sentencepiece.bpe.model" }
                .All(name => File.Exists(Path.Combine(path, name)) && new FileInfo(Path.Combine(path, name)).Length > 0)
                && new[] { "shared_vocabulary.txt", "shared_vocabulary.json" }.Any(name => File.Exists(Path.Combine(path, name)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    // Pair by timestamp and occurrence, so duplicate timestamps and silence markers remain stable.
    public static LrcDocument Compose(LrcDocument original, LrcDocument translated, LyricDisplayMode mode)
    {
        if (mode == LyricDisplayMode.Original) return original;
        if (mode == LyricDisplayMode.Translation) return translated;
        var remaining = translated.Lines.GroupBy(l => l.Time).ToDictionary(g => g.Key, g => new Queue<LyricLine>(g));
        var lines = new List<LyricLine>();
        foreach (var line in original.Lines)
        {
            string text = remaining.TryGetValue(line.Time, out var matches) && matches.TryDequeue(out var match) ? match.Text : "";
            lines.Add(string.IsNullOrWhiteSpace(line.Text) ? new(line.Time, text) : line with { Translation = text });
        }
        lines.AddRange(remaining.Values.SelectMany(q => q));
        return new LrcDocument(lines.OrderBy(l => l.Time).ToArray());
    }

    public static string Serialize(LrcDocument document, string language)
    {
        var text = new StringBuilder().AppendLine("[re:声屿 · 本地歌词翻译]").AppendLine($"[lang:{language}]");
        foreach (var line in document.Lines)
        {
            // Effective timestamps already include the original offset.
            long ms = Math.Max(0, (long)Math.Round(line.Time.TotalMilliseconds));
            text.Append(CultureInfo.InvariantCulture, $"[{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}]");
            text.AppendLine(line.Text.Replace('\r', ' ').Replace('\n', ' '));
        }
        return text.ToString();
    }
}
