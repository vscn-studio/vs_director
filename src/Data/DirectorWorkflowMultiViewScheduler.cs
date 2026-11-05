using System.Diagnostics;

namespace VSDirector.Data;

public readonly record struct DirectorWorkflowCameraPose(
    double X,
    double Y,
    double Z,
    int Dimension,
    float Yaw,
    float Pitch,
    float Roll,
    float Fov);

public sealed class DirectorWorkflowMultiViewSettings
{
    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public bool RenderTerrain { get; init; } = true;

    public bool RenderEntities { get; init; } = true;

    public bool RenderLocalPlayer { get; init; }

    public bool RenderOpaqueParticles { get; init; } = true;
}

public readonly record struct DirectorWorkflowMultiViewPass(
    Guid ViewId,
    Guid PassId,
    string Name,
    double StartSeconds,
    double EndSeconds,
    bool SplitForOverlap);

public readonly record struct DirectorWorkflowFollowPoseContext(
    Guid WorkflowId,
    Guid PassId,
    Guid ViewId,
    Guid CardId,
    double TimelineSeconds,
    double SourceTimeSeconds,
    CameraFollowRig Rig);

public sealed class DirectorWorkflowViewPlan
{
    public Guid ViewId { get; init; }

    public Guid PassId { get; init; }

    public string PassName { get; init; } = string.Empty;

    public double PassStartSeconds { get; init; }

    public double PassEndSeconds { get; init; }

    public Guid CardId { get; init; }

    public string CardName { get; init; } = string.Empty;

    public DirectorRecordingSourceType SourceType { get; init; }

    public Guid SourceId { get; init; }

    public double SourceTimeSeconds { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public DirectorWorkflowCameraPose Pose { get; init; }

    public bool RenderTerrain { get; init; }

    public bool RenderEntities { get; init; }

    public bool RenderLocalPlayer { get; init; }

    public bool RenderOpaqueParticles { get; init; }
}

public enum DirectorWorkflowMultiViewErrorCode
{
    NotPrepared,
    NotRunning,
    InvalidTimeline,
    InvalidPass,
    DuplicateViewId,
    MissingSegment,
    UnsupportedSource,
    MissingCamera,
    MissingPath,
    PathSampleFailed,
    MissingSequence,
    SequenceSampleFailed,
    MissingFollowRig,
    FollowPoseUnavailable,
    InvalidPose,
    ResolverFailed
}

public readonly record struct DirectorWorkflowMultiViewError(
    DirectorWorkflowMultiViewErrorCode Code,
    Guid PassId,
    string PassName,
    Guid CardId,
    DirectorRecordingSourceType SourceType,
    Guid SourceId,
    string Detail);

public sealed class DirectorWorkflowMultiViewFrame
{
    public double TimelineSeconds { get; init; }

    public bool Completed { get; init; }

    public IReadOnlyList<DirectorWorkflowViewPlan> Views { get; init; }
        = Array.Empty<DirectorWorkflowViewPlan>();

    public IReadOnlyList<DirectorWorkflowMultiViewError> Errors { get; init; }
        = Array.Empty<DirectorWorkflowMultiViewError>();
}

/// <summary>
/// Resolves all active workflow export passes against one wall-clock sample.
/// The scheduler only reads camera resources and never advances world,
/// animation, or particle simulation state.
/// </summary>
public sealed class DirectorWorkflowMultiViewScheduler
{
    private const int MinimumDimension = 16;
    private const int MaximumDimension = 16384;

    private readonly Func<Guid, CameraPreset?> cameraResolver;
    private readonly Func<Guid, CameraPath?> pathResolver;
    private readonly Func<Guid, CameraSequence?> sequenceResolver;
    private readonly Func<Guid, CameraFollowRig?> followRigResolver;
    private readonly Func<DirectorWorkflowFollowPoseContext, DirectorWorkflowCameraPose?> followPoseResolver;
    private readonly Func<long> timestampProvider;
    private readonly double timestampFrequency;
    private readonly List<PreparedPass> preparedPasses = new();
    private DirectorWorkflowMultiViewSettings settings = new();
    private Guid workflowId;
    private double timelineStartSeconds;
    private double timelineEndSeconds;
    private double lastTimelineSeconds;
    private long startTimestamp;
    private bool clockStarted;
    private bool running;

    public DirectorWorkflowMultiViewScheduler(
        Func<Guid, CameraPreset?> cameraResolver,
        Func<Guid, CameraPath?> pathResolver,
        Func<Guid, CameraSequence?> sequenceResolver,
        Func<Guid, CameraFollowRig?> followRigResolver,
        Func<DirectorWorkflowFollowPoseContext, DirectorWorkflowCameraPose?> followPoseResolver,
        Func<long>? timestampProvider = null,
        double timestampFrequency = 0d)
    {
        this.cameraResolver = cameraResolver ?? throw new ArgumentNullException(nameof(cameraResolver));
        this.pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        this.sequenceResolver = sequenceResolver ?? throw new ArgumentNullException(nameof(sequenceResolver));
        this.followRigResolver = followRigResolver ?? throw new ArgumentNullException(nameof(followRigResolver));
        this.followPoseResolver = followPoseResolver ?? throw new ArgumentNullException(nameof(followPoseResolver));
        this.timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;
        this.timestampFrequency = timestampFrequency > 0d && double.IsFinite(timestampFrequency)
            ? timestampFrequency
            : Stopwatch.Frequency;
    }

    public bool IsPrepared => preparedPasses.Count > 0;

    public bool IsRunning => running;

    public Guid WorkflowId => workflowId;

    public double StartSeconds => timelineStartSeconds;

    public double EndSeconds => timelineEndSeconds;

    public IReadOnlyList<DirectorWorkflowMultiViewPass> Passes
        => preparedPasses.Select(pass => pass.Descriptor).ToArray();

    public bool Prepare(
        DirectorWorkflow workflow,
        IReadOnlyList<DirectorWorkflowExportPass> passes,
        DirectorWorkflowMultiViewSettings? requestedSettings,
        out IReadOnlyList<DirectorWorkflowMultiViewError> errors)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(passes);
        Reset();

        DirectorWorkflowMultiViewSettings normalizedSettings = NormalizeSettings(requestedSettings);
        List<DirectorWorkflowMultiViewError> failures = new();
        HashSet<Guid> viewIds = new();
        List<PreparedPass> candidates = new();
        foreach (DirectorWorkflowExportPass? pass in passes)
        {
            if (!TryPreparePass(pass, viewIds, out PreparedPass? prepared, out DirectorWorkflowMultiViewError failure))
            {
                failures.Add(failure);
                continue;
            }

            candidates.Add(prepared!);
        }

        if (candidates.Count == 0 && failures.Count == 0)
        {
            failures.Add(Error(
                DirectorWorkflowMultiViewErrorCode.InvalidPass,
                null,
                null,
                "workflow-empty"));
        }

        if (failures.Count > 0)
        {
            errors = failures;
            return false;
        }

        workflowId = workflow.Id;
        settings = normalizedSettings;
        preparedPasses.AddRange(candidates
            .OrderBy(pass => pass.Descriptor.StartSeconds)
            .ThenBy(pass => pass.Descriptor.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pass => pass.Descriptor.ViewId));
        timelineStartSeconds = preparedPasses.Min(pass => pass.Descriptor.StartSeconds);
        timelineEndSeconds = preparedPasses.Max(pass => pass.Descriptor.EndSeconds);
        lastTimelineSeconds = timelineStartSeconds;
        errors = Array.Empty<DirectorWorkflowMultiViewError>();
        return true;
    }

    public bool Start(out DirectorWorkflowMultiViewError error)
    {
        if (!IsPrepared)
        {
            error = Error(
                DirectorWorkflowMultiViewErrorCode.NotPrepared,
                null,
                null,
                "workflow-not-prepared");
            return false;
        }

        startTimestamp = 0L;
        clockStarted = false;
        lastTimelineSeconds = timelineStartSeconds;
        running = true;
        error = default;
        return true;
    }

    /// <summary>
    /// Samples the wall clock exactly once and resolves every pass at that same
    /// timeline position. The first call establishes time zero at the first
    /// rendered frame instead of consuming setup time before rendering starts.
    /// </summary>
    public DirectorWorkflowMultiViewFrame EvaluateFrame()
    {
        if (!IsPrepared)
        {
            return FailureFrame(
                timelineStartSeconds,
                DirectorWorkflowMultiViewErrorCode.NotPrepared,
                "workflow-not-prepared");
        }
        if (!running)
        {
            return FailureFrame(
                lastTimelineSeconds,
                DirectorWorkflowMultiViewErrorCode.NotRunning,
                "workflow-not-running",
                completed: lastTimelineSeconds >= timelineEndSeconds - DirectorWorkflowPlanner.TimeEpsilon);
        }

        long timestamp = timestampProvider();
        if (!clockStarted)
        {
            startTimestamp = timestamp;
            clockStarted = true;
        }

        double elapsedSeconds = Math.Max(0d, (timestamp - startTimestamp) / timestampFrequency);
        double timelineSeconds = Math.Clamp(
            timelineStartSeconds + elapsedSeconds,
            lastTimelineSeconds,
            timelineEndSeconds);
        lastTimelineSeconds = timelineSeconds;
        bool completed = timelineSeconds >= timelineEndSeconds - DirectorWorkflowPlanner.TimeEpsilon;
        DirectorWorkflowMultiViewFrame frame = EvaluateAtCore(timelineSeconds, completed);
        if (completed)
        {
            running = false;
        }
        return frame;
    }

    public DirectorWorkflowMultiViewFrame EvaluateAt(double timelineSeconds)
    {
        if (!IsPrepared)
        {
            return FailureFrame(
                timelineSeconds,
                DirectorWorkflowMultiViewErrorCode.NotPrepared,
                "workflow-not-prepared");
        }
        if (!double.IsFinite(timelineSeconds))
        {
            return FailureFrame(
                0d,
                DirectorWorkflowMultiViewErrorCode.InvalidTimeline,
                "workflow-time-invalid");
        }

        return EvaluateAtCore(
            timelineSeconds,
            timelineSeconds >= timelineEndSeconds - DirectorWorkflowPlanner.TimeEpsilon);
    }

    public void Reset()
    {
        preparedPasses.Clear();
        workflowId = Guid.Empty;
        timelineStartSeconds = 0d;
        timelineEndSeconds = 0d;
        lastTimelineSeconds = 0d;
        startTimestamp = 0L;
        clockStarted = false;
        running = false;
        settings = new DirectorWorkflowMultiViewSettings();
    }

    private DirectorWorkflowMultiViewFrame EvaluateAtCore(double timelineSeconds, bool completed)
    {
        List<DirectorWorkflowViewPlan> views = new();
        List<DirectorWorkflowMultiViewError> errors = new();
        foreach (PreparedPass pass in preparedPasses)
        {
            DirectorWorkflowMultiViewPass descriptor = pass.Descriptor;
            if (timelineSeconds < descriptor.StartSeconds
                || timelineSeconds > descriptor.EndSeconds)
            {
                continue;
            }

            DirectorWorkflowExportSegment? segment = pass.Export.Resolve(timelineSeconds);
            if (segment is null)
            {
                errors.Add(Error(
                    DirectorWorkflowMultiViewErrorCode.MissingSegment,
                    pass.Export,
                    null,
                    "workflow-segment-missing"));
                continue;
            }

            double sourceTimeSeconds = DirectorWorkflowPlanner.SegmentLocalTime(segment, timelineSeconds);
            if (!TryResolvePose(
                    pass,
                    segment,
                    timelineSeconds,
                    sourceTimeSeconds,
                    out DirectorWorkflowCameraPose pose,
                    out DirectorWorkflowMultiViewError failure))
            {
                errors.Add(failure);
                continue;
            }

            views.Add(new DirectorWorkflowViewPlan
            {
                ViewId = descriptor.ViewId,
                PassId = descriptor.PassId,
                PassName = descriptor.Name,
                PassStartSeconds = descriptor.StartSeconds,
                PassEndSeconds = descriptor.EndSeconds,
                CardId = segment.CardId,
                CardName = segment.CardName,
                SourceType = segment.SourceType,
                SourceId = segment.SourceId,
                SourceTimeSeconds = sourceTimeSeconds,
                Width = settings.Width,
                Height = settings.Height,
                Pose = pose,
                RenderTerrain = settings.RenderTerrain,
                RenderEntities = settings.RenderEntities,
                RenderLocalPlayer = settings.RenderLocalPlayer,
                RenderOpaqueParticles = settings.RenderOpaqueParticles
            });
        }

        return new DirectorWorkflowMultiViewFrame
        {
            TimelineSeconds = timelineSeconds,
            Completed = completed,
            Views = views,
            Errors = errors
        };
    }

    private bool TryResolvePose(
        PreparedPass pass,
        DirectorWorkflowExportSegment segment,
        double timelineSeconds,
        double sourceTimeSeconds,
        out DirectorWorkflowCameraPose pose,
        out DirectorWorkflowMultiViewError error)
    {
        pose = default;
        error = default;
        try
        {
            switch (segment.SourceType)
            {
                case DirectorRecordingSourceType.StaticCamera:
                    {
                        CameraPreset? camera = cameraResolver(segment.SourceId);
                        if (camera is null)
                        {
                            error = Error(
                                DirectorWorkflowMultiViewErrorCode.MissingCamera,
                                pass.Export,
                                segment,
                                "workflow-camera-missing");
                            return false;
                        }

                        pose = FromPreset(camera);
                        break;
                    }
                case DirectorRecordingSourceType.Path:
                    {
                        CameraPath? path = pathResolver(segment.SourceId);
                        if (path is null)
                        {
                            error = Error(
                                DirectorWorkflowMultiViewErrorCode.MissingPath,
                                pass.Export,
                                segment,
                                "workflow-path-missing");
                            return false;
                        }

                        double elapsed = sourceTimeSeconds
                            * CameraPathTiming.NormalizePlaybackSpeed(path.PlaybackSpeed);
                        CameraPathTime pathTime = CameraPathSampler.ResolvePlaybackTime(
                            path.DurationSeconds,
                            elapsed,
                            path.LoopMode,
                            reverse: false);
                        if (!CameraPathSampler.TryEvaluate(path, pathTime.TimeSeconds, out CameraKeyframe frame))
                        {
                            error = Error(
                                DirectorWorkflowMultiViewErrorCode.PathSampleFailed,
                                pass.Export,
                                segment,
                                "workflow-path-sample-failed");
                            return false;
                        }

                        pose = FromKeyframe(frame);
                        break;
                    }
                case DirectorRecordingSourceType.Sequence:
                    {
                        CameraSequence? sequence = sequenceResolver(segment.SourceId);
                        if (sequence is null)
                        {
                            error = Error(
                                DirectorWorkflowMultiViewErrorCode.MissingSequence,
                                pass.Export,
                                segment,
                                "workflow-sequence-missing");
                            return false;
                        }
                        if (!CameraSequenceSampler.TryResolve(sequence, sourceTimeSeconds, out CameraSequenceTime time)
                            || time.ShotIndex < 0
                            || time.ShotIndex >= sequence.Shots.Count)
                        {
                            error = Error(
                                DirectorWorkflowMultiViewErrorCode.SequenceSampleFailed,
                                pass.Export,
                                segment,
                                "workflow-sequence-sample-failed");
                            return false;
                        }

                        CameraPreset? camera = cameraResolver(sequence.Shots[time.ShotIndex].CameraId);
                        if (camera is null)
                        {
                            error = Error(
                                DirectorWorkflowMultiViewErrorCode.MissingCamera,
                                pass.Export,
                                segment,
                                "workflow-sequence-camera-missing");
                            return false;
                        }

                        pose = FromPreset(camera);
                        break;
                    }
                case DirectorRecordingSourceType.Follow:
                    {
                        CameraFollowRig? rig = followRigResolver(segment.SourceId);
                        if (rig is null)
                        {
                            error = Error(
                                DirectorWorkflowMultiViewErrorCode.MissingFollowRig,
                                pass.Export,
                                segment,
                                "workflow-follow-missing");
                            return false;
                        }

                        DirectorWorkflowFollowPoseContext context = new(
                            workflowId,
                            pass.Descriptor.PassId,
                            pass.Descriptor.ViewId,
                            segment.CardId,
                            timelineSeconds,
                            sourceTimeSeconds,
                            rig.DeepClone());
                        DirectorWorkflowCameraPose? resolved = followPoseResolver(context);
                        if (resolved is null)
                        {
                            error = Error(
                                DirectorWorkflowMultiViewErrorCode.FollowPoseUnavailable,
                                pass.Export,
                                segment,
                                "workflow-follow-pose-unavailable");
                            return false;
                        }

                        pose = resolved.Value;
                        break;
                    }
                default:
                    error = Error(
                        DirectorWorkflowMultiViewErrorCode.UnsupportedSource,
                        pass.Export,
                        segment,
                        "workflow-source-unsupported");
                    return false;
            }
        }
        catch (Exception exception)
        {
            error = Error(
                DirectorWorkflowMultiViewErrorCode.ResolverFailed,
                pass.Export,
                segment,
                exception.Message);
            return false;
        }

        if (!IsValidPose(pose))
        {
            error = Error(
                DirectorWorkflowMultiViewErrorCode.InvalidPose,
                pass.Export,
                segment,
                "workflow-pose-invalid");
            return false;
        }

        pose = pose with { Fov = Math.Clamp(pose.Fov, 10f, 170f) };
        return true;
    }

    private static bool TryPreparePass(
        DirectorWorkflowExportPass? pass,
        HashSet<Guid> viewIds,
        out PreparedPass? prepared,
        out DirectorWorkflowMultiViewError error)
    {
        prepared = null;
        if (pass is null
            || pass.Id == Guid.Empty
            || !double.IsFinite(pass.StartSeconds)
            || !double.IsFinite(pass.EndSeconds)
            || pass.EndSeconds - pass.StartSeconds < DirectorWorkflowPlanner.MinCardDurationSeconds
            || pass.Segments is null
            || pass.Segments.Count == 0)
        {
            error = Error(
                DirectorWorkflowMultiViewErrorCode.InvalidPass,
                pass,
                null,
                "workflow-pass-invalid");
            return false;
        }
        if (!viewIds.Add(pass.Id))
        {
            error = Error(
                DirectorWorkflowMultiViewErrorCode.DuplicateViewId,
                pass,
                null,
                "workflow-view-id-duplicate");
            return false;
        }

        foreach (DirectorWorkflowExportSegment? segment in pass.Segments)
        {
            if (segment is null
                || segment.SourceId == Guid.Empty
                || !DirectorWorkflowPlanner.IsExportSource(segment.SourceType)
                || !double.IsFinite(segment.StartSeconds)
                || !double.IsFinite(segment.EndSeconds)
                || segment.EndSeconds - segment.StartSeconds < DirectorWorkflowPlanner.MinCardDurationSeconds
                || segment.StartSeconds < pass.StartSeconds - DirectorWorkflowPlanner.TimeEpsilon
                || segment.EndSeconds > pass.EndSeconds + DirectorWorkflowPlanner.TimeEpsilon)
            {
                error = Error(
                    DirectorWorkflowMultiViewErrorCode.InvalidPass,
                    pass,
                    segment,
                    "workflow-segment-invalid");
                return false;
            }
        }

        DirectorWorkflowExportPass snapshot = pass.DeepClone();
        DirectorWorkflowMultiViewPass descriptor = new(
            snapshot.Id,
            snapshot.Id,
            string.IsNullOrWhiteSpace(snapshot.Name) ? "Workflow" : snapshot.Name.Trim(),
            snapshot.StartSeconds,
            snapshot.EndSeconds,
            snapshot.SplitForOverlap);
        prepared = new PreparedPass(snapshot, descriptor);
        error = default;
        return true;
    }

    private static DirectorWorkflowMultiViewSettings NormalizeSettings(
        DirectorWorkflowMultiViewSettings? source)
    {
        source ??= new DirectorWorkflowMultiViewSettings();
        return new DirectorWorkflowMultiViewSettings
        {
            Width = Math.Clamp(source.Width, MinimumDimension, MaximumDimension),
            Height = Math.Clamp(source.Height, MinimumDimension, MaximumDimension),
            RenderTerrain = source.RenderTerrain,
            RenderEntities = source.RenderEntities,
            RenderLocalPlayer = source.RenderLocalPlayer,
            RenderOpaqueParticles = source.RenderOpaqueParticles
        };
    }

    private static DirectorWorkflowCameraPose FromPreset(CameraPreset camera)
        => new(
            camera.X,
            camera.Y,
            camera.Z,
            camera.Dimension,
            camera.Yaw,
            camera.Pitch,
            camera.Roll,
            camera.Fov);

    private static DirectorWorkflowCameraPose FromKeyframe(CameraKeyframe frame)
        => new(
            frame.X,
            frame.Y,
            frame.Z,
            frame.Dimension,
            frame.Yaw,
            frame.Pitch,
            frame.Roll,
            frame.Fov);

    private static bool IsValidPose(DirectorWorkflowCameraPose pose)
        => double.IsFinite(pose.X)
            && double.IsFinite(pose.Y)
            && double.IsFinite(pose.Z)
            && float.IsFinite(pose.Yaw)
            && float.IsFinite(pose.Pitch)
            && float.IsFinite(pose.Roll)
            && float.IsFinite(pose.Fov);

    private DirectorWorkflowMultiViewFrame FailureFrame(
        double timelineSeconds,
        DirectorWorkflowMultiViewErrorCode code,
        string detail,
        bool completed = false)
        => new()
        {
            TimelineSeconds = double.IsFinite(timelineSeconds) ? timelineSeconds : 0d,
            Completed = completed,
            Errors = new[] { Error(code, null, null, detail) }
        };

    private static DirectorWorkflowMultiViewError Error(
        DirectorWorkflowMultiViewErrorCode code,
        DirectorWorkflowExportPass? pass,
        DirectorWorkflowExportSegment? segment,
        string detail)
        => new(
            code,
            pass?.Id ?? Guid.Empty,
            pass?.Name ?? string.Empty,
            segment?.CardId ?? Guid.Empty,
            segment?.SourceType ?? DirectorRecordingSourceType.Program,
            segment?.SourceId ?? Guid.Empty,
            detail ?? string.Empty);

    private sealed record PreparedPass(
        DirectorWorkflowExportPass Export,
        DirectorWorkflowMultiViewPass Descriptor);
}
