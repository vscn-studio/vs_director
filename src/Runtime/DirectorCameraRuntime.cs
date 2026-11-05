using VSDirector.Data;

namespace VSDirector.Runtime;

internal static class DirectorCameraRuntime
{
    public static DirectorCameraController? Controller { get; set; }

    public static CameraPreset? PendingRemoteCameraPose { get; set; }

    public static CameraPreset? PendingRemoteTargetPose { get; set; }
}
