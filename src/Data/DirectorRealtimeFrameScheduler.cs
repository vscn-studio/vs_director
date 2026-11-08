namespace VSDirector.Data;

public readonly record struct DirectorRealtimeFrameDecision(
    bool CaptureFrame,
    long TimelineFrame,
    long TargetFrameCount,
    long DroppedFramesBeforeCapture,
    long BacklogFrames);

/// <summary>
/// Maps a monotonic capture clock to a constant-rate frame timeline. A render
/// callback can claim at most one current framebuffer; expired slots are
/// reported as dropped instead of repeatedly reading the same framebuffer.
/// </summary>
public sealed class DirectorRealtimeFrameScheduler
{
    private readonly int framesPerSecond;
    private long accountedFrameCount;

    public DirectorRealtimeFrameScheduler(int framesPerSecond)
    {
        this.framesPerSecond = DirectorTimecode.NormalizeFrameRate(framesPerSecond);
    }

    public long CapturedFrames { get; private set; }

    public long DroppedFrames { get; private set; }

    public long TargetFrameCount => accountedFrameCount;

    public long PeakBacklogFrames { get; private set; }

    public DirectorRealtimeFrameDecision Advance(TimeSpan elapsed)
        => AdvanceTo(DirectorFrameSchedule.TargetFrameCount(elapsed, framesPerSecond));

    public DirectorRealtimeFrameDecision AdvanceTo(long targetFrameCount)
    {
        long target = Math.Max(0L, targetFrameCount);
        long backlog = Math.Max(0L, target - accountedFrameCount);
        if (backlog == 0L)
        {
            return new DirectorRealtimeFrameDecision(
                false,
                -1L,
                accountedFrameCount,
                0L,
                0L);
        }

        long dropped = backlog - 1L;
        accountedFrameCount = target;
        CapturedFrames++;
        DroppedFrames += dropped;
        PeakBacklogFrames = Math.Max(PeakBacklogFrames, backlog);
        return new DirectorRealtimeFrameDecision(
            true,
            target - 1L,
            target,
            dropped,
            backlog);
    }

    public long Complete(TimeSpan elapsed)
        => CompleteTo(DirectorFrameSchedule.TargetFrameCount(elapsed, framesPerSecond));

    public long CompleteTo(long targetFrameCount)
    {
        long target = Math.Max(0L, targetFrameCount);
        long backlog = Math.Max(0L, target - accountedFrameCount);
        accountedFrameCount = Math.Max(accountedFrameCount, target);
        DroppedFrames += backlog;
        PeakBacklogFrames = Math.Max(PeakBacklogFrames, backlog);
        return backlog;
    }
}
