namespace VSDirector.Data;

public readonly record struct DirectorReplayElementFreezeState(
    bool Frozen,
    bool FullyFrozen,
    double ReplayTimeSeconds);

public static class DirectorReplayElementTimeline
{
    private const double TimeEpsilon = 0.000001d;
    private const double FreezeClientTicksShort = 0.999d;
    private const double FreezeClientTicksLong = 1.999d;
    public const double TicksPerSecond = 30d;
    public const int MaxFreezeDelayTicks = 15;
    public const float MinFovDegrees = 1f;
    public const float MaxFovDegrees = 150f;

    public static bool TrySample(
        IReadOnlyList<DirectorReplayElementKeyframe>? keyframes,
        DirectorReplayElementTrackType trackType,
        double timeSeconds,
        out DirectorReplayElementKeyframe sample)
        => TrySample(keyframes, trackType, Guid.Empty, timeSeconds, out sample);

    public static bool TrySample(
        IReadOnlyList<DirectorReplayElementKeyframe>? keyframes,
        DirectorReplayElementTrackType trackType,
        Guid trackId,
        double timeSeconds,
        out DirectorReplayElementKeyframe sample)
    {
        sample = new DirectorReplayElementKeyframe { TrackType = trackType };
        if (keyframes is null)
        {
            return false;
        }

        List<DirectorReplayElementKeyframe> track = keyframes
            .Where(keyframe => keyframe is not null
                && keyframe.TrackType == trackType
                && (trackId == Guid.Empty || keyframe.TrackId == trackId))
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .ThenBy(keyframe => keyframe.Id)
            .ToList();
        if (track.Count == 0 || timeSeconds + TimeEpsilon < track[0].TimeSeconds)
        {
            return false;
        }

        int upperIndex = track.FindIndex(keyframe => keyframe.TimeSeconds > timeSeconds + TimeEpsilon);
        if (upperIndex <= 0)
        {
            sample = track[^1].DeepClone();
            return true;
        }

        DirectorReplayElementKeyframe left = track[upperIndex - 1];
        DirectorReplayElementKeyframe right = track[upperIndex];
        if (left.InterpolationToNext == DirectorReplayElementInterpolation.Hold
            || right.TimeSeconds <= left.TimeSeconds + TimeEpsilon)
        {
            sample = left.DeepClone();
            return true;
        }

        double amount = Math.Clamp(
            (timeSeconds - left.TimeSeconds) / (right.TimeSeconds - left.TimeSeconds),
            0d,
            1d);
        sample = InterpolateFlashback(track, upperIndex - 1, amount);
        sample.TimeSeconds = timeSeconds;
        return true;
    }

    public static bool TrySample(
        DirectorReplayViewportTimeline timeline,
        DirectorReplayElementTrackType trackType,
        double timeSeconds,
        out DirectorReplayElementKeyframe sample)
    {
        sample = new DirectorReplayElementKeyframe { TrackType = trackType };
        foreach (DirectorReplayElementTrack track in timeline.Tracks.Where(track =>
            track.Enabled && track.TrackType == trackType))
        {
            if (TrySample(timeline.ElementKeyframes, trackType, track.Id, timeSeconds,
                    out DirectorReplayElementKeyframe candidate))
            {
                sample = candidate;
                return true;
            }
        }
        return false;
    }

    public static bool TrySampleFollowEntityInterval(
        IReadOnlyList<DirectorReplayElementKeyframe>? keyframes,
        Guid trackId,
        double timeSeconds,
        out DirectorReplayElementKeyframe start,
        out DirectorReplayElementKeyframe end)
    {
        start = new DirectorReplayElementKeyframe
        {
            TrackType = DirectorReplayElementTrackType.FollowEntity,
            FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.Start
        };
        end = new DirectorReplayElementKeyframe
        {
            TrackType = DirectorReplayElementTrackType.FollowEntity,
            FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.End
        };
        if (keyframes is null || !double.IsFinite(timeSeconds))
        {
            return false;
        }

        List<DirectorReplayElementKeyframe> frames = keyframes
            .Where(keyframe => keyframe is not null
                && keyframe.TrackType == DirectorReplayElementTrackType.FollowEntity
                && (trackId == Guid.Empty || keyframe.TrackId == trackId))
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .ThenBy(keyframe => keyframe.Id)
            .ToList();
        DirectorReplayElementKeyframe? selectedStart = frames
            .Where(keyframe => keyframe.FollowEntityRole == DirectorReplayFollowEntityKeyframeRole.Start
                && keyframe.TimeSeconds <= timeSeconds + TimeEpsilon)
            .LastOrDefault();
        if (selectedStart is null)
        {
            return false;
        }

        DirectorReplayElementKeyframe? selectedEnd = frames
            .Where(keyframe => keyframe.FollowEntityRole == DirectorReplayFollowEntityKeyframeRole.End
                && keyframe.TimeSeconds > selectedStart.TimeSeconds + TimeEpsilon)
            .FirstOrDefault();
        if (selectedEnd is null
            || timeSeconds + TimeEpsilon < selectedStart.TimeSeconds
            || timeSeconds + TimeEpsilon >= selectedEnd.TimeSeconds)
        {
            return false;
        }

        start = selectedStart.DeepClone();
        end = selectedEnd.DeepClone();
        return true;
    }

    public static bool TrySampleFollowEntityInterval(
        DirectorReplayViewportTimeline timeline,
        Guid trackId,
        double timeSeconds,
        out DirectorReplayElementKeyframe start,
        out DirectorReplayElementKeyframe end)
        => TrySampleFollowEntityInterval(timeline.ElementKeyframes, trackId, timeSeconds, out start, out end);

    public static double SamplePlaybackRate(
        DirectorReplayViewportTimeline timeline,
        double timeSeconds)
    {
        double rate = 1d;
        foreach (DirectorReplayElementTrack elementTrack in timeline.Tracks.Where(track =>
            track.Enabled && track.TrackType == DirectorReplayElementTrackType.Timelapse))
        {
            if (TrySampleTimelapseRate(timeline, elementTrack.Id, timeSeconds, out double sampled))
            {
                return sampled;
            }
        }
        return rate;
    }

    public static double SamplePlaybackRate(
        IReadOnlyList<DirectorReplayElementKeyframe>? keyframes,
        double timeSeconds)
        => SamplePlaybackRate(LegacyTimeline(keyframes), timeSeconds);

    public static double SampleSpeedMultiplier(
        DirectorReplayViewportTimeline timeline,
        double timeSeconds,
        double fallback)
    {
        double result = fallback;
        foreach (DirectorReplayElementTrack track in timeline.Tracks.Where(track =>
            track.Enabled && track.TrackType == DirectorReplayElementTrackType.Speed))
        {
            if (TrySample(timeline.ElementKeyframes, track.TrackType, track.Id, timeSeconds,
                    out DirectorReplayElementKeyframe keyframe))
            {
                return Math.Clamp(keyframe.TickRate / TicksPerSecond, 0.05d, 20d);
            }
        }
        return result;
    }

    /// <summary>
    /// Samples Speed and Timelapse as the same tick-rate change. Matching
    /// Flashback, the first enabled row that can produce a value wins.
    /// </summary>
    public static double SampleTimingRate(
        DirectorReplayViewportTimeline timeline,
        double timeSeconds,
        double fallback)
    {
        foreach (DirectorReplayElementTrack track in timeline.Tracks)
        {
            if (!track.Enabled)
            {
                continue;
            }

            if (track.TrackType == DirectorReplayElementTrackType.Speed
                && TrySample(timeline.ElementKeyframes, track.TrackType, track.Id, timeSeconds,
                    out DirectorReplayElementKeyframe speed))
            {
                return Math.Clamp(speed.TickRate / TicksPerSecond, 0.05d, 20d);
            }

            if (track.TrackType == DirectorReplayElementTrackType.Timelapse
                && TrySampleTimelapseRate(timeline, track.Id, timeSeconds, out double timelapse))
            {
                return timelapse;
            }
        }

        return fallback;
    }

    public static bool IsFrozen(
        DirectorReplayViewportTimeline timeline,
        double timeSeconds)
        => SampleFreeze(timeline, timeSeconds).FullyFrozen;

    public static bool IsFrozen(
        IReadOnlyList<DirectorReplayElementKeyframe>? keyframes,
        double timeSeconds)
        => IsFrozen(LegacyTimeline(keyframes), timeSeconds);

    /// <summary>
    /// Samples the freeze state and maps the logical replay clock to the
    /// replay content clock. The logical clock keeps moving so a later
    /// Frozen=false keyframe remains reachable; only the content clock is
    /// slowed and held while the replay is frozen.
    /// </summary>
    public static DirectorReplayElementFreezeState SampleFreeze(
        DirectorReplayViewportTimeline timeline,
        double timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        double logicalTime = double.IsFinite(timeSeconds) ? timeSeconds : 0d;
        List<DirectorReplayElementKeyframe>? activeTrack = null;
        int activeIndex = -1;
        foreach (DirectorReplayElementTrack track in timeline.Tracks.Where(track =>
            track.Enabled && track.TrackType == DirectorReplayElementTrackType.Freeze))
        {
            List<DirectorReplayElementKeyframe> keyframes = timeline.ElementKeyframes
                .Where(keyframe => keyframe is not null
                    && keyframe.TrackId == track.Id
                    && keyframe.TrackType == DirectorReplayElementTrackType.Freeze)
                .OrderBy(keyframe => keyframe.TimeSeconds)
                .ThenBy(keyframe => keyframe.Id)
                .ToList();
            int candidateIndex = keyframes.FindLastIndex(keyframe =>
                keyframe.TimeSeconds <= logicalTime + TimeEpsilon);
            if (candidateIndex >= 0)
            {
                activeTrack = keyframes;
                activeIndex = candidateIndex;
                break;
            }
        }

        if (activeTrack is null || activeIndex < 0 || !activeTrack[activeIndex].Frozen)
        {
            return new DirectorReplayElementFreezeState(
                Frozen: false,
                FullyFrozen: false,
                ReplayTimeSeconds: logicalTime);
        }

        int transitionStartIndex = activeIndex;
        while (transitionStartIndex > 0
            && activeTrack[transitionStartIndex - 1].Frozen)
        {
            transitionStartIndex--;
        }

        DirectorReplayElementKeyframe transitionStart = activeTrack[transitionStartIndex];
        int delayTicks = Math.Clamp(
            transitionStart.FreezeDelayTicks,
            0,
            MaxFreezeDelayTicks);
        double elapsedTicks = Math.Max(
            0d,
            (logicalTime - transitionStart.TimeSeconds) * TicksPerSecond);
        bool fullyFrozen = delayTicks <= 0
            || elapsedTicks + TimeEpsilon >= delayTicks;
        double replayTicks = FreezeReplayTicks(elapsedTicks, delayTicks);
        double replayTime = Math.Min(
            logicalTime,
            transitionStart.TimeSeconds + replayTicks / TicksPerSecond);
        return new DirectorReplayElementFreezeState(
            Frozen: true,
            FullyFrozen: fullyFrozen,
            ReplayTimeSeconds: replayTime);
    }

    private static double FreezeReplayTicks(double elapsedTicks, int delayTicks)
    {
        if (delayTicks <= 0)
        {
            return 0d;
        }

        double freezeClientTicks = delayTicks <= 5
            ? FreezeClientTicksShort
            : FreezeClientTicksLong;
        if (elapsedTicks + TimeEpsilon >= delayTicks)
        {
            return freezeClientTicks;
        }

        double freezePowerBase = FreezePowerBase(
            delayTicks,
            delayTicks <= 5 ? 1d : 0.5d);
        double power = Math.Pow(
            freezePowerBase,
            4d * elapsedTicks / delayTicks);
        double denominator = 1d - Math.Pow(freezePowerBase, 4d);
        return freezeClientTicks * (1d - power) / denominator;
    }

    private static double FreezePowerBase(int delayTicks, double targetDerivative)
    {
        double minimum = 0.05d;
        double maximum = 0.95d;
        if (FreezeDerivativeAtZero(delayTicks, minimum) < targetDerivative)
        {
            return minimum;
        }
        if (FreezeDerivativeAtZero(delayTicks, maximum) > targetDerivative)
        {
            return maximum;
        }

        for (int index = 0; index < 100; index++)
        {
            double middle = (minimum + maximum) * 0.5d;
            if (FreezeDerivativeAtZero(delayTicks, middle) > targetDerivative)
            {
                minimum = middle;
            }
            else
            {
                maximum = middle;
            }
        }
        return (minimum + maximum) * 0.5d;
    }

    private static double FreezeDerivativeAtZero(int delayTicks, double freezePower)
    {
        double freezePower4 = Math.Pow(freezePower, 4d);
        return -4d * Math.Log(freezePower)
            / (delayTicks - delayTicks * freezePower4);
    }

    private static DirectorReplayViewportTimeline LegacyTimeline(
        IReadOnlyList<DirectorReplayElementKeyframe>? keyframes)
    {
        DirectorReplayViewportTimeline timeline = new();
        foreach (IGrouping<DirectorReplayElementTrackType, DirectorReplayElementKeyframe> group in
            (keyframes ?? Array.Empty<DirectorReplayElementKeyframe>()).GroupBy(keyframe =>
                keyframe.TrackType))
        {
            DirectorReplayElementTrack track = new() { TrackType = group.Key };
            timeline.Tracks.Add(track);
            foreach (DirectorReplayElementKeyframe keyframe in group)
            {
                DirectorReplayElementKeyframe clone = keyframe.DeepClone();
                clone.TrackId = track.Id;
                timeline.ElementKeyframes.Add(clone);
            }
        }
        return timeline;
    }

    private static bool TrySampleTimelapseRate(
        DirectorReplayViewportTimeline timeline,
        Guid trackId,
        double timeSeconds,
        out double rate)
    {
        rate = 1d;
        DirectorReplayTimelapsePair? active = DirectorReplayTimelapse.FindPairs(
                timeline.ElementKeyframes,
                trackId)
            .Where(pair => pair.Start.TimeSeconds <= timeSeconds + TimeEpsilon
                && pair.End.TimeSeconds > timeSeconds + TimeEpsilon)
            .OrderByDescending(pair => pair.Start.TimeSeconds)
            .Select(pair => (DirectorReplayTimelapsePair?)pair)
            .FirstOrDefault();
        if (active is not DirectorReplayTimelapsePair pair)
        {
            return false;
        }

        DirectorReplayElementKeyframe lower = pair.Start;
        DirectorReplayElementKeyframe upper = pair.End;

        DirectorReplayElementTrack? track = timeline.Tracks.FirstOrDefault(item => item.Id == trackId);
        double durationSeconds = lower.TimelapseDurationSeconds;
        if (durationSeconds <= TimeEpsilon)
        {
            durationSeconds = upper.TimelapseDurationSeconds;
        }
        if (durationSeconds <= TimeEpsilon)
        {
            durationSeconds = track?.TimelapseDurationSeconds ?? 0d;
        }
        if (durationSeconds <= TimeEpsilon)
        {
            // Drafts from the original implementation derive the duration
            // from the two timeline keyframe positions.
            durationSeconds = upper.TimeSeconds - lower.TimeSeconds;
        }
        if (!double.IsFinite(durationSeconds) || durationSeconds <= TimeEpsilon)
        {
            return false;
        }

        double timelineTicks = (upper.TimeSeconds - lower.TimeSeconds) * TicksPerSecond;
        double playbackTicks = track?.TimelapseDurationSeconds > TimeEpsilon
            || lower.TimelapseDurationSeconds > TimeEpsilon
            ? durationSeconds * TicksPerSecond
            : upper.TimelapseTick - lower.TimelapseTick;
        if (!double.IsFinite(playbackTicks) || playbackTicks <= TimeEpsilon)
        {
            return false;
        }
        rate = Math.Clamp(timelineTicks / playbackTicks, 0.05d, 64d);
        return true;
    }

    // Flashback treats the interpolation type on the lower keyframe as the
    // right side of the interval, and the type on the upper keyframe as its
    // left side. Special spline sides are evaluated independently and then
    // blended with the original interval amount.
    private static DirectorReplayElementKeyframe InterpolateFlashback(
        IReadOnlyList<DirectorReplayElementKeyframe> track,
        int leftIndex,
        double amount)
    {
        DirectorReplayElementKeyframe left = track[leftIndex];
        DirectorReplayElementKeyframe right = track[leftIndex + 1];
        DirectorReplayElementKeyframe result = amount < 0.5d
            ? left.DeepClone()
            : right.DeepClone();

        result.X = InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.X);
        result.Y = InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.Y);
        result.Z = InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.Z);
        result.Distance = InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.Distance);
        result.Yaw = (float)InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.Yaw);
        result.Pitch = (float)InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.Pitch);
        result.Roll = (float)InterpolateFlashbackValue(
            track,
            leftIndex,
            amount,
            keyframe => keyframe.Roll,
            FlashbackInterpolationValueMode.Angle);
        result.Fov = (float)InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.Fov);
        result.OffsetX = InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.OffsetX);
        result.OffsetY = InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.OffsetY);
        result.OffsetZ = InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.OffsetZ);
        result.ViewOffsetX = InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.ViewOffsetX);
        result.ViewOffsetY = InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.ViewOffsetY);
        result.ViewOffsetZ = InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.ViewOffsetZ);
        result.FrequencyX = (float)InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.FrequencyX);
        result.AmplitudeX = (float)InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.AmplitudeX);
        result.FrequencyY = (float)InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.FrequencyY);
        result.AmplitudeY = (float)InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.AmplitudeY);
        result.TickRate = (float)InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.TickRate);
        result.TimelapseTick = (int)Math.Round(
            InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.TimelapseTick));
        result.TimeOfDayTick = (int)Math.Round(
            InterpolateFlashbackValue(
                track,
                leftIndex,
                amount,
                keyframe => keyframe.TimeOfDayTick,
                FlashbackInterpolationValueMode.Periodic,
                24000d));
        result.Volume = (float)InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.Volume);
        result.AudioPitch = (float)InterpolateFlashbackValue(track, leftIndex, amount, keyframe => keyframe.AudioPitch);
        return result;
    }

    private static double InterpolateFlashbackValue(
        IReadOnlyList<DirectorReplayElementKeyframe> track,
        int leftIndex,
        double amount,
        Func<DirectorReplayElementKeyframe, double> selector,
        FlashbackInterpolationValueMode mode = FlashbackInterpolationValueMode.Raw,
        double period = 0d)
    {
        DirectorReplayElementKeyframe left = track[leftIndex];
        DirectorReplayElementKeyframe right = track[leftIndex + 1];
        InterpolationSide leftSide = InterpolationSides(left.InterpolationToNext).Right;
        InterpolationSide rightSide = InterpolationSides(right.InterpolationToNext).Left;
        if (leftSide == InterpolationSide.Hold)
        {
            return selector(left);
        }
        if (rightSide == InterpolationSide.Hold)
        {
            rightSide = leftSide;
        }

        double ordinaryAmount = FlashbackInterpolationAmount(leftSide, rightSide, amount);
        double ordinary = InterpolateFlashbackSimple(
            selector(left),
            selector(right),
            ordinaryAmount,
            mode,
            period);
        double leftValue = IsFlashbackSpline(leftSide)
            ? InterpolateFlashbackSpline(track, leftIndex, amount, selector, leftSide, mode, period)
            : ordinary;
        double rightValue = IsFlashbackSpline(rightSide)
            ? InterpolateFlashbackSpline(track, leftIndex, amount, selector, rightSide, mode, period)
            : ordinary;
        return InterpolateFlashbackSimple(leftValue, rightValue, amount, mode, period);
    }

    private static double InterpolateFlashbackSpline(
        IReadOnlyList<DirectorReplayElementKeyframe> track,
        int leftIndex,
        double amount,
        Func<DirectorReplayElementKeyframe, double> selector,
        InterpolationSide interpolation,
        FlashbackInterpolationValueMode mode,
        double period)
    {
        if (interpolation == InterpolationSide.Smooth)
        {
            int previousIndex = leftIndex - 1;
            if (previousIndex < 0
                || track[previousIndex].InterpolationToNext == DirectorReplayElementInterpolation.Hold)
            {
                previousIndex = leftIndex;
            }

            int nextIndex = leftIndex + 2;
            if (nextIndex >= track.Count
                || track[leftIndex + 1].InterpolationToNext == DirectorReplayElementInterpolation.Hold)
            {
                nextIndex = leftIndex + 1;
            }

            double p0 = selector(track[previousIndex]);
            double p1 = selector(track[leftIndex]);
            double p2 = selector(track[leftIndex + 1]);
            double p3 = selector(track[nextIndex]);
            if (mode != FlashbackInterpolationValueMode.Raw)
            {
                p0 = NormalizeFlashbackSplineStart(p0, period, mode);
                p1 = p0 + NormalizeFlashbackPeriodic(p1 - p0, period, mode);
                p2 = p1 + NormalizeFlashbackPeriodic(p2 - p1, period, mode);
                p3 = p2 + NormalizeFlashbackPeriodic(p3 - p2, period, mode);
            }

            return CatmullRomValue(
                p0,
                p1,
                p2,
                p3,
                track[previousIndex].TimeSeconds,
                track[leftIndex].TimeSeconds,
                track[leftIndex + 1].TimeSeconds,
                track[nextIndex].TimeSeconds,
                amount);
        }

        return HermiteFlashbackValue(track, leftIndex, selector, amount, mode, period);
    }

    private static double FlashbackInterpolationAmount(
        InterpolationSide left,
        InterpolationSide right,
        double amount)
    {
        double value = Math.Clamp(amount, 0d, 1d);
        if (IsFlashbackSpline(left))
        {
            if (IsFlashbackSpline(right))
            {
                left = InterpolationSide.Linear;
                right = InterpolationSide.Linear;
            }
            else
            {
                left = right;
            }
        }
        else if (IsFlashbackSpline(right))
        {
            right = left;
        }

        if (left == InterpolationSide.Hold)
        {
            return 0d;
        }
        if (right == InterpolationSide.Hold)
        {
            right = left;
        }
        if (left == InterpolationSide.Linear)
        {
            return right == InterpolationSide.Ease
                ? 1d - Math.Pow(1d - value, 3d)
                : value;
        }
        if (left == InterpolationSide.Ease)
        {
            if (right == InterpolationSide.Linear)
            {
                return value * value * value;
            }
            if (right == InterpolationSide.Ease)
            {
                return value < 0.5d
                    ? 4d * value * value * value
                    : 1d - Math.Pow(-2d * value + 2d, 3d) / 2d;
            }
        }
        return value;
    }

    private static bool IsFlashbackSpline(InterpolationSide interpolation)
        => interpolation is InterpolationSide.Smooth or InterpolationSide.Hermite;

    private static double InterpolateFlashbackSimple(
        double left,
        double right,
        double amount,
        FlashbackInterpolationValueMode mode,
        double period)
    {
        double result = mode switch
        {
            FlashbackInterpolationValueMode.Angle
                => left + NormalizeAngle(right - left) * Math.Clamp(amount, 0d, 1d),
            FlashbackInterpolationValueMode.Periodic
                => LerpWrappedDay(left, right, period, amount),
            _ => Lerp(left, right, amount)
        };
        return mode switch
        {
            FlashbackInterpolationValueMode.Angle => NormalizeAngle(result),
            FlashbackInterpolationValueMode.Periodic => NormalizePeriodicValue(result, period),
            _ => result
        };
    }

    private static double HermiteFlashbackValue(
        IReadOnlyList<DirectorReplayElementKeyframe> track,
        int leftIndex,
        Func<DirectorReplayElementKeyframe, double> selector,
        double amount,
        FlashbackInterpolationValueMode mode,
        double period)
    {
        int firstIndex = leftIndex;
        while (firstIndex > 0
            && track[firstIndex - 1].InterpolationToNext != DirectorReplayElementInterpolation.Hold)
        {
            firstIndex--;
        }
        int lastIndex = leftIndex + 1;
        while (lastIndex + 1 < track.Count)
        {
            lastIndex++;
            if (track[lastIndex].InterpolationToNext == DirectorReplayElementInterpolation.Hold)
            {
                break;
            }
        }

        double query = track[leftIndex].TimeSeconds
            + (track[leftIndex + 1].TimeSeconds - track[leftIndex].TimeSeconds)
            * Math.Clamp(amount, 0d, 1d);
        List<(double Time, double Value)> points = new();
        for (int index = firstIndex; index <= lastIndex; index++)
        {
            double time = track[index].TimeSeconds;
            double value = selector(track[index]);
            if (mode != FlashbackInterpolationValueMode.Raw)
            {
                value = points.Count == 0
                    ? NormalizeFlashbackSplineStart(value, period, mode)
                    : points[^1].Value + NormalizeFlashbackPeriodic(
                        value - points[^1].Value,
                        period,
                        mode);
            }

            if (points.Count > 0 && Math.Abs(points[^1].Time - time) <= TimeEpsilon)
            {
                points[^1] = (time, value);
            }
            else
            {
                points.Add((time, value));
            }
        }
        if (points.Count < 2)
        {
            return InterpolateFlashbackSimple(
                selector(track[leftIndex]),
                selector(track[leftIndex + 1]),
                amount,
                mode,
                period);
        }

        double result = 0d;
        for (int index = 0; index < points.Count; index++)
        {
            double denominator = 1d;
            double term = points[index].Value;
            for (int other = 0; other < points.Count; other++)
            {
                if (other == index)
                {
                    continue;
                }
                double delta = points[index].Time - points[other].Time;
                if (Math.Abs(delta) <= TimeEpsilon)
                {
                    denominator = 0d;
                    break;
                }
                term *= (query - points[other].Time) / delta;
            }
            if (Math.Abs(denominator) > TimeEpsilon)
            {
                result += term;
            }
        }
        return mode switch
        {
            FlashbackInterpolationValueMode.Angle => NormalizeAngle(result),
            FlashbackInterpolationValueMode.Periodic => NormalizePeriodicValue(result, period),
            _ => result
        };
    }

    private static double NormalizeFlashbackPeriodic(
        double value,
        double period,
        FlashbackInterpolationValueMode mode)
        => mode == FlashbackInterpolationValueMode.Angle
            ? NormalizeAngle(value)
            : period > TimeEpsilon
                ? NormalizePeriodicDelta(value, period)
                : value;

    private static double NormalizeFlashbackSplineStart(
        double value,
        double period,
        FlashbackInterpolationValueMode mode)
        => mode == FlashbackInterpolationValueMode.Angle
            ? NormalizeAngle(value)
            : mode == FlashbackInterpolationValueMode.Periodic
                ? NormalizePeriodicValue(value, period)
                : value;

    private static double NormalizePeriodicDelta(double value, double period)
    {
        double result = value % period;
        if (result > period * 0.5d)
        {
            result -= period;
        }
        else if (result < period * -0.5d)
        {
            result += period;
        }
        return result;
    }

    private static double NormalizePeriodicValue(double value, double period)
    {
        if (period <= TimeEpsilon)
        {
            return value;
        }
        double result = value % period;
        return result < 0d ? result + period : result;
    }

    private enum FlashbackInterpolationValueMode
    {
        Raw,
        Angle,
        Periodic
    }

    private static (InterpolationSide Left, InterpolationSide Right) InterpolationSides(
        DirectorReplayElementInterpolation interpolation)
        => interpolation switch
        {
            DirectorReplayElementInterpolation.Hold
                => (InterpolationSide.Hold, InterpolationSide.Hold),
            DirectorReplayElementInterpolation.EaseIn
                => (InterpolationSide.Ease, InterpolationSide.Linear),
            DirectorReplayElementInterpolation.EaseOut
                => (InterpolationSide.Linear, InterpolationSide.Ease),
            DirectorReplayElementInterpolation.EaseInOut
                => (InterpolationSide.Ease, InterpolationSide.Ease),
            DirectorReplayElementInterpolation.Smooth
                => (InterpolationSide.Smooth, InterpolationSide.Smooth),
            DirectorReplayElementInterpolation.Hermite
                => (InterpolationSide.Hermite, InterpolationSide.Hermite),
            _ => (InterpolationSide.Linear, InterpolationSide.Linear)
        };

    private static double CatmullRomValue(
        double p0,
        double p1,
        double p2,
        double p3,
        double t0,
        double t1,
        double t2,
        double t3,
        double amount)
    {
        double time1 = Math.Max(0d, t1 - t0);
        double time2 = Math.Max(time1, t2 - t0);
        double time3 = Math.Max(time2, t3 - t0);
        double tj1 = Math.Sqrt(Math.Abs(p1 - p0));
        double tj2 = Math.Sqrt(Math.Abs(p2 - p1));
        double tj3 = Math.Sqrt(Math.Abs(p3 - p2));
        double averageTj = (tj1 + tj2 + tj3) / 3d;
        if (averageTj <= TimeEpsilon || time3 <= TimeEpsilon)
        {
            return p1;
        }

        double relation = (time3 / 3d) / averageTj;
        double deltaTime1 = ScaledCatmullDelta(tj1, relation, time1);
        double deltaTime2 = ScaledCatmullDelta(tj2, relation, time2 - time1);
        double deltaTime3 = ScaledCatmullDelta(tj3, relation, time3 - time2);
        double u0 = 0d;
        double u1 = u0 + deltaTime1;
        double u2 = u1 + deltaTime2;
        double u3 = u2 + deltaTime3;
        double u = u1 + (u2 - u1) * Math.Clamp(amount, 0d, 1d);
        double a1 = InterpolateAt(p0, p1, u, u0, u1);
        double a2 = InterpolateAt(p1, p2, u, u1, u2);
        double a3 = InterpolateAt(p2, p3, u, u2, u3);
        double b1 = InterpolateAt(a1, a2, u, u0, u2);
        double b2 = InterpolateAt(a2, a3, u, u1, u3);
        return InterpolateAt(b1, b2, u, u1, u2);
    }

    private static double ScaledCatmullDelta(
        double centripetal,
        double relation,
        double segmentTime)
    {
        if (segmentTime <= TimeEpsilon)
        {
            return 0d;
        }
        double factor = Math.Clamp(centripetal * relation / segmentTime, 0.4d, 2.5d);
        return centripetal * relation / Math.Max(TimeEpsilon, factor);
    }

    private static double InterpolateAt(
        double from,
        double to,
        double value,
        double fromTime,
        double toTime)
    {
        if (Math.Abs(toTime - fromTime) <= TimeEpsilon)
        {
            return (from + to) * 0.5d;
        }
        return Lerp(from, to, (value - fromTime) / (toTime - fromTime));
    }

    private static double Lerp(double left, double right, double amount)
        => left + (right - left) * amount;

    private static double LerpWrappedDay(double left, double right, double period, double amount)
    {
        double delta = (right - left) % period;
        if (delta > period * 0.5d)
        {
            delta -= period;
        }
        else if (delta < period * -0.5d)
        {
            delta += period;
        }
        double value = (left + delta * amount) % period;
        return value < 0d ? value + period : value;
    }

    private static double NormalizeAngle(double value)
    {
        double result = value % 360d;
        if (result > 180d)
        {
            result -= 360d;
        }
        else if (result < -180d)
        {
            result += 360d;
        }
        return result;
    }

    private enum InterpolationSide
    {
        Linear,
        Ease,
        Hold,
        Smooth,
        Hermite
    }
}
