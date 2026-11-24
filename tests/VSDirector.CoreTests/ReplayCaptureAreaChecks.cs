using System.Text.Json;
using VSDirector.Data;

internal static class ReplayCaptureAreaChecks
{
    internal static void Run()
    {
        var snapshot = new DirectorReplayWorldSnapshot
        {
            CenterChunkX = 15999, CenterChunkZ = 15998, RadiusChunks = 4, MapSizeY = 256, FollowCaptureArea = true
        };
        for (int z = 15994; z <= 16002; z++)
            for (int x = 15995; x <= 16003; x++)
                for (int y = 0; y < 8; y++) snapshot.Chunks.Add(new() { ChunkX = x, ChunkY = y, ChunkZ = z });
        var missing = DirectorReplayCaptureArea.MissingChunks(snapshot, [(511974.6, 113, 511778.8, 0)], 4);
        Check.Equal((15999, 3, 15993), missing[0]);
        Check.False(missing.Any(c => c.Z >= 15994));
        var both = DirectorReplayCaptureArea.MissingChunks(snapshot,
            [(511974.6, 113, 511778.8, 0), (511974.6, 113, 511778.8, 0)], 4);
        Check.Equal(missing.Count, both.Count);
        Check.Equal(0, DirectorReplayCaptureArea.MissingChunks(snapshot, [(1, 1, 1, 1), (double.NaN, 1, 1, 0)], 4).Count);
        var negative = DirectorReplayCaptureArea.MissingChunks(new() { MapSizeY = 32 }, [(-.1, 0, -.1, 0)], 0);
        Check.Equal((-1, 0, -1), negative.Single());
        var doc = new DirectorReplayDocument { DurationSeconds = 10, WorldSnapshot = snapshot };
        Check.True(doc.DeepClone().HasHistoricalBaselines);
        var reloaded = JsonSerializer.Deserialize<DirectorReplayDocument>(JsonSerializer.Serialize(doc))!;
        Check.True(DirectorReplayMath.Normalize(reloaded).HasHistoricalBaselines);
        reloaded.WorldSnapshot.FollowCaptureArea = false; reloaded.SchemaVersion = 25;
        Check.False(DirectorReplayMath.Normalize(reloaded).HasHistoricalBaselines);
    }
}
