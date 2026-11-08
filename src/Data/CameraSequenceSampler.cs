namespace VSDirector.Data;

public readonly record struct CameraSequenceTime(
    int ShotIndex,
    double ShotElapsedSeconds,
    double SequenceElapsedSeconds,
    bool Completed);

public static class CameraSequenceSampler
{
    public const double DefaultShotDurationSeconds = 5d;
    public const double MinShotDurationSeconds = 0.05d;
    public const double MaxShotDurationSeconds = 3600d;

    public static double NormalizeShotDuration(double durationSeconds)
    {
        return double.IsFinite(durationSeconds)
            ? Math.Clamp(durationSeconds, MinShotDurationSeconds, MaxShotDurationSeconds)
            : DefaultShotDurationSeconds;
    }

    public static bool TryResolve(
        CameraSequence sequence,
        double elapsedSeconds,
        out CameraSequenceTime time)
        => TryResolve(sequence, elapsedSeconds, loopOverride: null, out time);

    public static bool TryResolve(
        CameraSequence sequence,
        double elapsedSeconds,
        CameraSequenceLoopMode? loopOverride,
        out CameraSequenceTime time)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        time = default;
        if (!double.IsFinite(elapsedSeconds)
            || elapsedSeconds < 0d
            || sequence.Shots is null
            || sequence.Shots.Count == 0
            || sequence.Shots.Any(shot => shot is null))
        {
            return false;
        }

        double duration = sequence.DurationSeconds;
        if (!double.IsFinite(duration) || duration < MinShotDurationSeconds)
        {
            return false;
        }

        bool loop = loopOverride is CameraSequenceLoopMode.Loop
            || loopOverride is null && sequence.LoopMode == CameraSequenceLoopMode.Loop;
        bool completed = !loop && elapsedSeconds >= duration;
        double sequenceTime = loop
            ? elapsedSeconds % duration
            : Math.Min(elapsedSeconds, duration);

        if (completed)
        {
            int lastIndex = sequence.Shots.Count - 1;
            CameraSequenceShot last = sequence.Shots[lastIndex];
            time = new CameraSequenceTime(lastIndex, last.DurationSeconds, duration, true);
            return true;
        }

        double shotStart = 0d;
        for (int index = 0; index < sequence.Shots.Count; index++)
        {
            double shotDuration = sequence.Shots[index].DurationSeconds;
            double shotEnd = shotStart + shotDuration;
            if (sequenceTime < shotEnd || index == sequence.Shots.Count - 1)
            {
                time = new CameraSequenceTime(index, sequenceTime - shotStart, sequenceTime, false);
                return true;
            }

            shotStart = shotEnd;
        }

        return false;
    }
}
