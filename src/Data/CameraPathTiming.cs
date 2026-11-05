namespace VSDirector.Data;

public static class CameraPathTiming
{
    public const double DefaultPlaybackSpeed = 1d;
    public const double DefaultKeyframeDurationSeconds = 1d;
    public const double MinPlaybackSpeed = 0.05d;
    public const double MaxPlaybackSpeed = 8d;
    public const double DefaultSegmentSpeed = 1d;
    public const double MinSegmentSpeed = 0d;
    public const double MaxSegmentSpeed = 8d;
    public const double MinKeyframeGapSeconds = 0.001d;
    public const double MaxTimelineSeconds = 604800d;
    public const double TimeMatchEpsilon = 0.000001d;

    public static double NormalizePlaybackSpeed(double speed)
    {
        return double.IsFinite(speed)
            ? Math.Clamp(speed, MinPlaybackSpeed, MaxPlaybackSpeed)
            : DefaultPlaybackSpeed;
    }

    public static double NormalizeSegmentSpeed(double speed)
    {
        return double.IsFinite(speed)
            ? Math.Clamp(speed, MinSegmentSpeed, MaxSegmentSpeed)
            : DefaultSegmentSpeed;
    }

    public static bool TrySetSegmentSpeeds(
        CameraPath path,
        int keyframeIndex,
        double requestedStartSpeed,
        double requestedEndSpeed,
        out double appliedStartSpeed,
        out double appliedEndSpeed)
    {
        ArgumentNullException.ThrowIfNull(path);
        appliedStartSpeed = DefaultSegmentSpeed;
        appliedEndSpeed = DefaultSegmentSpeed;
        if (keyframeIndex < 0
            || keyframeIndex >= path.Keyframes.Count - 1
            || !double.IsFinite(requestedStartSpeed)
            || !double.IsFinite(requestedEndSpeed)
            || requestedStartSpeed < MinSegmentSpeed
            || requestedStartSpeed > MaxSegmentSpeed
            || requestedEndSpeed < MinSegmentSpeed
            || requestedEndSpeed > MaxSegmentSpeed)
        {
            return false;
        }

        appliedStartSpeed = NormalizeSegmentSpeed(requestedStartSpeed);
        appliedEndSpeed = NormalizeSegmentSpeed(requestedEndSpeed);
        if (appliedStartSpeed + appliedEndSpeed <= TimeMatchEpsilon)
        {
            return false;
        }

        CameraKeyframe frame = path.Keyframes[keyframeIndex];
        frame.SegmentStartSpeed = appliedStartSpeed;
        frame.SegmentEndSpeed = appliedEndSpeed;
        return true;
    }

    public static double RemapSegmentProgress(
        double linearProgress,
        double startSpeed,
        double endSpeed)
    {
        double progress = double.IsFinite(linearProgress)
            ? Math.Clamp(linearProgress, 0d, 1d)
            : 0d;
        double from = NormalizeSegmentSpeed(startSpeed);
        double to = NormalizeSegmentSpeed(endSpeed);
        double total = (from + to) * 0.5d;
        if (total <= TimeMatchEpsilon)
        {
            return progress;
        }

        double travelled = from * progress + (to - from) * progress * progress * 0.5d;
        return Math.Clamp(travelled / total, 0d, 1d);
    }

    public static int UpsertKeyframe(CameraPath path, CameraKeyframe keyframe)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(keyframe);

        CameraKeyframe copy = keyframe.DeepClone();
        copy.TimeSeconds = double.IsFinite(copy.TimeSeconds) ? Math.Max(0d, copy.TimeSeconds) : 0d;
        copy.DurationSeconds = NormalizeKeyframeDuration(
            copy.DurationSeconds,
            DefaultKeyframeDurationSeconds);
        NormalizeTimeline(path.Keyframes);
        int existingIndex = path.Keyframes.FindIndex(
            frame => Math.Abs(frame.TimeSeconds - copy.TimeSeconds) <= TimeMatchEpsilon);
        if (existingIndex >= 0)
        {
            // Capturing a new pose at an existing time must not silently erase
            // the editor's per-segment interpolation override.
            copy.InterpolationToNext ??= path.Keyframes[existingIndex].InterpolationToNext;
            copy.Name = path.Keyframes[existingIndex].Name;
            copy.DurationSeconds = path.Keyframes[existingIndex].DurationSeconds;
            copy.HoldSeconds = path.Keyframes[existingIndex].HoldSeconds;
            copy.SegmentStartSpeed = path.Keyframes[existingIndex].SegmentStartSpeed;
            copy.SegmentEndSpeed = path.Keyframes[existingIndex].SegmentEndSpeed;
            copy.Marker = path.Keyframes[existingIndex].Marker;
            copy.BezierHandlesEnabled = path.Keyframes[existingIndex].BezierHandlesEnabled;
            copy.BezierInX = path.Keyframes[existingIndex].BezierInX;
            copy.BezierInY = path.Keyframes[existingIndex].BezierInY;
            copy.BezierInZ = path.Keyframes[existingIndex].BezierInZ;
            copy.BezierOutX = path.Keyframes[existingIndex].BezierOutX;
            copy.BezierOutY = path.Keyframes[existingIndex].BezierOutY;
            copy.BezierOutZ = path.Keyframes[existingIndex].BezierOutZ;
            path.Keyframes[existingIndex] = copy;
        }
        else
        {
            path.Keyframes.Add(copy);
        }

        path.Keyframes.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        NormalizeTimeline(path.Keyframes);
        return path.Keyframes.IndexOf(copy);
    }

    public static bool TrySetKeyframeTime(
        CameraPath path,
        int keyframeIndex,
        double requestedTimeSeconds,
        out double appliedTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(path);
        appliedTimeSeconds = 0d;
        if (keyframeIndex < 0
            || keyframeIndex >= path.Keyframes.Count
            || !double.IsFinite(requestedTimeSeconds))
        {
            return false;
        }

        NormalizeTimeline(path.Keyframes);
        double minimum = keyframeIndex == 0
            ? 0d
            : path.Keyframes[keyframeIndex - 1].TimeSeconds + MinKeyframeGapSeconds;
        double lastTime = path.Keyframes[^1].TimeSeconds;
        double maximum = Math.Max(minimum,
            MaxTimelineSeconds - Math.Max(0d, lastTime - path.Keyframes[keyframeIndex].TimeSeconds));
        appliedTimeSeconds = Math.Clamp(requestedTimeSeconds, minimum, maximum);
        double delta = appliedTimeSeconds - path.Keyframes[keyframeIndex].TimeSeconds;
        for (int index = keyframeIndex; index < path.Keyframes.Count; index++)
        {
            path.Keyframes[index].TimeSeconds += delta;
        }
        NormalizeTimeline(path.Keyframes);
        return true;
    }

    public static bool TrySetKeyframeDuration(
        CameraPath path,
        int keyframeIndex,
        double requestedDurationSeconds,
        out double appliedDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(path);
        appliedDurationSeconds = 0d;
        if (keyframeIndex < 0
            || keyframeIndex >= path.Keyframes.Count
            || !double.IsFinite(requestedDurationSeconds))
        {
            return false;
        }

        NormalizeTimeline(path.Keyframes);
        double oldDuration = EffectiveDuration(path.Keyframes, keyframeIndex);
        double remaining = Math.Max(
            0d,
            path.DurationSeconds - path.Keyframes[keyframeIndex].TimeSeconds - oldDuration);
        double minimum = keyframeIndex == path.Keyframes.Count - 1 ? 0d : MinKeyframeGapSeconds;
        double maximum = Math.Max(minimum,
            MaxTimelineSeconds - path.Keyframes[keyframeIndex].TimeSeconds - Math.Max(0d, remaining));
        appliedDurationSeconds = Math.Clamp(
            requestedDurationSeconds,
            minimum,
            maximum);
        double delta = appliedDurationSeconds - oldDuration;
        path.Keyframes[keyframeIndex].DurationSeconds = appliedDurationSeconds;
        for (int index = keyframeIndex + 1; index < path.Keyframes.Count; index++)
        {
            path.Keyframes[index].TimeSeconds += delta;
        }
        NormalizeTimeline(path.Keyframes);
        return true;
    }

    public static bool TrySetKeyframeHold(
        CameraPath path,
        int keyframeIndex,
        double requestedHoldSeconds,
        out double appliedHoldSeconds)
    {
        ArgumentNullException.ThrowIfNull(path);
        appliedHoldSeconds = 0d;
        if (keyframeIndex < 0
            || keyframeIndex >= path.Keyframes.Count
            || !double.IsFinite(requestedHoldSeconds))
        {
            return false;
        }

        NormalizeTimeline(path.Keyframes);
        double duration = EffectiveDuration(path.Keyframes, keyframeIndex);
        appliedHoldSeconds = Math.Clamp(requestedHoldSeconds, 0d, duration);
        path.Keyframes[keyframeIndex].HoldSeconds = appliedHoldSeconds;
        return true;
    }

    public static bool TryScaleTimeline(CameraPath path, double requestedDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(path);
        NormalizeTimeline(path.Keyframes);
        double currentDuration = path.DurationSeconds;
        if (!double.IsFinite(requestedDurationSeconds)
            || requestedDurationSeconds < MinKeyframeGapSeconds
            || requestedDurationSeconds > MaxTimelineSeconds
            || currentDuration <= TimeMatchEpsilon)
        {
            return false;
        }

        double scale = requestedDurationSeconds / currentDuration;
        foreach (CameraKeyframe frame in path.Keyframes)
        {
            frame.TimeSeconds *= scale;
            frame.DurationSeconds *= scale;
            frame.HoldSeconds *= scale;
        }
        NormalizeTimeline(path.Keyframes);
        return true;
    }

    public static int InsertKeyframe(
        CameraPath path,
        int insertionIndex,
        CameraKeyframe keyframe,
        double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(keyframe);
        NormalizeTimeline(path.Keyframes);
        int index = Math.Clamp(insertionIndex, 0, path.Keyframes.Count);
        double duration = NormalizeKeyframeDuration(
            durationSeconds,
            DefaultKeyframeDurationSeconds);
        double start = index < path.Keyframes.Count
            ? path.Keyframes[index].TimeSeconds
            : path.DurationSeconds;
        for (int shifted = index; shifted < path.Keyframes.Count; shifted++)
        {
            path.Keyframes[shifted].TimeSeconds = Math.Min(
                MaxTimelineSeconds,
                path.Keyframes[shifted].TimeSeconds + duration);
        }

        CameraKeyframe inserted = keyframe.DeepClone();
        inserted.TimeSeconds = start;
        inserted.DurationSeconds = duration;
        path.Keyframes.Insert(index, inserted);
        NormalizeTimeline(path.Keyframes);
        return index;
    }

    public static int InsertKeyframeAtTime(
        CameraPath path,
        CameraKeyframe keyframe,
        double timeSeconds,
        double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(keyframe);
        NormalizeTimeline(path.Keyframes);
        double requested = double.IsFinite(timeSeconds)
            ? Math.Clamp(timeSeconds, 0d, MaxTimelineSeconds)
            : path.DurationSeconds;
        int index = path.Keyframes.FindIndex(frame => frame.TimeSeconds > requested);
        if (index < 0)
        {
            index = path.Keyframes.Count;
        }

        double minimum = index > 0
            ? path.Keyframes[index - 1].TimeSeconds + MinKeyframeGapSeconds
            : 0d;
        double maximum = index < path.Keyframes.Count
            ? path.Keyframes[index].TimeSeconds - MinKeyframeGapSeconds
            : MaxTimelineSeconds;
        double start = maximum >= minimum
            ? Math.Clamp(requested, minimum, maximum)
            : minimum;
        CameraKeyframe inserted = keyframe.DeepClone();
        inserted.TimeSeconds = start;
        inserted.DurationSeconds = NormalizeKeyframeDuration(
            durationSeconds,
            DefaultKeyframeDurationSeconds);
        path.Keyframes.Insert(index, inserted);
        NormalizeTimeline(path.Keyframes);
        return index;
    }

    public static bool RemoveKeyframe(CameraPath path, int keyframeIndex)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (keyframeIndex < 0 || keyframeIndex >= path.Keyframes.Count)
        {
            return false;
        }

        NormalizeTimeline(path.Keyframes);
        if (keyframeIndex > 0 && keyframeIndex < path.Keyframes.Count - 1)
        {
            path.Keyframes[keyframeIndex - 1].SegmentEndSpeed =
                path.Keyframes[keyframeIndex].SegmentEndSpeed;
        }
        double removedDuration = EffectiveDuration(path.Keyframes, keyframeIndex);
        path.Keyframes.RemoveAt(keyframeIndex);
        for (int index = keyframeIndex; index < path.Keyframes.Count; index++)
        {
            path.Keyframes[index].TimeSeconds = Math.Max(
                0d,
                path.Keyframes[index].TimeSeconds - removedDuration);
        }
        NormalizeTimeline(path.Keyframes);
        return true;
    }

    public static int RemoveKeyframesPreservingTimeline(
        CameraPath path,
        IEnumerable<int> keyframeIndices)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(keyframeIndices);
        NormalizeTimeline(path.Keyframes);

        int[] indices = keyframeIndices
            .Where(index => index >= 0 && index < path.Keyframes.Count)
            .Distinct()
            .OrderByDescending(index => index)
            .ToArray();
        foreach (int index in indices)
        {
            if (index > 0 && index < path.Keyframes.Count - 1)
            {
                path.Keyframes[index - 1].SegmentEndSpeed =
                    path.Keyframes[index].SegmentEndSpeed;
            }
            path.Keyframes.RemoveAt(index);
        }

        NormalizeTimeline(path.Keyframes);
        return indices.Length;
    }

    public static bool TryCutKeepLeft(
        CameraPath path,
        double cutTimeSeconds,
        out int endpointIndex)
    {
        ArgumentNullException.ThrowIfNull(path);
        endpointIndex = -1;
        if (!double.IsFinite(cutTimeSeconds) || path.Keyframes.Count < 2)
        {
            return false;
        }

        NormalizeTimeline(path.Keyframes);
        double firstTime = path.Keyframes[0].TimeSeconds;
        double duration = path.DurationSeconds;
        if (cutTimeSeconds <= firstTime + TimeMatchEpsilon
            || cutTimeSeconds >= duration - TimeMatchEpsilon)
        {
            return false;
        }

        double cutTime = Math.Clamp(cutTimeSeconds, firstTime, duration);
        int existingIndex = path.Keyframes.FindIndex(
            frame => Math.Abs(frame.TimeSeconds - cutTime) <= TimeMatchEpsilon);
        if (existingIndex >= 0)
        {
            path.Keyframes.RemoveRange(existingIndex + 1, path.Keyframes.Count - existingIndex - 1);
            path.Keyframes[existingIndex].DurationSeconds = 0d;
            endpointIndex = existingIndex;
            NormalizeTimeline(path.Keyframes);
            return path.Keyframes.Count >= 2;
        }

        int firstRightIndex = path.Keyframes.FindIndex(frame => frame.TimeSeconds > cutTime);
        if (firstRightIndex < 0)
        {
            CameraKeyframe last = path.Keyframes[^1];
            last.DurationSeconds = cutTime - last.TimeSeconds;
            endpointIndex = path.Keyframes.Count - 1;
            NormalizeTimeline(path.Keyframes);
            return true;
        }

        if (!CameraPathSampler.TryEvaluate(path, cutTime, out CameraKeyframe endpoint))
        {
            return false;
        }

        endpoint.TimeSeconds = cutTime;
        endpoint.DurationSeconds = 0d;
        endpoint.InterpolationToNext = null;
        path.Keyframes.RemoveRange(firstRightIndex, path.Keyframes.Count - firstRightIndex);
        path.Keyframes.Add(endpoint);
        endpointIndex = path.Keyframes.Count - 1;
        NormalizeTimeline(path.Keyframes);
        return path.Keyframes.Count >= 2;
    }

    public static bool TrySplitSegment(
        CameraPath path,
        double splitTimeSeconds,
        out int insertedIndex)
    {
        ArgumentNullException.ThrowIfNull(path);
        insertedIndex = -1;
        if (!double.IsFinite(splitTimeSeconds) || path.Keyframes.Count == 0)
        {
            return false;
        }

        NormalizeTimeline(path.Keyframes);
        double firstTime = path.Keyframes[0].TimeSeconds;
        double duration = path.DurationSeconds;
        if (splitTimeSeconds <= firstTime + TimeMatchEpsilon
            || splitTimeSeconds >= duration - TimeMatchEpsilon
            || path.Keyframes.Any(frame =>
                Math.Abs(frame.TimeSeconds - splitTimeSeconds) <= TimeMatchEpsilon)
            || !CameraPathSampler.TryEvaluate(path, splitTimeSeconds, out CameraKeyframe split))
        {
            return false;
        }

        int index = path.Keyframes.FindIndex(frame => frame.TimeSeconds > splitTimeSeconds);
        if (index < 0)
        {
            index = path.Keyframes.Count;
        }
        double oldDuration = index == path.Keyframes.Count
            ? Math.Max(0d, path.Keyframes[^1].DurationSeconds)
            : path.Keyframes[index].TimeSeconds - path.Keyframes[index - 1].TimeSeconds;
        double previousStart = path.Keyframes[index - 1].TimeSeconds;
        CameraKeyframe previous = path.Keyframes[index - 1];
        double splitAmount = oldDuration <= TimeMatchEpsilon
            ? 1d
            : Math.Clamp((splitTimeSeconds - previousStart) / oldDuration, 0d, 1d);
        double splitSpeed = NormalizeSegmentSpeed(
            previous.SegmentStartSpeed
            + (previous.SegmentEndSpeed - previous.SegmentStartSpeed) * splitAmount);
        double remaining = Math.Max(0d, previousStart + oldDuration - splitTimeSeconds);
        split.TimeSeconds = splitTimeSeconds;
        split.DurationSeconds = remaining;
        split.InterpolationToNext = previous.InterpolationToNext;
        split.Name = string.Empty;
        split.SegmentStartSpeed = splitSpeed;
        split.SegmentEndSpeed = previous.SegmentEndSpeed;
        split.Marker = string.Empty;
        previous.SegmentEndSpeed = splitSpeed;
        path.Keyframes.Insert(index, split);
        NormalizeTimeline(path.Keyframes);
        insertedIndex = index;
        return true;
    }

    public static bool TryDeleteSegment(CameraPath path, int segmentIndex)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (segmentIndex < 0 || segmentIndex >= path.Keyframes.Count)
        {
            return false;
        }

        NormalizeTimeline(path.Keyframes);
        if (segmentIndex == path.Keyframes.Count - 1)
        {
            if (path.Keyframes[segmentIndex].DurationSeconds <= TimeMatchEpsilon)
            {
                return false;
            }
            path.Keyframes[segmentIndex].DurationSeconds = 0d;
            return true;
        }

        if (path.Keyframes.Count <= 2)
        {
            return false;
        }
        return RemoveKeyframe(path, segmentIndex);
    }

    public static void NormalizeTimeline(IList<CameraKeyframe> keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        if (keyframes.Count == 0)
        {
            return;
        }

        if (keyframes is List<CameraKeyframe> list)
        {
            list.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        }
        for (int index = 0; index < keyframes.Count; index++)
        {
            CameraKeyframe frame = keyframes[index];
            frame.TimeSeconds = double.IsFinite(frame.TimeSeconds)
                ? Math.Clamp(frame.TimeSeconds, 0d, MaxTimelineSeconds)
                : 0d;
            frame.DurationSeconds = double.IsFinite(frame.DurationSeconds)
                ? Math.Clamp(frame.DurationSeconds, 0d, MaxTimelineSeconds)
                : 0d;
            frame.HoldSeconds = double.IsFinite(frame.HoldSeconds)
                ? Math.Clamp(frame.HoldSeconds, 0d, MaxTimelineSeconds)
                : 0d;
            frame.SegmentStartSpeed = NormalizeSegmentSpeed(frame.SegmentStartSpeed);
            frame.SegmentEndSpeed = NormalizeSegmentSpeed(frame.SegmentEndSpeed);
            if (index + 1 < keyframes.Count
                && frame.SegmentStartSpeed + frame.SegmentEndSpeed <= TimeMatchEpsilon)
            {
                frame.SegmentStartSpeed = DefaultSegmentSpeed;
                frame.SegmentEndSpeed = DefaultSegmentSpeed;
            }
            if (index > 0)
            {
                frame.TimeSeconds = Math.Max(
                    frame.TimeSeconds,
                    keyframes[index - 1].TimeSeconds + MinKeyframeGapSeconds);
            }
        }

        for (int index = 0; index + 1 < keyframes.Count; index++)
        {
            keyframes[index].DurationSeconds = Math.Max(
                MinKeyframeGapSeconds,
                keyframes[index + 1].TimeSeconds - keyframes[index].TimeSeconds);
            keyframes[index].HoldSeconds = Math.Min(
                keyframes[index].HoldSeconds,
                keyframes[index].DurationSeconds);
        }
        keyframes[^1].HoldSeconds = Math.Min(
            keyframes[^1].HoldSeconds,
            Math.Max(0d, keyframes[^1].DurationSeconds));
    }

    private static double EffectiveDuration(IReadOnlyList<CameraKeyframe> keyframes, int index)
        => index + 1 < keyframes.Count
            ? Math.Max(MinKeyframeGapSeconds,
                keyframes[index + 1].TimeSeconds - keyframes[index].TimeSeconds)
            : Math.Max(0d, keyframes[index].DurationSeconds);

    private static double NormalizeKeyframeDuration(double durationSeconds, double fallback)
        => double.IsFinite(durationSeconds) && durationSeconds >= MinKeyframeGapSeconds
            ? Math.Min(durationSeconds, MaxTimelineSeconds)
            : Math.Clamp(fallback, MinKeyframeGapSeconds, MaxTimelineSeconds);
}
