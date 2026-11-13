using static VSDirector.Gui.DirectorCreationText;
using VSDirector.Data;
using VSDirector.Rendering;
using VSDirector.Runtime;
using VSDirector.Networking;
using Vintagestory.Client.NoObf;
using Vintagestory.API.Client;

namespace VSDirector;

public sealed partial class VSDirectorModSystem
{
    private DirectorCreationRuntime? creation;
    private CameraPreset? beforeCreationView;
    private bool beforeCreationFree;
    private bool beforeCreationActive;
    private Guid beforeCreationReplay;
    private double beforeCreationTime;
    private EnumCameraMode? performanceCameraMode;
    private bool performanceControlActive;
    internal void BeginCreationPerformanceControl(float yaw)
    {
        performanceControlActive = true;
        controller?.Deactivate(); controller?.SetExternalInputLock(false); SetWorkspaceInputOverride(false);
        if (capi?.World is ClientMain game)
        {
            performanceCameraMode = ((ClientPlayer)game.Player).OverrideCameraMode;
            ((ClientPlayer)game.Player).OverrideCameraMode = EnumCameraMode.FirstPerson;
            // Deactivation restores the saved editor-entry camera. Initialize the
            // recording view afterward so that old pitch/roll cannot leak into it.
            var player = (ClientPlayer)game.Player;
            player.CameraYaw = yaw;
            // Native player pitch is PI-centred, unlike the editor's camera poses.
            player.CameraPitch = MathF.PI;
            player.CameraRoll = 0;
            // EntityPos.SetAngles is (roll, yaw, pitch). Keep the body upright;
            // the first-person camera's level pitch is managed separately above.
            player.Entity.Pos.SetAngles(0, yaw, MathF.PI);
            player.Entity.BodyYaw = yaw;
            player.Entity.Pos.HeadYaw = player.Entity.Pos.HeadPitch = 0;
            player.Entity.BodyYawServer = yaw;
            foreach (var manager in new[] { player.Entity.AnimManager, player.Entity.TpAnimManager })
                if (manager?.HeadController is { } head)
                { head.YawOffset = head.PitchOffset = 0; head.OnFrame(0); }
            game.AllowCharacterControl = game.AllowCameraControl = true;
        }
    }
    internal void EndCreationPerformanceControl(CameraPreset? view)
    {
        if (performanceControlActive && capi?.World is ClientMain game) ((ClientPlayer)game.Player).OverrideCameraMode = performanceCameraMode;
        performanceControlActive = false;
        if (view is not null) controller?.TryActivate(view, out _);
        TryEnableCreationFreeCamera(out _); SetWorkspaceInputOverride(true);
    }
    internal void EnterCreationMode()
    {
        beforeCreationView = TryCaptureCreationView(out var pose) ? pose : null;
        beforeCreationFree = controller?.IsFreeCamera == true;
        beforeCreationActive = controller?.IsActive == true;
        beforeCreationReplay = ActiveReplayId; beforeCreationTime = ReplayTimeSeconds;
        creation?.InvalidateBackgroundSelection();
        PauseReplayForTimelineScrub();
        if (!TryEnableCreationFreeCamera(out var error))
        {
            LeaveCreationMode();
            throw new InvalidOperationException(error);
        }
    }
    internal void LeaveCreationMode()
    {
        if (beforeCreationReplay != Guid.Empty && ActiveReplayId == beforeCreationReplay && Math.Abs(ReplayTimeSeconds - beforeCreationTime) > .01)
            SeekReplayFromPanel(beforeCreationTime);
        if (!beforeCreationActive) controller?.Deactivate();
        else if (beforeCreationView is { } view && controller is not null)
        {
            controller.TryActivate(view, out _);
            if (beforeCreationFree) controller.TryStartFreeCamera(out _);
        }
        beforeCreationView = null;
    }
    internal DirectorCreationRuntime Creation => creation ??= new(capi!, this);
    public DirectorCreationExtensions CreationExtensions => capi is not null
        ? Creation.Extensions : throw new InvalidOperationException(L("creation-creation-extensions-can-only-be-registered-after-client-initialization"));
    internal bool TryCaptureCreationView(out CameraPreset pose)
    {
        pose = new(); return controller?.TryCaptureCurrentView(L("creation-creation-view"), out pose) == true;
    }
    internal void FocusCreationView(CreationVector target, double radius, int dimension)
    {
        if(!TryEnableCreationFreeCamera(out var message))throw new InvalidOperationException(message);
        if(!TryCaptureCreationView(out var pose)||pose.Dimension!=dimension)return;
        var forward=Vintagestory.API.Common.Entities.EntityPos.GetViewVector(pose.Pitch*Vintagestory.API.MathTools.GameMath.DEG2RAD,pose.Yaw*Vintagestory.API.MathTools.GameMath.DEG2RAD);
        double aspect=Math.Max(.1,(capi?.Render.FrameWidth??1)/(double)Math.Max(1,capi?.Render.FrameHeight??1));
        double halfAngle=Math.Atan(Math.Tan(Math.Clamp(pose.Fov,20,120)*Math.PI/360)*Math.Min(1,aspect));
        double distance=Math.Max(.75,radius/Math.Sin(halfAngle)*1.15);
        pose.X=target.X-forward.X*distance;pose.Y=target.Y-forward.Y*distance;pose.Z=target.Z-forward.Z*distance;
        if(controller?.TryActivate(pose,out message)!=true)throw new InvalidOperationException(message);
        TryEnableCreationFreeCamera(out _);
    }
    internal DirectorOffscreenMultiViewRenderer CreationOffscreen()
    {
        if (!EnsureOffscreenRenderer(out string error)) throw new InvalidOperationException(error);
        return offscreenRenderer!;
    }
    internal void SeekCreationBackground(double time, bool advancing = false)
    {
        if (replay is null) return;
        replay.DriveCreationBackground(time, advancing);
    }
    internal bool CreationBackgroundReady => replay is not null && !replay.IsLoading && !replay.IsSeekPending;
    internal bool TryEnableCreationFreeCamera(out string message)
    {
        if (!EnsureFeatureEditAccess(DirectorPermission.FreeCamera, out message)) return false;
        if (controller is null) { message = L("creation-free-camera-is-unavailable"); return false; }
        if (controller.IsFreeCamera && (replay?.IsPlaying != true || replayCameraState.Mode == DirectorReplayCameraMode.Free))
        { message = ""; return true; }
        if (replay?.IsPlaying == true) return EnterReplayFreeCamera(out message, showNotification: false, refreshWorkspace: false);
        return controller.TryStartFreeCamera(out message);
    }
}
