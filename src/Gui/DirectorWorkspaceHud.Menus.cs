using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;
using VSDirector.Voice;


namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private DirectorImGuiWindow? imGuiToolbarWindow, imGuiResourceWindow;
    private DirectorSceneBrowser? sceneBrowser;
    private DirectorAboutDialog? aboutDialog;
    private bool voiceRecorderWindowOpen;
    private bool voiceRecorderPopupRequested;
    private string voiceAudioPath = string.Empty;
    private string voiceAudioDirectory = string.Empty;
    private string voiceFileBrowserPath = string.Empty;
    private string voiceFileCandidatePath = string.Empty;
    private string voiceFileImportError = string.Empty;
    private string voiceTakeName = "";
    private Guid voiceDestinationTrack;
    private (Guid Id, string Label)[] voiceTrackChoices = [];
    private bool voiceFileBrowserOpen;
    private bool voiceFileBrowserPopupRequested;

    internal void OpenAbout()
    {
        aboutDialog ??= new DirectorAboutDialog(capi);
        aboutDialog.Open();
    }
    private DirectorWorkspaceSection sceneCategory = DirectorWorkspaceSection.Cameras;
    private string sceneSearch = "";

    private void EmptyMenuComposer(string key)
    {
        if (Composers[key] is null)
            Composers[key] = capi.Gui.CreateCompo(key, ElementBounds.Fixed(-2, -2, 1, 1)).Compose(false);
    }

    private void ComposeImGuiToolbar()
    {
        EmptyMenuComposer(ToolbarKey);
        var commands = new List<DirectorWorkspaceMenus.Command>();
        commands.Add(new("creation", DirectorCreationText.L("creation-creation"), EnterCreationWorkspace));
        if (SceneSections().Any()) commands.Add(new("scene", Lang.Get("vsdirector:workspace-scene"), OpenSceneWindow));
        if (IsSectionAvailable(DirectorWorkspaceSection.Replay))
            commands.Add(new("tab-replay", SectionLabel(DirectorWorkspaceSection.Replay), () => OpenImGuiResourceMenu(DirectorWorkspaceSection.Replay), Selected: resourceMenuSection == DirectorWorkspaceSection.Replay));
        if (IsSectionAvailable(DirectorWorkspaceSection.Replay))
            commands.Add(new("export", Lang.Get("vsdirector:export"), () => OpenReplayExport(), owner.IsReplayPlaying));
        commands.Add(new("settings", Lang.Get("vsdirector:workspace-settings-button"), () => owner.ToggleSettings()));
        commands.Add(new("navigation", Lang.Get(layout.NavigationMode ? "vsdirector:workspace-edit-mode" : "vsdirector:workspace-navigate-mode"), () => ToggleNavigationMode()));
        commands.Add(new("hud", Lang.Get(owner.Config.HideHudOnCamera ? "vsdirector:workspace-show-hud" : "vsdirector:workspace-hide-hud"), () => ToggleCameraHud()));
        commands.Add(new("reset-layout", Lang.Get("vsdirector:workspace-reset-layout"), imGuiWorkspace.ResetLayout));
        commands.Add(new("about", Lang.Get("vsdirector:workspace-about"), OpenAbout));
        var form = DirectorWorkspaceMenus.Toolbar(commands);
        imGuiToolbarWindow ??= new DirectorImGuiWindow(capi, "workspace-menu", docked: true, escape: () => OnEscapePressed(), overlay: true, workspace: imGuiWorkspace);
        imGuiToolbarWindow.Render("", form);
        imGuiToolbarWindow.SetInputEnabled(!layout.NavigationMode);
        var panel = layout.Panel("toolbar");
        panel.Height = DirectorWorkspacePanelLayout.ToolbarHeight;
        imGuiToolbarWindow.SetDockBounds(panel.X, panel.Y, panel.Width, panel.Height);
        if (IsOpened()) imGuiToolbarWindow.Open();
    }

    private void DrawVoiceRecorderWindow()
    {
        if (!voiceRecorderWindowOpen || !IsOpened()) return;
        DirectorMicrophoneRecorder? recorder = owner.MicrophoneRecorder;
        if (recorder is null) return;

        float scale = DirectorImGuiTheme.Scale;
        Vector2 displaySize = ImGui.GetIO().DisplaySize;
        string popupId = Lang.Get("vsdirector:voice-recorder") + "###vsdirector-voice-recorder";
        if (voiceRecorderPopupRequested)
        {
            ImGui.OpenPopup(popupId);
            voiceRecorderPopupRequested = false;
        }
        if (string.IsNullOrWhiteSpace(voiceAudioDirectory))
        {
            voiceAudioDirectory = Path.Combine(capi.GetOrCreateDataPath("ModData"), "VSDirector", "Recordings");
            Directory.CreateDirectory(voiceAudioDirectory);
        }

        float width = Math.Max(1, Math.Min(400 * scale, displaySize.X - 20 * scale));
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(.5f));
        ImGui.SetNextWindowSize(new Vector2(width, 600 * scale), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(Math.Min(310 * scale, width), 300 * scale),
            new Vector2(Math.Max(width, displaySize.X - 20 * scale), Math.Max(1, displaySize.Y - 20 * scale)));
        bool open = true;
        bool visible = ImGui.BeginPopupModal(popupId, ref open,
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoDocking);
        if (!visible)
        {
            if (!open) voiceRecorderWindowOpen = false;
            return;
        }

        try
        {
            DrawVoiceRecorderControls(recorder, scale);
            DrawVoiceFileSelectionPanel();
            if (voiceFileBrowserPopupRequested)
            {
                ImGui.OpenPopup(Lang.Get("vsdirector:voice-file-browser") + "###voice-file-browser");
                voiceFileBrowserPopupRequested = false;
            }
            if (voiceFileBrowserOpen) DrawVoiceFileBrowser(scale);
            if (!open)
            {
                ImGui.CloseCurrentPopup();
                voiceRecorderWindowOpen = false;
            }
        }
        finally { ImGui.EndPopup(); }
    }

    private void DrawVoiceRecorderControls(DirectorMicrophoneRecorder recorder, float scale)
    {
        bool recording = recorder.IsRecording;
        float diameter = 40 * scale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - diameter) / 2));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, diameter * .5f);
        if (DirectorImGuiIcons.Button(DirectorImGuiIcons.Get(recording ? "player-stop" : "microphone") + "##voice-record", new(diameter)))
        {
            if (!recording) recorder.Start(voiceTakeName);
            else if (recorder.Stop())
            {
                voiceAudioPath = recorder.OutputPath;
                voiceFileImportError = string.Empty;
            }
        }
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered()) DirectorImGuiTooltip.Show(Lang.Get(recording ? "vsdirector:voice-stop" : "vsdirector:voice-record"));
        double peakDb = 20 * Math.Log10(Math.Max(recorder.IsRecording ? recorder.Peak : 0, .001));
        double rmsDb = 20 * Math.Log10(Math.Max(recorder.IsRecording ? recorder.Level : 0, .001));
        ImGui.ProgressBar((float)Math.Clamp((peakDb + 60) / 60, 0, 1), new(-1, ImGui.GetFrameHeight()), " ");
        var meterMin = ImGui.GetItemRectMin();
        var meterMax = ImGui.GetItemRectMax();
        var meter = ImGui.GetWindowDrawList();
        string reading = recorder.IsRecording ? $"{rmsDb:0.0} dBFS" : "−∞ dBFS";
        meter.AddText((meterMin + meterMax - ImGui.CalcTextSize(reading)) / 2, ImGui.GetColorU32(ImGuiCol.Text), reading);
        for (int db = -60; db <= 0; db += 10)
        {
            string label = db.ToString();
            float x = meterMin.X + (meterMax.X - meterMin.X - ImGui.CalcTextSize(label).X) * ((db + 60) / 60f);
            meter.AddText(new(x, meterMax.Y + 1), ImGui.GetColorU32(ImGuiCol.TextDisabled), label);
        }
        ImGui.Dummy(new(0, ImGui.GetTextLineHeight()));
        ImGui.Separator();
        ImGui.TextUnformatted(Lang.Get("vsdirector:voice-monitor"));
        float monitor = recorder.MonitorVolume * 100;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##voice-monitor", ref monitor, 0, 100, "%.0f%%")) recorder.MonitorVolume = monitor / 100;
        ImGui.BeginDisabled(recording);
        ImGui.TextUnformatted(Lang.Get("vsdirector:voice-take-name"));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##voice-take-name", ref voiceTakeName, 100);
        ImGui.TextUnformatted(Lang.Get("vsdirector:voice-record-track"));
        ImGui.SetNextItemWidth(-1);
        string automatic = Lang.Get("vsdirector:voice-auto-track");
        if (ImGui.BeginCombo("##voice-record-track", voiceTrackChoices.FirstOrDefault(t => t.Id == voiceDestinationTrack).Label ?? automatic))
        {
            if (ImGui.Selectable(automatic, voiceDestinationTrack == Guid.Empty)) voiceDestinationTrack = Guid.Empty;
            foreach (var choice in voiceTrackChoices)
                if (ImGui.Selectable(choice.Label + "###" + choice.Id, choice.Id == voiceDestinationTrack))
                    voiceDestinationTrack = choice.Id;
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        IReadOnlyList<string> devices = recorder.Devices;
        string currentDevice = recorder.SelectedDevice;
        string deviceLabel = currentDevice.Length == 0 ? Lang.Get("vsdirector:voice-default-device") : currentDevice;
        ImGui.TextUnformatted(Lang.Get("vsdirector:voice-input-device"));
        ImGui.SetNextItemWidth(-1);
        ImGui.BeginDisabled(recorder.IsRecording);
        if (ImGui.BeginCombo("##voice-input-device", deviceLabel))
        {
            bool defaultSelected = currentDevice.Length == 0;
            if (ImGui.Selectable(Lang.Get("vsdirector:voice-default-device"), defaultSelected))
                recorder.SelectedDevice = string.Empty;
            foreach (string device in devices)
            {
                bool selected = string.Equals(currentDevice, device, StringComparison.Ordinal);
                if (ImGui.Selectable(device, selected)) recorder.SelectedDevice = device;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();

        string[] noiseNames =
        [
            Lang.Get("vsdirector:voice-noise-off"),
            Lang.Get("vsdirector:voice-noise-gate"),
            Lang.Get("vsdirector:voice-noise-focus"),
            Lang.Get("vsdirector:voice-noise-rnnoise")
        ];
        int noiseMode = (int)recorder.NoiseReduction;
        ImGui.TextUnformatted(Lang.Get("vsdirector:voice-noise-reduction"));
        ImGui.SetNextItemWidth(-1);
        ImGui.BeginDisabled(recorder.IsRecording);
        if (ImGui.BeginCombo("##voice-noise-reduction", noiseNames[Math.Clamp(noiseMode, 0, noiseNames.Length - 1)]))
        {
            for (int index = 0; index < noiseNames.Length; index++)
            {
                bool selected = noiseMode == index;
                if (ImGui.Selectable(noiseNames[index], selected)) recorder.NoiseReduction = (DirectorNoiseReductionMode)index;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(recorder.Error)) ImGui.TextWrapped(recorder.Error);
        if (!string.IsNullOrWhiteSpace(recorder.NoiseReductionError)) ImGui.TextWrapped(recorder.NoiseReductionError);
    }

    private void DrawVoiceFileSelectionPanel()
    {
        ImGui.Separator();
        ImGui.TextUnformatted(Lang.Get("vsdirector:voice-audio-file"));
        string selectLabel = Lang.Get("vsdirector:voice-open-file-dialog");
        float buttonWidth = ImGui.CalcTextSize(selectLabel).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputText("##voice-audio-path", ref voiceAudioPath, 2048);
        ImGui.SameLine();
        if (ImGui.Button(selectLabel, new Vector2(buttonWidth, ImGui.GetFrameHeight())))
        {
            voiceFileCandidatePath = File.Exists(voiceAudioPath) ? voiceAudioPath : string.Empty;
            voiceFileImportError = string.Empty;
            voiceFileBrowserPath = File.Exists(voiceAudioPath)
                ? Path.GetDirectoryName(voiceAudioPath) ?? voiceAudioDirectory
                : Directory.Exists(voiceAudioPath) ? voiceAudioPath : voiceAudioDirectory;
            voiceAudioDirectory = voiceFileBrowserPath;
            voiceFileBrowserOpen = true;
            voiceFileBrowserPopupRequested = true;
        }
        DirectorMicrophoneRecorder? recorder = owner.MicrophoneRecorder;
        bool validAudio = File.Exists(voiceAudioPath)
            && string.Equals(Path.GetExtension(voiceAudioPath), ".wav", StringComparison.OrdinalIgnoreCase);
        ImGui.BeginDisabled(recorder?.IsRecording == true || !validAudio);
        if (ImGui.Button(Lang.Get("vsdirector:voice-use-file"), new Vector2(-1, 0)))
        {
            try
            {
                bool applied = ApplyVoiceAudioAsset(voiceAudioPath);
                voiceFileImportError = applied ? string.Empty : Lang.Get("vsdirector:voice-no-selected-keyframe");
                if (applied)
                {
                    ImGui.CloseCurrentPopup();
                    voiceRecorderWindowOpen = false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                voiceFileImportError = exception.Message;
            }
        }
        ImGui.EndDisabled();
        if (!string.IsNullOrWhiteSpace(voiceFileImportError)) ImGui.TextWrapped(voiceFileImportError);
    }

    private void DrawVoiceFileBrowser(float scale)
    {
        Vector2 displaySize = ImGui.GetIO().DisplaySize;
        Vector2 browserSize = new(Math.Min(700 * scale, displaySize.X - 24 * scale),
            Math.Min(500 * scale, displaySize.Y - 24 * scale));
        ImGui.SetNextWindowPos(displaySize * .5f, ImGuiCond.Always, new Vector2(.5f));
        ImGui.SetNextWindowSize(browserSize, ImGuiCond.Always);
        bool open = true;
        bool visible = ImGui.BeginPopupModal(Lang.Get("vsdirector:voice-file-browser") + "###voice-file-browser", ref open,
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar);
        if (!visible)
        {
            if (!open) voiceFileBrowserOpen = false;
            return;
        }
        try
        {
            string folderLabel = Lang.Get("vsdirector:voice-go-folder");
            float folderButtonWidth = ImGui.CalcTextSize(folderLabel).X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X - folderButtonWidth - ImGui.GetStyle().ItemSpacing.X));
            ImGui.InputText("##voice-audio-directory", ref voiceFileBrowserPath, 2048);
            ImGui.SameLine();
            if (ImGui.Button(folderLabel, new Vector2(folderButtonWidth, ImGui.GetFrameHeight()))
                && Directory.Exists(voiceFileBrowserPath))
                voiceAudioDirectory = voiceFileBrowserPath;
            ImGui.BeginChild("##voice-file-list", new Vector2(0, -88 * scale), true);
            try
            {
                if (Directory.Exists(voiceAudioDirectory))
                {
                    string? parent = Directory.GetParent(voiceAudioDirectory)?.FullName;
                    if (parent is not null && ImGui.Selectable("..", false, ImGuiSelectableFlags.AllowDoubleClick)
                        && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        voiceAudioDirectory = voiceFileBrowserPath = parent;
                    foreach (string directory in Directory.EnumerateDirectories(voiceAudioDirectory).OrderBy(path => path).Take(200))
                        if (ImGui.Selectable("📁 " + Path.GetFileName(directory), false, ImGuiSelectableFlags.AllowDoubleClick)
                            && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                                voiceAudioDirectory = voiceFileBrowserPath = directory;
                    foreach (string file in Directory.EnumerateFiles(voiceAudioDirectory)
                                 .Where(path => string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(path => path).Take(500))
                        if (ImGui.Selectable(Path.GetFileName(file), string.Equals(voiceFileCandidatePath, file, StringComparison.OrdinalIgnoreCase)))
                            voiceFileCandidatePath = file;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ImGui.TextWrapped(exception.Message);
            }
            ImGui.EndChild();
            bool validAudio = File.Exists(voiceFileCandidatePath)
                && string.Equals(Path.GetExtension(voiceFileCandidatePath), ".wav", StringComparison.OrdinalIgnoreCase);
            ImGui.BeginDisabled(!validAudio);
            if (ImGui.Button(Lang.Get("vsdirector:voice-confirm-selection"), new Vector2(210, 34) * scale))
            {
                voiceAudioPath = voiceFileCandidatePath;
                voiceFileImportError = string.Empty;
                voiceFileBrowserOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            if (!string.IsNullOrWhiteSpace(voiceFileImportError)) ImGui.TextWrapped(voiceFileImportError);
            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("vsdirector:close"), new Vector2(120, 34) * scale))
            {
                voiceFileBrowserOpen = false;
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            if (!open) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if (!open) voiceFileBrowserOpen = false;
    }

    internal void OpenVoiceRecorder()
    {
        if (!voiceRecorderWindowOpen)
        {
            voiceDestinationTrack = Guid.Empty;
            voiceTrackChoices = VoiceTrackChoices(CurrentReplayViewportTimeline());
            voiceTakeName = Lang.Get("vsdirector:voice-default-take");
        }
        voiceRecorderPopupRequested = !voiceRecorderWindowOpen;
        voiceRecorderWindowOpen = true;
    }

    private static (Guid Id, string Label)[] VoiceTrackChoices(DirectorReplayViewportTimeline timeline)
        => timeline.Tracks.Where(track => track.TrackType == DirectorReplayElementTrackType.VoiceAudio
                && !track.SpeakerId.EndsWith(":volume", StringComparison.Ordinal))
            .Select((track, index) =>
            {
                string? group = timeline.Tracks.FirstOrDefault(parent => parent.Id == track.ParentGroupId)?.Name;
                return (track.Id, $"{index + 1} · {group ?? track.Name}");
            }).ToArray();

    private bool ApplyVoiceAudioAsset(string path)
    {
        DirectorReplayElementKeyframe? keyframe = SelectedReplayElementKeyframe();
        if (keyframe is null || keyframe.TrackType != DirectorReplayElementTrackType.VoiceAudio) return false;
        DirectorReplayViewportTimeline timeline = CurrentReplayViewportTimeline();
        DirectorReplayElementKeyframe? editable = timeline.ElementKeyframes.FirstOrDefault(item => item.Id == keyframe.Id);
        DirectorReplayElementTrack? track = timeline.Tracks.FirstOrDefault(item => item.Id == keyframe.TrackId);
        if (editable is null || track is null || track.SpeakerId.EndsWith(":volume", StringComparison.Ordinal)) return false;
        if (voiceDestinationTrack != Guid.Empty && voiceDestinationTrack != track.Id)
        {
            var destination = timeline.Tracks.FirstOrDefault(t => t.Id == voiceDestinationTrack
                && t.TrackType == DirectorReplayElementTrackType.VoiceAudio && !t.SpeakerId.EndsWith(":volume", StringComparison.Ordinal));
            if (destination is null) return false;
            if (!string.IsNullOrEmpty(editable.AudioAsset))
            {
                editable = new DirectorReplayElementKeyframe { TimeSeconds = keyframe.TimeSeconds, TrackType = keyframe.TrackType };
                timeline.ElementKeyframes.Add(editable);
            }
            editable.TrackId = destination.Id;
            track = destination;
        }
        string directory = Path.Combine(capi.GetOrCreateDataPath("ModData"), "VSDirector", "Audio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string importedPath = Path.Combine(directory, Path.GetFileName(path));
        string speakerId = string.IsNullOrWhiteSpace(track.SpeakerId)
            ? "vsdirector:audio-asset:" + track.Id.ToString("N")
            : track.SpeakerId;
        track.SpeakerId = speakerId;
        bool committed = false;
        try
        {
            File.Copy(path, importedPath);
            editable.AudioAsset = importedPath;
            if (voiceDestinationTrack == Guid.Empty && !timeline.ElementKeyframes.Any(other =>
                    other.TrackId == track.Id && other.Id != editable.Id && !string.IsNullOrWhiteSpace(other.AudioAsset)))
            {
                var group = timeline.Tracks.FirstOrDefault(t => t.Id == track.ParentGroupId);
                if (group is not null) group.Name = Path.GetFileNameWithoutExtension(path);
                else track.Name = Path.GetFileNameWithoutExtension(path);
            }
            committed = CommitReplayViewportTimeline(timeline);
            if (committed)
            {
                selectedReplayElementTrackId = track.Id;
                selectedReplayElementKeyframeId = editable.Id;
                imGuiTimelineDirty = true;
            }
            return committed;
        }
        finally
        {
            if (!committed)
            {
                File.Delete(importedPath);
                Directory.Delete(directory);
            }
        }
    }

    private void OpenImGuiResourceMenu(DirectorWorkspaceSection target)
    {
        if (DirectorToolWindows.HasOpenTool || !IsSectionAvailable(target) || resourceMenuSection == target) return;
        PreserveInputs();
        resourceMenuSection = target;
        resourceScrollOffset = 0;
        ComposeResources();
        UpdateImGuiMenuStatus();
    }

    private void ComposeImGuiResources()
    {
        if (sceneBrowser?.IsOpened == true) ComposeSceneWindow();
        EmptyMenuComposer(ResourceMenuKey);
        replayResourceSplitBounds = replayResourceTopBounds = replayResourceViewBounds = null;
        if (resourceMenuSection is not { } target || !IsSectionAvailable(target))
        {
            imGuiResourceWindow?.Close();
            return;
        }
        var resources = new List<DirectorWorkspaceMenus.Resource>();
        var views = new List<DirectorWorkspaceMenus.Resource>();
        void Add(Guid id, string name, string detail, string description, IEnumerable<string> tags, Action select, Action activate)
        {
            void Run(Action action)
            {
                PreserveInputs();
                ActivateResourceMenuSection(target);
                action();
                Refresh();
            }
            resources.Add(new(id.ToString(), name, detail, name + " " + description + " " + string.Join(" ", tags),
                () => Run(select), () => Run(activate), selectedId == id));
        }
        switch (target)
        {
            case DirectorWorkspaceSection.Cameras:
                foreach (var camera in owner.Cameras)
                {
                    var (x, y, z) = owner.DisplayPosition(camera.X, camera.Y, camera.Z);
                    Add(camera.Id, camera.Name, $"{x:0.##}, {y:0.##}, {z:0.##}  FOV {camera.Fov:0.#}", camera.Description, camera.Tags,
                        () => SelectCamera(camera.Id), () => ActivateCameraFromWorkspace(camera.Id));
                }
                break;
            case DirectorWorkspaceSection.Paths:
                foreach (var path in owner.Paths)
                    Add(path.Id, path.Name, PathResourceDetails(path), path.Description, path.Tags,
                        () => SelectPath(path.Id), () => PlayPathFromWorkspace(path.Id, reverse: false));
                break;
            case DirectorWorkspaceSection.Follows:
                foreach (var rig in owner.FollowRigs)
                    Add(rig.Id, rig.Name, FollowResourceDetails(rig), rig.Description, rig.Tags,
                        () => SelectFollow(rig.Id), () => ActivateFollowFromWorkspace(rig.Id));
                break;
            case DirectorWorkspaceSection.Replay:
                foreach (var replay in owner.Replays)
                    Add(replay.Id, replay.Name, Lang.Get("vsdirector:replay-row-detail", replay.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                        replay.ActorCount, replay.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)), replay.Description, replay.Tags,
                        () => SelectReplay(replay.Id), () => PlayReplayFromWorkspace(replay.Id));
                if (HasReplayViewEditor)
                    foreach (var view in ReplayViewOptions())
                        views.Add(new(view.Id.ToString(), view.Name, ReplayViewResourceDetails(view.Id), view.Name,
                            () => { SelectReplayViewSource(view.Id, activate: false); ComposeResources(); },
                            () => { SelectReplayViewSource(view.Id, activate: true); ComposeResources(); }, view.Id == replayViewSourceId));
                break;
        }
        bool replayMenu = target == DirectorWorkspaceSection.Replay;
        var form = DirectorWorkspaceMenus.Browser(SectionLabel(target), search, replayMenu ? replayRecordingName : newName, replayMenu,
            Lang.Get("vsdirector:search"), replayMenu ? Lang.Get("vsdirector:replay-name-placeholder") : ResourceCreatePlaceholder(),
            Lang.Get("vsdirector:create"), Lang.Get("vsdirector:resource-record"), Lang.Get("vsdirector:resource-stop"), Lang.Get("vsdirector:refresh"),
            Lang.Get(replayMenu || target == DirectorWorkspaceSection.Paths ? "vsdirector:play" : "vsdirector:activate"),
            resources, views, ReplayViewResourceBlockTitle(), value => { search = value; ComposeResources(); },
            value => { if (replayMenu) replayRecordingName = value; else newName = value; }, () => OnCreateResource(),
            () => { StartReplayRecording(); ComposeResources(); }, () => { StopReplayRecording(); ComposeResources(); },
            () => { RefreshReplayIndex(); ComposeResources(); },
            !owner.IsReplayRecording && !owner.HasPendingReplaySave, owner.IsReplayRecording || owner.HasFailedReplaySave);
        if (imGuiResourceWindow is null)
        {
            imGuiResourceWindow = new DirectorImGuiWindow(capi, "resource-browser", docked: true, escape: () => OnEscapePressed(), overlay: true, workspace: imGuiWorkspace);
            imGuiResourceWindow.Closed += CloseResourceMenu;
        }
        imGuiResourceWindow.Render(SectionLabel(target), form);
        var panel = layout.Panel("resources");
        ConfigureResourceMenuPanel(panel);
        imGuiResourceWindow.SetDockBounds(panel.X, panel.Y, panel.Width, panel.Height);
        if (IsOpened()) imGuiResourceWindow.Open();
    }

    private void UpdateImGuiMenuStatus()
    {
        if (imGuiToolbarWindow?.Document is { IsDisposed: false })
        {
            imGuiToolbarWindow.SetEnabled("export", owner.IsReplayPlaying);
        }
        if (imGuiResourceWindow?.Document is { IsDisposed: false })
            DirectorWorkspaceMenus.UpdateRecordingButton(imGuiResourceWindow,
                !owner.IsReplayRecording && !owner.HasPendingReplaySave, owner.IsReplayRecording || owner.HasFailedReplaySave);
    }

    private DirectorWorkspaceSection? ImGuiToolbarSectionAt(int x, int y)
    {
        if (imGuiToolbarWindow?.Document is { IsDisposed: false } doc)
            foreach (var target in VisibleToolbarSections())
                if (doc.GetElementById(ToolbarTabKey(target)) is { } element && element.Bounds.Width > 0 && Contains(element.Bounds, x, y)) return target;

        return null;
    }
    private double ImGuiToolbarAnchor(DirectorWorkspaceSection target)
    {
        if (imGuiToolbarWindow?.Document?.GetElementById(ToolbarTabKey(target)) is { } element
            && element.Bounds.Width > 0)
            return element.Bounds.X / Math.Max(.001, GuiElement.scaled(1));

        if (imGuiToolbarWindow?.Document?.GetElementById("toolbar-overflow") is { } overflow
            && overflow.Bounds.Width > 0)
            return overflow.Bounds.X / Math.Max(.001, GuiElement.scaled(1));

        // Toolbar commands are rendered as a native ImGui button row.  The
        // replay resource command follows the optional scene command; account
        // for that command and ImGui's item spacing when calculating the menu
        // anchor instead of falling back to the top-left corner.
        double anchor = layout.Panel("toolbar").X;
        if (SceneSections().Any())
            anchor += ToolbarButtonWidth(Lang.Get("vsdirector:workspace-scene")) + 4d;
        return anchor;
    }
    private bool ImGuiResourceContains(int x, int y)
        => imGuiResourceWindow?.IsOpened == true && imGuiResourceWindow.ContainsScreenPoint(x, y);
    private static bool Contains(DirectorImGuiElementBounds bounds, int x, int y)
        => x >= bounds.X && y >= bounds.Y && x < bounds.X + bounds.Width && y < bounds.Y + bounds.Height;

    private IEnumerable<DirectorWorkspaceSection> SceneSections()
        => new[] { DirectorWorkspaceSection.Cameras, DirectorWorkspaceSection.Paths, DirectorWorkspaceSection.Follows }.Where(IsSectionAvailable);

    private void OpenSceneWindow()
    {
        ComposeSceneWindow();
    }

    private void ComposeSceneWindow()
    {
        var available = SceneSections().ToArray();
        if (available.Length == 0) { sceneBrowser?.Close(); return; }
        if (!available.Contains(sceneCategory)) sceneCategory = available[0];
        var categories = new List<DirectorSceneBrowser.Category>();
        foreach (var target in available)
        {
            var items = new List<DirectorSceneBrowser.Resource>();
            void Add(Guid id, string name, string detail, string description, IEnumerable<string> tags, Action select, Action activate)
            {
                void Run(Action action)
                {
                    sceneBrowser?.Close();
                    ActivateResourceMenuSection(target);
                    action();
                    Refresh();
                }
                items.Add(new(id.ToString(), name, detail, name + " " + description + " " + string.Join(" ", tags),
                    () => Run(select), () => Run(activate), selectedId == id));
            }
            if (target == DirectorWorkspaceSection.Cameras)
                foreach (var camera in owner.Cameras)
                {
                    var (x, y, z) = owner.DisplayPosition(camera.X, camera.Y, camera.Z);
                    Add(camera.Id, camera.Name, $"{x:0.##}, {y:0.##}, {z:0.##}  FOV {camera.Fov:0.#}", camera.Description, camera.Tags,
                        () => SelectCamera(camera.Id), () => ActivateCameraFromWorkspace(camera.Id));
                }
            else if (target == DirectorWorkspaceSection.Paths)
                foreach (var path in owner.Paths)
                    Add(path.Id, path.Name, PathResourceDetails(path), path.Description, path.Tags,
                        () => SelectPath(path.Id), () => PlayPathFromWorkspace(path.Id, reverse: false));
            else
                foreach (var rig in owner.FollowRigs)
                    Add(rig.Id, rig.Name, FollowResourceDetails(rig), rig.Description, rig.Tags,
                        () => SelectFollow(rig.Id), () => ActivateFollowFromWorkspace(rig.Id));
            string placeholder = target switch
            {
                DirectorWorkspaceSection.Cameras => "escape-camera-name-placeholder",
                DirectorWorkspaceSection.Paths => "path-name-placeholder",
                _ => "follow-name-placeholder"
            };
            categories.Add(new(target.ToString(), SectionLabel(target), Lang.Get("vsdirector:" + placeholder),
                Lang.Get(target == DirectorWorkspaceSection.Paths ? "vsdirector:play" : "vsdirector:activate"), items));
        }
        sceneBrowser ??= new DirectorSceneBrowser(capi, imGuiWorkspace);
        sceneBrowser.Show(categories, sceneCategory.ToString(), sceneSearch, newName,
            Lang.Get("vsdirector:workspace-scene"),
            Lang.Get("vsdirector:scene-search"), Lang.Get("vsdirector:scene-results"), Lang.Get("vsdirector:scene-empty"), Lang.Get("vsdirector:create"),
            id => sceneCategory = Enum.Parse<DirectorWorkspaceSection>(id), value => sceneSearch = value, value => newName = value,
            () => { ActivateResourceMenuSection(sceneCategory); OnCreateResource(); });
    }
}
