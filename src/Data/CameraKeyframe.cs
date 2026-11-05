namespace VSDirector.Data;

public sealed class CameraKeyframe
{
    public string Name { get; set; } = string.Empty;

    public double TimeSeconds { get; set; }

    /// <summary>
    /// Length of the timeline segment beginning at this keyframe. The final
    /// keyframe may use this as a hold duration.
    /// </summary>
    public double DurationSeconds { get; set; }

    /// <summary>
    /// Time spent holding this exact pose before the outgoing transition starts.
    /// This is clamped to the duration of the outgoing segment.
    /// </summary>
    public double HoldSeconds { get; set; }

    /// <summary>
    /// Optional interpolation override for the segment beginning at this
    /// keyframe. Null keeps the parent path's default interpolation.
    /// </summary>
    public CameraPathInterpolation? InterpolationToNext { get; set; }

    /// <summary>
    /// Relative camera speed at the beginning and end of the outgoing
    /// segment. Values shape motion inside the authored segment duration;
    /// they do not change the segment's end time.
    /// </summary>
    public double SegmentStartSpeed { get; set; } = CameraPathTiming.DefaultSegmentSpeed;

    public double SegmentEndSpeed { get; set; } = CameraPathTiming.DefaultSegmentSpeed;

    public string Marker { get; set; } = string.Empty;

    /// <summary>
    /// Enables explicit cubic Bezier position handles. Handle coordinates are
    /// offsets from this keyframe, so moving the keyframe keeps its curve shape.
    /// </summary>
    public bool BezierHandlesEnabled { get; set; }

    public double BezierInX { get; set; }

    public double BezierInY { get; set; }

    public double BezierInZ { get; set; }

    public double BezierOutX { get; set; }

    public double BezierOutY { get; set; }

    public double BezierOutZ { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public int Dimension { get; set; }

    public float Yaw { get; set; }

    public float Pitch { get; set; }

    public float Roll { get; set; }

    public float Fov { get; set; } = 70f;

    public CameraKeyframe DeepClone()
    {
        return new CameraKeyframe
        {
            Name = Name,
            TimeSeconds = TimeSeconds,
            DurationSeconds = DurationSeconds,
            HoldSeconds = HoldSeconds,
            InterpolationToNext = InterpolationToNext,
            SegmentStartSpeed = SegmentStartSpeed,
            SegmentEndSpeed = SegmentEndSpeed,
            Marker = Marker,
            BezierHandlesEnabled = BezierHandlesEnabled,
            BezierInX = BezierInX,
            BezierInY = BezierInY,
            BezierInZ = BezierInZ,
            BezierOutX = BezierOutX,
            BezierOutY = BezierOutY,
            BezierOutZ = BezierOutZ,
            X = X,
            Y = Y,
            Z = Z,
            Dimension = Dimension,
            Yaw = Yaw,
            Pitch = Pitch,
            Roll = Roll,
            Fov = Fov
        };
    }

    public static CameraKeyframe FromPreset(
        CameraPreset preset,
        double timeSeconds,
        double durationSeconds = CameraPathTiming.DefaultKeyframeDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new CameraKeyframe
        {
            TimeSeconds = timeSeconds,
            DurationSeconds = durationSeconds,
            InterpolationToNext = null,
            X = preset.X,
            Y = preset.Y,
            Z = preset.Z,
            Dimension = preset.Dimension,
            Yaw = preset.Yaw,
            Pitch = preset.Pitch,
            Roll = preset.Roll,
            Fov = preset.Fov
        };
    }

    public CameraPreset ToPreset(Guid id, string name)
    {
        return new CameraPreset
        {
            Id = id,
            Name = name,
            X = X,
            Y = Y,
            Z = Z,
            Dimension = Dimension,
            Yaw = Yaw,
            Pitch = Pitch,
            Roll = Roll,
            Fov = Fov
        };
    }
}
