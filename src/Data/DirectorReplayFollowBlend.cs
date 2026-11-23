namespace VSDirector.Data;

/// <summary>
/// Animated follow rigs. A follow interval is a start and an end keyframe; when the
/// start keyframe's <see cref="DirectorReplayElementKeyframe.FollowBlend"/> is not Hold,
/// the numeric rig parameters move from the start values to the end values over the
/// interval (crane-ups, pull-aways, orbit swings). Hold keeps the start values, which is
/// how every follow interval behaved before.
/// </summary>
public static class DirectorReplayFollowBlend
{
    public static DirectorReplayElementKeyframe Sample(
        DirectorReplayElementKeyframe start,
        DirectorReplayElementKeyframe end,
        double timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (end is null
            || start.FollowBlend == DirectorReplayElementInterpolation.Hold
            || end.TimeSeconds <= start.TimeSeconds + 1e-6d)
        {
            return start;
        }

        double u = Math.Clamp((timeSeconds - start.TimeSeconds) / (end.TimeSeconds - start.TimeSeconds), 0d, 1d);
        double w = Weight(start.FollowBlend, u);
        DirectorReplayElementKeyframe result = start.DeepClone();
        result.FollowOffsetX = Lerp(start.FollowOffsetX, end.FollowOffsetX, w);
        result.FollowOffsetY = Lerp(start.FollowOffsetY, end.FollowOffsetY, w);
        result.FollowOffsetZ = Lerp(start.FollowOffsetZ, end.FollowOffsetZ, w);
        result.FollowDistance = Lerp(start.FollowDistance, end.FollowDistance, w);
        result.FollowHeight = Lerp(start.FollowHeight, end.FollowHeight, w);
        // Stored angles are normalized to [-180, 180); full turns use the orbit speed instead.
        result.FollowOrbitAngleDegrees = Lerp(start.FollowOrbitAngleDegrees, end.FollowOrbitAngleDegrees, w);
        result.FollowOrbitSpeedDegreesPerSecond = Lerp(start.FollowOrbitSpeedDegreesPerSecond,
            end.FollowOrbitSpeedDegreesPerSecond, w);
        result.FollowSmoothTimeSeconds = Lerp(start.FollowSmoothTimeSeconds, end.FollowSmoothTimeSeconds, w);
        result.FollowFov = (float)Lerp(start.FollowFov, end.FollowFov, w);
        result.Roll = (float)Lerp(start.Roll, end.Roll, w);
        return result;
    }

    /// <summary>Same curves as element keyframe segments (Smooth/Hermite use ease-in-out).</summary>
    public static double Weight(DirectorReplayElementInterpolation mode, double u)
        => mode switch
        {
            DirectorReplayElementInterpolation.Hold => 0d,
            DirectorReplayElementInterpolation.EaseIn => 1d - Math.Cos(u * Math.PI / 2d),
            DirectorReplayElementInterpolation.EaseOut => Math.Sin(u * Math.PI / 2d),
            DirectorReplayElementInterpolation.Linear => u,
            _ => (1d - Math.Cos(u * Math.PI)) / 2d
        };

    private static double Lerp(double a, double b, double t)
        => double.IsFinite(a) && double.IsFinite(b) ? a + (b - a) * t : a;
}
