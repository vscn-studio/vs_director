using System.Buffers.Binary;

namespace VSDirector.Data;

/// <summary>Small peak envelopes for a visible window; never retains PCM payloads.</summary>
public static class DirectorVoiceWaveform
{
    public static Dictionary<string, float[]> Read(IReadOnlyList<DirectorReplayVoiceFrame> frames,
        IReadOnlySet<string> speakers, double start, double end, int bins, CancellationToken token = default)
    {
        bins = Math.Clamp(bins, 1, 1024);
        var result = speakers.ToDictionary(s => s, _ => new float[bins], StringComparer.Ordinal);
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
            int count = frame.Pcm16.Length / 2;
            int from = (int)Math.Clamp(Math.Ceiling((start - frame.TimeSeconds) * DirectorReplayVoiceFrame.SampleRate), 0, count);
            int to = (int)Math.Clamp(Math.Ceiling((end - frame.TimeSeconds) * DirectorReplayVoiceFrame.SampleRate), 0, count);
            for (int sample = from; sample < to; sample++)
            {
                if ((sample & 4095) == 0) token.ThrowIfCancellationRequested();
                int bin = Math.Clamp((int)((frame.TimeSeconds + sample / (double)DirectorReplayVoiceFrame.SampleRate - start) * scale), 0, bins - 1);
                float amplitude = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(frame.Pcm16.AsSpan(sample * 2))) / 32768f;
                peaks[bin] = Math.Max(peaks[bin], amplitude * frame.Volume);
            }
        }
        return result;
    }
}
