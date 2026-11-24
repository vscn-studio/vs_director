namespace VSDirector.Data;

/// <summary>Prioritize uncaptured chunks near the player and director camera as recording moves.</summary>
public static class DirectorReplayCaptureArea
{
    public static IReadOnlyList<(int X, int Y, int Z)> MissingChunks(DirectorReplayWorldSnapshot snapshot,
        IEnumerable<(double X, double Y, double Z, int Dimension)> centers, int radius)
    {
        radius = Math.Clamp(radius, 0, 16);
        var known = snapshot.Chunks.Select(c => (c.ChunkX, c.ChunkY, c.ChunkZ)).ToHashSet();
        var missing = new Dictionary<(int X, int Y, int Z), int>();
        int height = Math.Max(1, (snapshot.MapSizeY + 31) / 32);
        foreach (var center in centers)
        {
            if (center.Dimension != snapshot.Dimension || !double.IsFinite(center.X)
                || !double.IsFinite(center.Y) || !double.IsFinite(center.Z)) continue;
            int cx = (int)Math.Floor(center.X / 32), cy = (int)Math.Floor(center.Y / 32), cz = (int)Math.Floor(center.Z / 32);
            for (int z = cz - radius; z <= cz + radius; z++)
                for (int x = cx - radius; x <= cx + radius; x++)
                    for (int y = 0; y < height; y++)
                    {
                        var key = (x, y, z);
                        if (known.Contains(key)) continue;
                        int distance = Math.Abs(x - cx) + Math.Abs(z - cz) + Math.Abs(y - cy);
                        if (!missing.TryGetValue(key, out int previous) || distance < previous) missing[key] = distance;
                    }
        }
        return missing.OrderBy(item => item.Value).Select(item => item.Key).ToArray();
    }
}
