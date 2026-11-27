using System.Buffers.Binary;

namespace VSDirector.Data;

/// <summary>Small peak envelopes for a visible window; never retains PCM payloads.</summary>
public static class DirectorVoiceWaveform
{
    public sealed record Envelope(float[] Minimum, float[] Maximum);

    public static Envelope Slice(Envelope envelope, double envelopeStart, double envelopeEnd,
        double visibleStart, double visibleEnd)
    {
        if (envelope.Minimum.Length == 0 || envelope.Minimum.Length != envelope.Maximum.Length
            || envelopeEnd <= envelopeStart || visibleEnd <= visibleStart)
            return new([], []);

        int count = envelope.Minimum.Length;
        int from = (int)Math.Clamp(Math.Floor((visibleStart - envelopeStart) / (envelopeEnd - envelopeStart) * count), 0, count - 1);
        int to = (int)Math.Clamp(Math.Ceiling((visibleEnd - envelopeStart) / (envelopeEnd - envelopeStart) * count), from + 1, count);
        return new(envelope.Minimum[from..to], envelope.Maximum[from..to]);
    }

    public static Dictionary<string, Envelope> ReadEnvelope(IReadOnlyList<DirectorReplayVoiceFrame> frames,
        IReadOnlySet<string> speakers, double start, double end, int bins, CancellationToken token = default)
    {
        bins = Math.Clamp(bins, 1, 1024);
        var minimum = speakers.ToDictionary(s => s, _ => new float[bins], StringComparer.Ordinal);
        var maximum = speakers.ToDictionary(s => s, _ => new float[bins], StringComparer.Ordinal);
        var active = speakers.ToDictionary(s => s, _ => new bool[bins], StringComparer.Ordinal);
        if (end <= start || !double.IsFinite(start) || !double.IsFinite(end))
            return speakers.ToDictionary(s => s, _ => new Envelope(new float[bins], new float[bins]), StringComparer.Ordinal);

        double scale = bins / (end - start);
        int first = DirectorReplayTimeSearch.LowerBound(frames, Math.Max(0, start - 1), f => f.TimeSeconds);
        int last = DirectorReplayTimeSearch.LowerBound(frames, end, f => f.TimeSeconds);
        for (int i = first; i < last; i++)
        {
            token.ThrowIfCancellationRequested();
            DirectorReplayVoiceFrame frame = frames[i];
            string speaker = string.IsNullOrWhiteSpace(frame.SpeakerId) ? frame.SourceId.Trim() : frame.SpeakerId.Trim();
            if (!minimum.TryGetValue(speaker, out float[]? lower)) continue;
            float[] upper = maximum[speaker];
            bool[] hasSamples = active[speaker];
            int sampleCount = frame.Pcm16.Length / sizeof(short);
            int from = (int)Math.Clamp(Math.Ceiling((start - frame.TimeSeconds) * DirectorReplayVoiceFrame.SampleRate), 0, sampleCount);
            int to = (int)Math.Clamp(Math.Ceiling((end - frame.TimeSeconds) * DirectorReplayVoiceFrame.SampleRate), 0, sampleCount);
            float gain = Math.Max(0f, frame.Volume);
            for (int sample = from; sample < to; sample++)
            {
                if ((sample & 4095) == 0) token.ThrowIfCancellationRequested();
                int bin = Math.Clamp((int)((frame.TimeSeconds + sample / (double)DirectorReplayVoiceFrame.SampleRate - start) * scale), 0, bins - 1);
                float value = BinaryPrimitives.ReadInt16LittleEndian(frame.Pcm16.AsSpan(sample * sizeof(short))) / 32768f * gain;
                if (!hasSamples[bin])
                {
                    lower[bin] = upper[bin] = value;
                    hasSamples[bin] = true;
                }
                else
                {
                    lower[bin] = Math.Min(lower[bin], value);
                    upper[bin] = Math.Max(upper[bin], value);
                }
            }
        }

        return speakers.ToDictionary(s => s, s => new Envelope(minimum[s], maximum[s]), StringComparer.Ordinal);
    }

    public static Dictionary<string, float[]> Read(IReadOnlyList<DirectorReplayVoiceFrame> frames,
        IReadOnlySet<string> speakers, double start, double end, int bins, CancellationToken token = default)
        => ReadMagnitudes(frames, speakers, start, end, Math.Clamp(bins, 1, 1024), token, 0);

    internal static Dictionary<string, float[]> ReadAveraged(IReadOnlyList<DirectorReplayVoiceFrame> frames,
        IReadOnlySet<string> speakers, double start, double end, int bins, CancellationToken token, float reference = 32768f)
        => ReadMagnitudes(frames, speakers, start, end, Math.Clamp(bins, 1, 16384), token, Math.Max(1, reference));

    // A short loud click should not set the display scale for a whole clip.
    internal static float AmplitudeReference(IReadOnlyList<DirectorReplayVoiceFrame> frames, CancellationToken token)
    {
        const int window = DirectorReplayVoiceFrame.SampleRate / 40;
        int count = 0, total = 0;
        float reference = 1;
        foreach (var frame in frames)
        {
            token.ThrowIfCancellationRequested();
            for (int offset = 0; offset + 1 < frame.Pcm16.Length; offset += 2)
            {
                total += Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(frame.Pcm16.AsSpan(offset)));
                if (++count < window) continue;
                reference = Math.Max(reference, (float)total / count);
                total = count = 0;
            }
        }
        return count > 0 ? Math.Max(reference, (float)total / count) : reference;
    }

    private static Dictionary<string, float[]> ReadMagnitudes(IReadOnlyList<DirectorReplayVoiceFrame> frames,
        IReadOnlySet<string> speakers, double start, double end, int bins, CancellationToken token, float reference)
    {
        var result = speakers.ToDictionary(s => s, _ => new float[bins], StringComparer.Ordinal);
        var counts = reference > 0 ? speakers.ToDictionary(s => s, _ => new int[bins], StringComparer.Ordinal) : null;
        if (end <= start || !double.IsFinite(start) || !double.IsFinite(end)) return result;
        double scale = bins / (end - start);
        int first = DirectorReplayTimeSearch.LowerBound(frames, Math.Max(0, start - 1), f => f.TimeSeconds);
        int last = DirectorReplayTimeSearch.LowerBound(frames, end, f => f.TimeSeconds);
        for (int i = first; i < last; i++)
        {
            token.ThrowIfCancellationRequested();
            var frame = frames[i];
            string speaker = string.IsNullOrWhiteSpace(frame.SpeakerId) ? frame.SourceId.Trim() : frame.SpeakerId.Trim();
            if (!result.TryGetValue(speaker, out var peaks)) continue;
            var samplesPerBin = counts?[speaker];
            int count = frame.Pcm16.Length / 2;
            int from = (int)Math.Clamp(Math.Ceiling((start - frame.TimeSeconds) * DirectorReplayVoiceFrame.SampleRate), 0, count);
            int to = (int)Math.Clamp(Math.Ceiling((end - frame.TimeSeconds) * DirectorReplayVoiceFrame.SampleRate), 0, count);
            for (int sample = from; sample < to; sample++)
            {
                if ((sample & 4095) == 0) token.ThrowIfCancellationRequested();
                int bin = Math.Clamp((int)((frame.TimeSeconds + sample / (double)DirectorReplayVoiceFrame.SampleRate - start) * scale), 0, bins - 1);
                int amplitude = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(frame.Pcm16.AsSpan(sample * 2)));
                if (samplesPerBin is null)
                    peaks[bin] = Math.Max(peaks[bin], amplitude / 32768f * frame.Volume);
                else
                {
                    peaks[bin] += Math.Min(1, amplitude / reference) * frame.Volume;
                    samplesPerBin[bin]++;
                }
            }
        }
        if (counts is not null)
            foreach (var (speaker, levels) in result)
            {
                var samplesPerBin = counts[speaker];
                for (int bin = 0; bin < levels.Length; bin++)
                    if (samplesPerBin[bin] > 0) levels[bin] /= samplesPerBin[bin];
            }
        return result;
    }
}
