using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private bool composingImGuiInspector;
    private readonly HashSet<string> imGuiInspectorCollapsed = new();
    private string imGuiInspectorScrollKey = "";

    private void ComposeInspector()
    {
        // Old timeline commands may still query optional inspector fields. Keep an empty
        // composer for those queries, without a rendered panel or a mouse hit region.
        if (Composers[InspectorKey] is null)
            Composers[InspectorKey] = capi.Gui.CreateCompo(InspectorKey, ElementBounds.Fixed(-2, -2, 1, 1)).Compose(false);
        replayInspectorSplitBounds = null;
        replayInspectorTopBounds = null;
        replayInspectorViewBounds = null;
        replayVisualSettingsBounds = null;
        if (!composingImGuiInspector)
        {
            imGuiInspectorWindow ??= new DirectorImGuiWindow(capi, "inspector", docked: true, escape: () => OnEscapePressed(), workspace: imGuiWorkspace);
            imGuiViewWindow ??= new DirectorImGuiWindow(capi, "view", docked: true, escape: () => OnEscapePressed(), workspace: imGuiWorkspace);
            ComposeImGuiInspector();
            imGuiInspectorWindow.SetInputEnabled(!layout.NavigationMode);
            imGuiViewWindow.SetInputEnabled(!layout.NavigationMode);
            ComposeCameraTransform();
            UpdateInspectorBounds();
            if (IsOpened() && !imGuiInspectorWindow.IsOpened) imGuiInspectorWindow.Open();
            if (IsOpened() && !imGuiViewWindow.IsOpened) imGuiViewWindow.Open();
        }
    }

    private bool ToggleImGuiInspector(bool visual)
    {
        PreserveInputs();
        FlushPendingAutoSave();
        foreach (var entry in InspectorSections(visual)) imGuiInspectorCollapsed.Remove(section + ":" + entry.Key);
        resourceMenuSection = null;
        ComposeResources();
        ComposeInspector();
        (visual ? imGuiViewWindow : imGuiInspectorWindow)?.FocusNextFrame();
        return true;
    }

    private void UpdateInspectorBounds()
    {
        var panel = layout.Panel("inspector");
        imGuiInspectorWindow?.SetDockBounds(panel.X, panel.Y, panel.Width, panel.Height);
        imGuiViewWindow?.SetDockBounds(panel.X, panel.Y, panel.Width, panel.Height);
    }

    private static string ImGuiLabel(string key) => Lang.Get("vsdirector:" + key);

    private void ComposeImGuiInspector()
    {
        if (imGuiInspectorWindow is null || imGuiInspectorWindow.IsDisposed) return;
        string scrollKey = section.ToString();
        float scrollY = scrollKey == imGuiInspectorScrollKey
            ? imGuiInspectorWindow.Document?.GetElementById("content")?.Bounds.ScrollY ?? 0 : 0;
        composingImGuiInspector = true;
        try
        {
            foreach (bool visual in new[] { false, true })
            {
                var form = new DirectorImGuiForm { Docked = true, ClassName = "inspector" };
                switch (section)
                {
                    case DirectorWorkspaceSection.Cameras when cameraEdit is not null: ImGuiCamera(form, visual); break;
                    case DirectorWorkspaceSection.Paths when pathEdit is not null: ImGuiPath(form, visual); break;
                    case DirectorWorkspaceSection.Follows when followEdit is not null: ImGuiFollow(form, visual); break;
                    case DirectorWorkspaceSection.Replay: ImGuiReplay(form, visual); break;
                    default:
                        form.Note(ImGuiLabel(section switch
                        {
                            DirectorWorkspaceSection.Paths => "workspace-select-path",
                            DirectorWorkspaceSection.Follows => "workspace-select-follow",
                            _ => "workspace-select-camera"
                        }));
                        break;
                }
                var panel = layout.Panel("inspector");
                (visual ? imGuiViewWindow : imGuiInspectorWindow)?.Render(ImGuiLabel(visual ? "replay-right-view" : "replay-right-inspector"), form, (int)panel.Width, (int)panel.Height);
            }
            UpdateInspectorBounds();
            imGuiInspectorWindow.Document?.GetElementById("content")?.SetScrollOffset(0, scrollY);
            imGuiInspectorScrollKey = scrollKey;
        }
        finally { composingImGuiInspector = false; }
    }

    private IEnumerable<(string Key, string Label)> InspectorSections(bool visual)
    {
        if (!visual) return new[] { ("general", ImGuiLabel("inspector-section-general")), ("details", ImGuiLabel("inspector-section-details")) };
        if (section == DirectorWorkspaceSection.Replay)
            return ReplayVisualGroupDefinitions.Select(g => (g.Code, ImGuiLabel("replay-visual-group-" + g.Code)));
        string[] keys = section switch
        {
            DirectorWorkspaceSection.Cameras => ["pose", "lens", "display"],
            DirectorWorkspaceSection.Paths => ["playback", "keyframe"],
            DirectorWorkspaceSection.Follows => ["target", "pose", "motion", "lens"],
            _ => ["general"]
        };
        return keys.Select(key => (key, ImGuiLabel("inspector-section-" + key)));
    }

    private void InspectorSection(DirectorImGuiForm form, string key)
    {
        string label = InspectorSections(false).Concat(InspectorSections(true)).FirstOrDefault(s => s.Key == key).Label
            ?? ImGuiLabel("inspector-section-" + key);
        string stateKey = section + ":" + key;
        form.Accordion(label, !imGuiInspectorCollapsed.Contains(stateKey),
            expanded => { if (expanded) imGuiInspectorCollapsed.Remove(stateKey); else imGuiInspectorCollapsed.Add(stateKey); });
    }

    private void ImGuiCamera(DirectorImGuiForm form, bool visual)
    {
        var window = visual ? imGuiViewWindow : imGuiInspectorWindow;
        var camera = cameraEdit!;
        void Save()
        {
            owner.PreviewCameraEdit(camera);
            window?.Status(ImGuiLabel("auto-save-pending"));
            window?.QueueCommit(() =>
            {
                if (!owner.TryUpdateCamera(camera, out string message, showNotification: false)
                    && owner.FindCamera(camera.Id) is { } saved) owner.PreviewCameraEdit(saved);
                window?.Status(message);
            });
        }
        form.Note(camera.Name);
        if (!visual)
        {
            InspectorSection(form, "general");
            form.Text(ImGuiLabel("name"), camera.Name, v => { camera.Name = v; Save(); });
            form.Text(ImGuiLabel("tags"), string.Join(", ", camera.Tags), v => { camera.Tags = ParseTags(v); Save(); });
            InspectorSection(form, "details");
            form.Text(ImGuiLabel("description"), camera.Description, v => { camera.Description = v; Save(); });
        }
        else
        {
            InspectorSection(form, "pose");
            var displayed = owner.DisplayPosition(camera.X, camera.Y, camera.Z);
            double dx = camera.X - displayed.Item1, dy = camera.Y - displayed.Item2, dz = camera.Z - displayed.Item3;
            form.Number("X", displayed.Item1, v => { camera.X = v + dx; Save(); });
            form.Number("Y", displayed.Item2, v => { camera.Y = v + dy; Save(); });
            form.Number("Z", displayed.Item3, v => { camera.Z = v + dz; Save(); });
            form.Number("Yaw", camera.Yaw, v => { camera.Yaw = (float)v; Save(); });
            form.Number("Pitch", camera.Pitch, v => { camera.Pitch = (float)v; Save(); });
            form.Slider(ImGuiLabel("roll"), camera.Roll, -180, 180, 1, v => { camera.Roll = (float)v; Save(); });
            InspectorSection(form, "lens");
            form.Slider(ImGuiLabel("fov"), camera.Fov, 10, 170, 1, v => { camera.Fov = (float)v; Save(); });
            InspectorSection(form, "display");
            form.Number(ImGuiLabel("camera-dimension"), camera.Dimension, v => { camera.Dimension = (int)Math.Clamp(v, int.MinValue, int.MaxValue); Save(); });
            ImGuiQuickSlot(form, camera.QuickSlot, v => { camera.QuickSlot = v; Save(); });
            form.Check(ImGuiLabel("camera-model-show"), camera.PreviewVisible, v => { camera.PreviewVisible = v; Save(); });
        }
        form.EndSection();
        form.FooterButton(ImGuiLabel("recapture"), () => { RecaptureCameraEditor(); ComposeImGuiInspector(); });
        form.FooterButton(ImGuiLabel("activate"), () => ActivateCameraEditor());
        form.FooterButton(DeleteLabel(camera.Id), () => DeleteCameraEditor(), "danger");
    }

    private void ImGuiPath(DirectorImGuiForm form, bool visual)
    {
        var window = visual ? imGuiViewWindow : imGuiInspectorWindow;
        var path = pathEdit!;
        void Edit(Action change)
        {
            var before = path.DeepClone();
            change();
            RecordPathEdit(before);
            pathTagsText = string.Join(", ", path.Tags);
            pathDescriptionText = path.Description;
            SyncPathTimingText();
            owner.SetPathEditPreview(path, SelectedPathKeyframeIndex(), pathEditorPlayhead, selectedKeyframes: selectedPathKeyframes);
            window?.QueueCommit(() => { owner.TrySavePath(path, out string message, showNotification: false); window?.Status(message); });
        }
        form.Note(path.Name);
        if (!visual)
        {
            InspectorSection(form, "general");
            form.Text(ImGuiLabel("name"), path.Name, v => Edit(() => path.Name = v));
            form.Text(ImGuiLabel("tags"), string.Join(", ", path.Tags), v => Edit(() => path.Tags = ParseTags(v)));
            InspectorSection(form, "details");
            form.Text(ImGuiLabel("description"), path.Description, v => Edit(() => path.Description = v));
        }
        else
        {
            InspectorSection(form, "playback");
            form.Enum(ImGuiLabel("interpolation"), path.Interpolation, v => Edit(() => path.Interpolation = v),
                v => ImGuiLabel("interpolation-" + v.ToString().ToLowerInvariant()));
            form.Enum(ImGuiLabel("loop-mode"), path.LoopMode, v => Edit(() => path.LoopMode = v),
                v => ImGuiLabel("loop-" + v.ToString().ToLowerInvariant()));
            form.NumberStep(ImGuiLabel("path-speed"), path.PlaybackSpeed, .1f, v => Edit(() => path.PlaybackSpeed = v));
            form.Check(ImGuiLabel("path-preview"), path.PreviewVisible, v => Edit(() => path.PreviewVisible = v));
            InspectorSection(form, "keyframe");
            if (SelectedPathFrame() is { } frame)
            {
                form.Note(ImGuiLabel("path-camera-keyframe-help"));
                form.Text(ImGuiLabel("name"), frame.Name, v => Edit(() => frame.Name = v));
                var displayed = owner.DisplayPosition(frame.X, frame.Y, frame.Z);
                double dx = frame.X - displayed.Item1, dy = frame.Y - displayed.Item2, dz = frame.Z - displayed.Item3;
                form.Number("X", displayed.Item1, v => Edit(() => frame.X = v + dx));
                form.Number("Y", displayed.Item2, v => Edit(() => frame.Y = v + dy));
                form.Number("Z", displayed.Item3, v => Edit(() => frame.Z = v + dz));
                form.Number("Yaw", frame.Yaw, v => Edit(() => frame.Yaw = (float)v));
                form.Number("Pitch", frame.Pitch, v => Edit(() => frame.Pitch = (float)v));
                form.Check(ImGuiLabel("bezier-custom-handles"), frame.BezierHandlesEnabled, v => { Edit(() => frame.BezierHandlesEnabled = v); ComposeImGuiInspector(); });
                if (frame.BezierHandlesEnabled)
                {
                    form.Number("In X", frame.BezierInX, v => Edit(() => frame.BezierInX = v));
                    form.Number("In Y", frame.BezierInY, v => Edit(() => frame.BezierInY = v));
                    form.Number("In Z", frame.BezierInZ, v => Edit(() => frame.BezierInZ = v));
                    form.Number("Out X", frame.BezierOutX, v => Edit(() => frame.BezierOutX = v));
                    form.Number("Out Y", frame.BezierOutY, v => Edit(() => frame.BezierOutY = v));
                    form.Number("Out Z", frame.BezierOutZ, v => Edit(() => frame.BezierOutZ = v));
                }
            }
            else form.Note(ImGuiLabel("inspector-select-keyframe"));
            if (CanManageChunkPreload) form.FooterButton(ImGuiLabel("inspector-section-preload"), () => OpenPathPreloadEditor());
        }
    }

    private void ImGuiFollow(DirectorImGuiForm form, bool visual)
    {
        var window = visual ? imGuiViewWindow : imGuiInspectorWindow;
        var rig = followEdit!;
        void Save() => window?.QueueCommit(() => { if (owner.TrySaveFollowRig(rig, out string message, showNotification: false)) owner.TryPreviewFollowRig(rig); window?.Status(message); });
        form.Note(rig.Name);
        if (!visual)
        {
            InspectorSection(form, "general");
            form.Text(ImGuiLabel("name"), rig.Name, v => { rig.Name = v; Save(); });
            form.Text(ImGuiLabel("tags"), string.Join(", ", rig.Tags), v => { rig.Tags = ParseTags(v); Save(); });
            InspectorSection(form, "details");
            form.Text(ImGuiLabel("description"), rig.Description, v => { rig.Description = v; Save(); });
        }
        else
        {
            InspectorSection(form, "target");
            var targets = owner.GetFollowTargets();
            form.Select(ImGuiLabel("follow-target"), FollowTargetSelector(rig), targets.Select(t => (t.Selector, t.DisplayName)), v =>
            {
                if (owner.FindFollowTarget(v) is { } target)
                {
                    rig.TargetType = target.TargetType; rig.TargetPlayerUid = target.PlayerUid;
                    rig.TargetEntityId = target.EntityId; rig.TargetDisplayName = target.DisplayName; Save();
                }
            });
            form.Enum(ImGuiLabel("follow-view-mode"), rig.ViewMode, v => { rig.ViewMode = v; Save(); }, v => ImGuiLabel(v == CameraFollowViewMode.FirstPerson ? "follow-view-first" : "follow-view-third"));
            form.Enum(ImGuiLabel("follow-anchor"), rig.Anchor, v => { rig.Anchor = v; Save(); }, v => ImGuiLabel("follow-anchor-" + v.ToString().ToLowerInvariant()));
            form.Check(ImGuiLabel("follow-clear-view"), rig.OpenClearView, v => { rig.OpenClearView = v; Save(); });
            InspectorSection(form, "pose");
            form.Number(ImGuiLabel("follow-offset-x"), rig.OffsetX, v => { rig.OffsetX = v; Save(); });
            form.Number(ImGuiLabel("follow-offset-y"), rig.OffsetY, v => { rig.OffsetY = v; Save(); });
            form.Number(ImGuiLabel("follow-offset-z"), rig.OffsetZ, v => { rig.OffsetZ = v; Save(); });
            form.Number(ImGuiLabel("follow-distance"), rig.Distance, v => { rig.Distance = v; Save(); });
            form.Number(ImGuiLabel("follow-height"), rig.Height, v => { rig.Height = v; Save(); });
            form.Number(ImGuiLabel("follow-orbit-angle"), rig.OrbitAngleDegrees, v => { rig.OrbitAngleDegrees = v; Save(); });
            InspectorSection(form, "motion");
            form.Enum(ImGuiLabel("follow-motion-mode"), rig.MotionMode, v => { rig.MotionMode = v; Save(); }, v => ImGuiLabel("follow-motion-" + v.ToString().ToLowerInvariant()));
            form.Number(ImGuiLabel("follow-orbit-speed"), rig.OrbitSpeedDegreesPerSecond, v => { rig.OrbitSpeedDegreesPerSecond = v; Save(); });
            form.NumberStep(ImGuiLabel("follow-smooth-time"), rig.SmoothTimeSeconds, .01f, v => { rig.SmoothTimeSeconds = v; Save(); });
            form.NumberStep(ImGuiLabel("follow-delay"), rig.DelaySeconds, .05f, v => { rig.DelaySeconds = v; Save(); });
            InspectorSection(form, "lens");
            form.Slider(ImGuiLabel("follow-fov"), rig.Fov, 10, 170, 1, v => { rig.Fov = (float)v; Save(); });
            ImGuiQuickSlot(form, rig.QuickSlot, v => { rig.QuickSlot = v; Save(); });
        }
        form.EndSection();
        form.FooterButton(ImGuiLabel("activate"), () => ActivateFollowEditor());
        form.FooterButton(DeleteLabel(rig.Id), () => DeleteFollowEditor(), "danger");
    }

    private void ImGuiQuickSlot(DirectorImGuiForm form, int? slot, Action<int?> changed)
        => form.Select(ImGuiLabel("quick-slot"), (slot ?? 0).ToString(CultureInfo.InvariantCulture),
            new[] { ("0", ImGuiLabel("none")) }.Concat(Enumerable.Range(1, 10).Select(i => (i.ToString(CultureInfo.InvariantCulture), owner.QuickSlotHotkeyLabel(i)))),
            v => changed(int.TryParse(v, out int next) && next > 0 ? next : null));

    private void ImGuiReplay(DirectorImGuiForm form, bool visual)
    {
        var window = visual ? imGuiViewWindow : imGuiInspectorWindow;
        if (!visual)
        {
            if (SelectedReplayInfo() is not { } info) { form.Note(ImGuiLabel("workspace-select-replay")); return; }
            string name = info.Name, tags = string.Join(", ", info.Tags), description = info.Description;
            void Save()
            {
                replayMetadataName = name; replayMetadataTags = tags; replayMetadataDescription = description;
                window?.QueueCommit(() => { owner.TryUpdateReplayMetadata(info.Id, name, tags, description, out string message); window?.Status(message); });
            }
            InspectorSection(form, "general");
            form.Text(ImGuiLabel("name"), name, v => { name = v; Save(); });
            form.Text(ImGuiLabel("tags"), tags, v => { tags = v; Save(); });
            InspectorSection(form, "details");
            form.Text(ImGuiLabel("description"), description, v => { description = v; Save(); });
            return;
        }
        form.EndSection();
        var visuals = CurrentReplayVisuals();
        foreach (var group in ReplayVisualGroupDefinitions)
        {
            InspectorSection(form, group.Code);
            foreach (var descriptor in ReplayVisualDescriptors.Where(d => d.GroupCode == group.Code))
            {
                object? value = GetReplayVisualValue(visuals, descriptor);
                Type type = descriptor.ValueProperty.PropertyType;
                string label = Lang.Get(descriptor.LanguageKey);
                void Set(object next) => SetReplayVisualValue(descriptor, next, recompose: false);
                if (descriptor.Editor == ReplayVisualEditor.Time)
                    form.Time(label, TimeSpan.FromMinutes(Math.Round(Math.Clamp(Convert.ToInt32(value), 0, 24000) * 1440d / 24000) % 1440), v => Set((int)Math.Round(v.TotalMinutes * 24000 / 1440)));
                else if (descriptor.Editor == ReplayVisualEditor.Color) form.Color(label, (string?)value ?? "#FFFFFF", v => Set(v));
                else if (descriptor.Range is { } range)
                    form.Slider(label, Convert.ToDouble(value, CultureInfo.InvariantCulture), range.MinimumValue, range.MaximumValue, range.Step / range.Scale, v => Set(v));
                else if (type == typeof(bool)) form.Check(label, (bool)value!, v => Set(v));
                else if (type.IsEnum)
                    form.Select(label, value!.ToString()!, Enum.GetNames(type).Select(v => (v, ImGuiLabel("replay-visual-option-" + ToKebabCase(v)))), v => Set(v));
                else if (type == typeof(string)) form.Text(label, (string?)value ?? "", v => Set(v));
                else form.Number(label, Convert.ToDouble(value, CultureInfo.InvariantCulture), v => Set(v));
            }
        }
        form.EndSection();
        if (CanManageChunkPreload) form.FooterButton(ImGuiLabel("inspector-section-preload"), () => { preloadEditorExpanded = true; ComposeWorkflowCanvas(); });
    }

    internal void CloseFloatingTool() => CloseFloatingEditorWindow();
}
