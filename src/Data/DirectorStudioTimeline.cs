namespace VSDirector.Data;

public enum StudioInterpolation { Linear, Hold, Smooth }

public sealed class StudioTransformKey
{
    public double Time { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Roll { get; set; }
    public float Scale { get; set; } = 1;
    public bool Visible { get; set; } = true;
    public StudioInterpolation Interpolation { get; set; }
    public StudioTransformKey Clone() => (StudioTransformKey)MemberwiseClone();
    public static StudioTransformKey Capture(DirectorStudioSceneObject item, double time) => new()
    { Time = time, X = item.X, Y = item.Y, Z = item.Z, Yaw = item.Yaw, Pitch = item.Pitch, Roll = item.Roll, Scale = item.Scale, Visible = item.Visible };
}

public sealed class StudioActionClip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    // Snapshot the action, so edits/removal in the global library cannot change a film.
    public DirectorActionAsset Action { get; set; } = new();
    public double Start { get; set; }
    public double Duration { get; set; } = 1;
    public double Offset { get; set; }
    public double Speed { get; set; } = 1;
    public bool Loop { get; set; }
    public bool RootMotion { get; set; }
    public StudioActionClip Clone() => new() { Id = Id, Action = Action.DeepClone(), Start = Start,
        Duration = Duration, Offset = Offset, Speed = Speed, Loop = Loop, RootMotion = RootMotion };
    public double SourceTime(double time)
    {
        double duration = Action.DurationSeconds;
        double local = Math.Max(0, (time - Start) * Speed + Offset);
        return Loop && duration > 0 ? local % duration : Math.Min(local, duration);
    }
}

public sealed class StudioCameraKey
{
    public double Time { get; set; }
    public CameraPreset Pose { get; set; } = new() { Fov = 70 };
    public StudioInterpolation Interpolation { get; set; }
    public StudioCameraKey Clone() => new() { Time = Time, Pose = Pose.DeepClone(), Interpolation = Interpolation };
}

/// <summary>Pure, absolute-time sampling shared by preview, picking and offline capture.</summary>
public static class DirectorStudioTimeline
{
    public const int MaximumKeys = 10000;
    public static double Snap(double time, int fps) => Math.Round(Math.Max(0, time) * fps) / fps;
    public static long FrameCount(double duration, int fps) => checked((long)Math.Ceiling(duration * fps - 1e-8));
    public static float Angle(float a, float b, double t) => (float)(a + ((b - a + 540) % 360 - 180) * t);
    private static double Blend(double t, StudioInterpolation mode) => mode switch
    { StudioInterpolation.Hold => 0, StudioInterpolation.Smooth => t * t * (3 - 2 * t), _ => t };
    private static (int A, int B, double T) Segment<T>(IReadOnlyList<T> keys, double time, Func<T, double> at)
    {
        if (time <= at(keys[0])) return (0, 0, 0);
        for (int i = 1; i < keys.Count; i++)
            if (time < at(keys[i])) return (i - 1, i, (time - at(keys[i - 1])) / (at(keys[i]) - at(keys[i - 1])));
        return (keys.Count - 1, keys.Count - 1, 0);
    }
    public static DirectorStudioSceneObject Sample(DirectorStudioSceneObject source, double time)
    {
        // Do not clone action payloads for every rendered frame.
        var result = new DirectorStudioSceneObject { Id = source.Id, AssetId = source.AssetId, ResourceLink = source.ResourceLink,
            Name = source.Name, X = source.X, Y = source.Y, Z = source.Z, Yaw = source.Yaw, Pitch = source.Pitch,
            Roll = source.Roll, Scale = source.Scale, Visible = source.Visible };
        if (source.Keys.Count == 0) return result;
        var (i, j, f) = Segment(source.Keys, time, k => k.Time);
        var a = source.Keys[i]; var b = source.Keys[j]; double t = Blend(f, a.Interpolation);
        result.X = a.X + (b.X - a.X) * t; result.Y = a.Y + (b.Y - a.Y) * t; result.Z = a.Z + (b.Z - a.Z) * t;
        result.Yaw = Angle(a.Yaw, b.Yaw, t); result.Pitch = Angle(a.Pitch, b.Pitch, t); result.Roll = Angle(a.Roll, b.Roll, t);
        result.Scale = (float)(a.Scale + (b.Scale - a.Scale) * t); result.Visible = a.Visible;
        return result;
    }
    public static CameraPreset? Camera(DirectorStudioProject project, double time)
    {
        if (project.CameraKeys.Count == 0) return null;
        var (i, j, f) = Segment(project.CameraKeys, time, k => k.Time);
        var a = project.CameraKeys[i]; var b = project.CameraKeys[j];
        double t = a.Pose.Dimension == b.Pose.Dimension ? Blend(f, a.Interpolation) : 0;
        var pose = a.Pose.DeepClone();
        pose.X += (b.Pose.X - pose.X) * t; pose.Y += (b.Pose.Y - pose.Y) * t; pose.Z += (b.Pose.Z - pose.Z) * t;
        pose.Yaw = Angle(pose.Yaw, b.Pose.Yaw, t); pose.Pitch = Angle(pose.Pitch, b.Pose.Pitch, t); pose.Roll = Angle(pose.Roll, b.Pose.Roll, t);
        pose.Fov += (float)((b.Pose.Fov - pose.Fov) * t); return pose;
    }
    public static void Upsert(DirectorStudioSceneObject item, StudioTransformKey key)
    {
        item.Keys.RemoveAll(k => Math.Abs(k.Time - key.Time) < 1e-7); item.Keys.Add(key);
        item.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
    }
    public static void UpsertCamera(DirectorStudioProject project, StudioCameraKey key)
    {
        project.CameraKeys.RemoveAll(k => Math.Abs(k.Time - key.Time) < 1e-7); project.CameraKeys.Add(key);
        project.CameraKeys.Sort((a, b) => a.Time.CompareTo(b.Time));
    }
    internal static void Validate(DirectorStudioProject project)
    {
        if (!double.IsFinite(project.Duration) || project.Duration < 1d / 240 || project.Duration > 86400
            || project.FramesPerSecond is < 1 or > 240) throw new InvalidDataException("Invalid Studio duration or frame rate.");
        static void Number(double value) { if (!double.IsFinite(value) || Math.Abs(value) > 1_000_000) throw new InvalidDataException("Invalid Studio keyframe value."); }
        void Times(IEnumerable<double> values)
        {
            var times = values.ToArray();
            if (times.Length > MaximumKeys || times.Distinct().Count() != times.Length
                || times.Any(t => !double.IsFinite(t) || t < 0 || t > project.Duration)) throw new InvalidDataException("Invalid or duplicate Studio keyframe time.");
        }
        if (project.CameraKeys is null) throw new InvalidDataException("Missing Studio camera track.");
        Times(project.CameraKeys.Select(k => k?.Time ?? double.NaN));
        foreach (var key in project.CameraKeys)
        {
            if (key.Pose is null || !Enum.IsDefined(key.Interpolation)) throw new InvalidDataException("Invalid Studio camera key.");
            var p = key.Pose; foreach (double n in new double[] { p.X, p.Y, p.Z, p.Yaw, p.Pitch, p.Roll, p.Fov }) Number(n);
            if (p.Fov is < 1 or > 160) throw new InvalidDataException("Studio camera FOV must be 1–160 degrees.");
        }
        project.CameraKeys.Sort((a, b) => a.Time.CompareTo(b.Time));
        foreach (var item in project.SceneObjects)
        {
            if (item.Keys is null || item.Actions is null) throw new InvalidDataException("Missing Studio actor tracks.");
            Times(item.Keys.Select(k => k?.Time ?? double.NaN));
            foreach (var k in item.Keys)
            {
                foreach (double n in new double[] { k.X, k.Y, k.Z, k.Yaw, k.Pitch, k.Roll, k.Scale }) Number(n);
                if (k.Scale is < .01f or > 100 || !Enum.IsDefined(k.Interpolation)) throw new InvalidDataException("Invalid Studio transform key.");
            }
            item.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (item.Actions.Count > 1024 || item.Actions.Any(c => c is null)) throw new InvalidDataException("Invalid Studio action track.");
            item.Actions.Sort((a, b) => a.Start.CompareTo(b.Start));
            double end = 0;
            foreach (var clip in item.Actions)
            {
                foreach (double n in new[] { clip.Start, clip.Duration, clip.Offset, clip.Speed }) Number(n);
                if (clip.Start < end - 1e-7 || clip.Duration <= 0 || clip.Start + clip.Duration > project.Duration + 1e-7
                    || clip.Offset < 0 || clip.Speed is <= 0 or > 100 || clip.Action is null)
                    throw new InvalidDataException("Studio action clips must be in range and must not overlap on one actor.");
                clip.Action = DirectorActionNormalizer.Normalize(clip.Action);
                end = clip.Start + clip.Duration;
            }
        }
    }
}
