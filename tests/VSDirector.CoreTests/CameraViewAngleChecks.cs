using VSDirector.Data;

/// <summary>
/// Captured free-camera views use the game's 180-centred pitch. They are stored in the
/// -90..90 form so presets and keyframe blending see the real view direction.
/// </summary>
internal static class CameraViewAngleChecks
{
    public static void Run()
    {
        NormalizesToSameView();
        PresetMathSeesRealDirection();
        BlendingNoLongerFlipsOver();
    }

    private static void NormalizesToSameView()
    {
        // (input yaw, input pitch, expected yaw, expected pitch)
        (float, float, float, float)[] cases =
        {
            (76.307f, 196.699f, -103.693f, -16.699f), // free-camera keyframe from a real timeline
            (10f, 180f, -170f, 0f),                   // game "level"
            (30f, 350f, 30f, -10f),
            (30f, -100f, -150f, -80f),
            (30f, 45f, 30f, 45f),
            (30f, 90f, 30f, 90f),
            (30f, -90f, 30f, -90f),
            (30f, 270f, 30f, -90f),
            (400f, 0f, 40f, 0f),
        };
        foreach ((float yaw, float pitch, float expectedYaw, float expectedPitch) in cases)
        {
            (float y, float p) = CameraViewAngles.Normalize(yaw, pitch);
            Check.NearlyEqual(expectedYaw, y, 1e-3);
            Check.NearlyEqual(expectedPitch, p, 1e-3);
            AssertSameDirection(CameraViewAngles.Forward(yaw, pitch), CameraViewAngles.Forward(y, p), 1e-4);
        }

        (float nanYaw, float nanPitch) = CameraViewAngles.Normalize(float.NaN, float.PositiveInfinity);
        Check.NearlyEqual(0d, nanYaw);
        Check.NearlyEqual(0d, nanPitch);

        var pose = new CameraPreset { Yaw = 76.307f, Pitch = 196.699f, Roll = 12f };
        CameraViewAngles.Normalize(pose);
        Check.NearlyEqual(-16.699d, pose.Pitch, 1e-3);
        Check.NearlyEqual(12d, pose.Roll);
    }

    /// <summary>
    /// Camera move presets clamp pitch to +-89.5 and move along the view's forward. With
    /// the raw 196.7 the clamp gave 89.5 (straight up) and "dolly in" moved off-axis.
    /// </summary>
    private static void PresetMathSeesRealDirection()
    {
        const float rawYaw = -85.093f, rawPitch = 196.699f;
        var view = CameraViewAngles.Forward(rawYaw, rawPitch);

        float clampedRaw = Math.Clamp(rawPitch, -89.5f, 89.5f);
        Check.True(AngleBetween(view, CameraViewAngles.Forward(rawYaw, clampedRaw)) > 60d,
            "the raw pitch should demonstrate the old straight-up aim");

        (float yaw, float pitch) = CameraViewAngles.Normalize(rawYaw, rawPitch);
        Check.NearlyEqual(pitch, Math.Clamp(pitch, -89.5f, 89.5f));
        Check.True(view.Y < 0d, "the captured view looks slightly down");
        AssertSameDirection(view, CameraViewAngles.Forward(yaw, pitch), 1e-4);
    }

    /// <summary>
    /// Replay camera keyframes blend through CameraPathSampler (shortest-way angle lerp).
    /// A raw free-camera keyframe next to a normal one swung the camera to straight down
    /// halfway; with both in the same form the blend stays between the two views.
    /// </summary>
    private static void BlendingNoLongerFlipsOver()
    {
        const float yawA = 76.307f, pitchA = 196.699f; // free camera, raw
        const float yawB = -100f, pitchB = -10f;       // camera keyframe, -90..90 form
        double raw = MidpointDeviation(yawA, pitchA, yawB, pitchB);
        Check.True(raw > 45d, $"raw blend should show the flip (was {raw:0.0} degrees)");

        (float yaw, float pitch) = CameraViewAngles.Normalize(yawA, pitchA);
        double fixedBlend = MidpointDeviation(yaw, pitch, yawB, pitchB);
        Check.True(fixedBlend < 5d, $"normalized blend should stay between the views (was {fixedBlend:0.0} degrees)");
    }

    private static double MidpointDeviation(float yawA, float pitchA, float yawB, float pitchB)
    {
        var path = new CameraPath
        {
            Keyframes =
            {
                new CameraKeyframe { TimeSeconds = 0d, Yaw = yawA, Pitch = pitchA, Fov = 70f,
                    InterpolationToNext = CameraPathInterpolation.Linear },
                new CameraKeyframe { TimeSeconds = 2d, Yaw = yawB, Pitch = pitchB, Fov = 70f }
            }
        };
        Check.True(CameraPathSampler.TryEvaluate(path, 1d, out CameraKeyframe middle));
        var a = CameraViewAngles.Forward(yawA, pitchA);
        var b = CameraViewAngles.Forward(yawB, pitchB);
        var halfway = Normalize((a.X + b.X, a.Y + b.Y, a.Z + b.Z));
        return AngleBetween(halfway, CameraViewAngles.Forward(middle.Yaw, middle.Pitch));
    }

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        double length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return (v.X / length, v.Y / length, v.Z / length);
    }

    private static double AngleBetween((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => Math.Acos(Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1d, 1d)) * 180d / Math.PI;

    private static void AssertSameDirection((double X, double Y, double Z) expected, (double X, double Y, double Z) actual, double tolerance)
    {
        Check.NearlyEqual(expected.X, actual.X, tolerance);
        Check.NearlyEqual(expected.Y, actual.Y, tolerance);
        Check.NearlyEqual(expected.Z, actual.Z, tolerance);
    }
}
