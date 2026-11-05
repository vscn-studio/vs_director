using System.Text.Json.Serialization;

namespace VSDirector.Data;

public enum CameraSequenceLoopMode
{
    Once,
    Loop
}

public sealed class CameraSequenceShot
{
    public Guid CameraId { get; set; }

    public double DurationSeconds { get; set; } = CameraSequenceSampler.DefaultShotDurationSeconds;

    public CameraSequenceShot DeepClone()
    {
        return new CameraSequenceShot
        {
            CameraId = CameraId,
            DurationSeconds = DurationSeconds
        };
    }
}

public sealed class CameraSequence
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public CameraSequenceLoopMode LoopMode { get; set; } = CameraSequenceLoopMode.Once;

    public List<CameraSequenceShot> Shots { get; set; } = new();

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }

    [JsonIgnore]
    public double DurationSeconds => Shots?.Where(shot => shot is not null).Sum(shot => shot.DurationSeconds) ?? 0d;

    public CameraSequence DeepClone()
    {
        return new CameraSequence
        {
            Id = Id,
            Name = Name,
            LoopMode = LoopMode,
            Shots = Shots is null
                ? new List<CameraSequenceShot>()
                : Shots.Where(shot => shot is not null).Select(shot => shot.DeepClone()).ToList(),
            CreatedUtc = CreatedUtc,
            ModifiedUtc = ModifiedUtc
        };
    }
}
