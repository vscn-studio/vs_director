using System.Buffers;
using VSDirector.Compatibility;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Data;

namespace VSDirector.Replay;

internal static class DirectorReplayWorldCapture
{
    private const int ChunkSize = GlobalConstants.ChunkSize;
    private const int ChunkBlockCount = ChunkSize * ChunkSize * ChunkSize;

    public static DirectorReplayWorldSnapshot Capture(
        ICoreClientAPI capi,
        ClientMain game,
        int requestedRadiusChunks,
        CameraPreset? cameraCenter = null)
    {
        if (capi.World.Player?.Entity is not { } player)
        {
            return new DirectorReplayWorldSnapshot();
        }

        bool useCameraCenter = cameraCenter is not null
            && double.IsFinite(cameraCenter.X)
            && double.IsFinite(cameraCenter.Z);
        int dimension = useCameraCenter ? cameraCenter!.Dimension : player.Pos.Dimension;
        double centerX = useCameraCenter ? cameraCenter!.X : player.Pos.X;
        double centerZ = useCameraCenter ? cameraCenter!.Z : player.Pos.Z;
        int centerChunkX = (int)Math.Floor(centerX / ChunkSize);
        int centerChunkZ = (int)Math.Floor(centerZ / ChunkSize);
        int radius = Math.Clamp(requestedRadiusChunks, 0, 16);
        int verticalChunks = Math.Max(1, (capi.World.MapSizeY + ChunkSize - 1) / ChunkSize);
        DirectorReplayWorldSnapshot snapshot = new()
        {
            Dimension = dimension,
            CenterChunkX = centerChunkX,
            CenterChunkZ = centerChunkZ,
            RadiusChunks = radius,
            MapSizeY = capi.World.MapSizeY,
            FollowCaptureArea = true
        };
        for (int chunkZ = centerChunkZ - radius; chunkZ <= centerChunkZ + radius; chunkZ++)
            for (int chunkX = centerChunkX - radius; chunkX <= centerChunkX + radius; chunkX++)
                for (int chunkY = 0; chunkY < verticalChunks; chunkY++)
                    CaptureChunk(capi, game, snapshot, chunkX, chunkY, chunkZ, 0);
        return snapshot;
    }

    // Shared by the initial snapshot, bounded moving-area capture and the
    // pre-mutation hook. Never retain references to mutable game chunk storage.
    internal static bool CaptureChunk(ICoreClientAPI capi, ClientMain game,
        DirectorReplayWorldSnapshot snapshot, int chunkX, int chunkY, int chunkZ, double time)
    {
        int dimension = snapshot.Dimension;
        IWorldChunk? chunk = game.WorldMap.GetChunk(chunkX,
            chunkY + dimension * GlobalConstants.DimensionSizeInChunks, chunkZ);
        if (chunk is null || chunk.Disposed || (!chunk.Unpack_ReadOnly() && chunk.Data is null)) return false;
        IChunkBlocks? data = chunk.Data;
        if (data is null) return false;
        int[] solid = ArrayPool<int>.Shared.Rent(ChunkBlockCount);
        int[] fluid = ArrayPool<int>.Shared.Rent(ChunkBlockCount);
        try
        {
            data.TakeBulkReadLock();
            try
            {
                for (int index = 0; index < ChunkBlockCount; index++)
                {
                    solid[index] = data.GetBlockIdUnsafe(index);
                    fluid[index] = data.GetFluid(index);
                }
            }
            finally { data.ReleaseBulkReadLock(); }
            snapshot.Chunks.Add(new DirectorReplayChunkSnapshot
            {
                ChunkX = chunkX, ChunkY = chunkY, ChunkZ = chunkZ, CapturedAtSeconds = time,
                SolidRuns = DirectorReplayChunkCodec.Encode(solid.AsSpan(0, ChunkBlockCount)),
                FluidRuns = DirectorReplayChunkCodec.Encode(fluid.AsSpan(0, ChunkBlockCount))
            });
        }
        finally
        {
            ArrayPool<int>.Shared.Return(solid);
            ArrayPool<int>.Shared.Return(fluid);
        }
        foreach (BlockEntity blockEntity in DirectorGameCompatibility.BlockEntities(chunk))
        {
            BlockPos position = blockEntity.Pos.Copy();
            if (position.dimension != dimension)
            {
                position.dimension = dimension;
                position.Y %= BlockPos.DimensionBoundary;
            }
            try { snapshot.BlockEntities.Add(DirectorReplaySerialization.CaptureBlockEntity(capi, position, blockEntity)); }
            catch (Exception exception)
            {
                capi.Logger.Warning("VS Director: could not snapshot block entity at {0}: {1}", position, exception.Message);
            }
        }
        return true;
    }
}
