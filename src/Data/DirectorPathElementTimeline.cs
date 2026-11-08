namespace VSDirector.Data;

public enum DirectorPathElementTrackType
{
    CameraPath,
    Fov,
    Roll,
    CameraShake,
    LookAt,
    Speed,
    Marker
}

public enum DirectorPathElementInterpolation
{
    Hold,
    Linear,
    EaseInOut
}

public sealed class DirectorPathElementTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DirectorPathElementTrackType TrackType { get; set; }

    public bool Enabled { get; set; } = true;

    public string Name { get; set; } = string.Empty;

    public DirectorPathElementTrack DeepClone()
        => (DirectorPathElementTrack)MemberwiseClone();
}

public sealed class DirectorPathElementKeyframe
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TrackId { get; set; }

    public DirectorPathElementTrackType TrackType { get; set; }

    public double TimeSeconds { get; set; }

    public DirectorPathElementInterpolation InterpolationToNext { get; set; }
        = DirectorPathElementInterpolation.Linear;

    public float Fov { get; set; } = 70f;

    public float Roll { get; set; }

    public float FrequencyX { get; set; } = 1f;

    public float AmplitudeX { get; set; }

    public float FrequencyY { get; set; } = 1f;

    public float AmplitudeY { get; set; }

    public long TargetEntityId { get; set; }

    public string BodyPart { get; set; } = "head";

    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public int Dimension { get; set; }

    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double OffsetZ { get; set; }

    public float YawOffset { get; set; }

    public float PitchOffset { get; set; }

    public double PlaybackRate { get; set; } = 1d;

    public string Label { get; set; } = string.Empty;

    public DirectorPathElementKeyframe DeepClone()
        => (DirectorPathElementKeyframe)MemberwiseClone();
}

public static class DirectorPathElementTimeline
{
    private const double TimeEpsilon = 0.000001d;
    public const double TicksPerSecond = 30d;
    public const float MinFovDegrees = 1f;
    public const float MaxFovDegrees = 150f;
    public const double MinPlaybackRate = 0.05d;
    public const double MaxPlaybackRate = 8d;

    public static void Normalize(CameraPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        path.ElementTracks = NormalizeTracks(path.ElementTracks);
        path.ElementKeyframes = NormalizeKeyframes(path.ElementTracks, path.ElementKeyframes);
    }

    public static List<DirectorPathElementTrack> NormalizeTracks(
        IEnumerable<DirectorPathElementTrack>? source)
    {
        List<DirectorPathElementTrack> result = new();
        HashSet<Guid> ids = new();
        bool hasCameraPath = false;
        foreach (DirectorPathElementTrack? item in source ?? Array.Empty<DirectorPathElementTrack>())
        {
            if (item is null || !Enum.IsDefined(item.TrackType))
            {
                continue;
            }
            if (item.TrackType == DirectorPathElementTrackType.CameraPath && hasCameraPath)
            {
                continue;
            }
            Guid id = item.Id;
            if (id == Guid.Empty || !ids.Add(id))
            {
                do
                {
                    id = Guid.NewGuid();
                }
                while (!ids.Add(id));
            }
            result.Add(new DirectorPathElementTrack
            {
                Id = id,
                TrackType = item.TrackType,
                Enabled = item.TrackType == DirectorPathElementTrackType.CameraPath || item.Enabled,
                Name = item.Name?.Trim() ?? string.Empty
            });
            hasCameraPath |= item.TrackType == DirectorPathElementTrackType.CameraPath;
        }

        if (!hasCameraPath)
        {
            result.Insert(0, new DirectorPathElementTrack
            {
                TrackType = DirectorPathElementTrackType.CameraPath
            });
        }
        else
        {
            int cameraIndex = result.FindIndex(track =>
                track.TrackType == DirectorPathElementTrackType.CameraPath);
            if (cameraIndex > 0)
            {
                DirectorPathElementTrack camera = result[cameraIndex];
                result.RemoveAt(cameraIndex);
                result.Insert(0, camera);
            }
        }
        return result;
    }

    public static List<DirectorPathElementKeyframe> NormalizeKeyframes(
        IReadOnlyList<DirectorPathElementTrack> tracks,
        IEnumerable<DirectorPathElementKeyframe>? source)
    {
        Dictionary<Guid, DirectorPathElementTrack> byId = tracks.ToDictionary(track => track.Id);
        Dictionary<(Guid TrackId, int Tick), DirectorPathElementKeyframe> normalized = new();
        HashSet<Guid> keyframeIds = new();
        foreach (DirectorPathElementKeyframe? item in
            source ?? Array.Empty<DirectorPathElementKeyframe>())
        {
            if (item is null
                || !byId.TryGetValue(item.TrackId, out DirectorPathElementTrack? track)
                || track.TrackType == DirectorPathElementTrackType.CameraPath)
            {
                continue;
            }
            DirectorPathElementKeyframe frame = item.DeepClone();
            frame.Id = frame.Id == Guid.Empty ? Guid.NewGuid() : frame.Id;
            if (!keyframeIds.Add(frame.Id))
            {
                do
                {
                    frame.Id = Guid.NewGuid();
                }
                while (!keyframeIds.Add(frame.Id));
            }
            frame.TrackType = track.TrackType;
            int tick = TickAt(frame.TimeSeconds);
            frame.TimeSeconds = tick / TicksPerSecond;
            frame.InterpolationToNext = Enum.IsDefined(frame.InterpolationToNext)
                ? frame.InterpolationToNext
                : DirectorPathElementInterpolation.Linear;
            frame.Fov = float.IsFinite(frame.Fov)
                ? Math.Clamp(frame.Fov, MinFovDegrees, MaxFovDegrees)
                : 70f;
            frame.Roll = Finite(frame.Roll);
            frame.FrequencyX = Math.Clamp(Finite(frame.FrequencyX, 1f), 0f, 120f);
            frame.FrequencyY = Math.Clamp(Finite(frame.FrequencyY, 1f), 0f, 120f);
            frame.AmplitudeX = Math.Clamp(Finite(frame.AmplitudeX), -180f, 180f);
            frame.AmplitudeY = Math.Clamp(Finite(frame.AmplitudeY), -180f, 180f);
            frame.X = Finite(frame.X);
            frame.Y = Finite(frame.Y);
            frame.Z = Finite(frame.Z);
            frame.OffsetX = Finite(frame.OffsetX);
            frame.OffsetY = Finite(frame.OffsetY);
            frame.OffsetZ = Finite(frame.OffsetZ);
            frame.YawOffset = Finite(frame.YawOffset);
            frame.PitchOffset = Finite(frame.PitchOffset);
            frame.PlaybackRate = double.IsFinite(frame.PlaybackRate)
                ? Math.Clamp(frame.PlaybackRate, MinPlaybackRate, MaxPlaybackRate)
                : 1d;
            frame.BodyPart = frame.BodyPart is "body" or "root" ? frame.BodyPart : "head";
            frame.Label = frame.Label?.Trim() ?? string.Empty;
            normalized[(frame.TrackId, tick)] = frame;
        }
        return normalized.Values
            .OrderBy(frame => tracks.ToList().FindIndex(track => track.Id == frame.TrackId))
            .ThenBy(frame => frame.TimeSeconds)
            .ThenBy(frame => frame.Id)
            .ToList();
    }

    public static bool TrySample(
        CameraPath path,
        DirectorPathElementTrackType type,
        double timeSeconds,
        out DirectorPathElementKeyframe sample)
    {
        ArgumentNullException.ThrowIfNull(path);
        sample = new DirectorPathElementKeyframe { TrackType = type };
        foreach (DirectorPathElementTrack track in path.ElementTracks.Where(track =>
            track.Enabled && track.TrackType == type))
        {
            if (TrySample(path.ElementKeyframes, type, track.Id, timeSeconds, out sample))
            {
                return true;
            }
        }
        return false;
    }

    public static bool TrySample(
        IReadOnlyList<DirectorPathElementKeyframe>? keyframes,
        DirectorPathElementTrackType type,
        Guid trackId,
        double timeSeconds,
        out DirectorPathElementKeyframe sample)
    {
        sample = new DirectorPathElementKeyframe { TrackType = type };
        if (keyframes is null || !double.IsFinite(timeSeconds))
        {
            return false;
        }
        List<DirectorPathElementKeyframe> values = keyframes
            .Where(frame => frame.TrackId == trackId && frame.TrackType == type)
            .OrderBy(frame => frame.TimeSeconds)
            .ThenBy(frame => frame.Id)
            .ToList();
        if (values.Count == 0 || timeSeconds + TimeEpsilon < values[0].TimeSeconds)
        {
            return false;
        }
        int upper = values.FindIndex(frame => frame.TimeSeconds > timeSeconds + TimeEpsilon);
        if (upper <= 0)
        {
            sample = values[^1].DeepClone();
            return true;
        }
        DirectorPathElementKeyframe left = values[upper - 1];
        DirectorPathElementKeyframe right = values[upper];
        if (left.InterpolationToNext == DirectorPathElementInterpolation.Hold
            || right.TimeSeconds <= left.TimeSeconds + TimeEpsilon)
        {
            sample = left.DeepClone();
            return true;
        }
        double amount = Math.Clamp(
            (timeSeconds - left.TimeSeconds) / (right.TimeSeconds - left.TimeSeconds),
            0d,
            1d);
        if (left.InterpolationToNext == DirectorPathElementInterpolation.EaseInOut)
        {
            amount = amount * amount * (3d - 2d * amount);
        }
        sample = Interpolate(left, right, amount);
        sample.TimeSeconds = timeSeconds;
        return true;
    }

    public static bool TryUpsertKeyframeAtTime(
        CameraPath path,
        Guid trackId,
        double timeSeconds,
        CameraKeyframe? fallbackPose,
        string? defaultMarkerLabel,
        out DirectorPathElementKeyframe keyframe)
    {
        ArgumentNullException.ThrowIfNull(path);
        keyframe = new DirectorPathElementKeyframe();
        if (!double.IsFinite(timeSeconds) || timeSeconds < 0d
            || path.ElementTracks.FirstOrDefault(track => track.Id == trackId)
                is not DirectorPathElementTrack track
            || track.TrackType == DirectorPathElementTrackType.CameraPath)
        {
            return false;
        }

        int tick = TickAt(timeSeconds);
        double snappedTime = tick / TicksPerSecond;
        int existingIndex = path.ElementKeyframes.FindIndex(frame =>
            frame.TrackId == track.Id && TickAt(frame.TimeSeconds) == tick);
        if (existingIndex >= 0)
        {
            keyframe = path.ElementKeyframes[existingIndex].DeepClone();
            if (track.TrackType is DirectorPathElementTrackType.Fov
                or DirectorPathElementTrackType.Roll)
            {
                if (fallbackPose is null)
                {
                    return false;
                }
                if (track.TrackType == DirectorPathElementTrackType.Fov)
                {
                    keyframe.Fov = Math.Clamp(
                        fallbackPose.Fov,
                        MinFovDegrees,
                        MaxFovDegrees);
                }
                else
                {
                    keyframe.Roll = fallbackPose.Roll;
                }
                path.ElementKeyframes[existingIndex] = keyframe;
                Guid updatedId = keyframe.Id;
                Normalize(path);
                keyframe = path.ElementKeyframes.Single(frame => frame.Id == updatedId);
            }
            return true;
        }

        // Preserve the value already produced by this track when splitting its curve.
        DirectorPathElementKeyframe current = new();
        bool sampled = track.TrackType != DirectorPathElementTrackType.Marker
            && TrySample(path.ElementKeyframes, track.TrackType, track.Id, snappedTime,
                out current);
        if (sampled)
        {
            keyframe = current.DeepClone();
            keyframe.Id = Guid.NewGuid();
            keyframe.InterpolationToNext = OutgoingInterpolationAt(
                path.ElementKeyframes,
                track,
                snappedTime);
        }
        else
        {
            keyframe = new DirectorPathElementKeyframe
            {
                InterpolationToNext = track.TrackType == DirectorPathElementTrackType.Marker
                    ? DirectorPathElementInterpolation.Hold
                    : DirectorPathElementInterpolation.Linear,
                PlaybackRate = Math.Clamp(
                    CameraPathTiming.NormalizePlaybackSpeed(path.PlaybackSpeed),
                    MinPlaybackRate,
                    MaxPlaybackRate),
                Label = track.TrackType == DirectorPathElementTrackType.Marker
                    ? defaultMarkerLabel?.Trim() ?? string.Empty
                    : string.Empty
            };

            if (track.TrackType is DirectorPathElementTrackType.Fov
                or DirectorPathElementTrackType.Roll
                or DirectorPathElementTrackType.LookAt)
            {
                if (fallbackPose is null)
                {
                    return false;
                }

                keyframe.Fov = Math.Clamp(
                    fallbackPose.Fov,
                    MinFovDegrees,
                    MaxFovDegrees);
                keyframe.Roll = fallbackPose.Roll;
                if (track.TrackType == DirectorPathElementTrackType.LookAt)
                {
                    double yaw = fallbackPose.Yaw * Math.PI / 180d;
                    double pitch = fallbackPose.Pitch * Math.PI / 180d;
                    double horizontal = Math.Cos(pitch) * 10d;
                    keyframe.X = fallbackPose.X - Math.Sin(yaw) * horizontal;
                    keyframe.Y = fallbackPose.Y + Math.Sin(pitch) * 10d;
                    keyframe.Z = fallbackPose.Z - Math.Cos(yaw) * horizontal;
                    keyframe.Dimension = fallbackPose.Dimension;
                }
            }
        }

        keyframe.TrackId = track.Id;
        keyframe.TrackType = track.TrackType;
        keyframe.TimeSeconds = snappedTime;
        path.ElementKeyframes.Add(keyframe);
        Guid insertedId = keyframe.Id;
        Normalize(path);
        keyframe = path.ElementKeyframes.Single(frame => frame.Id == insertedId);
        return true;
    }

    public static double SamplePlaybackRate(
        CameraPath path,
        double timeSeconds,
        double fallback)
        => TrySample(path, DirectorPathElementTrackType.Speed, timeSeconds,
                out DirectorPathElementKeyframe speed)
            ? speed.PlaybackRate
            : fallback;

    public static int TickAt(double seconds)
        => Math.Max(0, (int)Math.Round(
            Math.Max(0d, double.IsFinite(seconds) ? seconds : 0d) * TicksPerSecond,
            MidpointRounding.AwayFromZero));

    private static DirectorPathElementInterpolation OutgoingInterpolationAt(
        IReadOnlyList<DirectorPathElementKeyframe> keyframes,
        DirectorPathElementTrack track,
        double timeSeconds)
        => keyframes
            .Where(frame => frame.TrackId == track.Id
                && frame.TrackType == track.TrackType
                && frame.TimeSeconds <= timeSeconds + TimeEpsilon)
            .OrderBy(frame => frame.TimeSeconds)
            .ThenBy(frame => frame.Id)
            .LastOrDefault()?.InterpolationToNext
            ?? DirectorPathElementInterpolation.Linear;

    private static DirectorPathElementKeyframe Interpolate(
        DirectorPathElementKeyframe left,
        DirectorPathElementKeyframe right,
        double amount)
    {
        DirectorPathElementKeyframe result = amount < 0.5d
            ? left.DeepClone()
            : right.DeepClone();
        result.Fov = (float)Lerp(left.Fov, right.Fov, amount);
        result.Roll = (float)LerpAngle(left.Roll, right.Roll, amount);
        result.FrequencyX = (float)Lerp(left.FrequencyX, right.FrequencyX, amount);
        result.AmplitudeX = (float)Lerp(left.AmplitudeX, right.AmplitudeX, amount);
        result.FrequencyY = (float)Lerp(left.FrequencyY, right.FrequencyY, amount);
        result.AmplitudeY = (float)Lerp(left.AmplitudeY, right.AmplitudeY, amount);
        result.X = Lerp(left.X, right.X, amount);
        result.Y = Lerp(left.Y, right.Y, amount);
        result.Z = Lerp(left.Z, right.Z, amount);
        result.OffsetX = Lerp(left.OffsetX, right.OffsetX, amount);
        result.OffsetY = Lerp(left.OffsetY, right.OffsetY, amount);
        result.OffsetZ = Lerp(left.OffsetZ, right.OffsetZ, amount);
        result.YawOffset = (float)LerpAngle(left.YawOffset, right.YawOffset, amount);
        result.PitchOffset = (float)LerpAngle(left.PitchOffset, right.PitchOffset, amount);
        result.PlaybackRate = Lerp(left.PlaybackRate, right.PlaybackRate, amount);
        return result;
    }

    private static float Finite(float value, float fallback = 0f)
        => float.IsFinite(value) ? value : fallback;

    private static double Finite(double value, double fallback = 0d)
        => double.IsFinite(value) ? value : fallback;

    private static double Lerp(double from, double to, double amount)
        => from + (to - from) * amount;

    private static double LerpAngle(double from, double to, double amount)
    {
        double delta = (to - from) % 360d;
        if (delta > 180d) delta -= 360d;
        if (delta < -180d) delta += 360d;
        return from + delta * amount;
    }
}
