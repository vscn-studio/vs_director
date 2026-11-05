namespace VSDirector.Data;

public enum DirectorReplayExportFrameAction
{
    WaitForReplay,
    ApplyCamera,
    Settle,
    Capture
}

/// <summary>
/// Prevents a replay seek, camera change, and framebuffer capture from being
/// accepted in the same rendered frame.
/// </summary>
public sealed class DirectorReplayExportFrameGate
{
    private readonly int settleFramesPerSeek;
    private bool waitingForReplay;
    private int settleFramesRemaining;

    public DirectorReplayExportFrameGate(int settleFramesPerSeek)
    {
        this.settleFramesPerSeek = Math.Clamp(settleFramesPerSeek, 0, 8);
    }

    public void BeginSeek()
    {
        waitingForReplay = true;
        settleFramesRemaining = 0;
    }

    public DirectorReplayExportFrameAction Next(bool replayReady)
    {
        if (!replayReady)
        {
            waitingForReplay = true;
            settleFramesRemaining = 0;
            return DirectorReplayExportFrameAction.WaitForReplay;
        }

        if (waitingForReplay)
        {
            waitingForReplay = false;
            settleFramesRemaining = settleFramesPerSeek;
            return DirectorReplayExportFrameAction.ApplyCamera;
        }

        if (settleFramesRemaining > 0)
        {
            settleFramesRemaining--;
            return DirectorReplayExportFrameAction.Settle;
        }

        return DirectorReplayExportFrameAction.Capture;
    }
}

/// <summary>
/// Delays OBS recording until the replay has rendered a stable starting frame.
/// Connecting to OBS and starting its recorder are intentionally separate phases.
/// </summary>
public sealed class DirectorReplayObsExportStartGate
{
    private readonly DirectorReplayExportFrameGate frameGate;

    public DirectorReplayObsExportStartGate(int settleFrames)
    {
        frameGate = new DirectorReplayExportFrameGate(settleFrames);
        frameGate.BeginSeek();
    }

    public bool ConnectionReady { get; private set; }

    public bool RecordingStartPending { get; private set; }

    public bool RecordingStarted { get; private set; }

    public bool CanAdvanceReplayClock => RecordingStarted;

    public void MarkConnected()
    {
        ConnectionReady = true;
        frameGate.BeginSeek();
    }

    public bool TryBeginRecording(bool replayReady)
    {
        if (!ConnectionReady || RecordingStartPending || RecordingStarted)
        {
            return false;
        }

        if (frameGate.Next(replayReady) != DirectorReplayExportFrameAction.Capture)
        {
            return false;
        }

        RecordingStartPending = true;
        return true;
    }

    public void CompleteRecordingStart()
    {
        if (!RecordingStartPending)
        {
            return;
        }

        RecordingStartPending = false;
        RecordingStarted = true;
    }

    public void FailRecordingStart()
        => RecordingStartPending = false;
}
