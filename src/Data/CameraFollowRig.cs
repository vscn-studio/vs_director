namespace VSDirector.Data;

public enum CameraFollowTargetType
{
    Player,
    Entity
}

public enum CameraFollowViewMode
{
    FirstPerson,
    ThirdPerson
}

public enum CameraFollowMotionMode
{
    Immediate,
    Smooth,
    Delayed
}

public enum CameraFollowAnchor
{
    Head,
    Body
}

/// <summary>
/// A named camera rig that derives its pose from a player or loaded entity.
/// Offsets are stored in target-local right/up/forward coordinates.
/// </summary>
public sealed class CameraFollowRig
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();

    public string Description { get; set; } = string.Empty;

    public CameraFollowTargetType TargetType { get; set; } = CameraFollowTargetType.Player;

    public string TargetPlayerUid { get; set; } = string.Empty;

    public long TargetEntityId { get; set; }

    public string TargetDisplayName { get; set; } = string.Empty;

    public CameraFollowViewMode ViewMode { get; set; } = CameraFollowViewMode.ThirdPerson;

    /// <summary>
    /// Requires the optional ClearView client mod when this rig is activated.
    /// </summary>
    public bool OpenClearView { get; set; }

    public CameraFollowMotionMode MotionMode { get; set; } = CameraFollowMotionMode.Smooth;

    public CameraFollowAnchor Anchor { get; set; } = CameraFollowAnchor.Head;

    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double OffsetZ { get; set; }

    public double Distance { get; set; } = CameraFollowMath.DefaultDistance;

    public double Height { get; set; } = CameraFollowMath.DefaultHeight;

    public double OrbitAngleDegrees { get; set; }

    public double OrbitSpeedDegreesPerSecond { get; set; }

    public double SmoothTimeSeconds { get; set; } = CameraFollowMath.DefaultSmoothTimeSeconds;

    public double DelaySeconds { get; set; } = CameraFollowMath.DefaultDelaySeconds;

    public float Fov { get; set; } = CameraFollowMath.DefaultFov;

    /// <summary>
    /// Optional Alt+number quick-switch slot. Valid values are 1 through 10.
    /// </summary>
    public int? QuickSlot { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }

    public CameraFollowRig DeepClone()
    {
        return new CameraFollowRig
        {
            Id = Id,
            Name = Name,
            Tags = Tags is null ? new List<string>() : new List<string>(Tags),
            Description = Description,
            TargetType = TargetType,
            TargetPlayerUid = TargetPlayerUid,
            TargetEntityId = TargetEntityId,
            TargetDisplayName = TargetDisplayName,
            ViewMode = ViewMode,
            OpenClearView = OpenClearView,
            MotionMode = MotionMode,
            Anchor = Anchor,
            OffsetX = OffsetX,
            OffsetY = OffsetY,
            OffsetZ = OffsetZ,
            Distance = Distance,
            Height = Height,
            OrbitAngleDegrees = OrbitAngleDegrees,
            OrbitSpeedDegreesPerSecond = OrbitSpeedDegreesPerSecond,
            SmoothTimeSeconds = SmoothTimeSeconds,
            DelaySeconds = DelaySeconds,
            Fov = Fov,
            QuickSlot = QuickSlot,
            CreatedUtc = CreatedUtc,
            ModifiedUtc = ModifiedUtc
        };
    }
}
