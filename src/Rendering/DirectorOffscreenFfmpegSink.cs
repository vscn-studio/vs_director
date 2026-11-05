using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Vintagestory.API.Client;
using VSDirector.Data;
using VSDirector.Recording;

namespace VSDirector.Rendering;

public enum DirectorOffscreenVideoEncoder
{
    Auto,
    NvidiaNvenc,
    IntelQuickSync,
    AmdAmf,
    Libx264
}

public sealed class DirectorOffscreenFfmpegOptions
{
    public const int DefaultPboRingSize = 4;

    public const int DefaultWriterQueueCapacity = 8;

    public required Guid ViewId { get; init; }

    public required string OutputPath { get; init; }

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public int OutputWidth { get; init; }

    public int OutputHeight { get; init; }

    public int FramesPerSecond { get; init; } = 60;

    public string FfmpegPath { get; init; } = "ffmpeg";

    public DirectorOffscreenVideoEncoder Encoder { get; init; } = DirectorOffscreenVideoEncoder.Auto;

    public bool AllowSoftwareFallback { get; init; } = true;

    public int VideoQuality { get; init; } = 18;

    public int VideoBitrateKbps { get; init; }

    public string SoftwarePreset { get; init; } = "medium";

    public int PboRingSize { get; init; } = DefaultPboRingSize;

    public int WriterQueueCapacity { get; init; } = DefaultWriterQueueCapacity;

    public int MaximumCatchUpFrames { get; init; } = 8;

    public int MaximumStopPaddingSeconds { get; init; } = 2;

    public int StopTimeoutMilliseconds { get; init; } = 15000;
    /// <summary>Offline export must reject missing frames rather than pad them.</summary>
    public bool RequireExactFrames { get; init; }
}

public readonly record struct DirectorOffscreenFfmpegStatistics(
    long InputFrames,
    long RateLimitedInputFrames,
    long ScheduledFrames,
    long SubmittedReadbacks,
    long EncodedFrames,
    long DuplicatedFrames,
    long GpuBackpressureEvents,
    long WriterBackpressureEvents,
    long ReadbackFailures,
    long DroppedFrames);

public readonly record struct DirectorOffscreenFfmpegResult(
    bool Success,
    string OutputPath,
    DirectorOffscreenVideoEncoder Encoder,
    string Codec,
    string Error,
    DirectorOffscreenFfmpegStatistics Statistics);

/// <summary>
/// Per-view FFmpeg output sink. The render thread performs only non-waiting
/// fence polling, PBO submission, and buffer mapping. CPU copies and FFmpeg
/// stdin writes run on a bounded single-reader worker queue.
/// </summary>
public sealed class DirectorOffscreenFfmpegSink : IDirectorOffscreenFrameSink, IDisposable
{
    private static readonly ConcurrentDictionary<string, bool> EncoderProbeCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ICoreClientAPI capi;
    private readonly DirectorOffscreenFfmpegOptions options;
    private readonly object sync = new();
    private readonly Counters counters = new();
    private SinkState state;
    private DirectorOpenGlPboApi? gl;
    private ReadbackSlot[] slots = Array.Empty<ReadbackSlot>();
    private Channel<ReadbackPacket>? channel;
    private Process? process;
    private Task? writerTask;
    private Task<string>? stderrTask;
    private Exception? writerFailure;
    private DirectorOffscreenVideoEncoder selectedEncoder;
    private string selectedCodec = string.Empty;
    private long firstWorldMilliseconds = -1L;
    private long scheduledFrames;
    private long submissionSequence;
    private long lastWorldMilliseconds = -1L;
    private long finalFrameTarget;
    private int gpuThreadId;
    private bool gpuCleanupQueued;
    private DirectorOffscreenFfmpegResult? stopResult;

    public bool IsRunning
    {
        get
        {
            lock (sync)
            {
                return state == SinkState.Running;
            }
        }
    }

    public DirectorOffscreenVideoEncoder SelectedEncoder
    {
        get
        {
            lock (sync)
            {
                return selectedEncoder;
            }
        }
    }

    public string SelectedCodec
    {
        get
        {
            lock (sync)
            {
                return selectedCodec;
            }
        }
    }

    public string FailureMessage
        => Volatile.Read(ref writerFailure)?.GetBaseException().Message ?? string.Empty;

    public DirectorOffscreenFfmpegStatistics Statistics => counters.Snapshot();

    public DirectorOffscreenFfmpegSink(
        ICoreClientAPI capi,
        DirectorOffscreenFfmpegOptions options)
    {
        this.capi = capi ?? throw new ArgumentNullException(nameof(capi));
        this.options = Normalize(options);
    }

    /// <summary>
    /// Starts FFmpeg and its writer thread. Encoder probing can launch short
    /// FFmpeg processes, so call this before enabling the offscreen renderer.
    /// </summary>
    public void Start(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (state != SinkState.Created)
            {
                throw new InvalidOperationException("The FFmpeg sink can only be started once.");
            }
            state = SinkState.Starting;
        }

        Process? created = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
            string executable = DirectorFfmpegLocator.ResolveRequired(options.FfmpegPath);
            (DirectorOffscreenVideoEncoder encoder, string codec) = SelectEncoder(
                executable,
                options,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ProcessStartInfo startInfo = BuildStartInfo(executable, codec, encoder, options);
            created = new Process { StartInfo = startInfo };
            if (!created.Start())
            {
                throw new InvalidOperationException("FFmpeg could not be started.");
            }

            Channel<ReadbackPacket> createdChannel = Channel.CreateBounded<ReadbackPacket>(
                new BoundedChannelOptions(options.WriterQueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                });
            Task<string> errors = created.StandardError.ReadToEndAsync();
            Process writerProcess = created;
            Task writer = Task.Run(() => WriterLoopAsync(
                writerProcess.StandardInput.BaseStream,
                createdChannel.Reader));
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                process = created;
                channel = createdChannel;
                stderrTask = errors;
                writerTask = writer;
                selectedEncoder = encoder;
                selectedCodec = codec;
                state = SinkState.Running;
            }
            created = null;
        }
        catch (Exception exception)
        {
            if (created is not null)
            {
                try
                {
                    if (!created.HasExited)
                    {
                        created.Kill(entireProcessTree: true);
                        created.WaitForExit(2000);
                    }
                }
                catch
                {
                }
                created.Dispose();
            }
            lock (sync)
            {
                writerFailure = exception;
                state = SinkState.Failed;
            }
            throw;
        }
    }

    public void OnFrame(in DirectorOffscreenFrame frame)
    {
        if (frame.ViewId != options.ViewId)
        {
            return;
        }

        Interlocked.Increment(ref counters.InputFrames);
        lock (sync)
        {
            if (state != SinkState.Running || Volatile.Read(ref writerFailure) is not null)
            {
                return;
            }
            if (frame.Width != options.Width || frame.Height != options.Height)
            {
                writerFailure = new InvalidOperationException(
                    $"Offscreen frame size {frame.Width}x{frame.Height} does not match "
                    + $"FFmpeg input {options.Width}x{options.Height}.");
                return;
            }

            try
            {
                lastWorldMilliseconds = Math.Max(lastWorldMilliseconds, frame.WorldElapsedMilliseconds);
                EnsureGpuResources();
                DrainReadySlots();
                long due = FramesDue(frame.WorldElapsedMilliseconds);
                if (due <= 0)
                {
                    Interlocked.Increment(ref counters.RateLimitedInputFrames);
                    return;
                }

                ReadbackSlot? slot = slots.FirstOrDefault(candidate => candidate.State == SlotState.Free);
                if (slot is null)
                {
                    Interlocked.Increment(ref counters.GpuBackpressureEvents);
                    return;
                }

                int repeats = (int)Math.Min(due, options.MaximumCatchUpFrames);
                SubmitReadback(slot, repeats);
                scheduledFrames += repeats;
                Interlocked.Add(ref counters.ScheduledFrames, repeats);
                Interlocked.Increment(ref counters.SubmittedReadbacks);
                if (repeats > 1)
                {
                    Interlocked.Add(ref counters.DuplicatedFrames, repeats - 1L);
                }
            }
            catch (Exception exception)
            {
                writerFailure = exception;
                capi.Logger.Error("VS Director: offscreen video capture failed: {0}", exception);
            }
        }
    }

    public DirectorOffscreenFfmpegResult Stop()
    {
        Channel<ReadbackPacket>? activeChannel;
        Process? activeProcess;
        Task? activeWriter;
        Task<string>? activeErrors;
        List<ReadbackPacket> forcedPackets;
        bool cleanupOnCallingThread;
        bool gpuCleanupHandled = false;
        lock (sync)
        {
            if (stopResult is DirectorOffscreenFfmpegResult completed)
            {
                return completed;
            }
            if (state == SinkState.Created)
            {
                state = SinkState.Stopped;
                DirectorOffscreenFfmpegResult neverStarted = BuildResult(
                    success: false,
                    "The offscreen video sink was not started.");
                stopResult = neverStarted;
                return neverStarted;
            }
            if (state == SinkState.Starting)
            {
                throw new InvalidOperationException("The FFmpeg sink is still starting.");
            }
            if (state == SinkState.Stopping)
            {
                throw new InvalidOperationException("The FFmpeg sink is already stopping.");
            }

            state = SinkState.Stopping;
            Volatile.Write(ref finalFrameTarget, FramesTargetedAtStop());
            cleanupOnCallingThread = gpuThreadId == 0
                || gpuThreadId == Environment.CurrentManagedThreadId;
            forcedPackets = PrepareSlotsForStop(cleanupOnCallingThread);
            activeChannel = channel;
            activeProcess = process;
            activeWriter = writerTask;
            activeErrors = stderrTask;
        }

        string error = string.Empty;
        try
        {
            if (activeChannel is not null)
            {
                foreach (ReadbackPacket packet in forcedPackets)
                {
                    try
                    {
                        activeChannel.Writer.WriteAsync(packet).AsTask().GetAwaiter().GetResult();
                    }
                    catch
                    {
                        packet.Slot.SetState(SlotState.CopyComplete);
                        Interlocked.Add(ref counters.DroppedFrames, packet.RepeatCount);
                        throw;
                    }
                }
                activeChannel.Writer.TryComplete();
            }

            bool writerFinished = activeWriter is null
                || activeWriter.Wait(options.StopTimeoutMilliseconds);
            if (!writerFinished && activeProcess is { HasExited: false })
            {
                activeProcess.Kill(entireProcessTree: true);
                activeProcess.WaitForExit(2000);
                writerFinished = activeWriter?.Wait(2000) ?? true;
                error = "FFmpeg writer did not stop within the configured timeout.";
            }
            if (activeWriter?.IsFaulted == true)
            {
                writerFailure ??= activeWriter.Exception?.GetBaseException();
            }

            if (writerFinished)
            {
                CleanupGpuResources(cleanupOnCallingThread);
                gpuCleanupHandled = true;
            }
            else
            {
                QueueGpuCleanupAfterWriter(activeWriter);
                gpuCleanupHandled = true;
            }

            if (activeProcess is not null && !activeProcess.HasExited)
            {
                if (!activeProcess.WaitForExit(options.StopTimeoutMilliseconds))
                {
                    activeProcess.Kill(entireProcessTree: true);
                    activeProcess.WaitForExit(2000);
                    error = error.Length == 0
                        ? "FFmpeg did not finalize within the configured timeout."
                        : error;
                }
            }

            string diagnostics = activeErrors?.GetAwaiter().GetResult() ?? string.Empty;
            if (writerFailure is not null && error.Length == 0)
            {
                error = writerFailure.GetBaseException().Message;
            }
            if (options.RequireExactFrames && error.Length == 0)
                error = DirectorReplayExportIntegrity.Validate(
                    Interlocked.Read(ref counters.ScheduledFrames), Interlocked.Read(ref counters.EncodedFrames),
                    Interlocked.Read(ref counters.DroppedFrames), Interlocked.Read(ref counters.ReadbackFailures), Interlocked.Read(ref counters.DuplicatedFrames));
            if (activeProcess is not null && activeProcess.ExitCode != 0 && error.Length == 0)
            {
                error = LastDiagnostic(diagnostics, activeProcess.ExitCode);
            }
            if (!File.Exists(options.OutputPath)
                || new FileInfo(options.OutputPath).Length == 0)
            {
                error = error.Length == 0 ? "FFmpeg did not produce a video file." : error;
            }
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            try
            {
                if (activeProcess is { HasExited: false })
                {
                    activeProcess.Kill(entireProcessTree: true);
                    activeProcess.WaitForExit(2000);
                }
            }
            catch
            {
            }
            if (!gpuCleanupHandled)
            {
                if (activeWriter?.IsCompleted != false)
                {
                    CleanupGpuResources(cleanupOnCallingThread);
                }
                else
                {
                    QueueGpuCleanupAfterWriter(activeWriter);
                }
                gpuCleanupHandled = true;
            }
        }
        finally
        {
            activeProcess?.Dispose();
        }

        lock (sync)
        {
            process = null;
            channel = null;
            writerTask = null;
            stderrTask = null;
            state = SinkState.Stopped;
            DirectorOffscreenFfmpegResult result = BuildResult(error.Length == 0, error);
            stopResult = result;
            return result;
        }
    }

    public void Dispose()
    {
        SinkState current;
        lock (sync)
        {
            current = state;
        }
        if (current is SinkState.Running or SinkState.Failed)
        {
            _ = Stop();
        }
    }

    internal void CompleteGpuCleanupOnRenderThread()
        => CleanupGpuResourcesOnCurrentThread();

    private void EnsureGpuResources()
    {
        if (slots.Length != 0)
        {
            if (gpuThreadId != Environment.CurrentManagedThreadId)
            {
                throw new InvalidOperationException("Offscreen PBO capture moved to a different GL thread.");
            }
            return;
        }
        if (!DirectorOpenGlPboApi.TryCreate(out gl, out string error) || gl is null)
        {
            throw new NotSupportedException("OpenGL PBO readback is unavailable: " + error);
        }

        gpuThreadId = Environment.CurrentManagedThreadId;
        int bytes = checked(options.Width * options.Height * 4);
        List<ReadbackSlot> created = new(options.PboRingSize);
        try
        {
            for (int index = 0; index < options.PboRingSize; index++)
            {
                created.Add(new ReadbackSlot(gl.CreatePixelPackBuffer(bytes), bytes));
            }
            slots = created.ToArray();
        }
        catch
        {
            foreach (ReadbackSlot slot in created)
            {
                gl.DeletePixelPackBuffer(slot.BufferId);
            }
            throw;
        }
    }

    private long FramesDue(long worldMilliseconds)
    {
        if (firstWorldMilliseconds < 0L)
        {
            firstWorldMilliseconds = worldMilliseconds;
        }
        long elapsed = Math.Max(0L, worldMilliseconds - firstWorldMilliseconds);
        long target = checked(elapsed * options.FramesPerSecond / 1000L + 1L);
        return Math.Max(0L, target - scheduledFrames);
    }

    private long FramesTargetedAtStop()
    {
        if (firstWorldMilliseconds < 0L || lastWorldMilliseconds < firstWorldMilliseconds)
        {
            return 0L;
        }

        long elapsed = lastWorldMilliseconds - firstWorldMilliseconds;
        return checked(elapsed * options.FramesPerSecond / 1000L + 1L);
    }

    private void SubmitReadback(ReadbackSlot slot, int repeats)
    {
        slot.Sequence = ++submissionSequence;
        slot.RepeatCount = repeats;
        slot.BackpressureReported = false;
        slot.Fence = gl!.SubmitRead(slot.BufferId, options.Width, options.Height);
        slot.SetState(SlotState.PendingGpu);
    }

    private void DrainReadySlots()
    {
        foreach (ReadbackSlot slot in slots)
        {
            if (slot.State == SlotState.CopyComplete)
            {
                FinalizeMappedSlot(slot);
            }
        }

        foreach (ReadbackSlot slot in slots
            .Where(candidate => candidate.State != SlotState.Free)
            .OrderBy(candidate => candidate.Sequence))
        {
            SlotState slotState = slot.State;
            if (slotState is SlotState.CopyInProgress or SlotState.CopyComplete)
            {
                continue;
            }
            if (slotState == SlotState.PendingGpu)
            {
                DirectorOpenGlPboApi.FenceStatus fenceStatus = gl!.PollFence(slot.Fence);
                if (fenceStatus == DirectorOpenGlPboApi.FenceStatus.Pending)
                {
                    break;
                }
                if (fenceStatus == DirectorOpenGlPboApi.FenceStatus.Failed)
                {
                    DropSlot(slot, readbackFailure: true);
                    continue;
                }

                IntPtr pointer = gl.MapReadOnly(slot.BufferId, slot.ByteCount);
                gl.DeleteFence(slot.Fence);
                slot.Fence = IntPtr.Zero;
                if (pointer == IntPtr.Zero)
                {
                    DropSlot(slot, readbackFailure: true);
                    continue;
                }
                slot.MappedPointer = pointer;
                slot.SetState(SlotState.MappedAwaitingQueue);
            }

            if (!TryQueueMappedSlot(slot))
            {
                if (!slot.BackpressureReported)
                {
                    slot.BackpressureReported = true;
                    Interlocked.Increment(ref counters.WriterBackpressureEvents);
                }
                break;
            }
        }
    }

    private bool TryQueueMappedSlot(ReadbackSlot slot)
    {
        Channel<ReadbackPacket>? activeChannel = channel;
        if (activeChannel is null || slot.State != SlotState.MappedAwaitingQueue)
        {
            return false;
        }

        ReadbackPacket packet = new(slot, slot.MappedPointer, slot.ByteCount, slot.RepeatCount);
        if (!activeChannel.Writer.TryWrite(packet))
        {
            return false;
        }
        slot.SetState(SlotState.CopyInProgress);
        return true;
    }

    private async Task WriterLoopAsync(Stream stdin, ChannelReader<ReadbackPacket> reader)
    {
        byte[]? retainedFrame = null;
        try
        {
            await foreach (ReadbackPacket packet in reader.ReadAllAsync().ConfigureAwait(false))
            {
                byte[]? bytes = ArrayPool<byte>.Shared.Rent(packet.ByteCount);
                try
                {
                    Marshal.Copy(packet.Pointer, bytes, 0, packet.ByteCount);
                    packet.Slot.SetState(SlotState.CopyComplete);
                    for (int repeat = 0; repeat < packet.RepeatCount; repeat++)
                    {
                        await stdin.WriteAsync(bytes.AsMemory(0, packet.ByteCount)).ConfigureAwait(false);
                        Interlocked.Increment(ref counters.EncodedFrames);
                    }

                    if (retainedFrame is not null)
                    {
                        ArrayPool<byte>.Shared.Return(retainedFrame);
                    }
                    retainedFrame = bytes;
                    bytes = null;
                }
                finally
                {
                    packet.Slot.SetState(SlotState.CopyComplete);
                    if (bytes is not null)
                    {
                        ArrayPool<byte>.Shared.Return(bytes);
                    }
                }
            }

            await PadFinalFramesAsync(stdin, retainedFrame).ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            writerFailure = exception;
            while (reader.TryRead(out ReadbackPacket? abandoned))
            {
                abandoned.Slot.SetState(SlotState.CopyComplete);
                Interlocked.Add(ref counters.DroppedFrames, abandoned.RepeatCount);
            }
        }
        finally
        {
            if (retainedFrame is not null)
            {
                ArrayPool<byte>.Shared.Return(retainedFrame);
            }
            try
            {
                stdin.Close();
            }
            catch
            {
            }
        }
    }

    private async Task PadFinalFramesAsync(Stream stdin, byte[]? retainedFrame)
    {
        long target = Volatile.Read(ref finalFrameTarget);
        long encoded = Interlocked.Read(ref counters.EncodedFrames);
        if (target <= encoded)
        {
            return;
        }
        if (retainedFrame is null)
        {
            throw new InvalidOperationException(
                "Offscreen capture stopped before its first GPU readback completed.");
        }

        long missing = target - encoded;
        if (options.RequireExactFrames)
            throw new InvalidOperationException($"Exact replay export lost {missing} frame(s); padding is disabled.");
        int maximumPadding = checked(options.MaximumStopPaddingSeconds * options.FramesPerSecond);
        int padding = DirectorFrameSchedule.FramesDue(target, encoded, maximumPadding);
        for (int index = 0; index < padding; index++)
        {
            await stdin.WriteAsync(retainedFrame.AsMemory(0, checked(options.Width * options.Height * 4)))
                .ConfigureAwait(false);
            Interlocked.Increment(ref counters.EncodedFrames);
            Interlocked.Increment(ref counters.DuplicatedFrames);
        }

        if (padding < missing)
        {
            throw new InvalidOperationException(
                $"Offscreen output fell {missing - padding} frame(s) behind its capture clock; "
                + $"stop padding is limited to {maximumPadding} frame(s).");
        }
    }

    private List<ReadbackPacket> PrepareSlotsForStop(bool canUseGl)
    {
        List<ReadbackPacket> packets = new();
        if (slots.Length == 0)
        {
            return packets;
        }

        if (canUseGl)
        {
            DrainReadySlots();
        }
        foreach (ReadbackSlot slot in slots
            .Where(candidate => candidate.State != SlotState.Free)
            .OrderBy(candidate => candidate.Sequence))
        {
            SlotState slotState = slot.State;
            if (slotState == SlotState.PendingGpu)
            {
                var fence = !canUseGl ? DirectorOpenGlPboApi.FenceStatus.Failed
                    : options.RequireExactFrames ? gl!.WaitFence(slot.Fence, options.StopTimeoutMilliseconds) : gl!.PollFence(slot.Fence);
                if (fence == DirectorOpenGlPboApi.FenceStatus.Ready)
                {
                    IntPtr pointer = gl!.MapReadOnly(slot.BufferId, slot.ByteCount);
                    gl.DeleteFence(slot.Fence);
                    slot.Fence = IntPtr.Zero;
                    if (pointer != IntPtr.Zero)
                    {
                        slot.MappedPointer = pointer;
                        slot.SetState(SlotState.MappedAwaitingQueue);
                        slotState = SlotState.MappedAwaitingQueue;
                    }
                }
                if (slotState == SlotState.PendingGpu)
                {
                    Interlocked.Add(ref counters.DroppedFrames, slot.RepeatCount);
                    if (options.RequireExactFrames) writerFailure ??= new InvalidOperationException("Final replay GPU readback did not complete; exact export is incomplete.");
                    continue;
                }
            }

            if (slotState == SlotState.MappedAwaitingQueue)
            {
                packets.Add(new ReadbackPacket(
                    slot,
                    slot.MappedPointer,
                    slot.ByteCount,
                    slot.RepeatCount));
                slot.SetState(SlotState.CopyInProgress);
            }
        }
        return packets;
    }

    private void FinalizeMappedSlot(ReadbackSlot slot)
    {
        if (slot.MappedPointer != IntPtr.Zero)
        {
            if (!gl!.Unmap(slot.BufferId))
            {
                Interlocked.Increment(ref counters.ReadbackFailures);
                if (options.RequireExactFrames) writerFailure ??= new InvalidOperationException("Replay GPU readback memory was corrupted.");
            }
        }
        slot.Reset();
    }

    private void DropSlot(ReadbackSlot slot, bool readbackFailure)
    {
        if (slot.Fence != IntPtr.Zero)
        {
            gl!.DeleteFence(slot.Fence);
        }
        if (slot.MappedPointer != IntPtr.Zero)
        {
            gl!.Unmap(slot.BufferId);
        }
        if (readbackFailure)
        {
            Interlocked.Increment(ref counters.ReadbackFailures);
        }
        if (options.RequireExactFrames)
            writerFailure ??= new InvalidOperationException("A replay GPU readback failed; exact export cannot continue.");
        Interlocked.Add(ref counters.DroppedFrames, slot.RepeatCount);
        slot.Reset();
    }

    private void CleanupGpuResources(bool canUseGl)
    {
        if (slots.Length == 0)
        {
            return;
        }
        if (!canUseGl || gpuThreadId != Environment.CurrentManagedThreadId)
        {
            QueueGpuCleanupAfterWriter(Task.CompletedTask);
            return;
        }
        CleanupGpuResourcesOnCurrentThread();
    }

    private void CleanupGpuResourcesOnCurrentThread()
    {
        lock (sync)
        {
            if (slots.Length == 0 || gl is null)
            {
                return;
            }
            foreach (ReadbackSlot slot in slots)
            {
                try
                {
                    if (slot.Fence != IntPtr.Zero)
                    {
                        gl.DeleteFence(slot.Fence);
                    }
                    if (slot.MappedPointer != IntPtr.Zero)
                    {
                        gl.Unmap(slot.BufferId);
                    }
                    gl.DeletePixelPackBuffer(slot.BufferId);
                }
                catch (Exception exception)
                {
                    capi.Logger.Warning("VS Director: failed to release an FFmpeg PBO: {0}", exception.Message);
                }
            }
            slots = Array.Empty<ReadbackSlot>();
            gpuThreadId = 0;
            gpuCleanupQueued = false;
        }
    }

    private void QueueGpuCleanupAfterWriter(Task? waitForWriter)
    {
        lock (sync)
        {
            if (gpuCleanupQueued || slots.Length == 0)
            {
                return;
            }
            gpuCleanupQueued = true;
        }

        _ = (waitForWriter ?? Task.CompletedTask).ContinueWith(
            _ => capi.Event.EnqueueMainThreadTask(
                CleanupGpuResourcesOnCurrentThread,
                "vsdirector-offscreen-ffmpeg-pbo-cleanup"),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private DirectorOffscreenFfmpegResult BuildResult(bool success, string error)
        => new(
            success,
            options.OutputPath,
            selectedEncoder,
            selectedCodec,
            error,
            counters.Snapshot());

    private static DirectorOffscreenFfmpegOptions Normalize(DirectorOffscreenFfmpegOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.ViewId == Guid.Empty)
        {
            throw new ArgumentException("An FFmpeg sink requires a view id.", nameof(source));
        }
        string outputPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(source.OutputPath)
                ? throw new ArgumentException("An FFmpeg output path is required.", nameof(source))
                : source.OutputPath.Trim());
        if (source.Width < 16 || source.Width > DirectorOffscreenMultiViewRenderer.MaximumDimension
            || source.Height < 16 || source.Height > DirectorOffscreenMultiViewRenderer.MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                "FFmpeg input dimensions must be between 16 and 8192 pixels.");
        }
        int outputWidth = source.OutputWidth > 0 ? source.OutputWidth : source.Width;
        int outputHeight = source.OutputHeight > 0 ? source.OutputHeight : source.Height;
        if (outputWidth < 16 || outputWidth > DirectorOffscreenMultiViewRenderer.MaximumDimension
            || outputHeight < 16 || outputHeight > DirectorOffscreenMultiViewRenderer.MaximumDimension
            || (outputWidth & 1) != 0
            || (outputHeight & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                "FFmpeg output dimensions must be even values between 16 and 8192 pixels.");
        }

        return new DirectorOffscreenFfmpegOptions
        {
            ViewId = source.ViewId,
            OutputPath = outputPath,
            Width = source.Width,
            Height = source.Height,
            OutputWidth = outputWidth,
            OutputHeight = outputHeight,
            FramesPerSecond = Math.Clamp(source.FramesPerSecond, 1, 240),
            FfmpegPath = string.IsNullOrWhiteSpace(source.FfmpegPath) ? "ffmpeg" : source.FfmpegPath.Trim(),
            Encoder = Enum.IsDefined(source.Encoder) ? source.Encoder : DirectorOffscreenVideoEncoder.Auto,
            AllowSoftwareFallback = source.AllowSoftwareFallback,
            VideoQuality = Math.Clamp(source.VideoQuality, 0, 51),
            VideoBitrateKbps = Math.Clamp(source.VideoBitrateKbps, 0, 1000000),
            SoftwarePreset = NormalizeSoftwarePreset(source.SoftwarePreset),
            PboRingSize = Math.Clamp(source.PboRingSize, 3, 16),
            WriterQueueCapacity = Math.Clamp(source.WriterQueueCapacity, 1, 64),
            MaximumCatchUpFrames = Math.Clamp(source.MaximumCatchUpFrames, 1, 120),
            MaximumStopPaddingSeconds = Math.Clamp(source.MaximumStopPaddingSeconds, 1, 10),
            StopTimeoutMilliseconds = Math.Clamp(source.StopTimeoutMilliseconds, 2000, 120000),
            RequireExactFrames = source.RequireExactFrames
        };
    }

    private static string NormalizeSoftwarePreset(string? value)
    {
        string preset = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return preset is "ultrafast" or "superfast" or "veryfast" or "faster" or "fast"
            or "medium" or "slow" or "slower" or "veryslow"
                ? preset
                : "medium";
    }

    private static (DirectorOffscreenVideoEncoder Encoder, string Codec) SelectEncoder(
        string executable,
        DirectorOffscreenFfmpegOptions options,
        CancellationToken cancellationToken)
    {
        DirectorOffscreenVideoEncoder[] candidates = options.Encoder switch
        {
            DirectorOffscreenVideoEncoder.Auto => new[]
            {
                DirectorOffscreenVideoEncoder.NvidiaNvenc,
                DirectorOffscreenVideoEncoder.IntelQuickSync,
                DirectorOffscreenVideoEncoder.AmdAmf,
                DirectorOffscreenVideoEncoder.Libx264
            },
            DirectorOffscreenVideoEncoder.Libx264 => new[] { DirectorOffscreenVideoEncoder.Libx264 },
            _ when options.AllowSoftwareFallback => new[]
            {
                options.Encoder,
                DirectorOffscreenVideoEncoder.Libx264
            },
            _ => new[] { options.Encoder }
        };
        foreach (DirectorOffscreenVideoEncoder candidate in candidates.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string codec = CodecName(candidate);
            string cacheKey = Path.GetFullPath(executable) + "|" + codec;
            if (EncoderProbeCache.GetOrAdd(
                    cacheKey,
                    _ => ProbeEncoder(executable, codec, cancellationToken)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (candidate, codec);
            }
        }
        throw new NotSupportedException(
            "FFmpeg could not initialize any requested H.264 encoder (NVENC, QSV, AMF, or libx264).");
    }

    private static string CodecName(DirectorOffscreenVideoEncoder encoder)
        => encoder switch
        {
            DirectorOffscreenVideoEncoder.NvidiaNvenc => "h264_nvenc",
            DirectorOffscreenVideoEncoder.IntelQuickSync => "h264_qsv",
            DirectorOffscreenVideoEncoder.AmdAmf => "h264_amf",
            _ => "libx264"
        };

    private static bool ProbeEncoder(
        string executable,
        string codec,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        AddArguments(startInfo,
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=black:s=64x64:r=1:d=0.1",
            "-frames:v", "1", "-an", "-c:v", codec,
            "-f", "null", "-");
        using Process probe = new() { StartInfo = startInfo };
        try
        {
            if (!probe.Start())
            {
                return false;
            }
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state =>
                {
                    try
                    {
                        Process activeProbe = (Process)state!;
                        if (!activeProbe.HasExited)
                        {
                            activeProbe.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                    }
                },
                probe);
            _ = probe.StandardError.ReadToEndAsync();
            _ = probe.StandardOutput.ReadToEndAsync();
            if (!probe.WaitForExit(6000))
            {
                probe.Kill(entireProcessTree: true);
                probe.WaitForExit(1000);
                return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return probe.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        string executable,
        string codec,
        DirectorOffscreenVideoEncoder encoder,
        DirectorOffscreenFfmpegOptions options)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        string filters = options.OutputWidth == options.Width && options.OutputHeight == options.Height
            ? "vflip,pad=ceil(iw/2)*2:ceil(ih/2)*2,format=yuv420p"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"vflip,scale={options.OutputWidth}:{options.OutputHeight}:flags=lanczos,format=yuv420p");
        AddArguments(startInfo,
            "-hide_banner", "-loglevel", "warning", "-y",
            "-f", "rawvideo",
            "-pixel_format", "bgra",
            "-video_size", $"{options.Width}x{options.Height}",
            "-framerate", options.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            "-an",
            "-vf", filters,
            "-c:v", codec);
        if (options.VideoBitrateKbps > 0)
        {
            AddArguments(startInfo, "-b:v", options.VideoBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k");
        }
        else
        {
            AddQualityArguments(startInfo, encoder, options);
        }
        AddArguments(startInfo,
            "-pix_fmt", "yuv420p",
            "-r", options.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-movflags", "+faststart",
            options.OutputPath);
        return startInfo;
    }

    private static void AddQualityArguments(
        ProcessStartInfo startInfo,
        DirectorOffscreenVideoEncoder encoder,
        DirectorOffscreenFfmpegOptions options)
    {
        string quality = options.VideoQuality.ToString(CultureInfo.InvariantCulture);
        switch (encoder)
        {
            case DirectorOffscreenVideoEncoder.NvidiaNvenc:
                AddArguments(startInfo, "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", quality, "-b:v", "0");
                break;
            case DirectorOffscreenVideoEncoder.IntelQuickSync:
                AddArguments(startInfo, "-preset", "medium", "-global_quality", quality);
                break;
            case DirectorOffscreenVideoEncoder.AmdAmf:
                AddArguments(startInfo, "-quality", "balanced", "-rc", "cqp", "-qp_i", quality, "-qp_p", quality);
                break;
            default:
                AddArguments(startInfo, "-preset", options.SoftwarePreset, "-crf", quality);
                break;
        }
    }

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static string LastDiagnostic(string diagnostics, int exitCode)
    {
        string message = diagnostics.Trim();
        if (message.Length > 1024)
        {
            message = message[^1024..];
        }
        return message.Length == 0
            ? $"FFmpeg exited with code {exitCode}."
            : message;
    }

    private enum SinkState
    {
        Created,
        Starting,
        Running,
        Stopping,
        Stopped,
        Failed
    }

    private enum SlotState
    {
        Free,
        PendingGpu,
        MappedAwaitingQueue,
        CopyInProgress,
        CopyComplete
    }

    private sealed class ReadbackSlot
    {
        private int state;

        internal int BufferId { get; }
        internal int ByteCount { get; }
        internal IntPtr Fence { get; set; }
        internal IntPtr MappedPointer { get; set; }
        internal long Sequence { get; set; }
        internal int RepeatCount { get; set; }
        internal bool BackpressureReported { get; set; }
        internal SlotState State => (SlotState)Volatile.Read(ref state);

        internal ReadbackSlot(int bufferId, int byteCount)
        {
            BufferId = bufferId;
            ByteCount = byteCount;
        }

        internal void SetState(SlotState value)
            => Volatile.Write(ref state, (int)value);

        internal void Reset()
        {
            Fence = IntPtr.Zero;
            MappedPointer = IntPtr.Zero;
            Sequence = 0L;
            RepeatCount = 0;
            BackpressureReported = false;
            SetState(SlotState.Free);
        }
    }

    private sealed record ReadbackPacket(
        ReadbackSlot Slot,
        IntPtr Pointer,
        int ByteCount,
        int RepeatCount);

    private sealed class Counters
    {
        internal long InputFrames;
        internal long RateLimitedInputFrames;
        internal long ScheduledFrames;
        internal long SubmittedReadbacks;
        internal long EncodedFrames;
        internal long DuplicatedFrames;
        internal long GpuBackpressureEvents;
        internal long WriterBackpressureEvents;
        internal long ReadbackFailures;
        internal long DroppedFrames;

        internal DirectorOffscreenFfmpegStatistics Snapshot()
            => new(
                Interlocked.Read(ref InputFrames),
                Interlocked.Read(ref RateLimitedInputFrames),
                Interlocked.Read(ref ScheduledFrames),
                Interlocked.Read(ref SubmittedReadbacks),
                Interlocked.Read(ref EncodedFrames),
                Interlocked.Read(ref DuplicatedFrames),
                Interlocked.Read(ref GpuBackpressureEvents),
                Interlocked.Read(ref WriterBackpressureEvents),
                Interlocked.Read(ref ReadbackFailures),
                Interlocked.Read(ref DroppedFrames));
    }
}
