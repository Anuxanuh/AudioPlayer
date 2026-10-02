using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AudioPlayer.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed class LrcDocument
{
    private static readonly Regex Timestamp = new(@"\[(\d{1,5}):([0-5]\d)(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    public IReadOnlyList<LyricLine> Lines { get; }
    public LrcDocument(IReadOnlyList<LyricLine> lines) => Lines = lines;
    public static LrcDocument Parse(string text)
    {
        var offset = Regex.Match(text, @"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase);
        double offsetMs = offset.Success && double.TryParse(offset.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        offsetMs = double.IsFinite(offsetMs) ? Math.Clamp(offsetMs, -86400000, 86400000) : 0;
        var lines = new List<LyricLine>();
        foreach (string raw in text.Split('\n'))
        {
            var stamps = Timestamp.Matches(raw);
            if (stamps.Count == 0) continue;
            string content = Timestamp.Replace(raw, "").Trim();
            foreach (Match stamp in stamps)
            {
                double seconds = int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                if (stamp.Groups[3].Success) seconds += double.Parse("0." + stamp.Groups[3].Value, CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(TimeSpan.FromMilliseconds(Math.Max(0, seconds * 1000 + offsetMs)), content));
            }
        }
        return new LrcDocument(lines.OrderBy(l => l.Time).ToArray());
    }

    public static LrcDocument Load(string path)
    {
        try { return Parse(File.ReadAllText(path, new UTF8Encoding(false, true))); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Parse(File.ReadAllText(path, Encoding.GetEncoding(936)));
        }
    }

    public int FindLine(TimeSpan position)
    {
        int lo = 0, hi = Lines.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Lines[mid].Time <= position) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return result;
    }
}
