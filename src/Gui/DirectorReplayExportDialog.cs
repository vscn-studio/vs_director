using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;
using VSImGui.API;

namespace VSDirector.Gui;

internal sealed class DirectorReplayExportDialog : DirectorImGuiDialog
{
    private static readonly string[] EncoderNames = ["Auto", "NVIDIA NVENC", "Intel QSV", "AMD AMF", "libx264"];
    private static readonly string[] PresetCodes = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
    private readonly VSDirectorModSystem owner;
    private readonly DirectorWorkspaceHud workspace;
    private DirectorOffscreenEncoder encoder = DirectorOffscreenEncoder.Auto;
    private string preset = "medium";
    private string startText = "0", endText = "0", scaleText = "1", fpsText = "30";
    private string widthText = "1920", heightText = "1080", qualityText = "18", bitrateText = "0";
    private bool includeAudio = true;
    private string audioBitrateText = "192", settleText = "1", outputText = "", ffmpegText = "ffmpeg";
    private string validationText = "";

    public DirectorReplayExportDialog(ICoreClientAPI capi, VSDirectorModSystem owner, DirectorWorkspaceHud workspace)
        : base(capi, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, L("export"), "vsdirector-replay-export", false)
    {
        this.owner = owner;
        this.workspace = workspace;
    }

    internal bool OpenForCurrentScene()
    {
        if (!owner.IsReplayPlaying) return false;
        DirectorConfig config = owner.Config;
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        startText = N(timeline.ExportStartSeconds ?? 0);
        endText = N(timeline.ExportEndSeconds ?? owner.ReplayDurationSeconds);
        scaleText = "1";
        fpsText = N(config.RecordingFrameRate);
        widthText = N(config.OffscreenWidth); heightText = N(config.OffscreenHeight);
        qualityText = N(config.FfmpegVideoCrf); bitrateText = N(config.OffscreenVideoBitrateKbps);
        includeAudio = true;
        audioBitrateText = N(config.FfmpegAudioBitrateKbps); settleText = N(config.ReplayExportWarmupFrames);
        outputText = config.RecordingOutputDirectory; ffmpegText = config.FfmpegPath;
        encoder = config.OffscreenEncoder; preset = config.FfmpegVideoPreset;
        validationText = "";
        Open();
        return Opened;
    }

    internal bool TryClose() { Close(); return true; }
    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string L(string key) => Lang.Get("vsdirector:" + key);
    protected override void PrepareWindow()
        => DirectorImGuiLayout.WindowSize(new Vector2(560, 740), new Vector2(560, 340));

    protected override bool OnDraw()
    {
        if (owner.ReplayViewportTimeline.ActiveScene is { } scene) ImGui.TextWrapped(scene.Name);
        float footerHeight = ImGui.GetFrameHeightWithSpacing();
        ImGui.BeginChild("export-options", new Vector2(0, -footerHeight), true);
        Input(L("replay-export-start"), "##start", ref startText);
        Input(L("replay-export-end"), "##end", ref endText);
        Input(L("replay-export-scale-label"), "##scale", ref scaleText);
        Input(L("record-fps"), "##fps", ref fpsText);
        Input(L("replay-export-width"), "##width", ref widthText);
        Input(L("replay-export-height"), "##height", ref heightText);
        Combo(L("offscreen-encoder"), EncoderNames[(int)encoder], EncoderNames, value => encoder = (DirectorOffscreenEncoder)Array.IndexOf(EncoderNames, value));
        Input(L("offscreen-quality"), "##quality", ref qualityText);
        Input(L("offscreen-bitrate"), "##bitrate", ref bitrateText);
        Combo(L("settings-preset"), preset, PresetCodes, value => preset = value);
        Input(L("replay-export-output"), "##output", ref outputText);
        Input(L("replay-export-ffmpeg-path"), "##ffmpeg", ref ffmpegText);
        ImGui.Checkbox(L("replay-export-audio"), ref includeAudio);
        Input(L("replay-export-audio-bitrate"), "##audio-bitrate", ref audioBitrateText);
        Input(L("replay-export-settle"), "##settle", ref settleText);
        ImGui.Spacing();
        ImGui.TextWrapped(validationText.Length > 0 ? validationText : owner.ReplayExportStatusShortText());
        ImGui.EndChild();
        if (ImGui.Button(L("cancel"))) return false;
        ImGui.SameLine();
        ImGui.BeginDisabled(!owner.IsReplayExporting);
        if (ImGui.Button(L("replay-export-stop"))) StopExport();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!owner.IsReplayPlaying || owner.IsReplayExporting);
        if (ImGui.Button(L("export"))) StartExport();
        ImGui.EndDisabled();
        return true;
    }

    private static void Input(string label, string id, ref string value, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        if (!BeginField(label, id)) return;
        try
        {
            if (id is "##output" or "##ffmpeg") ImGui.InputText(id, ref value, 2048, flags);
            else
            {
                bool fractional = id is "##start" or "##end" or "##scale";
                double number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                if (DirectorImGuiNumeric.Double(id, ref number, fractional ? .05f : 1, format: fractional ? "%.3f" : "%.0f"))
                    value = N(fractional ? number : Math.Round(number));
            }
        }
        finally { ImGui.EndTable(); }
    }

    private static void Combo(string label, string current, IReadOnlyList<string> values, Action<string> selected)
    {
        if (!BeginField(label, label)) return;
        try
        {
            if (!ImGui.BeginCombo("##" + label, current)) return;
            foreach (string value in values)
            {
                bool isSelected = value == current;
                if (ImGui.Selectable(value, isSelected)) selected(value);
                if (isSelected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        finally { ImGui.EndTable(); }
    }

    private static bool BeginField(string label, string id)
    {
        if (!ImGui.BeginTable("field-" + id, 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings)) return false;
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed,
            Math.Min(280 * DirectorImGuiTheme.Scale, ImGui.GetContentRegionAvail().X * .42f));
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextWrapped(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(-1);
        return true;
    }

    private void StartExport()
    {
        double scale = 1d;
        if (!TryBuildOptions(out DirectorReplayExportOptions options)
            || !double.TryParse(startText, NumberStyles.Float, CultureInfo.InvariantCulture, out double start)
            || !double.TryParse(endText, NumberStyles.Float, CultureInfo.InvariantCulture, out double end)
            || !double.IsFinite(start) || !double.IsFinite(end)
            || !double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out scale)
            || !double.IsFinite(scale) || scale is < 0.05d or > 20d
            || !workspace.TrySetReplayExportRange(start, end))
        { validationText = L("replay-export-config-invalid"); return; }
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        start = timeline.ExportStartSeconds ?? start; end = timeline.ExportEndSeconds ?? end;
        DirectorReplayElementScene? scene = timeline.ActiveScene;
        if (scene is null)
        { validationText = L("replay-export-scene-missing"); return; }
        if (!owner.TryStartReplaySceneExport(scene.Id, scene.Name, start, end, scale, options, out string message))
        { validationText = message; return; }
        Close();
    }

    private bool TryBuildOptions(out DirectorReplayExportOptions options)
    {
        options = new DirectorReplayExportOptions();
        if (!int.TryParse(fpsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps) || fps is < 1 or > 240) return false;
        if (!int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)
                || !int.TryParse(heightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int height)
                || !int.TryParse(qualityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int quality)
                || !int.TryParse(bitrateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bitrate)
                || !int.TryParse(audioBitrateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int audioBitrate)
                || !int.TryParse(settleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int settle)
                || width is < 16 or > 8192 || height is < 16 or > 8192 || (width & 1) != 0 || (height & 1) != 0
                || quality is < 0 or > 51 || bitrate is < 0 or > 1000000 || audioBitrate is < 64 or > 512 || settle is < 0 or > 8
                || string.IsNullOrWhiteSpace(ffmpegText)) return false;
        options = new DirectorReplayExportOptions
        {
            Backend = DirectorReplayExportBackend.Ffmpeg, FramesPerSecond = fps, Width = width, Height = height, Encoder = encoder,
            VideoQuality = quality, VideoBitrateKbps = bitrate, IncludeAudio = includeAudio, AudioBitrateKbps = audioBitrate,
            SoftwarePreset = preset, OutputDirectory = outputText.Trim(), FfmpegPath = ffmpegText.Trim(),
            SettleFrames = settle
        };
        return true;
    }

    private void StopExport()
    {
        if (owner.IsReplayExporting) owner.TryStopReplayExport(out _);
        validationText = "";
    }
}
