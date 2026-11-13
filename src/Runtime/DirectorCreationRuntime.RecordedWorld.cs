using static VSDirector.Gui.DirectorCreationText;
using VSDirector.Data;
using VSDirector.Replay;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    private DirectorReplayManager? creationReplay;
    private Guid creationReplayProject;
    private string creationWorldPath = "";
    private Guid creationWorldClip;

    private DirectorReplayManager EnsureCreationReplay()
    {
        var project = Session.Project ?? throw new InvalidOperationException(L("creation-recording-unavailable"));
        if (creationReplay is not null && creationReplayProject != project.Id)
            StopCreationWorld();
        if (creationReplay is null)
        {
            var ordinary = DirectorReplayRuntime.OrdinaryManager
                ?? throw new InvalidOperationException(L("creation-recording-unavailable"));
            if (ordinary.IsRecording || ordinary.IsLiveActionRecording || ordinary.HasPendingSave || ordinary.IsExporting)
                throw new InvalidOperationException(L("creation-recording-unavailable"));
            RestoreVisualEnvironment();
            ordinary.StopPlayback();
            creationReplay = ordinary.CreateCreationSession(Session.Store.DirectoryFor(project.Id));
            creationReplayProject = project.Id;
        }
        DirectorReplayRuntime.ActivateCreationSession(creationReplay);
        return creationReplay;
    }

    private void StopCreationWorld()
    {
        var session = creationReplay;
        if (session is null) return;
        // Restore reality while capture/render hooks still consult this owner.
        // Disposal releases ownership after its world restoration completes.
        try { session.Dispose(); }
        finally
        {
            DirectorReplayRuntime.ReleaseCreationSession(session);
            creationReplay = null;
            creationReplayProject = Guid.Empty;
            creationWorldPath = "";
            creationWorldClip = Guid.Empty;
        }
    }

    private CreationRecordedWorld? RecordedWorldAt(double time)
        => Session.Project is { } project ? DirectorCreationWorldTimeline.Resolve(project, time) : null;

    private bool CreationWorldReady => RecordedWorldAt(Session.Time) is not { } world
        || creationReplay is { IsPlaying: true, IsLoading: false, IsSeekPending: false }
            && creationReplayProject == Session.Project!.Id
            && creationWorldPath == Session.Store.AssetPath(Session.Project.Id, world.Performance.WorldSource)
            && 0.0001 > Math.Abs(creationReplay.PlaybackTimeSeconds
                - Math.Clamp(world.SourceTime, 0, creationReplay.PlaybackDurationSeconds));

    private void UpdateCreationWorld(bool advancing = false)
    {
        if (PerformanceActive) return;
        var target = Enabled ? RecordedWorldAt(Session.Time) : null;
        if (target is null)
        {
            StopCreationWorld();
            return;
        }
        var project = Session.Project!;
        string path = Session.Store.AssetPath(project.Id, target.Performance.WorldSource);
        var session = EnsureCreationReplay();
        if (creationWorldPath != path)
        {
            RestoreVisualEnvironment();
            if (!session.StartCreationPlayback(path, target.Clip.Name,
                    target.Performance.Action.SourceActorKey, out var error))
                throw new InvalidOperationException(Message(error));
            creationWorldPath = path;
            creationWorldClip = Guid.Empty;
        }
        if (session.PlaybackLoadError.Length > 0)
            throw new InvalidOperationException(Message(session.PlaybackLoadError));
        if (session.IsLoading || !session.IsPlaying) return;

        bool continuous = advancing && creationWorldClip == target.Clip.Id
            && DirectorCreationEvaluator.ClipActive(target.Clip, Session.Time, project.Duration);
        bool sourceAdvancing = continuous && (target.Clip.Loop
            || target.Performance.BackgroundStart + target.Performance.Action.DurationSeconds > target.SourceTime);
        session.DriveCreationWorld(target.SourceTime, sourceAdvancing, target.Clip.Speed);
        creationWorldClip = target.Clip.Id;
        owner.TryEnableCreationFreeCamera(out _);
    }
}
