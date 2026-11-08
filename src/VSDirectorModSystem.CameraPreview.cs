using VSDirector.Data;
using VSDirector.Rendering;

namespace VSDirector;

public sealed partial class VSDirectorModSystem
{
    private CameraPreset? replayShootingPose;
    private CameraPreset? replayShootingBasePose;
    private Guid replayShootingSourceId;
    private readonly Guid replayPreviewViewId = Guid.NewGuid();
    private IDirectorOffscreenFrameSink? replayPreviewSink;
    internal string ReplayCameraPreviewError { get; private set; } = "";
    internal void ClearReplayCameraPreviewError() => ReplayCameraPreviewError = "";

    internal CameraPreset? ReplayShootingPose => replayShootingPose?.DeepClone();
    internal Guid ReplayShootingSourceId => replayShootingSourceId;
    internal double ReplayCameraSampleTime => replay?.PreparedPlaybackTimeSeconds ?? 0;

    internal CameraPreset ApplyReplayShootingCameraVisuals(CameraPreset source,
        DirectorReplayViewportTimeline timeline, double time)
    {
        CameraPreset pose = source.DeepClone();
        var visual = timeline.Visuals.Camera;
        if (DirectorReplayElementTimeline.TrySample(timeline, DirectorReplayElementTrackType.Fov, time, out var fov))
            pose.Fov = fov.Fov;
        else if (visual.OverrideFov) pose.Fov = visual.Fov;
        if (DirectorReplayElementTimeline.TrySample(timeline, DirectorReplayElementTrackType.CameraShake, time, out var shake))
        {
            pose.Yaw += (float)(Math.Sin(time * Math.PI * 2 * shake.FrequencyX) * shake.AmplitudeX);
            pose.Pitch += (float)(Math.Sin(time * Math.PI * 2 * shake.FrequencyY + Math.PI / 2) * shake.AmplitudeY);
        }
        else if (visual.CameraShake)
        {
            double frequencyY = visual.SplitShake ? visual.ShakeFrequencyY : visual.ShakeFrequencyX;
            double amplitudeY = visual.SplitShake ? visual.ShakeAmplitudeY : visual.ShakeAmplitudeX;
            pose.Yaw += (float)(Math.Sin(time * Math.PI * 2 * visual.ShakeFrequencyX) * visual.ShakeAmplitudeX);
            pose.Pitch += (float)(Math.Sin(time * Math.PI * 2 * frequencyY + Math.PI / 2) * amplitudeY);
        }
        if (visual.OverrideRoll) pose.Roll = visual.Roll;
        return pose;
    }

    internal bool ConfigureReplayCameraPreview(CameraPreset? pose, int width, int height,
        IDirectorOffscreenFrameSink sink, out string error)
    {
        error = string.Empty;
        if (pose is null || replay?.IsPlaying != true || replay.IsLoading || replay.IsExporting || OffscreenCaptureBusy)
        {
            StopReplayCameraPreview();
            return false;
        }
        if (!EnsureOffscreenRenderer(out error) || offscreenRenderer is null) return false;
        if (!ReferenceEquals(replayPreviewSink, sink))
        {
            if (replayPreviewSink is not null) offscreenRenderer.RemoveSink(replayPreviewSink);
            replayPreviewSink = sink;
            offscreenRenderer.AddSink(sink);
            offscreenRenderer.PreviewRenderFailed -= OnReplayPreviewRenderFailed;
            offscreenRenderer.PreviewRenderFailed += OnReplayPreviewRenderFailed;
            ReplayCameraPreviewError = "";
        }
        double resolutionScale = Math.Min(1, 2048d / Math.Max(1, Math.Max(width, height)));
        offscreenRenderer.SetPreviewView(new DirectorOffscreenViewRequest
        {
            ViewId = replayPreviewViewId, Name = "Replay camera preview",
            Width = Math.Max(16, (int)Math.Round(width * resolutionScale)),
            Height = Math.Max(16, (int)Math.Round(height * resolutionScale)),
            Pose = new(pose.X, pose.Y, pose.Z, pose.Dimension, pose.Yaw, pose.Pitch, pose.Roll, pose.Fov),
            RenderLocalPlayer = true
        });
        return true;
    }

    internal Guid ReplayCameraPreviewViewId => replayPreviewViewId;

    private void OnReplayPreviewRenderFailed(Exception exception)
    {
        ReplayCameraPreviewError = exception.GetBaseException().Message;
        StopReplayCameraPreview();
    }

    internal void StopReplayCameraPreview()
    {
        if (offscreenRenderer is not null)
        {
            offscreenRenderer.SetPreviewView(null);
            if (replayPreviewSink is not null) offscreenRenderer.RemoveSink(replayPreviewSink);
            offscreenRenderer.PreviewRenderFailed -= OnReplayPreviewRenderFailed;
        }
        replayPreviewSink = null;
    }
}
