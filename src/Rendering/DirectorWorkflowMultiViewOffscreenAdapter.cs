using VSDirector.Data;

namespace VSDirector.Rendering;

public static class DirectorWorkflowMultiViewOffscreenAdapter
{
    public static IReadOnlyList<DirectorOffscreenViewRequest> ToOffscreenViewRequests(
        this DirectorWorkflowMultiViewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        long captureMilliseconds = checked((long)Math.Round(
            Math.Max(0d, frame.TimelineSeconds) * 1000d,
            MidpointRounding.AwayFromZero));
        return frame.Views.Select(view => new DirectorOffscreenViewRequest
        {
            ViewId = view.ViewId,
            Name = view.PassName,
            Width = view.Width,
            Height = view.Height,
            Pose = new DirectorOffscreenCameraPose(
                view.Pose.X,
                view.Pose.Y,
                view.Pose.Z,
                view.Pose.Dimension,
                view.Pose.Yaw,
                view.Pose.Pitch,
                view.Pose.Roll,
                view.Pose.Fov),
            CaptureClockMilliseconds = captureMilliseconds,
            RenderTerrain = view.RenderTerrain,
            RenderEntities = view.RenderEntities,
            RenderLocalPlayer = view.RenderLocalPlayer,
            RenderOpaqueParticles = view.RenderOpaqueParticles
        }).ToArray();
    }
}
