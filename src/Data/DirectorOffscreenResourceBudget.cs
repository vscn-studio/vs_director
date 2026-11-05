namespace VSDirector.Data;

public static class DirectorOffscreenResourceBudget
{
    public const long MaximumEstimatedGpuBytes = 2L * 1024L * 1024L * 1024L;

    private const int ColorBytesPerPixel = 4;
    private const int DepthBytesPerPixel = 4;
    private const int PboBytesPerPixel = 4;

    public static long EstimateGpuBytes(
        int viewCount,
        int width,
        int height,
        int pboRingSize)
    {
        if (viewCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewCount));
        }
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }
        if (pboRingSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pboRingSize));
        }

        return checked(
            (long)viewCount
            * width
            * height
            * (ColorBytesPerPixel + DepthBytesPerPixel + PboBytesPerPixel * pboRingSize));
    }

    public static bool FitsDefaultBudget(
        int viewCount,
        int width,
        int height,
        int pboRingSize,
        out long estimatedBytes)
    {
        estimatedBytes = EstimateGpuBytes(viewCount, width, height, pboRingSize);
        return estimatedBytes <= MaximumEstimatedGpuBytes;
    }
}
