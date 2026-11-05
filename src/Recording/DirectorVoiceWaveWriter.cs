using System.Text;
using VSDirector.Rendering;

namespace VSDirector.Recording;

/// <summary>Small realtime stereo WAV sink used by FFmpeg-only offscreen views.</summary>
internal sealed class DirectorVoiceWaveWriter : IDisposable
{
    private const int SampleRate = DirectorVoicePcm.SampleRate;
    private const long MixLeadFrames = SampleRate / 10L;
    private const long TimestampJitterFrames = SampleRate / 8L;
    private readonly object sync = new();
    private readonly FileStream stream;
    private readonly BinaryWriter writer;
    private readonly Dictionary<string, long> nextFrameBySource = new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, StereoMixSample> pendingFrames = new();
    private long dataBytes;
    private long writtenFrames;
    private long latestFrame;
    private bool disposed;

    internal DirectorVoiceWaveWriter(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        WriteHeader(0L);
    }

    internal string Path { get; }

    internal void Append(DirectorVoicePcmFrame frame, DirectorOffscreenCameraPose pose, double elapsedSeconds)
    {
        lock (sync)
        {
            if (disposed || frame.Samples.Length == 0)
            {
                return;
            }
            long targetFrame = ResolveTargetFrame(elapsedSeconds);
            long startFrame = ResolveStartFrame(frame.SourceId, targetFrame, frame.Samples.Length);
            float pan = ResolvePan(frame, pose);
            float distanceGain = ResolveDistanceGain(frame, pose);
            for (int index = 0; index < frame.Samples.Length; index++)
            {
                float value = frame.Samples[index] / (float)short.MaxValue * frame.Volume * distanceGain;
                AddFrame(startFrame + index, value * (1f - pan), value * (1f + pan));
            }
            long endFrame = checked(startFrame + frame.Samples.Length);
            nextFrameBySource[frame.SourceId] = endFrame;
            latestFrame = Math.Max(latestFrame, endFrame);
            FlushTo(Math.Max(writtenFrames, latestFrame - MixLeadFrames));
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            FlushTo(Math.Max(writtenFrames, latestFrame));
            stream.Position = 0L;
            WriteHeader(dataBytes);
            writer.Flush();
            writer.Dispose();
            stream.Dispose();
        }
    }

    private void WriteSilence(long frames)
    {
        while (frames > 0)
        {
            long chunk = Math.Min(frames, SampleRate * 10L);
            for (long index = 0; index < chunk; index++)
            {
                writer.Write((short)0);
                writer.Write((short)0);
            }
            frames -= chunk;
            dataBytes = checked(dataBytes + chunk * 2L * sizeof(short));
        }
    }

    private long ResolveTargetFrame(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds))
        {
            return writtenFrames;
        }

        return Math.Max(0L, (long)Math.Round(elapsedSeconds * SampleRate));
    }

    private long ResolveStartFrame(string sourceId, long targetFrame, int sampleCount)
    {
        long start = targetFrame;
        if (nextFrameBySource.TryGetValue(sourceId, out long expectedFrame))
        {
            // Capture callbacks can arrive in bursts on a client tick. Keep a
            // speaking source sample-continuous unless it has genuinely been silent.
            start = Math.Abs(targetFrame - expectedFrame) <= TimestampJitterFrames
                ? expectedFrame
                : Math.Max(targetFrame, expectedFrame);
        }

        long minimumFrame = Math.Max(writtenFrames, 0L);
        if (start < minimumFrame)
        {
            start = minimumFrame;
        }
        return sampleCount > 0 ? start : minimumFrame;
    }

    private void AddFrame(long frameIndex, float left, float right)
    {
        if (pendingFrames.TryGetValue(frameIndex, out StereoMixSample existing))
        {
            pendingFrames[frameIndex] = new StereoMixSample(existing.Left + left, existing.Right + right);
            return;
        }

        pendingFrames.Add(frameIndex, new StereoMixSample(left, right));
    }

    private void FlushTo(long exclusiveFrame)
    {
        while (writtenFrames < exclusiveFrame)
        {
            if (pendingFrames.Remove(writtenFrames, out StereoMixSample mixed))
            {
                writer.Write(ToPcm16(mixed.Left));
                writer.Write(ToPcm16(mixed.Right));
                dataBytes = checked(dataBytes + 2L * sizeof(short));
                writtenFrames++;
                continue;
            }

            long silenceUntil = exclusiveFrame;
            if (pendingFrames.Count > 0)
            {
                long nextMixedFrame = pendingFrames.First().Key;
                if (nextMixedFrame > writtenFrames)
                {
                    silenceUntil = Math.Min(silenceUntil, nextMixedFrame);
                }
            }
            if (silenceUntil <= writtenFrames)
            {
                silenceUntil = writtenFrames + 1L;
            }
            WriteSilence(silenceUntil - writtenFrames);
            writtenFrames = silenceUntil;
        }
    }

    private void WriteHeader(long bytes)
    {
        writer.Write("RIFF"u8.ToArray());
        writer.Write(checked((uint)(36L + bytes)));
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write((uint)SampleRate);
        writer.Write((uint)(SampleRate * 2 * sizeof(short)));
        writer.Write((ushort)(2 * sizeof(short)));
        writer.Write((ushort)16);
        writer.Write("data"u8.ToArray());
        writer.Write(checked((uint)bytes));
    }

    private static float ResolveDistanceGain(DirectorVoicePcmFrame frame, DirectorOffscreenCameraPose pose)
    {
        DirectorVoiceSpatialization spatialization = frame.Spatialization;
        DirectorVoicePosition position = spatialization.Position;
        if (!spatialization.IsSpatialized)
        {
            return 1f;
        }
        if (position.Dimension != pose.Dimension)
        {
            return 0f;
        }
        double distance = Math.Sqrt(
            Math.Pow(position.X - pose.X, 2d)
            + Math.Pow(position.Y - pose.Y, 2d)
            + Math.Pow(position.Z - pose.Z, 2d));
        return spatialization.ResolveGain(distance);
    }

    private static float ResolvePan(DirectorVoicePcmFrame frame, DirectorOffscreenCameraPose pose)
    {
        if (!frame.Spatialization.IsSpatialized)
        {
            return 0f;
        }
        DirectorVoicePosition position = frame.Spatialization.Position;
        float yaw = pose.Yaw * Vintagestory.API.MathTools.GameMath.DEG2RAD;
        double dx = position.X - pose.X;
        double dz = position.Z - pose.Z;
        double distance = Math.Sqrt(dx * dx + dz * dz);
        return (float)Math.Clamp((dx * Math.Cos(yaw) - dz * Math.Sin(yaw)) / Math.Max(1d, distance), -0.75d, 0.75d);
    }

    private static short ToPcm16(float value)
        => (short)Math.Round(Math.Clamp(value, -1f, 1f) * short.MaxValue);

    private readonly record struct StereoMixSample(float Left, float Right);
}
