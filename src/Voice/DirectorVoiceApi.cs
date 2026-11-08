using System.Collections.Concurrent;

namespace VSDirector;

/// <summary>Public client-side bridge for decoded third-party voice audio.</summary>
public sealed class DirectorVoiceApi
{
    public const int ApiVersion = 2;
    private readonly DirectorVoiceBridge bridge;

    internal DirectorVoiceApi(DirectorVoiceBridge bridge) => this.bridge = bridge;

    public int Version => ApiVersion;

    /// <summary>Whether either replay or live offscreen capture currently accepts voice frames.</summary>
    public bool IsCaptureEnabled => bridge.IsCaptureEnabled;

    /// <summary>
    /// Registers a stable speaker identity. This overload is retained for
    /// binary compatibility with integrations built against earlier API 2.
    /// </summary>
    public DirectorVoiceSource RegisterSpeaker(string ownerModId, string? speakerId = null)
        => bridge.Register(ownerModId, speakerId, speakerName: null);

    /// <summary>
    /// Registers a speaker and its human-readable display name for replay
    /// voice rows. The stable speaker id remains the routing key.
    /// </summary>
    public DirectorVoiceSource RegisterSpeaker(
        string ownerModId,
        string? speakerId,
        string? speakerName)
        => bridge.Register(ownerModId, speakerId, speakerName);
}

public sealed class DirectorVoiceSource : IDisposable
{
    private readonly DirectorVoiceBridge bridge;
    private readonly DirectorVoiceSourceState state;
    private int disposed;

    internal DirectorVoiceSource(DirectorVoiceBridge bridge, DirectorVoiceSourceState state)
    {
        this.bridge = bridge;
        this.state = state;
    }

    public string SourceId => state.SourceId;
    public DirectorVoiceStatistics Statistics => state.Statistics;

    public DirectorVoiceSubmitResult SubmitPcm16(
        ReadOnlySpan<short> samples,
        int sampleRate,
        DirectorVoicePosition position,
        float volume = 1f)
        => SubmitPcm16(
            samples,
            sampleRate,
            DirectorVoiceSpatialization.Default(position),
            timestampMilliseconds: 0,
            volume);

    /// <summary>
    /// Submits a mono PCM frame with the source attenuation used by the voice mod.
    /// The timestamp must use the director client's monotonic game clock in milliseconds.
    /// </summary>
    public DirectorVoiceSubmitResult SubmitPcm16(
        ReadOnlySpan<short> samples,
        int sampleRate,
        DirectorVoiceSpatialization spatialization,
        long timestampMilliseconds,
        float volume = 1f)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return DirectorVoiceSubmitResult.SourceDisposed;
        }
        return bridge.Submit(state, samples, sampleRate, spatialization, timestampMilliseconds, volume);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            bridge.Remove(state);
        }
    }
}

public readonly record struct DirectorVoicePosition(double X, double Y, double Z, int Dimension = 0)
{
    internal bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z)
        && Dimension is >= -1024 and <= 1024;
}

public static class DirectorVoiceCaptureRegion
{
    private const int ChunkSize = 32;

    public static bool Contains(
        DirectorVoicePosition position,
        DirectorVoicePosition center,
        int radiusChunks)
    {
        if (!position.IsValid || !center.IsValid || position.Dimension != center.Dimension)
        {
            return false;
        }

        int chunkX = (int)Math.Floor(position.X / ChunkSize);
        int chunkZ = (int)Math.Floor(position.Z / ChunkSize);
        int centerChunkX = (int)Math.Floor(center.X / ChunkSize);
        int centerChunkZ = (int)Math.Floor(center.Z / ChunkSize);
        int radius = Math.Clamp(radiusChunks, 0, 16);
        return Math.Abs(chunkX - centerChunkX) <= radius
            && Math.Abs(chunkZ - centerChunkZ) <= radius;
    }
}

/// <summary>World-space attenuation supplied by the source voice mod.</summary>
public readonly record struct DirectorVoiceSpatialization(
    DirectorVoicePosition Position,
    float MaxDistance,
    float ReferenceDistance,
    float RolloffFactor)
{
    public const float DefaultMaxDistance = 64f;
    public const float DefaultReferenceDistance = 6f;
    public const float DefaultRolloffFactor = 1.1073093f;

    /// <summary>
    /// True for a world-space voice. False for a direct, listener-relative source
    /// such as a replay narrator microphone track.
    /// </summary>
    public bool IsSpatialized { get; init; } = true;

    public static DirectorVoiceSpatialization Default(DirectorVoicePosition position)
        => new(position, DefaultMaxDistance, DefaultReferenceDistance, DefaultRolloffFactor);

    /// <summary>Creates a centered source that is unaffected by the listener pose.</summary>
    public static DirectorVoiceSpatialization Direct(DirectorVoicePosition position)
        => new(position, 1f, 1f, 0f) { IsSpatialized = false };

    internal bool IsValid => Position.IsValid
        && float.IsFinite(MaxDistance)
        && float.IsFinite(ReferenceDistance)
        && float.IsFinite(RolloffFactor)
        && MaxDistance >= 0.1f
        && ReferenceDistance >= 0.1f
        && ReferenceDistance <= MaxDistance
        && RolloffFactor is >= 0f and <= 32f;

    internal float ResolveGain(double distance)
    {
        if (!IsSpatialized)
        {
            return 1f;
        }
        if (!double.IsFinite(distance) || distance < 0d)
        {
            return 0f;
        }

        double clampedDistance = Math.Clamp(distance, ReferenceDistance, MaxDistance);
        return (float)Math.Pow(clampedDistance / ReferenceDistance, -RolloffFactor);
    }
}

public enum DirectorVoiceSubmitResult
{
    Accepted,
    DirectorUnavailable,
    Disabled,
    InvalidFormat,
    BufferFull,
    SourceDisposed
}

public readonly record struct DirectorVoiceStatistics(long AcceptedFrames, long DroppedFrames);

internal sealed class DirectorVoiceSourceState
{
    private long acceptedFrames;
    private long droppedFrames;

    internal DirectorVoiceSourceState(string sourceId, string speakerId, string speakerName)
    {
        SourceId = sourceId;
        SpeakerId = speakerId;
        SpeakerName = speakerName;
    }

    internal string SourceId { get; }
    internal string SpeakerId { get; }
    internal string SpeakerName { get; }
    internal DirectorVoiceStatistics Statistics => new(
        Interlocked.Read(ref acceptedFrames), Interlocked.Read(ref droppedFrames));
    internal void Accepted() => Interlocked.Increment(ref acceptedFrames);
    internal void Dropped() => Interlocked.Increment(ref droppedFrames);
}

internal sealed record DirectorVoicePcmFrame(
    string SourceId,
    string SpeakerId,
    string SpeakerName,
    short[] Samples,
    DirectorVoiceSpatialization Spatialization,
    long TimestampMilliseconds,
    float Volume);

/// <summary>
/// Prevents a source from placing consecutive PCM blocks on top of each other
/// when an integration supplies duplicate or backwards capture timestamps.
/// </summary>
internal sealed class DirectorReplayVoiceTimestampTimeline
{
    private readonly Dictionary<string, double> lastFrameEnds = new(StringComparer.Ordinal);
    private Guid replayId;
    private bool hasReplay;

    internal double Resolve(
        Guid currentReplayId,
        string? sourceId,
        double requestedTimeSeconds,
        int pcmSampleCount)
    {
        if (!hasReplay || replayId != currentReplayId)
        {
            hasReplay = true;
            replayId = currentReplayId;
            lastFrameEnds.Clear();
        }

        string key = string.IsNullOrWhiteSpace(sourceId) ? "unknown" : sourceId;
        double requested = double.IsFinite(requestedTimeSeconds)
            ? Math.Max(0d, requestedTimeSeconds)
            : 0d;
        double duration = Math.Max(
            1d / DirectorVoicePcm.SampleRate,
            Math.Max(0, pcmSampleCount) / (double)DirectorVoicePcm.SampleRate);
        if (lastFrameEnds.TryGetValue(key, out double previous))
        {
            requested = Math.Max(requested, previous);
        }

        lastFrameEnds[key] = requested + duration;
        return requested;
    }

    internal void Reset()
    {
        hasReplay = false;
        replayId = Guid.Empty;
        lastFrameEnds.Clear();
    }
}

internal sealed class DirectorVoiceBridge
{
    internal const int OutputSampleRate = 48000;
    private const int MaximumQueuedFrames = 256;
    private readonly ConcurrentQueue<DirectorVoicePcmFrame> queue = new();
    private readonly ConcurrentDictionary<string, DirectorVoiceSourceState> sources = new();
    private Func<DirectorConfig>? configProvider;
    private Func<bool>? readyProvider;
    private Func<bool>? replayCaptureProvider;
    private Action<DirectorVoicePcmFrame, bool, bool>? consumer;
    private int queuedFrames;

    internal void Configure(
        Func<DirectorConfig> getConfig,
        Func<bool> isReady,
        Action<DirectorVoicePcmFrame, bool, bool> consume,
        Func<bool>? captureReplay = null)
    {
        configProvider = getConfig;
        readyProvider = isReady;
        consumer = consume;
        replayCaptureProvider = captureReplay;
    }

    internal bool IsCaptureEnabled
    {
        get
        {
            DirectorConfig? config = configProvider?.Invoke();
            return (replayCaptureProvider?.Invoke() ?? config?.ReplayCaptureVoiceModAudio) == true
                || config?.OffscreenCaptureVoiceModAudio == true;
        }
    }

    internal DirectorVoiceSource Register(
        string ownerModId,
        string? speakerId,
        string? speakerName)
    {
        string owner = string.IsNullOrWhiteSpace(ownerModId) ? "unknown" : ownerModId.Trim();
        string speaker = string.IsNullOrWhiteSpace(speakerId) ? owner : speakerId.Trim();
        string name = string.IsNullOrWhiteSpace(speakerName) ? speaker : speakerName.Trim();
        string sourceId = owner + ":" + speaker + ":" + Guid.NewGuid().ToString("N");
        DirectorVoiceSourceState state = new(sourceId, speaker, name);
        sources[sourceId] = state;
        return new DirectorVoiceSource(this, state);
    }

    internal void Remove(DirectorVoiceSourceState state)
        => sources.TryRemove(state.SourceId, out _);

    internal DirectorVoiceSubmitResult Submit(
        DirectorVoiceSourceState source,
        ReadOnlySpan<short> samples,
        int sampleRate,
        DirectorVoiceSpatialization spatialization,
        long timestampMilliseconds,
        float volume)
    {
        if (!sources.ContainsKey(source.SourceId))
        {
            return DirectorVoiceSubmitResult.SourceDisposed;
        }
        if (readyProvider?.Invoke() != true)
        {
            source.Dropped();
            return DirectorVoiceSubmitResult.DirectorUnavailable;
        }
        DirectorConfig? config = configProvider?.Invoke();
        if (config is null || (!(replayCaptureProvider?.Invoke() ?? config.ReplayCaptureVoiceModAudio) && !config.OffscreenCaptureVoiceModAudio))
        {
            source.Dropped();
            return DirectorVoiceSubmitResult.Disabled;
        }
        if (sampleRate is < 8000 or > 192000
            || samples.Length == 0
            || samples.Length > sampleRate
            || !spatialization.IsValid
            || timestampMilliseconds < 0
            || !float.IsFinite(volume))
        {
            source.Dropped();
            return DirectorVoiceSubmitResult.InvalidFormat;
        }
        if (Interlocked.Increment(ref queuedFrames) > MaximumQueuedFrames)
        {
            Interlocked.Decrement(ref queuedFrames);
            source.Dropped();
            return DirectorVoiceSubmitResult.BufferFull;
        }
        queue.Enqueue(new DirectorVoicePcmFrame(
            source.SourceId,
            source.SpeakerId,
            source.SpeakerName,
            DirectorVoicePcm.ResampleTo48Khz(samples, sampleRate),
            spatialization,
            timestampMilliseconds,
            Math.Clamp(volume, 0f, 4f)));
        source.Accepted();
        return DirectorVoiceSubmitResult.Accepted;
    }

    internal void Drain()
    {
        DirectorConfig? config = configProvider?.Invoke();
        Action<DirectorVoicePcmFrame, bool, bool>? target = consumer;
        bool replayEnabled = (replayCaptureProvider?.Invoke() ?? config?.ReplayCaptureVoiceModAudio) == true;
        bool offscreenEnabled = config?.OffscreenCaptureVoiceModAudio == true;
        while (queue.TryDequeue(out DirectorVoicePcmFrame? frame))
        {
            Interlocked.Decrement(ref queuedFrames);
            if (config is not null && target is not null && (replayEnabled || offscreenEnabled))
            {
                target(frame, replayEnabled, offscreenEnabled);
            }
            else if (sources.TryGetValue(frame.SourceId, out DirectorVoiceSourceState? source))
            {
                source.Dropped();
            }
        }
    }
}

public static class DirectorVoicePcm
{
    public const int SampleRate = DirectorVoiceBridge.OutputSampleRate;

    public static short[] ResampleTo48Khz(ReadOnlySpan<short> source, int sourceRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRate, 1);
        if (sourceRate == SampleRate)
        {
            return source.ToArray();
        }
        int destinationLength = Math.Max(1, (int)Math.Round(source.Length * (double)SampleRate / sourceRate));
        short[] destination = new short[destinationLength];
        double step = sourceRate / (double)SampleRate;
        for (int index = 0; index < destination.Length; index++)
        {
            double position = index * step;
            int first = Math.Min(source.Length - 1, (int)position);
            int second = Math.Min(source.Length - 1, first + 1);
            double fraction = position - first;
            destination[index] = (short)Math.Clamp(
                Math.Round(source[first] + (source[second] - source[first]) * fraction),
                short.MinValue,
                short.MaxValue);
        }
        return destination;
    }

    public static byte[] ToLittleEndianBytes(ReadOnlySpan<short> samples)
    {
        byte[] bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static short[] FromLittleEndianBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 2)
        {
            return Array.Empty<short>();
        }
        short[] samples = new short[bytes.Length / sizeof(short)];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(short));
        return samples;
    }
}
