using VSDirector.Data;

namespace VSDirector;

public sealed partial class VSDirectorModSystem
{
    private readonly DirectorReplayEditHistory replayEditHistory = new();

    private DirectorReplayEditSnapshot? CaptureReplayEdit()
    {
        replayEditHistory.Select(ActiveReplayId);
        return ActiveReplayDocument is { } document
            ? new(DirectorReplayTimelineEditIO.Create(document),
                (replayElementReturnPose ?? replayShootingBasePose ?? controller?.CurrentPose)?.DeepClone()) : null;
    }

    internal bool CanUndoReplayEdit { get { replayEditHistory.Select(ActiveReplayId); return !IsReplayExporting && replayEditHistory.CanUndo; } }
    internal bool CanRedoReplayEdit { get { replayEditHistory.Select(ActiveReplayId); return !IsReplayExporting && replayEditHistory.CanRedo; } }

    private void ResetEditedReplayCamera()
    {
        var restore = replayElementReturnPose?.DeepClone();
        ResetReplayElementRuntime();
        if (restore is not null && replayViewportCameraAuthority == DirectorReplayCameraAuthority.Timeline && controller is not null)
        {
            // Restore before readiness checks: a deleted preset camera must not
            // keep playback waiting for chunks at its previous position.
            controller.TryRestoreReplayCamera(restore);
            replayCameraState = DirectorReplayCameraState.Free;
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
                controller.TryRestoreReplayCamera(pose);
                replayCameraState = DirectorReplayCameraState.Free;
            }
            pathPreview?.Refresh();
            return true;
        });
    }
}
