namespace VSDirector.Data;

/// <summary>Expands at most one delayed baseline at a time, rather than retaining millions of block events.</summary>
public static class DirectorReplayDeferredChunks
{
    public static IEnumerable<DirectorReplayBlockEvent> Events(DirectorReplayWorldSnapshot world, bool includeAir = false)
    {
        foreach (var chunk in world.Chunks.Where(c => c.CapturedAtSeconds > 0).OrderBy(c => c.CapturedAtSeconds))
        {
            int[] solid = DirectorReplayChunkCodec.Decode(chunk.SolidRuns, 32768);
            int[] fluid = DirectorReplayChunkCodec.Decode(chunk.FluidRuns, 32768);
            for (int i = 0; i < 32768; i++)
            {
                if (!includeAir && solid[i] == 0 && fluid[i] == 0) continue;
                yield return new()
                {
                    TimeSeconds = chunk.CapturedAtSeconds, Dimension = world.Dimension,
                    X = chunk.ChunkX * 32 + i % 32, Y = chunk.ChunkY * 32 + i / 1024, Z = chunk.ChunkZ * 32 + i / 32 % 32,
                    SolidBlockId = solid[i], FluidBlockId = fluid[i]
                };
            }
        }
    }
}
