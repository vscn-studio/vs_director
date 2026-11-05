namespace VSDirector.Data;

public static class DirectorReplayTimeline
{
    public const double MinSpeed = 0.05d;

    public const double MaxSpeed = 64d;

    public static double NormalizeSpeed(double speed)
        => double.IsFinite(speed) ? Math.Clamp(speed, MinSpeed, MaxSpeed) : 1d;

    public static List<DirectorReplayMarker> NormalizeMarkers(
        IEnumerable<DirectorReplayMarker>? source,
        double durationSeconds)
    {
        double duration = double.IsFinite(durationSeconds) ? Math.Max(0d, durationSeconds) : 0d;
        HashSet<Guid> ids = new();
        int fallbackIndex = 1;
        return (source ?? Array.Empty<DirectorReplayMarker>())
            .Where(item => item is not null)
            .Select(item =>
            {
                Guid id = item.Id != Guid.Empty && ids.Add(item.Id) ? item.Id : Guid.NewGuid();
                ids.Add(id);
                string label = item.Label?.Trim() ?? string.Empty;
                if (label.Length == 0)
                {
                    label = $"Marker {fallbackIndex}";
                }
                fallbackIndex++;
                return new DirectorReplayMarker
                {
                    Id = id,
                    TimeSeconds = Math.Clamp(FiniteNonNegative(item.TimeSeconds), 0d, duration),
                    Label = label.Length <= 80 ? label : label[..80]
                };
            })
            .OrderBy(item => item.TimeSeconds)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<DirectorReplaySpeedKeyframe> NormalizeSpeedKeyframes(
        IEnumerable<DirectorReplaySpeedKeyframe>? source,
        double durationSeconds)
    {
        double duration = double.IsFinite(durationSeconds) ? Math.Max(0d, durationSeconds) : 0d;
        HashSet<Guid> ids = new();
        return (source ?? Array.Empty<DirectorReplaySpeedKeyframe>())
            .Where(item => item is not null)
            .Select(item =>
            {
                Guid id = item.Id != Guid.Empty && ids.Add(item.Id) ? item.Id : Guid.NewGuid();
                ids.Add(id);
                return new DirectorReplaySpeedKeyframe
                {
                    Id = id,
                    TimeSeconds = Math.Clamp(FiniteNonNegative(item.TimeSeconds), 0d, duration),
                    Speed = NormalizeSpeed(item.Speed),
                    InterpolationToNext = Enum.IsDefined(item.InterpolationToNext)
                        ? item.InterpolationToNext
                        : DirectorReplaySpeedInterpolation.Linear
                };
            })
            .GroupBy(item => item.TimeSeconds)
            .Select(group => group.Last())
            .OrderBy(item => item.TimeSeconds)
            .ToList();
    }

    public static double SampleSpeed(
        IReadOnlyList<DirectorReplaySpeedKeyframe>? keyframes,
        double timeSeconds,
        double fallbackSpeed = 1d)
    {
        double fallback = NormalizeSpeed(fallbackSpeed);
        if (keyframes is not { Count: > 0 })
        {
            return fallback;
        }

        double time = double.IsFinite(timeSeconds) ? Math.Max(0d, timeSeconds) : 0d;
        if (time < keyframes[0].TimeSeconds)
        {
            return fallback;
        }

        int low = 0;
        int high = keyframes.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (keyframes[middle].TimeSeconds <= time)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        DirectorReplaySpeedKeyframe previous = keyframes[low];
        if (low + 1 >= keyframes.Count
            || previous.InterpolationToNext == DirectorReplaySpeedInterpolation.Hold)
        {
            return NormalizeSpeed(previous.Speed);
        }

        DirectorReplaySpeedKeyframe next = keyframes[low + 1];
        double span = next.TimeSeconds - previous.TimeSeconds;
        if (span <= double.Epsilon)
        {
            return NormalizeSpeed(previous.Speed);
        }

        double amount = Math.Clamp((time - previous.TimeSeconds) / span, 0d, 1d);
        if (previous.InterpolationToNext == DirectorReplaySpeedInterpolation.EaseInOut)
        {
            amount = amount * amount * (3d - 2d * amount);
        }

        return NormalizeSpeed(previous.Speed
            + (NormalizeSpeed(next.Speed) - NormalizeSpeed(previous.Speed)) * amount);
    }

    public static double AdjustedDurationSeconds(
        double durationSeconds,
        IReadOnlyList<DirectorReplaySpeedKeyframe>? keyframes,
        double fallbackSpeed = 1d)
        => AdjustedElapsedSeconds(durationSeconds, durationSeconds, keyframes, fallbackSpeed);

    public static double AdjustedElapsedSeconds(
        double timeSeconds,
        double durationSeconds,
        IReadOnlyList<DirectorReplaySpeedKeyframe>? keyframes,
        double fallbackSpeed = 1d)
    {
        double duration = double.IsFinite(durationSeconds) ? Math.Max(0d, durationSeconds) : 0d;
        double elapsed = double.IsFinite(timeSeconds)
            ? Math.Clamp(timeSeconds, 0d, duration)
            : 0d;
        if (elapsed <= double.Epsilon)
        {
            return 0d;
        }

        double fallback = NormalizeSpeed(fallbackSpeed);
        List<DirectorReplaySpeedKeyframe> normalized = NormalizeSpeedKeyframes(keyframes, duration);
        if (normalized.Count == 0)
        {
            return elapsed / fallback;
        }

        double adjusted = 0d;
        if (normalized[0].TimeSeconds > 0d)
        {
            double fallbackEnd = Math.Min(elapsed, normalized[0].TimeSeconds);
            adjusted += fallbackEnd / fallback;
            if (elapsed <= normalized[0].TimeSeconds)
            {
                return adjusted;
            }
        }

        for (int index = 0; index < normalized.Count; index++)
        {
            DirectorReplaySpeedKeyframe current = normalized[index];
            double segmentStart = current.TimeSeconds;
            if (elapsed <= segmentStart)
            {
                break;
            }

            double fullSegmentEnd = index + 1 < normalized.Count
                ? normalized[index + 1].TimeSeconds
                : duration;
            fullSegmentEnd = Math.Clamp(fullSegmentEnd, segmentStart, duration);
            double fullSpan = fullSegmentEnd - segmentStart;
            if (fullSpan <= double.Epsilon)
            {
                continue;
            }

            double segmentEnd = Math.Min(elapsed, fullSegmentEnd);
            double elapsedSpan = segmentEnd - segmentStart;
            double startSpeed = NormalizeSpeed(current.Speed);
            double endSpeed = index + 1 < normalized.Count
                ? NormalizeSpeed(normalized[index + 1].Speed)
                : startSpeed;
            adjusted += IntegrateSpeedSegment(
                fullSpan,
                elapsedSpan,
                startSpeed,
                endSpeed,
                current.InterpolationToNext);
            if (elapsed <= fullSegmentEnd)
            {
                break;
            }
        }

        return double.IsFinite(adjusted) ? Math.Max(0d, adjusted) : elapsed / fallback;
    }

    private static double IntegrateSpeedSegment(
        double fullSpan,
        double elapsedSpan,
        double startSpeed,
        double endSpeed,
        DirectorReplaySpeedInterpolation interpolation)
    {
        double amount = Math.Clamp(elapsedSpan / fullSpan, 0d, 1d);
        return interpolation switch
        {
            DirectorReplaySpeedInterpolation.Hold => elapsedSpan / startSpeed,
            DirectorReplaySpeedInterpolation.Linear => IntegrateLinearSpeed(
                fullSpan,
                amount,
                startSpeed,
                endSpeed),
            DirectorReplaySpeedInterpolation.EaseInOut => IntegrateEaseSpeed(
                fullSpan,
                amount,
                startSpeed,
                endSpeed),
            _ => elapsedSpan / startSpeed
        };
    }

    private static double IntegrateLinearSpeed(
        double fullSpan,
        double endAmount,
        double startSpeed,
        double endSpeed)
    {
        double difference = endSpeed - startSpeed;
        if (Math.Abs(difference) <= 0.000000001d)
        {
            return fullSpan * endAmount / startSpeed;
        }
        double partialEndSpeed = startSpeed + difference * endAmount;
        return fullSpan * Math.Log(partialEndSpeed / startSpeed) / difference;
    }

    private static double IntegrateEaseSpeed(
        double fullSpan,
        double endAmount,
        double startSpeed,
        double endSpeed)
    {
        const int intervals = 128;
        double difference = endSpeed - startSpeed;
        if (Math.Abs(difference) <= 0.000000001d)
        {
            return fullSpan * endAmount / startSpeed;
        }
        if (endAmount <= double.Epsilon)
        {
            return 0d;
        }

        double step = endAmount / intervals;
        double sum = ReciprocalEaseSpeed(0d, startSpeed, difference)
            + ReciprocalEaseSpeed(endAmount, startSpeed, difference);
        for (int index = 1; index < intervals; index++)
        {
            double amount = index * step;
            sum += (index & 1) == 0 ? 2d * ReciprocalEaseSpeed(amount, startSpeed, difference)
                : 4d * ReciprocalEaseSpeed(amount, startSpeed, difference);
        }
        return fullSpan * step * sum / 3d;
    }

    private static double ReciprocalEaseSpeed(double amount, double startSpeed, double difference)
    {
        double eased = amount * amount * (3d - 2d * amount);
        return 1d / NormalizeSpeed(startSpeed + difference * eased);
    }

    private static double FiniteNonNegative(double value)
        => double.IsFinite(value) ? Math.Max(0d, value) : 0d;
}
