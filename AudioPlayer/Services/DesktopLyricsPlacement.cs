using System.Windows;

namespace AudioPlayer.Services;

/// <summary>Use actual monitor work areas, not their bounding rectangle (which can contain gaps).</summary>
public static class DesktopLyricsPlacement
{
    public static Rect Constrain(Rect desired, IReadOnlyList<Rect> workAreas)
    {
        if (workAreas.Count == 0) return desired;
        Rect area = workAreas.OrderByDescending(a => Overlap(desired, a))
            .ThenBy(a => DistanceSquared(desired, a)).First();
        double width = Math.Min(desired.Width, area.Width), height = Math.Min(desired.Height, area.Height);
        return new Rect(Math.Clamp(desired.Left, area.Left, area.Right - width),
            Math.Clamp(desired.Top, area.Top, area.Bottom - height), width, height);
    }
    private static double Overlap(Rect left, Rect right)
    {
        var intersection = Rect.Intersect(left, right);
        return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
    }
    private static double DistanceSquared(Rect rect, Rect area)
    {
        double x = rect.Left + rect.Width / 2, y = rect.Top + rect.Height / 2;
        double dx = x - Math.Clamp(x, area.Left, area.Right), dy = y - Math.Clamp(y, area.Top, area.Bottom);
        return dx * dx + dy * dy;
    }
}
