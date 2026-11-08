using VSDirector.Data;

namespace VSDirector.Rendering;

internal readonly record struct DirectorWorkflowMultiViewTerminal(
    bool Completed,
    double TimelineSeconds,
    IReadOnlyList<DirectorWorkflowMultiViewManifestError> Errors);

internal interface IDirectorWorkflowMultiViewRenderDriver
{
    void BeforeOffscreenRender();

    void AfterOffscreenRender();
}

internal static class DirectorWorkflowMultiViewRenderRuntime
{
    private static IDirectorWorkflowMultiViewRenderDriver? driver;

    internal static IDirectorWorkflowMultiViewRenderDriver? Driver
    {
        get => Volatile.Read(ref driver);
        set => Volatile.Write(ref driver, value);
    }
}

/// <summary>
/// Resolves a workflow once per rendered client frame, before the offscreen
/// renderer repeats opaque draws. It does not tick the world or simulation.
/// </summary>
internal sealed class DirectorWorkflowMultiViewCapture : IDirectorWorkflowMultiViewRenderDriver
{
    private readonly DirectorWorkflowMultiViewScheduler scheduler;
    private readonly DirectorOffscreenMultiViewRenderer renderer;
    private readonly Func<DirectorWorkflowViewPlan, string?> prepareLocalActor;
    private readonly Action<DirectorWorkflowMultiViewTerminal> onTerminal;
    private DirectorWorkflowMultiViewTerminal? pendingTerminal;
    private bool active;
    private bool terminalDelivered;
    private bool renderFailureSubscribed;

    internal DirectorWorkflowMultiViewCapture(
        DirectorWorkflowMultiViewScheduler scheduler,
        DirectorOffscreenMultiViewRenderer renderer,
        Func<DirectorWorkflowViewPlan, string?> prepareLocalActor,
        Action<DirectorWorkflowMultiViewTerminal> onTerminal)
    {
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        this.prepareLocalActor = prepareLocalActor ?? throw new ArgumentNullException(nameof(prepareLocalActor));
        this.onTerminal = onTerminal ?? throw new ArgumentNullException(nameof(onTerminal));
        renderer.RenderFailed += OnRenderFailed;
        renderFailureSubscribed = true;
    }

    internal double TimelineSeconds { get; private set; }

    internal bool Start(out DirectorWorkflowMultiViewError error)
    {
        if (!scheduler.Start(out error))
        {
            return false;
        }

        pendingTerminal = null;
        terminalDelivered = false;
        active = true;
        TimelineSeconds = scheduler.StartSeconds;
        return true;
    }

    internal void Deactivate()
    {
        active = false;
        pendingTerminal = null;
        UnsubscribeRenderFailure();
        if (ReferenceEquals(DirectorWorkflowMultiViewRenderRuntime.Driver, this))
        {
            DirectorWorkflowMultiViewRenderRuntime.Driver = null;
        }
    }

    public void BeforeOffscreenRender()
    {
        if (!active || pendingTerminal is not null)
        {
            return;
        }

        try
        {
            DirectorWorkflowMultiViewFrame frame = scheduler.EvaluateFrame();
            TimelineSeconds = frame.TimelineSeconds;
            if (frame.Errors.Count > 0)
            {
                renderer.SetViews(Array.Empty<DirectorOffscreenViewRequest>());
                pendingTerminal = new DirectorWorkflowMultiViewTerminal(
                    false,
                    frame.TimelineSeconds,
                    frame.Errors.Select(ToManifestError).ToArray());
                return;
            }

            int[] activeDimensions = frame.Views
                .Select(view => view.Pose.Dimension)
                .Distinct()
                .Take(2)
                .ToArray();
            if (activeDimensions.Length > 1)
            {
                renderer.SetViews(Array.Empty<DirectorOffscreenViewRequest>());
                pendingTerminal = Failure(
                    frame.TimelineSeconds,
                    "ConcurrentDimensionsUnsupported",
                    frame.Views.FirstOrDefault(view => view.Pose.Dimension == activeDimensions[1]),
                    $"Active workflow views require dimensions {activeDimensions[0]} and {activeDimensions[1]} in one frame; the client has only one observation anchor.");
                return;
            }

            DirectorWorkflowViewPlan? primary = frame.Views
                .OrderBy(view => view.PassStartSeconds)
                .ThenBy(view => view.PassName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(view => view.ViewId)
                .FirstOrDefault();
            if (primary is not null)
            {
                string? actorError = prepareLocalActor(primary);
                if (!string.IsNullOrWhiteSpace(actorError))
                {
                    renderer.SetViews(Array.Empty<DirectorOffscreenViewRequest>());
                    pendingTerminal = Failure(
                        frame.TimelineSeconds,
                        "LocalPlayerPoseFailed",
                        primary,
                        actorError);
                    return;
                }
            }

            renderer.SetViews(frame.ToOffscreenViewRequests());
            if (frame.Completed)
            {
                pendingTerminal = new DirectorWorkflowMultiViewTerminal(
                    true,
                    frame.TimelineSeconds,
                    Array.Empty<DirectorWorkflowMultiViewManifestError>());
            }
        }
        catch (Exception exception)
        {
            try
            {
                renderer.SetViews(Array.Empty<DirectorOffscreenViewRequest>());
            }
            catch
            {
            }
            pendingTerminal = Failure(
                TimelineSeconds,
                "FramePlanFailed",
                null,
                exception.GetBaseException().Message);
        }
    }

    public void AfterOffscreenRender()
    {
        if (!active || terminalDelivered || pendingTerminal is not DirectorWorkflowMultiViewTerminal terminal)
        {
            return;
        }

        terminalDelivered = true;
        active = false;
        UnsubscribeRenderFailure();
        try
        {
            onTerminal(terminal);
        }
        catch
        {
            // Never let capture cleanup tear down the client's final framebuffer pass.
        }
    }

    private void OnRenderFailed(Exception exception)
    {
        if (!active || terminalDelivered)
        {
            return;
        }

        pendingTerminal = Failure(
            TimelineSeconds,
            "OffscreenRenderFailed",
            null,
            exception.GetBaseException().Message);
    }

    private void UnsubscribeRenderFailure()
    {
        if (!renderFailureSubscribed)
        {
            return;
        }

        renderer.RenderFailed -= OnRenderFailed;
        renderFailureSubscribed = false;
    }

    private static DirectorWorkflowMultiViewTerminal Failure(
        double timelineSeconds,
        string code,
        DirectorWorkflowViewPlan? view,
        string detail)
        => new(
            false,
            timelineSeconds,
            new[]
            {
                new DirectorWorkflowMultiViewManifestError
                {
                    Code = code,
                    PassId = view?.PassId ?? Guid.Empty,
                    PassName = view?.PassName ?? string.Empty,
                    CardId = view?.CardId ?? Guid.Empty,
                    SourceType = view?.SourceType ?? DirectorRecordingSourceType.Program,
                    SourceId = view?.SourceId ?? Guid.Empty,
                    Detail = detail
                }
            });

    private static DirectorWorkflowMultiViewManifestError ToManifestError(
        DirectorWorkflowMultiViewError error)
        => new()
        {
            Code = error.Code.ToString(),
            PassId = error.PassId,
            PassName = error.PassName,
            CardId = error.CardId,
            SourceType = error.SourceType,
            SourceId = error.SourceId,
            Detail = error.Detail
        };
}
