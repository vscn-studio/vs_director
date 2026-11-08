using System.Diagnostics;
using VSDirector.Data;

namespace VSDirector.Recording;

internal sealed class DirectorLiveWorkflowRunner
{
    private readonly Func<DirectorRecordingTrack, double, (bool Success, string Error)> applyTrack;
    private readonly Func<long> timestampProvider;
    private readonly double timestampFrequency;
    private DirectorRecordingTrack? preparedTrack;
    private double timelineStartSeconds;
    private double durationSeconds;
    private Func<double>? timelineProvider;
    private long startTimestamp;
    private bool clockStarted;
    private bool running;
    private bool completionPending;
    private string failurePending = string.Empty;

    public DirectorLiveWorkflowRunner(
        Func<DirectorRecordingTrack, double, (bool Success, string Error)> applyTrack,
        Func<long>? timestampProvider = null,
        double timestampFrequency = 0d)
    {
        this.applyTrack = applyTrack;
        this.timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;
        this.timestampFrequency = timestampFrequency > 0d
            ? timestampFrequency
            : Stopwatch.Frequency;
    }

    public bool IsPrepared => preparedTrack is not null;

    public Guid WorkflowId => preparedTrack?.SourceId ?? Guid.Empty;

    public bool Prepare(
        DirectorWorkflow workflow,
        IReadOnlyList<DirectorWorkflowExportPass> passes,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(passes);
        Reset();
        error = string.Empty;

        if (passes.Count != 1 || passes[0].SplitForOverlap)
        {
            error = "workflow-live-overlap";
            return false;
        }

        DirectorWorkflowExportPass pass = passes[0];
        if (pass.Segments.Count == 0
            || !double.IsFinite(pass.StartSeconds)
            || !double.IsFinite(pass.EndSeconds)
            || pass.EndSeconds - pass.StartSeconds < DirectorWorkflowPlanner.MinCardDurationSeconds)
        {
            error = "workflow-empty";
            return false;
        }

        preparedTrack = new DirectorRecordingTrack
        {
            Id = workflow.Id,
            Name = workflow.Name,
            SourceType = DirectorRecordingSourceType.Workflow,
            SourceId = workflow.Id,
            Enabled = true,
            WorkflowSegments = pass.Segments
                .Select(segment => segment.DeepClone())
                .ToList()
        };
        timelineStartSeconds = pass.StartSeconds;
        durationSeconds = pass.EndSeconds - pass.StartSeconds;
        if (!Apply(timelineStartSeconds, out error))
        {
            Reset();
            return false;
        }

        return true;
    }

    public bool Start(Guid workflowId, out string error)
        => Start(workflowId, null, out error);

    public bool Start(Guid workflowId, Func<double>? externalTimelineProvider, out string error)
    {
        error = string.Empty;
        if (preparedTrack is null || preparedTrack.SourceId != workflowId)
        {
            error = "workflow-not-prepared";
            return false;
        }

        startTimestamp = 0L;
        clockStarted = false;
        completionPending = false;
        failurePending = string.Empty;
        timelineProvider = externalTimelineProvider;
        running = true;
        return true;
    }

    public void PrepareFrame()
    {
        if (!running || preparedTrack is null)
        {
            return;
        }

        double elapsed;
        if (timelineProvider is not null)
        {
            double timelineTime = timelineProvider();
            if (!double.IsFinite(timelineTime))
            {
                failurePending = "workflow-camera-failed";
                running = false;
                return;
            }

            elapsed = Math.Clamp(timelineTime - timelineStartSeconds, 0d, durationSeconds);
        }
        else
        {
            long timestamp = timestampProvider();
            if (!clockStarted)
            {
                startTimestamp = timestamp;
                clockStarted = true;
            }

            elapsed = Math.Min(
                durationSeconds,
                Math.Max(0d, (timestamp - startTimestamp) / timestampFrequency));
        }
        if (!Apply(timelineStartSeconds + elapsed, out string error))
        {
            failurePending = string.IsNullOrWhiteSpace(error)
                ? "workflow-camera-failed"
                : error;
            running = false;
            return;
        }

        if (elapsed >= durationSeconds - DirectorWorkflowPlanner.TimeEpsilon)
        {
            running = false;
            completionPending = true;
        }
    }

    public void Hold()
    {
        running = false;
    }

    public bool TryTakeCompletion()
    {
        bool completed = completionPending;
        completionPending = false;
        return completed;
    }

    public bool TryTakeFailure(out string error)
    {
        error = failurePending;
        failurePending = string.Empty;
        return error.Length > 0;
    }

    public void Reset()
    {
        running = false;
        completionPending = false;
        failurePending = string.Empty;
        timelineStartSeconds = 0d;
        durationSeconds = 0d;
        preparedTrack = null;
        startTimestamp = 0L;
        clockStarted = false;
        timelineProvider = null;
    }

    private bool Apply(double timeSeconds, out string error)
    {
        if (preparedTrack is null)
        {
            error = "workflow-not-prepared";
            return false;
        }

        try
        {
            (bool success, string applyError) = applyTrack(preparedTrack, timeSeconds);
            error = applyError;
            return success;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }
}

internal static class DirectorLiveWorkflowRuntime
{
    public static DirectorLiveWorkflowRunner? Runner { get; set; }
}
