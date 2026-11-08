using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Runtime;

namespace VSDirector.Replay;

internal static class ReplayBlockEntityStateCapturePatch
{
    private const string DoorBehaviorTypeName = "Vintagestory.GameContent.BEBehaviorDoor";
    private const string BlockEntityAnimationUtilTypeName =
        "Vintagestory.GameContent.BlockEntityAnimationUtil";

    private static readonly ConditionalWeakTable<AnimationUtil, BlockEntity> AnimationOwners = new();
    private static readonly ConditionalWeakTable<AnimationUtil, InvalidatedAnimationMarker>
        InvalidatedAnimations = new();
    private static readonly ConcurrentDictionary<(Type Type, RuntimeMethodHandle Method), bool>
        EffectiveTargetCache = new();
    private static readonly object AnimationOwnersLock = new();
    [ThreadStatic]
    private static Dictionary<BlockEntity, int>? stateBoundaryDepths;
    [ThreadStatic]
    private static int livePacketDepth;
    [ThreadStatic]
    private static int replayAnimationRedrawDepth;
    private static PropertyInfo? leftDoorProperty;
    private static PropertyInfo? rightDoorProperty;

    internal static IReadOnlyList<MethodBase> Targets { get; private set; } = Array.Empty<MethodBase>();

    internal static bool IsReplayAnimationRedraw => replayAnimationRedrawDepth > 0;

    internal static bool TryPatch(Harmony harmony, ILogger logger)
    {
        try
        {
            MethodInfo? statePrefix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(StatePrefix));
            MethodInfo? statePostfix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(StatePostfix));
            MethodInfo? initializePostfix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(InitializePostfix));
            MethodInfo? animationCreatedPostfix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(AnimationCreatedPostfix));
            MethodInfo? animationStartPrefix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(AnimationStartPrefix));
            MethodInfo? animationStartPostfix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(AnimationStartPostfix));
            MethodInfo? animationStopPrefix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(AnimationStopPrefix));
            MethodInfo? animationStopPostfix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(AnimationStopPostfix));
            MethodInfo? animationStateChangePrefix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(AnimationStateChangePrefix));
            MethodInfo? doorPrefix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(DoorPrefix));
            MethodInfo? doorPostfix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(DoorPostfix));
            MethodInfo? livePacketPrefix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(LiveBlockEntityPacketPrefix));
            MethodInfo? livePacketPostfix = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(LiveBlockEntityPacketPostfix));
            MethodInfo? livePacketFinalizer = AccessTools.Method(
                typeof(ReplayBlockEntityStateCapturePatch),
                nameof(LiveBlockEntityPacketFinalizer));
            if (statePrefix is null
                || statePostfix is null
                || initializePostfix is null
                || animationCreatedPostfix is null
                || animationStartPrefix is null
                || animationStartPostfix is null
                || animationStopPrefix is null
                || animationStopPostfix is null
                || animationStateChangePrefix is null
                || doorPrefix is null
                || doorPostfix is null
                || livePacketPrefix is null
                || livePacketPostfix is null
                || livePacketFinalizer is null)
            {
                logger.Warning("VS Director: replay block-entity capture patch methods were not available.");
                return false;
            }

            HashSet<MethodBase> targets = new();
            MethodInfo markDirty = AccessTools.Method(
                typeof(BlockEntity),
                nameof(BlockEntity.MarkDirty),
                new[] { typeof(bool), typeof(IPlayer) })
                ?? throw new MissingMethodException(typeof(BlockEntity).FullName, nameof(BlockEntity.MarkDirty));
            PatchStateBoundary(harmony, markDirty, statePrefix, statePostfix, targets);

            MethodInfo liveBlockEntityPacket = AccessTools.Method(
                typeof(GeneralPacketHandler),
                "HandleBlockEntities",
                new[] { typeof(Packet_Server) })
                ?? throw new MissingMethodException(
                    typeof(GeneralPacketHandler).FullName,
                    "HandleBlockEntities");
            harmony.Patch(
                liveBlockEntityPacket,
                prefix: new HarmonyMethod(livePacketPrefix) { priority = Priority.First },
                postfix: new HarmonyMethod(livePacketPostfix) { priority = Priority.Last },
                finalizer: new HarmonyMethod(livePacketFinalizer) { priority = Priority.Last });
            targets.Add(liveBlockEntityPacket);

            foreach (Type type in LoadedBlockEntityTypes())
            {
                PatchDeclaredStateBoundary(
                    harmony,
                    type,
                    nameof(BlockEntity.FromTreeAttributes),
                    new[] { typeof(ITreeAttribute), typeof(IWorldAccessor) },
                    statePrefix,
                    statePostfix,
                    targets);
                PatchDeclaredStateBoundary(
                    harmony,
                    type,
                    nameof(BlockEntity.OnReceivedServerPacket),
                    new[] { typeof(int), typeof(byte[]) },
                    statePrefix,
                    statePostfix,
                    targets);

                MethodInfo? initialize = type.GetMethod(
                    nameof(BlockEntity.Initialize),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null,
                    new[] { typeof(ICoreAPI) },
                    modifiers: null);
                if (initialize is not null && !initialize.IsAbstract && targets.Add(initialize))
                {
                    harmony.Patch(
                        initialize,
                        postfix: new HarmonyMethod(initializePostfix) { priority = Priority.Last });
                }
            }

            Type? animationUtilType = AccessTools.TypeByName(BlockEntityAnimationUtilTypeName);
            ConstructorInfo? animationConstructor = animationUtilType?.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                new[] { typeof(ICoreAPI), typeof(BlockEntity) },
                modifiers: null);
            MethodInfo animationStart = AccessTools.Method(
                typeof(AnimationUtil),
                nameof(AnimationUtil.StartAnimation),
                new[] { typeof(AnimationMetaData) })
                ?? throw new MissingMethodException(typeof(AnimationUtil).FullName, nameof(AnimationUtil.StartAnimation));
            MethodInfo animationStop = AccessTools.Method(
                typeof(AnimationUtil),
                nameof(AnimationUtil.StopAnimation),
                new[] { typeof(string) })
                ?? throw new MissingMethodException(typeof(AnimationUtil).FullName, nameof(AnimationUtil.StopAnimation));
            if (animationConstructor is null)
            {
                throw new MissingMethodException(
                    BlockEntityAnimationUtilTypeName,
                    ".ctor");
            }

            harmony.Patch(
                animationConstructor,
                postfix: new HarmonyMethod(animationCreatedPostfix) { priority = Priority.Last });
            targets.Add(animationConstructor);
            harmony.Patch(
                animationStart,
                prefix: new HarmonyMethod(animationStartPrefix) { priority = Priority.First },
                postfix: new HarmonyMethod(animationStartPostfix) { priority = Priority.Last });
            targets.Add(animationStart);
            harmony.Patch(
                animationStop,
                prefix: new HarmonyMethod(animationStopPrefix) { priority = Priority.First },
                postfix: new HarmonyMethod(animationStopPostfix) { priority = Priority.Last });
            targets.Add(animationStop);

            MethodInfo? animationStateChange = AccessTools.Method(
                animationUtilType,
                "OnAnimationsStateChange",
                new[] { typeof(bool) });
            if (animationStateChange is null)
            {
                throw new MissingMethodException(
                    BlockEntityAnimationUtilTypeName,
                    "OnAnimationsStateChange");
            }
            harmony.Patch(
                animationStateChange,
                prefix: new HarmonyMethod(animationStateChangePrefix)
                {
                    priority = Priority.First
                });
            targets.Add(animationStateChange);

            Type? doorType = AccessTools.TypeByName(DoorBehaviorTypeName);
            MethodInfo? toggleDoor = doorType is null
                ? null
                : AccessTools.Method(
                    doorType,
                    "ToggleDoorState",
                    new[] { typeof(IPlayer), typeof(bool) });
            if (doorType is not null && toggleDoor is not null)
            {
                leftDoorProperty = AccessTools.Property(doorType, "LeftDoor");
                rightDoorProperty = AccessTools.Property(doorType, "RightDoor");
                harmony.Patch(
                    toggleDoor,
                    prefix: new HarmonyMethod(doorPrefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(doorPostfix) { priority = Priority.Last });
                targets.Add(toggleDoor);
            }

            Targets = targets.OrderBy(MethodDisplayName, StringComparer.Ordinal).ToArray();
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning("VS Director: replay block-entity state capture patch failed: {0}", exception.Message);
            Targets = Array.Empty<MethodBase>();
            return false;
        }
    }

    internal static void RegisterAnimationOwner(AnimationUtil animation, BlockEntity blockEntity)
    {
        lock (AnimationOwnersLock)
        {
            if (AnimationOwners.TryGetValue(animation, out BlockEntity? previous)
                && !ReferenceEquals(previous, blockEntity))
            {
                DirectorReplaySerialization.InvalidateAnimationOwner(animation);
            }

            DirectorReplaySerialization.ReviveAnimationOwner(animation);
            InvalidatedAnimations.Remove(animation);
            AnimationOwners.Remove(animation);
            AnimationOwners.Add(animation, blockEntity);
        }
    }

    internal static void UnregisterAnimationOwner(AnimationUtil animation)
    {
        lock (AnimationOwnersLock)
        {
            DirectorReplaySerialization.InvalidateAnimationOwner(animation);
            AnimationOwners.Remove(animation);
            InvalidatedAnimations.GetValue(
                animation,
                static _ => new InvalidatedAnimationMarker());
        }
    }

    private static IEnumerable<Type> LoadedBlockEntityTypes()
    {
        HashSet<Type> types = new() { typeof(BlockEntity) };
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            IEnumerable<Type> assemblyTypes;
            try
            {
                assemblyTypes = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                assemblyTypes = exception.Types.OfType<Type>();
            }
            catch
            {
                continue;
            }

            foreach (Type type in assemblyTypes)
            {
                if (typeof(BlockEntity).IsAssignableFrom(type))
                {
                    types.Add(type);
                }
            }
        }

        return types;
    }

    private static void PatchDeclaredStateBoundary(
        Harmony harmony,
        Type type,
        string methodName,
        Type[] parameterTypes,
        MethodInfo prefix,
        MethodInfo postfix,
        HashSet<MethodBase> targets)
    {
        MethodInfo? method = type.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            binder: null,
            parameterTypes,
            modifiers: null);
        if (method is not null && !method.IsAbstract)
        {
            PatchStateBoundary(harmony, method, prefix, postfix, targets);
        }
    }

    private static void PatchStateBoundary(
        Harmony harmony,
        MethodInfo method,
        MethodInfo prefix,
        MethodInfo postfix,
        HashSet<MethodBase> targets)
    {
        if (!targets.Add(method))
        {
            return;
        }

        harmony.Patch(
            method,
            prefix: new HarmonyMethod(prefix) { priority = Priority.First },
            postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
    }

    private static void StatePrefix(
        BlockEntity __instance,
        MethodBase __originalMethod,
        ref StateBoundary? __state)
    {
        if (SuppressNestedLivePacketCapture()
            || !IsEffectiveTarget(__instance, __originalMethod))
        {
            return;
        }

        Dictionary<BlockEntity, int> depths = stateBoundaryDepths ??=
            new Dictionary<BlockEntity, int>(ReferenceEqualityComparer.Instance);
        depths.TryGetValue(__instance, out int depth);
        bool outermost = depth == 0;
        if (outermost)
        {
            Capture(__instance, baseline: true, ignoreReplayOwned: true);
        }
        depths[__instance] = depth + 1;
        __state = new StateBoundary(__instance, outermost);
    }

    private static void StatePostfix(StateBoundary? __state)
    {
        if (__state is null)
        {
            return;
        }

        Dictionary<BlockEntity, int>? depths = stateBoundaryDepths;
        if (depths is not null && depths.TryGetValue(__state.BlockEntity, out int depth))
        {
            if (depth <= 1)
            {
                depths.Remove(__state.BlockEntity);
            }
            else
            {
                depths[__state.BlockEntity] = depth - 1;
            }
        }

        if (__state.Outermost)
        {
            Capture(
                __state.BlockEntity,
                baseline: false,
                ignoreReplayOwned: true);
        }
    }

    private static void InitializePostfix(BlockEntity __instance, MethodBase __originalMethod)
    {
        if (SuppressNestedLivePacketCapture()
            || !IsEffectiveTarget(__instance, __originalMethod))
        {
            return;
        }

        DirectorReplaySerialization.RegisterAnimationOwners(__instance);
        DirectorReplayManager? manager = DirectorReplayRuntime.Manager;
        manager?.CaptureObservedBlockEntityState(__instance, baseline: !manager.IsPlaying);
    }

    private static bool IsEffectiveTarget(BlockEntity blockEntity, MethodBase originalMethod)
    {
        Type blockEntityType = blockEntity.GetType();
        return EffectiveTargetCache.GetOrAdd(
            (blockEntityType, originalMethod.MethodHandle),
            _ =>
            {
                Type[] parameters = originalMethod.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .ToArray();
                MethodInfo? effective = blockEntityType.GetMethod(
                    originalMethod.Name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    parameters,
                    modifiers: null);
                return effective == originalMethod;
            });
    }

    private static void AnimationCreatedPostfix(AnimationUtil __instance, object[] __args)
    {
        BlockEntity? owner = __args.OfType<BlockEntity>().FirstOrDefault();
        if (owner is not null)
        {
            RegisterAnimationOwner(__instance, owner);
        }
    }

    private static void AnimationStartPrefix(AnimationUtil __instance, ref BlockEntity? __state)
        => AnimationPrefix(__instance, ref __state);

    private static void AnimationStartPostfix(
        AnimationUtil __instance,
        bool __result,
        BlockEntity? __state)
    {
        // The animation owner can be invalidated between prefix and postfix
        // when replay replaces the block entity. Do not let that stale call
        // capture the removed entity as a live transition.
        if (__result
            && __state is not null
            && IsCurrentAnimationOwner(__instance, __state))
        {
            Capture(__state, baseline: false, ignoreReplayOwned: true);
        }
    }

    private static void AnimationStopPrefix(AnimationUtil __instance, ref BlockEntity? __state)
        => AnimationPrefix(__instance, ref __state);

    private static void AnimationStopPostfix(
        AnimationUtil __instance,
        BlockEntity? __state)
    {
        if (__state is not null && IsCurrentAnimationOwner(__instance, __state))
        {
            Capture(__state, baseline: false, ignoreReplayOwned: true);
        }
    }

    // VSEssentials queues a callback that blindly assigns ShouldRender. If a
    // cabinet changes again before the chunk queue drains, an older callback
    // can overwrite the newer state. Resolve it against the animation revision
    // that requested the redraw when the callback actually executes.
    private static bool AnimationStateChangePrefix(
        AnimationUtil __instance,
        bool animsNowActive)
    {
        BlockEntity? blockEntity;
        long animationRevision;
        lock (AnimationOwnersLock)
        {
            if (!AnimationOwners.TryGetValue(__instance, out blockEntity))
            {
                return !InvalidatedAnimations.TryGetValue(__instance, out _);
            }

            // Acquire the revision while the owner is still registered. This
            // prevents an invalidate/register race from reviving a stale
            // renderer callback after the block entity was replaced.
            animationRevision =
                DirectorReplaySerialization.BeginAnimationStateChange(__instance);
        }

        if (blockEntity.Pos is null
            || blockEntity.Api?.World?.BlockAccessor is not { } blockAccessor)
        {
            return true;
        }

        if (!IsCurrentAnimationOwner(__instance, blockEntity))
        {
            return false;
        }

        BlockPos position = blockEntity.Pos.Copy();
        DirectorReplayManager? manager = DirectorReplayRuntime.Manager;
        ClientWorldMap? replayWorldMap = manager?.IsReplayBlockEntity(blockEntity) == true
            && blockEntity.Api?.World is ClientMain client
                ? client.WorldMap
                : null;
        Action onRetessellated = () => CompleteAnimationStateChange(
            __instance,
            blockEntity,
            replayWorldMap,
            position,
            animationRevision,
            animsNowActive);

        if (replayWorldMap is not null)
        {
            RetessellateReplayAnimationInOrder(
                replayWorldMap,
                position,
                onRetessellated);
            return false;
        }

        bool suppressRealityObservation = manager?.IsWorldPlaybackActive == true;
        if (suppressRealityObservation)
        {
            replayAnimationRedrawDepth++;
        }
        try
        {
            blockAccessor.MarkBlockDirty(
                position,
                onRetessellated);
        }
        finally
        {
            if (suppressRealityObservation)
            {
                replayAnimationRedrawDepth--;
            }
        }
        return false;
    }

    private static void CompleteAnimationStateChange(
        AnimationUtil animation,
        BlockEntity blockEntity,
        ClientWorldMap? replayWorldMap,
        BlockPos position,
        long animationRevision,
        bool activeWhenQueued)
    {
        bool ownerCurrent = IsCurrentAnimationOwner(animation, blockEntity);
        bool revisionCurrent =
            DirectorReplaySerialization.IsAnimationRevisionCurrent(
                animation,
                animationRevision);
        if (ownerCurrent && animation.renderer is not null)
        {
            animation.renderer.ShouldRender =
                DirectorReplaySerialization.ResolveAnimationRendererVisibility(
                    animation,
                    animationRevision,
                    activeWhenQueued);
        }

        if (revisionCurrent
            || replayWorldMap is null
            || !ownerCurrent
            || DirectorReplayRuntime.Manager?.IsReplayBlockEntity(blockEntity) != true)
        {
            return;
        }

        // The callback can be current-world stale while its chunk mesh is
        // also stale. Queue one normal redraw behind any older upload so the
        // authoritative static/dynamic handoff becomes the final mesh.
        RetessellateReplayAnimationInOrder(
            replayWorldMap,
            position,
            onRetessellated: null,
            advanceRevision: false);
    }

    private static void RetessellateReplayAnimationInOrder(
        ClientWorldMap worldMap,
        BlockPos position,
        Action? onRetessellated,
        bool advanceRevision = true)
    {
        int internalChunkY = (position.Y >> 5)
            + position.dimension * GlobalConstants.DimensionSizeInChunks;
        ReplayRemoteChunkPriorityPatch.RetessellateExistingChunkInOrder(
            worldMap,
            position.X >> 5,
            internalChunkY,
            position.Z >> 5,
            onRetessellated,
            advanceRevision);
    }

    private static void AnimationPrefix(AnimationUtil animation, ref BlockEntity? state)
    {
        if (!AnimationOwners.TryGetValue(animation, out BlockEntity? owner))
        {
            return;
        }

        state = owner;
        Capture(owner, baseline: true, ignoreReplayOwned: true);
    }

    private static bool IsCurrentAnimationOwner(AnimationUtil animation, BlockEntity owner)
    {
        lock (AnimationOwnersLock)
        {
            return AnimationOwners.TryGetValue(animation, out BlockEntity? current)
                && ReferenceEquals(current, owner);
        }
    }

    private static void DoorPrefix(BlockEntityBehavior __instance)
        => CaptureDoorSet(__instance, baseline: true);

    private static void DoorPostfix(BlockEntityBehavior __instance)
        => CaptureDoorSet(__instance, baseline: false);

    private static void CaptureDoorSet(BlockEntityBehavior door, bool baseline)
    {
        DirectorReplayManager? manager = DirectorReplayRuntime.Manager;
        if (manager is null)
        {
            return;
        }

        // ToggleDoorState updates both halves of a linked door. If the
        // initiating half belongs to the replay, the whole transition is
        // replay-owned even when a partner entity has not been marked yet.
        // Capturing that partner as reality would immediately reapply the
        // replay frame and can invalidate the animation handoff.
        if (manager.IsReplayBlockEntity(door.Blockentity))
        {
            return;
        }

        manager.CaptureObservedBlockEntityState(door.Blockentity, baseline);
        CaptureLinkedDoor(manager, door, leftDoorProperty, baseline);
        CaptureLinkedDoor(manager, door, rightDoorProperty, baseline);
    }

    private static void CaptureLinkedDoor(
        DirectorReplayManager manager,
        BlockEntityBehavior door,
        PropertyInfo? property,
        bool baseline)
    {
        try
        {
            if (property?.GetValue(door) is BlockEntityBehavior linked
                && !manager.IsReplayBlockEntity(linked.Blockentity))
            {
                manager.CaptureObservedBlockEntityState(linked.Blockentity, baseline);
            }
        }
        catch
        {
            // Linked doors can unload while their partner handles an interaction.
        }
    }

    private static void Capture(
        BlockEntity blockEntity,
        bool baseline,
        bool ignoreReplayOwned = false)
    {
        if (SuppressNestedLivePacketCapture()
            || stateBoundaryDepths?.ContainsKey(blockEntity) == true)
        {
            return;
        }

        DirectorReplayManager? manager = DirectorReplayRuntime.Manager;
        if (ignoreReplayOwned && manager?.IsReplayBlockEntity(blockEntity) == true)
        {
            return;
        }

        manager?.CaptureObservedBlockEntityState(blockEntity, baseline);
    }

    private static void LiveBlockEntityPacketPrefix(
        Packet_Server packet,
        ref LiveBlockEntityPacketBoundary? __state)
    {
        livePacketDepth++;
        Packet_BlockEntities? blockEntities = packet.BlockEntities;
        if (blockEntities is null)
        {
            __state = new LiveBlockEntityPacketBoundary(Array.Empty<BlockPos>());
            return;
        }

        Packet_BlockEntity[] entries = blockEntities.BlockEntitites ?? Array.Empty<Packet_BlockEntity>();
        int count = Math.Clamp(blockEntities.BlockEntititesCount, 0, entries.Length);
        List<BlockPos> positions = new(count);
        HashSet<(int X, int Y, int Z)> seen = new();
        for (int index = 0; index < count; index++)
        {
            Packet_BlockEntity blockEntity = entries[index];
            if (seen.Add((blockEntity.PosX, blockEntity.PosY, blockEntity.PosZ)))
            {
                positions.Add(new BlockPos(blockEntity.PosX, blockEntity.PosY, blockEntity.PosZ));
            }
        }
        __state = new LiveBlockEntityPacketBoundary(positions);
    }

    private static void LiveBlockEntityPacketPostfix(LiveBlockEntityPacketBoundary? __state)
    {
        if (__state is not null)
        {
            foreach (BlockPos position in __state.Positions)
            {
                DirectorReplayRuntime.Manager?.CaptureObservedBlockEntityPacket(position);
            }
        }
    }

    private static Exception? LiveBlockEntityPacketFinalizer(Exception? __exception)
    {
        if (livePacketDepth > 0)
        {
            livePacketDepth--;
        }
        return __exception;
    }

    private static bool SuppressNestedLivePacketCapture()
        => livePacketDepth > 0 && DirectorReplayRuntime.Manager?.IsWorldPlaybackActive == true;

    internal static bool IsLiveBlockEntityPacket
        => SuppressNestedLivePacketCapture();

    private static string MethodDisplayName(MethodBase method)
        => $"{method.DeclaringType?.FullName}.{method.Name}";

    private sealed record StateBoundary(
        BlockEntity BlockEntity,
        bool Outermost);

    private sealed record LiveBlockEntityPacketBoundary(IReadOnlyList<BlockPos> Positions);

    private sealed class InvalidatedAnimationMarker
    {
    }
}
