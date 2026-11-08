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
            MapSizeY = capi.World.MapSizeY
        };
        int[] solid = new int[ChunkBlockCount];
        int[] fluid = new int[ChunkBlockCount];
        HashSet<BlockPos> capturedBlockEntities = new();

        for (int chunkZ = centerChunkZ - radius; chunkZ <= centerChunkZ + radius; chunkZ++)
        {
            for (int chunkX = centerChunkX - radius; chunkX <= centerChunkX + radius; chunkX++)
            {
                for (int chunkY = 0; chunkY < verticalChunks; chunkY++)
                {
                    int internalChunkY = chunkY + dimension * GlobalConstants.DimensionSizeInChunks;
                    IWorldChunk? chunk = game.WorldMap.GetChunk(chunkX, internalChunkY, chunkZ);
                    if (chunk is null || chunk.Disposed || !chunk.Unpack_ReadOnly() && chunk.Data is null)
                    {
                        continue;
                    }

                    IChunkBlocks? data = chunk.Data;
                    if (data is null)
                    {
                        continue;
                    }

                    data.TakeBulkReadLock();
                    try
                    {
                        for (int index = 0; index < ChunkBlockCount; index++)
                        {
                            solid[index] = data.GetBlockIdUnsafe(index);
                            fluid[index] = data.GetFluid(index);
                        }
                    }
                    finally
                    {
                        data.ReleaseBulkReadLock();
                    }

                    snapshot.Chunks.Add(new DirectorReplayChunkSnapshot
                    {
                        ChunkX = chunkX,
                        ChunkY = chunkY,
                        ChunkZ = chunkZ,
                        SolidRuns = DirectorReplayChunkCodec.Encode(solid),
                        FluidRuns = DirectorReplayChunkCodec.Encode(fluid)
                    });

                    foreach ((BlockPos position, BlockEntity blockEntity) in chunk.BlockEntities)
                    {
                        BlockPos normalized = position.Copy();
                        if (normalized.dimension != dimension)
                        {
                            normalized.dimension = dimension;
                            normalized.Y %= BlockPos.DimensionBoundary;
                        }

                        if (!capturedBlockEntities.Add(normalized))
                        {
                            continue;
                        }

                        try
                        {
                            snapshot.BlockEntities.Add(
                                DirectorReplaySerialization.CaptureBlockEntity(capi, normalized, blockEntity));
                        }
                        catch (Exception exception)
                        {
                            capi.Logger.Warning(
                                "VS Director: could not snapshot block entity at {0}: {1}",
                                normalized,
                                exception.Message);
                        }
                    }
                }
            }
        }

        return snapshot;
    }
}
