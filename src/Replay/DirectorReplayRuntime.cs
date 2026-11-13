namespace VSDirector.Replay;

internal static class DirectorReplayRuntime
{
    private static DirectorReplayManager? ordinaryManager;
    internal static DirectorReplayManager? OrdinaryManager => ordinaryManager;
    internal static DirectorReplayManager? CreationManager { get; private set; }

    // Rendering, capture patches and world restoration must all consult the
    // same owner. UI replay lists continue using the ordinary manager instance.
    public static DirectorReplayManager? Manager
    {
        get => CreationManager ?? ordinaryManager;
        set
        {
            ordinaryManager = value;
            if (value is null) CreationManager = null;
        }
    }

    internal static void ActivateCreationSession(DirectorReplayManager session)
    {
        if (!session.IsCreationSession
            || CreationManager is not null && !ReferenceEquals(CreationManager, session))
            throw new InvalidOperationException("creation-recording-unavailable");
        if (ordinaryManager is { } replay && (replay.IsRecording || replay.IsLiveActionRecording
            || replay.HasPendingSave || replay.IsPlaying || replay.IsLoading || replay.IsExporting))
            throw new InvalidOperationException("creation-recording-unavailable");
        CreationManager = session;
    }

    internal static void ReleaseCreationSession(DirectorReplayManager session)
    {
        if (ReferenceEquals(CreationManager, session)) CreationManager = null;
    }

    /// <summary>
    /// Runs after replay actors have been sampled for the render frame. Camera
    /// elements must be evaluated at this point so tracking observes the same
    /// entity pose that is about to be rendered.
    /// </summary>
    public static Action? UpdateCameraForRenderFrame { get; set; }
}
