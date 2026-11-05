using HarmonyLib;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using VSDirector.Data;
using VSDirector.Networking;
using VSDirector.Rendering;
using VSDirector.Runtime;

namespace VSDirector.Replay;

internal static class ReplayPlayerRenderMode
{
    internal static bool UseThirdPerson(EntityPlayer player)
        => DirectorReplayRuntime.Manager?.IsReplayThirdPerson(player.EntityId) == true
            || DirectorCameraRuntime.Controller?.UsesDetachedThirdPersonRendering(player.EntityId) == true
            || (DirectorOffscreenRenderRuntime.CurrentView is { RenderLocalPlayer: true }
                && DirectorCameraRuntime.Controller?.IsLocalPlayerEntity(player) == true);
}

// Replay actors are driven by recorded samples. Detach them from the normal
// client tick so live AI, physics, and interpolation cannot overwrite a
// playback frame; the replay manager runs only the player observer animation
// selection that it needs.
[HarmonyPatch(typeof(ClientSystemEntities), "OnGameTick")]
internal static class ReplayEntityTickIsolationPatch
{
    [ThreadStatic]
    private static List<KeyValuePair<long, Entity>>? detachedGhosts;

    [ThreadStatic]
    private static DirectorReplayManager? detachedManager;

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        if (DirectorReplayRuntime.Manager is not { } manager)
        {
            return;
        }

        List<KeyValuePair<long, Entity>> detached = detachedGhosts ??= new();
        detachedManager = manager;
        manager.DetachReplayEntitiesForTick(detached);
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
        => RestoreDetachedGhosts();

    [HarmonyFinalizer]
    private static void Finalizer()
        => RestoreDetachedGhosts();

    private static void RestoreDetachedGhosts()
    {
        if (detachedGhosts is { Count: > 0 } detached && detachedManager is { } manager)
        {
            manager.RestoreReplayGhostsAfterTick(detached);
        }
        detachedManager = null;
    }
}

[HarmonyPatch(typeof(AnimationManager), nameof(AnimationManager.OnClientFrame))]
internal static class ReplayAnimationFrameIsolationPatch
{
    private static readonly FieldInfo? EntityField = AccessTools.Field(typeof(AnimationManager), "entity");

    [HarmonyPrefix]
    private static bool Prefix(AnimationManager __instance)
    {
        if (EntityField?.GetValue(__instance) is not Entity entity
            || DirectorReplayRuntime.Manager is not { } manager)
        {
            return true;
        }

        // A live entity replaced by a replay ghost must not run its normal
        // animation frame. The ghost owns the recorded pose for this frame.
        if (manager.IsLiveEntitySuppressed(entity))
        {
            return false;
        }

        return manager.ShouldDriveReplayAnimation(entity.EntityId) != true;
    }
}

[HarmonyPatch(typeof(EntityPlayer), nameof(EntityPlayer.AnimManager), MethodType.Getter)]
internal static class ReplayPlayerAnimationManagerPatch
{
    [HarmonyPostfix]
    private static void Postfix(EntityPlayer __instance, ref IAnimationManager __result)
    {
        if (ReplayPlayerRenderMode.UseThirdPerson(__instance))
        {
            __result = __instance.TpAnimManager;
        }
    }
}

[HarmonyPatch(typeof(EntityPlayer), nameof(EntityPlayer.OtherAnimManager), MethodType.Getter)]
internal static class ReplayPlayerOtherAnimationManagerPatch
{
    [HarmonyPostfix]
    private static void Postfix(EntityPlayer __instance, ref IAnimationManager __result)
    {
        if (ReplayPlayerRenderMode.UseThirdPerson(__instance))
        {
            __result = __instance.TpAnimManager;
        }
    }
}

[HarmonyPatch(typeof(EntityPlayer), nameof(EntityPlayer.RightHandItemSlot), MethodType.Getter)]
internal static class ReplayPlayerRightHandSlotPatch
{
    [HarmonyPostfix]
    private static void Postfix(EntityPlayer __instance, ref ItemSlot? __result)
    {
        if (DirectorReplayRuntime.Manager?.TryGetReplayHandSlot(
            __instance.EntityId,
            rightHand: true,
            out ItemSlot? slot) == true)
        {
            __result = slot;
        }
    }
}

[HarmonyPatch(typeof(EntityPlayer), nameof(EntityPlayer.LeftHandItemSlot), MethodType.Getter)]
internal static class ReplayPlayerLeftHandSlotPatch
{
    [HarmonyPostfix]
    private static void Postfix(EntityPlayer __instance, ref ItemSlot? __result)
    {
        if (DirectorReplayRuntime.Manager?.TryGetReplayHandSlot(
            __instance.EntityId,
            rightHand: false,
            out ItemSlot? slot) == true)
        {
            __result = slot;
        }
    }
}

// EntityPlayer selects its first-person animator from a private IsSelf getter.
// Replay ghosts intentionally retain the recorded player UID, so the normal
// UID comparison would incorrectly make a ghost look like the local player.
// Force the getter to report a remote entity for ghosts before initialization,
// tessellation, and renderer selection can cache the wrong animation manager.
[HarmonyPatch(typeof(EntityPlayer), "get_IsSelf")]
internal static class ReplayPlayerIsSelfPatch
{
    [HarmonyPostfix]
    private static void Postfix(EntityPlayer __instance, ref bool __result)
    {
        if (ReplayPlayerRenderMode.UseThirdPerson(__instance))
        {
            __result = false;
        }
    }
}

// PlayerHeadController does not use EntityPlayer.IsSelf. It compares the
// player UID directly, so a replay ghost of the local player would otherwise
// still execute the first-person head/body-follow path. That path immediately
// aligns the body to the camera in first person and flattens the third-person
// turn animation that the replay renderer is meant to show.
[HarmonyPatch(typeof(PlayerHeadController), "IsSelf")]
internal static class ReplayPlayerHeadControllerIsSelfPatch
{
    private static readonly FieldInfo? EntityPlayerField = AccessTools.Field(
        typeof(PlayerHeadController),
        "entityPlayer");

    [HarmonyPostfix]
    private static void Postfix(PlayerHeadController __instance, ref bool __result)
    {
        if (__result
            && EntityPlayerField?.GetValue(__instance) is EntityPlayer player
            && DirectorReplayRuntime.Manager?.IsReplayThirdPerson(player.EntityId) == true)
        {
            __result = false;
        }
    }
}

[HarmonyPatch(typeof(EntityPlayer), nameof(EntityPlayer.OnTesselation))]
internal static class ReplayPlayerThirdPersonShapePatch
{
    [HarmonyPrefix]
    private static void Prefix(EntityPlayer __instance, ref TesselationState? __state)
    {
        __state = new TesselationState(
            ReplayPlayerInventoryContext.Current,
            __instance,
            IsLocalPlayerOffscreen(__instance));
        ReplayPlayerInventoryContext.Current = __instance;
    }

    [HarmonyPostfix]
    private static void Postfix(
        EntityPlayer __instance,
        ref Shape entityShape,
        TesselationState? __state)
    {
        ReplayPlayerInventoryContext.Current = __state?.PreviousInventoryPlayer;
        try
        {
            if (!ReplayPlayerRenderMode.UseThirdPerson(__instance))
            {
                return;
            }

            __instance.TpAnimManager.UseFpAnmations = false;
            // The local player's first-person manager is shared with the
            // primary view. During an offscreen third-person tessellation it
            // must not be disabled permanently, or returning to first person
            // leaves the model frozen in its last pose.
            if (__state?.RestoreLocalOffscreenState != true)
            {
                __instance.SelfFpAnimManager.UseFpAnmations = false;
            }
            __instance.BodyYawLimits = null;
            __instance.HeadYawLimits = null;
            __instance.TpAnimManager.HeadController = new EntityHeadController(
                __instance.TpAnimManager,
                __instance,
                entityShape);
        }
        finally
        {
            __state?.RestoreLocalState(__instance);
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(TesselationState? __state, Exception? __exception)
    {
        ReplayPlayerInventoryContext.Current = __state?.PreviousInventoryPlayer;
        __state?.RestoreLocalState(__state.Player);
        return __exception;
    }

    private static bool IsLocalPlayerOffscreen(EntityPlayer player)
        => DirectorOffscreenRenderRuntime.CurrentView is { RenderLocalPlayer: true }
            && ReferenceEquals(
                player,
                (player.Api as ICoreClientAPI)?.World.Player?.Entity);

    private sealed class TesselationState
    {
        internal EntityPlayer? PreviousInventoryPlayer { get; }

        internal EntityPlayer Player { get; }

        internal bool RestoreLocalOffscreenState { get; }

        private readonly bool tpUseFpAnimations;
        private readonly bool selfUseFpAnimations;
        private readonly AngleConstraint? bodyYawLimits;
        private readonly AngleConstraint? headYawLimits;
        private readonly IHeadController? tpHeadController;
        private bool restored;

        internal TesselationState(
            EntityPlayer? previousInventoryPlayer,
            EntityPlayer player,
            bool restoreLocalOffscreenState)
        {
            PreviousInventoryPlayer = previousInventoryPlayer;
            Player = player;
            RestoreLocalOffscreenState = restoreLocalOffscreenState;
            tpUseFpAnimations = player.TpAnimManager.UseFpAnmations;
            selfUseFpAnimations = player.SelfFpAnimManager.UseFpAnmations;
            bodyYawLimits = player.BodyYawLimits;
            headYawLimits = player.HeadYawLimits;
            tpHeadController = player.TpAnimManager.HeadController;
        }

        internal void RestoreLocalState(EntityPlayer player)
        {
            if (!RestoreLocalOffscreenState || restored)
            {
                return;
            }

            restored = true;
            player.TpAnimManager.UseFpAnmations = tpUseFpAnimations;
            player.SelfFpAnimManager.UseFpAnmations = selfUseFpAnimations;
            player.BodyYawLimits = bodyYawLimits;
            player.HeadYawLimits = headYawLimits;
            player.TpAnimManager.HeadController = tpHeadController;
        }
    }
}

internal static class ReplayPlayerInventoryContext
{
    [ThreadStatic]
    internal static EntityPlayer? Current;
}

// Replay ghosts are not registered as real world players, so EntityPlayer.Player
// is null. VSEssentials resolves clothing through that property before asking
// the inventory manager for the character and backpack inventories. During a
// replay tessellation, borrow the local client player only as the inventory
// manager dispatch target; ReplayPlayerInventoryPatch replaces its result with
// the replay-owned inventory below.
[HarmonyPatch(typeof(EntityPlayer), nameof(EntityPlayer.Player), MethodType.Getter)]
internal static class ReplayPlayerIdentityPatch
{
    [HarmonyPostfix]
    private static void Postfix(EntityPlayer __instance, ref IPlayer? __result)
    {
        if (__result is not null
            || !ReferenceEquals(ReplayPlayerInventoryContext.Current, __instance)
            || DirectorReplayRuntime.Manager?.IsReplayThirdPerson(__instance.EntityId) != true)
        {
            return;
        }

        __result = (__instance.Api as ICoreClientAPI)?.World.Player;
    }
}

// VSEssentials 1.22.3 frees texture sub-id 0 even when a clothing behavior
// never allocated its dynamic skin region. Keep the rest of despawn cleanup
// intact while making that one unallocated release a no-op for replay ghosts.
internal static class ReplayClothingTextureGuard
{
    private static readonly Type? TexturedClothingType = AccessTools.TypeByName(
        "Vintagestory.GameContent.EntityBehaviorTexturedClothing");
    private static readonly FieldInfo? TextureSpaceAllocatedField = TexturedClothingType is null
        ? null
        : AccessTools.Field(TexturedClothingType, "textureSpaceAllocated");
    private static readonly FieldInfo? ClientApiField = TexturedClothingType is null
        ? null
        : AccessTools.Field(TexturedClothingType, "capi");

    internal static bool IsAvailable
        => TexturedClothingType is not null
            && TextureSpaceAllocatedField is not null
            && ClientApiField is not null;

    internal static bool AreTextureSpacesAllocated(
        IEnumerable<EntityBehavior>? behaviors)
    {
        if (!IsAvailable || behaviors is null)
        {
            return true;
        }

        foreach (EntityBehavior behavior in behaviors)
        {
            if (TexturedClothingType?.IsInstanceOfType(behavior) == true
                && TextureSpaceAllocatedField?.GetValue(behavior) is not true)
            {
                return false;
            }
        }

        return true;
    }

    internal static IDisposable SuppressUnallocatedTextureRelease(
        IEnumerable<EntityBehavior>? behaviors)
    {
        if (!IsAvailable || behaviors is null)
        {
            return Scope.Empty;
        }

        List<SuspendedClientApi>? suspended = null;
        foreach (EntityBehavior behavior in behaviors)
        {
            if (TexturedClothingType?.IsInstanceOfType(behavior) != true
                || TextureSpaceAllocatedField?.GetValue(behavior) is not false
                || ClientApiField?.GetValue(behavior) is not { } clientApi)
            {
                continue;
            }

            try
            {
                ClientApiField.SetValue(behavior, null);
                (suspended ??= new()).Add(new SuspendedClientApi(behavior, clientApi));
            }
            catch
            {
            }
        }

        return suspended is null ? Scope.Empty : new Scope(suspended);
    }

    private readonly record struct SuspendedClientApi(EntityBehavior Behavior, object ClientApi);

    private sealed class Scope : IDisposable
    {
        internal static readonly Scope Empty = new(null);

        private List<SuspendedClientApi>? suspended;

        internal Scope(List<SuspendedClientApi>? suspended)
        {
            this.suspended = suspended;
        }

        public void Dispose()
        {
            List<SuspendedClientApi>? restore = Interlocked.Exchange(ref suspended, null);
            if (restore is null || ClientApiField is null)
            {
                return;
            }

            for (int index = restore.Count - 1; index >= 0; index--)
            {
                try
                {
                    SuspendedClientApi entry = restore[index];
                    ClientApiField.SetValue(entry.Behavior, entry.ClientApi);
                }
                catch
                {
                }
            }
        }
    }
}

[HarmonyPatch(
    typeof(PlayerInventoryManager),
    nameof(PlayerInventoryManager.GetOwnInventory),
    new[] { typeof(string) })]
internal static class ReplayPlayerInventoryPatch
{
    internal static bool TryPatchPlayerBehaviorInventory(Harmony harmony, ILogger logger)
    {
        try
        {
            Type? behaviorType = AccessTools.TypeByName(
                "Vintagestory.GameContent.EntityBehaviorPlayerInventory");
            MethodInfo? getter = behaviorType is null
                ? null
                : AccessTools.PropertyGetter(behaviorType, "Inventory");
            MethodInfo? postfix = AccessTools.Method(
                typeof(ReplayPlayerInventoryPatch),
                nameof(PlayerBehaviorInventoryPostfix));
            if (getter is null || postfix is null)
            {
                logger.Warning(
                    "VS Director: replay player behavior inventory getter patch target was not available.");
                return false;
            }

            harmony.Patch(
                getter,
                postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning(
                "VS Director: replay player behavior inventory getter patch failed: {0}",
                exception.GetBaseException().Message);
            return false;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(string inventoryClassName, ref IInventory __result)
    {
        EntityPlayer? player = ReplayPlayerInventoryContext.Current;
        DirectorReplayManager? manager = DirectorReplayRuntime.Manager;
        if (player is null || manager?.IsReplayThirdPerson(player.EntityId) != true)
        {
            return;
        }

        // Never leak the surrogate local player's equipment into a replay
        // ghost while its first sample or inventory is still being prepared.
        __result = manager.TryGetReplayPlayerInventory(
                player.EntityId,
                inventoryClassName,
                out IInventory? replayInventory)
            ? replayInventory!
            : null!;
    }

    // EntityPlayerShapeRenderer checks this getter before calling any
    // tessellation behavior. A replay ghost has no IPlayer registered in the
    // world, so the original getter returns null and exits before clothing
    // can contribute its mesh. Resolve the replay inventory directly from
    // the behavior's owning entity instead of borrowing a live player.
    private static void PlayerBehaviorInventoryPostfix(
        object __instance,
        ref InventoryBase? __result)
    {
        if (__instance is not EntityBehavior behavior
            || behavior.entity is not EntityPlayer player
            || DirectorReplayRuntime.Manager?.IsReplayThirdPerson(player.EntityId) != true)
        {
            return;
        }

        DirectorReplayManager? manager = DirectorReplayRuntime.Manager;
        if (manager?.TryGetReplayPlayerInventory(
                player.EntityId,
                "character",
                out IInventory? replayInventory) == true
            && replayInventory is InventoryBase inventoryBase)
        {
            __result = inventoryBase;
        }
        else
        {
            // Keep the engine's early guard honest while a binding is still
            // being prepared. In particular, never expose the local player's
            // equipment as a fallback for a replay ghost.
            __result = null;
        }
    }
}

// EntityPlayerShapeRenderer has a separate IsSelf property based directly on
// entity id. A replay that falls back to the already loaded local actor can
// therefore still select the split first-person arm mesh even though
// EntityPlayer.AnimManager is forced to third person. VSEssentials is loaded
// as a game mod rather than an API reference, so this target is installed
// dynamically after the client mod assemblies are available.
internal static class ReplayPlayerShapeRendererPatch
{
    private static FieldInfo? rendererEntityField;
    private static FieldInfo? rendererModeField;
    private static FieldInfo? bodyYawLerpedField;
    private static FieldInfo? smoothedBodyYawField;
    private static FieldInfo? meshRefOpaqueField;
    private static FieldInfo? firstPersonMeshRefField;
    private static FieldInfo? thirdPersonMeshRefField;

    internal static bool TryPatch(Harmony harmony, ILogger logger)
    {
        try
        {
            Type? rendererType = AccessTools.TypeByName("Vintagestory.GameContent.EntityPlayerShapeRenderer");
            MethodInfo? getter = rendererType is null
                ? null
                : AccessTools.PropertyGetter(rendererType, "IsSelf");
            MethodInfo? render = rendererType is null
                ? null
                : AccessTools.Method(rendererType, "DoRender3DOpaque", new[] { typeof(float), typeof(bool) });
            MethodInfo? renderBatched = rendererType is null
                ? null
                : AccessTools.Method(rendererType, "DoRender3DOpaqueBatched", new[] { typeof(float), typeof(bool) });
            MethodInfo? beforeRender = rendererType is null
                ? null
                : AccessTools.Method(rendererType, nameof(EntityRenderer.BeforeRender), new[] { typeof(float) });
            MethodInfo? tesselateShape = rendererType is null
                ? null
                : AccessTools.Method(rendererType, "TesselateShape", Type.EmptyTypes);
            rendererEntityField = rendererType is null ? null : AccessTools.Field(rendererType, "entity");
            rendererModeField = rendererType is null ? null : AccessTools.Field(rendererType, "renderMode");
            bodyYawLerpedField = rendererType is null ? null : AccessTools.Field(rendererType, "bodyYawLerped");
            smoothedBodyYawField = rendererType is null ? null : AccessTools.Field(rendererType, "smoothedBodyYaw");
            meshRefOpaqueField = rendererType is null ? null : AccessTools.Field(rendererType, "meshRefOpaque");
            firstPersonMeshRefField = rendererType is null ? null : AccessTools.Field(rendererType, "firstPersonMeshRef");
            thirdPersonMeshRefField = rendererType is null ? null : AccessTools.Field(rendererType, "thirdPersonMeshRef");
            MethodInfo? postfix = AccessTools.Method(typeof(ReplayPlayerShapeRendererPatch), nameof(Postfix));
            MethodInfo? renderPrefix = AccessTools.Method(
                typeof(ReplayPlayerShapeRendererPatch),
                nameof(RenderPrefix));
            MethodInfo? renderPostfix = AccessTools.Method(
                typeof(ReplayPlayerShapeRendererPatch),
                nameof(RenderPostfix));
            MethodInfo? renderFinalizer = AccessTools.Method(
                typeof(ReplayPlayerShapeRendererPatch),
                nameof(RenderFinalizer));
            MethodInfo? tesselationPrefix = AccessTools.Method(
                typeof(ReplayPlayerShapeRendererPatch),
                nameof(TesselationPrefix));
            MethodInfo? tesselationPostfix = AccessTools.Method(
                typeof(ReplayPlayerShapeRendererPatch),
                nameof(TesselationPostfix));
            MethodInfo? tesselationFinalizer = AccessTools.Method(
                typeof(ReplayPlayerShapeRendererPatch),
                nameof(TesselationFinalizer));
            if (getter is null
                || render is null
                || renderBatched is null
                || beforeRender is null
                || tesselateShape is null
                || rendererEntityField is null
                || rendererModeField is null
                || meshRefOpaqueField is null
                || firstPersonMeshRefField is null
                || thirdPersonMeshRefField is null
                || postfix is null
                || renderPrefix is null
                || renderPostfix is null
                || renderFinalizer is null
                || tesselationPrefix is null
                || tesselationPostfix is null
                || tesselationFinalizer is null)
            {
                logger.Warning("VS Director: replay player renderer patch target was not available.");
                return false;
            }

            harmony.Patch(getter, postfix: new HarmonyMethod(postfix));
            HarmonyMethod visibilityPrefix = new(renderPrefix) { priority = Priority.First };
            HarmonyMethod modePostfix = new(renderPostfix) { priority = Priority.Last };
            HarmonyMethod modeFinalizer = new(renderFinalizer) { priority = Priority.Last };
            harmony.Patch(render, prefix: visibilityPrefix, postfix: modePostfix, finalizer: modeFinalizer);
            harmony.Patch(renderBatched, prefix: visibilityPrefix, postfix: modePostfix, finalizer: modeFinalizer);
            harmony.Patch(
                tesselateShape,
                prefix: new HarmonyMethod(tesselationPrefix) { priority = Priority.First },
                postfix: new HarmonyMethod(tesselationPostfix) { priority = Priority.Last },
                finalizer: new HarmonyMethod(tesselationFinalizer) { priority = Priority.Last });
            harmony.Patch(
                beforeRender,
                prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(ReplayPlayerShapeRendererPatch), nameof(BeforeRenderPrefix)))
                {
                    priority = Priority.First
                });
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning("VS Director: replay player renderer patch failed: {0}", exception.Message);
            return false;
        }
    }

    private static void Postfix(object __instance, ref bool __result)
    {
        if (__result && DirectorIndependentPlayerRendererRegistry.Contains(__instance))
        {
            __result = false;
            return;
        }
        if (__result
            && rendererEntityField?.GetValue(__instance) is Entity entity
            && entity is EntityPlayer player
            && ReplayPlayerRenderMode.UseThirdPerson(player))
        {
            __result = false;
        }
    }

    private static bool RenderPrefix(object __instance, ref object? __state)
    {
        RenderState state = new();
        __state = state;
        if (rendererEntityField?.GetValue(__instance) is not Entity entity)
        {
            __state = null;
            return true;
        }
        if (!EnsurePlayerMeshesUsable(__instance, entity))
        {
            __state = null;
            return false;
        }

        if (DirectorReplayRuntime.Manager is { } manager
            && (manager.IsLiveEntitySuppressed(entity)
                || !manager.ShouldRenderReplayEntity(entity.EntityId)))
        {
            return false;
        }

        if (entity is EntityPlayer
            && DirectorReplayRuntime.Manager is { } replayManager
            && replayManager.IsReplayThirdPerson(entity.EntityId)
            && replayManager.TryGetReplayThirdPersonBodyYaw(
                entity.EntityId,
                out float replayBodyYaw))
        {
            // The vanilla third-person renderer keeps a second, slowly
            // interpolated body yaw. Replay stores a detached third-person
            // body pose even when the source was recorded in first person, so
            // seeks and camera cuts can restore the same head/body separation
            // without depending on stale renderer history.
            bodyYawLerpedField?.SetValue(__instance, replayBodyYaw);
            smoothedBodyYawField?.SetValue(__instance, replayBodyYaw);
            state.Entity = entity;
            state.OriginalYaw = entity.Pos.Yaw;
            state.RestoreYaw = true;
            // EntityPlayerShapeRenderer's third-person matrix path uses
            // Pos.Yaw (not EntityAgent.BodyYaw), and overwrites its own
            // smoothing fields during the call. Bind the recorded body yaw
            // for the render scope so the camera/head yaw cannot flatten the
            // whole actor into a rigid facing direction.
            entity.Pos.Yaw = replayBodyYaw;
        }

        if (DirectorOffscreenRenderRuntime.CurrentView is { RenderLocalPlayer: true }
            && entity is EntityPlayer player
            && DirectorCameraRuntime.Controller?.IsLocalPlayerEntity(player) == true
            && !DirectorIndependentPlayerRendererRegistry.Contains(__instance)
            && rendererModeField is not null)
        {
            __state = rendererModeField.GetValue(__instance);
            rendererModeField.SetValue(__instance, Enum.ToObject(rendererModeField.FieldType, 2));
            state.RenderMode = __state;
            __state = state;
        }
        return true;
    }

    private static bool EnsurePlayerMeshesUsable(object renderer, Entity entity)
    {
        bool invalid = false;
        foreach (FieldInfo? field in new[]
        {
            meshRefOpaqueField,
            firstPersonMeshRefField,
            thirdPersonMeshRefField
        })
        {
            if (field?.GetValue(renderer) is not MultiTextureMeshRef mesh
                || (!mesh.Disposed && mesh.Initialized))
            {
                continue;
            }

            field.SetValue(renderer, null);
            invalid = true;
        }
        if (!invalid)
        {
            return true;
        }

        entity.MarkShapeModified();
        return false;
    }

    private static bool BeforeRenderPrefix(object __instance)
    {
        if (rendererEntityField?.GetValue(__instance) is not Entity entity
            || DirectorReplayRuntime.Manager is not { } manager)
        {
            return true;
        }

        if (!manager.IsLiveEntitySuppressed(entity)
            && manager.ShouldRenderReplayEntity(entity.EntityId))
        {
            return true;
        }

        entity.IsRendered = false;
        entity.IsShadowRendered = false;
        return false;
    }

    private static void TesselationPrefix(object __instance, ref EntityPlayer? __state)
    {
        __state = ReplayPlayerInventoryContext.Current;
        if (rendererEntityField?.GetValue(__instance) is EntityPlayer player
            && DirectorReplayRuntime.Manager?.IsReplayThirdPerson(player.EntityId) == true)
        {
            ReplayPlayerInventoryContext.Current = player;
        }
    }

    private static void TesselationPostfix(EntityPlayer? __state)
        => ReplayPlayerInventoryContext.Current = __state;

    private static Exception? TesselationFinalizer(
        EntityPlayer? __state,
        Exception? __exception)
    {
        ReplayPlayerInventoryContext.Current = __state;
        return __exception;
    }

    private static void RenderPostfix(object __instance, object? __state)
        => RestoreRenderState(__instance, __state);

    private static Exception? RenderFinalizer(object __instance, object? __state, Exception? __exception)
    {
        RestoreRenderState(__instance, __state);
        return __exception;
    }

    private static void RestoreRenderState(object instance, object? state)
    {
        if (state is not RenderState renderState)
        {
            rendererModeField?.SetValue(instance, state);
            return;
        }
        if (renderState.RenderMode is not null)
        {
            rendererModeField?.SetValue(instance, renderState.RenderMode);
        }
        if (renderState.RestoreYaw && renderState.Entity is not null)
        {
            renderState.Entity.Pos.Yaw = renderState.OriginalYaw;
        }
    }

    private sealed class RenderState
    {
        internal object? RenderMode { get; set; }

        internal Entity? Entity { get; set; }

        internal float OriginalYaw { get; set; }

        internal bool RestoreYaw { get; set; }
    }
}

// Most creatures use VSEssentials' EntityShapeRenderer. SystemRenderEntities
// sets IsRendered before calling BeforeRender, so hiding an entity only after
// the culling pass still performs its mesh preparation and animation work.
// Guard the shape renderer entry points themselves to avoid processing the
// live copy while its replay ghost is active.
internal static class ReplayEntityShapeRendererPatch
{
    private static FieldInfo? rendererEntityField;

    internal static bool TryPatch(Harmony harmony, ILogger logger)
    {
        try
        {
            Type? rendererType = AccessTools.TypeByName("Vintagestory.GameContent.EntityShapeRenderer");
            rendererEntityField = rendererType is null
                ? null
                : AccessTools.Field(rendererType, "entity");
            MethodInfo? beforeRender = rendererType is null
                ? null
                : AccessTools.Method(rendererType, nameof(EntityRenderer.BeforeRender), new[] { typeof(float) });
            MethodInfo? opaque = rendererType is null
                ? null
                : AccessTools.Method(rendererType, "DoRender3DOpaque", new[] { typeof(float), typeof(bool) });
            MethodInfo? opaqueBatched = rendererType is null
                ? null
                : AccessTools.Method(rendererType, "DoRender3DOpaqueBatched", new[] { typeof(float), typeof(bool) });
            MethodInfo? prefix = AccessTools.Method(
                typeof(ReplayEntityShapeRendererPatch),
                nameof(Prefix));
            if (rendererEntityField is null
                || beforeRender is null
                || opaque is null
                || opaqueBatched is null
                || prefix is null)
            {
                logger.Warning("VS Director: replay entity shape renderer patch target was not available.");
                return false;
            }

            HarmonyMethod prefixMethod = new(prefix) { priority = Priority.First };
            harmony.Patch(beforeRender, prefix: prefixMethod);
            harmony.Patch(opaque, prefix: prefixMethod);
            harmony.Patch(opaqueBatched, prefix: prefixMethod);
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning("VS Director: replay entity shape renderer patch failed: {0}", exception.Message);
            return false;
        }
    }

    private static bool Prefix(object __instance)
    {
        if (rendererEntityField?.GetValue(__instance) is not Entity entity
            || DirectorReplayRuntime.Manager is not { } manager)
        {
            return true;
        }

        bool render = !manager.IsLiveEntitySuppressed(entity)
            && manager.ShouldRenderReplayEntity(entity.EntityId);
        if (!render)
        {
            entity.IsRendered = false;
            entity.IsShadowRendered = false;
            return false;
        }
        return true;
    }
}

internal static class ReplayPositionInterpolationIsolationPatch
{
    private static FieldInfo? behaviorEntityField;

    internal static bool TryPatch(Harmony harmony, ILogger logger)
    {
        try
        {
            Type? interpolationType = AccessTools.TypeByName("EntityBehaviorInterpolatePosition");
            MethodInfo? render = interpolationType is null
                ? null
                : AccessTools.Method(
                    interpolationType,
                    "OnRenderFrame",
                    new[] { typeof(float), typeof(EnumRenderStage) });
            behaviorEntityField = interpolationType is null
                ? null
                : AccessTools.Field(interpolationType, "entity");
            MethodInfo? prefix = AccessTools.Method(
                typeof(ReplayPositionInterpolationIsolationPatch),
                nameof(Prefix));
            MethodInfo? postfix = AccessTools.Method(
                typeof(ReplayPositionInterpolationIsolationPatch),
                nameof(Postfix));
            if (render is null || behaviorEntityField is null || prefix is null || postfix is null)
            {
                logger.Warning("VS Director: replay position-interpolation patch target was not available.");
                return false;
            }

            harmony.Patch(
                render,
                prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                postfix: new HarmonyMethod(postfix));
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning("VS Director: replay position-interpolation patch failed: {0}", exception.Message);
            return false;
        }
    }

    private static bool Prefix(object __instance)
        => behaviorEntityField?.GetValue(__instance) is not Entity entity
            || DirectorReplayRuntime.Manager?.IsReplayGhost(entity.EntityId) != true;

    private static void Postfix(object __instance)
    {
        if (behaviorEntityField?.GetValue(__instance) is Entity entity
            && DirectorReplayRuntime.Manager?.IsReplayGhost(entity.EntityId) != true)
        {
            DirectorReplayRuntime.Manager?.CaptureRenderedPose(entity);
        }
    }
}

// SystemRenderEntities performs a separate shadow-frustum pass which can call
// EntityItemRenderer even after the normal visibility pass hid a future replay
// actor. Keep the item renderer itself behind the replay lifetime boundary.
internal static class ReplayItemRendererVisibilityPatch
{
    private static FieldInfo? rendererEntityField;

    [ThreadStatic]
    private static Entity? renderingEntity;

    internal static bool TryPatch(Harmony harmony, ILogger logger)
    {
        try
        {
            Type? rendererType = AccessTools.TypeByName("Vintagestory.GameContent.EntityItemRenderer");
            MethodInfo? render = rendererType is null
                ? null
                : AccessTools.Method(rendererType, "DoRender3DOpaque", new[] { typeof(float), typeof(bool) });
            MethodInfo? loadModelMatrix = rendererType is null
                ? null
                : AccessTools.Method(
                    rendererType,
                    "LoadModelMatrix",
                    new[] { typeof(ItemRenderInfo), typeof(bool), typeof(float) });
            rendererEntityField = rendererType is null ? null : AccessTools.Field(rendererType, "entity");
            MethodInfo? prefix = AccessTools.Method(
                typeof(ReplayItemRendererVisibilityPatch),
                nameof(Prefix));
            MethodInfo? postfix = AccessTools.Method(
                typeof(ReplayItemRendererVisibilityPatch),
                nameof(Postfix));
            MethodInfo? finalizer = AccessTools.Method(
                typeof(ReplayItemRendererVisibilityPatch),
                nameof(Finalizer));
            MethodInfo? transpiler = AccessTools.Method(
                typeof(ReplayItemRendererVisibilityPatch),
                nameof(Transpiler));
            if (render is null
                || loadModelMatrix is null
                || rendererEntityField is null
                || prefix is null
                || postfix is null
                || finalizer is null
                || transpiler is null)
            {
                logger.Warning("VS Director: replay item visibility patch target was not available.");
                return false;
            }

            harmony.Patch(
                render,
                prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                postfix: new HarmonyMethod(postfix) { priority = Priority.Last },
                finalizer: new HarmonyMethod(finalizer) { priority = Priority.Last });
            harmony.Patch(loadModelMatrix, transpiler: new HarmonyMethod(transpiler));
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning("VS Director: replay item visibility patch failed: {0}", exception.Message);
            return false;
        }
    }

    private static bool Prefix(object __instance, ref float __0, ref Entity? __state)
    {
        __state = renderingEntity;
        if (rendererEntityField?.GetValue(__instance) is not Entity entity
            || DirectorReplayRuntime.Manager is not { } manager)
        {
            return true;
        }

        bool render = !manager.IsLiveEntitySuppressed(entity)
            && manager.ShouldRenderReplayEntity(entity.EntityId);
        if (!render)
        {
            return false;
        }

        renderingEntity = entity;
        if (manager.PrepareReplayItemRenderState(entity, __instance))
        {
            // The captured renderer angles are the pose for this exact replay
            // sample. Do not advance them again with physical render time.
            __0 = 0f;
        }
        return true;
    }

    private static void Postfix(Entity? __state)
        => renderingEntity = __state;

    private static Exception? Finalizer(Entity? __state, Exception? __exception)
    {
        renderingEntity = __state;
        return __exception;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo? elapsedGetter = AccessTools.PropertyGetter(
            typeof(IWorldAccessor),
            nameof(IWorldAccessor.ElapsedMilliseconds));
        MethodInfo replacement = AccessTools.Method(
            typeof(ReplayItemRendererVisibilityPatch),
            nameof(ReplayElapsedMilliseconds));
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo method
                && ((elapsedGetter is not null && method == elapsedGetter)
                    || (method.Name == "get_ElapsedMilliseconds"
                        && method.ReturnType == typeof(long)
                        && method.GetParameters().Length == 0)))
            {
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = replacement;
            }
            yield return instruction;
        }
    }

    private static long ReplayElapsedMilliseconds(IWorldAccessor world)
        => renderingEntity is { } entity
            && DirectorReplayRuntime.Manager?.TryGetReplayItemRenderElapsedMilliseconds(
                entity.EntityId,
                out long elapsedMilliseconds) == true
                ? elapsedMilliseconds
                : world.ElapsedMilliseconds;
}

// Boats use a separate OIT renderer to write a hull-shaped depth mask that
// hides the water surface. It ignores Entity.IsRendered, so a suppressed live
// boat otherwise leaves a black silhouette while its replay ghost is visible.
internal static class ReplayHideWaterSurfaceVisibilityPatch
{
    internal static bool TryPatch(Harmony harmony, ILogger logger)
    {
        try
        {
            Type? behaviorType = AccessTools.TypeByName(
                "Vintagestory.GameContent.EntityBehaviorHideWaterSurface");
            MethodInfo? render = behaviorType is null
                ? null
                : AccessTools.Method(
                    behaviorType,
                    "OnRenderFrame",
                    new[] { typeof(float), typeof(EnumRenderStage) });
            MethodInfo? prefix = AccessTools.Method(
                typeof(ReplayHideWaterSurfaceVisibilityPatch),
                nameof(Prefix));
            if (render is null || prefix is null)
            {
                logger.Warning("VS Director: replay water-mask visibility patch target was not available.");
                return false;
            }

            harmony.Patch(
                render,
                prefix: new HarmonyMethod(prefix) { priority = Priority.First });
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning("VS Director: replay water-mask visibility patch failed: {0}", exception.Message);
            return false;
        }
    }

    private static bool Prefix(object __instance)
    {
        if (__instance is not EntityBehavior behavior
            || DirectorReplayRuntime.Manager is not { } manager)
        {
            return true;
        }

        Entity entity = behavior.entity;
        return !manager.IsLiveEntitySuppressed(entity)
            && manager.ShouldRenderReplayEntity(entity.EntityId);
    }
}

// Falling blocks bypass the normal entity renderer entirely. Their dedicated
// renderer, particle system, and physics tick all keep private collections and
// do not honor Entity.IsRendered. Replay ghosts must therefore be isolated at
// those game-content entry points as well as at the generic entity pass.
internal static class ReplayFallingBlockIsolationPatch
{
    private static FieldInfo? fallSoundField;
    private static FieldInfo? doRemoveBlockField;
    private static FieldInfo? initialBlockRemovedField;

    internal static bool TryPatch(Harmony harmony, ILogger logger)
    {
        try
        {
            Type? fallingType = AccessTools.TypeByName("Vintagestory.GameContent.EntityBlockFalling");
            Type? rendererType = AccessTools.TypeByName(
                "Vintagestory.GameContent.ModSystemRenderFallingBlocksFast");
            Type? particleType = AccessTools.TypeByName(
                "Vintagestory.GameContent.FallingBlockParticlesModSystem");
            MethodInfo? initialize = fallingType is null
                ? null
                : AccessTools.Method(fallingType, nameof(Entity.Initialize));
            MethodInfo? tick = fallingType is null
                ? null
                : AccessTools.Method(fallingType, nameof(Entity.OnGameTick), new[] { typeof(float) });
            MethodInfo? isRendered = fallingType is null || rendererType is null
                ? null
                : AccessTools.Method(rendererType, "IsRendered", new[] { fallingType });
            MethodInfo? register = fallingType is null || particleType is null
                ? null
                : AccessTools.Method(particleType, "Register", new[] { fallingType });
            fallSoundField = fallingType is null ? null : AccessTools.Field(fallingType, "fallSound");
            doRemoveBlockField = fallingType is null ? null : AccessTools.Field(fallingType, "DoRemoveBlock");
            initialBlockRemovedField = fallingType is null
                ? null
                : AccessTools.Field(fallingType, "InitialBlockRemoved");
            MethodInfo? initializePrefix = AccessTools.Method(
                typeof(ReplayFallingBlockIsolationPatch),
                nameof(InitializePrefix));
            MethodInfo? tickPrefix = AccessTools.Method(
                typeof(ReplayFallingBlockIsolationPatch),
                nameof(TickPrefix));
            MethodInfo? renderPostfix = AccessTools.Method(
                typeof(ReplayFallingBlockIsolationPatch),
                nameof(RenderPostfix));
            MethodInfo? registerPrefix = AccessTools.Method(
                typeof(ReplayFallingBlockIsolationPatch),
                nameof(RegisterPrefix));
            if (initialize is null
                || tick is null
                || isRendered is null
                || register is null
                || fallSoundField is null
                || doRemoveBlockField is null
                || initialBlockRemovedField is null
                || initializePrefix is null
                || tickPrefix is null
                || renderPostfix is null
                || registerPrefix is null)
            {
                logger.Warning("VS Director: replay falling-block isolation targets were not available.");
                return false;
            }

            harmony.Patch(
                initialize,
                prefix: new HarmonyMethod(initializePrefix) { priority = Priority.First });
            harmony.Patch(
                tick,
                prefix: new HarmonyMethod(tickPrefix) { priority = Priority.First });
            harmony.Patch(isRendered, postfix: new HarmonyMethod(renderPostfix));
            harmony.Patch(
                register,
                prefix: new HarmonyMethod(registerPrefix) { priority = Priority.First });
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning("VS Director: replay falling-block isolation patch failed: {0}", exception.Message);
            return false;
        }
    }

    private static void InitializePrefix(object __instance)
    {
        if (__instance is not Entity entity
            || DirectorReplayRuntime.Manager is not { } manager)
        {
            return;
        }

        bool replayGhost = manager.IsReplayGhost(entity.EntityId);
        if (!replayGhost && !manager.IsLiveEntitySuppressed(entity))
        {
            return;
        }

        // EntityBlockFalling.Initialize normally removes its source block and
        // starts its live fall sound. World events and recorded sounds own both
        // effects during replay.
        doRemoveBlockField?.SetValue(__instance, false);
        fallSoundField?.SetValue(__instance, null);
        if (replayGhost)
        {
            // The live entity sets this during its first physics ticks. Replay
            // ghosts deliberately skip those ticks, so leaving the default
            // false makes the fast renderer reject the ghost whenever the
            // source cell contains another snow layer or restored block.
            initialBlockRemovedField?.SetValue(__instance, true);
        }
    }

    private static bool TickPrefix(object __instance)
        => __instance is not Entity entity
            || DirectorReplayRuntime.Manager is not { } manager
            || (!manager.IsReplayGhost(entity.EntityId)
                && !manager.IsLiveEntitySuppressed(entity));

    private static void RenderPostfix(object __0, ref bool __result)
    {
        if (!__result
            || __0 is not Entity entity
            || DirectorReplayRuntime.Manager is not { } manager)
        {
            return;
        }

        __result = !manager.IsLiveEntitySuppressed(entity)
            && manager.ShouldRenderReplayEntity(entity.EntityId);
    }

    private static bool RegisterPrefix(object __0)
        => __0 is not Entity entity
            || DirectorReplayRuntime.Manager is not { } manager
            || (!manager.IsReplayGhost(entity.EntityId)
                && !manager.IsLiveEntitySuppressed(entity));
}

// Replay items are positioned and restored explicitly from recorded samples.
// Letting the client tick them would run OnGroundIdle, cooling, and other live
// world behavior before a future lifetime begins.
[HarmonyPatch(typeof(EntityItem), nameof(EntityItem.OnGameTick))]
internal static class ReplayItemTickIsolationPatch
{
    [HarmonyPrefix]
    private static bool Prefix(EntityItem __instance)
        => DirectorReplayRuntime.Manager?.IsReplayGhost(__instance.EntityId) != true;
}

[HarmonyPatch(typeof(ParticleManager), "copyLoadedEntitiesList2sec")]
internal static class ReplayAsyncParticleEntityIsolationPatch
{
    private static readonly FieldInfo? LoadedEntitiesField = AccessTools.Field(
        typeof(ParticleManager),
        "loadedEntities");

    [HarmonyPostfix]
    private static void Postfix(ParticleManager __instance)
    {
        if (LoadedEntitiesField?.GetValue(__instance) is not Dictionary<long, Entity> loaded
            || DirectorReplayRuntime.Manager is not { } manager)
        {
            return;
        }

        Dictionary<long, Entity> filtered = loaded
            .Where(entry => !manager.IsReplayGhost(entry.Key)
                && !manager.IsLiveEntitySuppressed(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        LoadedEntitiesField.SetValue(__instance, filtered);
    }
}

[HarmonyPatch(typeof(SystemRenderParticles), nameof(SystemRenderParticles.OnRenderFrame3D))]
internal static class ReplayOpaqueParticleTimePatch
{
    [HarmonyPrefix]
    private static void Prefix(ref float deltaTime)
    {
        if (DirectorReplayRuntime.Manager is { } manager)
        {
            manager.BeginReplayParticleRenderFrame();
            deltaTime = manager.ReplayParticleDeltaTime(deltaTime);
        }
    }
}

// Drive both main-thread and asynchronous pools from the replay clock. This
// keeps particles spawned through direct pool APIs (including block debris)
// moving and lets the original pool update its mesh without a second full scan.
// ParticlePoolCubes inherits this method, so the guard is per pool.
[HarmonyPatch(typeof(ParticlePoolQuads), nameof(ParticlePoolQuads.OnNewFrame))]
internal static class ReplayParticlePoolTimePatch
{
    private static readonly FieldInfo? OffthreadField = AccessTools.Field(
        typeof(ParticlePoolQuads),
        "offthread");

    [HarmonyPrefix]
    private static void Prefix(ParticlePoolQuads __instance, ref float dt)
    {
        if (DirectorReplayRuntime.Manager is not { } manager)
        {
            return;
        }

        bool offthread = OffthreadField?.GetValue(__instance) is true;
        dt = manager.ReplayParticlePoolDeltaTime(__instance, offthread, dt);
    }
}

[HarmonyPatch(typeof(SystemRenderParticles), nameof(SystemRenderParticles.OnRenderFrame3DOIT))]
internal static class ReplayOitParticleTimePatch
{
    [HarmonyPrefix]
    private static void Prefix(ref float deltaTime)
    {
        if (DirectorReplayRuntime.Manager is { } manager)
        {
            deltaTime = manager.ReplayParticleDeltaTime(deltaTime);
        }
    }

    [HarmonyPostfix]
    private static void Postfix()
    {
        DirectorReplayRuntime.Manager?.CompleteReplayParticleFrame();
    }
}

[HarmonyPatch(
    typeof(ClientMain),
    nameof(ClientMain.SpawnParticles),
    new[]
    {
        typeof(float), typeof(int), typeof(Vec3d), typeof(Vec3d), typeof(Vec3f), typeof(Vec3f),
        typeof(float), typeof(float), typeof(float), typeof(EnumParticleModel), typeof(IPlayer)
    })]
internal static class ReplaySimpleParticleCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        float quantity,
        int color,
        Vec3d minPos,
        Vec3d maxPos,
        Vec3f minVelocity,
        Vec3f maxVelocity,
        float lifeLength,
        float gravityEffect,
        float scale,
        EnumParticleModel model)
    {
        DirectorReplayManager? manager = DirectorReplayRuntime.Manager;
        manager?.CaptureParticle(
            quantity,
            color,
            minPos,
            maxPos,
            minVelocity,
            maxVelocity,
            lifeLength,
            gravityEffect,
            scale,
            model);
        return manager?.ShouldSuppressLiveParticleSpawns != true;
    }
}

[HarmonyPatch(
    typeof(ClientMain),
    nameof(ClientMain.SpawnParticles),
    new[] { typeof(IParticlePropertiesProvider), typeof(IPlayer) })]
internal static class ReplayProviderParticleCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(IParticlePropertiesProvider particlePropertiesProvider)
    {
        DirectorReplayManager? manager = DirectorReplayRuntime.Manager;
        manager?.CaptureParticle(particlePropertiesProvider);
        return manager?.ShouldSuppressLiveParticleSpawns != true;
    }
}

[HarmonyPatch(
    typeof(SystemRenderParticles),
    nameof(SystemRenderParticles.Spawn),
    new[] { typeof(IParticlePropertiesProvider) })]
internal static class ReplayAsyncParticleCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(IParticlePropertiesProvider particleProperties)
    {
        DirectorReplayManager? manager = DirectorReplayRuntime.Manager;
        manager?.CaptureParticle(particleProperties);
        return ShouldAllowParticleSpawn(
            manager?.ShouldSuppressLiveParticleSpawns == true);
    }

    internal static bool ShouldAllowParticleSpawn(bool suppressLiveParticleSpawns)
        => !suppressLiveParticleSpawns
            || DirectorReplayWeatherVisualPatch.AllowsLiveParticleSpawns;
}

[HarmonyPatch(
    typeof(ClientMain),
    "PlaySoundAtInternal",
    new[]
    {
        typeof(AssetLocation), typeof(double), typeof(double), typeof(double), typeof(float), typeof(float),
        typeof(float), typeof(EnumSoundType)
    })]
internal static class ReplaySoundCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        ClientMain __instance,
        AssetLocation? location,
        double x,
        double y,
        double z,
        float volume,
        float pitch,
        float range,
        EnumSoundType soundType)
    {
        DirectorBlockSoundRelay.Capture(
            location,
            x,
            y,
            z,
            volume,
            pitch,
            range,
            soundType);
        DirectorLiveOffscreenRuntime.Session?.CaptureSoundEvent(
            __instance,
            location,
            x,
            y,
            z,
            volume,
            pitch,
            range,
            soundType);
        return !DirectorFollowSoundIsolation.ShouldSuppressObserverRelativeSound(
            x,
            y,
            z,
            soundType);
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo original = AccessTools.Method(
                typeof(EntityPos),
                nameof(EntityPos.SquareDistanceTo),
                new[] { typeof(double), typeof(double), typeof(double) })
            ?? throw new MissingMethodException(typeof(EntityPos).FullName, nameof(EntityPos.SquareDistanceTo));
        MethodInfo replacement = AccessTools.Method(
                typeof(DirectorFollowSoundIsolation),
                nameof(DirectorFollowSoundIsolation.DistanceSquaredFromActiveListener))
            ?? throw new MissingMethodException(
                typeof(DirectorFollowSoundIsolation).FullName,
                nameof(DirectorFollowSoundIsolation.DistanceSquaredFromActiveListener));
        int replacements = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method == original)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replacements++;
            }
            yield return instruction;
        }

        if (replacements != 1)
        {
            throw new InvalidOperationException(
                $"Expected one ClientMain sound-distance check, found {replacements}.");
        }
    }
}

[HarmonyPatch(
    typeof(ClientMain),
    nameof(ClientMain.StartPlaying),
    new[] { typeof(ILoadedSound), typeof(AssetLocation) })]
internal static class ReplayClientSoundStartCapturePatch
{
    [HarmonyPrefix]
    private static void Prefix(ILoadedSound loadedSound)
        => DirectorReplayRuntime.Manager?.RegisterReplayLoadedSound(loadedSound, loadedSound.Params);

    [HarmonyPostfix]
    private static void Postfix(ILoadedSound loadedSound)
        => DirectorReplayRuntime.Manager?.CaptureClientSoundStart(loadedSound);
}

[HarmonyPatch(typeof(ClientMain), nameof(ClientMain.LoadSound), new[] { typeof(SoundParams) })]
internal static class ReplayLoadedSoundRegistrationPatch
{
    [HarmonyPostfix]
    private static void Postfix(SoundParams sound, ILoadedSound? __result)
        => DirectorReplayRuntime.Manager?.RegisterReplayLoadedSound(__result, sound);
}

[HarmonyPatch(typeof(LoadedSoundNative), nameof(LoadedSoundNative.Start))]
internal static class ReplayLoadedSoundStartCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(LoadedSoundNative __instance)
    {
        if (DirectorFollowSoundIsolation.ShouldSuppressObserverRelativeSound(__instance.Params))
        {
            return false;
        }

        if (DirectorReplayRuntime.Manager is not { } manager)
        {
            return true;
        }

        manager.RegisterReplayLoadedSound(__instance, __instance.Params);
        return manager.ShouldAllowLoadedSoundStart(__instance);
    }

    [HarmonyPostfix]
    private static void Postfix(LoadedSoundNative __instance, bool __runOriginal)
    {
        if (!__runOriginal || DirectorReplayRuntime.Manager is not { } manager)
        {
            return;
        }

        manager.RegisterReplayLoadedSound(__instance, __instance.Params);
        manager.CaptureReplayLoadedSoundStart(__instance);
    }
}

[HarmonyPatch(typeof(LoadedSoundNative), nameof(LoadedSoundNative.Pause))]
internal static class ReplayLoadedSoundPauseCapturePatch
{
    [HarmonyPostfix]
    private static void Postfix(LoadedSoundNative __instance)
        => DirectorReplayRuntime.Manager?.CaptureReplayLoadedSoundState(
            __instance,
            DirectorReplaySoundAction.Pause);
}

[HarmonyPatch(typeof(LoadedSoundNative), nameof(LoadedSoundNative.Stop))]
internal static class ReplayLoadedSoundStopCapturePatch
{
    [HarmonyPrefix]
    private static void Prefix(LoadedSoundNative __instance)
        => DirectorReplayRuntime.Manager?.CaptureReplayLoadedSoundState(
            __instance,
            DirectorReplaySoundAction.Stop);
}

[HarmonyPatch(typeof(LoadedSoundNative), nameof(LoadedSoundNative.Dispose))]
internal static class ReplayLoadedSoundDisposeCapturePatch
{
    [HarmonyPrefix]
    private static void Prefix(LoadedSoundNative __instance)
        => DirectorReplayRuntime.Manager?.CaptureReplayLoadedSoundState(
            __instance,
            DirectorReplaySoundAction.Stop);
}

[HarmonyPatch(typeof(LoadedSoundNative), nameof(LoadedSoundNative.SetVolume), new[] { typeof(float) })]
internal static class ReplayLoadedSoundVolumeIsolationPatch
{
    [HarmonyPrefix]
    private static bool Prefix(LoadedSoundNative __instance, float val)
        => DirectorReplayRuntime.Manager?.ShouldApplyLoadedSoundVolume(__instance, val) != false;
}

[HarmonyPatch]
internal static class ReplayLoadedSoundParameterCapturePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(
            typeof(LoadedSoundNative),
            nameof(LoadedSoundNative.SetPosition),
            new[] { typeof(float), typeof(float), typeof(float) });
        yield return AccessTools.Method(
            typeof(LoadedSoundNative),
            nameof(LoadedSoundNative.SetPosition),
            new[] { typeof(Vec3f) });
        yield return AccessTools.Method(
            typeof(LoadedSoundNative),
            nameof(LoadedSoundNative.SetPitch),
            new[] { typeof(float) });
        yield return AccessTools.Method(
            typeof(LoadedSoundNative),
            nameof(LoadedSoundNative.SetVolume),
            new[] { typeof(float) });
        yield return AccessTools.Method(
            typeof(LoadedSoundNative),
            nameof(LoadedSoundNative.SetLooping),
            new[] { typeof(bool) });
        yield return AccessTools.Method(
            typeof(LoadedSoundNative),
            nameof(LoadedSoundNative.SetLowPassfiltering),
            new[] { typeof(float) });
        yield return AccessTools.Method(
            typeof(LoadedSoundNative),
            nameof(LoadedSoundNative.SetReverb),
            new[] { typeof(float) });
    }

    [HarmonyPostfix]
    private static void Postfix(LoadedSoundNative __instance)
        => DirectorReplayRuntime.Manager?.CaptureReplayLoadedSoundState(
            __instance,
            DirectorReplaySoundAction.Update);
}

[HarmonyPatch(typeof(LoadedSoundNative), nameof(LoadedSoundNative.SetPitchOffset), new[] { typeof(float) })]
internal static class ReplayLoadedSoundPitchOffsetCapturePatch
{
    [HarmonyPostfix]
    private static void Postfix(LoadedSoundNative __instance, float val)
        => DirectorReplayRuntime.Manager?.CaptureReplayLoadedSoundState(
            __instance,
            DirectorReplaySoundAction.Update,
            val);
}

[HarmonyPatch(
    typeof(BlockAccessorRelaxed),
    nameof(BlockAccessorRelaxed.SetBlock),
    new[] { typeof(int), typeof(BlockPos), typeof(ItemStack) })]
internal static class ReplaySolidBlockCapturePatch
{
    [HarmonyPostfix]
    private static void Postfix(BlockPos pos)
        => DirectorReplayRuntime.Manager?.QueueBlockChanged(pos);
}

[HarmonyPatch(
    typeof(BlockAccessorRelaxed),
    nameof(BlockAccessorRelaxed.SetBlock),
    new[] { typeof(int), typeof(BlockPos), typeof(int) })]
internal static class ReplayFluidBlockCapturePatch
{
    [HarmonyPostfix]
    private static void Postfix(BlockPos pos)
        => DirectorReplayRuntime.Manager?.QueueBlockChanged(pos);
}

[HarmonyPatch(typeof(BlockAccessorBulkMinimalUpdate), nameof(BlockAccessorBulkMinimalUpdate.FastCommit))]
internal static class ReplayMinimalBulkBlockCapturePatch
{
    [HarmonyPrefix]
    private static void Prefix(BlockAccessorBulkMinimalUpdate __instance, out BlockPos[] __state)
    {
        if (DirectorReplayRuntime.Manager?.ShouldTrackDirectBlockChanges != true)
        {
            __state = Array.Empty<BlockPos>();
            return;
        }

        __state = new BlockPos[__instance.StagedBlocks.Count];
        int index = 0;
        foreach (BlockPos position in __instance.StagedBlocks.Keys)
        {
            __state[index++] = position.Copy();
        }
    }

    [HarmonyPostfix]
    private static void Postfix(BlockPos[] __state)
    {
        if (__state.Length > 0)
        {
            DirectorReplayRuntime.Manager?.QueueBlockChanges(__state);
        }
    }
}
