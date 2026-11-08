namespace VSDirector.Data;

/// <summary>Backend owns one continuous audio source per speaker, not one per packet.</summary>
public interface IDirectorReplayVoiceOutput : IDisposable
{
    void Enqueue(byte[] pcm);
    void Update(DirectorReplayVoiceFrame frame, float gain, float pitch);
    void SetPaused(bool paused);
    bool IsEmpty { get; }
}

/// <summary>Queues recorded PCM ahead of the render clock. Sample timestamps
/// determine trimming and silence, so variable render intervals never discard
/// ordinary 20ms packets. Pause/seek explicitly reset the playback timeline.</summary>
public sealed class DirectorReplayVoiceStream : IDisposable
{
    private const double LookaheadSeconds = 0.6;
    private const int MaxSpeakers = 32;
    private const int Rate = DirectorReplayVoiceFrame.SampleRate;
    private readonly IReadOnlyList<DirectorReplayVoiceFrame> frames;
    private readonly Func<IDirectorReplayVoiceOutput> create;
    private readonly Dictionary<string, StreamState> streams = new(StringComparer.Ordinal);
    private int cursor;
    private double lastTime = double.NaN;
    private bool paused;
    public int ActiveSources => streams.Count;

    public DirectorReplayVoiceStream(IReadOnlyList<DirectorReplayVoiceFrame> frames, Func<IDirectorReplayVoiceOutput> create)
    { this.frames = frames; this.create = create; }

    public void Update(double time, float pitch, bool pause, Func<DirectorReplayVoiceFrame, float> gain)
    {
        if (!double.IsFinite(time)) return;
        pitch = float.IsFinite(pitch) ? Math.Clamp(pitch, .1f, 4f) : 1f;
        if (!double.IsFinite(lastTime) || time < lastTime - .001 || time - lastTime > 1d)
        {
            Reset();
            // Native replay voice packets are normalized to at most one second.
            cursor = DirectorReplayTimeSearch.LowerBound(frames, Math.Max(0, time - 1), static f => f.TimeSeconds);
        }
        lastTime = time;
        SetPaused(pause);
        if (pause) return;
        foreach (var pair in streams.ToArray())
        {
            if (pair.Value.Output.IsEmpty && pair.Value.EndTime < time - 1)
            { pair.Value.Output.Dispose(); streams.Remove(pair.Key); }
        }
        double horizon = time + LookaheadSeconds * pitch;
        while (cursor < frames.Count && frames[cursor].TimeSeconds <= horizon)
        {
            var frame = frames[cursor++];
            int samples = frame.Pcm16.Length / 2;
            double end = frame.TimeSeconds + samples / (double)Rate;
            if (samples == 0 || end <= time) continue; // Seek/long stall only; never discard queued packets.
            string key = string.IsNullOrEmpty(frame.SourceId) ? frame.SpeakerId : frame.SourceId;
            if (!streams.TryGetValue(key, out var state))
            {
                if (streams.Count >= MaxSpeakers) continue;
                state = new StreamState(create(), time, frame);
                streams.Add(key, state);
            }
            if (state.Output.IsEmpty) state.EndTime = Math.Max(state.EndTime, time);
            int trim = Math.Clamp((int)Math.Round((state.EndTime - frame.TimeSeconds) * Rate), 0, samples);
            int silence = Math.Max(0, (int)Math.Round((frame.TimeSeconds - state.EndTime) * Rate));
            int count = samples - trim;
            if (count == 0) continue;
            byte[] pcm = new byte[checked((silence + count) * 2)];
            Buffer.BlockCopy(frame.Pcm16, trim * 2, pcm, silence * 2, count * 2);
            state.Output.Enqueue(pcm);
            state.EndTime += (silence + count) / (double)Rate;
            state.Positions.Enqueue(frame);
        }
        foreach (var state in streams.Values)
        {
            while (state.Positions.TryPeek(out var frame) && frame.TimeSeconds <= time)
                state.Current = state.Positions.Dequeue();
            state.Output.Update(state.Current, gain(state.Current), pitch);
            state.Output.SetPaused(false);
        }
    }
    public void SetPaused(bool value)
    {
        if (paused == value) return;
        paused = value;
        foreach (var state in streams.Values) state.Output.SetPaused(value);
    }
    public void Reset()
    {
        foreach (var state in streams.Values) state.Output.Dispose();
        streams.Clear(); lastTime = double.NaN; cursor = 0;
    }
    public void Dispose() => Reset();
    private sealed class StreamState(IDirectorReplayVoiceOutput output, double time, DirectorReplayVoiceFrame initial)
    {
        public IDirectorReplayVoiceOutput Output { get; } = output;
        public double EndTime = time;
        public DirectorReplayVoiceFrame Current = initial;
        public Queue<DirectorReplayVoiceFrame> Positions { get; } = new();
    }
}
