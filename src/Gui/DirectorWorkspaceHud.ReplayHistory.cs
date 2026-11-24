using ImGuiNET;
using VSDirector.Data;
using Vintagestory.API.Config;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private bool UndoReplayEdit() => RestoreReplayEdit(false);
    private bool RedoReplayEdit() => RestoreReplayEdit(true);

    private void DrawReplayHistoryShortcuts()
    {
        if (section != DirectorWorkspaceSection.Replay || layout.NavigationMode || ImGui.GetIO().WantTextInput) return;
        // VSImGui can supply individual modifier keys without updating the legacy KeyCtrl field.
        bool ctrl = ImGui.GetIO().KeyCtrl || ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl);
        if (!ctrl) return;
        bool shift = ImGui.GetIO().KeyShift || ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
        if (ImGui.IsKeyPressed(ImGuiKey.Z, false)) RestoreReplayEdit(shift);
        else if (ImGui.IsKeyPressed(ImGuiKey.Y, false)) RestoreReplayEdit(true);
    }

    private bool RestoreReplayEdit(bool forward)
    {
        imGuiInspectorWindow?.FlushCommit();
        imGuiViewWindow?.FlushCommit();
        CloseReplayFloatingEditor();
        if (!owner.RestoreReplayEdit(forward)) return true;
        ResetWorkspacePointerInteraction();
        replayViewportDraftTimeline = null;
        selectedReplayViewportClipId = selectedReplayElementKeyframeId = selectedReplayElementTrackId = Guid.Empty;
        replayViewportFieldsId = replayElementFieldsId = Guid.Empty;
        ComposeInspector();
        ComposeTimeline();
        return true;
    }
}
