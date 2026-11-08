namespace VSDirector.Data;

public static class DirectorReplayPathTimeline
{
    public static bool UsesReplaySource(CameraPath path, Guid activeReplayId)
    {
        ArgumentNullException.ThrowIfNull(path);
        return activeReplayId != Guid.Empty && path.SourceReplayId == activeReplayId;
    }

    public static double StartTimeSeconds(CameraPath path, Guid activeReplayId)
    {
        ArgumentNullException.ThrowIfNull(path);
        return UsesReplaySource(path, activeReplayId)
            && double.IsFinite(path.ReplayTimeOffsetSeconds)
                ? Math.Max(0d, path.ReplayTimeOffsetSeconds)
                : 0d;
    }

    public static double ResolvePathTime(
        CameraPath path,
        Guid activeReplayId,
        double replayTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(path);
        double replayTime = double.IsFinite(replayTimeSeconds)
            ? Math.Max(0d, replayTimeSeconds)
            : 0d;
        if (UsesReplaySource(path, activeReplayId))
        {
            return replayTime - StartTimeSeconds(path, activeReplayId);
        }

        double elapsed = replayTime * CameraPathTiming.NormalizePlaybackSpeed(path.PlaybackSpeed);
        return CameraPathSampler.ResolvePlaybackTime(
            path.DurationSeconds,
            elapsed,
            path.LoopMode,
            reverse: false).TimeSeconds;
    }

    public static double ResolveReplayTime(
        CameraPath path,
        Guid activeReplayId,
        double pathTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(path);
        double pathTime = double.IsFinite(pathTimeSeconds)
            ? Math.Max(0d, pathTimeSeconds)
            : 0d;
        if (UsesReplaySource(path, activeReplayId))
        {
            return StartTimeSeconds(path, activeReplayId) + pathTime;
        }

        return pathTime / CameraPathTiming.NormalizePlaybackSpeed(path.PlaybackSpeed);
    }
}
