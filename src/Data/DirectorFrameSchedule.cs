namespace VSDirector.Data;

public static class DirectorFrameSchedule
{
    public static long TargetFrameCount(TimeSpan elapsed, int framesPerSecond)
    {
        int fps = DirectorTimecode.NormalizeFrameRate(framesPerSecond);
        double seconds = Math.Max(0d, elapsed.TotalSeconds);
        if (seconds <= 0d)
        {
            return 0L;
        }

        double frames = seconds * fps;
        return Math.Max(1L, checked((long)Math.Ceiling(frames - 0.000000001d)));
    }

    public static int FramesDue(long targetFrameCount, long capturedFrameCount, int maximum)
    {
        if (maximum <= 0 || targetFrameCount <= capturedFrameCount)
        {
            return 0;
        }

        return (int)Math.Min(maximum, Math.Min(int.MaxValue, targetFrameCount - capturedFrameCount));
    }
}
