using Vintagestory.API.Client;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    internal bool HandleModeShortcut(KeyEvent args)
    {
        if (!IsOpened() || args.Handled) return false;
        var mapping = capi.Input.GetHotKeyByCode("vsdirector-workspace-mode")?.CurrentMapping
            ?? new KeyCombination { KeyCode = (int)GlKeys.F8 };
        if (!MatchesModeShortcut(args, mapping)) return false;
        args.Handled = ToggleInteractionMode();
        return args.Handled;
    }

    internal static bool MatchesModeShortcut(KeyEvent args, KeyCombination mapping)
        => args.KeyCode == mapping.KeyCode && args.CtrlPressed == mapping.Ctrl
            && args.AltPressed == mapping.Alt && args.ShiftPressed == mapping.Shift
            && (!mapping.SecondKeyCode.HasValue || mapping.SecondKeyCode == 0 || mapping.SecondKeyCode == args.KeyCode2);

    private void SynchronizeNavigationInput()
    {
        if (layout.NavigationMode)
        {
            CloseResourceMenu();
            DirectorToolWindows.Close();
            EndReplayViewportFreeCameraPointerInput();
            ResetWorkspacePointerInteraction();
            UnFocus();
            floatingEditorDialog.UnFocus();
        }
        rmlInspectorWindow?.SetInputEnabled(!layout.NavigationMode);
        rmlViewWindow?.SetInputEnabled(!layout.NavigationMode);
        rmlToolbarWindow?.SetInputEnabled(!layout.NavigationMode);
        rmlTimeline?.SetInputEnabled(!layout.NavigationMode);
    }
}
