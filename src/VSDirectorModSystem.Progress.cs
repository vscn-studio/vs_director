using System.Globalization;
using System.Numerics;
using Vintagestory.API.Config;
using VSDirector.Data;
using VSDirector.Gui;

namespace VSDirector;

public sealed partial class VSDirectorModSystem
{
    private DirectorReplayProgressOverlay? replayProgressOverlay;
    internal bool IsObsReplayExporting => replay?.IsObsExporting == true;

    internal DirectorOperationStatus ReplayOperationStatus(bool export)
    {
        if (IsReplayExporting || export && !IsReplayLoading)
        {
            string phase = replay?.ExportStatus ?? "idle";
            string exportDetail = Lang.Get("vsdirector:replay-progress-frames", replay?.ExportCurrentTrackNumber ?? 0,
                replay?.ExportTrackCount ?? 0, replay?.ExportCurrentTrackFrame ?? 0, replay?.ExportCurrentTrackFramesTotal ?? 0);
            if (IsReplayExporting && IsReplayLoading)
            {
                phase = "buffering";
                exportDetail = Lang.Get("vsdirector:workspace-replay-loading",
                    Lang.Get("vsdirector:workspace-replay-phase-" + LoadingPhase),
                    (ReplayLoadingProgress * 100).ToString("0.0", CultureInfo.InvariantCulture));
            }
            return new(IsReplayExporting, true, replay?.ExportProgress ?? 0, phase,
                Lang.Get("vsdirector:replay-progress-export"), exportDetail, replay?.ExportError ?? "");
        }
        string loadingPhase = IsReplayLoading ? LoadingPhase : PlaybackLoadError.Length > 0 ? "error" : IsReplayPlaying ? "ready" : "stopped";
        string detail = loadingPhase is "error" or "stopped"
            ? Lang.Get("vsdirector:replay-export-state-" + loadingPhase)
            : Lang.Get("vsdirector:workspace-replay-loading", Lang.Get("vsdirector:workspace-replay-phase-" + loadingPhase),
                (ReplayLoadingProgress * 100).ToString("0.0", CultureInfo.InvariantCulture));
        return new(IsReplayLoading, false, ReplayLoadingProgress, loadingPhase,
            Lang.Get("vsdirector:replay-progress-loading"), detail, PlaybackLoadError);
    }

    internal (Vector2 Position, Vector2 Size) ReplayProgressViewport(Vector2 display)
    {
        if (workspace?.IsOpened() == true && workspace.ImGuiWorkspace is { } host && host.ViewportSize.X > 0 && host.ViewportSize.Y > 0)
            return (host.ViewportPosition, host.ViewportSize);
        return (Vector2.Zero, display);
    }
}
