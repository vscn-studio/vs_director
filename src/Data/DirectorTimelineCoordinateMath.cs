namespace VSDirector.Data;

public static class DirectorTimelineCoordinateMath
{
    private const double MinimumPixelsPerTick = 0.000001d;

    public static double PositivePixelsPerTick(double pixelsPerTick)
        => double.IsFinite(pixelsPerTick) && pixelsPerTick > MinimumPixelsPerTick
            ? pixelsPerTick
            : MinimumPixelsPerTick;

    public static bool TryDragTime(double originalTime, double pressX, double mouseX,
        double pixelsPerSecond, double scale, bool dragging, out double time)
    {
        time = originalTime;
        if (!dragging && Math.Abs(mouseX - pressX) < 6 * Math.Max(.001, scale)) return false;
        time += (mouseX - pressX) / PositivePixelsPerTick(pixelsPerSecond);
        return true;
    }
}
