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
    private readonly Guid replayObservationViewId = Guid.NewGuid();
    private IDirectorOffscreenFrameSink? replayObservationSink;
    internal string ReplayCameraObservationError { get; private set; } = "";
    internal void ClearReplayCameraObservationError() => ReplayCameraObservationError = "";
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
        => ConfigureReplayPreview(pose, width, height, sink, false, out error);

    internal bool ConfigureReplayCameraObservation(CameraPreset? pose, int width, int height,
        IDirectorOffscreenFrameSink sink, out string error)
        => ConfigureReplayPreview(pose, width, height, sink, true, out error);

    private bool ConfigureReplayPreview(CameraPreset? pose, int width, int height,
        IDirectorOffscreenFrameSink sink, bool observation, out string error)
    {
        error = string.Empty;
        if (pose is null || replay?.IsPlaying != true || replay.IsLoading || replay.IsExporting || OffscreenCaptureBusy)
        {
            StopReplayPreview(observation);
            return false;
        }
        if (!EnsureOffscreenRenderer(out error) || offscreenRenderer is null) return false;
        ref var channelSink = ref (observation ? ref replayObservationSink : ref replayPreviewSink);
        if (!ReferenceEquals(channelSink, sink))
        {
            if (channelSink is not null) offscreenRenderer.RemoveSink(channelSink);
            channelSink = sink;
            offscreenRenderer.AddSink(sink);
            offscreenRenderer.PreviewRenderFailed -= OnReplayPreviewRenderFailed;
            offscreenRenderer.PreviewRenderFailed += OnReplayPreviewRenderFailed;
            if (observation) ReplayCameraObservationError = "";
            else ReplayCameraPreviewError = "";
        }
        double resolutionScale = Math.Min(1, 2048d / Math.Max(1, Math.Max(width, height)));
        Guid viewId = observation ? replayObservationViewId : replayPreviewViewId;
        offscreenRenderer.SetPreviewView(viewId, new DirectorOffscreenViewRequest
        {
            ViewId = viewId, Name = observation ? "Replay camera observation" : "Replay camera preview",
            Width = Math.Max(16, (int)Math.Round(width * resolutionScale)),
            Height = Math.Max(16, (int)Math.Round(height * resolutionScale)),
            Pose = new(pose.X, pose.Y, pose.Z, pose.Dimension, pose.Yaw, pose.Pitch, pose.Roll, pose.Fov),
            RenderLocalPlayer = true
        });
        return true;
    }

    internal Guid ReplayCameraPreviewViewId => replayPreviewViewId;
    internal Guid ReplayCameraObservationViewId => replayObservationViewId;

    private void OnReplayPreviewRenderFailed(Guid viewId, Exception exception)
    {
        if (viewId == replayObservationViewId)
        {
            ReplayCameraObservationError = exception.GetBaseException().Message;
            StopReplayCameraObservation();
        }
        else if (viewId == replayPreviewViewId)
        {
            ReplayCameraPreviewError = exception.GetBaseException().Message;
            StopReplayCameraPreview();
        }
    }

    internal void StopReplayCameraPreview()
        => StopReplayPreview(false);

    internal void StopReplayCameraObservation()
        => StopReplayPreview(true);

    private void StopReplayPreview(bool observation)
    {
        ref var channelSink = ref (observation ? ref replayObservationSink : ref replayPreviewSink);
        if (offscreenRenderer is not null)
        {
            offscreenRenderer.SetPreviewView(observation ? replayObservationViewId : replayPreviewViewId, null);
            if (channelSink is not null) offscreenRenderer.RemoveSink(channelSink);
        }
        channelSink = null;
        if (offscreenRenderer is not null && replayPreviewSink is null && replayObservationSink is null)
            offscreenRenderer.PreviewRenderFailed -= OnReplayPreviewRenderFailed;
    }
}
