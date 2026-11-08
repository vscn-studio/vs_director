using System.Text.Json.Serialization;

namespace VSDirector.Data;

public enum DirectorWorkflowCardKind
{
    Shot,
    Countdown,
    // Retained so older workspace documents can still be deserialized and migrated.
    Entity,
    ActorPath,
    Action,
    Environment
}

public enum DirectorWorkflowType
{
    Recording,
    // Retained so older workspace documents can still be deserialized and migrated.
    Action
}

public sealed class DirectorWorkflowCard
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DirectorWorkflowCardKind Kind { get; set; } = DirectorWorkflowCardKind.Shot;

    public DirectorRecordingSourceType SourceType { get; set; } = DirectorRecordingSourceType.StaticCamera;

    public Guid SourceId { get; set; }

    public string ActorKey { get; set; } = string.Empty;

    public string EntityCode { get; set; } = string.Empty;

    public string ModelCode { get; set; } = string.Empty;

    public bool HideLiveActor { get; set; } = true;

    public Guid ActionId { get; set; }

    public Guid ActorPerformanceId { get; set; }

    public Guid PathId { get; set; }

    public Guid BoundCardId { get; set; }

    public double SourceOffsetSeconds { get; set; }

    public double PlaybackSpeed { get; set; } = 1d;

    public DirectorActionLoopMode ActionLoopMode { get; set; }

    public DirectorActionTriggerType ActionTriggerType { get; set; }

    public string EnvironmentCode { get; set; } = string.Empty;

    public string EnvironmentData { get; set; } = string.Empty;

    public double StartSeconds { get; set; }

    public double EndSeconds { get; set; } = 5d;

    public int Lane { get; set; }

    public int Order { get; set; }

    public bool Enabled { get; set; } = true;

    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Null keeps the automatic cut/cross-dissolve decision based on card
    /// overlap. A value explicitly controls how this card enters the edit.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DirectorVideoTransition? TransitionIn { get; set; }

    [JsonIgnore]
    public double DurationSeconds => Math.Max(0d, EndSeconds - StartSeconds);

    public DirectorWorkflowCard DeepClone()
    {
        DirectorWorkflowCard clone = (DirectorWorkflowCard)MemberwiseClone();
        clone.TransitionIn = TransitionIn?.DeepClone();
        return clone;
    }
}

public sealed class DirectorWorkflow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DirectorWorkflowType Type { get; set; }

    public int? QuickSlot { get; set; }

    public List<DirectorWorkflowCard> Cards { get; set; } = new();

    /// <summary>External audio materials placed on the workflow timeline.</summary>
    public List<DirectorTimelineAudioClip> AudioClips { get; set; } = new();

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }

    [JsonIgnore]
    public double DurationSeconds => Math.Max(
        (Cards ?? new List<DirectorWorkflowCard>())
            .Where(card => card is not null && card.Enabled)
            .Select(card => card.EndSeconds)
            .DefaultIfEmpty()
            .Max(),
        (AudioClips ?? new List<DirectorTimelineAudioClip>())
            .Where(clip => clip is not null && clip.Enabled)
            .Select(clip => clip.TimelineStartSeconds + Math.Max(0d, clip.DurationSeconds))
            .DefaultIfEmpty()
            .Max());

    public DirectorWorkflow DeepClone()
    {
        return new DirectorWorkflow
        {
            Id = Id,
            Name = Name,
            Type = Type,
            QuickSlot = QuickSlot,
            Cards = Cards is null
                ? new List<DirectorWorkflowCard>()
                : Cards.Where(card => card is not null).Select(card => card.DeepClone()).ToList(),
            AudioClips = AudioClips is null
                ? new List<DirectorTimelineAudioClip>()
                : AudioClips.Where(clip => clip is not null).Select(clip => clip.DeepClone()).ToList(),
            CreatedUtc = CreatedUtc,
            ModifiedUtc = ModifiedUtc
        };
    }
}

public sealed class DirectorWorkflowExportSegment
{
    public Guid CardId { get; set; }

    public string CardName { get; set; } = string.Empty;

    public DirectorRecordingSourceType SourceType { get; set; }

    public Guid SourceId { get; set; }

    public double StartSeconds { get; set; }

    public double EndSeconds { get; set; }

    public DirectorWorkflowExportSegment DeepClone() => (DirectorWorkflowExportSegment)MemberwiseClone();
}

public sealed class DirectorWorkflowExportPass
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public double StartSeconds { get; set; }

    public double EndSeconds { get; set; }

    public bool SplitForOverlap { get; set; }

    public List<DirectorWorkflowExportSegment> Segments { get; set; } = new();

    public DirectorWorkflowExportPass DeepClone()
    {
        return new DirectorWorkflowExportPass
        {
            Id = Id,
            Name = Name,
            StartSeconds = StartSeconds,
            EndSeconds = EndSeconds,
            SplitForOverlap = SplitForOverlap,
            Segments = Segments.Select(segment => segment.DeepClone()).ToList()
        };
    }

    public DirectorWorkflowExportSegment? Resolve(double timeSeconds)
    {
        if (Segments.Count == 0 || !double.IsFinite(timeSeconds))
        {
            return null;
        }

        DirectorWorkflowExportSegment[] ordered = Segments
            .OrderBy(segment => segment.StartSeconds)
            .ThenBy(segment => segment.EndSeconds)
            .ThenBy(segment => segment.CardName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DirectorWorkflowExportSegment? active = ordered.LastOrDefault(segment =>
            timeSeconds >= segment.StartSeconds - DirectorWorkflowPlanner.TimeEpsilon
            && timeSeconds < segment.EndSeconds - DirectorWorkflowPlanner.TimeEpsilon);
        if (active is not null)
        {
            return active;
        }

        // A conflict-free edit is a continuous output. In an intentional gap,
        // hold the preceding shot; before the first cut, use the first shot.
        return ordered.LastOrDefault(segment => segment.StartSeconds <= timeSeconds)
            ?? ordered[0];
    }
}

public static class DirectorWorkflowPlanner
{
    public const double MinCardDurationSeconds = 0.05d;
    public const double MaxTimelineSeconds = 604800d;
    public const double TimeEpsilon = 0.000001d;

    public static IReadOnlyList<DirectorWorkflowExportPass> Create(
        DirectorWorkflow workflow,
        Func<DirectorWorkflowCard, bool>? sourceExists = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        DirectorWorkflowCard[] cards = (workflow.Cards ?? new List<DirectorWorkflowCard>())
            .Where(card => card is not null
                && card.Enabled
                && card.Kind == DirectorWorkflowCardKind.Shot
                && IsExportSource(card.SourceType)
                && card.SourceId != Guid.Empty
                && card.EndSeconds - card.StartSeconds >= MinCardDurationSeconds - TimeEpsilon
                && (sourceExists?.Invoke(card) ?? true))
            .OrderBy(card => card.StartSeconds)
            .ThenBy(card => card.Lane)
            .ThenBy(card => card.Order)
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (cards.Length == 0)
        {
            return Array.Empty<DirectorWorkflowExportPass>();
        }

        HashSet<Guid> overlapping = new();
        for (int left = 0; left < cards.Length; left++)
        {
            for (int right = left + 1; right < cards.Length; right++)
            {
                if (cards[right].StartSeconds >= cards[left].EndSeconds - TimeEpsilon)
                {
                    break;
                }

                if (IntervalsOverlap(cards[left], cards[right]))
                {
                    overlapping.Add(cards[left].Id);
                    overlapping.Add(cards[right].Id);
                }
            }
        }

        List<DirectorWorkflowExportPass> passes = new();
        DirectorWorkflowCard[] continuous = cards.Where(card => !overlapping.Contains(card.Id)).ToArray();
        if (continuous.Length > 0)
        {
            passes.Add(new DirectorWorkflowExportPass
            {
                Id = DeterministicPassId(workflow.Id, Guid.Empty),
                Name = string.IsNullOrWhiteSpace(workflow.Name) ? "Workflow" : workflow.Name.Trim(),
                StartSeconds = continuous.Min(card => card.StartSeconds),
                EndSeconds = continuous.Max(card => card.EndSeconds),
                SplitForOverlap = false,
                Segments = continuous.Select(ToSegment).ToList()
            });
        }

        foreach (DirectorWorkflowCard card in cards.Where(card => overlapping.Contains(card.Id)))
        {
            passes.Add(new DirectorWorkflowExportPass
            {
                Id = DeterministicPassId(workflow.Id, card.Id),
                Name = $"{(string.IsNullOrWhiteSpace(workflow.Name) ? "Workflow" : workflow.Name.Trim())}_{card.Name}",
                StartSeconds = card.StartSeconds,
                EndSeconds = card.EndSeconds,
                SplitForOverlap = true,
                Segments = new List<DirectorWorkflowExportSegment> { ToSegment(card) }
            });
        }

        return passes
            .OrderBy(pass => pass.StartSeconds)
            .ThenBy(pass => pass.SplitForOverlap)
            .ThenBy(pass => pass.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static double RecordingCountdownSeconds(DirectorWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return (workflow.Cards ?? new List<DirectorWorkflowCard>())
            .Where(card => card is not null
                && card.Enabled
                && card.Kind == DirectorWorkflowCardKind.Countdown
                && double.IsFinite(card.StartSeconds)
                && double.IsFinite(card.EndSeconds)
                && card.EndSeconds - card.StartSeconds >= MinCardDurationSeconds - TimeEpsilon)
            .OrderBy(card => card.StartSeconds)
            .ThenBy(card => card.Lane)
            .ThenBy(card => card.Order)
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .Select(card => card.EndSeconds - card.StartSeconds)
            .FirstOrDefault();
    }

    public static bool IsExportSource(DirectorRecordingSourceType sourceType)
        => sourceType is DirectorRecordingSourceType.StaticCamera
            or DirectorRecordingSourceType.Path
            or DirectorRecordingSourceType.Sequence
            or DirectorRecordingSourceType.Follow
            or DirectorRecordingSourceType.ReplayScene;

    public static bool IsCardKindAllowed(DirectorWorkflowType workflowType, DirectorWorkflowCardKind cardKind)
        => cardKind is DirectorWorkflowCardKind.Shot or DirectorWorkflowCardKind.Countdown;

    public static int FindAvailableLane(
        DirectorWorkflow workflow,
        double startSeconds,
        double endSeconds,
        Guid? ignoredCardId = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        double start = double.IsFinite(startSeconds) ? Math.Max(0d, startSeconds) : 0d;
        double end = double.IsFinite(endSeconds)
            ? Math.Max(start + MinCardDurationSeconds, endSeconds)
            : start + MinCardDurationSeconds;
        for (int lane = 0; lane <= 31; lane++)
        {
            bool occupied = (workflow.Cards ?? new List<DirectorWorkflowCard>()).Any(card => card is not null
                && card.Enabled
                && card.Id != ignoredCardId
                && card.Lane == lane
                && card.StartSeconds < end - TimeEpsilon
                && start < card.EndSeconds - TimeEpsilon);
            if (!occupied)
            {
                return lane;
            }
        }
        return 31;
    }

    public static double SegmentLocalTime(
        DirectorWorkflowExportSegment segment,
        double timelineSeconds)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (!double.IsFinite(timelineSeconds))
        {
            return 0d;
        }

        return Math.Clamp(
            timelineSeconds - segment.StartSeconds,
            0d,
            Math.Max(0d, segment.EndSeconds - segment.StartSeconds));
    }

    private static bool IntervalsOverlap(DirectorWorkflowCard left, DirectorWorkflowCard right)
        => left.StartSeconds < right.EndSeconds - TimeEpsilon
            && right.StartSeconds < left.EndSeconds - TimeEpsilon;

    private static DirectorWorkflowExportSegment ToSegment(DirectorWorkflowCard card)
        => new()
        {
            CardId = card.Id,
            CardName = card.Name,
            SourceType = card.SourceType,
            SourceId = card.SourceId,
            StartSeconds = card.StartSeconds,
            EndSeconds = card.EndSeconds
        };

    private static Guid DeterministicPassId(Guid workflowId, Guid cardId)
    {
        Span<byte> value = stackalloc byte[16];
        Span<byte> card = stackalloc byte[16];
        workflowId.TryWriteBytes(value);
        cardId.TryWriteBytes(card);
        for (int index = 0; index < value.Length; index++)
        {
            value[index] ^= card[index];
        }
        return new Guid(value);
    }
}
