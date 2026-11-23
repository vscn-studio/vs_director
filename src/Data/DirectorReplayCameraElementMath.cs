namespace VSDirector.Data;

/// <summary>Target position and orientation in native game camera degrees.</summary>
public readonly record struct DirectorReplayTrackedTargetPose(
    double X, double Y, double Z, int Dimension, double YawDegrees, double PitchDegrees);

/// <summary>Camera placement using an orthonormal right/up/backward basis.</summary>
public static class DirectorReplayCameraElementMath
{
    private readonly record struct Vector(double X, double Y, double Z)
    {
        public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector operator *(Vector a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
        public double Dot(Vector b) => X * b.X + Y * b.Y + Z * b.Z;
    }

    private readonly record struct CameraBasis(Vector Right, Vector Up, Vector Back)
    {
        public Vector ToWorld(Vector local) => Right * local.X + Up * local.Y + Back * local.Z;
        public Vector ToLocal(Vector world) => new(Right.Dot(world), Up.Dot(world), Back.Dot(world));
    }

    private static CameraBasis Basis(double yawDegrees, double pitchDegrees)
    {
        double yaw = yawDegrees * Math.PI / 180d;
        double pitch = pitchDegrees * Math.PI / 180d;
        Vector right = new(Math.Cos(yaw), 0d, -Math.Sin(yaw));
        Vector up = new(Math.Sin(yaw) * Math.Sin(pitch), Math.Cos(pitch), Math.Cos(yaw) * Math.Sin(pitch));
        Vector back = new(Math.Sin(yaw) * Math.Cos(pitch), -Math.Sin(pitch), Math.Cos(yaw) * Math.Cos(pitch));
        return new CameraBasis(right, up, back);
    }

    public static CameraPreset EvaluateOrbit(DirectorReplayElementKeyframe orbit, float fov)
    {
        ArgumentNullException.ThrowIfNull(orbit);
        double yaw = Finite(orbit.Yaw);
        double pitch = Finite(orbit.Pitch);
        Vector center = new(Finite(orbit.X), Finite(orbit.Y), Finite(orbit.Z));
        Vector position = center + Basis(yaw, pitch).Back * Math.Max(0.01d, Finite(orbit.Distance, 8d));
        return Pose("Replay orbit", position, orbit.Dimension, yaw, pitch, Finite(orbit.Roll), fov);
    }

    public static CameraPreset EvaluateTrack(DirectorReplayElementKeyframe tracking,
        DirectorReplayTrackedTargetPose target, float fov)
    {
        ArgumentNullException.ThrowIfNull(tracking);
        double yaw = Finite(target.YawDegrees) + Finite(tracking.Yaw);
        double pitch = Finite(target.PitchDegrees) + Finite(tracking.Pitch);
        Vector anchor = new(Finite(target.X) + Finite(tracking.OffsetX),
            Finite(target.Y) + Finite(tracking.OffsetY), Finite(target.Z) + Finite(tracking.OffsetZ));
        Vector offset = new(Finite(tracking.ViewOffsetX), Finite(tracking.ViewOffsetY), Finite(tracking.ViewOffsetZ, 3d));
        return Pose("Replay entity tracking", anchor + Basis(yaw, pitch).ToWorld(offset),
            target.Dimension, yaw, pitch, Finite(tracking.Roll), fov);
    }

    /// <summary>A subject to look at: a point and the radius that should stay in frame.</summary>
    public readonly record struct LookSubject(double X, double Y, double Z, double Radius);

    /// <summary>
    /// Camera placement for tripod and over-the-shoulder tracking. Tripod keeps the
    /// keyframe's world X/Y/Z; over-the-shoulder sits behind <paramref name="anchor"/>
    /// using ViewOffsetX (side), ViewOffsetY (up) and ViewOffsetZ (back). Both turn to
    /// look at <paramref name="subject"/>. With auto-framing the FOV is chosen so the
    /// subject's radius fills TrackFrameSize of the frame height.
    /// </summary>
    public static CameraPreset EvaluateLookAt(DirectorReplayElementKeyframe tracking,
        LookSubject subject, LookSubject? anchor, int dimension, float fov)
    {
        ArgumentNullException.ThrowIfNull(tracking);
        Vector position;
        if (tracking.TrackMode == DirectorReplayTrackMode.OverShoulder && anchor is LookSubject from)
        {
            double dx = from.X - subject.X;
            double dz = from.Z - subject.Z;
            double length = Math.Sqrt(dx * dx + dz * dz);
            if (length < 0.0001d) { dx = 0d; dz = 1d; length = 1d; }
            dx /= length; dz /= length;
            // Right of the anchor when looking from the anchor toward the subject.
            double rightX = dz;
            double rightZ = -dx;
            position = new Vector(
                from.X + dx * Finite(tracking.ViewOffsetZ, 1.8d) + rightX * Finite(tracking.ViewOffsetX),
                from.Y + Finite(tracking.ViewOffsetY),
                from.Z + dz * Finite(tracking.ViewOffsetZ, 1.8d) + rightZ * Finite(tracking.ViewOffsetX));
        }
        else
        {
            position = new Vector(Finite(tracking.X), Finite(tracking.Y), Finite(tracking.Z));
        }
        double tx = subject.X - position.X;
        double ty = subject.Y - position.Y;
        double tz = subject.Z - position.Z;
        double horizontal = Math.Sqrt(tx * tx + tz * tz);
        double distance = Math.Sqrt(horizontal * horizontal + ty * ty);
        double yaw = horizontal > 0.0001d ? Math.Atan2(-tx, -tz) * 180d / Math.PI : Finite(tracking.Yaw);
        double pitch = distance > 0.0001d ? Math.Atan2(ty, Math.Max(horizontal, 0.0001d)) * 180d / Math.PI : 0d;
        float resolvedFov = fov;
        if (tracking.TrackAutoFrame && distance > 0.05d)
        {
            double size = Math.Clamp(Finite(tracking.TrackFrameSize, 0.45d), 0.05d, 1d);
            double half = Math.Atan(Math.Max(0.3d, subject.Radius) / (distance * size));
            resolvedFov = (float)Math.Clamp(half * 2d * 180d / Math.PI, 4d, 120d);
        }
        return Pose("Replay entity look-at", position, dimension, yaw, Math.Clamp(pitch, -89.5d, 89.5d),
            Finite(tracking.Roll), resolvedFov);
    }

    public static void ConfigureTrackFromCamera(DirectorReplayElementKeyframe tracking,
        DirectorReplayTrackedTargetPose target, CameraPreset camera)
    {
        ArgumentNullException.ThrowIfNull(tracking);
        ArgumentNullException.ThrowIfNull(camera);
        double yaw = Finite(camera.Yaw);
        double pitch = Finite(camera.Pitch);
        Vector displacement = new(Finite(camera.X) - Finite(target.X),
            Finite(camera.Y) - Finite(target.Y), Finite(camera.Z) - Finite(target.Z));
        Vector local = Basis(yaw, pitch).ToLocal(displacement);
        tracking.OffsetX = tracking.OffsetY = tracking.OffsetZ = 0d;
        tracking.ViewOffsetX = local.X;
        tracking.ViewOffsetY = local.Y;
        tracking.ViewOffsetZ = local.Z;
        tracking.Yaw = (float)(yaw - Finite(target.YawDegrees));
        tracking.Pitch = (float)(pitch - Finite(target.PitchDegrees));
        tracking.Roll = Finite(camera.Roll);
    }

    private static CameraPreset Pose(string name, Vector position, int dimension,
        double yaw, double pitch, float roll, float fov) => new()
        {
            Name = name, X = position.X, Y = position.Y, Z = position.Z,
            Dimension = dimension, Yaw = (float)yaw, Pitch = (float)pitch, Roll = roll, Fov = fov
        };

    private static double Finite(double value, double fallback = 0d)
        => double.IsFinite(value) ? value : fallback;
    private static float Finite(float value, float fallback = 0f)
        => float.IsFinite(value) ? value : fallback;
}
