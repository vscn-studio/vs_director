using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VSDirector.Gui;

/// <summary>One tool window above the workspace. Claiming a replacement closes the previous tool.</summary>
internal static class DirectorToolWindows
{
    private static object? current;
    private static Action? close;
    internal static bool HasOpenTool => current is not null;
    internal static Action? CloseWorkspaceMenu { get; set; }
    public static void Claim(object window, Action closeWindow)
    {
        CloseWorkspaceMenu?.Invoke();
        if (ReferenceEquals(current, window)) return;
        var previous = close;
        current = null; close = null;
        previous?.Invoke();
        current = window; close = closeWindow;
    }
    public static void Release(object window) { if (ReferenceEquals(current, window)) { current = null; close = null; } }
    public static void Close() { var previous = close; current = null; close = null; previous?.Invoke(); }
}

/// <summary>Vintage Story broadcasts Escape to every open dialog; consume it at the top layer only.</summary>
[HarmonyPatch(typeof(GuiManager), nameof(GuiManager.OnKeyDown), new[] { typeof(KeyEvent) })]
internal static class DirectorEscapeStackPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(object __instance, KeyEvent args)
    {
        if (args.Handled) return true;
        var game = AccessTools.Field(__instance.GetType(), "game")?.GetValue(__instance);
        var opened = game is null ? null : AccessTools.Field(game.GetType(), "OpenedGuis")?.GetValue(game) as IEnumerable<GuiDialog>;
        if (opened is null) return true;
        if (args.KeyCode != (int)GlKeys.Escape) return !RouteModeShortcut(opened, args);
        if (!CloseTopLayer(opened)) return true;
        args.Handled = true;
        return false;
    }

    internal static bool CloseTopLayer(IEnumerable<GuiDialog> opened)
    {
        if (DirectorStudioWindow.TryCloseActive()) return true;
        foreach (var dialog in opened.Where(d => d.IsOpened()).OrderByDescending(d => d.DrawOrder))
        {
            if (dialog is DirectorWorkspaceFloatingEditorDialog floating)
            {
                if (!floating.HasComposer) continue;
                DirectorToolWindows.Close();
                return true;
            }
            if (dialog is DirectorWorkspaceHud workspace) return workspace.OnEscapePressed();
            if (dialog.GetType().Assembly == typeof(DirectorToolWindows).Assembly)
                return dialog.OnEscapePressed();
            // Do not close a workspace underneath an unrelated game's dialog.
            if (dialog.DialogType == EnumDialogType.Dialog) return false;
        }
        return false;
    }

    // Route the workspace shortcut before a focused field consumes it.
    // Native ImGui Studio is not part of OpenedGuis, so check it first.
    internal static bool RouteModeShortcut(IEnumerable<GuiDialog> opened, KeyEvent args)
    {
        if (DirectorStudioWindow.RouteModeShortcut(args)) return true;
        foreach (var dialog in opened.Where(d => d.IsOpened()).OrderByDescending(d => d.DrawOrder))
        {
            if (DirectorStudioWindow.OwnsActiveHost(dialog)) return DirectorStudioWindow.RouteModeShortcut(args);
            if (dialog is DirectorWorkspaceHud workspace) return workspace.HandleModeShortcut(args);
            if (dialog is DirectorWorkspaceFloatingEditorDialog) continue;
            if (dialog.DialogType == EnumDialogType.Dialog) return false;
        }
        return false;
    }
}

[HarmonyPatch(typeof(GuiDialog), nameof(GuiDialog.TryOpen), new[] { typeof(bool) })]
internal static class DirectorToolWindowOpenPatch
{
    private static void Prefix(GuiDialog __instance)
    {
        if (__instance.GetType().Namespace != typeof(DirectorWorkspaceHud).Namespace
            || __instance is DirectorWorkspaceHud or DirectorWorkspaceFloatingEditorDialog
            || __instance.GetType().Name.Contains("Overlay", StringComparison.Ordinal)) return;
        DirectorToolWindows.Claim(__instance, () => __instance.TryClose());
    }
}

[HarmonyPatch(typeof(GuiDialog), nameof(GuiDialog.OnGuiClosed))]
internal static class DirectorToolWindowClosePatch
{
    private static void Postfix(GuiDialog __instance) => DirectorToolWindows.Release(__instance);
}
