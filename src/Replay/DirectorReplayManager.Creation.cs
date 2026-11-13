using System.Text.Json;
using VSDirector.Data;

namespace VSDirector.Replay;

internal sealed partial class DirectorReplayManager
{
    private string creationProjectDirectory = "";
    private string creationExcludedActorKey = "";
    private bool creationPlaybackAdvancing;
    internal bool IsCreationSession => !string.IsNullOrEmpty(creationProjectDirectory);
    private bool CreationOwnsWorld => !IsCreationSession && DirectorReplayRuntime.CreationManager is not null;

    internal bool FreezeCreationCapture(out string error)
    {
        error = "";
        if (!IsCreationSession)
        {
            error = "creation-recording-unavailable";
            return false;
        }
        return pendingSave is not null || TryFreezeRecording(out error);
    }

    internal void DriveCreationWorld(double timeSeconds, bool advancing, double speed)
    {
        if (!IsCreationSession || playback is not { } active) return;
        // Keep the internal replay clock paused: the project supplies its
        // position. Sound playback follows the project transport independently.
        creationPlaybackAdvancing = advancing;
        active.Speed = double.IsFinite(speed) ? Math.Clamp(speed, .01, 16) : 1;
        DriveCreationBackground(timeSeconds, advancing);
        // Pause/resume must also work when the playhead has not moved.
        UpdateReplaySoundPauseState(active);
    }

    // A separate instance owns capture clocks, pending saves, playback state,
    // world restoration and sounds. Only the underlying recording machinery is
    // shared with ordinary replay; its catalog and event subscribers are not.
    internal DirectorReplayManager CreateCreationSession(string projectDirectory)
    {
        if (IsCreationSession || IsRecording || IsLiveActionRecording || HasPendingSave
            || IsPlaying || IsLoading || IsExporting)
            throw new InvalidOperationException("creation-recording-unavailable");

        string root = Path.GetFullPath(projectDirectory);
        var settings = JsonSerializer.Deserialize(JsonSerializer.Serialize(config), typeof(DirectorConfig))
            as DirectorConfig ?? throw new InvalidOperationException("creation-recording-unavailable");
        settings.ReplayOutputDirectory = Path.Combine(root, "recordings");
        settings.ReplaySampleRate = DirectorCreationTiming.FramesPerSecond;
        settings.ReplayCaptureWorld = true;
        settings.ReplayApplyWorldSnapshot = true;
        settings.ReplayQuickMode = false;

        var session = new DirectorReplayManager(capi, settings, controller, inputLockHandler,
            (_, _) => (false, "creation-recording-unavailable"))
        {
            creationProjectDirectory = root
        };
        try
        {
            session.InitializeWorld(capi.World.SavegameIdentifier ?? "");
            if (session.store is null)
                throw new IOException("creation-recording-unavailable");
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // Resolve a take directly from its project-owned asset path. It need not
    // enter a replay catalog, and ordinary replay sidecar edits do not apply.
    internal bool StartCreationPlayback(string sourcePath, string name, string actorKey, out string error)
    {
        error = "";
        if (!IsCreationSession || recording is not null || pendingSave is not null || liveActionCapture is not null)
        {
            error = "creation-recording-unavailable";
            return false;
        }
        string path = Path.GetFullPath(sourcePath);
        string root = creationProjectDirectory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recording path must remain inside its creation project.");
        if (!File.Exists(path))
        {
            error = "creation-performance-unavailable";
            return false;
        }

        StopPlayback();
        creationPlaybackAdvancing = false;
        creationExcludedActorKey = actorKey ?? "";
        playbackLoadError = "";
        var info = new DirectorReplayInfo(path, Guid.Empty,
            string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name,
            DateTimeOffset.MinValue, 0, 0);
        var loading = new PendingReplayLoad(info, quickMode: false, pauseAfterLoad: true)
        {
            ProjectOwned = true
        };
        pendingLoad = loading;
        replayLoadOverlayVisible = false;
        loading.Task = LoadReplayWithServerCompanionAsync(path,
            new ReplayLoadProgress(loading), loading.Cancellation.Token);
        return true;
    }
}
