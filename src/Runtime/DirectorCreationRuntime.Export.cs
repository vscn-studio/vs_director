using static VSDirector.Gui.DirectorCreationText;
using Vintagestory.API.Common;
using VSDirector.Data;
using VSDirector.Replay;
using VSDirector.Recording;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    private string? pendingExport;
    private long exportPrepareStarted, exportGeneration, exportWaitStarted;
    private Guid exportBackground;
    private Guid observedBackground;
    private long backgroundGeneration = -1;
    private void SyncBackgroundSelection()
    {
        if (PerformanceActive || Session.Project is not { } project) return;
        bool changedProject = backgroundGeneration != Session.DocumentGeneration;
        if (!changedProject && observedBackground == project.BackgroundReplayId) return;
        backgroundGeneration = Session.DocumentGeneration; observedBackground = project.BackgroundReplayId;
        if (observedBackground == Guid.Empty)
        {
            pendingBackground = Guid.Empty;
            if (owner.ActiveReplayId != Guid.Empty)
            {
                RestoreVisualEnvironment();
                DirectorReplayRuntime.Manager?.StopPlayback();
                owner.TryEnableCreationFreeCamera(out _);
            }
        }
        else if (owner.ActiveReplayId != observedBackground && pendingBackground != observedBackground) LoadLinkedBackground();
    }

    internal void LinkCurrentBackground()
    {
        if (owner.ActiveReplayId == Guid.Empty) return;
        Session.Edit(L("creation-link-background-replay"), p => p.SetBackground(owner.ActiveReplayId));
        Seek(Session.Time);
    }
    internal void UseCurrentWorld()
    {
        Session.Edit(L("creation-use-current-world"), p => p.SetBackground(Guid.Empty));
        pendingBackground = Guid.Empty;
        RestoreVisualEnvironment();
        DirectorReplayRuntime.Manager?.StopPlayback();
        owner.TryEnableCreationFreeCamera(out _);
        Status = L("creation-background-world");
    }
    internal void LoadLinkedBackground()
    {
        if (Session.Project is not { } project || project.BackgroundReplayId == Guid.Empty) return;
        RestoreVisualEnvironment();
        var replay = DirectorReplayRuntime.Manager;
        if (replay is null || !replay.StartPlayback(project.BackgroundReplayId.ToString(), out _, out var error, pauseAfterLoad: true))
            throw new InvalidOperationException(L("creation-background-load-failed"));
        pendingBackground = project.BackgroundReplayId; pendingBackgroundTime = BackgroundTime(Session.Time);
        Status = L("creation-background-loading");
    }
    internal string BackgroundStatus => Session.Project is not { } p || p.BackgroundReplayId == Guid.Empty
        ? L("creation-background-world")
        : L(p.BackgroundReplayId == owner.ActiveReplayId && owner.CreationBackgroundReady
            ? "creation-background-ready" : "creation-background-unloaded") + " " + p.BackgroundReplayId;

    internal IReadOnlyList<string> CheckExportResources()
    {
        var issues = new List<string>();
        if (Session.Project is not { } p) return issues;
        p.Validate();
        if (p.CameraId == Guid.Empty) issues.Add(L("creation-select-a-shooting-camera"));
        if (p.WorldId.Length > 0 && p.WorldId != api.World.SavegameIdentifier
            || owner.TryCaptureCreationView(out var view) && view.Dimension != p.Dimension)
            issues.Add(L("creation-export-world-mismatch"));
        Session.Store.LoadPerformances(p, retry: true);
        foreach (var dependency in DirectorCreationDependencies.Collect(p))
        {
            try
            {
                bool exists;
                if (dependency.GameTexture)
                {
                    var location = new AssetLocation(dependency.Source);
                    location.Path = "textures/" + location.Path.Replace("textures/", "");
                    if (!location.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) location.Path += ".png";
                    exists = api.Assets.TryGet(location) is not null;
                }
                else exists = File.Exists(Session.Store.AssetPath(p.Id, dependency.Source));
                if (!exists) issues.Add(L("creation-missing-asset") + dependency.Name + " — " + dependency.Source);
                else if (dependency.Performance != Guid.Empty && !p.Performances.ContainsKey(dependency.Performance))
                    issues.Add(dependency.Name + ": " + Session.Store.PerformanceError(p, dependency.Source));
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            { issues.Add(dependency.Name + ": " + Message(e.Message)); }
        }
        return issues.Distinct().ToArray();
    }
    internal void Export(string path)
    {
        if (PerformanceActive) throw new InvalidOperationException(L("creation-recording-unavailable"));
        if (Exporting || Session.Project is not { } p) return;
        var issues = CheckExportResources();
        if (issues.Count > 0) throw new InvalidOperationException(L("creation-export-check-failed") + "\n" + string.Join("\n", issues));
        _ = DirectorFfmpegLocator.ResolveRequired(owner.Config.FfmpegPath);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Enabled = true; exportFrame = 0;
        Session.Play(false);
        if (p.BackgroundReplayId == Guid.Empty)
        {
            // Live-world exports must never accidentally capture an unrelated replay.
            pendingBackground = Guid.Empty;
            RestoreVisualEnvironment();
            DirectorReplayRuntime.Manager?.StopPlayback();
            owner.TryEnableCreationFreeCamera(out _);
        }
        else if (owner.ActiveReplayId != p.BackgroundReplayId) LoadLinkedBackground();
        pendingExport = path; exportGeneration = Session.DocumentGeneration; exportBackground = p.BackgroundReplayId;
        exportPrepareStarted = Environment.TickCount64;
        Status = L("creation-export-preparing");
    }
    private void TickExportPreparation()
    {
        if (pendingExport is not { } path) return;
        if (Session.DocumentGeneration != exportGeneration || Session.Project?.BackgroundReplayId != exportBackground)
        { CancelExport(); return; }
        if (Environment.TickCount64 - exportPrepareStarted > 60000)
            throw new InvalidOperationException(L("creation-background-load-timeout"));
        bool ready = exportBackground == Guid.Empty ? owner.ActiveReplayId == Guid.Empty
            : owner.ActiveReplayId == exportBackground && owner.CreationBackgroundReady && pendingBackground == Guid.Empty;
        if (!ready) return;
        pendingExport = null; StartExport(path);
    }
}
