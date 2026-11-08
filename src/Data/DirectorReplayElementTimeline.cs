namespace VSDirector.Data;

public readonly record struct DirectorReplayElementFreezeState(
    bool Frozen,
    bool FullyFrozen,
    double ReplayTimeSeconds);

public static class DirectorReplayElementTimeline
{
    private const double TimeEpsilon = 0.000001d;
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
        sample = SampleSegment(track, upperIndex - 1, amount);
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
    /// Samples the first enabled timing row with a value at the requested time.
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
        if (delayTicks <= 0) return 0d;

        // Integrate velocity v(u) = (1-u)^2 over the requested slowdown duration.
        // This starts at normal speed, reaches zero continuously, and then holds.
        double u = Math.Clamp(elapsedTicks / delayTicks, 0d, 1d);
        return delayTicks * u * (1d - u + u * u / 3d);
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

    // A keyframe owns its outgoing segment. Incoming modes do not alter it.
    private static DirectorReplayElementKeyframe SampleSegment(
        IReadOnlyList<DirectorReplayElementKeyframe> frames, int index, double u)
    {
        DirectorReplayElementKeyframe result = frames[index].DeepClone();
        result.X = SampleChannel(frames, index, u, frame => frame.X);
        result.Y = SampleChannel(frames, index, u, frame => frame.Y);
        result.Z = SampleChannel(frames, index, u, frame => frame.Z);
        result.Distance = SampleChannel(frames, index, u, frame => frame.Distance);
        result.Yaw = (float)SampleChannel(frames, index, u, frame => frame.Yaw);
        result.Pitch = (float)SampleChannel(frames, index, u, frame => frame.Pitch);
        result.Roll = (float)SampleChannel(frames, index, u, frame => frame.Roll, 360d);
        result.Fov = (float)SampleChannel(frames, index, u, frame => frame.Fov);
        result.OffsetX = SampleChannel(frames, index, u, frame => frame.OffsetX);
        result.OffsetY = SampleChannel(frames, index, u, frame => frame.OffsetY);
        result.OffsetZ = SampleChannel(frames, index, u, frame => frame.OffsetZ);
        result.ViewOffsetX = SampleChannel(frames, index, u, frame => frame.ViewOffsetX);
        result.ViewOffsetY = SampleChannel(frames, index, u, frame => frame.ViewOffsetY);
        result.ViewOffsetZ = SampleChannel(frames, index, u, frame => frame.ViewOffsetZ);
        result.FrequencyX = (float)SampleChannel(frames, index, u, frame => frame.FrequencyX);
        result.AmplitudeX = (float)SampleChannel(frames, index, u, frame => frame.AmplitudeX);
        result.FrequencyY = (float)SampleChannel(frames, index, u, frame => frame.FrequencyY);
        result.AmplitudeY = (float)SampleChannel(frames, index, u, frame => frame.AmplitudeY);
        result.TickRate = (float)SampleChannel(frames, index, u, frame => frame.TickRate);
        result.TimelapseTick = (int)Math.Round(SampleChannel(frames, index, u, frame => frame.TimelapseTick));
        result.TimeOfDayTick = (int)Math.Round(SampleChannel(frames, index, u, frame => frame.TimeOfDayTick, 24000d));
        result.Volume = (float)SampleChannel(frames, index, u, frame => frame.Volume);
        result.AudioPitch = (float)SampleChannel(frames, index, u, frame => frame.AudioPitch);
        return result;
    }

    private static double SampleChannel(
        IReadOnlyList<DirectorReplayElementKeyframe> frames, int i, double u,
        Func<DirectorReplayElementKeyframe, double> read, double period = 0d)
    {
        double a = read(frames[i]);
        double b = a + ChannelDelta(read(frames[i + 1]) - a, period);
        double duration = frames[i + 1].TimeSeconds - frames[i].TimeSeconds;
        DirectorReplayElementInterpolation mode = frames[i].InterpolationToNext;
        double value;
        if (mode is DirectorReplayElementInterpolation.Smooth or DirectorReplayElementInterpolation.Hermite)
        {
            double slope = (b - a) / duration;
            double startTangent = slope;
            double endTangent = slope;
            if (i > 0)
            {
                double before = frames[i].TimeSeconds - frames[i - 1].TimeSeconds;
                if (before > TimeEpsilon)
                {
                    double previousSlope = ChannelDelta(a - read(frames[i - 1]), period) / before;
                    startTangent = SegmentTangent(previousSlope, slope, before, duration, mode);
                }
            }
            if (i + 2 < frames.Count)
            {
                double after = frames[i + 2].TimeSeconds - frames[i + 1].TimeSeconds;
                if (after > TimeEpsilon)
                {
                    double nextSlope = ChannelDelta(read(frames[i + 2]) - b, period) / after;
                    endTangent = SegmentTangent(slope, nextSlope, duration, after, mode);
                }
            }
            // Cubic Hermite basis with derivatives expressed per second.
            double u2 = u * u;
            double u3 = u2 * u;
            value = (2d * u3 - 3d * u2 + 1d) * a
                + (u3 - 2d * u2 + u) * duration * startTangent
                + (3d * u2 - 2d * u3) * b
                + (u3 - u2) * duration * endTangent;
        }
        else
        {
            double weight = mode switch
            {
                DirectorReplayElementInterpolation.Hold => 0d,
                DirectorReplayElementInterpolation.EaseIn => 1d - Math.Cos(u * Math.PI / 2d),
                DirectorReplayElementInterpolation.EaseOut => Math.Sin(u * Math.PI / 2d),
                DirectorReplayElementInterpolation.EaseInOut => (1d - Math.Cos(u * Math.PI)) / 2d,
                _ => u
            };
            value = a + (b - a) * weight;
        }
        if (!double.IsFinite(value)) value = double.IsFinite(a) ? a : 0d;
        if (period <= 0d) return value;
        value = ((value % period) + period) % period;
        return period == 360d && value > 180d ? value - period : value;
    }

    private static double SegmentTangent(double incoming, double outgoing,
        double before, double after, DirectorReplayElementInterpolation mode)
    {
        if (mode == DirectorReplayElementInterpolation.Hermite)
            return (incoming * before + outgoing * after) / (before + after);

        // PCHIP interior derivative: flatten at extrema and preserve monotonicity.
        if (incoming == 0d || outgoing == 0d || Math.Sign(incoming) != Math.Sign(outgoing))
            return 0d;
        double firstWeight = 2d * after + before;
        double secondWeight = after + 2d * before;
        return (firstWeight + secondWeight) / (firstWeight / incoming + secondWeight / outgoing);
    }

    private static double ChannelDelta(double delta, double period)
        => period > 0d ? delta - period * Math.Floor(delta / period + 0.5d) : delta;
}
