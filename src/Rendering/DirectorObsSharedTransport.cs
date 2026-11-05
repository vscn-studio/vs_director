using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace VSDirector.Rendering;

internal sealed class DirectorObsSharedTransport : IDisposable
{
    internal const string InputKind = "vsdirector_offscreen_source";
    internal const string ChannelName = "VSDirector.Live";
    internal const int ProtocolVersion = 1;
    internal const int HeaderBytes = 512;
    internal const int VideoSlotCount = 2;
    internal const int AudioSampleRate = 48000;
    internal const int AudioChannels = 2;
    internal const int AudioCapacityFrames = AudioSampleRate * 2;

    private const int MagicOffset = 0;
    private const int VersionOffset = 8;
    private const int HeaderSizeOffset = 12;
    private const int WidthOffset = 16;
    private const int HeightOffset = 20;
    private const int StrideOffset = 24;
    private const int PixelFormatOffset = 28;
    private const int VideoSlotCountOffset = 32;
    private const int VideoFrameBytesOffset = 36;
    private const int ActiveVideoSlotOffset = 40;
    private const int ProducerStateOffset = 44;
    private const int VideoSequenceOffset = 48;
    private const int VideoTimestampOffset = 56;
    private const int ProducerHeartbeatOffset = 64;
    private const int ConsumerHeartbeatOffset = 72;
    private const int DroppedVideoFramesOffset = 80;
    private const int AudioSampleRateOffset = 88;
    private const int AudioChannelsOffset = 92;
    private const int AudioCapacityFramesOffset = 96;
    private const int AudioDataOffsetOffset = 100;
    private const int AudioWriteFrameOffset = 104;
    private const int AudioSequenceOffset = 112;
    private const int AudioProducerHeartbeatOffset = 120;
    private const int AudioDroppedFramesOffset = 128;

    private readonly object sync = new();
    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor view;
    private readonly int frameBytes;
    private readonly long audioDataOffset;
    private readonly byte[] videoCopyBuffer;
    private bool disposed;

    internal int Width { get; }

    internal int Height { get; }

    internal int FrameBytes => frameBytes;

    internal long VideoSequence
    {
        get
        {
            lock (sync)
            {
                return disposed ? 0L : view.ReadInt64(VideoSequenceOffset);
            }
        }
    }

    internal bool ConsumerConnected
    {
        get
        {
            lock (sync)
            {
                return !disposed
                    && Environment.TickCount64 - view.ReadInt64(ConsumerHeartbeatOffset) < 3000L;
            }
        }
    }

    internal DirectorObsSharedTransport(int width, int height)
    {
        Width = width;
        Height = height;
        frameBytes = checked(width * height * 4);
        audioDataOffset = checked(HeaderBytes + (long)frameBytes * VideoSlotCount);
        long capacity = checked(audioDataOffset
            + (long)AudioCapacityFrames * AudioChannels * sizeof(short));
        map = CreateMap(capacity);
        view = map.CreateViewAccessor(0L, capacity, MemoryMappedFileAccess.ReadWrite);
        videoCopyBuffer = ArrayPool<byte>.Shared.Rent(frameBytes);
        InitializeHeader();
    }

    internal void SetRecording(bool recording)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            view.Write(ProducerStateOffset, recording ? 2 : 1);
            view.Write(ProducerHeartbeatOffset, Environment.TickCount64);
            view.Flush();
        }
    }

    internal void PublishVideo(IntPtr pixels, long timestampNanoseconds)
    {
        if (pixels == IntPtr.Zero)
        {
            return;
        }

        lock (sync)
        {
            ThrowIfDisposed();
            long sequence = view.ReadInt64(VideoSequenceOffset) + 1L;
            int slot = (int)(sequence % VideoSlotCount);
            Marshal.Copy(pixels, videoCopyBuffer, 0, frameBytes);
            long offset = checked(HeaderBytes + (long)slot * frameBytes);
            view.WriteArray(offset, videoCopyBuffer, 0, frameBytes);
            view.Write(VideoTimestampOffset, timestampNanoseconds);
            view.Write(ProducerHeartbeatOffset, Environment.TickCount64);
            view.Write(ActiveVideoSlotOffset, slot);
            Thread.MemoryBarrier();
            view.Write(VideoSequenceOffset, sequence);
        }
    }

    internal void PublishAudio(short[] samples, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(samples);
        int sampleCount = checked(frameCount * AudioChannels);
        if (frameCount <= 0 || samples.Length < sampleCount)
        {
            return;
        }

        lock (sync)
        {
            ThrowIfDisposed();
            long writeFrame = view.ReadInt64(AudioWriteFrameOffset);
            int firstFrame = (int)(writeFrame % AudioCapacityFrames);
            int firstCount = Math.Min(frameCount, AudioCapacityFrames - firstFrame);
            WriteAudioPart(firstFrame, samples, 0, firstCount);
            int remainder = frameCount - firstCount;
            if (remainder > 0)
            {
                WriteAudioPart(0, samples, firstCount * AudioChannels, remainder);
            }

            view.Write(AudioProducerHeartbeatOffset, Environment.TickCount64);
            view.Write(AudioWriteFrameOffset, writeFrame + frameCount);
            view.Write(AudioSequenceOffset, view.ReadInt64(AudioSequenceOffset) + 1L);
        }
    }

    internal void ReportDroppedVideoFrame()
    {
        lock (sync)
        {
            if (!disposed)
            {
                view.Write(DroppedVideoFramesOffset, view.ReadInt64(DroppedVideoFramesOffset) + 1L);
            }
        }
    }

    internal void ReportDroppedAudioFrames(int frames)
    {
        lock (sync)
        {
            if (!disposed && frames > 0)
            {
                view.Write(AudioDroppedFramesOffset,
                    view.ReadInt64(AudioDroppedFramesOffset) + frames);
            }
        }
    }

    private void WriteAudioPart(int targetFrame, short[] samples, int sampleOffset, int frameCount)
    {
        long offset = checked(audioDataOffset
            + (long)targetFrame * AudioChannels * sizeof(short));
        view.WriteArray(offset, samples, sampleOffset, checked(frameCount * AudioChannels));
    }

    private void InitializeHeader()
    {
        byte[] magic = "VSDIRIPC"u8.ToArray();
        view.WriteArray(MagicOffset, magic, 0, magic.Length);
        view.Write(VersionOffset, ProtocolVersion);
        view.Write(HeaderSizeOffset, HeaderBytes);
        view.Write(WidthOffset, Width);
        view.Write(HeightOffset, Height);
        view.Write(StrideOffset, checked(Width * 4));
        view.Write(PixelFormatOffset, 1); // BGRA8, bottom-up OpenGL row order.
        view.Write(VideoSlotCountOffset, VideoSlotCount);
        view.Write(VideoFrameBytesOffset, frameBytes);
        view.Write(ActiveVideoSlotOffset, 0);
        view.Write(ProducerStateOffset, 1);
        view.Write(VideoSequenceOffset, 0L);
        view.Write(ProducerHeartbeatOffset, Environment.TickCount64);
        view.Write(ConsumerHeartbeatOffset, 0L);
        view.Write(AudioSampleRateOffset, AudioSampleRate);
        view.Write(AudioChannelsOffset, AudioChannels);
        view.Write(AudioCapacityFramesOffset, AudioCapacityFrames);
        view.Write(AudioDataOffsetOffset, checked((int)audioDataOffset));
        view.Write(AudioWriteFrameOffset, 0L);
        view.Write(AudioSequenceOffset, 0L);
        view.Flush();
    }

    private static MemoryMappedFile CreateMap(long capacity)
    {
        if (OperatingSystem.IsWindows())
        {
            return MemoryMappedFile.CreateOrOpen(
                "Local\\" + ChannelName,
                capacity,
                MemoryMappedFileAccess.ReadWrite);
        }

        string path = Path.Combine(Path.GetTempPath(), "vsdirector-live.ipc");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.SetLength(capacity);
        return MemoryMappedFile.CreateFromFile(
            stream,
            mapName: null,
            capacity,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: false);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                view.Write(ProducerStateOffset, 0);
                view.Write(ProducerHeartbeatOffset, Environment.TickCount64);
                view.Flush();
            }
            catch
            {
            }
            view.Dispose();
            map.Dispose();
            ArrayPool<byte>.Shared.Return(videoCopyBuffer);
        }
    }
}
