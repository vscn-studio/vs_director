using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VSDirector.Replay;

// ClientChunk.quantityDrawn advances when background tessellation starts, not
// when that exact mesh is uploaded. Retessellation callbacks therefore cannot
// distinguish an old completed mesh from the latest requested replay state.
// Tag each generated mesh with the replay redraw revision it observed and drop
// obsolete meshes before they can replace newer GPU data.
internal static class ReplayChunkTessellationRevisionPatch
{
    private static readonly ConditionalWeakTable<ClientWorldMap, ChunkRevisionTable>
        RevisionTables = new();
    private static readonly ConditionalWeakTable<TesselatedChunk, TessellationTicket>
        TessellationTickets = new();
    private static readonly FieldInfo? TesselatorGameField =
        AccessTools.Field(typeof(ChunkTesselatorManager), "game");
    private static readonly MethodInfo? DisposeUnusedTessellationMethod =
        AccessTools.Method(typeof(TesselatedChunk), "UnusedDispose");
    private static readonly MethodInfo? MergeTesselatedChunkMethod =
        AccessTools.Method(
            typeof(TesselatedChunk),
            nameof(TesselatedChunk.MergeIfEqual),
            new[] { typeof(TesselatedChunk) });

    [ThreadStatic]
    private static TessellationTicket? pendingTessellation;
    [ThreadStatic]
    private static TessellationTicket? suppressedRetessellationEvent;

    private static ILogger? logger;

    internal static IReadOnlyList<MethodBase> Targets { get; private set; } =
        Array.Empty<MethodBase>();

    internal static bool TryPatch(Harmony harmony, ILogger patchLogger)
    {
        try
        {
            MethodInfo? tesselateChunk = AccessTools.Method(
                typeof(ChunkTesselatorManager),
                "TesselateChunk",
                new[]
                {
                    typeof(int),
                    typeof(int),
                    typeof(int),
                    typeof(bool),
                    typeof(bool),
                    typeof(bool).MakeByRefType()
                });
            ConstructorInfo? tesselatedChunkConstructor = AccessTools.Constructor(
                typeof(TesselatedChunk),
                Type.EmptyTypes);
            MethodInfo? addTesselatedChunk = AccessTools.Method(
                typeof(ChunkRenderer),
                "AddTesselatedChunk",
                new[] { typeof(TesselatedChunk), typeof(ClientChunk) });
            MethodInfo? mergeTesselatedChunk = MergeTesselatedChunkMethod;
            MethodInfo? triggerRetessellated = AccessTools.Method(
                typeof(ClientEventManager),
                "TriggerChunkRetesselated",
                new[] { typeof(Vec3i), typeof(ClientChunk) });
            MethodInfo? tesselatePrefix = AccessTools.Method(
                typeof(ReplayChunkTessellationRevisionPatch),
                nameof(TesselateChunkPrefix));
            MethodInfo? tesselateFinalizer = AccessTools.Method(
                typeof(ReplayChunkTessellationRevisionPatch),
                nameof(TesselateChunkFinalizer));
            MethodInfo? constructorPostfix = AccessTools.Method(
                typeof(ReplayChunkTessellationRevisionPatch),
                nameof(TesselatedChunkConstructorPostfix));
            MethodInfo? addPrefix = AccessTools.Method(
                typeof(ReplayChunkTessellationRevisionPatch),
                nameof(AddTesselatedChunkPrefix));
            MethodInfo? mergePrefix = AccessTools.Method(
                typeof(ReplayChunkTessellationRevisionPatch),
                nameof(MergeTesselatedChunkPrefix));
            MethodInfo? triggerPrefix = AccessTools.Method(
                typeof(ReplayChunkTessellationRevisionPatch),
                nameof(TriggerChunkRetesselatedPrefix));

            if (tesselateChunk is null
                || tesselatedChunkConstructor is null
                || addTesselatedChunk is null
                || mergeTesselatedChunk is null
                || triggerRetessellated is null
                || TesselatorGameField is null
                || DisposeUnusedTessellationMethod is null
                || tesselatePrefix is null
                || tesselateFinalizer is null
                || constructorPostfix is null
                || addPrefix is null
                || mergePrefix is null
                || triggerPrefix is null)
            {
                patchLogger.Warning(
                    "VS Director: replay chunk tessellation revision targets were not available.");
                return false;
            }

            harmony.Patch(
                tesselateChunk,
                prefix: new HarmonyMethod(tesselatePrefix) { priority = Priority.First },
                finalizer: new HarmonyMethod(tesselateFinalizer) { priority = Priority.Last });
            harmony.Patch(
                tesselatedChunkConstructor,
                postfix: new HarmonyMethod(constructorPostfix) { priority = Priority.Last });
            harmony.Patch(
                addTesselatedChunk,
                prefix: new HarmonyMethod(addPrefix) { priority = Priority.First });
            harmony.Patch(
                mergeTesselatedChunk,
                prefix: new HarmonyMethod(mergePrefix) { priority = Priority.First });
            harmony.Patch(
                triggerRetessellated,
                prefix: new HarmonyMethod(triggerPrefix) { priority = Priority.First });

            logger = patchLogger;
            Targets = new MethodBase[]
            {
                tesselateChunk,
                tesselatedChunkConstructor,
                addTesselatedChunk,
                mergeTesselatedChunk,
                triggerRetessellated
            };
            return true;
        }
        catch (Exception exception)
        {
            patchLogger.Warning(
                "VS Director: replay chunk tessellation revision patch failed: {0}",
                exception.Message);
            Targets = Array.Empty<MethodBase>();
            return false;
        }
    }

    internal static long BeginRedraw(
        ClientWorldMap worldMap,
        int chunkX,
        int chunkY,
        int chunkZ)
    {
        long chunkIndex = worldMap.ChunkIndex3D(chunkX, chunkY, chunkZ);
        return RevisionTables
            .GetValue(worldMap, static _ => new ChunkRevisionTable())
            .Begin(chunkIndex);
    }

    internal static bool IsCurrentRevision(
        ClientWorldMap worldMap,
        int chunkX,
        int chunkY,
        int chunkZ,
        long revision)
    {
        long chunkIndex = worldMap.ChunkIndex3D(chunkX, chunkY, chunkZ);
        return RevisionTables
            .GetValue(worldMap, static _ => new ChunkRevisionTable())
            .IsCurrent(chunkIndex, revision);
    }

    private static void TesselateChunkPrefix(
        ChunkTesselatorManager __instance,
        int chunkX,
        int chunkY,
        int chunkZ,
        ref TessellationTicket? __state)
    {
        if (TesselatorGameField?.GetValue(__instance) is not ClientMain game)
        {
            return;
        }

        ClientWorldMap worldMap = game.WorldMap;
        long chunkIndex = worldMap.ChunkIndex3D(chunkX, chunkY, chunkZ);
        ChunkRevisionTable revisions = RevisionTables.GetValue(
            worldMap,
            static _ => new ChunkRevisionTable());
        __state = new TessellationTicket(
            revisions,
            chunkIndex,
            revisions.Current(chunkIndex),
            chunkX,
            chunkY,
            chunkZ);
        pendingTessellation = __state;
    }

    private static Exception? TesselateChunkFinalizer(
        Exception? __exception,
        TessellationTicket? __state)
    {
        if (ReferenceEquals(pendingTessellation, __state))
        {
            pendingTessellation = null;
        }
        return __exception;
    }

    private static void TesselatedChunkConstructorPostfix(TesselatedChunk __instance)
    {
        TessellationTicket? ticket = pendingTessellation;
        if (ticket is null)
        {
            return;
        }

        TessellationTickets.Remove(__instance);
        TessellationTickets.Add(__instance, ticket);
    }

    private static bool AddTesselatedChunkPrefix(TesselatedChunk tesschunk)
    {
        suppressedRetessellationEvent = null;
        if (!TessellationTickets.TryGetValue(tesschunk, out TessellationTicket? ticket))
        {
            return true;
        }

        TessellationTickets.Remove(tesschunk);
        if (ticket.Revisions.IsCurrent(ticket.ChunkIndex, ticket.Revision))
        {
            return true;
        }

        suppressedRetessellationEvent = ticket;
        DisposeUnusedTessellation(tesschunk);
        return false;
    }

    private static bool MergeTesselatedChunkPrefix(
        TesselatedChunk __instance,
        TesselatedChunk otc,
        ref bool __result)
    {
        TessellationTickets.TryGetValue(
            __instance,
            out TessellationTicket? currentTicket);
        TessellationTickets.TryGetValue(
            otc,
            out TessellationTicket? incomingTicket);
        if (currentTicket is null || incomingTicket is null)
        {
            return true;
        }
        if (!ReferenceEquals(currentTicket.Revisions, incomingTicket.Revisions)
            || currentTicket.ChunkIndex != incomingTicket.ChunkIndex
            || currentTicket.ChunkX != incomingTicket.ChunkX
            || currentTicket.ChunkY != incomingTicket.ChunkY
            || currentTicket.ChunkZ != incomingTicket.ChunkZ)
        {
            return true;
        }

        // SortableQueue merges the incoming parts into the existing queue
        // object. Preserve the newer ticket on that object, and dispose an
        // older late result without letting it replace newer parts.
        if (incomingTicket.Revision < currentTicket.Revision)
        {
            TessellationTickets.Remove(otc);
            DisposeUnusedTessellation(otc);
            __result = true;
            return false;
        }

        TessellationTickets.Remove(__instance);
        TessellationTickets.Add(__instance, incomingTicket);
        TessellationTickets.Remove(otc);
        return true;
    }

    private static void DisposeUnusedTessellation(TesselatedChunk tesschunk)
    {
        try
        {
            DisposeUnusedTessellationMethod?.Invoke(tesschunk, null);
        }
        catch (Exception exception)
        {
            logger?.Warning(
                "VS Director: could not dispose an obsolete replay chunk mesh: {0}",
                exception.GetBaseException().Message);
        }
    }

    private static bool TriggerChunkRetesselatedPrefix(Vec3i chunkPos)
    {
        TessellationTicket? ticket = suppressedRetessellationEvent;
        suppressedRetessellationEvent = null;
        return ticket is null
            || ticket.ChunkX != chunkPos.X
            || ticket.ChunkY != chunkPos.Y
            || ticket.ChunkZ != chunkPos.Z;
    }

    private sealed class ChunkRevisionTable
    {
        private readonly ConcurrentDictionary<long, long> revisions = new();

        internal long Begin(long chunkIndex)
            => revisions.AddOrUpdate(
                chunkIndex,
                static _ => 1L,
                static (_, current) => unchecked(current + 1L));

        internal long Current(long chunkIndex)
            => revisions.TryGetValue(chunkIndex, out long revision) ? revision : 0L;

        internal bool IsCurrent(long chunkIndex, long revision)
            => Current(chunkIndex) == revision;
    }

    private sealed record TessellationTicket(
        ChunkRevisionTable Revisions,
        long ChunkIndex,
        long Revision,
        int ChunkX,
        int ChunkY,
        int ChunkZ);
}
