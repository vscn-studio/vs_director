using VSDirector.Data;

namespace VSDirector;

public sealed partial class VSDirectorModSystem
{
    private readonly DirectorReplayEditHistory replayEditHistory = new();

    private void RestoreReplayObserverPose(CameraPreset pose)
    {
        if (controller?.TryRestoreReplayCamera(pose) == true)
            replayCameraState = controller.IsFreeCamera ? DirectorReplayCameraState.Free
                : DirectorReplayCameraState.FromSource(DirectorReplayCameraMode.Fixed, Guid.Empty);
    }

    private DirectorReplayEditSnapshot? CaptureReplayEdit()
    {
        replayEditHistory.Select(ActiveReplayId);
        return ActiveReplayDocument is { } document
            ? new(DirectorReplayTimelineEditIO.Create(document),
                ((replayElementCleanPoseReplayId == replay?.ActiveReplayId ? replayElementCleanPose : null)
                    ?? replayElementReturnPose ?? replayShootingBasePose ?? controller?.CurrentPose)?.DeepClone()) : null;
    }

    internal bool CanUndoReplayEdit { get { replayEditHistory.Select(ActiveReplayId); return !IsReplayExporting && replayEditHistory.CanUndo; } }
    internal bool CanRedoReplayEdit { get { replayEditHistory.Select(ActiveReplayId); return !IsReplayExporting && replayEditHistory.CanRedo; } }

    private void ResetEditedReplayCamera()
    {
        // Capture the pose before clearing the element runtime.  A preset can
        // own the complete camera without using an explicit FOV track (the
        // nature documentary style is one example).  Releasing only the FOV
        // flag in that case leaves the last tracked pose active after the
        // group is deleted, so the next frame treats that already-modified
        // pose as its clean baseline.
        var restore = replayElementReturnPose?.DeepClone();
        var fovRestore = (replayElementReturnPose ?? replayShootingBasePose)?.DeepClone();
        bool restoreCamera = restore is not null;
        bool restoreFov = replayElementFovWasApplied;
        ResetReplayElementRuntime();
        if (controller is null) return;

        if (restoreCamera)
        {
            RestoreReplayObserverPose(restore!);
        }
        else if (restoreFov && fovRestore is not null)
        {
            // Manual camera editing owns position and orientation, while a
            // removed timeline FOV effect still must release its projection
            // override.
            controller.TrySetReplayFov(fovRestore.Fov, out _);
        }
    }

    internal bool RestoreReplayEdit(bool forward)
    {
        if (IsReplayExporting || CaptureReplayEdit() is not { } current || replay is null) return false;
        return replayEditHistory.Restore(forward, current, restored =>
        {
            if (!replay.ReplaceTimelineEdit(restored.Edits, out string error))
            {
                Show(ReplayTimelineEditError(error));
                return false;
            }
            ResetReplayElementRuntime();
            if (restored.Camera is { } pose && controller is not null)
            {
                RestoreReplayObserverPose(pose);
            }
            pathPreview?.Refresh();
            return true;
        });
    }
}
