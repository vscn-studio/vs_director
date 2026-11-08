namespace VSDirector.Data;

/// <summary>
/// A named, persistent camera position.  Angles and field of view are stored
/// in degrees so the document remains independent of the game client's
/// radians-based camera API.
/// </summary>
public sealed class CameraPreset
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();

    public string Description { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    /// <summary>
    /// Vintage Story dimension id (the overworld is normally zero).
    /// </summary>
    public int Dimension { get; set; }

    public float Yaw { get; set; }

    public float Pitch { get; set; }

    public float Roll { get; set; }

    public float Fov { get; set; }

    // Editor-only pivot offset in world axes; it does not affect the rendered view.
    public double EditorPivotOffsetX { get; set; }
    public double EditorPivotOffsetY { get; set; }
    public double EditorPivotOffsetZ { get; set; }

    public bool PreviewVisible { get; set; }

    /// <summary>
    /// Optional quick-switch key slot. Valid values are 1 through 10.
    /// </summary>
    public int? QuickSlot { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }

    /// <summary>
    /// Creates a detached copy, including a new tag list.
    /// </summary>
    public CameraPreset Clone() => DeepClone();

    /// <summary>
    /// Creates a detached copy, including a new tag list.
    /// </summary>
    public CameraPreset DeepClone()
    {
        return new CameraPreset
        {
            Id = Id,
            Name = Name,
            Tags = Tags is null ? new List<string>() : new List<string>(Tags),
            Description = Description,
            X = X,
            Y = Y,
            Z = Z,
            Dimension = Dimension,
            Yaw = Yaw,
            Pitch = Pitch,
            Roll = Roll,
            Fov = Fov,
            EditorPivotOffsetX = EditorPivotOffsetX,
            EditorPivotOffsetY = EditorPivotOffsetY,
            EditorPivotOffsetZ = EditorPivotOffsetZ,
            PreviewVisible = PreviewVisible,
            QuickSlot = QuickSlot,
            CreatedUtc = CreatedUtc,
            ModifiedUtc = ModifiedUtc
        };
    }
}
