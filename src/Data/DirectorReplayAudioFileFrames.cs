using System.Buffers.Binary;

namespace VSDirector.Data;

/// <summary>Turns timeline WAV clips into the shared replay voice-frame format.</summary>
internal static class DirectorReplayAudioFileFrames
{
    private const int TargetRate = DirectorReplayVoiceFrame.SampleRate;
    private const int FrameSamples = TargetRate / 50;
    private const int MaximumFileBytes = 512 * 1024 * 1024;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> DurationCache = new(StringComparer.OrdinalIgnoreCase);

    internal static double DurationSeconds(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
        string key = Path.GetFullPath(path);
        if (DurationCache.TryGetValue(key, out double cached)) return cached;
        using FileStream stream = File.OpenRead(key);
        using BinaryReader reader = new(stream);
        if (stream.Length is < 44 or > MaximumFileBytes
            || new string(reader.ReadChars(4)) != "RIFF") return 0;
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") return 0;
        ushort format = 0, channels = 0, bits = 0;
        int byteRate = 0;
        long dataLength = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            string chunk = new(reader.ReadChars(4));
            uint length = reader.ReadUInt32();
            if (length > stream.Length - stream.Position) return 0;
            long end = stream.Position + length;
            if (chunk == "fmt " && length >= 16)
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                reader.ReadUInt32();
                byteRate = checked((int)reader.ReadUInt32());
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
            }
            else if (chunk == "data") dataLength = length;
            stream.Position = end + (length & 1);
        }
        double duration = format == 1 && channels is >= 1 and <= 2 && bits == 16 && byteRate > 0
            ? dataLength / (double)byteRate : 0;
        if (double.IsFinite(duration) && duration > 0) DurationCache[key] = duration;
        return duration;
    }

    internal static IReadOnlyList<DirectorReplayVoiceFrame> Create(DirectorReplayViewportTimeline timeline)
    {
        List<DirectorReplayVoiceFrame> frames = new();
        foreach (DirectorReplayElementTrack track in timeline.Tracks.Where(track =>
                     track.TrackType == DirectorReplayElementTrackType.VoiceAudio && timeline.IsTrackEnabled(track)))
        {
            foreach (DirectorReplayElementKeyframe keyframe in timeline.ElementKeyframes.Where(keyframe =>
                         keyframe.TrackId == track.Id && keyframe.TrackType == DirectorReplayElementTrackType.VoiceAudio
                         && !string.IsNullOrWhiteSpace(keyframe.AudioAsset)))
            {
                short[] samples;
                int sampleRate;
                try { (samples, sampleRate) = ReadWave(keyframe.AudioAsset); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidDataException or OverflowException or ArgumentException)
                {
                    continue;
                }
                short[] converted = Resample(samples, sampleRate);
                int trimStart = (int)Math.Clamp(
                    Math.Round(keyframe.AudioTrimStartSeconds * TargetRate), 0, converted.Length);
                int trimEnd = keyframe.AudioTrimEndSeconds > 0d
                    ? (int)Math.Clamp(Math.Round(keyframe.AudioTrimEndSeconds * TargetRate), trimStart, converted.Length)
                    : converted.Length;
                if (trimEnd <= trimStart) continue;
                string sourceId = string.IsNullOrWhiteSpace(track.SpeakerId)
                    ? "vsdirector:audio-" + track.Id.ToString("N")
                    : track.SpeakerId;
                string clipSourceId = sourceId + ":" + keyframe.Id.ToString("N");
                for (int offset = trimStart; offset < trimEnd; offset += FrameSamples)
                {
                    int count = Math.Min(FrameSamples, trimEnd - offset);
                    byte[] pcm = new byte[count * sizeof(short)];
                    for (int sample = 0; sample < count; sample++)
                        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 2), converted[offset + sample]);
                    frames.Add(new DirectorReplayVoiceFrame
                    {
                        TimeSeconds = keyframe.TimeSeconds + (offset - trimStart) / (double)TargetRate,
                        SourceId = clipSourceId,
                        SpeakerId = sourceId,
                        SpeakerName = track.Name,
                        Volume = keyframe.AudioClipGain,
                        Spatialized = false,
                        Pcm16 = pcm
                    });
                }
            }
        }
        return frames.OrderBy(frame => frame.TimeSeconds).ThenBy(frame => frame.SourceId, StringComparer.Ordinal).ToArray();
    }

    private static (short[] Samples, int SampleRate) ReadWave(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length is < 44 or > MaximumFileBytes) throw new InvalidDataException("WAV file size is unsupported.");
        using BinaryReader reader = new(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Only RIFF/WAVE audio is supported.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Only RIFF/WAVE audio is supported.");
        ushort format = 0, channels = 0, bits = 0;
        int sampleRate = 0;
        byte[]? data = null;
        while (stream.Position + 8 <= stream.Length)
        {
            string chunk = new(reader.ReadChars(4));
            uint length = reader.ReadUInt32();
            if (length > stream.Length - stream.Position) throw new InvalidDataException("WAV chunk is truncated.");
            long end = stream.Position + length;
            if (chunk == "fmt " && length >= 16)
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = checked((int)reader.ReadUInt32());
                reader.ReadUInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
            }
            else if (chunk == "data") data = reader.ReadBytes(checked((int)length));
            stream.Position = end + (length & 1);
        }
        if (format != 1 || bits != 16 || channels is < 1 or > 2 || sampleRate is < 8000 or > 192000 || data is null)
            throw new InvalidDataException("WAV audio must be 16-bit PCM with one or two channels.");
        int samplesCount = data.Length / (sizeof(short) * channels);
        short[] samples = new short[samplesCount];
        for (int index = 0; index < samplesCount; index++)
        {
            int left = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(index * channels * 2));
            int rightOffset = index * channels * sizeof(short) + sizeof(short);
            int value = channels == 1 ? left : (left + BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(rightOffset))) / 2;
            samples[index] = (short)value;
        }
        if (samples.Length == 0) throw new InvalidDataException("WAV file contains no audio samples.");
        return (samples, sampleRate);
    }

    private static short[] Resample(short[] source, int sampleRate)
    {
        int count = checked((int)Math.Round(source.Length * (double)TargetRate / sampleRate));
        short[] result = new short[count];
        for (int index = 0; index < count; index++)
        {
            double position = index * (double)sampleRate / TargetRate;
            int left = Math.Min((int)position, source.Length - 1);
            int right = Math.Min(left + 1, source.Length - 1);
            double fraction = position - left;
            result[index] = (short)Math.Clamp((int)Math.Round(source[left] + (source[right] - source[left]) * fraction), short.MinValue, short.MaxValue);
        }
        return result;
    }
}
