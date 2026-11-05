namespace VSDirector.Replay;

internal static class DirectorReplayRuntime
{
    public static DirectorReplayManager? Manager { get; set; }

    /// <summary>
    /// Runs after replay actors have been sampled for the render frame. Camera
    /// elements must be evaluated at this point so tracking observes the same
    /// entity pose that is about to be rendered.
    /// </summary>
    public static Action? UpdateCameraForRenderFrame { get; set; }
}
