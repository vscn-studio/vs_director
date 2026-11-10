using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Recording;
using VSDirector.Replay;
using VSDirector.Rendering;

namespace VSDirector.Runtime;

internal readonly record struct DirectorMountFrameState(
    EntityAgent? Agent,
    IMountableSeat? Seat);

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.OnBeforeRenderFrame3D))]
internal static class DirectorMountedCameraIsolationPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(out DirectorMountFrameState __state)
    {
        DirectorCameraController? controller = DirectorCameraRuntime.Controller;
        __state = controller?.TrySuspendLocalMount(out EntityAgent? agent, out IMountableSeat? seat) == true
            ? new DirectorMountFrameState(agent, seat)
            : default;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(DirectorMountFrameState __state)
        => DirectorCameraRuntime.Controller?.RestoreLocalMount(__state.Agent, __state.Seat);

    [HarmonyFinalizer]
    private static void Finalizer(DirectorMountFrameState __state)
        => DirectorCameraRuntime.Controller?.RestoreLocalMount(__state.Agent, __state.Seat);
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.OnBeforeRenderFrame3D))]
internal static class ReplayBeforeCameraPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(float dt)
    {
        DirectorReplayRuntime.Manager?.PrepareFrame(dt);
        DirectorReplayRuntime.UpdateCameraForRenderFrame?.Invoke();
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.OnBeforeRenderFrame3D))]
internal static class PlayerCameraBeforeRenderPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(PlayerCamera __instance, float dt)
    {
        DirectorLiveWorkflowRuntime.Runner?.PrepareFrame();
        DirectorCameraController? controller = DirectorCameraRuntime.Controller;
        if (controller?.IsActive == true)
        {
            controller.PrepareCameraFrameSafe(__instance, dt);
        }
        if (DirectorReplayVisualRuntime.Current?.Camera is { OverrideNearClip: true } visualCamera)
        {
            __instance.ZNear = visualCamera.NearClip;
        }
    }
}

[HarmonyPatch(typeof(SystemRenderEntities), "OnBeforeRender")]
internal static class LocalPlayerRenderPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        DirectorCreationRuntime.Current?.RestoreEntities();
        DirectorCameraRuntime.Controller?.RestoreFollowTargetForFrame();
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        DirectorReplayRuntime.Manager?.EnforceEntityVisibilityAfterCulling();
        DirectorCameraRuntime.Controller?.HideLocalPlayerForFrame();
        DirectorCreationRuntime.Current?.HideReplacedEntities();
    }
}

[HarmonyPatch(
    typeof(Camera),
    nameof(Camera.GetCameraMatrix),
    new[] { typeof(Vec3d), typeof(Vec3d), typeof(double), typeof(double), typeof(AABBIntersectionTest) })]
internal static class CameraMatrixPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(
        Camera __instance,
        Vec3d camEyePosIn,
        Vec3d worldPos,
        ref double yaw,
        ref double pitch,
        AABBIntersectionTest intersectionTester)
    {
        DirectorCameraController? controller = DirectorCameraRuntime.Controller;
        if (__instance is PlayerCamera && controller?.TryGetLockedAngles(out double lockedYaw, out double lockedPitch) == true)
        {
            yaw = lockedYaw;
            pitch = lockedPitch;
        }

        // VS Director drives a follow camera with native TPP distance 0, so the
        // vanilla LimitThirdPersonCameraToWalls hook (and Clear View's patch on
        // it) is skipped. Invoke Clear View's own prefix with the actual custom
        // eye/target once per camera update to preserve its fade behavior.
        if (__instance is PlayerCamera playerCamera
            && ReferenceEquals(camEyePosIn, worldPos)
            && controller?.IsActive == true
            && controller.IsClearViewFollowEnabled)
        {
            DirectorClearViewBridge.TryApply(playerCamera, worldPos, yaw, pitch, intersectionTester);
        }
    }

    [HarmonyPostfix]
    private static void Postfix(Camera __instance, AABBIntersectionTest intersectionTester)
    {
        if (__instance is not PlayerCamera || DirectorCameraRuntime.Controller?.IsActive != true)
        {
            return;
        }

        if (intersectionTester?.bsTester is IClientWorldAccessor world && world.Player is ClientPlayer player)
        {
            player.OverrideCameraMode = null;
        }
    }
}

internal static class DirectorClearViewBridge
{
    private static bool? available;
    private static bool warned;

    public static void TryApply(
        PlayerCamera camera,
        Vec3d worldPos,
        double yaw,
        double pitch,
        AABBIntersectionTest intersectionTester)
    {
        try
        {
            if (available != true)
            {
                available = AppDomain.CurrentDomain.GetAssemblies()
                    .Any(assembly => assembly.GetType("ClearView.Systems.ClearViewModSystem") is not null);
            }
            if (available != true)
            {
                return;
            }

            EntityPlayer? entity = (intersectionTester.bsTester as IClientWorldAccessor)?.Player?.Entity;
            if (entity is null)
            {
                return;
            }

            Vec3f forward = EntityPos.GetViewVector((float)pitch, (float)yaw);
            // Entity camera elements fade obstacles against their tracked
            // actor. Other detached cameras retain the local-player fallback.
            Vec3d target = DirectorCameraRuntime.Controller?.TryGetClearViewTarget(out Vec3d followedTarget) == true
                ? followedTarget
                : new Vec3d(
                    entity.Pos.X + entity.LocalEyePos.X,
                    entity.Pos.Y + entity.Pos.DimensionYAdjustment + entity.LocalEyePos.Y,
                    entity.Pos.Z + entity.LocalEyePos.Z);
            Vec3d eye = new(
                worldPos.X + entity.LocalEyePos.X + forward.X * 0.2d,
                worldPos.Y + entity.LocalEyePos.Y + forward.Y * 0.2d,
                worldPos.Z + entity.LocalEyePos.Z + forward.Z * 0.2d);

            // Clear View uses this value only to determine how far its optional
            // vanilla-collision ray may inspect. Supply the custom eye distance
            // while leaving the player's camera settings untouched.
            int oldMax = camera.TppCameraDistanceMax;
            try
            {
                camera.TppCameraDistanceMax = (int)Math.Clamp(eye.DistanceTo(target), 1d, 64d);
                FloatRef distance = FloatRef.Create((float)eye.DistanceTo(target));
                camera.LimitThirdPersonCameraToWalls(intersectionTester, yaw, eye, target, distance);
            }
            finally
            {
                camera.TppCameraDistanceMax = oldMax;
            }
        }
        catch (Exception ex)
        {
            if (!warned)
            {
                warned = true;
                (intersectionTester.bsTester as IClientWorldAccessor)?.Api.Logger.Debug(
                    "VS Director: Clear View follow bridge unavailable: {0}", ex.Message);
            }
        }
    }

}

[HarmonyPatch(
    typeof(FrustumCulling),
    nameof(FrustumCulling.CalcFrustumEquations),
    new[] { typeof(BlockPos), typeof(double[]), typeof(double[]) })]
internal static class DirectorFrustumOriginPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(ref BlockPos playerPos)
    {
        // Preview/export passes supply their own camera. The editor pose must
        // not replace the origin used for their terrain range and LOD checks.
        if (DirectorOffscreenRenderRuntime.CurrentView is { } view)
        {
            playerPos = new BlockPos((int)Math.Floor(view.Pose.X), (int)Math.Floor(view.Pose.Y),
                (int)Math.Floor(view.Pose.Z), view.Pose.Dimension);
            return;
        }
        if (DirectorCameraRuntime.Controller is not { IsActive: true, CurrentPose: { } camera }
            || !double.IsFinite(camera.X)
            || !double.IsFinite(camera.Y)
            || !double.IsFinite(camera.Z))
        {
            return;
        }

        playerPos = new BlockPos(
            (int)Math.Floor(camera.X),
            (int)Math.Floor(camera.Y),
            (int)Math.Floor(camera.Z),
            camera.Dimension);
    }
}

[HarmonyPatch(typeof(SystemPlayerControl), nameof(SystemPlayerControl.OnGameTick))]
internal static class PlayerControlTickPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(
        int ___forwardKey,
        int ___backwardKey,
        int ___leftKey,
        int ___rightKey,
        int ___jumpKey,
        int ___sneakKey,
        int ___sprintKey,
        int ___sittingKey,
        int ___ctrlKey,
        int ___shiftKey)
    {
        DirectorCameraController? controller = DirectorCameraRuntime.Controller;
        if (controller?.ShouldLockPlayerInput == true)
        {
            controller.PreparePlayerControlTick(
                ___forwardKey,
                ___backwardKey,
                ___leftKey,
                ___rightKey,
                ___jumpKey,
                ___sneakKey,
                ___sprintKey,
                ___sittingKey,
                ___ctrlKey,
                ___shiftKey);
        }
    }
}

[HarmonyPatch(
    typeof(SystemSoundEngine),
    nameof(SystemSoundEngine.OnRenderFrame),
    new[] { typeof(float), typeof(EnumRenderStage) })]
internal static class DirectorSoundListenerPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        DirectorCameraRuntime.Controller?.UpdateAudioListenerForFrame();
    }
}
