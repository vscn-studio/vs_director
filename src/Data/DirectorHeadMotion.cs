using System.Numerics;

namespace VSDirector.Data;

/// <summary>Camera-local metres, degrees and FOV multiplier; independent of tracking hardware.</summary>
public readonly record struct DirectorHeadMotionSample(double Time, float X, float Y, float Z,
    float Yaw, float Pitch, float Roll, float FovScale = 1, float Vignette = 0)
{
    public bool IsFinite => double.IsFinite(Time) && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z)
        && float.IsFinite(Yaw) && float.IsFinite(Pitch) && float.IsFinite(Roll) && float.IsFinite(FovScale) && float.IsFinite(Vignette);
}

public sealed class DirectorHeadMotionClip
{
    public List<DirectorHeadMotionSample> Samples { get; set; } = [];
    public double TrimStart { get; set; }
    public double TrimEnd { get; set; }
    public double PlaybackRate { get; set; } = 1;
    public float Strength { get; set; } = 1;
    public bool Translation { get; set; } = true;
    public bool Rotation { get; set; } = true;
    public bool Zoom { get; set; } = true;
    public double Duration => Math.Max(0, TrimEnd - TrimStart) / Math.Max(.01, PlaybackRate);
    public DirectorHeadMotionClip DeepClone()
    {
        var copy = (DirectorHeadMotionClip)MemberwiseClone();
        copy.Samples = Samples?.ToList() ?? [];
        return copy;
    }
    public void Normalize()
    {
        Samples = (Samples ?? []).Where(s => s.IsFinite && s.Time >= 0 && s.Time <= 3600)
            .OrderBy(s => s.Time).GroupBy(s => s.Time).Select(g => g.Last()).Take(216001).ToList();
        double end = Samples.Count == 0 ? 0 : Samples[^1].Time;
        TrimStart = Math.Clamp(double.IsFinite(TrimStart) ? TrimStart : 0, 0, end);
        TrimEnd = Math.Clamp(double.IsFinite(TrimEnd) ? TrimEnd : end, TrimStart, end);
        PlaybackRate = Math.Clamp(double.IsFinite(PlaybackRate) ? PlaybackRate : 1, .05, 20);
        Strength = Math.Clamp(float.IsFinite(Strength) ? Strength : 1, 0, 4);
    }
    public bool TrySample(double time, out DirectorHeadMotionSample sample)
    {
        sample = default;
        if (!double.IsFinite(time) || Samples.Count == 0 || time < 0 || time >= Duration) return false;
        double source = TrimStart + time * PlaybackRate;
        int lo = 0, hi = Samples.Count - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (Samples[mid].Time <= source) lo = mid; else hi = mid - 1; }
        var a = Samples[lo]; var b = Samples[Math.Min(lo + 1, Samples.Count - 1)];
        float t = b.Time <= a.Time ? 0 : (float)Math.Clamp((source - a.Time) / (b.Time - a.Time), 0, 1);
        float L(float x, float y) => x + (y - x) * t;
        float A(float x, float y) => x + (float)Math.IEEERemainder(y - x, 360) * t;
        sample = new(time, Translation ? L(a.X, b.X) * Strength : 0,
            Translation ? L(a.Y, b.Y) * Strength : 0, Translation ? L(a.Z, b.Z) * Strength : 0,
            Rotation ? A(a.Yaw, b.Yaw) * Strength : 0, Rotation ? A(a.Pitch, b.Pitch) * Strength : 0,
            Rotation ? A(a.Roll, b.Roll) * Strength : 0, Zoom ? Math.Clamp(1 + (L(a.FovScale, b.FovScale) - 1) * Strength, .01f, 4) : 1,
            Zoom ? Math.Clamp(L(a.Vignette, b.Vignette) * Strength, 0, 1) : 0);
        return true;
    }
}

public static class DirectorHeadMotion
{
    /// <summary>Reuse preset families so recording children share visibility, deletion and undo.</summary>
    public static DirectorReplayElementTrack EnsureGroup(DirectorReplayViewportTimeline timeline,
        DirectorReplayElementTrack motion, string name = "")
    {
        var group = timeline.Tracks.FirstOrDefault(t => t.Id == motion.ParentGroupId
            && t.TrackType == DirectorReplayElementTrackType.PresetGroup);
        if (group is null)
        {
            group = new DirectorReplayElementTrack
            {
                Id = ChildId(motion.Id, 0x48), TrackType = DirectorReplayElementTrackType.PresetGroup,
                Name = string.IsNullOrEmpty(name) ? motion.Name : name
            };
            int index = timeline.Tracks.IndexOf(motion);
            timeline.Tracks.Insert(Math.Max(0, index), group);
            motion.ParentGroupId = group.Id;
        }
        if (!timeline.Tracks.Any(t => t.ParentGroupId == group.Id && t.TrackType == DirectorReplayElementTrackType.Camera))
            timeline.Tracks.Insert(timeline.Tracks.IndexOf(motion), new DirectorReplayElementTrack
            {
                Id = ChildId(motion.Id, 0x43), ParentGroupId = group.Id, TrackType = DirectorReplayElementTrackType.Camera
            });
        return group;
    }

    // Stable IDs keep repeated normalization of old files and undo snapshots identical.
    private static Guid ChildId(Guid source, byte discriminator)
    {
        byte[] bytes = source.ToByteArray(); bytes[0] ^= discriminator; bytes[15] ^= 0xa7;
        return new Guid(bytes);
    }

    public static void AddCameraAnchor(DirectorReplayViewportTimeline timeline,
        DirectorReplayElementTrack motion, DirectorReplayElementKeyframe frame, CameraPreset pose)
    {
        var group = EnsureGroup(timeline, motion);
        var camera = timeline.Tracks.First(t => t.ParentGroupId == group.Id && t.TrackType == DirectorReplayElementTrackType.Camera);
        // An edited camera row belongs to the user. Never replace its keys on a retake.
        if (timeline.Clips.Any(c => c.TrackId == camera.Id) || frame.HeadMotion is not { Duration: > 0 } clip) return;
        foreach (bool end in new[] { false, true })
            timeline.Clips.Add(new DirectorReplayViewportClip
            {
                TrackId = camera.Id, SourceType = DirectorReplayViewportSourceType.StaticCamera,
                CameraPose = pose.DeepClone(), TimelineStartSeconds = frame.TimeSeconds + (end ? clip.Duration : 0),
                CameraInterpolationToNext = CameraPathInterpolation.Step, EndsShot = end,
                DurationSeconds = 1, PlaybackRate = 1, Enabled = true, TrackOrder = timeline.Clips.Count
            });
    }

    public static void ResizeCameraAnchor(DirectorReplayViewportTimeline timeline,
        DirectorReplayElementKeyframe frame, double oldStart, double oldDuration)
    {
        var motion = timeline.Tracks.FirstOrDefault(t => t.Id == frame.TrackId);
        if (motion is null || motion.ParentGroupId == Guid.Empty) return;
        var camera = timeline.Tracks.FirstOrDefault(t => t.ParentGroupId == motion.ParentGroupId && t.TrackType == DirectorReplayElementTrackType.Camera);
        if (camera is null) return;
        var keys = timeline.Clips.Where(c => c.TrackId == camera.Id).OrderBy(c => c.TimelineStartSeconds).ToArray();
        // Only resize an unchanged two-key interval. More elaborate camera animation stays independent.
        if (keys.Length != 2 || keys[0].EndsShot != (oldDuration <= 0) || !keys[1].EndsShot
            || Math.Abs(keys[0].TimelineStartSeconds - oldStart) > 1e-6
            || Math.Abs(keys[1].TimelineStartSeconds - oldStart - oldDuration) > 1e-6) return;
        keys[0].TimelineStartSeconds = frame.TimeSeconds;
        keys[0].EndsShot = (frame.HeadMotion?.Duration ?? 0) <= 0;
        keys[1].TimelineStartSeconds = frame.TimeSeconds + (frame.HeadMotion?.Duration ?? 0);
    }

    public static bool TrySample(DirectorReplayViewportTimeline timeline, double time, out DirectorHeadMotionSample sample)
    {
        foreach (var track in timeline.EffectTracksInPriorityOrder())
        {
            if (track.TrackType != DirectorReplayElementTrackType.HeadMotion || !timeline.IsTrackEnabled(track)) continue;
            // A newer clip on the same row takes precedence only over its own duration.
            foreach (var frame in timeline.ElementKeyframes.Where(f => f.TrackId == track.Id && !f.EndsElement && f.TimeSeconds <= time)
                .OrderByDescending(f => f.TimeSeconds))
                if (frame.HeadMotion?.TrySample(time - frame.TimeSeconds, out sample) == true) return true;
        }
        sample = default;
        return false;
    }
    public static CameraPreset Apply(CameraPreset source, DirectorHeadMotionSample sample)
    {
        var pose = source.DeepClone();
        if (!sample.IsFinite) return pose;
        const float radians = MathF.PI / 180;
        // Vintage Story also represents upright views with pitch centred on 180.
        // Treating that as a conventional Euler rotation introduces a half-turn
        // of roll. Use the same world-up hemisphere and roll sign as CameraMatrices.
        var (yaw, pitch) = CameraViewAngles.Normalize(source.Yaw, source.Pitch);
        var basis = Quaternion.CreateFromYawPitchRoll(yaw * radians, pitch * radians, -source.Roll * radians);
        var offset = Vector3.Transform(new Vector3(sample.X, sample.Y, sample.Z), basis);
        pose.X += offset.X; pose.Y += offset.Y; pose.Z += offset.Z;
        var rotation = Quaternion.Normalize(basis * Quaternion.CreateFromYawPitchRoll(sample.Yaw * radians, sample.Pitch * radians, -sample.Roll * radians));
        var matrix = Matrix4x4.CreateFromQuaternion(rotation);
        pose.Yaw = MathF.Atan2(matrix.M31, matrix.M33) / radians;
        pose.Pitch = MathF.Asin(Math.Clamp(-matrix.M32, -1, 1)) / radians;
        pose.Roll = -MathF.Atan2(matrix.M12, matrix.M22) / radians;
        if (sample.Yaw == 0 && sample.Pitch == 0 && sample.Roll == 0)
        {
            pose.Yaw = source.Yaw; pose.Pitch = source.Pitch; pose.Roll = source.Roll;
        }
        pose.Fov = Math.Clamp(pose.Fov * sample.FovScale, 1, 150);
        return pose;
    }
}
