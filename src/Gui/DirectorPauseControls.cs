using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;
using VSDirector.Networking;

namespace VSDirector.Gui;

/// <summary>Vanilla controls in the separate pause companion window.</summary>
internal sealed class DirectorPauseControls(GuiComposer composer, VSDirectorModSystem owner, ElementBounds bounds)
    : GuiElement(composer.Api, bounds)
{
    internal const string ReplayNameKey = "pause-replay-name";
    internal const string OffscreenSourceKey = "pause-source";
    internal const string CameraNameKey = "pause-camera-name";
    internal const string FreeCameraButtonKey = "pause-free-camera";
    private const double InputColumnWidth = 190;
    private const double ButtonGap = 6;
    private const double WindowHorizontalPadding = 32;
    private static readonly string[] ActionLabels =
        ["escape-replay-record", "escape-stop-recording", "escape-offscreen-render", "escape-stop-offscreen-recording", "escape-create-camera"];
    private bool disposed;
    private float refreshDelay;
    private string[] sourceValues = [""];
    private string[] sourceNames = [L("escape-offscreen-source-placeholder")];

    internal string ReplayName => (composer.GetElement(ReplayNameKey) as GuiElementTextInput)?.GetText() ?? "";
    internal string CameraName => (composer.GetElement(CameraNameKey) as GuiElementTextInput)?.GetText() ?? "";
    internal string SelectedSource => (composer.GetElement(OffscreenSourceKey) as GuiElementDropDown)?.SelectedValue ?? "";
    internal static int RowCount(VSDirectorModSystem owner) => 1
        + (owner.HasPermission(DirectorPermission.ReplayCapture) ? 1 : 0)
        + (owner.IsReplayPlaying ? 1 : 0)
        + (owner.HasPermission(DirectorPermission.CameraLibrary) ? 1 : 0)
        + (owner.HasPermission(DirectorPermission.FreeCamera) ? 1 : 0);

    internal static double WindowWidth => WindowHorizontalPadding + InputColumnWidth + ButtonGap + ActionColumnWidth;

    private static double ActionColumnWidth
    {
        get
        {
            using var font = CairoFont.ButtonText().WithFontSize(18f);
            return ActionLabels.Max(label => font.GetTextExtents(L(label)).Width / RuntimeEnv.GUIScale) + 16;
        }
    }

    internal void AddControls(double width, double height, double pitch, params Func<GuiComposer, bool>[] actions)
    {
        double actionWidth = ActionColumnWidth;
        double fieldWidth = width - actionWidth - ButtonGap;
        double y = 0;

        // Explicit vanilla helpers avoid the workspace's custom Director extension methods.
        void Input(string key, double y)
        {
            GuiComposerHelpers.AddTextInput(composer, ElementBounds.Fixed(0, y, fieldWidth, height), null, key: key);
        }
        void Button(string key, string label, double x, double y, double buttonWidth, Func<GuiComposer, bool> action)
        {
            GuiComposerHelpers.AddButton(composer, L(label), () =>
            {
                bool handled = action(composer);
                if (!disposed) Refresh();
                return handled;
            }, ElementBounds.Fixed(x, y, buttonWidth - 4, height - 2).WithFixedPadding(2, 1),
                CairoFont.ButtonText().WithFontSize(18f), EnumButtonStyle.Normal, key);
        }

        if (owner.HasPermission(DirectorPermission.ReplayCapture))
        {
            Input(ReplayNameKey, y);
            Button("pause-record", owner.HasEscapeMenuPrimaryRecording ? "escape-stop-recording" : "escape-replay-record",
                fieldWidth + ButtonGap, y, actionWidth, actions[0]);
            y += pitch;
        }
        if (owner.HasPermission(DirectorPermission.OffscreenSavedSources))
            GuiComposerHelpers.AddDropDown(composer, sourceValues, sourceNames, 0, null,
                ElementBounds.Fixed(0, y + (height - 30) / 2, fieldWidth, 30), OffscreenSourceKey);
        Button("pause-offscreen", owner.CanStopLiveOffscreen ? "escape-stop-offscreen-recording" : "escape-offscreen-render",
            fieldWidth + ButtonGap, y, actionWidth, actions[3]);
        y += pitch;
        if (owner.HasPermission(DirectorPermission.CameraLibrary))
        {
            Input(CameraNameKey, y);
            Button("pause-create-camera", "escape-create-camera", fieldWidth + ButtonGap, y, actionWidth, actions[4]);
            y += pitch;
        }
        if (owner.IsReplayPlaying)
        {
            double half = (width - ButtonGap) / 2;
            Button("pause-playback", owner.IsReplayPaused ? "escape-resume-replay" : "escape-pause-replay", 0, y, half, actions[1]);
            Button("pause-exit", "escape-exit-replay", half + ButtonGap, y, half, actions[2]);
            y += pitch;
        }
        if (owner.HasPermission(DirectorPermission.FreeCamera))
            Button(FreeCameraButtonKey, "escape-free-camera", 0, y, width, actions[5]);
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        // Added after the controls, so their native textures and bounds already exist.
        (composer.GetElement(ReplayNameKey) as GuiElementTextInput)?.SetPlaceHolderText(L("replay-name-placeholder"));
        (composer.GetElement(CameraNameKey) as GuiElementTextInput)?.SetPlaceHolderText(L("escape-camera-name-placeholder"));
        Refresh();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!disposed && (refreshDelay -= deltaTime) <= 0)
        {
            Refresh();
            refreshDelay = .2f;
        }
    }

    private static string L(string key) => Lang.Get("vsdirector:" + key);

    private void Refresh()
    {
        bool recording = owner.HasEscapeMenuPrimaryRecording, offscreen = owner.CanStopLiveOffscreen;
        bool capture = owner.HasPermission(DirectorPermission.ReplayCapture);
        Enabled(ReplayNameKey, capture && !recording && owner.CanStartEscapeMenuReplayRecording);
        Button("pause-record", recording ? "escape-stop-recording" : "escape-replay-record",
            capture && (recording || owner.CanStartEscapeMenuReplayRecording));
        Button("pause-playback", owner.IsReplayPaused ? "escape-resume-replay" : "escape-pause-replay", owner.IsReplayPlaying);
        Enabled("pause-exit", owner.IsReplayPlaying);
        Button("pause-offscreen", offscreen ? "escape-stop-offscreen-recording" : "escape-offscreen-render",
            offscreen || owner.CanStartEscapeMenuLiveOffscreen);
        bool cameraEnabled = owner.IsWorldReady && owner.HasPermission(DirectorPermission.CameraLibrary);
        Enabled(CameraNameKey, cameraEnabled);
        Enabled("pause-create-camera", cameraEnabled);
        Enabled(FreeCameraButtonKey, owner.IsWorldReady && owner.HasPermission(DirectorPermission.FreeCamera));
        RefreshSources();
        Enabled(OffscreenSourceKey, !offscreen && owner.HasPermission(DirectorPermission.OffscreenSavedSources)
            && owner.CanStartEscapeMenuLiveOffscreen && sourceValues.Length > 1);
    }

    private void RefreshSources()
    {
        if (composer.GetElement(OffscreenSourceKey) is not GuiElementDropDown select) return;
        string previous = SelectedSource;
        var values = new List<(string Value, string Label)> { ("", L("escape-offscreen-source-placeholder")) };
        if (owner.HasPermission(DirectorPermission.OffscreenSavedSources))
        {
            values.AddRange(owner.Cameras.Select(c => (new DirectorLiveOffscreenSource(DirectorRecordingSourceType.StaticCamera, c.Id).Serialize(), L("record-source-static") + ": " + c.Name)));
            values.AddRange(owner.Paths.Select(p => (new DirectorLiveOffscreenSource(DirectorRecordingSourceType.Path, p.Id).Serialize(), L("record-source-path") + ": " + p.Name)));
        }
        string[] nextValues = values.Select(v => v.Value).ToArray(), nextNames = values.Select(v => v.Label).ToArray();
        if (sourceValues.SequenceEqual(nextValues) && sourceNames.SequenceEqual(nextNames)) return;
        sourceValues = nextValues;
        sourceNames = nextNames;
        select.OnFocusLost();
        select.SetList(sourceValues, sourceNames);
        select.SetSelectedIndex(Math.Max(0, Array.IndexOf(sourceValues, previous)));
    }

    private void Enabled(string key, bool enabled)
    {
        if (composer.GetElement(key) is not GuiElementControl element || element.Enabled == enabled) return;
        if (!enabled) element.OnFocusLost();
        element.Enabled = enabled;
    }

    private void Button(string key, string label, bool enabled)
    {
        if (composer.GetElement(key) is not GuiElementTextButton button) return;
        string text = L(label);
        if (button.Text != text)
        {
            button.Text = text;
            button.ComposeElements(null!, null!);
        }
        Enabled(key, enabled);
    }

    internal void SelectSource(string value)
    {
        RefreshSources();
        if (composer.GetElement(OffscreenSourceKey) is GuiElementDropDown select)
            select.SetSelectedIndex(Math.Max(0, Array.IndexOf(sourceValues, value)));
    }

    internal void ClearCameraName() => (composer.GetElement(CameraNameKey) as GuiElementTextInput)?.SetValue("");

    internal void CloseInputs()
    {
        (composer.GetElement(ReplayNameKey) as GuiElementTextInput)?.OnFocusLost();
        (composer.GetElement(CameraNameKey) as GuiElementTextInput)?.OnFocusLost();
        (composer.GetElement(OffscreenSourceKey) as GuiElementDropDown)?.OnFocusLost();
        refreshDelay = 0;
    }

    public override void Dispose()
    {
        disposed = true;
        // The composer owns and disposes each vanilla control.
        base.Dispose();
    }
}
