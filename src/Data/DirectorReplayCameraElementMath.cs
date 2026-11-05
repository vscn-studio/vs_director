namespace VSDirector.Data;

/// <summary>
/// The point and facing used by a replay TrackEntity keyframe. Angles are in
/// Vintage Story camera degrees and positions are world-space coordinates.
/// </summary>
public readonly record struct DirectorReplayTrackedTargetPose(
    double X,
    double Y,
    double Z,
    int Dimension,
    double YawDegrees,
    double PitchDegrees);

/// <summary>
/// Pure camera math for the replay elements that are not regular camera path
/// keyframes. Keeping this independent from the client Entity API makes the
/// coordinate convention testable and prevents the editor and playback from
/// slowly acquiring different interpretations of the same fields.
/// </summary>
public static class DirectorReplayCameraElementMath
{
    private const double RadiansPerDegree = Math.PI / 180d;

    public static CameraPreset EvaluateOrbit(
        DirectorReplayElementKeyframe orbit,
        float fov)
    {
        double yawDegrees = Finite(orbit.Yaw);
        double pitchDegrees = Finite(orbit.Pitch);
        double yaw = yawDegrees * RadiansPerDegree;
        double pitch = pitchDegrees * RadiansPerDegree;
        double distance = Math.Max(0.01d, Finite(orbit.Distance, 8d));
        double horizontal = Math.Cos(pitch) * distance;

        return new CameraPreset
        {
            Name = "Replay orbit",
            X = Finite(orbit.X) + Math.Sin(yaw) * horizontal,
            Y = Finite(orbit.Y) - Math.Sin(pitch) * distance,
            Z = Finite(orbit.Z) + Math.Cos(yaw) * horizontal,
            Dimension = orbit.Dimension,
            Yaw = (float)yawDegrees,
            Pitch = (float)pitchDegrees,
            // Flashback's CameraOrbit change has no roll component.  Keeping
            // old drafts' value in the data model is harmless, but it must
            // never leak into the evaluated camera pose.
            Roll = 0f,
            Fov = fov
        };
    }

    /// <summary>
    /// Evaluates the Flashback-style TrackEntity fields in Vintage Story's
    /// camera coordinate system. PositionOffset is world-space; ViewOffset is
    /// relative to the tracked facing, with positive Z behind the target.
    /// </summary>
    public static CameraPreset EvaluateTrack(
        DirectorReplayElementKeyframe tracking,
        DirectorReplayTrackedTargetPose target,
        float fov)
    {
        double targetYaw = Finite(target.YawDegrees);
        double targetPitch = Finite(target.PitchDegrees);
        double yawOffset = Finite(tracking.Yaw);
        double cameraYaw = targetYaw + yawOffset;
        double cameraPitch = targetPitch * Math.Cos(yawOffset * RadiansPerDegree)
            + Finite(tracking.Pitch);

        double yaw = cameraYaw * RadiansPerDegree;
        double pitch = cameraPitch * RadiansPerDegree;
        double cosYaw = Math.Cos(yaw);
        double sinYaw = Math.Sin(yaw);
        double cosPitch = Math.Cos(pitch);
        double sinPitch = Math.Sin(pitch);

        // Vintage Story's native camera forward is
        // (-cos(pitch) * sin(yaw), sin(pitch), -cos(pitch) * cos(yaw)).
        // ViewOffset follows Flashback's local convention: X is right, Y is
        // up, and positive Z is away from the tracked target.
        double rightX = cosYaw;
        double rightZ = -sinYaw;
        double upX = sinPitch * sinYaw;
        double upY = cosPitch;
        double upZ = sinPitch * cosYaw;
        double forwardX = -cosPitch * sinYaw;
        double forwardY = sinPitch;
        double forwardZ = -cosPitch * cosYaw;
        double viewX = Finite(tracking.ViewOffsetX);
        double viewY = Finite(tracking.ViewOffsetY);
        double viewZ = Finite(tracking.ViewOffsetZ, 3d);

        double cameraX = Finite(target.X) + Finite(tracking.OffsetX)
            + rightX * viewX + upX * viewY - forwardX * viewZ;
        double cameraY = Finite(target.Y) + Finite(tracking.OffsetY)
            + upY * viewY - forwardY * viewZ;
        double cameraZ = Finite(target.Z) + Finite(tracking.OffsetZ)
            + rightZ * viewX + upZ * viewY - forwardZ * viewZ;

        return new CameraPreset
        {
            Name = "Replay entity tracking",
            X = cameraX,
            Y = cameraY,
            Z = cameraZ,
            Dimension = target.Dimension,
            Yaw = (float)cameraYaw,
            Pitch = (float)cameraPitch,
            Roll = Finite(tracking.Roll),
            Fov = fov
        };
    }

    /// <summary>
    /// Seeds a TrackEntity keyframe from a captured camera pose. The result is
    /// equivalent to the captured pose at this instant while retaining the
    /// entity-relative semantics used during playback.
    /// </summary>
    public static void ConfigureTrackFromCamera(
        DirectorReplayElementKeyframe tracking,
        DirectorReplayTrackedTargetPose target,
        CameraPreset camera)
    {
        ArgumentNullException.ThrowIfNull(tracking);
        ArgumentNullException.ThrowIfNull(camera);

        double targetYaw = Finite(target.YawDegrees);
        double yawOffset = Finite(camera.Yaw) - targetYaw;
        double targetPitch = Finite(target.PitchDegrees);
        double pitchOffset = Finite(camera.Pitch)
            - targetPitch * Math.Cos(yawOffset * RadiansPerDegree);

        // Keep the captured displacement in the same coordinate system as
        // Flashback: PositionOffset is a deliberate world-space adjustment,
        // while ViewOffset is rotated with the tracked facing.  A captured
        // camera pose therefore starts with no world offset and stores the
        // relative displacement in the current camera basis.  Writing the
        // displacement to OffsetX/Y/Z made the camera stay at a fixed world
        // offset when the entity turned.
        tracking.OffsetX = 0d;
        tracking.OffsetY = 0d;
        tracking.OffsetZ = 0d;

        double cameraYawRadians = (targetYaw + yawOffset) * RadiansPerDegree;
        double cameraPitchRadians = (
            targetPitch * Math.Cos(yawOffset * RadiansPerDegree)
            + pitchOffset) * RadiansPerDegree;
        double cosYaw = Math.Cos(cameraYawRadians);
        double sinYaw = Math.Sin(cameraYawRadians);
        double cosPitch = Math.Cos(cameraPitchRadians);
        double sinPitch = Math.Sin(cameraPitchRadians);
        double rightX = cosYaw;
        double rightZ = -sinYaw;
        double upX = sinPitch * sinYaw;
        double upY = cosPitch;
        double upZ = sinPitch * cosYaw;
        double forwardX = -cosPitch * sinYaw;
        double forwardY = sinPitch;
        double forwardZ = -cosPitch * cosYaw;
        double relativeX = Finite(camera.X) - Finite(target.X);
        double relativeY = Finite(camera.Y) - Finite(target.Y);
        double relativeZ = Finite(camera.Z) - Finite(target.Z);
        tracking.ViewOffsetX = relativeX * rightX + relativeZ * rightZ;
        tracking.ViewOffsetY = relativeX * upX + relativeY * upY + relativeZ * upZ;
        tracking.ViewOffsetZ = -(relativeX * forwardX
            + relativeY * forwardY
            + relativeZ * forwardZ);
        tracking.Yaw = (float)yawOffset;
        tracking.Pitch = (float)pitchOffset;
        tracking.Roll = Finite(camera.Roll);
    }

    private static double Finite(double value, double fallback = 0d)
        => double.IsFinite(value) ? value : fallback;

    private static float Finite(float value, float fallback = 0f)
        => float.IsFinite(value) ? value : fallback;
}
