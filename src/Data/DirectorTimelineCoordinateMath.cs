namespace VSDirector.Data;

public static class DirectorTimelineCoordinateMath
{
    private const double MinimumPixelsPerTick = 0.000001d;

    public static double PositivePixelsPerTick(double pixelsPerTick)
        => double.IsFinite(pixelsPerTick) && pixelsPerTick > MinimumPixelsPerTick
            ? pixelsPerTick
            : MinimumPixelsPerTick;
}
