using System.Globalization;
using System.Diagnostics;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using VSDirector.Rendering;

namespace VSDirector.Recording;

internal sealed class DirectorNativeRecorder : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly DirectorFfmpegAudioRecorder audioRecorder = new();
    private readonly Guid ffmpegViewId = Guid.NewGuid();
    private Task<PreparedFfmpegCapture>? preparationTask;
    private Task<DirectorRecordingResult>? finalizeTask;
    private string preparationSessionId = string.Empty;
    private DirectorOffscreenFfmpegSink? ffmpegSink;
    private StreamWriter? frameTimecodes;
    private DirectorRecordingStartContext? context;
    private string outputPath = string.Empty;
    private long frameCount;
    private long startFrameOffset;
    private long captureStartTimestamp;
    private DateTimeOffset actualStartUtc;
    private long physicalFrameNumber;
    private bool disposed;

    public DirectorNativeRecorder(ICoreClientAPI capi)
    {
        this.capi = capi;
        game = capi.World as ClientMain
            ?? throw new InvalidOperationException("VS Director recording requires the desktop client.");
        capi.Event.RegisterRenderer(this, EnumRenderStage.Done, "vsdirector-recorder");
    }

    public double RenderOrder => 0.98d;

    public int RenderRange => int.MaxValue;

    public bool IsRecording => ffmpegSink is not null;

    public bool IsFfmpegRecording => ffmpegSink is not null;

    public event Action<Exception>? RecordingFailed;

    public Task PrepareAsync(DirectorRecordingStartContext startContext)
    {
        ArgumentNullException.ThrowIfNull(startContext);
        CancelPreparation();
        Directory.CreateDirectory(startContext.OutputDirectory);
        string basePath = Path.Combine(
            startContext.OutputDirectory,
            DirectorRecordingFiles.BuildBaseName(startContext));
        string preparedOutputPath = basePath + ".video.mp4";
        int width = game.Width;
        int height = game.Height;
        DirectorOffscreenFfmpegOptions options = new()
        {
            ViewId = ffmpegViewId,
            OutputPath = preparedOutputPath,
            Width = width,
            Height = height,
            FramesPerSecond = startContext.FramesPerSecond,
            FfmpegPath = startContext.FfmpegPath,
            Encoder = DirectorOffscreenVideoEncoder.Auto,
            AllowSoftwareFallback = true,
            VideoQuality = startContext.FfmpegVideoCrf,
            SoftwarePreset = startContext.FfmpegVideoPreset,
            PboRingSize = 4,
            WriterQueueCapacity = 8,
            MaximumCatchUpFrames = 8,
            MaximumStopPaddingSeconds = 2
        };
        preparationSessionId = startContext.SessionId;
        preparationTask = Task.Run(() =>
        {
            DirectorOffscreenFfmpegSink? sink = null;
            try
            {
                sink = new DirectorOffscreenFfmpegSink(capi, options);
                sink.Start();
                return new PreparedFfmpegCapture(sink, preparedOutputPath, string.Empty);
            }
            catch (Exception exception)
            {
                sink?.Dispose();
                return new PreparedFfmpegCapture(
                    null,
                    preparedOutputPath,
                    exception.GetBaseException().Message);
            }
        });
        return preparationTask;
    }

    public void CancelPreparation()
    {
        Task<PreparedFfmpegCapture>? pending = preparationTask;
        preparationTask = null;
        preparationSessionId = string.Empty;
        if (pending is null)
        {
            return;
        }

        _ = pending.ContinueWith(
            completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    completed.Result.Sink?.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    public string Start(DirectorRecordingStartContext startContext)
    {
        ArgumentNullException.ThrowIfNull(startContext);
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(DirectorNativeRecorder));
        }

        if (IsRecording)
        {
            throw new InvalidOperationException("The native recorder is already running.");
        }

        Directory.CreateDirectory(startContext.OutputDirectory);
        string baseName = DirectorRecordingFiles.BuildBaseName(startContext);
        string basePath = Path.Combine(startContext.OutputDirectory, baseName);
        bool audioRequested = startContext.CaptureGameAudio || startContext.CaptureMicrophone;
        DirectorOffscreenFfmpegSink? createdSink = null;
        try
        {
            Task<PreparedFfmpegCapture>? pending = preparationTask;
            if (pending is { IsCompleted: false })
            {
                throw new InvalidOperationException("The asynchronous FFmpeg writer is still preparing.");
            }
            if (pending is { IsCompletedSuccessfully: true }
                && string.Equals(preparationSessionId, startContext.SessionId, StringComparison.Ordinal))
            {
                PreparedFfmpegCapture prepared = pending.Result;
                createdSink = prepared.Sink;
                outputPath = prepared.OutputPath;
                if (createdSink is null)
                {
                    throw new InvalidOperationException(
                        "FFmpeg capture could not be prepared: " + prepared.Error);
                }
            }
            preparationTask = null;
            preparationSessionId = string.Empty;
            if (createdSink is null)
            {
                throw new InvalidOperationException("FFmpeg capture was not prepared.");
            }

            string csvPath = outputPath + ".timecode.csv";
            frameTimecodes = new StreamWriter(csvPath, append: false, new UTF8Encoding(false));
            frameTimecodes.WriteLine(
                "frame,mediaElapsedSeconds,timecode,utc,timelineFrame,timelineElapsedSeconds,droppedBeforeCapture");
            ffmpegSink = createdSink;
            context = startContext;
            actualStartUtc = DateTimeOffset.UtcNow;
            double startOffsetSeconds = Math.Max(
                0d,
                (capi.World.ElapsedMilliseconds - startContext.StartWorldMilliseconds) / 1000d);
            startFrameOffset = Math.Max(
                0L,
                (long)Math.Round(startOffsetSeconds * startContext.FramesPerSecond));
            frameCount = 0L;
            physicalFrameNumber = 0L;
            captureStartTimestamp = Stopwatch.GetTimestamp();
            _ = audioRecorder.Start(startContext, basePath);
            return outputPath;
        }
        catch
        {
            ffmpegSink = null;
            context = null;
            try
            {
                createdSink?.Dispose();
            }
            catch
            {
            }

            frameTimecodes?.Dispose();
            frameTimecodes = null;
            if (audioRecorder.IsRecording)
            {
                _ = audioRecorder.Stop();
            }
            throw;
        }
    }

    public DirectorRecordingResult Stop()
        => FinalizeStop(Detach(capturePendingFrames: true));

    public Task<DirectorRecordingResult> StopAsync()
        => StopAsync(capturePendingFrames: true);

    private Task<DirectorRecordingResult> StopAsync(bool capturePendingFrames)
    {
        NativeStopSession session = Detach(capturePendingFrames);
        Task<DirectorRecordingResult> task = Task.Run(() => FinalizeStop(session));
        finalizeTask = task;
        return task;
    }

    private NativeStopSession Detach(bool capturePendingFrames)
    {
        DirectorRecordingStartContext activeContext = context
            ?? throw new InvalidOperationException("The native recorder is not running.");
        DirectorOffscreenFfmpegSink? activeSink = ffmpegSink;
        if (activeSink is null)
        {
            throw new InvalidOperationException("The native recorder is not running.");
        }

        _ = capturePendingFrames;

        NativeStopSession session = new()
        {
            Context = activeContext,
            FfmpegSink = activeSink,
            FrameTimecodes = frameTimecodes,
            OutputPath = outputPath,
            FrameCount = frameCount,
            StartFrameOffset = startFrameOffset,
            ActualStartUtc = actualStartUtc
        };
        ffmpegSink = null;
        frameTimecodes = null;
        context = null;
        return session;
    }

    private DirectorRecordingResult FinalizeStop(NativeStopSession session)
    {
        Exception? closeError = null;
        DirectorAudioCaptureResult audioResult = new(false, string.Empty, string.Empty);
        DirectorOffscreenFfmpegStatistics ffmpegStatistics = default;
        try
        {
            if (session.FfmpegSink is not null)
            {
                DirectorOffscreenFfmpegResult ffmpegResult = session.FfmpegSink.Stop();
                ffmpegStatistics = ffmpegResult.Statistics;
                if (!ffmpegResult.Success)
                {
                    throw new IOException(ffmpegResult.Error);
                }
            }
        }
        catch (Exception exception)
        {
            closeError ??= exception;
        }
        finally
        {
            session.FrameTimecodes?.Flush();
            session.FrameTimecodes?.Dispose();
            session.FrameTimecodes = null;
            if (audioRecorder.IsRecording)
            {
                audioResult = audioRecorder.Stop();
            }
        }

        if (closeError is not null)
        {
            throw new IOException("The native video writer could not finalize the recording.", closeError);
        }

        long resultFrameCount = ffmpegStatistics.EncodedFrames;
        RewriteFrameTimecodesAtomically(
            session.OutputPath + ".timecode.csv",
            session.Context,
            session.StartFrameOffset,
            resultFrameCount);

        long timelineFrameCount = resultFrameCount;
        long droppedFrames = ffmpegStatistics.DroppedFrames;
        long duplicatedFrames = ffmpegStatistics.DuplicatedFrames;
        long backpressureEvents = ffmpegStatistics.GpuBackpressureEvents
            + ffmpegStatistics.WriterBackpressureEvents;

        return new DirectorRecordingResult
        {
            Backend = "ffmpeg-pbo",
            OutputPath = session.OutputPath,
            StopUtc = DateTimeOffset.UtcNow,
            ActualStartUtc = session.ActualStartUtc,
            StartOffsetSeconds = session.StartFrameOffset / (double)session.Context.FramesPerSecond,
            FrameCount = resultFrameCount,
            DurationSeconds = resultFrameCount / (double)session.Context.FramesPerSecond,
            TimelineFrameCount = timelineFrameCount,
            TimelineDurationSeconds = timelineFrameCount / (double)session.Context.FramesPerSecond,
            DroppedFrames = droppedFrames,
            DuplicatedFrames = duplicatedFrames,
            PeakBacklogFrames = 0L,
            BackpressureEvents = backpressureEvents,
            AudioPath = audioResult.Success ? audioResult.OutputPath ?? string.Empty : string.Empty,
            AudioError = audioResult.Error ?? string.Empty
        };
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        _ = deltaTime;
        _ = stage;
        DirectorOffscreenFfmpegSink? activeSink = ffmpegSink;
        DirectorRecordingStartContext? activeContext = context;
        if (activeSink is null || activeContext is null)
        {
            return;
        }

        try
        {
            double elapsedMilliseconds = Stopwatch.GetElapsedTime(captureStartTimestamp).TotalMilliseconds;
            long captureMilliseconds = elapsedMilliseconds >= long.MaxValue
                ? long.MaxValue
                : Math.Max(0L, (long)elapsedMilliseconds);
            DirectorOffscreenFrame frame = new(
                ffmpegViewId,
                "Main framebuffer",
                ++physicalFrameNumber,
                captureMilliseconds,
                game.Width,
                game.Height,
                0,
                0,
                0,
                DirectorOffscreenCapabilities.None);
            activeSink.OnFrame(in frame);
            if (activeSink.FailureMessage.Length > 0)
            {
                throw new IOException(activeSink.FailureMessage);
            }
        }
        catch (Exception exception)
        {
            try
            {
                _ = StopAsync(capturePendingFrames: false);
            }
            catch
            {
            }

            RecordingFailed?.Invoke(exception);
            return;
        }
    }

    private static void RewriteFrameTimecodesAtomically(
        string path,
        DirectorRecordingStartContext activeContext,
        long startOffset,
        long encodedFrameCount)
    {
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (StreamWriter timecodes = new(
                temporaryPath,
                append: false,
                new UTF8Encoding(false)))
            {
                timecodes.WriteLine(
                    "frame,mediaElapsedSeconds,timecode,utc,timelineFrame,timelineElapsedSeconds,droppedBeforeCapture");
                for (long frame = 0L; frame < encodedFrameCount; frame++)
                {
                    WriteFrameTimecode(
                        timecodes,
                        activeContext,
                        startOffset,
                        frame,
                        frame,
                        0L);
                }
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void WriteFrameTimecode(
        DirectorRecordingStartContext activeContext,
        long timelineCaptureFrame,
        long droppedBeforeCapture)
    {
        long capturedFrame = frameCount++;
        WriteFrameTimecode(
            frameTimecodes,
            activeContext,
            startFrameOffset,
            capturedFrame,
            timelineCaptureFrame,
            droppedBeforeCapture);
        if (frameCount % activeContext.FramesPerSecond == 0)
        {
            frameTimecodes?.Flush();
        }
    }

    private static void WriteFrameTimecode(
        StreamWriter? timecodes,
        DirectorRecordingStartContext activeContext,
        long startOffset,
        long capturedFrame,
        long timelineCaptureFrame,
        long droppedBeforeCapture)
    {
        double mediaElapsed = capturedFrame / (double)activeContext.FramesPerSecond;
        double timelineElapsed = timelineCaptureFrame / (double)activeContext.FramesPerSecond;
        long timelineFrame = startOffset + timelineCaptureFrame;
        string timecode = activeContext.StartTimecode.AddFrames(timelineFrame).ToString();
        DateTimeOffset frameUtc = activeContext.StartUtc.AddSeconds(
            timelineFrame / (double)activeContext.FramesPerSecond);
        timecodes?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{capturedFrame},{mediaElapsed:0.000000},{timecode},{frameUtc:O},"
            + $"{timelineCaptureFrame},{timelineElapsed:0.000000},{droppedBeforeCapture}"));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Done);
        CancelPreparation();
        if (finalizeTask is { IsCompleted: false } pendingFinalize)
        {
            _ = pendingFinalize.ContinueWith(
                _ => audioRecorder.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return;
        }
        if (IsFfmpegRecording)
        {
            try
            {
                Task<DirectorRecordingResult> stopTask = StopAsync();
                _ = stopTask.ContinueWith(
                    _ => audioRecorder.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
            catch (Exception exception)
            {
                capi.Logger.Warning(
                    "VS Director: failed to schedule native recording finalization during disposal: {0}",
                    exception.Message);
                audioRecorder.Dispose();
            }
            return;
        }
        if (IsRecording)
        {
            try
            {
                Stop();
            }
            catch (Exception exception)
            {
                capi.Logger.Warning("VS Director: failed to finalize native recording during disposal: {0}", exception.Message);
            }
        }
        audioRecorder.Dispose();
    }

    private sealed record PreparedFfmpegCapture(
        DirectorOffscreenFfmpegSink? Sink,
        string OutputPath,
        string Error);

    private sealed class NativeStopSession
    {
        public required DirectorRecordingStartContext Context { get; init; }

        public DirectorOffscreenFfmpegSink? FfmpegSink { get; init; }

        public StreamWriter? FrameTimecodes { get; set; }

        public required string OutputPath { get; init; }

        public long FrameCount { get; init; }

        public long StartFrameOffset { get; init; }

        public DateTimeOffset ActualStartUtc { get; init; }
    }
}
