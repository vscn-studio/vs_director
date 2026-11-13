using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using VSDirector.Recording;
using VSDirector.Rendering;
using VSDirector.Runtime;

namespace VSDirector.Replay;

internal sealed class DirectorReplayMultiTrackExporter : IRenderer, IDisposable
{
    private const double TimeEpsilon = 0.000001d;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly DirectorConfig config;
    private readonly DirectorCameraController controller;
    private readonly Func<double, bool> seekReplay;
    private readonly Func<bool> replayReady;
    private readonly Func<DirectorRecordingTrack, double, (bool Success, string Error)> applyTrack;
    private readonly object statusLock = new();
    private ExportSession? session;
    private string status = "idle";
    private string lastError = string.Empty;
    private string outputDirectory = string.Empty;
    private string currentTrackName = string.Empty;
    private string nextTrackName = string.Empty;
    private string currentOutputFile = string.Empty;
    private int currentTrackNumber;
    private int trackCount;
    private long currentTrackFrame;
    private long currentTrackFramesTotal;
    private bool disposed;

    public DirectorReplayMultiTrackExporter(
        ICoreClientAPI capi,
        DirectorConfig config,
        DirectorCameraController controller,
        Func<double, bool> seekReplay,
        Func<bool> replayReady,
        Func<DirectorRecordingTrack, double, (bool Success, string Error)> applyTrack)
    {
        this.capi = capi;
        this.config = config;
        this.controller = controller;
        this.seekReplay = seekReplay;
        this.replayReady = replayReady;
        this.applyTrack = applyTrack;
        game = capi.World as ClientMain
            ?? throw new InvalidOperationException("Replay export requires the desktop client.");
        capi.Event.RegisterRenderer(this, EnumRenderStage.Done, "vsdirector-replay-multitrack");
    }

    public double RenderOrder => 0.975d;

    public int RenderRange => int.MaxValue;

    public bool IsExporting => session is not null;

    public string Status
    {
        get
        {
            lock (statusLock)
            {
                return status;
            }
        }
    }

    public string LastError
    {
        get
        {
            lock (statusLock)
            {
                return lastError;
            }
        }
    }

    public string OutputDirectory
    {
        get
        {
            lock (statusLock)
            {
                return outputDirectory;
            }
        }
    }

    public string CurrentTrackName
    {
        get
        {
            lock (statusLock)
            {
                return currentTrackName;
            }
        }
    }

    public string NextTrackName
    {
        get
        {
            lock (statusLock)
            {
                return nextTrackName;
            }
        }
    }

    public string CurrentOutputFile
    {
        get
        {
            lock (statusLock)
            {
                return currentOutputFile;
            }
        }
    }

    public int CurrentTrackNumber
    {
        get
        {
            lock (statusLock)
            {
                return currentTrackNumber;
            }
        }
    }

    public int TrackCount
    {
        get
        {
            lock (statusLock)
            {
                return trackCount;
            }
        }
    }

    public long CurrentTrackFrame
    {
        get
        {
            lock (statusLock)
            {
                return currentTrackFrame;
            }
        }
    }

    public long CurrentTrackFramesTotal
    {
        get
        {
            lock (statusLock)
            {
                return currentTrackFramesTotal;
            }
        }
    }

    public double Progress
    {
        get
        {
            ExportSession? active = session;
            return active is null || active.TotalOutputFrames <= 0
                ? Status is "complete" or "converting" ? 1d : 0d
                : Math.Clamp(active.CompletedOutputFrames / (double)active.TotalOutputFrames, 0d, 1d);
        }
    }

    public bool Start(
        string replayName,
        IReadOnlyList<DirectorRecordingTrack> requestedTracks,
        DirectorReplayViewportTimeline timeline,
        double startSeconds,
        double endSeconds,
        double timeScale,
        DirectorReplayExportOptions options,
        DirectorReplayDocument? audioDocument,
        string preferredPlayerUid,
        out string error)
        => StartCore(
            replayName,
            requestedTracks,
            timeline,
            startSeconds,
            endSeconds,
            timeScale,
            options,
            audioDocument,
            preferredPlayerUid,
            out error);

    private bool StartCore(
        string replayName,
        IReadOnlyList<DirectorRecordingTrack> requestedTracks,
        DirectorReplayViewportTimeline timeline,
        double startSeconds,
        double endSeconds,
        double timeScale,
        DirectorReplayExportOptions options,
        DirectorReplayDocument? audioDocument,
        string preferredPlayerUid,
        out string error)
    {
        error = string.Empty;
        if (disposed)
        {
            error = "exporter-disposed";
            return false;
        }

        if (session is not null)
        {
            error = "replay-export-active";
            return false;
        }

        if (requestedTracks.Count == 0
            || requestedTracks.Count > 32
            || requestedTracks.Any(track => !track.Enabled
                || track.SourceType == DirectorRecordingSourceType.Program
                || !DirectorRecordingTrack.RequiresSource(track.SourceType)
                || track.SourceId == Guid.Empty))
        {
            error = "replay-export-tracks";
            return false;
        }

        if (!double.IsFinite(startSeconds)
            || !double.IsFinite(endSeconds)
            || startSeconds < 0d
            || endSeconds <= startSeconds
            || !double.IsFinite(timeScale)
            || timeScale < 0.05d
            || timeScale > 1024d
            || options.Backend != DirectorReplayExportBackend.Ffmpeg)
        {
            error = "replay-export-range";
            return false;
        }

        ClearTrackStatus();
        int fps = DirectorTimecode.NormalizeFrameRate(options.FramesPerSecond);
        int outputWidth = NormalizeEvenDimension(options.Width, game.Width);
        int outputHeight = NormalizeEvenDimension(options.Height, game.Height);
        int captureWidth = game.Width;
        int captureHeight = game.Height;
        DirectorReplayViewportTimeline exportTimeline = timeline.DeepClone();
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        bool restoreCameraActive = controller.IsActive;
        CameraPreset? restorePose = controller.CurrentPose;
        string root = ResolveOutputRoot(options.OutputDirectory);
        string exportId = Guid.NewGuid().ToString("N")[..8];
        string folderName = $"{startedUtc.ToLocalTime():yyyy-MM-dd_HH-mm-ss-fff}_Replay_{DirectorRecordingFiles.SanitizeFilePart(replayName, "Replay")}_{exportId}";
        string folder = Path.Combine(root, folderName);
        List<ExportTrack> tracks = new(requestedTracks.Count);
        DirectorReplayAudioExportPlan? audioPlan = null;
        try
        {
            if (options.IncludeAudio && audioDocument is not null)
            {
                audioPlan = DirectorReplayAudioExporter.Prepare(
                    game,
                    audioDocument,
                    preferredPlayerUid);
                if (audioPlan.MissingAssets > 0)
                {
                    capi.Logger.Warning(
                        "VS Director: {0} replay audio asset(s) could not be decoded for export.",
                        audioPlan.MissingAssets);
                }
            }
            Directory.CreateDirectory(folder);
            for (int index = 0; index < requestedTracks.Count; index++)
            {
                DirectorRecordingTrack track = requestedTracks[index].DeepClone();
                double trackStart = track.ExportStartSeconds ?? startSeconds;
                double trackEnd = track.ExportEndSeconds ?? endSeconds;
                if (!double.IsFinite(trackStart)
                    || !double.IsFinite(trackEnd)
                    || trackStart < startSeconds - TimeEpsilon
                    || trackEnd > endSeconds + TimeEpsilon
                    || trackEnd <= trackStart)
                {
                    throw new InvalidOperationException("replay-export-range");
                }
                long trackFrames = DirectorReplayElementExportClock.EstimateFrameCount(
                    exportTimeline,
                    trackStart,
                    trackEnd,
                    timeScale,
                    fps);
                track.ExportStartSeconds = trackStart;
                track.ExportEndSeconds = trackEnd;
                string stem = $"{index + 1:00}_{DirectorRecordingFiles.SanitizeFilePart(track.Name, "Shot")}";
                string outputPath = Path.Combine(folder, stem + ".mp4");
                DirectorOffscreenFfmpegSink sink = new(capi, new DirectorOffscreenFfmpegOptions
                {
                    ViewId = track.Id,
                    OutputPath = outputPath,
                    Width = captureWidth,
                    Height = captureHeight,
                    OutputWidth = outputWidth,
                    OutputHeight = outputHeight,
                    FramesPerSecond = fps,
                    FfmpegPath = options.FfmpegPath,
                    Encoder = ToFfmpegEncoder(options.Encoder),
                    AllowSoftwareFallback = true,
                    VideoQuality = options.VideoQuality,
                    VideoBitrateKbps = options.VideoBitrateKbps,
                    SoftwarePreset = options.SoftwarePreset,
                    MaximumCatchUpFrames = 1,
                    RequireExactFrames = true
                });
                sink.Start();
                StreamWriter timecodes = new(
                    outputPath + ".timecode.csv",
                    append: false,
                    new UTF8Encoding(false));
                timecodes.WriteLine("frame,replaySeconds,sourceTimecode");
                tracks.Add(new ExportTrack(
                    track,
                    sink,
                    timecodes,
                    outputPath,
                    trackStart,
                    trackEnd,
                    trackFrames,
                    new DirectorReplayElementExportClock(
                        exportTimeline,
                        trackStart,
                        trackEnd,
                        timeScale,
                        fps)));
            }

            ExportSession created = new(
                replayName,
                tracks,
                startedUtc,
                folder,
                startSeconds,
                endSeconds,
                timeScale,
                fps,
                captureWidth,
                captureHeight,
                tracks.Count == 0 ? 0L : tracks[0].FramesTotal,
                Math.Max(
                    options.SettleFrames,
                    1),
                restoreCameraActive,
                restorePose,
                workflow: null,
                timelineOutputPath: string.Empty,
                audioPlan,
                options.FfmpegPath,
                options.AudioBitrateKbps);
            if (!seekReplay(created.Tracks[0].StartSeconds))
            {
                CloseTracks(created.Tracks);
                RestoreCameraAfterExport(created);
                if (error.Length == 0)
                {
                    error = "replay-seek-failed";
                }
                return false;
            }

            created.FrameGate.BeginSeek();

            session = created;
            SetStatus("rendering", string.Empty, folder);
            UpdateTrackStatus(created);
            WriteManifest(created, "rendering");
            return true;
        }
        catch (Exception exception)
        {
            CloseTracks(tracks);
            error = exception.GetBaseException().Message;
            capi.Logger.Error("VS Director: replay export initialization failed: {0}", exception);
            SetStatus("error", error, folder);
            return false;
        }
    }

    public void Stop()
    {
        ExportSession? active = session;
        if (active is null)
        {
            return;
        }
        session = null;
        active.Cancellation.Cancel();
        if (!active.SinksClosed)
        {
            CloseTracks(active.Tracks);
            active.SinksClosed = true;
        }
        if (!active.Finalizing)
        {
            active.Cancellation.Dispose();
        }
        RestoreCameraAfterExport(active);
        SetStatus("stopped", string.Empty, active.OutputDirectory);
        WriteManifest(active, "stopped");
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        _ = deltaTime;
        _ = stage;
        ExportSession? active = session;
        if (active is null)
        {
            return;
        }
        if (active.Finalizing)
        {
            return;
        }

        try
        {
            DirectorReplayExportFrameAction frameAction = active.FrameGate.Next(replayReady());
            if (frameAction is DirectorReplayExportFrameAction.WaitForReplay
                or DirectorReplayExportFrameAction.Settle)
            {
                return;
            }
            if (frameAction == DirectorReplayExportFrameAction.ApplyCamera)
            {
                string readyError = string.Empty;
                if (!SelectTrack(active, active.TrackIndex, out readyError))
                {
                    Fail(active, readyError);
                    return;
                }

                // SelectTrack runs at the Done render stage, after the current
                // framebuffer already exists. The frame gate waits for both a
                // fresh camera frame and the configured post-seek settle frames.
                return;
            }

            ExportTrack track = active.Tracks[active.TrackIndex];
            long scheduledBefore = track.Sink.Statistics.ScheduledFrames;
            long captureMilliseconds = (long)Math.Ceiling(
                track.FrameCount * 1000d / active.FramesPerSecond);
            DirectorOffscreenFrame frame = new(
                track.Track.Id,
                track.Track.Name,
                track.FrameCount + 1L,
                captureMilliseconds,
                active.OutputWidth,
                active.OutputHeight,
                0,
                0,
                0,
                DirectorOffscreenCapabilities.None);
            track.Sink.OnFrame(in frame);
            if (track.Sink.FailureMessage.Length > 0)
            {
                Fail(active, track.Sink.FailureMessage);
                return;
            }
            if (track.Sink.Statistics.ScheduledFrames <= scheduledBefore)
            {
                return;
            }
            long trackFrame = track.FrameCount++;
            DirectorTimecode sourceTimecode = DirectorTimecode.FromUtc(
                active.StartedUtc.AddSeconds(active.CurrentReplaySeconds),
                active.FramesPerSecond);
            track.Timecodes.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{trackFrame},{active.CurrentReplaySeconds:0.000000},{sourceTimecode}"));
            if (active.AudioPlan is not null)
                track.AudioFrames.Add(new DirectorReplayAudioFrame(
                    active.CurrentReplaySeconds,
                    controller.CurrentPose?.DeepClone() ?? new CameraPreset()));
            if (trackFrame % active.FramesPerSecond == 0)
            {
                track.Timecodes.Flush();
            }

            active.CompletedOutputFrames++;
            UpdateTrackStatus(active);
            // A client has one final framebuffer. Finish one complete camera
            // pass before selecting the next path so the window does not
            // flicker between views on every timeline frame.
            if (track.FrameCount >= track.FramesTotal)
            {
                int nextTrack = active.TrackIndex + 1;
                if (nextTrack >= active.Tracks.Count)
                {
                    CompleteCapture(active);
                    return;
                }

                active.TrackIndex = nextTrack;
                active.CurrentReplaySeconds = active.Tracks[nextTrack].StartSeconds;
                UpdateTrackStatus(active);
                if (!seekReplay(active.CurrentReplaySeconds))
                {
                    Fail(active, "replay-seek-failed");
                    return;
                }
                active.FrameGate.BeginSeek();

                return;
            }

            active.CurrentReplaySeconds = track.Clock.Advance();
            if (!seekReplay(active.CurrentReplaySeconds))
            {
                Fail(active, "replay-seek-failed");
                return;
            }
            active.FrameGate.BeginSeek();
        }
        catch (Exception exception)
        {
            Fail(active, exception.Message);
        }
    }

    private bool SelectTrack(
        ExportSession active,
        int index,
        out string error)
    {
        error = string.Empty;
        DirectorRecordingTrack track = active.Tracks[index].Track;
        (bool success, string applyError) = applyTrack(track, active.CurrentReplaySeconds);
        if (!success)
        {
            error = string.IsNullOrWhiteSpace(applyError)
                ? $"Recording track '{track.Name}' could not be sampled at {active.CurrentReplaySeconds:0.###}s."
                : applyError;
            return false;
        }

        active.TrackIndex = index;
        UpdateTrackStatus(active);
        return true;
    }

    private void CompleteCapture(ExportSession active)
    {
        active.Finalizing = true;
        string closeError = CloseTracks(active.Tracks);
        active.SinksClosed = true;
        RestoreCameraAfterExport(active);
        if (closeError.Length > 0)
        {
            session = null;
            active.Cancellation.Dispose();
            SetStatus("error", closeError, active.OutputDirectory);
            WriteManifest(active, "error", closeError);
            return;
        }

        if (active.AudioPlan is not { HasPlayableEvents: true })
        {
            string noAudioError = active.AudioPlan is { MissingAssets: > 0 } missing
                ? $"No recorded replay sound assets could be decoded ({missing.MissingAssets} missing)."
                : string.Empty;
            session = null;
            active.Cancellation.Dispose();
            SetStatus(
                noAudioError.Length == 0 ? "complete" : "error",
                noAudioError,
                active.OutputDirectory);
            WriteManifest(
                active,
                noAudioError.Length == 0 ? "complete" : "error",
                noAudioError);
            return;
        }

        SetStatus("converting", string.Empty, active.OutputDirectory);
        WriteManifest(active, "converting");
        CancellationToken cancellationToken = active.Cancellation.Token;
        _ = Task.Run(() =>
        {
            string audioError;
            try
            {
                audioError = RenderAudio(active, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                audioError = "Replay audio export was canceled.";
            }
            catch (Exception exception)
            {
                audioError = exception.GetBaseException().Message;
            }
            active.Cancellation.Dispose();
            capi.Event.EnqueueMainThreadTask(
                () => CompleteAudioFinalization(active, audioError),
                "vsdirector-replay-audio-finalized");
        });
    }

    private string RenderAudio(ExportSession active, CancellationToken cancellationToken)
    {
        if (active.AudioPlan is not { HasPlayableEvents: true } plan)
        {
            return string.Empty;
        }

        foreach (ExportTrack track in active.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectorReplayAudioExportResult result = DirectorReplayAudioExporter.RenderAndMux(
                plan,
                track.AudioFrames,
                active.FramesPerSecond,
                track.OutputPath,
                active.FfmpegPath,
                active.AudioBitrateKbps,
                active.StartedUtc.AddSeconds(track.StartSeconds),
                cancellationToken);
            track.AudioSampleFrames = result.SampleFrames;
            track.AudioMuxed = result.HasAudio && result.Success;
            track.AudioError = result.Error;
            if (!result.Success)
            {
                return result.Error.Length == 0
                    ? "Replay audio could not be added to the FFmpeg export."
                    : result.Error;
            }
        }
        return string.Empty;
    }

    private void CompleteAudioFinalization(ExportSession active, string audioError)
    {
        if (!ReferenceEquals(session, active))
        {
            return;
        }

        session = null;
        if (audioError.Length > 0)
        {
            SetStatus("error", audioError, active.OutputDirectory);
            WriteManifest(active, "error", audioError);
            return;
        }
        SetStatus("complete", string.Empty, active.OutputDirectory);
        WriteManifest(active, "complete");
    }

    private void Fail(ExportSession active, string error)
    {
        if (!ReferenceEquals(session, active))
        {
            return;
        }

        session = null;
        active.Cancellation.Cancel();
        CloseTracks(active.Tracks);
        active.SinksClosed = true;
        active.Cancellation.Dispose();
        RestoreCameraAfterExport(active);
        SetStatus("error", error, active.OutputDirectory);
        WriteManifest(active, "error", error);
    }

    private void UpdateTrackStatus(ExportSession active)
    {
        int index = Math.Clamp(active.TrackIndex, 0, active.Tracks.Count - 1);
        ExportTrack current = active.Tracks[index];
        string next = index + 1 < active.Tracks.Count
            ? active.Tracks[index + 1].Track.Name
            : string.Empty;
        lock (statusLock)
        {
            currentTrackName = current.Track.Name;
            nextTrackName = next;
            currentOutputFile = current.OutputPath;
            currentTrackNumber = index + 1;
            trackCount = active.Tracks.Count;
            currentTrackFrame = current.FrameCount;
            currentTrackFramesTotal = current.FramesTotal;
        }
    }

    private void ClearTrackStatus()
    {
        lock (statusLock)
        {
            currentTrackName = string.Empty;
            nextTrackName = string.Empty;
            currentOutputFile = string.Empty;
            currentTrackNumber = 0;
            trackCount = 0;
            currentTrackFrame = 0L;
            currentTrackFramesTotal = 0L;
        }
    }

    private void SetCurrentOutputFile(string path)
    {
        lock (statusLock)
        {
            currentOutputFile = path;
        }
    }

    private void RestoreCameraAfterExport(ExportSession active)
    {
        if (active.CameraRestored)
        {
            return;
        }

        active.CameraRestored = true;
        try
        {
            if (active.RestoreCameraActive && active.RestorePose is not null)
            {
                if (!controller.TrySetReplayPose(active.RestorePose, out string error))
                {
                    capi.Logger.Warning("VS Director: could not restore the pre-export camera: {0}", error);
                }
            }
            else
            {
                controller.Deactivate();
            }
        }
        catch (Exception exception)
        {
            capi.Logger.Warning("VS Director: could not restore the pre-export camera: {0}", exception.Message);
        }
    }

    private void WriteManifest(ExportSession active, string phase, string error = "")
    {
        try
        {
            object manifest = new
            {
                schemaVersion = 3,
                phase,
                error,
                replay = active.ReplayName,
                capture = new
                {
                    scope = "single-client-full-framebuffer-multipass",
                    independentViewCount = active.Tracks.Count,
                    sameFramebufferCopied = false,
                    shadersAndPostProcessing = true,
                    trackPassStrategy = "sequential-full-track",
                    restoresPreExportCamera = true,
                    preExportCameraRestored = active.CameraRestored,
                    physicalFramesCaptured = active.CompletedOutputFrames,
                    warmupFramesPerTrackSwitch = active.SettleFramesPerSeek,
                    settleFramesPerSeek = active.SettleFramesPerSeek
                },
                timeline = new
                {
                    startSeconds = active.StartSeconds,
                    endSeconds = active.EndSeconds,
                    active.TimeScale,
                    active.FramesPerSecond,
                    requestedFramesPerTrack = active.FramesPerTrack,
                    completedFramesPerTrack = active.Tracks.Min(track => track.FrameCount)
                },
                audio = new
                {
                    enabled = active.AudioPlan is not null,
                    recordedEvents = active.AudioPlan?.Events.Count ?? 0,
                    decodedAssets = active.AudioPlan?.Assets.Count ?? 0,
                    missingAssets = active.AudioPlan?.MissingAssets ?? 0,
                    sampleRate = DirectorReplayAudioExportPolicy.SampleRate,
                    channels = DirectorReplayAudioExportPolicy.Channels,
                    bitrateKbps = active.AudioBitrateKbps
                },
                edit = active.Workflow is null ? null : new
                {
                    workflowId = active.Workflow.Id,
                    workflowName = active.Workflow.Name,
                    outputPath = active.TimelineOutputPath,
                    completed = active.TimelineCompleted,
                    durationSeconds = active.TimelinePlan?.DurationSeconds ?? 0d,
                    startTimecode = active.TimelinePlan?.StartTimecode.ToString() ?? string.Empty,
                    videoClipCount = active.TimelinePlan?.Inputs.Count(input =>
                        input.Type == DirectorTimelineInputType.Video) ?? 0,
                    audioClipCount = active.TimelinePlan?.Inputs.Count(input =>
                        input.Type == DirectorTimelineInputType.Audio) ?? active.Workflow.AudioClips.Count,
                    videoClips = active.TimelineProject?.VideoClips.Select(clip => new
                    {
                        clip.Id,
                        clip.Name,
                        clip.InputPath,
                        clip.Track,
                        clip.TimelineStartSeconds,
                        clip.SourceStartSeconds,
                        clip.DurationSeconds,
                        transition = clip.TransitionIn.Type.ToString(),
                        transitionDurationSeconds = clip.TransitionIn.DurationSeconds
                    }),
                    audioClips = active.TimelineProject?.AudioClips.Select(clip => new
                    {
                        clip.Id,
                        clip.Name,
                        clip.InputPath,
                        clip.Track,
                        clip.TimelineStartSeconds,
                        clip.SourceStartSeconds,
                        clip.DurationSeconds,
                        clip.GainDecibels,
                        clip.FadeInSeconds,
                        clip.FadeOutSeconds,
                        clip.PlaybackRate
                    })
                },
                active.StartedUtc,
                tracks = active.Tracks.Select((track, index) => new
                {
                    index,
                    trackId = track.Track.Id,
                    trackName = track.Track.Name,
                    sourceType = track.Track.SourceType.ToString(),
                    sourceId = track.Track.SourceId,
                    startSeconds = track.StartSeconds,
                    endSeconds = track.EndSeconds,
                    requestedFrames = track.FramesTotal,
                    workflowSegments = track.Track.WorkflowSegments.Select(segment => new
                    {
                        segment.CardId,
                        segment.CardName,
                        sourceType = segment.SourceType.ToString(),
                        segment.SourceId,
                        segment.StartSeconds,
                        segment.EndSeconds
                    }),
                    frameCount = track.FrameCount,
                    audioMuxed = track.AudioMuxed,
                    audioSampleFrames = track.AudioSampleFrames,
                    audioError = track.AudioError,
                    outputPath = track.OutputPath,
                    encoder = track.Sink.SelectedEncoder.ToString(),
                    codec = track.Sink.SelectedCodec
                })
            };
            File.WriteAllText(
                Path.Combine(active.OutputDirectory, "multitrack.manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions));
        }
        catch (Exception exception)
        {
            capi.Logger.Warning("VS Director: replay export manifest could not be written: {0}", exception.Message);
        }
    }

    private static string ResolveOutputRoot(string? configuredDirectory)
    {
        string configured = configuredDirectory?.Trim() ?? string.Empty;
        return configured.Length == 0
            ? Path.GetFullPath(Path.Combine(GamePaths.Videos, "VSDirector", "ReplayExports"))
            : Path.GetFullPath(Path.Combine(configured, "ReplayExports"));
    }

    private void SetStatus(string value, string error, string directory)
    {
        lock (statusLock)
        {
            status = value;
            lastError = error;
            outputDirectory = directory;
        }
    }

    private static string CloseTracks(IEnumerable<ExportTrack> tracks)
    {
        string error = string.Empty;
        foreach (ExportTrack track in tracks)
        {
            try
            {
                DirectorOffscreenFfmpegResult result = track.Sink.Stop();
                string integrity = DirectorReplayExportIntegrity.Validate(track.FrameCount, result.Statistics.EncodedFrames,
                    result.Statistics.DroppedFrames, result.Statistics.ReadbackFailures, result.Statistics.DuplicatedFrames);
                if (integrity.Length > 0 && error.Length == 0) error = integrity;
                if (!result.Success && error.Length == 0)
                {
                    error = result.Error;
                }
            }
            catch (Exception exception)
            {
                error = error.Length == 0 ? exception.GetBaseException().Message : error;
            }

            try
            {
                track.Timecodes.Flush();
                track.Timecodes.Dispose();
            }
            catch
            {
            }
        }
        return error;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Stop();
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Done);
    }

    private static int NormalizeEvenDimension(int value, int fallback)
    {
        int normalized = value > 0 ? value : fallback;
        normalized = Math.Clamp(normalized, 16, DirectorOffscreenMultiViewRenderer.MaximumDimension);
        return (normalized & 1) == 0 ? normalized : normalized - 1;
    }

    private static DirectorOffscreenVideoEncoder ToFfmpegEncoder(DirectorOffscreenEncoder encoder)
        => encoder switch
        {
            DirectorOffscreenEncoder.NvidiaNvenc => DirectorOffscreenVideoEncoder.NvidiaNvenc,
            DirectorOffscreenEncoder.IntelQuickSync => DirectorOffscreenVideoEncoder.IntelQuickSync,
            DirectorOffscreenEncoder.AmdAmf => DirectorOffscreenVideoEncoder.AmdAmf,
            DirectorOffscreenEncoder.Libx264 => DirectorOffscreenVideoEncoder.Libx264,
            _ => DirectorOffscreenVideoEncoder.Auto
        };

    private sealed class ExportSession
    {
        public ExportSession(
            string replayName,
            List<ExportTrack> tracks,
            DateTimeOffset startedUtc,
            string outputDirectory,
            double startSeconds,
            double endSeconds,
            double timeScale,
            int framesPerSecond,
            int outputWidth,
            int outputHeight,
            long framesPerTrack,
            int settleFramesPerSeek,
            bool restoreCameraActive,
            CameraPreset? restorePose,
            DirectorWorkflow? workflow,
            string timelineOutputPath,
            DirectorReplayAudioExportPlan? audioPlan,
            string ffmpegPath,
            int audioBitrateKbps)
        {
            ReplayName = replayName;
            Tracks = tracks;
            StartedUtc = startedUtc;
            OutputDirectory = outputDirectory;
            StartSeconds = startSeconds;
            EndSeconds = endSeconds;
            CurrentReplaySeconds = tracks.Count == 0 ? startSeconds : tracks[0].StartSeconds;
            TimeScale = timeScale;
            FramesPerSecond = framesPerSecond;
            OutputWidth = outputWidth;
            OutputHeight = outputHeight;
            FramesPerTrack = framesPerTrack;
            TotalOutputFrames = tracks.Aggregate(0L, (total, track) => checked(total + track.FramesTotal));
            SettleFramesPerSeek = settleFramesPerSeek;
            FrameGate = new DirectorReplayExportFrameGate(settleFramesPerSeek);
            RestoreCameraActive = restoreCameraActive;
            RestorePose = restorePose?.DeepClone();
            Workflow = workflow?.DeepClone();
            TimelineOutputPath = timelineOutputPath;
            AudioPlan = audioPlan;
            FfmpegPath = ffmpegPath;
            AudioBitrateKbps = Math.Clamp(audioBitrateKbps, 64, 512);
        }

        public string ReplayName { get; }

        public List<ExportTrack> Tracks { get; }

        public DateTimeOffset StartedUtc { get; }

        public string OutputDirectory { get; }

        public double StartSeconds { get; }

        public double EndSeconds { get; }

        public double CurrentReplaySeconds { get; set; }

        public double TimeScale { get; }

        public int FramesPerSecond { get; }

        public int OutputWidth { get; }

        public int OutputHeight { get; }

        public long FramesPerTrack { get; }

        public long TotalOutputFrames { get; }

        public int SettleFramesPerSeek { get; }

        public DirectorReplayExportFrameGate FrameGate { get; }

        public bool RestoreCameraActive { get; }

        public CameraPreset? RestorePose { get; }

        public DirectorWorkflow? Workflow { get; }

        public string TimelineOutputPath { get; }

        public DirectorReplayAudioExportPlan? AudioPlan { get; }

        public string FfmpegPath { get; }

        public int AudioBitrateKbps { get; }

        public DirectorTimelineExportPlan? TimelinePlan { get; set; }

        public DirectorTimelineProject? TimelineProject { get; set; }

        public bool TimelineCompleted { get; set; }

        public int TrackIndex { get; set; }

        public bool CameraRestored { get; set; }

        public long CompletedOutputFrames { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public bool Finalizing { get; set; }

        public bool SinksClosed { get; set; }
    }

    private sealed class ExportTrack
    {
        public ExportTrack(
            DirectorRecordingTrack track,
            DirectorOffscreenFfmpegSink sink,
            StreamWriter timecodes,
            string outputPath,
            double startSeconds,
            double endSeconds,
            long framesTotal,
            DirectorReplayElementExportClock clock)
        {
            Track = track;
            Sink = sink;
            Timecodes = timecodes;
            OutputPath = outputPath;
            StartSeconds = startSeconds;
            EndSeconds = endSeconds;
            FramesTotal = framesTotal;
            Clock = clock;
        }

        public DirectorRecordingTrack Track { get; }

        public DirectorOffscreenFfmpegSink Sink { get; }

        public StreamWriter Timecodes { get; }

        public string OutputPath { get; }

        public double StartSeconds { get; }

        public double EndSeconds { get; }

        public long FramesTotal { get; }

        public DirectorReplayElementExportClock Clock { get; }

        public long FrameCount { get; set; }

        public List<DirectorReplayAudioFrame> AudioFrames { get; } = new();

        public bool AudioMuxed { get; set; }

        public long AudioSampleFrames { get; set; }

        public string AudioError { get; set; } = string.Empty;
    }
}
