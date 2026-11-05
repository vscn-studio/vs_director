using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;
using VSDirector.Networking;

namespace VSDirector.Recording;

internal sealed class DirectorRecordingCoordinator : IDisposable
{
    private readonly ICoreClientAPI capi;
    private readonly DirectorConfig config;
    private readonly IClientNetworkChannel channel;
    private readonly Func<DirectorRecordingTrack, bool, (bool Success, string Error)> activateTrack;
    private readonly Action<bool> inputLockHandler;
    private DirectorObsWebSocketClient? obsClient;
    private Task? obsPrepareTask;
    private DirectorRecordingStartContext? context;
    private long actualStartWorldMilliseconds;
    private DateTimeOffset actualStartUtc;
    private string pendingTerminalError = string.Empty;
    private bool sourceReleased;
    private bool disposed;

    public DirectorRecordingCoordinator(
        ICoreClientAPI capi,
        DirectorConfig config,
        Func<DirectorRecordingTrack, bool, (bool Success, string Error)> activateTrack,
        Action<bool> inputLockHandler)
    {
        this.capi = capi;
        this.config = config;
        this.activateTrack = activateTrack;
        this.inputLockHandler = inputLockHandler;
        channel = capi.Network.GetChannel(DirectorNetwork.ChannelName)
            .SetMessageHandler<DirectorRecordingControl>(OnControl)
            .SetMessageHandler<DirectorRecordingSessionStatus>(OnSessionStatus);
    }

    public DirectorRecordingState State { get; private set; }

    public bool IsActive => State is not DirectorRecordingState.Idle and not DirectorRecordingState.Error;

    public string? SessionId => context?.SessionId;

    public DirectorRecordingTrack? ActiveTrack => context?.Track.DeepClone();

    public string LastOutputPath { get; private set; } = string.Empty;

    public string LastError { get; private set; } = string.Empty;

    public DateTimeOffset LastActualStartUtc { get; private set; }

    public event Action<DirectorRecordingState, string>? StateChanged;

    public event Action<DirectorRecordingSessionStatus>? ServerStatusReceived;

    public event Action<DirectorRecordingTrack>? SourceCaptureStopped;

    public void RegisterWorker(DirectorRecordingTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        channel.SendPacket(new DirectorRecordingWorkerRegistration
        {
            Active = true,
            TrackId = track.Id.ToString("D"),
            TrackName = track.Name,
            SourceType = (int)track.SourceType,
            SourceId = track.SourceId == Guid.Empty ? string.Empty : track.SourceId.ToString("D"),
            Enabled = track.Enabled
        });
    }

    public void UnregisterWorker()
        => channel.SendPacket(new DirectorRecordingWorkerRegistration { Active = false });

    public bool RequestStart(out string error, Guid? expectedTrackId = null)
    {
        error = string.Empty;
        if (State is not DirectorRecordingState.Idle and not DirectorRecordingState.Error)
        {
            error = "recording-busy";
            return false;
        }

        LastError = string.Empty;
        LastOutputPath = string.Empty;
        LastActualStartUtc = default;
        pendingTerminalError = string.Empty;
        SetState(DirectorRecordingState.Requesting, "start-requested");
        channel.SendPacket(new DirectorRecordingSessionRequest
        {
            Action = (int)DirectorRecordingSessionAction.Start,
            FramesPerSecond = DirectorTimecode.NormalizeFrameRate(config.RecordingFrameRate),
            Container = (int)DirectorRecordingContainer.Mp4,
            ExpectedTrackId = expectedTrackId?.ToString("D") ?? string.Empty
        });
        return true;
    }

    public bool RequestStop(out string error)
    {
        error = string.Empty;
        if (State == DirectorRecordingState.Idle)
        {
            error = "recording-idle";
            return false;
        }

        channel.SendPacket(new DirectorRecordingSessionRequest
        {
            Action = (int)DirectorRecordingSessionAction.Stop
        });
        return true;
    }

    public bool Toggle(out string error)
        => State is DirectorRecordingState.Idle or DirectorRecordingState.Error
            ? RequestStart(out error)
            : RequestStop(out error);

    public void RequestStatus()
        => channel.SendPacket(new DirectorRecordingSessionRequest
        {
            Action = (int)DirectorRecordingSessionAction.Status
        });

    public void RefreshConfiguration()
    {
        bool shouldLock = config.LockInputWhileRecording
            && State is DirectorRecordingState.Armed
                or DirectorRecordingState.Starting
                or DirectorRecordingState.Recording;
        inputLockHandler(shouldLock);
    }

    public void StopIfSourceCompleted(DirectorRecordingSourceType sourceType)
    {
        if (context?.Track.SourceType == sourceType
            && State is DirectorRecordingState.Armed
                or DirectorRecordingState.Starting
                or DirectorRecordingState.Recording)
        {
            StopLocal(sendReport: true);
        }
    }

    public void FailActiveSource(string error)
    {
        if (context is null)
        {
            Fail(error);
            return;
        }

        pendingTerminalError = string.IsNullOrWhiteSpace(error)
            ? "recording-source-failed"
            : error;
        if (pendingTerminalError.Length > 512)
        {
            pendingTerminalError = pendingTerminalError[^512..];
        }
        LastError = pendingTerminalError;
        Report(DirectorRecordingWorkerPhase.Error, pendingTerminalError);
        StopLocal(sendReport: false);
    }

    public void OnGameTick()
    {
        DirectorRecordingStartContext? activeContext = context;
        if (State != DirectorRecordingState.Armed || activeContext is null
            || capi.World.ElapsedMilliseconds < activeContext.StartWorldMilliseconds)
        {
            return;
        }

        if (obsPrepareTask is { IsCompleted: false })
        {
            if (capi.World.ElapsedMilliseconds - activeContext.StartWorldMilliseconds > 1000L)
            {
                Fail("obs-prepare-timeout");
            }
            return;
        }

        if (obsPrepareTask?.Exception is Exception prepareException)
        {
            Fail(Unwrap(prepareException).Message);
            return;
        }

        StartObs(activeContext);
    }

    public void ResetWorld()
    {
        DirectorRecordingStartContext? activeContext = context;
        inputLockHandler(false);
        if (activeContext is not null
            && obsClient is not null
            && State is DirectorRecordingState.Starting
                or DirectorRecordingState.Recording
                or DirectorRecordingState.Stopping)
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
                string outputPath = obsClient.StopRecordingAsync(timeout.Token).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    DirectorRecordingResult result = CreateObsResult(activeContext, outputPath);
                    LastActualStartUtc = result.ActualStartUtc;
                    LastOutputPath = outputPath;
                    DirectorRecordingFiles.WriteSidecar(activeContext, result);
                }
            }
            catch (Exception exception)
            {
                capi.Logger.Warning("VS Director: OBS did not confirm stop while leaving world: {0}", exception.Message);
            }
        }

        if (activeContext is not null)
        {
            ReleaseSource(activeContext);
        }
        context = null;
        obsPrepareTask = null;
        obsClient?.Dispose();
        obsClient = null;
        pendingTerminalError = string.Empty;
        SetState(DirectorRecordingState.Idle, string.Empty);
    }

    private void OnControl(DirectorRecordingControl control)
    {
        DirectorRecordingSessionAction action = Enum.IsDefined(typeof(DirectorRecordingSessionAction), control.Action)
            ? (DirectorRecordingSessionAction)control.Action
            : DirectorRecordingSessionAction.Status;
        if (action == DirectorRecordingSessionAction.Stop)
        {
            if (context is null || string.Equals(context.SessionId, control.SessionId, StringComparison.Ordinal))
            {
                StopLocal(sendReport: true);
            }
            return;
        }

        if (action == DirectorRecordingSessionAction.Start)
        {
            Arm(control);
        }
    }

    private void Arm(DirectorRecordingControl control)
    {
        if (State is not DirectorRecordingState.Idle
            and not DirectorRecordingState.Error
            and not DirectorRecordingState.Requesting)
        {
            ReportSession(control.SessionId, DirectorRecordingWorkerPhase.Error, "client-busy");
            return;
        }

        if (!TryParseTrack(control, out DirectorRecordingTrack? track, out string parseError))
        {
            ReportSession(control.SessionId, DirectorRecordingWorkerPhase.Error, parseError);
            Fail(parseError, sendReport: false);
            return;
        }

        DateTimeOffset startUtc;
        try
        {
            startUtc = new DateTimeOffset(control.StartUtcTicks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            ReportSession(control.SessionId, DirectorRecordingWorkerPhase.Error, "invalid-start-time");
            Fail("invalid-start-time", sendReport: false);
            return;
        }

        (bool activated, string activationError) = activateTrack(track, true);
        if (!activated)
        {
            string error = string.IsNullOrWhiteSpace(activationError)
                ? "track-activation-failed"
                : activationError;
            ReportSession(control.SessionId, DirectorRecordingWorkerPhase.Error, error);
            Fail(error, sendReport: false);
            return;
        }

        context = new DirectorRecordingStartContext
        {
            SessionId = control.SessionId,
            Track = track,
            StartUtc = startUtc,
            StartWorldMilliseconds = ResolveLocalStartMilliseconds(control, startUtc),
            FramesPerSecond = DirectorTimecode.NormalizeFrameRate(control.FramesPerSecond),
            Container = DirectorRecordingContainer.Mp4,
            Backend = DirectorRecordingBackend.ObsWebSocket,
            OutputDirectory = ResolveOutputDirectory(),
            ClientPlayerUid = capi.World.Player?.PlayerUID ?? string.Empty
        };
        DirectorRecordingStartContext armedContext = context;
        actualStartWorldMilliseconds = 0L;
        actualStartUtc = default;
        pendingTerminalError = string.Empty;
        sourceReleased = false;
        LastError = string.Empty;
        LastOutputPath = string.Empty;
        LastActualStartUtc = default;
        if (config.LockInputWhileRecording)
        {
            inputLockHandler(true);
        }

        SetState(DirectorRecordingState.Armed, track.Name);
        try
        {
            obsClient?.Dispose();
            obsClient = new DirectorObsWebSocketClient(config.ObsWebSocketUrl, config.ObsWebSocketPassword);
            obsPrepareTask = obsClient.PrepareAsync();
            ObserveTask(
                obsPrepareTask,
                () =>
                {
                    if (ReferenceEquals(context, armedContext) && State == DirectorRecordingState.Armed)
                    {
                        Report(DirectorRecordingWorkerPhase.Armed, string.Empty);
                    }
                },
                exception =>
                {
                    if (ReferenceEquals(context, armedContext) && State == DirectorRecordingState.Armed)
                    {
                        Fail(exception.Message);
                    }
                });
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
    }

    private void StartObs(DirectorRecordingStartContext activeContext)
    {
        SetState(DirectorRecordingState.Starting, activeContext.Track.Name);
        DirectorObsWebSocketClient? activeObs = obsClient;
        if (activeObs is null)
        {
            Fail("obs-not-ready");
            return;
        }

        ObserveTask(
            activeObs.StartRecordingAsync(),
            () =>
            {
                if (!ReferenceEquals(context, activeContext) || State != DirectorRecordingState.Starting)
                {
                    return;
                }

                (bool activated, string activationError) = activateTrack(activeContext.Track, false);
                if (!activated)
                {
                    FailActiveSource(string.IsNullOrWhiteSpace(activationError)
                        ? "track-activation-failed"
                        : activationError);
                    return;
                }

                actualStartWorldMilliseconds = capi.World.ElapsedMilliseconds;
                actualStartUtc = DateTimeOffset.UtcNow;
                SetState(DirectorRecordingState.Recording, "obs");
                Report(DirectorRecordingWorkerPhase.Recording, string.Empty);
            },
            exception =>
            {
                if (ReferenceEquals(context, activeContext))
                {
                    Fail(exception.Message);
                }
            });
    }

    private void StopLocal(bool sendReport)
    {
        DirectorRecordingStartContext? activeContext = context;
        if (activeContext is null)
        {
            inputLockHandler(false);
            SetState(DirectorRecordingState.Idle, string.Empty);
            return;
        }
        if (State == DirectorRecordingState.Stopping)
        {
            return;
        }

        DirectorRecordingState previousState = State;
        SetState(DirectorRecordingState.Stopping, activeContext.Track.Name);
        inputLockHandler(false);
        DirectorObsWebSocketClient? activeObs = obsClient;
        if (activeObs is null || previousState is DirectorRecordingState.Armed or DirectorRecordingState.Requesting)
        {
            ReleaseSource(activeContext);
            CompleteStop(activeContext, null, sendReport);
            return;
        }

        ObserveTask(
            activeObs.StopRecordingAsync(),
            outputPath =>
            {
                if (!ReferenceEquals(context, activeContext))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    Fail("obs-output-path-missing", sendReport);
                    return;
                }

                string resolvedOutput = outputPath;
                DirectorRecordingResult result = CreateObsResult(activeContext, resolvedOutput);
                LastActualStartUtc = result.ActualStartUtc;
                LastOutputPath = resolvedOutput;
                ReleaseSource(activeContext);
                DirectorRecordingFiles.WriteSidecar(activeContext, result);
                CompleteStop(activeContext, result, sendReport);
            },
            exception =>
            {
                if (ReferenceEquals(context, activeContext))
                {
                    Fail(exception.Message, sendReport);
                }
            });
    }

    private DirectorRecordingResult CreateObsResult(
        DirectorRecordingStartContext activeContext,
        string outputPath)
        => new()
        {
            Backend = "obs-websocket",
            OutputPath = outputPath,
            StopUtc = DateTimeOffset.UtcNow,
            ActualStartUtc = actualStartUtc == default ? activeContext.StartUtc : actualStartUtc,
            StartOffsetSeconds = ResolveStartOffsetSeconds(activeContext),
            FrameCount = 0,
            DurationSeconds = ResolveElapsedSeconds(activeContext),
            Failure = pendingTerminalError
        };

    private void CompleteStop(
        DirectorRecordingStartContext activeContext,
        DirectorRecordingResult? result,
        bool sendReport)
    {
        if (!ReferenceEquals(context, activeContext))
        {
            return;
        }

        ReleaseSource(activeContext);
        string terminalError = pendingTerminalError;
        if (sendReport && terminalError.Length == 0)
        {
            Report(
                DirectorRecordingWorkerPhase.Stopped,
                LastError,
                LastOutputPath.Length > 0 ? LastOutputPath : result?.OutputPath ?? string.Empty);
        }

        context = null;
        obsPrepareTask = null;
        obsClient?.Dispose();
        obsClient = null;
        pendingTerminalError = string.Empty;
        SetState(
            terminalError.Length == 0 ? DirectorRecordingState.Idle : DirectorRecordingState.Error,
            terminalError.Length == 0 ? LastOutputPath : terminalError);
    }

    private void Fail(string message, bool sendReport = true)
    {
        LastError = string.IsNullOrWhiteSpace(message) ? "recording-error" : message;
        if (LastError.Length > 512)
        {
            LastError = LastError[^512..];
        }

        if (sendReport)
        {
            Report(DirectorRecordingWorkerPhase.Error, LastError);
        }
        if (context is { } activeContext)
        {
            ReleaseSource(activeContext);
        }
        inputLockHandler(false);
        context = null;
        obsPrepareTask = null;
        obsClient?.Dispose();
        obsClient = null;
        pendingTerminalError = string.Empty;
        SetState(DirectorRecordingState.Error, LastError);
    }

    private void ReleaseSource(DirectorRecordingStartContext activeContext)
    {
        if (sourceReleased)
        {
            return;
        }

        sourceReleased = true;
        try
        {
            SourceCaptureStopped?.Invoke(activeContext.Track.DeepClone());
        }
        catch (Exception exception)
        {
            capi.Logger.Warning("VS Director: recording source cleanup failed: {0}", exception.Message);
        }
    }

    private void Report(DirectorRecordingWorkerPhase phase, string message, string outputPath = "")
    {
        string sessionId = context?.SessionId ?? string.Empty;
        if (sessionId.Length > 0)
        {
            ReportSession(sessionId, phase, message, outputPath);
        }
    }

    private void ReportSession(
        string sessionId,
        DirectorRecordingWorkerPhase phase,
        string message,
        string outputPath = "")
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        channel.SendPacket(new DirectorRecordingWorkerReport
        {
            SessionId = sessionId,
            Phase = (int)phase,
            Message = message,
            OutputPath = outputPath
        });
    }

    private void OnSessionStatus(DirectorRecordingSessionStatus status)
    {
        ServerStatusReceived?.Invoke(status);
        if (State == DirectorRecordingState.Requesting
            && status.Phase == (int)DirectorRecordingWorkerPhase.Error)
        {
            Fail(status.Message, sendReport: false);
        }
    }

    private void SetState(DirectorRecordingState state, string detail)
    {
        State = state;
        StateChanged?.Invoke(state, detail);
    }

    private string ResolveOutputDirectory()
    {
        string configured = config.RecordingOutputDirectory?.Trim() ?? string.Empty;
        return configured.Length == 0
            ? Path.GetFullPath(Path.Combine(GamePaths.Videos, "VSDirector"))
            : Path.GetFullPath(configured);
    }

    private long ResolveLocalStartMilliseconds(DirectorRecordingControl control, DateTimeOffset startUtc)
    {
        int fallbackDelay = Math.Clamp(control.StartDelayMilliseconds, 0, 15000);
        double utcDelay = (startUtc - DateTimeOffset.UtcNow).TotalMilliseconds;
        double selectedDelay = double.IsFinite(utcDelay)
            && utcDelay >= -1000d
            && utcDelay <= fallbackDelay + 2000d
                ? Math.Max(0d, utcDelay)
                : fallbackDelay;
        return capi.World.ElapsedMilliseconds + (long)Math.Round(selectedDelay);
    }

    private double ResolveElapsedSeconds(DirectorRecordingStartContext activeContext)
    {
        long start = actualStartWorldMilliseconds > 0L
            ? actualStartWorldMilliseconds
            : activeContext.StartWorldMilliseconds;
        return Math.Max(0d, (capi.World.ElapsedMilliseconds - start) / 1000d);
    }

    private double ResolveStartOffsetSeconds(DirectorRecordingStartContext activeContext)
    {
        if (actualStartWorldMilliseconds <= 0L)
        {
            return 0d;
        }
        return Math.Max(
            0d,
            (actualStartWorldMilliseconds - activeContext.StartWorldMilliseconds) / 1000d);
    }

    private static bool TryParseTrack(
        DirectorRecordingControl control,
        out DirectorRecordingTrack track,
        out string error)
    {
        track = new DirectorRecordingTrack();
        error = string.Empty;
        if (!Enum.IsDefined(typeof(DirectorRecordingSourceType), control.SourceType))
        {
            error = "invalid-track-type";
            return false;
        }

        DirectorRecordingSourceType sourceType = (DirectorRecordingSourceType)control.SourceType;
        Guid sourceId = Guid.Empty;
        if (DirectorRecordingTrack.RequiresSource(sourceType)
            && (!Guid.TryParse(control.SourceId, out sourceId) || sourceId == Guid.Empty))
        {
            error = "invalid-track-source";
            return false;
        }

        Guid trackId = Guid.TryParse(control.TrackId, out Guid parsedTrackId) ? parsedTrackId : Guid.Empty;
        track = new DirectorRecordingTrack
        {
            Id = trackId,
            Name = string.IsNullOrWhiteSpace(control.TrackName) ? "Program" : control.TrackName.Trim(),
            SourceType = sourceType,
            SourceId = sourceId,
            Enabled = true
        };
        return true;
    }

    private void ObserveTask(Task task, Action success, Action<Exception> failure)
    {
        _ = task.ContinueWith(
            completed =>
            {
                if (disposed)
                {
                    return;
                }

                capi.Event.EnqueueMainThreadTask(
                    () =>
                    {
                        if (disposed)
                        {
                            return;
                        }

                        if (completed.IsCanceled)
                        {
                            DirectorRecordingCallbackGuard.InvokeFailure(
                                failure,
                                new OperationCanceledException(),
                                LogObservedCallbackFailure);
                        }
                        else if (completed.Exception is Exception exception)
                        {
                            DirectorRecordingCallbackGuard.InvokeFailure(
                                failure,
                                Unwrap(exception),
                                LogObservedCallbackFailure);
                        }
                        else
                        {
                            DirectorRecordingCallbackGuard.InvokeSuccess(
                                success,
                                failure,
                                LogObservedCallbackFailure);
                        }
                    },
                    "vsdirector-recording-task");
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ObserveTask<T>(Task<T> task, Action<T> success, Action<Exception> failure)
    {
        _ = task.ContinueWith(
            completed =>
            {
                if (disposed)
                {
                    return;
                }

                capi.Event.EnqueueMainThreadTask(
                    () =>
                    {
                        if (disposed)
                        {
                            return;
                        }

                        if (completed.IsCanceled)
                        {
                            DirectorRecordingCallbackGuard.InvokeFailure(
                                failure,
                                new OperationCanceledException(),
                                LogObservedCallbackFailure);
                        }
                        else if (completed.Exception is Exception exception)
                        {
                            DirectorRecordingCallbackGuard.InvokeFailure(
                                failure,
                                Unwrap(exception),
                                LogObservedCallbackFailure);
                        }
                        else
                        {
                            DirectorRecordingCallbackGuard.InvokeSuccess(
                                () => success(completed.Result),
                                failure,
                                LogObservedCallbackFailure);
                        }
                    },
                    "vsdirector-recording-task");
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void LogObservedCallbackFailure(Exception exception)
        => capi.Logger.Error("VS Director: asynchronous recording callback failed: {0}", exception);

    private static Exception Unwrap(Exception exception)
        => exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1
            ? aggregate.InnerExceptions[0]
            : exception;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        ResetWorld();
        disposed = true;
    }
}
