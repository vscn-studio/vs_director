using System.Diagnostics;

namespace VSDirector.Rendering;

internal sealed class DirectorObsSharedFrameSink : IDirectorOffscreenFrameSink, IDisposable
{
    private readonly Guid viewId;
    private readonly int width;
    private readonly int height;
    private readonly int framesPerSecond;
    private readonly DirectorObsSharedTransport transport;
    private readonly ReadbackSlot[] slots;
    private DirectorOpenGlPboApi? gl;
    private long firstMilliseconds = -1L;
    private long scheduledFrames;
    private long sequence;
    private int renderThreadId;
    private bool disposed;

    internal DirectorObsSharedFrameSink(
        Guid viewId,
        int width,
        int height,
        int framesPerSecond,
        DirectorObsSharedTransport transport,
        int pboRingSize = 3)
    {
        this.viewId = viewId;
        this.width = width;
        this.height = height;
        this.framesPerSecond = Math.Clamp(framesPerSecond, 1, 240);
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        slots = Enumerable.Range(0, Math.Clamp(pboRingSize, 3, 8))
            .Select(_ => new ReadbackSlot())
            .ToArray();
    }

    public void OnFrame(in DirectorOffscreenFrame frame)
    {
        if (disposed || frame.ViewId != viewId)
        {
            return;
        }
        if (frame.Width != width || frame.Height != height)
        {
            throw new InvalidOperationException("OBS offscreen frame dimensions changed during capture.");
        }

        EnsureGpuResources();
        DrainReadySlots();
        if (!FrameDue(frame.WorldElapsedMilliseconds))
        {
            return;
        }

        ReadbackSlot? slot = slots.FirstOrDefault(candidate => candidate.Fence == IntPtr.Zero);
        if (slot is null)
        {
            transport.ReportDroppedVideoFrame();
            return;
        }

        slot.Sequence = ++sequence;
        slot.TimestampNanoseconds = Stopwatch.GetTimestamp() * 1_000_000_000L / Stopwatch.Frequency;
        slot.Fence = gl!.SubmitRead(slot.BufferId, width, height);
        scheduledFrames++;
    }

    private bool FrameDue(long milliseconds)
    {
        if (firstMilliseconds < 0L)
        {
            firstMilliseconds = milliseconds;
        }
        long elapsed = Math.Max(0L, milliseconds - firstMilliseconds);
        long target = checked(elapsed * framesPerSecond / 1000L + 1L);
        return target > scheduledFrames;
    }

    private void EnsureGpuResources()
    {
        if (gl is not null)
        {
            if (renderThreadId != Environment.CurrentManagedThreadId)
            {
                throw new InvalidOperationException("OBS offscreen readback moved to another GL thread.");
            }
            return;
        }
        if (!DirectorOpenGlPboApi.TryCreate(out gl, out string error) || gl is null)
        {
            throw new NotSupportedException("OpenGL PBO readback is unavailable: " + error);
        }

        renderThreadId = Environment.CurrentManagedThreadId;
        foreach (ReadbackSlot slot in slots)
        {
            slot.BufferId = gl.CreatePixelPackBuffer(transport.FrameBytes);
        }
    }

    private void DrainReadySlots()
    {
        foreach (ReadbackSlot slot in slots
            .Where(candidate => candidate.Fence != IntPtr.Zero)
            .OrderBy(candidate => candidate.Sequence))
        {
            DirectorOpenGlPboApi.FenceStatus status = gl!.PollFence(slot.Fence);
            if (status == DirectorOpenGlPboApi.FenceStatus.Pending)
            {
                break;
            }
            if (status == DirectorOpenGlPboApi.FenceStatus.Failed)
            {
                ReleaseFence(slot);
                transport.ReportDroppedVideoFrame();
                continue;
            }

            IntPtr pointer = gl.MapReadOnly(slot.BufferId, transport.FrameBytes);
            if (pointer == IntPtr.Zero)
            {
                ReleaseFence(slot);
                transport.ReportDroppedVideoFrame();
                continue;
            }

            try
            {
                transport.PublishVideo(pointer, slot.TimestampNanoseconds);
            }
            finally
            {
                _ = gl.Unmap(slot.BufferId);
                ReleaseFence(slot);
            }
        }
    }

    private void ReleaseFence(ReadbackSlot slot)
    {
        gl!.DeleteFence(slot.Fence);
        slot.Fence = IntPtr.Zero;
    }

    internal void CompleteGpuCleanupOnRenderThread()
    {
        if (gl is null)
        {
            return;
        }
        if (renderThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("OBS offscreen GPU cleanup must run on the GL thread.");
        }

        foreach (ReadbackSlot slot in slots)
        {
            if (slot.Fence != IntPtr.Zero)
            {
                gl.DeleteFence(slot.Fence);
                slot.Fence = IntPtr.Zero;
            }
            gl.DeletePixelPackBuffer(slot.BufferId);
            slot.BufferId = 0;
        }
        gl = null;
    }

    public void Dispose()
    {
        disposed = true;
    }

    private sealed class ReadbackSlot
    {
        internal int BufferId;
        internal IntPtr Fence;
        internal long Sequence;
        internal long TimestampNanoseconds;
    }
}
