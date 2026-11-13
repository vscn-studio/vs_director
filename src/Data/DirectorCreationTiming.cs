namespace VSDirector.Data;

public enum CreationTimeDisplay { Frames, Time }

/// <summary>The authoring clock is independent of video export and source animation sampling.</summary>
public static class DirectorCreationTiming
{
    public const int FramesPerSecond = 30;
    public const double FrameDuration = 1d / FramesPerSecond;

    // Poses, world changes and effects share this clock when a take is edited.
    public static double ClipSourceTime(CreationClip clip, double sceneTime, double sourceDuration, bool sourceLoop = false)
    {
        double duration = Math.Max(FrameDuration, sourceDuration);
        double source = clip.SourceStart + Math.Clamp(sceneTime - clip.Start, 0, clip.Duration) * clip.Speed;
        return clip.Loop || sourceLoop ? source % duration : Math.Min(source, duration);
    }

    public static double Snap(double seconds) => Math.Round(seconds * FramesPerSecond, MidpointRounding.AwayFromZero) / FramesPerSecond;

    public static double Step(double seconds, int direction) => direction > 0
        ? (Math.Floor(seconds * FramesPerSecond + 1e-7) + 1) / FramesPerSecond
        : Math.Max(0, (Math.Ceiling(seconds * FramesPerSecond - 1e-7) - 1) / FramesPerSecond);

    public static double ToDisplay(double seconds, CreationTimeDisplay display) => display == CreationTimeDisplay.Frames ? seconds * FramesPerSecond : seconds;
    public static double FromDisplay(double value, CreationTimeDisplay display) => display == CreationTimeDisplay.Frames ? value / FramesPerSecond : value;

    // Both ruler modes use whole-frame ticks. Hide minor ticks when they are too dense.
    public static (long MajorFrames, long MinorFrames) Ruler(double desiredSeconds, CreationTimeDisplay display)
    {
        double desired = Math.Max(FrameDuration, desiredSeconds) * (display == CreationTimeDisplay.Frames ? FramesPerSecond : 1);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(desired)));
        double nice = new[] { 1d, 2, 5, 10 }.First(n => n * magnitude >= desired) * magnitude;
        long major = Math.Max(1, (long)Math.Ceiling(nice * (display == CreationTimeDisplay.Frames ? 1 : FramesPerSecond) - 1e-7));
        long minor = major % 5 == 0 ? major / 5 : major % 2 == 0 ? major / 2 : major % 3 == 0 ? major / 3 : major;
        if (minor * FrameDuration < desiredSeconds / 8) minor = major;
        return (major, minor);
    }
}
