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
