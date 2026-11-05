using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.Server;
using VSDirector.Data;

namespace VSDirector.Networking;

internal readonly record struct DirectorAnchorSoundColumn(
    int X,
    int Z,
    int Dimension);

internal static class DirectorAnchorChunkProtection
{
    private const int ChunkSize = 32;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, HashSet<long>> ProtectedChunks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<long>> EntityTrackingChunks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<DirectorAnchorSoundColumn>> SoundColumns =
        new(StringComparer.Ordinal);

    internal static void Set(
        string playerUid,
        IEnumerable<long> chunkIds,
        IEnumerable<long> entityTrackingChunkIds)
        => Set(
            playerUid,
            chunkIds,
            entityTrackingChunkIds,
            Array.Empty<DirectorAnchorSoundColumn>());

    internal static void Set(
        string playerUid,
        IEnumerable<long> chunkIds,
        IEnumerable<long> entityTrackingChunkIds,
        IEnumerable<DirectorAnchorSoundColumn> soundColumns)
    {
        lock (Sync)
        {
            ProtectedChunks[playerUid] = chunkIds.ToHashSet();
            EntityTrackingChunks[playerUid] = entityTrackingChunkIds.ToHashSet();
            SoundColumns[playerUid] = soundColumns.ToHashSet();
        }
    }

    internal static void Remove(string playerUid)
    {
        lock (Sync)
        {
            ProtectedChunks.Remove(playerUid);
            EntityTrackingChunks.Remove(playerUid);
            SoundColumns.Remove(playerUid);
        }
    }

    internal static List<long>? DetachFrom(ConnectedClient client)
    {
        string playerUid = client.Player?.PlayerUID ?? string.Empty;
        if (playerUid.Length == 0 || client.ChunkSent is not { Count: > 0 })
        {
            return null;
        }

        HashSet<long>? protectedChunks;
        lock (Sync)
        {
            if (!ProtectedChunks.TryGetValue(playerUid, out HashSet<long>? current))
            {
                return null;
            }

            protectedChunks = current;
        }

        List<long>? detached = null;
        foreach (long chunkId in protectedChunks)
        {
            if (!client.ChunkSent.Remove(chunkId))
            {
                continue;
            }

            detached ??= new List<long>();
            detached.Add(chunkId);
        }

        return detached;
    }

    internal static void RestoreTo(ConnectedClient client, List<long>? detached)
    {
        if (detached is { Count: > 0 })
        {
            client.ChunkSent.UnionWith(detached);
        }
    }

    internal static bool IsProtected(ConnectedClient client, long chunkId)
    {
        string playerUid = client.Player?.PlayerUID ?? string.Empty;
        return IsProtected(playerUid, chunkId);
    }

    internal static bool IsProtected(string playerUid, long chunkId)
    {
        lock (Sync)
        {
            return playerUid.Length > 0
                && ProtectedChunks.TryGetValue(playerUid, out HashSet<long>? chunks)
                && chunks.Contains(chunkId);
        }
    }

    internal static bool ShouldTrackEntity(ConnectedClient client, long chunkId)
    {
        string playerUid = client.Player?.PlayerUID ?? string.Empty;
        return ShouldTrackEntity(playerUid, chunkId);
    }

    internal static bool ShouldTrackEntity(string playerUid, long chunkId)
    {
        lock (Sync)
        {
            return playerUid.Length > 0
                && EntityTrackingChunks.TryGetValue(playerUid, out HashSet<long>? chunks)
                && chunks.Contains(chunkId);
        }
    }

    internal static bool ShouldReceiveRemoteSound(
        ConnectedClient client,
        double x,
        double y,
        double z)
    {
        string playerUid = client.Player?.PlayerUID ?? string.Empty;
        return ShouldReceiveRemoteSound(playerUid, x, y, z);
    }

    internal static bool ShouldReceiveRemoteSound(
        string playerUid,
        double x,
        double y,
        double z)
    {
        if (playerUid.Length == 0
            || !double.IsFinite(x)
            || !double.IsFinite(y)
            || !double.IsFinite(z))
        {
            return false;
        }

        double chunkXValue = Math.Floor(x / ChunkSize);
        double chunkZValue = Math.Floor(z / ChunkSize);
        double dimensionValue = Math.Floor(y / DirectorChunkWindow.DimensionHeight);
        if (chunkXValue is < int.MinValue or > int.MaxValue
            || chunkZValue is < int.MinValue or > int.MaxValue
            || dimensionValue is < 0d or > int.MaxValue)
        {
            return false;
        }

        DirectorAnchorSoundColumn column = new(
            (int)chunkXValue,
            (int)chunkZValue,
            (int)dimensionValue);
        lock (Sync)
        {
            return SoundColumns.TryGetValue(
                    playerUid,
                    out HashSet<DirectorAnchorSoundColumn>? columns)
                && columns.Contains(column);
        }
    }
}

internal static class DirectorAnchorChunkUnloadPatch
{
    internal static MethodBase? TargetMethod { get; private set; }

    internal static bool TryPatch(Harmony harmony, ILogger logger)
    {
        try
        {
            Type? unloadSystem = AccessTools.TypeByName("Vintagestory.Server.ServerSystemUnloadChunks");
            MethodInfo? target = unloadSystem is null
                ? null
                : AccessTools.Method(
                    unloadSystem,
                    "SendOutOfRangeChunkUnloads",
                    new[] { typeof(ConnectedClient) });
            MethodInfo? prefix = AccessTools.Method(typeof(DirectorAnchorChunkUnloadPatch), nameof(Prefix));
            MethodInfo? postfix = AccessTools.Method(typeof(DirectorAnchorChunkUnloadPatch), nameof(Postfix));
            if (target is null || prefix is null || postfix is null)
            {
                logger.Warning("VS Director: remote chunk unload patch target was not available.");
                return false;
            }

            harmony.Patch(
                target,
                prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
            TargetMethod = target;
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning("VS Director: remote chunk unload patch failed: {0}", exception.Message);
            return false;
        }
    }

    private static void Prefix(ConnectedClient __0, out List<long>? __state)
        => __state = DirectorAnchorChunkProtection.DetachFrom(__0);

    private static void Postfix(ConnectedClient __0, List<long>? __state)
        => DirectorAnchorChunkProtection.RestoreTo(__0, __state);
}

internal static class DirectorAnchorEntityTrackingPatch
{
    internal static MethodBase? TargetMethod { get; private set; }

    internal static bool TryPatch(Harmony harmony, ILogger logger)
    {
        try
        {
            Type? physicsType = AccessTools.TypeByName("Vintagestory.Server.PhysicsManager");
            MethodInfo? target = physicsType is null
                ? null
                : AccessTools.Method(
                    physicsType,
                    "UpdateTrackedEntityState",
                    new[] { typeof(Entity), typeof(List<ConnectedClient>), typeof(int) });
            MethodInfo? postfix = AccessTools.Method(typeof(DirectorAnchorEntityTrackingPatch), nameof(Postfix));
            if (target is null || postfix is null)
            {
                logger.Warning("VS Director: remote entity tracking patch target was not available.");
                return false;
            }

            harmony.Patch(
                target,
                postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
            TargetMethod = target;
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning("VS Director: remote entity tracking patch failed: {0}", exception.Message);
            return false;
        }
    }

    private static void Postfix(
        Entity entity,
        List<ConnectedClient> clients,
        int zeroBasedThreadNum,
        ref bool __result)
    {
        if (entity is null || clients is null || entity.InChunkIndex3d < 0)
        {
            return;
        }

        foreach (ConnectedClient client in clients)
        {
            if (!DirectorAnchorChunkProtection.ShouldTrackEntity(client, entity.InChunkIndex3d)
                || !client.DidSendChunk(entity.InChunkIndex3d)
                || client.threadedTrackedEntities is not { } trackedByThread
                || zeroBasedThreadNum < 0
                || zeroBasedThreadNum >= trackedByThread.Length)
            {
                continue;
            }

            List<Entity> tracked = trackedByThread[zeroBasedThreadNum];
            if (!tracked.Contains(entity))
            {
                tracked.Add(entity);
            }

            entity.IsTracked = 2;
            entity.NearestPlayerDistance = 0f;
            if (!entity.AlwaysActive && entity.State != EnumEntityState.Active)
            {
                entity.State = EnumEntityState.Active;
                __result = true;
            }
        }
    }
}

internal static class DirectorAnchorSoundBroadcastPatch
{
    private delegate void SendSoundDelegate(
        ServerMain server,
        IServerPlayer player,
        AssetLocation location,
        double x,
        double y,
        double z,
        float pitch,
        float range,
        float volume,
        EnumSoundType soundType);

    private static SendSoundDelegate? sendSound;
    private static ILogger? logger;
    private static int sendFailureLogged;
    private static readonly object RelayedSoundsSync = new();
    private static readonly Dictionary<RelayedSoundKey, long> RelayedSounds = new();

    private readonly record struct RelayedSoundKey(
        int ClientId,
        string Location,
        int BlockX,
        int BlockY,
        int BlockZ);

    internal static MethodBase? TargetMethod { get; private set; }

    internal static bool TryPatch(Harmony harmony, ILogger patchLogger)
    {
        try
        {
            MethodInfo? target = AccessTools.Method(
                typeof(ServerMain),
                "PlaySoundAtExceptPlayer",
                new[]
                {
                    typeof(AssetLocation),
                    typeof(double),
                    typeof(double),
                    typeof(double),
                    typeof(int?),
                    typeof(float),
                    typeof(float),
                    typeof(float),
                    typeof(EnumSoundType)
                });
            MethodInfo? sender = AccessTools.Method(
                typeof(ServerMain),
                "SendSound",
                new[]
                {
                    typeof(IServerPlayer),
                    typeof(AssetLocation),
                    typeof(double),
                    typeof(double),
                    typeof(double),
                    typeof(float),
                    typeof(float),
                    typeof(float),
                    typeof(EnumSoundType)
                });
            MethodInfo? postfix = AccessTools.Method(
                typeof(DirectorAnchorSoundBroadcastPatch),
                nameof(Postfix));
            if (target is null || sender is null || postfix is null)
            {
                patchLogger.Warning("VS Director: remote sound broadcast patch target was not available.");
                return false;
            }

            sendSound = sender.CreateDelegate<SendSoundDelegate>();
            logger = patchLogger;
            Interlocked.Exchange(ref sendFailureLogged, 0);
            harmony.Patch(
                target,
                postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
            TargetMethod = target;
            return true;
        }
        catch (Exception exception)
        {
            patchLogger.Warning("VS Director: remote sound broadcast patch failed: {0}", exception.Message);
            return false;
        }
    }

    private static void Postfix(
        ServerMain __instance,
        AssetLocation __0,
        double __1,
        double __2,
        double __3,
        int? __4,
        float __5,
        float __6,
        float __7,
        EnumSoundType __8)
    {
        if (__0 is null || sendSound is null)
        {
            return;
        }

        if (__4 is int sourceClientId
            && WasClientRelayed(sourceClientId, __0, __1, __2, __3, __instance.ElapsedMilliseconds))
        {
            return;
        }

        Forward(
            __instance,
            __4,
            __0,
            __1,
            __2,
            __3,
            __5,
            __6,
            __7,
            __8);
    }

    internal static void ForwardClientSound(
        ServerMain server,
        int sourceClientId,
        AssetLocation location,
        double x,
        double y,
        double z,
        int blockX,
        int blockY,
        int blockZ,
        float pitch,
        float range,
        float volume,
        EnumSoundType soundType)
    {
        if (sendSound is null || location is null)
        {
            return;
        }

        RememberClientRelay(
            sourceClientId,
            location,
            blockX,
            blockY,
            blockZ,
            server.ElapsedMilliseconds);
        Forward(
            server,
            sourceClientId,
            location,
            x,
            y,
            z,
            pitch,
            range,
            volume,
            soundType);
    }

    private static void Forward(
        ServerMain server,
        int? excludedClientId,
        AssetLocation location,
        double x,
        double y,
        double z,
        float pitch,
        float range,
        float volume,
        EnumSoundType soundType)
    {
        SendSoundDelegate? sender = sendSound;
        if (sender is null)
        {
            return;
        }

        float rangeSquared = range * range;
        foreach (ConnectedClient client in server.Clients.Values)
        {
            if ((excludedClientId.HasValue && client.Id == excludedClientId.Value)
                || client.State != EnumClientState.Playing
                || client.Player is null
                || client.Position.InRangeOf(x, y, z, rangeSquared)
                || !DirectorAnchorChunkProtection.ShouldReceiveRemoteSound(
                    client,
                    x,
                    y,
                    z))
            {
                continue;
            }

            try
            {
                sender(
                    server,
                    client.Player,
                    location,
                    x,
                    y,
                    z,
                    pitch,
                    range,
                    volume,
                    soundType);
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref sendFailureLogged, 1) == 0)
                {
                    logger?.Warning(
                        "VS Director: a remote sound could not be forwarded: {0}",
                        exception.GetBaseException().Message);
                }
            }
        }
    }

    private static void RememberClientRelay(
        int clientId,
        AssetLocation location,
        int blockX,
        int blockY,
        int blockZ,
        long now)
    {
        RelayedSoundKey key = new(
            clientId,
            location.ToString(),
            blockX,
            blockY,
            blockZ);
        lock (RelayedSoundsSync)
        {
            RelayedSounds[key] = now;
            if (RelayedSounds.Count > 1024)
            {
                foreach (RelayedSoundKey stale in RelayedSounds
                    .Where(pair => now - pair.Value > 2000)
                    .Select(pair => pair.Key)
                    .ToArray())
                {
                    RelayedSounds.Remove(stale);
                }
            }
        }
    }

    private static bool WasClientRelayed(
        int clientId,
        AssetLocation location,
        double x,
        double y,
        double z,
        long now)
    {
        RelayedSoundKey key = new(
            clientId,
            location.ToString(),
            (int)Math.Floor(x),
            (int)Math.Floor(y),
            (int)Math.Floor(z));
        lock (RelayedSoundsSync)
        {
            return RelayedSounds.TryGetValue(key, out long sentAt)
                && now - sentAt <= 1000;
        }
    }
}
