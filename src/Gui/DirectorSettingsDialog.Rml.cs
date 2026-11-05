using System.Globalization;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using Vintagestory.API.Config;
using VSDirector.Networking;

namespace VSDirector.Gui;

internal sealed partial class DirectorSettingsDialog
{
    private string categorySearch = string.Empty;
    private string outlineSearch = string.Empty;
    private bool permissionsExpanded;

    protected override VSImGui.API.CallbackGUIStatus Draw(float deltaSeconds)
    {
        if (Opened)
        {
            DirectorImGuiLayout.WindowSize(new Vector2(680, 640), new Vector2(340, 300));
        }
        return base.Draw(deltaSeconds);
    }

    protected override bool OnDraw()
    {
        NormalizePage();
        DrawHeader();
        ImGui.Separator();
        float footerHeight = DirectorImGuiLayout.ButtonRowsHeight(
            new[] { "config-reload", "settings-defaults", "cancel", "settings-apply", "settings-ok" }
                .Select(key => Lang.Get("vsdirector:" + key)));
        ImGui.BeginChild("settings-content", new Vector2(0, -footerHeight), false);
        DrawCategories();
        if (status.Length > 0) { ImGui.Spacing(); ImGui.TextWrapped(status); }
        ImGui.EndChild();
        ImGui.Separator();
        DrawFooter();
        return true;
    }

    private void DrawHeader()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##settings-search", Lang.Get("vsdirector:settings-search"), ref categorySearch, 128);
    }

    private void DrawCategories()
    {
        bool searching = !string.IsNullOrWhiteSpace(categorySearch);
        int visible = 0;
        foreach (SettingsPage target in Enum.GetValues<SettingsPage>())
        {
            if (!IsPageAvailable(target)) continue;
            string code = target == SettingsPage.Experimental ? "experimental-features" : target.ToString().ToLowerInvariant();
            string label = Lang.Get("vsdirector:settings-" + code);
            bool categoryMatch = MatchesSearch(label);
            if (target == SettingsPage.Permissions)
            {
                if (searching && !categoryMatch) { permissionsExpanded = false; continue; }
                visible++;
                ImGui.PushID((int)target);
                if (searching) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
                bool expanded = DirectorImGuiTheme.CollapsingHeader(label,
                    target == page ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
                if (expanded)
                {
                    if (!permissionsExpanded) owner.RequestPermissionMembers();
                    DrawPermissions();
                }
                permissionsExpanded = expanded;
                ImGui.PopID();
                continue;
            }
            var fields = SettingsFields.Where(f => f.Page == target && ShowSetting(target, f.Property)
                && (categoryMatch || MatchesSearch(Lang.Get("vsdirector:" + f.Label)) || MatchesSearch(f.Property)
                    || MatchesSearch(Lang.Get("vsdirector:settings-section-" + SettingGroup(f.Property))))).ToArray();
            if (searching && fields.Length == 0) continue;
            visible++;
            ImGui.PushID((int)target);
            if (searching) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            if (DirectorImGuiTheme.CollapsingHeader(label, target == page ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            {
                foreach (var group in fields.GroupBy(f => SettingGroup(f.Property)))
                {
                    ImGui.SeparatorText(Lang.Get("vsdirector:settings-section-" + group.Key));
                    foreach (SettingField field in group) DrawSetting(field.Property, Lang.Get("vsdirector:" + field.Label));
                }
                if (target is SettingsPage.Offscreen or SettingsPage.Recording)
                {
                    ImGui.BeginDisabled(ffmpegTestRunning);
                    if (ImGui.Button(Lang.Get(ffmpegTestRunning ? "vsdirector:settings-ffmpeg-testing" : "vsdirector:settings-test-ffmpeg-short"))) TestFfmpeg();
                    ImGui.EndDisabled();
                    if (ImGui.Button(Lang.Get("vsdirector:settings-download-ffmpeg"))) OpenFfmpegDownload();
                }
                if (target == SettingsPage.Accessibility && draft.EnableModelOutlines && !searching) DrawOutlineEntities();
                ImGui.Spacing();
            }
            ImGui.PopID();
        }
        if (visible == 0) ImGui.TextDisabled(Lang.Get("vsdirector:scene-empty"));
    }

    private bool MatchesSearch(string text) => string.IsNullOrWhiteSpace(categorySearch)
        || text.Contains(categorySearch.Trim(), StringComparison.OrdinalIgnoreCase);

    private void DrawSetting(string name, string label)
    {
        PropertyInfo property = typeof(DirectorConfig).GetProperty(name)
            ?? throw new InvalidOperationException("Unknown Director setting: " + name);
        object? value = property.GetValue(draft);
        Type type = property.PropertyType;
        void Set(object next) => property.SetValue(draft, next);

        ImGui.PushID(name);
        if (type != typeof(bool))
        {
            ImGui.TextWrapped(label);
            label = "##value";
            ImGui.SetNextItemWidth(-1);
        }
        if (name is "ReplayOutputDirectory" or "RecordingOutputDirectory" or "OffscreenOutputDirectory")
        {
            string text = (string?)value ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText(label, ref text, 2048)) Set(text);
            if (name == "ReplayOutputDirectory") ImGui.TextWrapped(Lang.Get("vsdirector:replay-directory-help", owner.ReplayDirectory));
        }
        else if (name == "ModelOutlineColor")
        {
            Vector4 color = ParseColor((string?)value);
            if (ImGui.ColorEdit4(label, ref color, ImGuiColorEditFlags.InputRGB | ImGuiColorEditFlags.AlphaPreviewHalf)) Set(FormatColor(color));
        }
        else if (name == "ModelOutlineThickness")
        {
            float thickness = Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (ImGui.SliderFloat(label, ref thickness, .5f, 16f, "%.1f")) Set(Convert.ChangeType(thickness, type, CultureInfo.InvariantCulture)!);
        }
        else if (type == typeof(bool))
        {
            bool enabled = (bool)value!;
            if (ImGui.Checkbox(label, ref enabled)) Set(enabled);
        }
        else if (type.IsEnum)
        {
            string current = value?.ToString() ?? string.Empty;
            if (ImGui.BeginCombo(label, SettingOption(name, current)))
            {
                foreach (string option in Enum.GetNames(type))
                {
                    bool selected = option == current;
                    if (ImGui.Selectable(SettingOption(name, option), selected)) Set(Enum.Parse(type, option));
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }
        else if (type == typeof(string))
        {
            string text = (string?)value ?? string.Empty;
            if (name == "FfmpegVideoPreset")
            {
                string[] presets = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
                if (ImGui.BeginCombo(label, text))
                {
                    foreach (string option in presets) if (ImGui.Selectable(option, option == text)) Set(option);
                    ImGui.EndCombo();
                }
            }
            else
            {
                ImGuiInputTextFlags flags = name == "ObsWebSocketPassword" ? ImGuiInputTextFlags.Password : ImGuiInputTextFlags.None;
                if (ImGui.InputText(label, ref text, 2048, flags)) Set(text);
            }
        }
        else
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            bool integer = type == typeof(int) || type == typeof(long) || type == typeof(short);
            bool changed;
            if (integer)
            {
                int intValue = (int)Math.Clamp(number, int.MinValue, int.MaxValue);
                changed = ImGui.InputInt(label, ref intValue);
                number = intValue;
            }
            else changed = ImGui.InputDouble(label, ref number, 0.1, 1, "%.3f");
            if (changed)
            {
                try { Set(Convert.ChangeType(number, type, CultureInfo.InvariantCulture)!); }
                catch (OverflowException) { status = Lang.Get("vsdirector:invalid-number"); }
            }
        }
        ImGui.PopID();
    }

    private void DrawOutlineEntities()
    {
        if (outlineEntityValues.Length == 0) BuildOutlineEntityOptions();
        ImGui.Separator();
        ImGui.Text(Lang.Get("vsdirector:settings-outline-entities"));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##outline-search", Lang.Get("vsdirector:settings-entity-search"), ref outlineSearch, 128);
        for (int i = 0; i < outlineEntityValues.Length; i++)
        {
            string code = outlineEntityValues[i];
            bool enabled = draft.ModelOutlineEntityCodes.Contains(code, StringComparer.OrdinalIgnoreCase);
            if (!enabled && outlineSearch.Length > 0 && !code.Contains(outlineSearch, StringComparison.OrdinalIgnoreCase)
                && !outlineEntityNames[i].Contains(outlineSearch, StringComparison.OrdinalIgnoreCase)) continue;
            if (ImGui.Checkbox(outlineEntityNames[i] + "##outline-" + i, ref enabled))
            {
                draft.ModelOutlineEntityCodes.RemoveAll(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
                if (enabled) draft.ModelOutlineEntityCodes.Add(code);
            }
        }
    }

    private void DrawPermissions()
    {
        if (permissionMember is { } member)
        {
            if (ImGui.Button(Lang.Get("vsdirector:back"))) { permissionMember = null; return; }
            ImGui.SameLine();
            ImGui.Text(member.PlayerName);
            DirectorPermission inherited = DirectorPermissions.Sanitize(member.InheritedMask) | DirectorPermissions.DefaultPlayerPermissions;
            foreach (DirectorPermissionDefinition definition in DirectorPermissions.Definitions)
            {
                bool inheritedFlag = (inherited & definition.Permission) != 0;
                bool enabled = (permissionDraftMask & definition.Permission) != 0 || inheritedFlag;
                ImGui.BeginDisabled(inheritedFlag);
                if (ImGui.Checkbox(Lang.Get(definition.NameLanguageKey) + (inheritedFlag ? " · " + Lang.Get("vsdirector:permissions-inherited") : ""), ref enabled)
                    && !inheritedFlag)
                {
                    if (enabled) permissionDraftMask |= definition.Permission;
                    else permissionDraftMask &= ~definition.Permission;
                }
                ImGui.EndDisabled();
                ImGui.TextWrapped(Lang.Get(definition.DescriptionLanguageKey));
            }
            if (ImGui.Button(Lang.Get("vsdirector:settings-apply"))) SavePermissions();
            return;
        }

        ImGui.TextWrapped(Lang.Get("vsdirector:permissions-members-note"));
        foreach (DirectorPermissionMemberDto item in owner.PermissionMembers)
        {
            ImGui.Separator();
            ImGui.Text(item.PlayerName + (item.Online ? "  ●" : ""));
            ImGui.SameLine();
            if (item.IsAdmin) ImGui.TextDisabled(Lang.Get("vsdirector:permissions-admin"));
            else if (ImGui.SmallButton(Lang.Get("vsdirector:permissions-settings") + "##" + item.PlayerUid))
            {
                permissionMember = item;
                permissionDraftMask = DirectorPermissions.Sanitize(item.AssignedMask);
            }
            ImGui.TextWrapped(string.Join(" · ", DirectorPermissions.Definitions.Where(d => (DirectorPermissions.Sanitize(item.PermissionMask) & d.Permission) != 0).Select(d => Lang.Get(d.NameLanguageKey))));
        }
        if (owner.PermissionMembers.Count == 0) ImGui.TextWrapped(Lang.Get("vsdirector:permissions-members-empty"));
        if (ImGui.Button(Lang.Get("vsdirector:permissions-refresh"))) { owner.RequestPermissionMembers(); status = Lang.Get("vsdirector:permissions-refreshing"); }
    }

    private void DrawFooter()
    {
        if (ImGui.Button(Lang.Get("vsdirector:config-reload"))) Reload();
        FooterSameLine("vsdirector:settings-defaults");
        if (ImGui.Button(Lang.Get("vsdirector:settings-defaults"))) ResetDefaults();
        FooterSameLine("vsdirector:cancel");
        if (ImGui.Button(Lang.Get("vsdirector:cancel"))) { draft = Clone(owner.Config); TryClose(); return; }
        FooterSameLine("vsdirector:settings-apply");
        if (ImGui.Button(Lang.Get("vsdirector:settings-apply"))) Save();
        FooterSameLine("vsdirector:settings-ok");
        if (ImGui.Button(Lang.Get("vsdirector:settings-ok")) && Save()) TryClose();
    }

    private static void FooterSameLine(string key)
    {
        float width = ImGui.CalcTextSize(Lang.Get(key)).X + ImGui.GetStyle().FramePadding.X * 2;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width
            < ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X) ImGui.SameLine();
    }

    private static Vector4 ParseColor(string? value)
    {
        string hex = (value ?? "#00FF00").Trim().TrimStart('#');
        if (hex.Length == 6) hex += "FF";
        if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
            return new Vector4(((packed >> 24) & 255) / 255f, ((packed >> 16) & 255) / 255f, ((packed >> 8) & 255) / 255f, (packed & 255) / 255f);
        return new Vector4(0, 1, 0, 1);
    }

    private static string FormatColor(Vector4 color)
        => $"#{(int)(Math.Clamp(color.X, 0, 1) * 255):X2}{(int)(Math.Clamp(color.Y, 0, 1) * 255):X2}{(int)(Math.Clamp(color.Z, 0, 1) * 255):X2}{(int)(Math.Clamp(color.W, 0, 1) * 255):X2}";

    private static string SettingGroup(string property) => property switch
    {
        "ObsWebSocketUrl" or "ObsWebSocketPassword" => "obs",
        "FfmpegPath" or "OffscreenEncoder" or "OffscreenVideoQuality" or "OffscreenVideoBitrateKbps" or "FfmpegVideoPreset" => "encoding",
        "OffscreenCaptureDirectorAudio" or "OffscreenMuteOtherObsAudio" or "OffscreenCaptureVoiceModAudio" => "audio",
        "ReplayApplyWorldSnapshot" or "ReplaySpawnGhostActors" or "ReplayWorldOperationsPerFrame" or "ReplayExportWarmupFrames" or "ReplayFreeCameraSpeed" or "ReplayFreeCameraMouseSensitivity" or "LockInputDuringReplay" => "playback",
        "ReplayCaptureWorld" or "ReplayIncludeNonCreatures" or "ReplayPauseWorld" or "ReplaySnapshotRadiusChunks" or "ReplayCaptureParticles" or "ReplayCaptureSounds" or "ReplayCaptureVoiceModAudio" or "ReplayEntityStateRate" or "ReplayMaxWorldEvents" => "world",
        "AutoRecordReplay" or "ReplayQuickMode" or "ReplaySampleRate" or "ReplayMaxActors" or "ReplayCompression" => "capture",
        "ReplayOutputDirectory" => "storage",
        "EnableModelOutlines" or "ModelOutlineColor" or "ModelOutlineThickness" or "ModelOutlineThroughObstacles" => "outlines",
        "EnableGreenScreenSupport" or "EnableGreenScreenSky" or "GreenScreenSkyHideClouds" => "greenscreen",
        _ => "general"
    };

    private bool ShowSetting(SettingsPage target, string property)
    {
        if (target == SettingsPage.Replay)
        {
            if (SettingGroup(property) == "storage") return true;
            if (SettingGroup(property) == "playback" || property == "ReplayQuickMode") return owner.HasPermission(DirectorPermission.ReplayPlayback);
            return owner.HasPermission(DirectorPermission.ReplayCapture);
        }
        bool ffmpeg = draft.LiveOffscreenBackend == DirectorLiveOffscreenBackend.Ffmpeg;
        return property switch
        {
            "OffscreenEncoder" or "OffscreenVideoQuality" or "OffscreenVideoBitrateKbps" or "FfmpegVideoPreset" => ffmpeg,
            "OffscreenCaptureDirectorAudio" or "OffscreenMuteOtherObsAudio" => !ffmpeg,
            "ModelOutlineColor" or "ModelOutlineThickness" or "ModelOutlineThroughObstacles" => draft.EnableModelOutlines,
            "EnableGreenScreenSky" => draft.EnableGreenScreenSupport,
            "GreenScreenSkyHideClouds" => draft.EnableGreenScreenSupport && draft.EnableGreenScreenSky,
            _ => true
        };
    }

    private static string SettingOption(string property, string value) => property switch
    {
        "LiveOffscreenBackend" => Lang.Get("vsdirector:offscreen-backend-" + value.ToLowerInvariant()),
        "ReplayCompression" => Lang.Get("vsdirector:replay-compression-" + value.ToLowerInvariant()),
        "OffscreenEncoder" when value.Equals("Auto", StringComparison.OrdinalIgnoreCase) => Lang.Get("vsdirector:offscreen-encoder-auto"),
        _ => value
    };

    private sealed record SettingField(SettingsPage Page, string Property, string Label);
    private static readonly SettingField[] SettingsFields =
    [
        new(SettingsPage.Replay, "ReplayOutputDirectory", "replay-output-directory"),
        new(SettingsPage.Recording, "RecordingFrameRate", "record-fps"), new(SettingsPage.Recording, "RecordingOutputDirectory", "record-output"), new(SettingsPage.Recording, "LockInputWhileRecording", "record-lock-input"), new(SettingsPage.Recording, "RecordReplayWithVideo", "record-take-replay"), new(SettingsPage.Recording, "ObsWebSocketUrl", "record-obs-url"), new(SettingsPage.Recording, "ObsWebSocketPassword", "record-obs-password"), new(SettingsPage.Recording, "FfmpegPath", "record-ffmpeg"),
        new(SettingsPage.Replay, "AutoRecordReplay", "settings-auto-replay"), new(SettingsPage.Replay, "ReplayQuickMode", "settings-quick-mode"), new(SettingsPage.Replay, "ReplaySampleRate", "settings-replay-rate"), new(SettingsPage.Replay, "ReplayMaxActors", "settings-max-actors"), new(SettingsPage.Replay, "ReplayCaptureWorld", "settings-capture-world"), new(SettingsPage.Replay, "ReplayIncludeNonCreatures", "settings-noncreatures"), new(SettingsPage.Replay, "ReplayPauseWorld", "settings-pause-world"), new(SettingsPage.Replay, "ReplaySnapshotRadiusChunks", "settings-snapshot-radius"), new(SettingsPage.Replay, "ReplayCompression", "settings-replay-compression"), new(SettingsPage.Replay, "ReplayCaptureParticles", "settings-particles"), new(SettingsPage.Replay, "ReplayCaptureSounds", "settings-sounds"), new(SettingsPage.Replay, "ReplayCaptureVoiceModAudio", "settings-voice-replay"), new(SettingsPage.Replay, "ReplayEntityStateRate", "settings-state-rate"), new(SettingsPage.Replay, "ReplayMaxWorldEvents", "settings-max-events"), new(SettingsPage.Replay, "ReplayApplyWorldSnapshot", "settings-apply-snapshot"), new(SettingsPage.Replay, "ReplaySpawnGhostActors", "settings-ghosts"), new(SettingsPage.Replay, "ReplayWorldOperationsPerFrame", "settings-world-ops"), new(SettingsPage.Replay, "ReplayExportWarmupFrames", "settings-warmup"), new(SettingsPage.Replay, "ReplayFreeCameraSpeed", "settings-free-speed"), new(SettingsPage.Replay, "ReplayFreeCameraMouseSensitivity", "settings-mouse-sensitivity"), new(SettingsPage.Replay, "LockInputDuringReplay", "settings-lock-replay"),
        new(SettingsPage.Interface, "HideHudOnCamera", "settings-hide-hud"), new(SettingsPage.Interface, "LockPlayerInput", "settings-lock-camera"), new(SettingsPage.Interface, "ShowSwitchNotifications", "settings-notifications"),
        new(SettingsPage.Offscreen, "LiveOffscreenBackend", "offscreen-recording-backend"), new(SettingsPage.Offscreen, "OffscreenOutputDirectory", "offscreen-output-directory"), new(SettingsPage.Offscreen, "OffscreenWidth", "rml-output-width"), new(SettingsPage.Offscreen, "OffscreenHeight", "rml-output-height"), new(SettingsPage.Offscreen, "OffscreenFrameRate", "record-fps"), new(SettingsPage.Offscreen, "ObsWebSocketUrl", "record-obs-url"), new(SettingsPage.Offscreen, "ObsWebSocketPassword", "record-obs-password"), new(SettingsPage.Offscreen, "FfmpegPath", "record-ffmpeg"), new(SettingsPage.Offscreen, "OffscreenEncoder", "offscreen-encoder"), new(SettingsPage.Offscreen, "OffscreenVideoQuality", "offscreen-quality"), new(SettingsPage.Offscreen, "OffscreenVideoBitrateKbps", "offscreen-bitrate"), new(SettingsPage.Offscreen, "FfmpegVideoPreset", "settings-preset"), new(SettingsPage.Offscreen, "OffscreenCaptureDirectorAudio", "offscreen-director-audio"), new(SettingsPage.Offscreen, "OffscreenMuteOtherObsAudio", "offscreen-mute-other-audio"), new(SettingsPage.Offscreen, "OffscreenCaptureVoiceModAudio", "settings-voice-offscreen"), new(SettingsPage.Offscreen, "OffscreenUseIndependentPlayerRenderer", "offscreen-independent-player-renderer"),
        new(SettingsPage.Accessibility, "EnableModelOutlines", "settings-model-outlines"), new(SettingsPage.Accessibility, "ModelOutlineColor", "settings-outline-color"), new(SettingsPage.Accessibility, "ModelOutlineThickness", "settings-outline-thickness"), new(SettingsPage.Accessibility, "ModelOutlineThroughObstacles", "settings-outline-through-obstacles"), new(SettingsPage.Accessibility, "EnableGreenScreenSupport", "settings-green-screen-support"), new(SettingsPage.Accessibility, "EnableGreenScreenSky", "settings-green-screen-sky"), new(SettingsPage.Accessibility, "GreenScreenSkyHideClouds", "settings-green-screen-hide-clouds"), new(SettingsPage.Experimental, "ReplayUseDedicatedRenderer", "settings-dedicated-replay-renderer")
    ];
}
