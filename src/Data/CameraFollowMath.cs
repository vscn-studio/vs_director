namespace VSDirector.Data;

public readonly record struct CameraFollowTargetPose(
    double X,
    double Y,
    double Z,
    int Dimension,
    double YawDegrees,
    double PitchDegrees);

public readonly record struct CameraFollowPose(
    double X,
    double Y,
    double Z,
    int Dimension,
    double YawDegrees,
    double PitchDegrees,
    double RollDegrees,
    double Fov)
{
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
            Yaw = (float)YawDegrees,
            Pitch = (float)PitchDegrees,
            Roll = (float)RollDegrees,
            Fov = (float)Fov
        };
    }
}

public static class CameraFollowMath
{
    public const double DefaultDistance = 5d;
    public const double DefaultHeight = 1.5d;
    public const double DefaultSmoothTimeSeconds = 0.35d;
    public const double DefaultDelaySeconds = 1d;
    public const float DefaultFov = 70f;
    public const double MaxOffset = 1024d;
    public const double MaxDistance = 1024d;
    public const double MaxHeight = 1024d;
    public const double MaxOrbitSpeed = 720d;
    public const double MaxElevationDegrees = 85d;
    public const double MinSmoothTimeSeconds = 0.01d;
    public const double MaxSmoothTimeSeconds = 60d;
    public const double MaxDelaySeconds = 60d;

    private const double RadiansPerDegree = Math.PI / 180d;
    private const double DegreesPerRadian = 180d / Math.PI;

    public static CameraFollowPose Evaluate(
        CameraFollowRig rig,
        CameraFollowTargetPose target,
        double runtimeOrbitOffsetDegrees = 0d,
        double runtimeElevationDegrees = 0d)
    {
        ArgumentNullException.ThrowIfNull(rig);

        double yaw = NormalizeDegrees(target.YawDegrees);
        double pitch = Math.Clamp(FiniteOr(target.PitchDegrees, 0d), -89.9d, 89.9d);
        Basis basis = CreateBasis(yaw, pitch);
        double offsetX = NormalizeOffset(rig.OffsetX);
        double offsetY = NormalizeOffset(rig.OffsetY);
        double offsetZ = NormalizeOffset(rig.OffsetZ);
        double fov = NormalizeFov(rig.Fov);

        if (rig.ViewMode == CameraFollowViewMode.FirstPerson)
        {
            return new CameraFollowPose(
                target.X + basis.RightX * offsetX + basis.UpX * offsetY + basis.ForwardX * offsetZ,
                target.Y + basis.RightY * offsetX + basis.UpY * offsetY + basis.ForwardY * offsetZ,
                target.Z + basis.RightZ * offsetX + basis.UpZ * offsetY + basis.ForwardZ * offsetZ,
                target.Dimension,
                yaw,
                pitch,
                0d,
                fov);
        }

        double orbitYaw = NormalizeDegrees(
            yaw + NormalizeDegrees(rig.OrbitAngleDegrees) + FiniteOr(runtimeOrbitOffsetDegrees, 0d));
        Basis orbitBasis = CreateBasis(orbitYaw, 0d);
        double distance = NormalizeDistance(rig.Distance);
        double height = NormalizeHeight(rig.Height);
        double elevation = Math.Clamp(FiniteOr(runtimeElevationDegrees, 0d),
            -MaxElevationDegrees, MaxElevationDegrees) * RadiansPerDegree;
        double horizontalDistance = distance * Math.Cos(elevation);
        double verticalDistance = distance * Math.Sin(elevation);
        double cameraX = target.X
            + orbitBasis.RightX * offsetX
            + orbitBasis.ForwardX * (offsetZ - horizontalDistance);
        double cameraY = target.Y + offsetY + height + verticalDistance;
        double cameraZ = target.Z
            + orbitBasis.RightZ * offsetX
            + orbitBasis.ForwardZ * (offsetZ - horizontalDistance);
        (double lookYaw, double lookPitch) = LookAt(
            cameraX,
            cameraY,
            cameraZ,
            target.X,
            target.Y,
            target.Z);

        return new CameraFollowPose(
            cameraX,
            cameraY,
            cameraZ,
            target.Dimension,
            lookYaw,
            lookPitch,
            0d,
            fov);
    }

    public static CameraFollowPose Interpolate(CameraFollowPose from, CameraFollowPose to, double amount)
    {
        double t = Math.Clamp(FiniteOr(amount, 0d), 0d, 1d);
        if (from.Dimension != to.Dimension)
        {
            return t < 1d ? from : to;
        }

        return new CameraFollowPose(
            Lerp(from.X, to.X, t),
            Lerp(from.Y, to.Y, t),
            Lerp(from.Z, to.Z, t),
            to.Dimension,
            LerpAngle(from.YawDegrees, to.YawDegrees, t),
            LerpAngle(from.PitchDegrees, to.PitchDegrees, t),
            LerpAngle(from.RollDegrees, to.RollDegrees, t),
            Lerp(from.Fov, to.Fov, t));
    }

    public static CameraFollowPose Smooth(
        CameraFollowPose current,
        CameraFollowPose target,
        double deltaTimeSeconds,
        double smoothTimeSeconds)
    {
        double dt = Math.Clamp(FiniteOr(deltaTimeSeconds, 0d), 0d, 1d);
        double smoothTime = NormalizeSmoothTime(smoothTimeSeconds);
        double amount = 1d - Math.Exp(-dt / smoothTime);
        return Interpolate(current, target, amount);
    }

    public static double NormalizeDegrees(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        double normalized = value % 360d;
        if (normalized >= 180d)
        {
            normalized -= 360d;
        }
        else if (normalized < -180d)
        {
            normalized += 360d;
        }

        return normalized;
    }

    public static double NormalizeOffset(double value)
        => Math.Clamp(FiniteOr(value, 0d), -MaxOffset, MaxOffset);

    public static double NormalizeDistance(double value)
        => Math.Clamp(FiniteOr(value, DefaultDistance), 0d, MaxDistance);

    public static double NormalizeHeight(double value)
        => Math.Clamp(FiniteOr(value, DefaultHeight), -MaxHeight, MaxHeight);

    public static double NormalizeOrbitSpeed(double value)
        => Math.Clamp(FiniteOr(value, 0d), -MaxOrbitSpeed, MaxOrbitSpeed);

    public static double NormalizeSmoothTime(double value)
        => Math.Clamp(FiniteOr(value, DefaultSmoothTimeSeconds), MinSmoothTimeSeconds, MaxSmoothTimeSeconds);

    public static double NormalizeDelay(double value)
        => Math.Clamp(FiniteOr(value, DefaultDelaySeconds), 0d, MaxDelaySeconds);

    public static float NormalizeFov(float value)
        => float.IsFinite(value) && value > 0f ? Math.Clamp(value, 10f, 170f) : DefaultFov;

    private static (double Yaw, double Pitch) LookAt(
        double fromX,
        double fromY,
        double fromZ,
        double toX,
        double toY,
        double toZ)
    {
        double dx = toX - fromX;
        double dy = toY - fromY;
        double dz = toZ - fromZ;
        double horizontal = Math.Sqrt(dx * dx + dz * dz);
        if (horizontal <= 0.0000001d && Math.Abs(dy) <= 0.0000001d)
        {
            return (0d, 0d);
        }

        return (
            NormalizeDegrees(Math.Atan2(-dx, -dz) * DegreesPerRadian),
            Math.Atan2(dy, horizontal) * DegreesPerRadian);
    }

    private static Basis CreateBasis(double yawDegrees, double pitchDegrees)
    {
        double yaw = yawDegrees * RadiansPerDegree;
        double pitch = pitchDegrees * RadiansPerDegree;
        double cosYaw = Math.Cos(yaw);
        double sinYaw = Math.Sin(yaw);
        double cosPitch = Math.Cos(pitch);
        double sinPitch = Math.Sin(pitch);
        return new Basis(
            cosYaw,
            0d,
            -sinYaw,
            -cosPitch * sinYaw,
            sinPitch,
            -cosPitch * cosYaw,
            sinPitch * sinYaw,
            cosPitch,
            sinPitch * cosYaw);
    }

    private static double Lerp(double from, double to, double amount)
        => from + (to - from) * amount;

    private static double LerpAngle(double from, double to, double amount)
        => NormalizeDegrees(from + NormalizeDegrees(to - from) * amount);

    private static double FiniteOr(double value, double fallback)
        => double.IsFinite(value) ? value : fallback;

    private readonly record struct Basis(
        double RightX,
        double RightY,
        double RightZ,
        double ForwardX,
        double ForwardY,
        double ForwardZ,
        double UpX,
        double UpY,
        double UpZ);
}

public sealed class CameraFollowPoseHistory
{
    private readonly List<TimedPose> samples = new();

    public int Count => samples.Count;

    public void Clear() => samples.Clear();

    public void Add(double timeSeconds, CameraFollowPose pose, double retentionSeconds)
    {
        if (!double.IsFinite(timeSeconds))
        {
            return;
        }

        if (samples.Count > 0 && timeSeconds < samples[^1].TimeSeconds)
        {
            samples.Clear();
        }

        samples.Add(new TimedPose(timeSeconds, pose));
        double minimumTime = timeSeconds - Math.Max(1d, CameraFollowMath.NormalizeDelay(retentionSeconds) + 1d);
        int removeCount = 0;
        while (removeCount < samples.Count - 2 && samples[removeCount + 1].TimeSeconds < minimumTime)
        {
            removeCount++;
        }

        if (removeCount > 0)
        {
            samples.RemoveRange(0, removeCount);
        }
    }

    public bool TrySample(double timeSeconds, out CameraFollowPose pose)
    {
        if (samples.Count == 0 || !double.IsFinite(timeSeconds))
        {
            pose = default;
            return false;
        }

        if (timeSeconds <= samples[0].TimeSeconds)
        {
            pose = samples[0].Pose;
            return true;
        }

        for (int index = 1; index < samples.Count; index++)
        {
            TimedPose right = samples[index];
            if (timeSeconds > right.TimeSeconds)
            {
                continue;
            }

            TimedPose left = samples[index - 1];
            double duration = right.TimeSeconds - left.TimeSeconds;
            double amount = duration <= 0.0000001d ? 1d : (timeSeconds - left.TimeSeconds) / duration;
            pose = CameraFollowMath.Interpolate(left.Pose, right.Pose, amount);
            return true;
        }

        pose = samples[^1].Pose;
        return true;
    }

    private readonly record struct TimedPose(double TimeSeconds, CameraFollowPose Pose);
}
