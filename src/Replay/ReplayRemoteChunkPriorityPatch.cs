using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.Common;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using VSDirector.Networking;
using VSDirector.Runtime;

namespace VSDirector.Replay;

// ClientWorldMap prioritizes new chunk meshes from the real player position.
// A detached director camera needs the same priority around its observation
// points or received terrain can remain behind the normal mesh queue.
internal static class ReplayRemoteChunkPriorityPatch
{
    private const int PriorityRadiusChunks = 5;
    private static readonly FieldInfo? GameField = AccessTools.Field(typeof(ClientWorldMap), "game");
    private static readonly FieldInfo? DirtyChunksField = AccessTools.Field(typeof(ClientMain), "dirtyChunks");
    private static readonly FieldInfo? DirtyChunksLockField = AccessTools.Field(typeof(ClientMain), "dirtyChunksLock");
    private static readonly FieldInfo? ClientSystemGameField = AccessTools.Field(typeof(ClientSystem), "game");
    private static readonly FieldInfo? ClientChunkMapChunkField = AccessTools.Field(typeof(ClientChunk), "clientmapchunk");

    internal static MethodBase? TargetMethod { get; private set; }

    internal static MethodBase? MapChunkTargetMethod { get; private set; }

    internal static bool TryPatch(Harmony harmony, ILogger logger)
    {
        try
        {
            MethodInfo? target = AccessTools.Method(
                typeof(ClientWorldMap),
                "loadChunkMT",
                new[] { typeof(Packet_ServerChunk), typeof(ClientChunk) });
            MethodInfo? postfix = AccessTools.Method(
                typeof(ReplayRemoteChunkPriorityPatch),
                nameof(Postfix));
            MethodInfo? mapChunkTarget = AccessTools.Method(
                typeof(SystemNetworkProcess),
                "ProcessInBackground",
                new[] { typeof(Packet_Server) });
            MethodInfo? mapChunkPostfix = AccessTools.Method(
                typeof(ReplayRemoteChunkPriorityPatch),
                nameof(MapChunkPostfix));
            if (target is null
                || postfix is null
                || mapChunkTarget is null
                || mapChunkPostfix is null
                || ClientSystemGameField is null
                || ClientChunkMapChunkField is null)
            {
                logger.Warning("VS Director: remote chunk mesh-priority target was not available.");
                return false;
            }

            harmony.Patch(
                target,
                postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
            harmony.Patch(
                mapChunkTarget,
                postfix: new HarmonyMethod(mapChunkPostfix) { priority = Priority.Last });
            TargetMethod = target;
            MapChunkTargetMethod = mapChunkTarget;
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning("VS Director: remote chunk mesh-priority patch failed: {0}", exception.Message);
            return false;
        }
    }

    private static void Postfix(ClientWorldMap __instance, Packet_ServerChunk p)
    {
        if (DirectorReplayRuntime.Manager?.IsServerRecordingWorldPlaybackActive == true && GameField?.GetValue(__instance) is ClientMain game)
        {
            int x = p.X, y = p.Y, z = p.Z;
            game.EnqueueMainThreadTask(() => DirectorReplayRuntime.Manager?.ObserveServerRecordingChunkLoad(x, y, z), "vsdirector-session-loaded");
        }
        if (!ShouldPrioritize(p.X, p.Y, p.Z))
        {
            return;
        }

        PrioritizeExistingChunk(__instance, p.X, p.Y, p.Z);
    }

    private static void MapChunkPostfix(SystemNetworkProcess __instance, Packet_Server packet)
    {
        if (packet.Id != 17
            || packet.MapChunk is not { } mapChunkPacket
            || !IsPendingRemoteColumn(mapChunkPacket.ChunkX, mapChunkPacket.ChunkZ)
            || ClientSystemGameField?.GetValue(__instance) is not ClientMain game)
        {
            return;
        }

        int chunkX = mapChunkPacket.ChunkX;
        int chunkZ = mapChunkPacket.ChunkZ;
        game.EnqueueMainThreadTask(
            () => BackfillRemoteMapChunk(game.WorldMap, chunkX, chunkZ),
            "vsdirector-backfill-remote-mapchunk");
    }

    private static void BackfillRemoteMapChunk(
        ClientWorldMap worldMap,
        int chunkX,
        int chunkZ)
    {
        object? mapChunk = worldMap.GetMapChunk(chunkX, chunkZ);
        if (mapChunk is null)
        {
            return;
        }

        HashSet<int> dimensions = new();
        if (DirectorCameraRuntime.PendingRemoteCameraPose is { } camera)
        {
            dimensions.Add(camera.Dimension);
        }
        if (DirectorCameraRuntime.PendingRemoteTargetPose is { } target)
        {
            dimensions.Add(target.Dimension);
        }

        int chunkSize = worldMap.ClientChunkSize;
        int chunkCountY = (worldMap.MapSizeY + chunkSize - 1) / chunkSize;
        foreach (int dimension in dimensions)
        {
            int dimensionOffset = dimension * DirectorChunkWindow.DimensionHeight / chunkSize;
            for (int chunkY = 0; chunkY < chunkCountY; chunkY++)
            {
                int internalChunkY = chunkY + dimensionOffset;
                if (worldMap.GetChunk(chunkX, internalChunkY, chunkZ) is not ClientChunk
                    {
                        LoadedFromServer: true,
                        MapChunk: null
                    } chunk)
                {
                    continue;
                }

                ClientChunkMapChunkField?.SetValue(chunk, mapChunk);
                PrioritizeExistingChunk(worldMap, chunkX, internalChunkY, chunkZ);
            }
        }
    }

    private static bool IsPendingRemoteColumn(int chunkX, int chunkZ)
        => DirectorCameraRuntime.PendingRemoteCameraPose is { } camera
               && IsNear(
                   camera.X,
                   camera.Z,
                   camera.Dimension,
                   chunkX,
                   chunkZ,
                   camera.Dimension)
            || DirectorCameraRuntime.PendingRemoteTargetPose is { } target
               && IsNear(
                   target.X,
                   target.Z,
                   target.Dimension,
                   chunkX,
                   chunkZ,
                   target.Dimension);

    internal static void PrioritizeExistingChunk(
        ClientWorldMap worldMap,
        int chunkX,
        int chunkY,
        int chunkZ,
        Action? onRetessellated = null)
    {
        ReplayChunkTessellationRevisionPatch.BeginRedraw(
            worldMap,
            chunkX,
            chunkY,
            chunkZ);
        worldMap.MarkChunkDirty(
            chunkX,
            chunkY,
            chunkZ,
            priority: true,
            sunRelight: false,
            OnRetesselated: onRetessellated,
            fireEvent: false);

        // ClientWorldMap only honors priority=true near the real player. A
        // detached replay camera can be many chunks away, so promote the
        // already-coalesced redraw explicitly after registering its callback.
        long chunkIndex = worldMap.ChunkIndex3D(chunkX, chunkY, chunkZ);
        if (GameField?.GetValue(worldMap) is ClientMain game
            && DirtyChunksField?.GetValue(game) is UniqueQueue<long> normalQueue
            && DirtyChunksLockField?.GetValue(game) is object queueLock)
        {
            lock (queueLock)
            {
                RemoveQueuedChunkIfPresent(normalQueue, chunkIndex);
            }
        }

        worldMap.SetChunkDirty(chunkIndex, priority: true);
    }

    internal static void RetessellateExistingChunkInOrder(
        ClientWorldMap worldMap,
        int chunkX,
        int chunkY,
        int chunkZ,
        Action? onRetessellated = null,
        bool advanceRevision = true)
    {
        // A newer priority result can upload before an older normal result for
        // the same chunk. The older mesh then wins last and can leave an
        // animated cabinet with neither its static nor dynamic doors visible.
        // Keep replay state handoffs on the normal queue, whose per-chunk merge
        // preserves latest-wins ordering, and reserve priority promotion for
        // initial remote chunk availability.
        if (advanceRevision)
        {
            ReplayChunkTessellationRevisionPatch.BeginRedraw(
                worldMap,
                chunkX,
                chunkY,
                chunkZ);
        }
        worldMap.MarkChunkDirty(
            chunkX,
            chunkY,
            chunkZ,
            priority: false,
            sunRelight: false,
            OnRetesselated: onRetessellated,
            fireEvent: false);
    }

    internal static void PrioritizeExistingWindow(
        ClientWorldMap worldMap,
        CameraPreset pose,
        int radiusChunks)
    {
        if (!double.IsFinite(pose.X) || !double.IsFinite(pose.Z) || pose.Dimension < 0)
        {
            return;
        }

        int chunkSize = worldMap.ClientChunkSize;
        int centerChunkX = (int)Math.Floor(pose.X / chunkSize);
        int centerChunkZ = (int)Math.Floor(pose.Z / chunkSize);
        int radius = Math.Max(0, radiusChunks);
        int radiusSquared = radius * radius;
        int chunkCountY = (worldMap.MapSizeY + chunkSize - 1) / chunkSize;
        int dimensionOffset = pose.Dimension * DirectorChunkWindow.DimensionHeight / chunkSize;
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
                    int x = centerChunkX + offsetX;
                    int y = chunkY + dimensionOffset;
                    int z = centerChunkZ + offsetZ;
                    if (worldMap.GetChunk(x, y, z) is ClientChunk { LoadedFromServer: true })
                    {
                        PrioritizeExistingChunk(worldMap, x, y, z);
                    }
                }
            }
        }
    }

    internal static bool IsWindowRendered(
        ClientWorldMap worldMap,
        CameraPreset pose,
        int radiusChunks)
    {
        if (!double.IsFinite(pose.X) || !double.IsFinite(pose.Z))
        {
            return false;
        }

        int chunkSize = worldMap.ClientChunkSize;
        return DirectorChunkWindow.IsReady(
            (int)Math.Floor(pose.X / chunkSize),
            (int)Math.Floor(pose.Z / chunkSize),
            pose.Dimension,
            radiusChunks,
            worldMap.MapSizeY,
            chunkSize,
            (x, y, z) => worldMap.GetChunk(x, y, z) is ClientChunk
            {
                LoadedFromServer: true,
                MapChunk: not null
            } && worldMap.IsChunkRendered(x, y, z));
    }

    private static void RemoveQueuedChunkIfPresent(UniqueQueue<long> queue, long chunkIndex)
    {
        // UniqueQueue.Remove unconditionally dequeues its backing queue. The
        // redraw can already be in the priority queue (or have been consumed)
        // when this promotion runs, so only remove a confirmed normal entry.
        if (queue.Contains(chunkIndex))
        {
            queue.Remove(chunkIndex);
        }
    }

    internal static bool ShouldPrioritize(int chunkX, int chunkY, int chunkZ)
    {
        if (DirectorChunkPreloadRuntime.Contains(chunkX, chunkY, chunkZ))
        {
            return true;
        }
        if (DirectorReplayRuntime.Manager?.ShouldPrioritizePreloadedChunk(chunkX, chunkY, chunkZ) == true)
        {
            return true;
        }

        int dimension = Math.Max(0, chunkY / (DirectorChunkWindow.DimensionHeight / 32));
        if (DirectorCameraRuntime.PendingRemoteCameraPose is { } pendingCamera
            && IsNear(
                pendingCamera.X,
                pendingCamera.Z,
                pendingCamera.Dimension,
                chunkX,
                chunkZ,
                dimension))
        {
            return true;
        }
        if (DirectorCameraRuntime.PendingRemoteTargetPose is { } pendingTarget
            && IsNear(
                pendingTarget.X,
                pendingTarget.Z,
                pendingTarget.Dimension,
                chunkX,
                chunkZ,
                dimension))
        {
            return true;
        }
        if (DirectorCameraRuntime.Controller?.CurrentPose is { } camera
            && IsNear(camera.X, camera.Z, camera.Dimension, chunkX, chunkZ, dimension))
        {
            return true;
        }

        return DirectorReplayRuntime.Manager?.TryGetPlaybackAnchorPose(out CameraPreset replay) == true
            && IsNear(replay.X, replay.Z, replay.Dimension, chunkX, chunkZ, dimension);
    }

    private static bool IsNear(
        double x,
        double z,
        int poseDimension,
        int chunkX,
        int chunkZ,
        int chunkDimension)
    {
        if (poseDimension != chunkDimension || !double.IsFinite(x) || !double.IsFinite(z))
        {
            return false;
        }

        int centerX = (int)Math.Floor(x / 32d);
        int centerZ = (int)Math.Floor(z / 32d);
        int offsetX = chunkX - centerX;
        int offsetZ = chunkZ - centerZ;
        return offsetX * offsetX + offsetZ * offsetZ <= PriorityRadiusChunks * PriorityRadiusChunks;
    }
}
