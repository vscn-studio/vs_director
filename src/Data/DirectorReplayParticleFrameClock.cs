namespace VSDirector.Data;

public readonly record struct DirectorReplayParticleFrameDelta(
    bool UsesReplayClock,
    float DeltaSeconds);

public sealed class DirectorReplayParticleFrameClock<TPool>
    where TPool : class
{
    private readonly object sync = new();
    private readonly Dictionary<TPool, int> consumedFrames = new(ReferenceEqualityComparer.Instance);
    private int frame;
    private float deltaSeconds;
    private bool deterministic;

    public void Publish(bool usesReplayClock, float frameDeltaSeconds)
    {
        lock (sync)
        {
            frame = unchecked(frame + 1);
            deterministic = usesReplayClock;
            deltaSeconds = usesReplayClock && float.IsFinite(frameDeltaSeconds)
                ? Math.Max(0f, frameDeltaSeconds)
                : 0f;
        }
    }

    public DirectorReplayParticleFrameDelta Read(float fallback)
    {
        lock (sync)
        {
            return deterministic
                ? new DirectorReplayParticleFrameDelta(true, deltaSeconds)
                : new DirectorReplayParticleFrameDelta(false, fallback);
        }
    }

    public DirectorReplayParticleFrameDelta Consume(TPool pool, float fallback)
    {
        ArgumentNullException.ThrowIfNull(pool);
        lock (sync)
        {
            if (!deterministic)
            {
                return new DirectorReplayParticleFrameDelta(false, fallback);
            }
            if (consumedFrames.TryGetValue(pool, out int consumedFrame)
                && consumedFrame == frame)
            {
                return new DirectorReplayParticleFrameDelta(true, 0f);
            }

            consumedFrames[pool] = frame;
            return new DirectorReplayParticleFrameDelta(true, deltaSeconds);
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            consumedFrames.Clear();
            deltaSeconds = 0f;
            deterministic = false;
        }
    }
}
