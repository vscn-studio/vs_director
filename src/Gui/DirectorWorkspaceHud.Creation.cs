using Vintagestory.API.Common;
using VSDirector.Rendering;
using Vintagestory.API.Client;
using VSDirector.Runtime;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private void EnterCreationWorkspace()
    {
        if (creationWorkspace.Active || owner.IsReplayExporting || DirectorToolWindows.HasOpenTool) return;
        FinishCameraTransform(cancel: false);
        EndReplayViewportFreeCameraPointerInput(); ResetWorkspacePointerInteraction(); FlushPendingAutoSave();
        owner.EnterCreationMode();
        floatingEditorDialog.TryClose();
        creationWorkspace.Enter();
        owner.SetWorkspaceInputOverride(true);
    }
    private void LeaveCreationWorkspace()
    {
        EndReplayViewportFreeCameraPointerInput();
        creationWorkspace.Exit();
        owner.LeaveCreationMode();
        owner.SetWorkspaceInputOverride(!layout.NavigationMode);
        floatingEditorDialog.OpenForWorkspace();
        UpdateGameViewportRuntime();
    }
    private void UpdateCreationViewport()
    {
        var p = imGuiWorkspace.ViewportPosition; var s = imGuiWorkspace.ViewportSize;
        DirectorWorkspaceViewportRuntime.Configure(this, capi, (int)p.X, (int)p.Y, Math.Max(1, (int)s.X), Math.Max(1, (int)s.Y), mapGameInput: false);
    }
    private void CreationMouseDown(MouseEvent args)
    {
        if (owner.Creation.ControllingActor || owner.Creation.NativeAppearanceEditorOpen) return;
        if (args.Handled) return;
        var p = imGuiWorkspace.ViewportPosition; var s = imGuiWorkspace.ViewportSize;
        bool viewport = args.X >= p.X && args.Y >= p.Y && args.X < p.X + s.X && args.Y < p.Y + s.Y;
        if (args.Button == EnumMouseButton.Left && viewport && !ImGuiNET.ImGui.GetIO().WantCaptureMouse
            && !creationWorkspace.CapturePointer(args.X, args.Y) && owner.TryEnableCreationFreeCamera(out _))
        {
            replayViewportFreeCameraPointerActive = true; owner.SetReplayViewportFreeCameraPointerActive(true);
        }
        args.Handled = true;
    }
}
