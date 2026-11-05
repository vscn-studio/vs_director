using System.Reflection;
using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;
using VSDirector.Gui;
using VSDirector.Networking;

namespace VSDirector.Runtime;

internal static class DirectorEscapeMenuRuntime
{
    internal static VSDirectorModSystem? Owner { get; private set; }
    internal static DirectorPauseControls? Controls { get; set; }
    internal static GuiDialog? Dialog { get; set; }

    internal static void Attach(VSDirectorModSystem owner) => Owner = owner;
    internal static void CloseInputs() => Controls?.CloseInputs();
    internal static void Detach(VSDirectorModSystem owner)
    {
        if (!ReferenceEquals(Owner, owner)) return;
        if (Dialog is { } dialog) DirectorEscapeMenuComposePatch.RemoveWindow(dialog);
        Controls?.CloseInputs();
        Controls = null;
        Owner = null;
    }
}

/// <summary>Composes a separate native window beside the unmodified pause controls.</summary>
[HarmonyPatch]
internal static class DirectorEscapeMenuComposePatch
{
    internal const double MenuControlHeight = 30d;
    internal const double MenuRowPitch = 36d;
    internal const string SideWindowKey = "vsdirector-pause-window";

    internal static double WindowHeight(VSDirectorModSystem owner) => 60 + MenuControlHeight
        + (DirectorPauseControls.RowCount(owner) - 1) * MenuRowPitch;

    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("Vintagestory.Client.NoObf.GuiDialogEscapeMenu"), "EscapeMenuHome");

    [HarmonyPostfix]
    private static void Postfix(GuiDialog __instance) => BuildWindow(__instance);

    internal static void BuildWindow(GuiDialog dialog)
    {
        if (DirectorEscapeMenuRuntime.Owner is not { } owner || dialog.SingleComposer is not { } menu
            || menu.DialogName != "escapemenu") return;
        RemoveWindow(dialog);
        var api = menu.Api;
        double height = WindowHeight(owner);
        double width = DirectorPauseControls.WindowWidth;
        double contentWidth = width - 32;
        var bounds = new DirectorPauseWindowBounds(api, menu.Bounds, width, height);
        var composer = api.Gui.CreateCompo(SideWindowKey, bounds)
            .AddShadedDialogBG(ElementBounds.Fill, withTitleBar: false)
            .AddStaticText(Lang.Get("vsdirector:escape-window-title"),
                DirectorFont.WhiteSmallishText().WithOrientation(EnumTextOrientation.Center),
                ElementBounds.Fixed(16, 10, contentWidth, 24), "title");
        var content = ElementBounds.Fixed(16, 44, contentWidth, height - 60);
        composer.BeginChildElements(content);
        var controls = new DirectorPauseControls(composer, owner, ElementBounds.Fixed(0, 0, 0, 0));
        DirectorEscapeMenuRuntime.Controls = controls;
        controls.AddControls(contentWidth, MenuControlHeight, MenuRowPitch, ToggleRecording, ToggleReplayPause,
            ExitReplay, ToggleOffscreenRendering, CreateCamera, StartFreeCamera);
        composer.AddInteractiveElement(controls, "controls");
        composer.EndChildElements().Compose(false);
        dialog.Composers[SideWindowKey] = composer;
        DirectorEscapeMenuRuntime.Dialog = dialog;
    }

    internal static void RemoveWindow(GuiDialog dialog)
    {
        DirectorEscapeMenuRuntime.CloseInputs();
        if (dialog.Composers[SideWindowKey] is { } window)
        {
            window.Dispose();
            dialog.Composers.Remove(SideWindowKey);
        }
        DirectorEscapeMenuRuntime.Controls = null;
        if (ReferenceEquals(DirectorEscapeMenuRuntime.Dialog, dialog)) DirectorEscapeMenuRuntime.Dialog = null;
        if (dialog.SingleComposer is { DialogName: "escapemenu" } menu)
        {
            menu.Bounds.fixedOffsetX = 0;
            menu.Bounds.CalcWorldBounds();
        }
    }

    private static bool ToggleRecording(GuiComposer composer)
    {
        string name = DirectorEscapeMenuRuntime.Controls?.ReplayName.Trim() ?? "";
        DirectorEscapeMenuRuntime.Owner?.TryToggleEscapeMenuReplayRecording(name, out _);
        return true;
    }
    private static bool ToggleReplayPause(GuiComposer composer)
    {
        DirectorEscapeMenuRuntime.Owner?.TryToggleReplayPause(out _);
        return true;
    }
    private static bool ExitReplay(GuiComposer composer)
    {
        DirectorEscapeMenuRuntime.Owner?.StopReplay();
        return true;
    }
    private static bool StartFreeCamera(GuiComposer composer)
    {
        if (DirectorEscapeMenuRuntime.Owner?.TryStartEscapeMenuFreeCamera(out _) == true) CloseEscapeMenu(composer);
        return true;
    }
    internal static void CloseEscapeMenu(GuiComposer composer)
    {
        DirectorEscapeMenuRuntime.CloseInputs();
        composer.Api.Gui.OpenedGuis.FirstOrDefault(dialog => dialog.Composers.Values.Any(c => ReferenceEquals(c, composer)))?.TryClose();
    }
    private static bool ToggleOffscreenRendering(GuiComposer composer)
    {
        if (DirectorEscapeMenuRuntime.Owner is not { } owner) return true;
        if (owner.CanStopLiveOffscreen) owner.TryStopLiveOffscreenRecording(out _);
        else
        {
            string source = DirectorEscapeMenuRuntime.Controls?.SelectedSource ?? "";
            if (owner.TryStartEscapeMenuLiveOffscreenRecording(source, out string resolved, out _))
                DirectorEscapeMenuRuntime.Controls?.SelectSource(resolved);
        }
        return true;
    }
    private static bool CreateCamera(GuiComposer composer)
    {
        if (DirectorEscapeMenuRuntime.Owner is not { } owner || DirectorEscapeMenuRuntime.Controls is not { } controls) return true;
        string name = controls.CameraName.Trim();
        if (owner.TryAddCamera(name, out _))
        {
            controls.ClearCameraName();
            var camera = owner.Cameras.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (camera is not null) controls.SelectSource(new DirectorLiveOffscreenSource(DirectorRecordingSourceType.StaticCamera, camera.Id).Serialize());
        }
        return true;
    }
}

[HarmonyPatch]
internal static class DirectorEscapeMenuVisibilityPatch
{
    private const string EscapeMenuTypeName = "Vintagestory.Client.NoObf.GuiDialogEscapeMenu";

    private static IEnumerable<MethodBase> TargetMethods()
    {
        Type escapeMenuType = AccessTools.TypeByName(EscapeMenuTypeName)
            ?? throw new TypeLoadException(EscapeMenuTypeName);
        yield return AccessTools.Method(escapeMenuType, nameof(GuiDialog.OnGuiOpened), Type.EmptyTypes)
            ?? throw new MissingMethodException(EscapeMenuTypeName, nameof(GuiDialog.OnGuiOpened));
        yield return AccessTools.Method(escapeMenuType, nameof(GuiDialog.OnGuiClosed), Type.EmptyTypes)
            ?? throw new MissingMethodException(EscapeMenuTypeName, nameof(GuiDialog.OnGuiClosed));
        yield return AccessTools.Method(escapeMenuType, "LoadComposer", new[] { typeof(GuiComposer) })
            ?? throw new MissingMethodException(EscapeMenuTypeName, "LoadComposer");
    }

    [HarmonyPrefix]
    private static void Prefix(MethodBase __originalMethod)
    {
        if (__originalMethod.Name == nameof(GuiDialog.OnGuiOpened))
        {
            DirectorEscapeMenuRuntime.Owner?.SetCameraGuiOverride(true);
        }
    }

    [HarmonyPostfix]
    private static void Postfix(GuiDialog __instance, MethodBase __originalMethod)
    {
        // The vanilla settings pages replace SingleComposer without closing the pause dialog.
        if (__originalMethod.Name == "LoadComposer") DirectorEscapeMenuComposePatch.RemoveWindow(__instance);
        if (__originalMethod.Name == nameof(GuiDialog.OnGuiClosed))
        {
            DirectorEscapeMenuComposePatch.RemoveWindow(__instance);
            DirectorEscapeMenuRuntime.Owner?.SetCameraGuiOverride(false);
        }
    }
}
