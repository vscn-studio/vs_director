namespace VSDirector.Data;

/// <summary>Shared geometry for drawing, hit testing and reordering variable-height rows.</summary>
public sealed class DirectorTimelineRows
{
    public const double DefaultHeight = 40;
    public static double NormalizeHeight(double height) => double.IsFinite(height) ? Math.Clamp(height, 40, 160) : DefaultHeight;
    private readonly double[] tops;
    public int Count => tops.Length - 1;
    public DirectorTimelineRows(IEnumerable<double> heights)
    {
        var values = heights.Select(NormalizeHeight).ToArray();
        tops = new double[values.Length + 1];
        for (int i = 0; i < values.Length; i++) tops[i + 1] = tops[i] + values[i];
    }
    public double Top(int index) => tops[Math.Clamp(index, 0, Count)];
    public double Height(int index) => Top(index + 1) - Top(index);
    public int MaxFirst(double viewportHeight)
    {
        int first = Math.Max(0, Count - 1);
        while (first > 0 && Top(Count) - Top(first - 1) <= viewportHeight) first--;
        return first;
    }
    public int At(double y)
    {
        if (y < 0) return -1;
        int lo = 0, hi = Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (tops[mid + 1] <= y) lo = mid + 1; else hi = mid; }
        return lo;
    }
}
