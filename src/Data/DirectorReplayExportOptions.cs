namespace VSDirector.Data;

public enum DirectorReplayExportBackend
{
    Ffmpeg,
    Obs,
    IndependentAudio
}

public sealed class DirectorReplayExportOptions
{
    public DirectorReplayExportBackend Backend { get; set; } = DirectorReplayExportBackend.Ffmpeg;

    public int FramesPerSecond { get; set; } = 30;

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public DirectorOffscreenEncoder Encoder { get; set; } = DirectorOffscreenEncoder.Auto;

    public int VideoQuality { get; set; } = 18;

    public int VideoBitrateKbps { get; set; }

    public bool IncludeAudio { get; set; } = true;

    public int AudioBitrateKbps { get; set; } = 192;

    public string SoftwarePreset { get; set; } = "medium";

    public string OutputDirectory { get; set; } = string.Empty;

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string ObsWebSocketUrl { get; set; } = "ws://127.0.0.1:4455";

    public string ObsWebSocketPassword { get; set; } = string.Empty;

    public int SettleFrames { get; set; } = 1;

    public DirectorReplayExportOptions DeepClone() => (DirectorReplayExportOptions)MemberwiseClone();
}

public static class DirectorReplayAudioExportPolicy
{
    public const int SampleRate = 48000;

    public const int Channels = 2;

    public static long SamplesThroughFrame(long frameCount, int framesPerSecond)
    {
        long frames = Math.Max(0L, frameCount);
        int fps = Math.Clamp(framesPerSecond, 1, 240);
        return checked(frames * SampleRate / fps);
    }

    public static int SamplesInFrame(long frameIndex, int framesPerSecond)
    {
        long index = Math.Max(0L, frameIndex);
        long before = SamplesThroughFrame(index, framesPerSecond);
        long after = SamplesThroughFrame(checked(index + 1L), framesPerSecond);
        return checked((int)(after - before));
    }

    public static double ResolveReplayRate(
        double currentReplaySeconds,
        double nextReplaySeconds,
        int framesPerSecond,
        double fallbackRate)
    {
        int fps = Math.Clamp(framesPerSecond, 1, 240);
        double delta = nextReplaySeconds - currentReplaySeconds;
        if (double.IsFinite(delta) && delta >= 0d)
        {
            return delta * fps;
        }

        return double.IsFinite(fallbackRate) ? Math.Max(0d, fallbackRate) : 0d;
    }
}

/// <summary>
/// Maps output frames to the logical replay timeline while honoring active
/// playback elements. Freeze presentation is resolved by the replay manager;
/// keeping this clock logical makes later Frozen=false keyframes reachable.
/// </summary>
public sealed class DirectorReplayElementExportClock
{
    private const double TimeEpsilon = 0.000001d;
    private readonly DirectorReplayViewportTimeline timeline;
    private readonly double endSeconds;
    private readonly double outputFrameStepSeconds;
    private readonly double timeScale;

    public DirectorReplayElementExportClock(
        DirectorReplayViewportTimeline timeline,
        double startSeconds,
        double endSeconds,
        double timeScale,
        int framesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (!double.IsFinite(startSeconds)
            || !double.IsFinite(endSeconds)
            || endSeconds <= startSeconds
            || !double.IsFinite(timeScale)
            || timeScale <= 0d
            || framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(endSeconds));
        }

        this.timeline = timeline.DeepClone();
        CurrentReplaySeconds = startSeconds;
        this.endSeconds = endSeconds;
        this.timeScale = timeScale;
        outputFrameStepSeconds = 1d / framesPerSecond;
    }

    public double CurrentReplaySeconds { get; private set; }

    public bool IsComplete => CurrentReplaySeconds >= endSeconds - TimeEpsilon;

    public double Advance()
        => AdvanceByOutputSeconds(outputFrameStepSeconds);

    public double AdvanceByOutputSeconds(double outputSeconds)
    {
        if (IsComplete)
        {
            CurrentReplaySeconds = endSeconds;
            return CurrentReplaySeconds;
        }

        double outputDelta = double.IsFinite(outputSeconds)
            ? Math.Clamp(outputSeconds, 0d, 1d)
            : 0d;
        double rate = DirectorReplayElementTimeline.SampleTimingRate(
            timeline,
            CurrentReplaySeconds,
            1d);
        CurrentReplaySeconds = Math.Min(
            endSeconds,
            CurrentReplaySeconds + outputDelta * timeScale * rate);
        return CurrentReplaySeconds;
    }

    public static long EstimateFrameCount(
        DirectorReplayViewportTimeline timeline,
        double startSeconds,
        double endSeconds,
        double timeScale,
        int framesPerSecond)
    {
        DirectorReplayElementExportClock clock = new(
            timeline,
            startSeconds,
            endSeconds,
            timeScale,
            framesPerSecond);
        long frames = 1L;
        long maximumFrames = checked((long)Math.Ceiling(
            (endSeconds - startSeconds) * framesPerSecond * 400d / timeScale) + 10000L);
        while (!clock.IsComplete && frames < maximumFrames)
        {
            clock.Advance();
            frames++;
        }
        if (!clock.IsComplete)
        {
            throw new InvalidOperationException("Replay element timing did not reach the export end time.");
        }
        return frames;
    }
}
