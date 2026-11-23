namespace VSDirector.Data;

/// <summary>
/// Camera yaw and pitch in the form Director's camera maths expects: pitch in -90..90
/// (positive looks up), yaw in -180..180.
///
/// The game's own camera pitch is centred on 180 degrees: looking level reads as 180,
/// and 196.7 means "16.7 degrees down, facing the other way". Capturing a free-camera
/// view stores that value as it is. The view direction is the same, so a single pose
/// plays back fine, but anything that does maths on the angles goes wrong: clamping the
/// pitch to +-90 turns it into "straight up", forward/right vectors point the wrong way,
/// and blending such a keyframe with a normal one takes the short way round the circle
/// and flips the camera over.
/// </summary>
public static class CameraViewAngles
{
    /// <summary>
    /// Returns the same view direction with pitch in -90..90 and yaw in -180..180.
    /// A pitch past straight up or down is folded back and the yaw turned by 180.
    /// Roll is unaffected: the camera's up comes from the world up plus roll.
    /// </summary>
    public static (float Yaw, float Pitch) Normalize(float yaw, float pitch)
    {
        double y = double.IsFinite(yaw) ? yaw : 0d;
        double p = Wrap(double.IsFinite(pitch) ? pitch : 0d);
        if (p > 90d)
        {
            p = 180d - p;
            y += 180d;
        }
        else if (p < -90d)
        {
            p = -180d - p;
            y += 180d;
        }
        return ((float)Wrap(y), (float)p);
    }

    /// <summary>Normalizes a pose's yaw and pitch in place.</summary>
    public static void Normalize(CameraPreset pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        (pose.Yaw, pose.Pitch) = Normalize(pose.Yaw, pose.Pitch);
    }

    /// <summary>Unit view direction for a yaw/pitch, matching the camera maths (Y is up).</summary>
    public static (double X, double Y, double Z) Forward(double yawDegrees, double pitchDegrees)
    {
        double yaw = yawDegrees * Math.PI / 180d;
        double pitch = pitchDegrees * Math.PI / 180d;
        return (-Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch));
    }

    private static double Wrap(double degrees)
    {
        double wrapped = degrees % 360d;
        if (wrapped > 180d) wrapped -= 360d;
        else if (wrapped <= -180d) wrapped += 360d;
        return wrapped;
    }
}
