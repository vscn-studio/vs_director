using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VSDirector.Data;

public enum DirectorActionCategory
{
    Player,
    Npc,
    Creature,
    Expression,
    SpecialEffect
}

public enum DirectorActionLayerKind
{
    FullBody,
    UpperBody,
    LowerBody,
    Head,
    Hands,
    Expression,
    Additive
}

public enum DirectorActionBlendMode
{
    Override,
    Additive
}

public enum DirectorActionLoopMode
{
    Once,
    Loop,
    PingPong
}

public enum DirectorActionEventType
{
    Marker,
    Sound,
    Particle,
    HeldItem,
    Environment
}

public enum DirectorActionTriggerType
{
    Manual,
    Attack,
    Mining,
    Swimming,
    Sprinting,
    Jumping,
    Hurt,
    Custom
}

public sealed class DirectorActionBoneTransform
{
    public float TranslationX { get; set; }
    public float TranslationY { get; set; }
    public float TranslationZ { get; set; }
    public float RotationX { get; set; }
    public float RotationY { get; set; }
    public float RotationZ { get; set; }
    public float ScaleX { get; set; } = 1f;
    public float ScaleY { get; set; } = 1f;
    public float ScaleZ { get; set; } = 1f;

    public DirectorActionBoneTransform DeepClone() => (DirectorActionBoneTransform)MemberwiseClone();
}

public sealed class DirectorActionFrame
{
    public double TimeSeconds { get; set; }

    /// <summary>
    /// Reuses the replay pose representation so captured animation progress,
    /// posture, head direction and hand items remain frame-exact.
    /// </summary>
    public DirectorReplayActorSample State { get; set; } = new();

    public Dictionary<string, DirectorActionBoneTransform> BoneOverrides { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, float> Expressions { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> StringProperties { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public DirectorActionFrame DeepClone()
        => new()
        {
            TimeSeconds = TimeSeconds,
            State = State?.DeepClone() ?? new DirectorReplayActorSample(),
            BoneOverrides = BoneOverrides is null
                ? new Dictionary<string, DirectorActionBoneTransform>(StringComparer.OrdinalIgnoreCase)
                : BoneOverrides
                    .Where(item => item.Value is not null)
                    .ToDictionary(item => item.Key, item => item.Value.DeepClone(), StringComparer.OrdinalIgnoreCase),
            Expressions = Expressions is null
                ? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, float>(Expressions, StringComparer.OrdinalIgnoreCase),
            StringProperties = StringProperties is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringProperties, StringComparer.OrdinalIgnoreCase)
        };
}

public sealed class DirectorActionLayer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DirectorActionLayerKind Kind { get; set; }
    public DirectorActionBlendMode BlendMode { get; set; }
    public bool Enabled { get; set; } = true;
    public float Weight { get; set; } = 1f;
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
    public int Order { get; set; }

    /// <summary>Optional per-shape-element weights. Empty uses the standard layer mask.</summary>
    public Dictionary<string, float> ElementWeights { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public List<DirectorActionFrame> Frames { get; set; } = new();

    [JsonIgnore]
    public double DurationSeconds => Frames?.Select(frame => frame?.TimeSeconds ?? 0d).DefaultIfEmpty().Max() ?? 0d;

    public DirectorActionLayer DeepClone()
        => new()
        {
            Id = Id,
            Name = Name,
            Kind = Kind,
            BlendMode = BlendMode,
            Enabled = Enabled,
            Weight = Weight,
            FadeInSeconds = FadeInSeconds,
            FadeOutSeconds = FadeOutSeconds,
            Order = Order,
            ElementWeights = ElementWeights is null
                ? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, float>(ElementWeights, StringComparer.OrdinalIgnoreCase),
            Frames = Frames is null
                ? new List<DirectorActionFrame>()
                : Frames.Where(frame => frame is not null).Select(frame => frame.DeepClone()).ToList()
        };
}

public sealed class DirectorActionEvent
{
    public Guid Id { get; set; }
    public double TimeSeconds { get; set; }
    public DirectorActionEventType Type { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
    public bool Spatial { get; set; } = true;
    public float Volume { get; set; } = 1f;
    public float Pitch { get; set; } = 1f;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double OffsetZ { get; set; }

    public DirectorActionEvent DeepClone() => (DirectorActionEvent)MemberwiseClone();
}

public sealed class DirectorActionTrigger
{
    public Guid Id { get; set; }
    public DirectorActionTriggerType Type { get; set; }
    public string EventCode { get; set; } = string.Empty;
    public double DelaySeconds { get; set; }
    public bool RestartIfPlaying { get; set; } = true;

    public DirectorActionTrigger DeepClone() => (DirectorActionTrigger)MemberwiseClone();
}

public sealed class DirectorActionTransition
{
    public Guid Id { get; set; }
    public string FromTag { get; set; } = string.Empty;
    public string ToTag { get; set; } = string.Empty;
    public double DurationSeconds { get; set; } = 0.2d;

    public DirectorActionTransition DeepClone() => (DirectorActionTransition)MemberwiseClone();
}

public sealed class DirectorActionAsset
{
    public const int CurrentFormatVersion = 2;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public DirectorActionCategory Category { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool Favorite { get; set; }
    public string Thumbnail { get; set; } = string.Empty;
    public List<string> CompatibleEntityCodes { get; set; } = new();
    public List<string> CompatibleModelCodes { get; set; } = new();
    public string PreviewModelCode { get; set; } = string.Empty;
    public string PreviewEntityCode { get; set; } = string.Empty;
    public string PreviewEntityName { get; set; } = string.Empty;
    public byte[] PreviewEntityState { get; set; } = Array.Empty<byte>();
    public DirectorActionLoopMode LoopMode { get; set; }
    public int LoopCount { get; set; } = 1;
    public double PlaybackSpeed { get; set; } = 1d;
    public bool IncludeRootMotion { get; set; }
    public Guid SourceReplayId { get; set; }
    public string SourceActorKey { get; set; } = string.Empty;
    public string SourceModId { get; set; } = string.Empty;
    public string SourceCode { get; set; } = string.Empty;
    public byte[] InitialEntityState { get; set; } = Array.Empty<byte>();
    public List<DirectorActionLayer> Layers { get; set; } = new();
    public List<DirectorActionEvent> Events { get; set; } = new();
    public List<DirectorActionTrigger> Triggers { get; set; } = new();
    public List<DirectorActionTransition> Transitions { get; set; } = new();
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }

    [JsonIgnore]
    public double DurationSeconds => Math.Max(
        Layers?.Select(layer => layer?.DurationSeconds ?? 0d).DefaultIfEmpty().Max() ?? 0d,
        Events?.Select(item => item?.TimeSeconds ?? 0d).DefaultIfEmpty().Max() ?? 0d);

    [JsonIgnore]
    public int FrameCount => Layers?.Sum(layer => layer?.Frames?.Count ?? 0) ?? 0;

    public DirectorActionAsset DeepClone()
        => new()
        {
            FormatVersion = FormatVersion,
            Id = Id,
            Name = Name,
            Description = Description,
            Author = Author,
            Version = Version,
            Category = Category,
            Tags = Tags is null ? new List<string>() : new List<string>(Tags),
            Favorite = Favorite,
            Thumbnail = Thumbnail,
            CompatibleEntityCodes = CompatibleEntityCodes is null
                ? new List<string>()
                : new List<string>(CompatibleEntityCodes),
            CompatibleModelCodes = CompatibleModelCodes is null
                ? new List<string>()
                : new List<string>(CompatibleModelCodes),
            PreviewModelCode = PreviewModelCode,
            PreviewEntityCode = PreviewEntityCode,
            PreviewEntityName = PreviewEntityName,
            PreviewEntityState = PreviewEntityState is null
                ? Array.Empty<byte>()
                : (byte[])PreviewEntityState.Clone(),
            LoopMode = LoopMode,
            LoopCount = LoopCount,
            PlaybackSpeed = PlaybackSpeed,
            IncludeRootMotion = IncludeRootMotion,
            SourceReplayId = SourceReplayId,
            SourceActorKey = SourceActorKey,
            SourceModId = SourceModId,
            SourceCode = SourceCode,
            InitialEntityState = InitialEntityState is null
                ? Array.Empty<byte>()
                : (byte[])InitialEntityState.Clone(),
            Layers = Layers is null
                ? new List<DirectorActionLayer>()
                : Layers.Where(layer => layer is not null).Select(layer => layer.DeepClone()).ToList(),
            Events = Events is null
                ? new List<DirectorActionEvent>()
                : Events.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            Triggers = Triggers is null
                ? new List<DirectorActionTrigger>()
                : Triggers.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            Transitions = Transitions is null
                ? new List<DirectorActionTransition>()
                : Transitions.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            CreatedUtc = CreatedUtc,
            ModifiedUtc = ModifiedUtc
        };
}

public static class DirectorActionNormalizer
{
    public const int MaximumLayers = 32;
    public const int MaximumFramesPerLayer = 250_000;
    public const int MaximumEvents = 100_000;
    public const double TimeEpsilon = 0.000001d;

    public static DirectorActionAsset Normalize(DirectorActionAsset? source)
    {
        source ??= new DirectorActionAsset();
        if (source.FormatVersion > DirectorActionAsset.CurrentFormatVersion)
        {
            throw new NotSupportedException($"Action format {source.FormatVersion} is newer than supported format {DirectorActionAsset.CurrentFormatVersion}.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset created = source.CreatedUtc == default ? now : source.CreatedUtc.ToUniversalTime();
        DateTimeOffset modified = source.ModifiedUtc == default ? created : source.ModifiedUtc.ToUniversalTime();
        HashSet<Guid> layerIds = new();
        List<DirectorActionLayer> layers = new();
        foreach (DirectorActionLayer? input in source.Layers ?? new List<DirectorActionLayer>())
        {
            if (input is null || layers.Count >= MaximumLayers)
            {
                continue;
            }
            Guid id = ReserveId(input.Id, layerIds);
            List<DirectorActionFrame> frames = NormalizeFrames(input.Frames);
            layers.Add(new DirectorActionLayer
            {
                Id = id,
                Name = Limited(input.Name, $"Layer {layers.Count + 1}", 96),
                Kind = Enum.IsDefined(input.Kind) ? input.Kind : DirectorActionLayerKind.FullBody,
                BlendMode = Enum.IsDefined(input.BlendMode) ? input.BlendMode : DirectorActionBlendMode.Override,
                Enabled = input.Enabled,
                Weight = Math.Clamp(Finite(input.Weight, 1f), 0f, 100f),
                FadeInSeconds = Time(input.FadeInSeconds),
                FadeOutSeconds = Time(input.FadeOutSeconds),
                Order = Math.Clamp(input.Order, -1024, 1024),
                ElementWeights = NormalizeWeights(input.ElementWeights),
                Frames = frames
            });
        }

        return new DirectorActionAsset
        {
            FormatVersion = DirectorActionAsset.CurrentFormatVersion,
            Id = source.Id == Guid.Empty ? Guid.NewGuid() : source.Id,
            Name = Limited(source.Name, "Action", 96),
            Description = Limited(source.Description, string.Empty, 2048),
            Author = Limited(source.Author, string.Empty, 128),
            Version = Limited(source.Version, "1.0.0", 32),
            Category = Enum.IsDefined(source.Category) ? source.Category : DirectorActionCategory.Player,
            Tags = NormalizeStrings(source.Tags, 64, 64),
            Favorite = source.Favorite,
            Thumbnail = Limited(source.Thumbnail, string.Empty, 512),
            CompatibleEntityCodes = NormalizeStrings(source.CompatibleEntityCodes, 128, 128),
            CompatibleModelCodes = NormalizeStrings(source.CompatibleModelCodes, 128, 128),
            PreviewModelCode = Limited(source.PreviewModelCode, string.Empty, 128),
            PreviewEntityCode = Limited(source.PreviewEntityCode, string.Empty, 256),
            PreviewEntityName = Limited(source.PreviewEntityName, string.Empty, 128),
            PreviewEntityState = source.PreviewEntityState is null
                ? Array.Empty<byte>()
                : (byte[])source.PreviewEntityState.Clone(),
            LoopMode = Enum.IsDefined(source.LoopMode) ? source.LoopMode : DirectorActionLoopMode.Once,
            LoopCount = Math.Clamp(source.LoopCount, 0, 100_000),
            PlaybackSpeed = Math.Clamp(Finite(source.PlaybackSpeed, 1d), 0.01d, 64d),
            IncludeRootMotion = source.IncludeRootMotion,
            SourceReplayId = source.SourceReplayId,
            SourceActorKey = Limited(source.SourceActorKey, string.Empty, 256),
            SourceModId = Limited(source.SourceModId, string.Empty, 128),
            SourceCode = Limited(source.SourceCode, string.Empty, 128),
            InitialEntityState = source.InitialEntityState is null
                ? Array.Empty<byte>()
                : (byte[])source.InitialEntityState.Clone(),
            Layers = layers.OrderBy(layer => layer.Order).ThenBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Events = NormalizeEvents(source.Events),
            Triggers = NormalizeTriggers(source.Triggers),
            Transitions = NormalizeTransitions(source.Transitions),
            CreatedUtc = created,
            ModifiedUtc = modified < created ? created : modified
        };
    }

    private static List<DirectorActionFrame> NormalizeFrames(IEnumerable<DirectorActionFrame>? source)
    {
        List<DirectorActionFrame> candidates = (source ?? Array.Empty<DirectorActionFrame>())
            .Where(frame => frame is not null)
            .Take(MaximumFramesPerLayer)
            .Select(frame =>
            {
                DirectorActionFrame clone = frame.DeepClone();
                clone.TimeSeconds = Time(frame.TimeSeconds);
                clone.State.TimeSeconds = clone.TimeSeconds;
                clone.BoneOverrides = NormalizeBones(clone.BoneOverrides);
                clone.Expressions = NormalizeWeights(clone.Expressions);
                clone.StringProperties = clone.StringProperties
                    .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                    .Take(128)
                    .ToDictionary(
                        item => item.Key.Trim(),
                        item => item.Value?.Trim() ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase);
                return clone;
            })
            .OrderBy(frame => frame.TimeSeconds)
            .ToList();

        List<DirectorActionFrame> deduplicated = new();
        foreach (DirectorActionFrame frame in candidates)
        {
            if (deduplicated.Count > 0
                && Math.Abs(deduplicated[^1].TimeSeconds - frame.TimeSeconds) <= TimeEpsilon)
            {
                deduplicated[^1] = frame;
            }
            else
            {
                deduplicated.Add(frame);
            }
        }
        if (deduplicated.Count == 0)
        {
            return deduplicated;
        }

        DirectorReplayActorTrack track = new()
        {
            ActorKey = "action-normalize",
            EntityCode = "player",
            Samples = deduplicated.Select(frame => frame.State.DeepClone()).ToList()
        };
        DirectorReplayDocument document = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            Actors = new List<DirectorReplayActorTrack> { track }
        });
        IReadOnlyList<DirectorReplayActorSample> normalized = document.Actors[0].Samples;
        for (int index = 0; index < Math.Min(deduplicated.Count, normalized.Count); index++)
        {
            deduplicated[index].State = normalized[index];
            deduplicated[index].TimeSeconds = normalized[index].TimeSeconds;
        }
        return deduplicated;
    }

    private static Dictionary<string, DirectorActionBoneTransform> NormalizeBones(
        IReadOnlyDictionary<string, DirectorActionBoneTransform>? source)
    {
        Dictionary<string, DirectorActionBoneTransform> result = new(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }
        foreach ((string key, DirectorActionBoneTransform? value) in source.Take(256))
        {
            string name = Limited(key, string.Empty, 128);
            if (name.Length == 0 || value is null)
            {
                continue;
            }
            DirectorActionBoneTransform clone = value.DeepClone();
            clone.TranslationX = Finite(clone.TranslationX);
            clone.TranslationY = Finite(clone.TranslationY);
            clone.TranslationZ = Finite(clone.TranslationZ);
            clone.RotationX = Finite(clone.RotationX);
            clone.RotationY = Finite(clone.RotationY);
            clone.RotationZ = Finite(clone.RotationZ);
            clone.ScaleX = Math.Clamp(Finite(clone.ScaleX, 1f), 0.01f, 100f);
            clone.ScaleY = Math.Clamp(Finite(clone.ScaleY, 1f), 0.01f, 100f);
            clone.ScaleZ = Math.Clamp(Finite(clone.ScaleZ, 1f), 0.01f, 100f);
            result[name] = clone;
        }
        return result;
    }

    private static Dictionary<string, float> NormalizeWeights(IReadOnlyDictionary<string, float>? source)
    {
        Dictionary<string, float> result = new(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }
        foreach ((string key, float value) in source.Take(256))
        {
            string name = Limited(key, string.Empty, 128);
            if (name.Length > 0)
            {
                result[name] = Math.Clamp(Finite(value), 0f, 1000f);
            }
        }
        return result;
    }

    private static List<DirectorActionEvent> NormalizeEvents(IEnumerable<DirectorActionEvent>? source)
    {
        HashSet<Guid> ids = new();
        return (source ?? Array.Empty<DirectorActionEvent>())
            .Where(item => item is not null && Enum.IsDefined(item.Type))
            .Take(MaximumEvents)
            .Select(item => new DirectorActionEvent
            {
                Id = ReserveId(item.Id, ids),
                TimeSeconds = Time(item.TimeSeconds),
                Type = item.Type,
                Code = Limited(item.Code, string.Empty, 256),
                Data = Limited(item.Data, string.Empty, 4096),
                Spatial = item.Spatial,
                Volume = Math.Clamp(Finite(item.Volume, 1f), 0f, 16f),
                Pitch = Math.Clamp(Finite(item.Pitch, 1f), 0.01f, 8f),
                OffsetX = Finite(item.OffsetX),
                OffsetY = Finite(item.OffsetY),
                OffsetZ = Finite(item.OffsetZ)
            })
            .OrderBy(item => item.TimeSeconds)
            .ThenBy(item => item.Id)
            .ToList();
    }

    private static List<DirectorActionTrigger> NormalizeTriggers(IEnumerable<DirectorActionTrigger>? source)
    {
        HashSet<Guid> ids = new();
        return (source ?? Array.Empty<DirectorActionTrigger>())
            .Where(item => item is not null && Enum.IsDefined(item.Type))
            .Take(256)
            .Select(item => new DirectorActionTrigger
            {
                Id = ReserveId(item.Id, ids),
                Type = item.Type,
                EventCode = Limited(item.EventCode, string.Empty, 128),
                DelaySeconds = Time(item.DelaySeconds),
                RestartIfPlaying = item.RestartIfPlaying
            })
            .ToList();
    }

    private static List<DirectorActionTransition> NormalizeTransitions(IEnumerable<DirectorActionTransition>? source)
    {
        HashSet<Guid> ids = new();
        return (source ?? Array.Empty<DirectorActionTransition>())
            .Where(item => item is not null)
            .Take(256)
            .Select(item => new DirectorActionTransition
            {
                Id = ReserveId(item.Id, ids),
                FromTag = Limited(item.FromTag, string.Empty, 64),
                ToTag = Limited(item.ToTag, string.Empty, 64),
                DurationSeconds = Math.Clamp(Time(item.DurationSeconds), 0d, 30d)
            })
            .ToList();
    }

    private static Guid ReserveId(Guid requested, ISet<Guid> used)
    {
        Guid id = requested;
        if (id == Guid.Empty || !used.Add(id))
        {
            do { id = Guid.NewGuid(); } while (!used.Add(id));
        }
        return id;
    }

    private static List<string> NormalizeStrings(IEnumerable<string>? source, int count, int length)
        => (source ?? Array.Empty<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => Limited(item, string.Empty, length))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();

    private static string Limited(string? value, string fallback, int length)
    {
        string result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return result.Length <= length ? result : result[..length];
    }

    private static double Time(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0d, CameraPathTiming.MaxTimelineSeconds) : 0d;
    private static double Finite(double value, double fallback = 0d) => double.IsFinite(value) ? value : fallback;
    private static float Finite(float value, float fallback = 0f) => float.IsFinite(value) ? value : fallback;
}

public sealed class DirectorActionLayerEvaluation
{
    public required DirectorActionLayer Layer { get; init; }
    public required DirectorReplayActorSample State { get; init; }
    public float EffectiveWeight { get; init; }
    public IReadOnlyDictionary<string, DirectorActionBoneTransform> BoneOverrides { get; init; }
        = new Dictionary<string, DirectorActionBoneTransform>();
    public IReadOnlyDictionary<string, float> Expressions { get; init; }
        = new Dictionary<string, float>();
    public IReadOnlyDictionary<string, string> StringProperties { get; init; }
        = new Dictionary<string, string>();
}

public sealed class DirectorActionEvaluation
{
    public double TimelineSeconds { get; init; }
    public double SourceSeconds { get; init; }
    public bool Completed { get; init; }
    public IReadOnlyList<DirectorActionLayerEvaluation> Layers { get; init; }
        = Array.Empty<DirectorActionLayerEvaluation>();
    public IReadOnlyList<DirectorActionEvent> CrossedEvents { get; init; }
        = Array.Empty<DirectorActionEvent>();
}

public sealed class DirectorActionEvaluator
{
    private readonly DirectorActionAsset action;
    private readonly Dictionary<Guid, DirectorReplayActorTrack> tracks = new();

    public DirectorActionEvaluator(DirectorActionAsset action)
    {
        ArgumentNullException.ThrowIfNull(action);
        this.action = DirectorActionNormalizer.Normalize(action);
        foreach (DirectorActionLayer layer in this.action.Layers)
        {
            tracks[layer.Id] = new DirectorReplayActorTrack
            {
                ActorKey = "action:" + layer.Id.ToString("N"),
                EntityCode = this.action.CompatibleEntityCodes.FirstOrDefault() ?? string.Empty,
                Samples = layer.Frames.Select(frame => frame.State.DeepClone()).ToList()
            };
        }
    }

    public double DurationSeconds => action.DurationSeconds;

    public DirectorActionEvaluation Sample(double timelineSeconds)
    {
        (double source, bool completed) = ResolveTime(timelineSeconds);
        List<DirectorActionLayerEvaluation> layers = new();
        foreach (DirectorActionLayer layer in action.Layers.Where(layer => layer.Enabled))
        {
            if (!tracks.TryGetValue(layer.Id, out DirectorReplayActorTrack? track)
                || !DirectorReplayMath.TrySample(track, source, out DirectorReplayActorSample state))
            {
                continue;
            }

            float fade = 1f;
            if (layer.FadeInSeconds > DirectorActionNormalizer.TimeEpsilon)
            {
                fade = Math.Min(fade, (float)Math.Clamp(source / layer.FadeInSeconds, 0d, 1d));
            }
            if (layer.FadeOutSeconds > DirectorActionNormalizer.TimeEpsilon)
            {
                fade = Math.Min(fade, (float)Math.Clamp(
                    (DurationSeconds - source) / layer.FadeOutSeconds,
                    0d,
                    1d));
            }
            layers.Add(new DirectorActionLayerEvaluation
            {
                Layer = layer.DeepClone(),
                State = state,
                EffectiveWeight = layer.Weight * fade,
                BoneOverrides = FrameAtOrBefore(layer, source)?.BoneOverrides
                    .ToDictionary(item => item.Key, item => item.Value.DeepClone(), StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, DirectorActionBoneTransform>(StringComparer.OrdinalIgnoreCase),
                Expressions = FrameAtOrBefore(layer, source) is { } expressionFrame
                    ? new Dictionary<string, float>(expressionFrame.Expressions, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase),
                StringProperties = FrameAtOrBefore(layer, source) is { } propertyFrame
                    ? new Dictionary<string, string>(propertyFrame.StringProperties, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            });
        }
        return new DirectorActionEvaluation
        {
            TimelineSeconds = Math.Max(0d, double.IsFinite(timelineSeconds) ? timelineSeconds : 0d),
            SourceSeconds = source,
            Completed = completed,
            Layers = layers
        };
    }

    public DirectorActionCursor CreateCursor() => new(this, action.Events);

    private static DirectorActionFrame? FrameAtOrBefore(DirectorActionLayer layer, double time)
        => layer.Frames.LastOrDefault(frame => frame.TimeSeconds <= time + DirectorActionNormalizer.TimeEpsilon)
            ?? layer.Frames.FirstOrDefault();

    internal (double Source, bool Completed) ResolveTime(double timelineSeconds)
    {
        double duration = DurationSeconds;
        if (duration <= DirectorActionNormalizer.TimeEpsilon)
        {
            return (0d, true);
        }
        double scaled = Math.Max(0d, double.IsFinite(timelineSeconds) ? timelineSeconds : 0d) * action.PlaybackSpeed;
        return action.LoopMode switch
        {
            DirectorActionLoopMode.Loop => ResolveLoop(scaled, duration, pingPong: false),
            DirectorActionLoopMode.PingPong => ResolveLoop(scaled, duration, pingPong: true),
            _ => (Math.Min(scaled, duration), scaled >= duration - DirectorActionNormalizer.TimeEpsilon)
        };
    }

    private (double Source, bool Completed) ResolveLoop(double scaled, double duration, bool pingPong)
    {
        double cycle = pingPong ? duration * 2d : duration;
        int limit = action.LoopCount;
        bool completed = limit > 0 && scaled >= cycle * limit - DirectorActionNormalizer.TimeEpsilon;
        if (completed)
        {
            return (pingPong ? 0d : duration, true);
        }
        double local = scaled % cycle;
        return (pingPong && local > duration ? cycle - local : local, false);
    }
}

public static class DirectorActionCardPlayback
{
    public static DirectorActionAsset Configure(
        DirectorActionAsset action,
        DirectorActionLoopMode loopMode)
    {
        ArgumentNullException.ThrowIfNull(action);
        DirectorActionAsset configured = action.DeepClone();
        configured.LoopMode = Enum.IsDefined(loopMode) ? loopMode : DirectorActionLoopMode.Once;
        if (configured.LoopMode is DirectorActionLoopMode.Loop or DirectorActionLoopMode.PingPong)
        {
            // The workflow card duration, not the asset's authoring loop count,
            // controls how long a scheduled action remains active.
            configured.LoopCount = 0;
        }
        return configured;
    }
}

public sealed class DirectorActionCursor
{
    private readonly DirectorActionEvaluator evaluator;
    private readonly DirectorActionEvent[] events;
    private double previousTimeline;
    private double previousSource;
    private bool initialized;

    internal DirectorActionCursor(DirectorActionEvaluator evaluator, IEnumerable<DirectorActionEvent> events)
    {
        this.evaluator = evaluator;
        this.events = events.OrderBy(item => item.TimeSeconds).ToArray();
    }

    public DirectorActionEvaluation Seek(double timelineSeconds)
    {
        DirectorActionEvaluation evaluation = evaluator.Sample(timelineSeconds);
        previousTimeline = evaluation.TimelineSeconds;
        previousSource = evaluation.SourceSeconds;
        initialized = true;
        return evaluation;
    }

    public DirectorActionEvaluation Advance(double timelineSeconds)
    {
        DirectorActionEvaluation sampled = evaluator.Sample(timelineSeconds);
        IReadOnlyList<DirectorActionEvent> crossed = Array.Empty<DirectorActionEvent>();
        if (!initialized)
        {
            crossed = events.Where(item => item.TimeSeconds <= sampled.SourceSeconds + DirectorActionNormalizer.TimeEpsilon)
                .Select(item => item.DeepClone()).ToArray();
        }
        else if (sampled.TimelineSeconds >= previousTimeline)
        {
            crossed = sampled.SourceSeconds >= previousSource
                ? events.Where(item => item.TimeSeconds > previousSource + DirectorActionNormalizer.TimeEpsilon
                    && item.TimeSeconds <= sampled.SourceSeconds + DirectorActionNormalizer.TimeEpsilon)
                    .Select(item => item.DeepClone()).ToArray()
                : events.Where(item => item.TimeSeconds > previousSource + DirectorActionNormalizer.TimeEpsilon
                        || item.TimeSeconds <= sampled.SourceSeconds + DirectorActionNormalizer.TimeEpsilon)
                    .Select(item => item.DeepClone()).ToArray();
        }
        previousTimeline = sampled.TimelineSeconds;
        previousSource = sampled.SourceSeconds;
        initialized = true;
        return new DirectorActionEvaluation
        {
            TimelineSeconds = sampled.TimelineSeconds,
            SourceSeconds = sampled.SourceSeconds,
            Completed = sampled.Completed,
            Layers = sampled.Layers,
            CrossedEvents = crossed
        };
    }
}

public static class DirectorActionTriggerDetector
{
    public static bool Matches(
        DirectorReplayActorSample sample,
        DirectorActionTriggerType type,
        string? customCode = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        IEnumerable<string> animations = sample.AnimationStates
            .SelectMany(animation => new[] { animation.Code, animation.StateCode })
            .Concat(sample.Animations)
            .Where(code => !string.IsNullOrWhiteSpace(code));
        return type switch
        {
            DirectorActionTriggerType.Swimming => sample.Swimming || sample.FeetInLiquid
                || Contains(animations, "swim"),
            DirectorActionTriggerType.Sprinting => Contains(animations, "sprint"),
            DirectorActionTriggerType.Jumping => !sample.OnGround && sample.HasMotion && sample.MotionY > 0.02d
                || Contains(animations, "jump", "land"),
            DirectorActionTriggerType.Attack => Contains(
                animations, "attack", "hit", "sword", "cleaver", "bow", "throw"),
            DirectorActionTriggerType.Mining => Contains(
                animations, "mine", "pickaxe", "break", "dig", "chop", "hammer"),
            DirectorActionTriggerType.Hurt => Contains(
                animations, "hurt", "damage", "stagger"),
            DirectorActionTriggerType.Custom => !string.IsNullOrWhiteSpace(customCode)
                && Contains(animations, customCode.Trim()),
            _ => false
        };
    }

    private static bool Contains(IEnumerable<string> animations, params string[] fragments)
        => animations.Any(animation => fragments.Any(fragment => animation.Contains(
            fragment,
            StringComparison.OrdinalIgnoreCase)));
}

public static class DirectorActionComposer
{
    private static readonly string[] UpperElements =
    {
        "UpperTorso", "UpperArmR", "LowerArmR", "ItemAnchor", "UpperArmL", "LowerArmL",
        "ItemAnchorL", "ShoulderAttachment", "Neck", "Head", "UpperBackAttachment",
        "eyesroot", "R eye", "L eye2", "mouth", "eyelid r", "eyelid l"
    };
    private static readonly string[] LowerElements =
    {
        "LowerTorso", "UpperFootL", "LowerFootL", "FootAttachmentL", "UpperFootR",
        "LowerFootR", "FootAttachmentR", "LowerBackAttachment"
    };

    public static DirectorReplayActorSample Apply(
        DirectorReplayActorSample baseState,
        DirectorActionEvaluation evaluation,
        bool includeRootMotion = false)
    {
        ArgumentNullException.ThrowIfNull(baseState);
        ArgumentNullException.ThrowIfNull(evaluation);
        DirectorReplayActorSample result = baseState.DeepClone();
        foreach (DirectorActionLayerEvaluation layerState in evaluation.Layers
            .OrderBy(item => item.Layer.Order))
        {
            ApplyLayer(result, layerState, includeRootMotion);
        }
        result.TimeSeconds = baseState.TimeSeconds;
        return result;
    }

    private static void ApplyLayer(
        DirectorReplayActorSample target,
        DirectorActionLayerEvaluation evaluation,
        bool includeRootMotion)
    {
        DirectorActionLayer layer = evaluation.Layer;
        DirectorReplayActorSample state = evaluation.State;
        if (includeRootMotion && layer.Kind == DirectorActionLayerKind.FullBody)
        {
            target.X += state.X;
            target.Y += state.Y;
            target.Z += state.Z;
        }

        if (layer.Kind is DirectorActionLayerKind.FullBody or DirectorActionLayerKind.LowerBody)
        {
            target.HasMotion = state.HasMotion;
            target.MotionX = state.MotionX;
            target.MotionY = state.MotionY;
            target.MotionZ = state.MotionZ;
            target.HasRenderState = state.HasRenderState;
            target.OnGround = state.OnGround;
            target.FeetInLiquid = state.FeetInLiquid;
            target.Swimming = state.Swimming;
            target.ClimbingOnFace = state.ClimbingOnFace;
            target.HasAgentState = state.HasAgentState;
            target.Controls = state.Controls;
            target.ServerControls = state.ServerControls;
            target.HasPlayerPostureState = state.HasPlayerPostureState;
            target.CanStandUp = state.CanStandUp;
        }
        if (layer.Kind is DirectorActionLayerKind.FullBody or DirectorActionLayerKind.Head)
        {
            target.HeadYaw = state.HeadYaw;
            target.HeadPitch = state.HeadPitch;
            target.BodyYaw = state.BodyYaw;
            target.HasThirdPersonPose = state.HasThirdPersonPose;
            target.ThirdPersonBodyYaw = state.ThirdPersonBodyYaw;
            target.ThirdPersonHeadYaw = state.ThirdPersonHeadYaw;
            target.ThirdPersonHeadPitch = state.ThirdPersonHeadPitch;
            target.BodyYawServer = state.BodyYawServer;
            target.WalkYaw = state.WalkYaw;
            target.WalkPitch = state.WalkPitch;
        }
        if (layer.Kind is DirectorActionLayerKind.FullBody or DirectorActionLayerKind.Hands)
        {
            target.HasPlayerState = state.HasPlayerState;
            target.HandUse = state.HandUse;
            target.ServerHandUse = state.ServerHandUse;
            target.RightHandItem = state.RightHandItem is null ? Array.Empty<byte>() : (byte[])state.RightHandItem.Clone();
            target.LeftHandItem = state.LeftHandItem is null ? Array.Empty<byte>() : (byte[])state.LeftHandItem.Clone();
        }
        if (layer.Kind == DirectorActionLayerKind.FullBody)
        {
            target.EquipmentItems = CloneItemList(state.EquipmentItems);
            target.BackpackItems = CloneItemList(state.BackpackItems);
        }

        List<DirectorReplayAnimationSample> states = state.AnimationStates
            .Select(animation => PrepareAnimation(animation, layer, evaluation.EffectiveWeight))
            .ToList();
        if (layer.BlendMode == DirectorActionBlendMode.Override
            && layer.Kind == DirectorActionLayerKind.FullBody)
        {
            target.AnimationStates = states;
            target.Animations = states.Select(item => item.Code).ToList();
            return;
        }
        foreach (DirectorReplayAnimationSample animation in states)
        {
            int existing = target.AnimationStates.FindIndex(item =>
                string.Equals(item.Code, animation.Code, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    string.IsNullOrWhiteSpace(item.StateCode) ? item.Code : item.StateCode,
                    string.IsNullOrWhiteSpace(animation.StateCode) ? animation.Code : animation.StateCode,
                    StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                target.AnimationStates[existing] = animation;
            }
            else
            {
                target.AnimationStates.Add(animation);
            }
        }
        target.Animations = target.AnimationStates.Select(item => item.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<byte[]> CloneItemList(IReadOnlyList<byte[]>? items)
        => items is null
            ? new List<byte[]>()
            : items.Select(item => item is null ? Array.Empty<byte>() : (byte[])item.Clone()).ToList();

    private static DirectorReplayAnimationSample PrepareAnimation(
        DirectorReplayAnimationSample source,
        DirectorActionLayer layer,
        float effectiveWeight)
    {
        DirectorReplayAnimationSample result = source.DeepClone();
        result.Weight = Math.Max(0f, result.Weight * effectiveWeight);
        result.BlendedWeight = Math.Max(0f, result.BlendedWeight * effectiveWeight);
        Dictionary<string, float> mask = layer.ElementWeights.Count > 0
            ? new Dictionary<string, float>(layer.ElementWeights, StringComparer.OrdinalIgnoreCase)
            : StandardMask(layer.Kind);
        foreach ((string element, float weight) in mask)
        {
            result.ElementWeights[element] = weight * Math.Max(0f, effectiveWeight);
        }
        return result;
    }

    private static Dictionary<string, float> StandardMask(DirectorActionLayerKind kind)
    {
        Dictionary<string, float> result = new(StringComparer.OrdinalIgnoreCase);
        if (kind == DirectorActionLayerKind.UpperBody)
        {
            foreach (string element in UpperElements) result[element] = 100f;
            foreach (string element in LowerElements) result[element] = 0f;
        }
        else if (kind == DirectorActionLayerKind.LowerBody)
        {
            foreach (string element in UpperElements) result[element] = 0f;
            foreach (string element in LowerElements) result[element] = 100f;
        }
        else if (kind is DirectorActionLayerKind.Head or DirectorActionLayerKind.Expression)
        {
            foreach (string element in UpperElements) result[element] = 0f;
            foreach (string element in LowerElements) result[element] = 0f;
            foreach (string element in new[] { "Neck", "Head", "eyesroot", "R eye", "L eye2", "mouth", "eyelid r", "eyelid l" })
            {
                result[element] = 100f;
            }
        }
        else if (kind == DirectorActionLayerKind.Hands)
        {
            foreach (string element in UpperElements) result[element] = 0f;
            foreach (string element in LowerElements) result[element] = 0f;
            foreach (string element in new[] { "UpperArmR", "LowerArmR", "ItemAnchor", "UpperArmL", "LowerArmL", "ItemAnchorL" })
            {
                result[element] = 100f;
            }
        }
        return result;
    }
}

public static class DirectorActionImporter
{
    public static bool TryCreateFromReplay(
        DirectorReplayActorTrack? actor,
        double startSeconds,
        double endSeconds,
        string? requestedName,
        out DirectorActionAsset action,
        out string error,
        Guid sourceReplayId = default)
    {
        action = new DirectorActionAsset();
        error = string.Empty;
        if (actor?.Samples is not { Count: > 0 })
        {
            error = "action-no-samples";
            return false;
        }
        double start = double.IsFinite(startSeconds) ? Math.Max(actor.Samples[0].TimeSeconds, startSeconds) : actor.Samples[0].TimeSeconds;
        double end = double.IsFinite(endSeconds) ? Math.Min(actor.Samples[^1].TimeSeconds, endSeconds) : actor.Samples[^1].TimeSeconds;
        if (end <= start + DirectorActionNormalizer.TimeEpsilon)
        {
            error = "action-range-invalid";
            return false;
        }

        List<DirectorReplayActorSample> samples = actor.Samples
            .Where(sample => sample.TimeSeconds >= start - DirectorActionNormalizer.TimeEpsilon
                && sample.TimeSeconds <= end + DirectorActionNormalizer.TimeEpsilon)
            .Select(sample => sample.DeepClone())
            .ToList();
        AddBoundary(actor, start, samples);
        AddBoundary(actor, end, samples);
        samples = samples.OrderBy(sample => sample.TimeSeconds)
            .GroupBy(sample => sample.TimeSeconds)
            .Select(group => group.Last())
            .ToList();
        double originX = samples[0].X;
        double originY = samples[0].Y;
        double originZ = samples[0].Z;
        foreach (DirectorReplayActorSample sample in samples)
        {
            sample.TimeSeconds -= start;
            sample.X -= originX;
            sample.Y -= originY;
            sample.Z -= originZ;
        }

        List<string> tags = DetectTags(samples);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        action = DirectorActionNormalizer.Normalize(new DirectorActionAsset
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(requestedName)
                ? (string.IsNullOrWhiteSpace(actor.DisplayName) ? "Recorded Action" : actor.DisplayName + " Action")
                : requestedName.Trim(),
            Category = string.IsNullOrWhiteSpace(actor.PlayerUid)
                && !string.Equals(actor.EntityCode, "player", StringComparison.OrdinalIgnoreCase)
                    ? DirectorActionCategory.Creature
                    : DirectorActionCategory.Player,
            Tags = tags,
            CompatibleEntityCodes = string.IsNullOrWhiteSpace(actor.EntityCode)
                ? new List<string>()
                : new List<string> { actor.EntityCode },
            SourceReplayId = sourceReplayId,
            SourceActorKey = actor.ActorKey,
            InitialEntityState = actor.InitialState,
            Layers = new List<DirectorActionLayer>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Base",
                    Kind = DirectorActionLayerKind.FullBody,
                    Frames = samples.Select(sample => new DirectorActionFrame
                    {
                        TimeSeconds = sample.TimeSeconds,
                        State = sample
                    }).ToList()
                }
            },
            Triggers = TagsToTriggers(tags),
            CreatedUtc = now,
            ModifiedUtc = now
        });
        return true;
    }

    public static DirectorActionAsset CreateAnimationAction(
        string name,
        string animationCode,
        string sourceModId = "",
        string sourceCode = "",
        bool requiresTarget = false,
        double durationSeconds = 1d,
        float animationSpeed = 1f,
        float weight = 1f,
        float easeInSpeed = 10f,
        float easeOutSpeed = 10f,
        int blendMode = 0)
    {
        double duration = double.IsFinite(durationSeconds)
            ? Math.Clamp(durationSeconds, 1d / 60d, 600d)
            : 1d;
        DirectorReplayAnimationSample animation = new()
        {
            Code = animationCode,
            StateCode = animationCode,
            Progress = 0f,
            AnimationSpeed = float.IsFinite(animationSpeed) ? animationSpeed : 1f,
            Weight = float.IsFinite(weight) ? Math.Max(0f, weight) : 1f,
            BlendedWeight = 1f,
            EasingFactor = 1f,
            EaseInSpeed = float.IsFinite(easeInSpeed) ? Math.Max(0f, easeInSpeed) : 10f,
            EaseOutSpeed = float.IsFinite(easeOutSpeed) ? Math.Max(0f, easeOutSpeed) : 10f,
            BlendMode = blendMode
        };
        DirectorReplayActorSample state = new()
        {
            HasPlayerState = true,
            HasAgentState = true,
            HasRenderState = true,
            OnGround = true,
            Animations = new List<string> { animationCode },
            AnimationStates = new List<DirectorReplayAnimationSample> { animation }
        };
        DirectorReplayActorSample completedState = state.DeepClone();
        completedState.TimeSeconds = duration;
        completedState.AnimationStates[0].Progress = 1f;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return DirectorActionNormalizer.Normalize(new DirectorActionAsset
        {
            Name = name,
            Category = DirectorActionCategory.Player,
            Tags = requiresTarget ? new List<string> { "emote", "paired" } : new List<string> { "emote" },
            CompatibleEntityCodes = new List<string> { "player" },
            LoopMode = DirectorActionLoopMode.Loop,
            LoopCount = 0,
            SourceModId = sourceModId,
            SourceCode = sourceCode,
            Layers = new List<DirectorActionLayer>
            {
                new()
                {
                    Name = "Emote",
                    Kind = DirectorActionLayerKind.Additive,
                    Frames = new List<DirectorActionFrame>
                    {
                        new() { TimeSeconds = 0d, State = state },
                        new() { TimeSeconds = duration, State = completedState }
                    }
                }
            },
            CreatedUtc = now,
            ModifiedUtc = now
        });
    }

    public static bool TryRepairGeneratedAnimationProgress(
        DirectorActionAsset source,
        out DirectorActionAsset repaired)
    {
        ArgumentNullException.ThrowIfNull(source);
        repaired = source.DeepClone();
        if (!source.SourceModId.Equals("emotes", StringComparison.OrdinalIgnoreCase)
            && !source.SourceModId.Equals("playermodellib", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool changed = false;
        foreach (DirectorActionLayer layer in repaired.Layers)
        {
            if (layer.Frames.Count < 2)
            {
                continue;
            }
            DirectorReplayAnimationSample[] states = layer.Frames
                .SelectMany(frame => frame.State.AnimationStates)
                .ToArray();
            string[] animationCodes = states
                .Select(state => string.IsNullOrWhiteSpace(state.StateCode) ? state.Code : state.StateCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (states.Length == 0 || animationCodes.Length != 1)
            {
                continue;
            }
            if (layer.Kind == DirectorActionLayerKind.FullBody)
            {
                layer.Kind = DirectorActionLayerKind.Additive;
                changed = true;
            }
            if (states.Any(state => Math.Abs(state.Progress) > 0.0001f))
            {
                continue;
            }
            double start = layer.Frames[0].TimeSeconds;
            double end = layer.Frames[^1].TimeSeconds;
            double duration = end - start;
            if (duration <= DirectorActionNormalizer.TimeEpsilon)
            {
                continue;
            }
            foreach (DirectorActionFrame frame in layer.Frames)
            {
                float progress = (float)Math.Clamp((frame.TimeSeconds - start) / duration, 0d, 1d);
                foreach (DirectorReplayAnimationSample state in frame.State.AnimationStates)
                {
                    state.Progress = progress;
                }
            }
            changed = true;
        }
        if (!changed)
        {
            repaired = source.DeepClone();
            return false;
        }
        repaired.ModifiedUtc = DateTimeOffset.UtcNow;
        repaired = DirectorActionNormalizer.Normalize(repaired);
        return true;
    }

    public static DirectorActionAsset CreateExpressionAction(
        string name,
        string expressionCode,
        string modelCode,
        string sourceModId = "playermodellib")
    {
        DirectorReplayActorSample state = new()
        {
            HasPlayerState = true,
            HasAgentState = true,
            HasRenderState = true,
            OnGround = true
        };
        DirectorActionFrame first = new()
        {
            State = state,
            StringProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["facialexpression"] = expressionCode
            }
        };
        DirectorActionFrame last = first.DeepClone();
        last.TimeSeconds = 1d;
        last.State.TimeSeconds = 1d;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return DirectorActionNormalizer.Normalize(new DirectorActionAsset
        {
            Name = name,
            Category = DirectorActionCategory.Expression,
            Tags = new List<string> { "expression", expressionCode },
            CompatibleEntityCodes = new List<string> { "player" },
            CompatibleModelCodes = string.IsNullOrWhiteSpace(modelCode)
                ? new List<string>()
                : new List<string> { modelCode },
            PreviewModelCode = modelCode,
            LoopMode = DirectorActionLoopMode.Loop,
            LoopCount = 0,
            SourceModId = sourceModId,
            SourceCode = expressionCode,
            Layers = new List<DirectorActionLayer>
            {
                new()
                {
                    Name = "Expression",
                    Kind = DirectorActionLayerKind.Expression,
                    BlendMode = DirectorActionBlendMode.Additive,
                    Frames = new List<DirectorActionFrame> { first, last }
                }
            },
            CreatedUtc = now,
            ModifiedUtc = now
        });
    }

    private static void AddBoundary(DirectorReplayActorTrack actor, double time, ICollection<DirectorReplayActorSample> samples)
    {
        if (!samples.Any(sample => Math.Abs(sample.TimeSeconds - time) <= DirectorActionNormalizer.TimeEpsilon)
            && DirectorReplayMath.TrySample(actor, time, out DirectorReplayActorSample sample))
        {
            sample.TimeSeconds = time;
            samples.Add(sample);
        }
    }

    private static List<string> DetectTags(IEnumerable<DirectorReplayActorSample> samples)
    {
        HashSet<string> tags = new(StringComparer.OrdinalIgnoreCase);
        foreach (DirectorReplayActorSample sample in samples)
        {
            if (sample.Swimming || sample.FeetInLiquid) tags.Add("swimming");
            if (!sample.OnGround && sample.MotionY > 0.02d) tags.Add("jumping");
            foreach (string animation in sample.AnimationStates.Select(item => item.Code).Concat(sample.Animations))
            {
                string code = animation.ToLowerInvariant();
                if (ContainsAny(code, "sprint", "run")) tags.Add("sprinting");
                if (ContainsAny(code, "swim")) tags.Add("swimming");
                if (ContainsAny(code, "jump", "land")) tags.Add("jumping");
                if (ContainsAny(code, "hit", "attack", "sword", "cleaver", "bow", "throw")) tags.Add("attack");
                if (ContainsAny(code, "pickaxe", "break", "mine", "dig", "chop", "hammer")) tags.Add("mining");
                if (ContainsAny(code, "hurt", "damage", "stagger")) tags.Add("hurt");
                if (ContainsAny(code, "sneak")) tags.Add("sneak");
            }
        }
        return tags.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<DirectorActionTrigger> TagsToTriggers(IEnumerable<string> tags)
        => tags.Select(tag => tag.ToLowerInvariant() switch
            {
                "attack" => DirectorActionTriggerType.Attack,
                "mining" => DirectorActionTriggerType.Mining,
                "swimming" => DirectorActionTriggerType.Swimming,
                "sprinting" => DirectorActionTriggerType.Sprinting,
                "jumping" => DirectorActionTriggerType.Jumping,
                "hurt" => DirectorActionTriggerType.Hurt,
                _ => DirectorActionTriggerType.Custom
            })
            .Distinct()
            .Select(type => new DirectorActionTrigger { Id = Guid.NewGuid(), Type = type })
            .ToList();

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(value.Contains);
}

public static class DirectorActionEditor
{
    public static DirectorActionAsset Trim(DirectorActionAsset source, double startSeconds, double endSeconds)
    {
        DirectorActionAsset result = DirectorActionNormalizer.Normalize(source).DeepClone();
        double start = Math.Clamp(double.IsFinite(startSeconds) ? startSeconds : 0d, 0d, result.DurationSeconds);
        double end = Math.Clamp(double.IsFinite(endSeconds) ? endSeconds : result.DurationSeconds, start, result.DurationSeconds);
        foreach (DirectorActionLayer layer in result.Layers)
        {
            DirectorReplayActorTrack track = new()
            {
                ActorKey = "trim",
                Samples = layer.Frames.Select(frame => frame.State.DeepClone()).ToList()
            };
            List<DirectorActionFrame> kept = layer.Frames
                .Where(frame => frame.TimeSeconds >= start - DirectorActionNormalizer.TimeEpsilon
                    && frame.TimeSeconds <= end + DirectorActionNormalizer.TimeEpsilon)
                .Select(frame => frame.DeepClone())
                .ToList();
            AddTrimBoundary(track, layer, start, kept);
            AddTrimBoundary(track, layer, end, kept);
            foreach (DirectorActionFrame frame in kept)
            {
                frame.TimeSeconds -= start;
                frame.State.TimeSeconds = frame.TimeSeconds;
            }
            layer.Frames = kept.OrderBy(frame => frame.TimeSeconds).ToList();
        }
        result.Events = result.Events
            .Where(item => item.TimeSeconds >= start - DirectorActionNormalizer.TimeEpsilon
                && item.TimeSeconds <= end + DirectorActionNormalizer.TimeEpsilon)
            .Select(item => { DirectorActionEvent clone = item.DeepClone(); clone.TimeSeconds -= start; return clone; })
            .ToList();
        result.ModifiedUtc = DateTimeOffset.UtcNow;
        return DirectorActionNormalizer.Normalize(result);
    }

    public static DirectorActionAsset Append(
        DirectorActionAsset left,
        DirectorActionAsset right,
        double transitionSeconds = 0d)
    {
        DirectorActionAsset result = DirectorActionNormalizer.Normalize(left).DeepClone();
        DirectorActionAsset addition = DirectorActionNormalizer.Normalize(right);
        double transition = Math.Clamp(
            double.IsFinite(transitionSeconds) ? transitionSeconds : 0d,
            0d,
            Math.Min(result.DurationSeconds, addition.DurationSeconds));
        double offset = Math.Max(0d, result.DurationSeconds - transition);
        foreach (DirectorActionLayer sourceLayer in addition.Layers)
        {
            DirectorActionLayer? target = result.Layers.FirstOrDefault(layer => layer.Kind == sourceLayer.Kind);
            if (target is null)
            {
                target = sourceLayer.DeepClone();
                target.Id = Guid.NewGuid();
                target.Frames.Clear();
                result.Layers.Add(target);
            }
            foreach (DirectorActionFrame sourceFrame in sourceLayer.Frames)
            {
                DirectorActionFrame frame = sourceFrame.DeepClone();
                frame.TimeSeconds += offset;
                frame.State.TimeSeconds = frame.TimeSeconds;
                target.Frames.Add(frame);
            }
            if (transition > 0d)
            {
                target.FadeInSeconds = Math.Max(target.FadeInSeconds, transition);
            }
        }
        result.Events.AddRange(addition.Events.Select(item =>
        {
            DirectorActionEvent clone = item.DeepClone();
            clone.Id = Guid.NewGuid();
            clone.TimeSeconds += offset;
            return clone;
        }));
        result.Tags = result.Tags.Concat(addition.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.ModifiedUtc = DateTimeOffset.UtcNow;
        return DirectorActionNormalizer.Normalize(result);
    }

    private static void AddTrimBoundary(
        DirectorReplayActorTrack track,
        DirectorActionLayer source,
        double time,
        ICollection<DirectorActionFrame> destination)
    {
        if (destination.Any(frame => Math.Abs(frame.TimeSeconds - time) <= DirectorActionNormalizer.TimeEpsilon)
            || !DirectorReplayMath.TrySample(track, time, out DirectorReplayActorSample sample))
        {
            return;
        }
        DirectorActionFrame? nearest = source.Frames.OrderBy(frame => Math.Abs(frame.TimeSeconds - time)).FirstOrDefault();
        destination.Add(new DirectorActionFrame
        {
            TimeSeconds = time,
            State = sample,
            BoneOverrides = nearest?.BoneOverrides.ToDictionary(
                item => item.Key,
                item => item.Value.DeepClone(),
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, DirectorActionBoneTransform>(StringComparer.OrdinalIgnoreCase),
            Expressions = nearest is null
                ? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, float>(nearest.Expressions, StringComparer.OrdinalIgnoreCase),
            StringProperties = nearest is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(nearest.StringProperties, StringComparer.OrdinalIgnoreCase)
        });
    }
}

public sealed class DirectorActionEditHistory
{
    private readonly int capacity;
    private readonly List<DirectorActionAsset> undo = new();
    private readonly List<DirectorActionAsset> redo = new();

    public DirectorActionEditHistory(int capacity = 64)
    {
        this.capacity = Math.Max(1, capacity);
    }

    public bool CanUndo => undo.Count > 0;

    public bool CanRedo => redo.Count > 0;

    public bool RecordChange(DirectorActionAsset before, DirectorActionAsset after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (DirectorActionIO.ExportJson(before) == DirectorActionIO.ExportJson(after))
        {
            return false;
        }
        Push(undo, before);
        redo.Clear();
        return true;
    }

    public bool TryUndo(DirectorActionAsset current, out DirectorActionAsset restored)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (undo.Count == 0)
        {
            restored = current.DeepClone();
            return false;
        }
        Push(redo, current);
        restored = Pop(undo);
        return true;
    }

    public bool TryRedo(DirectorActionAsset current, out DirectorActionAsset restored)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (redo.Count == 0)
        {
            restored = current.DeepClone();
            return false;
        }
        Push(undo, current);
        restored = Pop(redo);
        return true;
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }

    private void Push(List<DirectorActionAsset> stack, DirectorActionAsset action)
    {
        if (stack.Count >= capacity)
        {
            stack.RemoveAt(0);
        }
        stack.Add(action.DeepClone());
    }

    private static DirectorActionAsset Pop(List<DirectorActionAsset> stack)
    {
        int index = stack.Count - 1;
        DirectorActionAsset result = stack[index].DeepClone();
        stack.RemoveAt(index);
        return result;
    }
}

public sealed class DirectorActionFileDocument
{
    public int SchemaVersion { get; set; } = 1;
    public DirectorActionAsset Action { get; set; } = new();
}

public static class DirectorActionIO
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VSACTN01");
    private static readonly JsonSerializerOptions Options = new()
    {
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public static string ExportJson(DirectorActionAsset action)
        => JsonSerializer.Serialize(new DirectorActionFileDocument
        {
            Action = DirectorActionNormalizer.Normalize(action)
        }, Options);

    public static DirectorActionAsset ImportJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        DirectorActionFileDocument? document = JsonSerializer.Deserialize<DirectorActionFileDocument>(json, Options);
        if (document is null || document.SchemaVersion > 1)
        {
            throw new InvalidDataException("Unsupported VS Director action JSON.");
        }
        return DirectorActionNormalizer.Normalize(document.Action);
    }

    public static void Write(string path, DirectorActionAsset action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string extension = Path.GetExtension(fullPath);
        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            AtomicWrite(fullPath, stream =>
            {
                using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(ExportJson(action));
                writer.Flush();
            });
            return;
        }
        AtomicWrite(fullPath, stream =>
        {
            stream.Write(Magic);
            using GZipStream gzip = new(stream, CompressionLevel.SmallestSize, leaveOpen: true);
            JsonSerializer.Serialize(gzip, new DirectorActionFileDocument
            {
                Action = DirectorActionNormalizer.Normalize(action)
            }, Options);
        });
    }

    public static DirectorActionAsset Read(string path)
    {
        string fullPath = Path.GetFullPath(path);
        using FileStream stream = File.OpenRead(fullPath);
        if (string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            using StreamReader reader = new(stream, Encoding.UTF8);
            return ImportJson(reader.ReadToEnd());
        }
        Span<byte> magic = stackalloc byte[Magic.Length];
        if (stream.Read(magic) != Magic.Length || !magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("Invalid .vsaction header.");
        }
        using GZipStream gzip = new(stream, CompressionMode.Decompress);
        DirectorActionFileDocument? document = JsonSerializer.Deserialize<DirectorActionFileDocument>(gzip, Options);
        if (document is null || document.SchemaVersion > 1)
        {
            throw new InvalidDataException("Unsupported .vsaction document.");
        }
        return DirectorActionNormalizer.Normalize(document.Action);
    }

    public static void WritePackage(string path, IEnumerable<DirectorActionAsset> actions, string packageName)
    {
        DirectorActionAsset[] normalized = actions.Select(DirectorActionNormalizer.Normalize).ToArray();
        AtomicWrite(Path.GetFullPath(path), stream =>
        {
            using ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true);
            ZipArchiveEntry manifest = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
            using (Stream manifestStream = manifest.Open())
            {
                JsonSerializer.Serialize(manifestStream, new
                {
                    schemaVersion = 1,
                    name = string.IsNullOrWhiteSpace(packageName) ? "Action Pack" : packageName.Trim(),
                    actionCount = normalized.Length,
                    actions = normalized.Select(action => new { id = action.Id, name = action.Name }).ToArray()
                }, Options);
            }
            foreach (DirectorActionAsset action in normalized)
            {
                ZipArchiveEntry entry = archive.CreateEntry($"actions/{action.Id:N}.json", CompressionLevel.SmallestSize);
                using Stream entryStream = entry.Open();
                JsonSerializer.Serialize(entryStream, new DirectorActionFileDocument { Action = action }, Options);
            }
        });
    }

    public static IReadOnlyList<DirectorActionAsset> ReadPackage(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(Path.GetFullPath(path));
        List<DirectorActionAsset> actions = new();
        foreach (ZipArchiveEntry entry in archive.Entries
            .Where(entry => entry.FullName.StartsWith("actions/", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using Stream stream = entry.Open();
            DirectorActionFileDocument? document = JsonSerializer.Deserialize<DirectorActionFileDocument>(stream, Options);
            if (document is not null && document.SchemaVersion <= 1)
            {
                actions.Add(DirectorActionNormalizer.Normalize(document.Action));
            }
        }
        return actions;
    }

    private static void AtomicWrite(string fullPath, Action<Stream> writer)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writer(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }
}
