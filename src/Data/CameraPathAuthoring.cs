namespace VSDirector.Data;

public static class CameraPathAuthoring
{
    public const double TicksPerSecond = DirectorPathElementTimeline.TicksPerSecond;

    public static double SnapTime(double timeSeconds)
    {
        double clamped = double.IsFinite(timeSeconds)
            ? Math.Clamp(timeSeconds, 0d, CameraPathTiming.MaxTimelineSeconds)
            : 0d;
        return Math.Round(
            clamped * TicksPerSecond,
            MidpointRounding.AwayFromZero) / TicksPerSecond;
    }

    public static double ResolveCaptureTime(
        CameraPath path,
        double playheadTimeSeconds,
        bool playheadWasPositioned)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Keyframes.Count == 0)
        {
            return 0d;
        }
        return playheadWasPositioned
            ? SnapTime(playheadTimeSeconds)
            : NextAppendTime(path);
    }

    public static double NextAppendTime(CameraPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Keyframes.Count == 0)
        {
            return 0d;
        }

        CameraKeyframe last = path.Keyframes
            .OrderBy(frame => frame.TimeSeconds)
            .Last();
        double minimumVisibleDuration = 1d / TicksPerSecond;
        double segmentDuration = double.IsFinite(last.DurationSeconds)
            && last.DurationSeconds >= minimumVisibleDuration
                ? last.DurationSeconds
                : CameraPathTiming.DefaultKeyframeDurationSeconds;
        double next = Math.Min(
            CameraPathTiming.MaxTimelineSeconds,
            Math.Max(0d, last.TimeSeconds) + segmentDuration);
        return SnapTime(next);
    }

    public static int ExpandCollapsedKeyframes(CameraPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Keyframes.Count < 2)
        {
            return 0;
        }

        CameraPathTiming.NormalizeTimeline(path.Keyframes);
        double[] originalTimes = path.Keyframes
            .Select(frame => frame.TimeSeconds)
            .ToArray();
        double accumulatedShift = 0d;
        int expanded = 0;
        for (int index = 1; index < path.Keyframes.Count; index++)
        {
            double originalGap = originalTimes[index] - originalTimes[index - 1];
            if (originalGap <= CameraPathTiming.MinKeyframeGapSeconds
                    + CameraPathTiming.TimeMatchEpsilon
                && originalGap < CameraPathTiming.DefaultKeyframeDurationSeconds)
            {
                accumulatedShift += CameraPathTiming.DefaultKeyframeDurationSeconds
                    - originalGap;
                expanded++;
            }
            path.Keyframes[index].TimeSeconds = Math.Min(
                CameraPathTiming.MaxTimelineSeconds,
                originalTimes[index] + accumulatedShift);
        }
        if (expanded > 0)
        {
            CameraPathTiming.NormalizeTimeline(path.Keyframes);
        }
        return expanded;
    }
}
