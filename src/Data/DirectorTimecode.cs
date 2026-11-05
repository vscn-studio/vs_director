using System.Globalization;

namespace VSDirector.Data;

public readonly record struct DirectorTimecode(long FrameNumber, int FramesPerSecond)
{
    public static DirectorTimecode FromUtc(DateTimeOffset utc, int framesPerSecond)
    {
        int fps = NormalizeFrameRate(framesPerSecond);
        DateTimeOffset value = utc.ToUniversalTime();
        double seconds = value.TimeOfDay.TotalSeconds;
        return new DirectorTimecode((long)Math.Floor(seconds * fps), fps);
    }

    public DirectorTimecode AddFrames(long frames)
        => new(Math.Max(0L, FrameNumber + frames), NormalizeFrameRate(FramesPerSecond));

    public DirectorTimecode AtElapsed(double elapsedSeconds)
    {
        double seconds = double.IsFinite(elapsedSeconds) ? Math.Max(0d, elapsedSeconds) : 0d;
        long frames = (long)Math.Floor(seconds * NormalizeFrameRate(FramesPerSecond) + 0.0000001d);
        return AddFrames(frames);
    }

    public override string ToString()
    {
        int fps = NormalizeFrameRate(FramesPerSecond);
        long framesPerDay = 24L * 60L * 60L * fps;
        long wrapped = ((FrameNumber % framesPerDay) + framesPerDay) % framesPerDay;
        long totalSeconds = wrapped / fps;
        int frame = (int)(wrapped % fps);
        int hours = (int)(totalSeconds / 3600L);
        int minutes = (int)(totalSeconds / 60L % 60L);
        int seconds = (int)(totalSeconds % 60L);
        int frameDigits = Math.Max(2, (fps - 1).ToString(CultureInfo.InvariantCulture).Length);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:00}:{minutes:00}:{seconds:00}:{frame.ToString($"D{frameDigits}", CultureInfo.InvariantCulture)}");
    }

    public static int NormalizeFrameRate(int framesPerSecond)
        => Math.Clamp(framesPerSecond, 1, 240);
}
