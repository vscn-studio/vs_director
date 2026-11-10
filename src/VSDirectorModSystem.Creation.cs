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
    internal void BeginCreationPerformanceControl()
    {
        performanceControlActive = true;
        controller?.Deactivate(); controller?.SetExternalInputLock(false); SetWorkspaceInputOverride(false);
        if (capi?.World is ClientMain game)
        {
            performanceCameraMode = ((ClientPlayer)game.Player).OverrideCameraMode;
            ((ClientPlayer)game.Player).OverrideCameraMode = EnumCameraMode.FirstPerson;
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
