namespace VSDirector.Data;

/// <summary>Search timestamp metadata before touching compressed payloads.</summary>
public static class DirectorReplayTimeSearch
{
    public static int UpperBound<T>(IReadOnlyList<T> items, double time, Func<T, double> timestamp)
        => Bound(items, time, true, timestamp);
    public static int LowerBound<T>(IReadOnlyList<T> items, double time, Func<T, double> timestamp)
        => Bound(items, time, false, timestamp);
    private static int Bound<T>(IReadOnlyList<T> items, double time, bool inclusive, Func<T, double> timestamp)
    {
        if (items is DirectorReplayList<T> paged && paged.TrySearchTime(time, inclusive, out int index)) return index;
        int low = 0, high = items.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            double at = timestamp(items[middle]);
            if (inclusive ? at <= time : at < time) low = middle + 1; else high = middle;
        }
        return low;
    }
}
