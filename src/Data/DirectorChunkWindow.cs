namespace VSDirector.Data;

public static class DirectorChunkWindow
{
    public const int DimensionHeight = 32768;

    public static double ToAbsoluteY(double localY, int dimension)
        => localY + dimension * (double)DimensionHeight;

    public static bool IsReady(
        int centerChunkX,
        int centerChunkZ,
        int dimension,
        int radiusChunks,
        int mapSizeY,
        int chunkSize,
        Func<int, int, int, bool> hasChunk)
    {
        ArgumentNullException.ThrowIfNull(hasChunk);
        if (chunkSize <= 0 || mapSizeY <= 0 || dimension < 0)
        {
            return false;
        }

        int radius = Math.Max(0, radiusChunks);
        int radiusSquared = radius * radius;
        int chunkCountY = (mapSizeY + chunkSize - 1) / chunkSize;
        int dimensionOffset = dimension * DimensionHeight / chunkSize;
        for (int offsetX = -radius; offsetX <= radius; offsetX++)
        {
            for (int offsetZ = -radius; offsetZ <= radius; offsetZ++)
            {
                if (offsetX * offsetX + offsetZ * offsetZ > radiusSquared)
                {
                    continue;
                }

                for (int chunkY = 0; chunkY < chunkCountY; chunkY++)
                {
                    if (!hasChunk(
                            centerChunkX + offsetX,
                            chunkY + dimensionOffset,
                            centerChunkZ + offsetZ))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
