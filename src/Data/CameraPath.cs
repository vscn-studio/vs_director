using System.Text.Json.Serialization;

namespace VSDirector.Data;

public enum CameraPathInterpolation
{
    Linear,
    EaseInOut,
    Bezier,
    Step
}

public enum CameraPathLoopMode
{
    Once,
    Loop,
    PingPong
}

public enum ActorPathYawConvention
{
    ModelFacing,
    CameraFacing
}

public sealed class CameraPath
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();

    public string Description { get; set; } = string.Empty;

    public CameraPathInterpolation Interpolation { get; set; } = CameraPathInterpolation.EaseInOut;

    public CameraPathLoopMode LoopMode { get; set; } = CameraPathLoopMode.Once;

    public bool PreviewVisible { get; set; } = true;

    public double PlaybackSpeed { get; set; } = CameraPathTiming.DefaultPlaybackSpeed;

    public Guid SourceReplayId { get; set; }

    public string SourceActorKey { get; set; } = string.Empty;

    public ActorPathYawConvention ActorYawConvention { get; set; }

    public double? ActorEyeHeightOffset { get; set; }

    public double ReplayTimeOffsetSeconds { get; set; }

    public List<DirectorReplayPreloadColumn> IncludedPreloadColumns { get; set; } = new();

    public List<DirectorReplayPreloadColumn> ExcludedPreloadColumns { get; set; } = new();

    public List<CameraKeyframe> Keyframes { get; set; } = new();

    public List<DirectorPathElementTrack> ElementTracks { get; set; } = new();

    public List<DirectorPathElementKeyframe> ElementKeyframes { get; set; } = new();

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }

    [JsonIgnore]
    public double DurationSeconds => Keyframes.Count == 0
        ? 0d
        : Keyframes[^1].TimeSeconds + Math.Max(0d, Keyframes[^1].DurationSeconds);

    [JsonIgnore]
    public double PlaybackDurationSeconds => DurationSeconds / CameraPathTiming.NormalizePlaybackSpeed(PlaybackSpeed);

    public CameraPath DeepClone()
    {
        return new CameraPath
        {
            Id = Id,
            Name = Name,
            Tags = Tags is null ? new List<string>() : new List<string>(Tags),
            Description = Description,
            Interpolation = Interpolation,
            LoopMode = LoopMode,
            PreviewVisible = PreviewVisible,
            PlaybackSpeed = PlaybackSpeed,
            SourceReplayId = SourceReplayId,
            SourceActorKey = SourceActorKey,
            ActorYawConvention = ActorYawConvention,
            ActorEyeHeightOffset = ActorEyeHeightOffset,
            ReplayTimeOffsetSeconds = ReplayTimeOffsetSeconds,
            IncludedPreloadColumns = IncludedPreloadColumns is null
                ? new List<DirectorReplayPreloadColumn>()
                : new List<DirectorReplayPreloadColumn>(IncludedPreloadColumns),
            ExcludedPreloadColumns = ExcludedPreloadColumns is null
                ? new List<DirectorReplayPreloadColumn>()
                : new List<DirectorReplayPreloadColumn>(ExcludedPreloadColumns),
            Keyframes = Keyframes is null
                ? new List<CameraKeyframe>()
                : Keyframes.Where(frame => frame is not null).Select(frame => frame.DeepClone()).ToList(),
            ElementTracks = ElementTracks is null
                ? new List<DirectorPathElementTrack>()
                : ElementTracks.Where(track => track is not null)
                    .Select(track => track.DeepClone()).ToList(),
            ElementKeyframes = ElementKeyframes is null
                ? new List<DirectorPathElementKeyframe>()
                : ElementKeyframes.Where(frame => frame is not null)
                    .Select(frame => frame.DeepClone()).ToList(),
            CreatedUtc = CreatedUtc,
            ModifiedUtc = ModifiedUtc
        };
    }
}
