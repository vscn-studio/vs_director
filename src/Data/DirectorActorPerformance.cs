namespace VSDirector.Data;

public enum DirectorActorTransformInterpolation
{
    Linear,
    EaseInOut
}

public enum DirectorActorCueType
{
    AnimationSet,
    AnimationStop,
    Visibility,
    LookAt
}

public sealed class DirectorActorLookAtTarget
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public int Dimension { get; set; }

    public DirectorActorLookAtTarget DeepClone() => (DirectorActorLookAtTarget)MemberwiseClone();
}

/// <summary>
/// A dimension-local actor transform. Replay actor angles are retained in
/// radians so a sampled transform can be applied without unit conversion.
/// </summary>
public sealed class DirectorActorTransformKeyframe
{
    public double TimeSeconds { get; set; }

    public DirectorActorTransformInterpolation? InterpolationToNext { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public int Dimension { get; set; }

    public float Yaw { get; set; }

    public float Pitch { get; set; }

    public float Roll { get; set; }

    public float HeadYaw { get; set; }

    public float HeadPitch { get; set; }

    public float BodyYaw { get; set; }

    public DirectorActorTransformKeyframe DeepClone()
        => (DirectorActorTransformKeyframe)MemberwiseClone();
}

public sealed class DirectorActorCue
{
    public Guid Id { get; set; }

    public double TimeSeconds { get; set; }

    /// <summary>Stable order for cues that share a timestamp.</summary>
    public int Order { get; set; }

    public DirectorActorCueType Type { get; set; }

    public List<string> Animations { get; set; } = new();

    public bool Visible { get; set; } = true;

    public DirectorActorLookAtTarget? LookAt { get; set; }

    public DirectorActorCue DeepClone()
    {
        return new DirectorActorCue
        {
            Id = Id,
            TimeSeconds = TimeSeconds,
            Order = Order,
            Type = Type,
            Animations = Animations is null ? new List<string>() : new List<string>(Animations),
            Visible = Visible,
            LookAt = LookAt?.DeepClone()
        };
    }
}

/// <summary>
/// Pure data describing one actor's transform and persistent action state.
/// It does not grant authority to move a live multiplayer player.
/// </summary>
public sealed class DirectorActorPerformance
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DirectorActorTransformInterpolation Interpolation { get; set; }
        = DirectorActorTransformInterpolation.Linear;

    public Guid SourceReplayId { get; set; }

    public string SourceActorKey { get; set; } = string.Empty;

    public long SourceEntityId { get; set; }

    public string SourcePlayerUid { get; set; } = string.Empty;

    public string SourceEntityCode { get; set; } = string.Empty;

    public double ReplayTimeOffsetSeconds { get; set; }

    public byte[] InitialState { get; set; } = Array.Empty<byte>();

    public List<DirectorActorTransformKeyframe> TransformKeyframes { get; set; } = new();

    public List<DirectorActorCue> Cues { get; set; } = new();

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }

    public double DurationSeconds
        => Math.Max(
            TransformKeyframes?.Select(frame => frame?.TimeSeconds ?? 0d).DefaultIfEmpty().Max() ?? 0d,
            Cues?.Select(cue => cue?.TimeSeconds ?? 0d).DefaultIfEmpty().Max() ?? 0d);

    public DirectorActorPerformance DeepClone()
    {
        return new DirectorActorPerformance
        {
            Id = Id,
            Name = Name,
            Interpolation = Interpolation,
            SourceReplayId = SourceReplayId,
            SourceActorKey = SourceActorKey,
            SourceEntityId = SourceEntityId,
            SourcePlayerUid = SourcePlayerUid,
            SourceEntityCode = SourceEntityCode,
            ReplayTimeOffsetSeconds = ReplayTimeOffsetSeconds,
            InitialState = InitialState is null ? Array.Empty<byte>() : (byte[])InitialState.Clone(),
            TransformKeyframes = TransformKeyframes is null
                ? new List<DirectorActorTransformKeyframe>()
                : TransformKeyframes
                    .Where(frame => frame is not null)
                    .Select(frame => frame.DeepClone())
                    .ToList(),
            Cues = Cues is null
                ? new List<DirectorActorCue>()
                : Cues.Where(cue => cue is not null).Select(cue => cue.DeepClone()).ToList(),
            CreatedUtc = CreatedUtc,
            ModifiedUtc = ModifiedUtc
        };
    }
}

public sealed class DirectorActorPerformanceState
{
    public double TimeSeconds { get; init; }

    public DirectorActorTransformKeyframe? Transform { get; init; }

    public List<string> Animations { get; init; } = new();

    public bool Visible { get; init; } = true;

    public DirectorActorLookAtTarget? LookAt { get; init; }

    public DirectorActorPerformanceState DeepClone()
    {
        return new DirectorActorPerformanceState
        {
            TimeSeconds = TimeSeconds,
            Transform = Transform?.DeepClone(),
            Animations = new List<string>(Animations),
            Visible = Visible,
            LookAt = LookAt?.DeepClone()
        };
    }
}

public sealed class DirectorActorPerformanceEvaluation
{
    public required DirectorActorPerformanceState State { get; init; }

    public IReadOnlyList<DirectorActorCue> CrossedCues { get; init; }
        = Array.Empty<DirectorActorCue>();
}

public static class DirectorActorPerformanceNormalizer
{
    public const int MaximumTransformKeyframes = 250_000;
    public const int MaximumCues = 100_000;
    public const int MaximumNameLength = 96;
    public const int MaximumAnimationCodeLength = 128;
    public const double TimeEpsilon = 0.000001d;

    public static DirectorActorPerformance Normalize(DirectorActorPerformance? source)
    {
        source ??= new DirectorActorPerformance();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset createdUtc = NormalizeTimestamp(source.CreatedUtc, now);
        DateTimeOffset modifiedUtc = NormalizeTimestamp(source.ModifiedUtc, createdUtc);
        if (modifiedUtc < createdUtc)
        {
            modifiedUtc = createdUtc;
        }

        return new DirectorActorPerformance
        {
            Id = source.Id == Guid.Empty ? Guid.NewGuid() : source.Id,
            Name = NormalizeName(source.Name),
            Interpolation = Enum.IsDefined(source.Interpolation)
                ? source.Interpolation
                : DirectorActorTransformInterpolation.Linear,
            SourceReplayId = source.SourceReplayId,
            SourceActorKey = NormalizeText(source.SourceActorKey),
            SourceEntityId = source.SourceEntityId,
            SourcePlayerUid = NormalizeText(source.SourcePlayerUid),
            SourceEntityCode = NormalizeText(source.SourceEntityCode),
            ReplayTimeOffsetSeconds = NormalizeTime(source.ReplayTimeOffsetSeconds),
            InitialState = source.InitialState is null
                ? Array.Empty<byte>()
                : (byte[])source.InitialState.Clone(),
            TransformKeyframes = NormalizeTransforms(source.TransformKeyframes),
            Cues = NormalizeCues(source.Cues),
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc
        };
    }

    public static List<string> NormalizeAnimations(IEnumerable<string>? animations)
    {
        if (animations is null)
        {
            return new List<string>();
        }

        return animations
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Where(code => code.Length > 0)
            .Select(code => code.Length <= MaximumAnimationCodeLength
                ? code
                : code[..MaximumAnimationCodeLength])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DirectorActorTransformKeyframe> NormalizeTransforms(
        IEnumerable<DirectorActorTransformKeyframe>? source)
    {
        List<(DirectorActorTransformKeyframe Frame, int SourceIndex)> candidates = new();
        if (source is null)
        {
            return new List<DirectorActorTransformKeyframe>();
        }

        int sourceIndex = 0;
        foreach (DirectorActorTransformKeyframe? input in source)
        {
            int currentIndex = sourceIndex++;
            if (candidates.Count >= MaximumTransformKeyframes)
            {
                break;
            }
            if (input is null)
            {
                continue;
            }

            DirectorActorTransformKeyframe frame = new()
            {
                TimeSeconds = NormalizeTime(input.TimeSeconds),
                InterpolationToNext = input.InterpolationToNext is DirectorActorTransformInterpolation interpolation
                    && Enum.IsDefined(interpolation)
                        ? interpolation
                        : null,
                X = NormalizeCoordinate(input.X),
                Y = NormalizeCoordinate(input.Y),
                Z = NormalizeCoordinate(input.Z),
                Dimension = input.Dimension,
                Yaw = NormalizeAngle(input.Yaw),
                Pitch = NormalizeAngle(input.Pitch),
                Roll = NormalizeAngle(input.Roll),
                HeadYaw = NormalizeAngle(input.HeadYaw),
                HeadPitch = NormalizeAngle(input.HeadPitch),
                BodyYaw = NormalizeAngle(input.BodyYaw)
            };
            candidates.Add((frame, currentIndex));
        }

        List<DirectorActorTransformKeyframe> frames = new(candidates.Count);
        foreach (DirectorActorTransformKeyframe frame in candidates
            .OrderBy(item => item.Frame.TimeSeconds)
            .ThenBy(item => item.SourceIndex)
            .Select(item => item.Frame))
        {
            if (frames.Count > 0
                && Math.Abs(frames[^1].TimeSeconds - frame.TimeSeconds) <= TimeEpsilon)
            {
                frames[^1] = frame;
            }
            else
            {
                frames.Add(frame);
            }
        }
        return frames;
    }

    private static List<DirectorActorCue> NormalizeCues(IEnumerable<DirectorActorCue>? source)
    {
        List<(DirectorActorCue Cue, int SourceIndex)> candidates = new();
        if (source is null)
        {
            return new List<DirectorActorCue>();
        }

        HashSet<Guid> usedIds = new();
        int sourceIndex = 0;
        foreach (DirectorActorCue? input in source)
        {
            int currentIndex = sourceIndex++;
            if (input is null || !Enum.IsDefined(input.Type) || candidates.Count >= MaximumCues)
            {
                continue;
            }

            DirectorActorCueType type = input.Type;
            List<string> animations = type == DirectorActorCueType.AnimationSet
                ? NormalizeAnimations(input.Animations)
                : new List<string>();
            if (type == DirectorActorCueType.AnimationSet && animations.Count == 0)
            {
                type = DirectorActorCueType.AnimationStop;
            }

            DirectorActorLookAtTarget? lookAt = type == DirectorActorCueType.LookAt
                ? NormalizeLookAt(input.LookAt)
                : null;
            if (type == DirectorActorCueType.LookAt && lookAt is null)
            {
                continue;
            }

            Guid id = input.Id;
            if (id == Guid.Empty || !usedIds.Add(id))
            {
                do
                {
                    id = Guid.NewGuid();
                }
                while (!usedIds.Add(id));
            }

            candidates.Add((new DirectorActorCue
            {
                Id = id,
                TimeSeconds = NormalizeTime(input.TimeSeconds),
                Order = input.Order,
                Type = type,
                Animations = animations,
                Visible = input.Visible,
                LookAt = lookAt
            }, currentIndex));
        }

        DirectorActorCue[] ordered = candidates
            .OrderBy(item => item.Cue.TimeSeconds)
            .ThenBy(item => item.Cue.Order >= 0 ? item.Cue.Order : item.SourceIndex)
            .ThenBy(item => item.SourceIndex)
            .Select(item => item.Cue)
            .ToArray();
        for (int index = 0; index < ordered.Length; index++)
        {
            ordered[index].Order = index;
        }
        return ordered.ToList();
    }

    private static DirectorActorLookAtTarget? NormalizeLookAt(DirectorActorLookAtTarget? source)
    {
        if (source is null)
        {
            return null;
        }

        return new DirectorActorLookAtTarget
        {
            X = NormalizeCoordinate(source.X),
            Y = NormalizeCoordinate(source.Y),
            Z = NormalizeCoordinate(source.Z),
            Dimension = source.Dimension
        };
    }

    private static string NormalizeName(string? value)
    {
        string name = string.IsNullOrWhiteSpace(value) ? "Actor Performance" : value.Trim();
        return name.Length <= MaximumNameLength ? name : name[..MaximumNameLength];
    }

    private static string NormalizeText(string? value) => value?.Trim() ?? string.Empty;

    private static double NormalizeTime(double value)
        => double.IsFinite(value)
            ? Math.Clamp(value, 0d, CameraPathTiming.MaxTimelineSeconds)
            : 0d;

    private static double NormalizeCoordinate(double value) => double.IsFinite(value) ? value : 0d;

    private static float NormalizeAngle(float value) => float.IsFinite(value) ? value : 0f;

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value, DateTimeOffset fallback)
        => value == default ? fallback : value.ToUniversalTime();
}

/// <summary>
/// Samples persistent actor state without mutating game or actor objects.
/// </summary>
public sealed class DirectorActorPerformanceEvaluator
{
    private const double TwoPi = Math.PI * 2d;
    private readonly DirectorActorPerformance performance;
    private readonly DirectorActorCue[] animationCues;
    private readonly DirectorActorCue[] visibilityCues;
    private readonly DirectorActorCue[] lookAtCues;

    public DirectorActorPerformanceEvaluator(DirectorActorPerformance performance)
    {
        ArgumentNullException.ThrowIfNull(performance);
        this.performance = DirectorActorPerformanceNormalizer.Normalize(performance);
        animationCues = this.performance.Cues
            .Where(cue => cue.Type is DirectorActorCueType.AnimationSet
                or DirectorActorCueType.AnimationStop)
            .ToArray();
        visibilityCues = this.performance.Cues
            .Where(cue => cue.Type == DirectorActorCueType.Visibility)
            .ToArray();
        lookAtCues = this.performance.Cues
            .Where(cue => cue.Type == DirectorActorCueType.LookAt)
            .ToArray();
    }

    public double DurationSeconds => performance.DurationSeconds;

    public DirectorActorPerformanceState Sample(double timeSeconds)
    {
        double time = NormalizeEvaluationTime(timeSeconds);
        DirectorActorCue? animation = LastCueAtOrBefore(animationCues, time);
        DirectorActorCue? visibility = LastCueAtOrBefore(visibilityCues, time);
        DirectorActorCue? lookAt = LastCueAtOrBefore(lookAtCues, time);

        return new DirectorActorPerformanceState
        {
            TimeSeconds = time,
            Transform = SampleTransform(time),
            Animations = animation?.Type == DirectorActorCueType.AnimationSet
                ? new List<string>(animation.Animations)
                : new List<string>(),
            Visible = visibility?.Visible ?? true,
            LookAt = lookAt?.LookAt?.DeepClone()
        };
    }

    public DirectorActorPerformanceCursor CreateCursor() => new(this);

    internal IReadOnlyList<DirectorActorCue> CuesThrough(double timeSeconds)
    {
        double time = NormalizeEvaluationTime(timeSeconds);
        int end = UpperBound(performance.Cues, time);
        return performance.Cues
            .Take(end)
            .Select(cue => cue.DeepClone())
            .ToArray();
    }

    internal IReadOnlyList<DirectorActorCue> CuesBetween(double previousSeconds, double timeSeconds)
    {
        double previous = NormalizeEvaluationTime(previousSeconds);
        double time = NormalizeEvaluationTime(timeSeconds);
        if (time <= previous)
        {
            return Array.Empty<DirectorActorCue>();
        }

        int start = UpperBound(performance.Cues, previous);
        int end = UpperBound(performance.Cues, time);
        return performance.Cues
            .Skip(start)
            .Take(end - start)
            .Select(cue => cue.DeepClone())
            .ToArray();
    }

    internal double NormalizeEvaluationTime(double value)
    {
        double finite = double.IsFinite(value) ? Math.Max(0d, value) : 0d;
        return Math.Min(finite, DurationSeconds);
    }

    private DirectorActorTransformKeyframe? SampleTransform(double time)
    {
        IReadOnlyList<DirectorActorTransformKeyframe> frames = performance.TransformKeyframes;
        if (frames.Count == 0)
        {
            return null;
        }
        if (frames.Count == 1 || time <= frames[0].TimeSeconds)
        {
            return AtTime(frames[0], time);
        }

        int lastIndex = frames.Count - 1;
        if (time >= frames[lastIndex].TimeSeconds)
        {
            return AtTime(frames[lastIndex], time);
        }

        int low = 0;
        int high = lastIndex;
        while (low + 1 < high)
        {
            int middle = (low + high) / 2;
            if (frames[middle].TimeSeconds <= time)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        DirectorActorTransformKeyframe from = frames[low];
        DirectorActorTransformKeyframe to = frames[low + 1];
        if (from.Dimension != to.Dimension)
        {
            return AtTime(from, time);
        }

        double duration = to.TimeSeconds - from.TimeSeconds;
        double amount = duration <= DirectorActorPerformanceNormalizer.TimeEpsilon
            ? 1d
            : Math.Clamp((time - from.TimeSeconds) / duration, 0d, 1d);
        DirectorActorTransformInterpolation interpolation = from.InterpolationToNext
            ?? performance.Interpolation;
        if (interpolation == DirectorActorTransformInterpolation.EaseInOut)
        {
            amount = amount * amount * (3d - 2d * amount);
        }

        return new DirectorActorTransformKeyframe
        {
            TimeSeconds = time,
            X = Lerp(from.X, to.X, amount),
            Y = Lerp(from.Y, to.Y, amount),
            Z = Lerp(from.Z, to.Z, amount),
            Dimension = from.Dimension,
            Yaw = (float)LerpAngle(from.Yaw, to.Yaw, amount),
            Pitch = (float)LerpAngle(from.Pitch, to.Pitch, amount),
            Roll = (float)LerpAngle(from.Roll, to.Roll, amount),
            HeadYaw = (float)LerpAngle(from.HeadYaw, to.HeadYaw, amount),
            HeadPitch = (float)LerpAngle(from.HeadPitch, to.HeadPitch, amount),
            BodyYaw = (float)LerpAngle(from.BodyYaw, to.BodyYaw, amount)
        };
    }

    private static DirectorActorTransformKeyframe AtTime(
        DirectorActorTransformKeyframe source,
        double time)
    {
        DirectorActorTransformKeyframe result = source.DeepClone();
        result.TimeSeconds = time;
        result.InterpolationToNext = null;
        return result;
    }

    private static double Lerp(double from, double to, double amount)
        => from + (to - from) * amount;

    private static double LerpAngle(double from, double to, double amount)
        => NormalizeAngle(from + NormalizeAngle(to - from) * amount);

    private static double NormalizeAngle(double angle)
    {
        double wrapped = angle % TwoPi;
        if (wrapped > Math.PI)
        {
            wrapped -= TwoPi;
        }
        else if (wrapped < -Math.PI)
        {
            wrapped += TwoPi;
        }
        return wrapped;
    }

    private static DirectorActorCue? LastCueAtOrBefore(
        IReadOnlyList<DirectorActorCue> cues,
        double time)
    {
        int index = UpperBound(cues, time) - 1;
        return index >= 0 ? cues[index] : null;
    }

    private static int UpperBound(IReadOnlyList<DirectorActorCue> cues, double time)
    {
        int low = 0;
        int high = cues.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (cues[middle].TimeSeconds <= time)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }
}

/// <summary>
/// Stateful cue cursor. Seek and rewind only establish a new baseline; cues are
/// emitted once when advancing across their timestamp in (previous, current].
/// </summary>
public sealed class DirectorActorPerformanceCursor
{
    private readonly DirectorActorPerformanceEvaluator evaluator;
    private double previousSeconds;
    private bool initialized;

    internal DirectorActorPerformanceCursor(DirectorActorPerformanceEvaluator evaluator)
    {
        this.evaluator = evaluator;
    }

    public bool IsInitialized => initialized;

    public double TimeSeconds => previousSeconds;

    public DirectorActorPerformanceEvaluation Seek(double timeSeconds)
    {
        previousSeconds = evaluator.NormalizeEvaluationTime(timeSeconds);
        initialized = true;
        return Evaluation(previousSeconds, Array.Empty<DirectorActorCue>());
    }

    public DirectorActorPerformanceEvaluation Advance(double timeSeconds)
    {
        double time = evaluator.NormalizeEvaluationTime(timeSeconds);
        IReadOnlyList<DirectorActorCue> crossed = !initialized
            ? evaluator.CuesThrough(time)
            : time > previousSeconds
                ? evaluator.CuesBetween(previousSeconds, time)
                : Array.Empty<DirectorActorCue>();
        previousSeconds = time;
        initialized = true;
        return Evaluation(time, crossed);
    }

    public void Reset()
    {
        previousSeconds = 0d;
        initialized = false;
    }

    private DirectorActorPerformanceEvaluation Evaluation(
        double time,
        IReadOnlyList<DirectorActorCue> crossed)
    {
        return new DirectorActorPerformanceEvaluation
        {
            State = evaluator.Sample(time),
            CrossedCues = crossed
        };
    }
}

public static class DirectorActorPerformanceImporter
{
    public static bool TryCreate(
        DirectorReplayActorTrack? actor,
        string? requestedName,
        out DirectorActorPerformance performance,
        out string error,
        Guid sourceReplayId = default)
    {
        performance = new DirectorActorPerformance();
        error = string.Empty;
        if (actor?.Samples is not { Count: > 0 })
        {
            error = "actor-performance-no-samples";
            return false;
        }

        List<DirectorReplayActorSample> samples = actor.Samples
            .Where(IsFinite)
            .OrderBy(sample => sample.TimeSeconds)
            .GroupBy(sample => sample.TimeSeconds)
            .Select(group => group.Last())
            .ToList();
        if (samples.Count == 0)
        {
            error = "actor-performance-no-samples";
            return false;
        }

        double offset = samples[0].TimeSeconds;
        List<DirectorActorCue> cues = new();
        List<string> previousAnimations = new();
        bool first = true;
        foreach (DirectorReplayActorSample sample in samples)
        {
            List<string> animations = DirectorActorPerformanceNormalizer.NormalizeAnimations(sample.Animations);
            if (first || !animations.SequenceEqual(previousAnimations, StringComparer.OrdinalIgnoreCase))
            {
                if (animations.Count > 0)
                {
                    cues.Add(new DirectorActorCue
                    {
                        Id = Guid.NewGuid(),
                        TimeSeconds = sample.TimeSeconds - offset,
                        Type = DirectorActorCueType.AnimationSet,
                        Animations = animations
                    });
                }
                else if (!first && previousAnimations.Count > 0)
                {
                    cues.Add(new DirectorActorCue
                    {
                        Id = Guid.NewGuid(),
                        TimeSeconds = sample.TimeSeconds - offset,
                        Type = DirectorActorCueType.AnimationStop
                    });
                }
            }

            previousAnimations = animations;
            first = false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        performance = DirectorActorPerformanceNormalizer.Normalize(new DirectorActorPerformance
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(requestedName)
                ? DefaultName(actor)
                : requestedName.Trim(),
            Interpolation = DirectorActorTransformInterpolation.Linear,
            SourceReplayId = sourceReplayId,
            SourceActorKey = actor.ActorKey,
            SourceEntityId = actor.EntityId,
            SourcePlayerUid = actor.PlayerUid,
            SourceEntityCode = actor.EntityCode,
            ReplayTimeOffsetSeconds = offset,
            InitialState = actor.InitialState,
            TransformKeyframes = samples.Select(sample => new DirectorActorTransformKeyframe
            {
                TimeSeconds = sample.TimeSeconds - offset,
                X = sample.X,
                Y = sample.Y,
                Z = sample.Z,
                Dimension = sample.Dimension,
                Yaw = sample.Yaw,
                Pitch = sample.Pitch,
                Roll = sample.Roll,
                HeadYaw = sample.HeadYaw,
                HeadPitch = sample.HeadPitch,
                BodyYaw = sample.BodyYaw
            }).ToList(),
            Cues = cues,
            CreatedUtc = now,
            ModifiedUtc = now
        });
        return true;
    }

    private static bool IsFinite(DirectorReplayActorSample sample)
        => double.IsFinite(sample.TimeSeconds)
            && sample.TimeSeconds >= 0d
            && double.IsFinite(sample.X)
            && double.IsFinite(sample.Y)
            && double.IsFinite(sample.Z)
            && float.IsFinite(sample.Yaw)
            && float.IsFinite(sample.Pitch)
            && float.IsFinite(sample.Roll)
            && float.IsFinite(sample.HeadYaw)
            && float.IsFinite(sample.HeadPitch)
            && float.IsFinite(sample.BodyYaw);

    private static string DefaultName(DirectorReplayActorTrack actor)
    {
        string actorName = string.IsNullOrWhiteSpace(actor.DisplayName)
            ? string.IsNullOrWhiteSpace(actor.PlayerUid) ? actor.ActorKey : actor.PlayerUid
            : actor.DisplayName;
        return string.IsNullOrWhiteSpace(actorName)
            ? "Actor Performance"
            : actorName.Trim() + " Performance";
    }
}
