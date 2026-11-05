namespace VSDirector.Data;

public readonly record struct CameraPathTime(double TimeSeconds, bool Completed);

public static class CameraPathSampler
{
    private const double DuplicateTimeEpsilon = 0.000001d;

    public static bool TryEvaluate(CameraPath path, double timeSeconds, out CameraKeyframe frame)
    {
        ArgumentNullException.ThrowIfNull(path);
        frame = new CameraKeyframe();
        if (!double.IsFinite(timeSeconds))
        {
            return false;
        }

        IReadOnlyList<CameraKeyframe> keyframes = path.Keyframes;
        if (keyframes.Count == 0)
        {
            return false;
        }

        if (keyframes.Count == 1 || timeSeconds <= keyframes[0].TimeSeconds)
        {
            frame = keyframes[0].DeepClone();
            return true;
        }

        int lastIndex = keyframes.Count - 1;
        if (timeSeconds >= keyframes[lastIndex].TimeSeconds)
        {
            frame = keyframes[lastIndex].DeepClone();
            return true;
        }

        int low = 0;
        int high = lastIndex;
        while (low + 1 < high)
        {
            int middle = (low + high) / 2;
            if (keyframes[middle].TimeSeconds <= timeSeconds)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        CameraKeyframe current = keyframes[low];
        CameraKeyframe next = keyframes[low + 1];
        if (current.Dimension != next.Dimension)
        {
            // Dimensions are distinct render spaces. Keep the previous pose
            // until the transition keyframe instead of flying through the
            // coordinate gap between dimensions.
            frame = current.DeepClone();
            frame.TimeSeconds = timeSeconds;
            return true;
        }

        double segmentDuration = next.TimeSeconds - current.TimeSeconds;
        double holdSeconds = double.IsFinite(current.HoldSeconds)
            ? Math.Clamp(current.HoldSeconds, 0d, Math.Max(0d, segmentDuration))
            : 0d;
        if (timeSeconds <= current.TimeSeconds + holdSeconds + DuplicateTimeEpsilon)
        {
            frame = current.DeepClone();
            frame.TimeSeconds = timeSeconds;
            return true;
        }
        double transitionStart = current.TimeSeconds + holdSeconds;
        double transitionDuration = next.TimeSeconds - transitionStart;
        double amount = segmentDuration <= DuplicateTimeEpsilon
            ? 1d
            : Math.Clamp((timeSeconds - transitionStart) / Math.Max(
                DuplicateTimeEpsilon,
                transitionDuration), 0d, 1d);
        amount = CameraPathTiming.RemapSegmentProgress(
            amount,
            current.SegmentStartSpeed,
            current.SegmentEndSpeed);

        CameraPathInterpolation interpolation = current.InterpolationToNext is CameraPathInterpolation segment
            && Enum.IsDefined(segment)
                ? segment
                : path.Interpolation;
        frame = interpolation switch
        {
            CameraPathInterpolation.Linear => InterpolateLinear(current, next, amount),
            CameraPathInterpolation.EaseInOut => InterpolateLinear(current, next, SmoothStep(amount)),
            CameraPathInterpolation.Bezier => InterpolateBezier(keyframes, low, amount),
            CameraPathInterpolation.Step => InterpolateStep(current, next, amount),
            _ => InterpolateLinear(current, next, amount)
        };
        frame.TimeSeconds = timeSeconds;
        return true;
    }

    public static CameraPathTime ResolvePlaybackTime(
        double durationSeconds,
        double elapsedSeconds,
        CameraPathLoopMode loopMode,
        bool reverse)
    {
        double duration = double.IsFinite(durationSeconds) ? Math.Max(0d, durationSeconds) : 0d;
        double elapsed = double.IsFinite(elapsedSeconds) ? Math.Max(0d, elapsedSeconds) : 0d;
        if (duration <= DuplicateTimeEpsilon)
        {
            return new CameraPathTime(0d, true);
        }

        switch (loopMode)
        {
            case CameraPathLoopMode.Loop:
                {
                    double phase = elapsed % duration;
                    return new CameraPathTime(reverse ? duration - phase : phase, false);
                }

            case CameraPathLoopMode.PingPong:
                {
                    long leg = (long)Math.Floor(elapsed / duration);
                    double phase = elapsed % duration;
                    bool forward = (leg & 1L) == 0L;
                    if (reverse)
                    {
                        forward = !forward;
                    }

                    return new CameraPathTime(forward ? phase : duration - phase, false);
                }

            default:
                {
                    double clamped = Math.Min(elapsed, duration);
                    return new CameraPathTime(reverse ? duration - clamped : clamped, elapsed >= duration);
                }
        }
    }

    private static CameraKeyframe InterpolateLinear(CameraKeyframe from, CameraKeyframe to, double amount)
    {
        return new CameraKeyframe
        {
            X = Lerp(from.X, to.X, amount),
            Y = Lerp(from.Y, to.Y, amount),
            Z = Lerp(from.Z, to.Z, amount),
            Dimension = amount < 1d ? from.Dimension : to.Dimension,
            Yaw = (float)LerpAngle(from.Yaw, to.Yaw, amount),
            Pitch = (float)LerpAngle(from.Pitch, to.Pitch, amount),
            Roll = (float)LerpAngle(from.Roll, to.Roll, amount),
            Fov = (float)Math.Clamp(Lerp(from.Fov, to.Fov, amount), 10d, 170d)
        };
    }

    private static CameraKeyframe InterpolateBezier(IReadOnlyList<CameraKeyframe> frames, int index, double amount)
    {
        CameraKeyframe p1 = frames[index];
        CameraKeyframe p2 = frames[index + 1];
        if (p1.BezierHandlesEnabled || p2.BezierHandlesEnabled)
        {
            double deltaX = (p2.X - p1.X) / 3d;
            double deltaY = (p2.Y - p1.Y) / 3d;
            double deltaZ = (p2.Z - p1.Z) / 3d;
            double c1x = p1.X + (p1.BezierHandlesEnabled ? p1.BezierOutX : deltaX);
            double c1y = p1.Y + (p1.BezierHandlesEnabled ? p1.BezierOutY : deltaY);
            double c1z = p1.Z + (p1.BezierHandlesEnabled ? p1.BezierOutZ : deltaZ);
            double c2x = p2.X + (p2.BezierHandlesEnabled ? p2.BezierInX : -deltaX);
            double c2y = p2.Y + (p2.BezierHandlesEnabled ? p2.BezierInY : -deltaY);
            double c2z = p2.Z + (p2.BezierHandlesEnabled ? p2.BezierInZ : -deltaZ);
            CameraKeyframe result = InterpolateLinear(p1, p2, amount);
            result.X = CubicBezier(p1.X, c1x, c2x, p2.X, amount);
            result.Y = CubicBezier(p1.Y, c1y, c2y, p2.Y, amount);
            result.Z = CubicBezier(p1.Z, c1z, c2z, p2.Z, amount);
            return result;
        }
        CameraKeyframe p0 = index > 0 ? frames[index - 1] : p1;
        CameraKeyframe p3 = index + 2 < frames.Count ? frames[index + 2] : p2;

        return new CameraKeyframe
        {
            X = CatmullRom(p0.X, p1.X, p2.X, p3.X, amount),
            Y = CatmullRom(p0.Y, p1.Y, p2.Y, p3.Y, amount),
            Z = CatmullRom(p0.Z, p1.Z, p2.Z, p3.Z, amount),
            Dimension = amount < 1d ? p1.Dimension : p2.Dimension,
            Yaw = (float)CatmullRomAngle(p0.Yaw, p1.Yaw, p2.Yaw, p3.Yaw, amount),
            Pitch = (float)CatmullRomAngle(p0.Pitch, p1.Pitch, p2.Pitch, p3.Pitch, amount),
            Roll = (float)CatmullRomAngle(p0.Roll, p1.Roll, p2.Roll, p3.Roll, amount),
            Fov = (float)Math.Clamp(CatmullRom(p0.Fov, p1.Fov, p2.Fov, p3.Fov, amount), 10d, 170d)
        };
    }

    private static CameraKeyframe InterpolateStep(
        CameraKeyframe from,
        CameraKeyframe to,
        double amount)
    {
        CameraKeyframe result = (amount >= 1d ? to : from).DeepClone();
        return result;
    }

    private static double CubicBezier(double p0, double p1, double p2, double p3, double amount)
    {
        double inverse = 1d - amount;
        return inverse * inverse * inverse * p0
            + 3d * inverse * inverse * amount * p1
            + 3d * inverse * amount * amount * p2
            + amount * amount * amount * p3;
    }

    private static double CatmullRomAngle(double p0, double p1, double p2, double p3, double amount)
    {
        double a0 = UnwrapNear(p1, p0);
        double a2 = UnwrapNear(p1, p2);
        double a3 = UnwrapNear(a2, p3);
        return NormalizeAngle(CatmullRom(a0, p1, a2, a3, amount));
    }

    private static double CatmullRom(double p0, double p1, double p2, double p3, double amount)
    {
        double amount2 = amount * amount;
        double amount3 = amount2 * amount;
        return 0.5d * (
            2d * p1
            + (-p0 + p2) * amount
            + (2d * p0 - 5d * p1 + 4d * p2 - p3) * amount2
            + (-p0 + 3d * p1 - 3d * p2 + p3) * amount3);
    }

    private static double SmoothStep(double amount) => amount * amount * (3d - 2d * amount);

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;

    private static double LerpAngle(double from, double to, double amount)
        => NormalizeAngle(from + NormalizeAngle(to - from) * amount);

    private static double UnwrapNear(double reference, double value)
        => reference + NormalizeAngle(value - reference);

    private static double NormalizeAngle(double angle)
    {
        double wrapped = angle % 360d;
        if (wrapped > 180d)
        {
            wrapped -= 360d;
        }
        else if (wrapped < -180d)
        {
            wrapped += 360d;
        }

        return wrapped;
    }
}
