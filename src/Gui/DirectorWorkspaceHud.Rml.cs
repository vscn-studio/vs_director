using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private bool composingRmlInspector;
    private readonly HashSet<string> rmlInspectorCollapsed = new();
    private string rmlInspectorScrollKey = "";

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
        if (!composingRmlInspector)
        {
            rmlInspectorWindow ??= new DirectorImGuiWindow(capi, "inspector", docked: true, escape: () => OnEscapePressed(), workspace: imGuiWorkspace);
            rmlViewWindow ??= new DirectorImGuiWindow(capi, "view", docked: true, escape: () => OnEscapePressed(), workspace: imGuiWorkspace);
            ComposeRmlInspector();
            rmlInspectorWindow.SetInputEnabled(!layout.NavigationMode);
            rmlViewWindow.SetInputEnabled(!layout.NavigationMode);
            ComposeCameraTransform();
            UpdateInspectorBounds();
            if (IsOpened() && !rmlInspectorWindow.IsOpened) rmlInspectorWindow.Open();
            if (IsOpened() && !rmlViewWindow.IsOpened) rmlViewWindow.Open();
        }
    }

    private bool ToggleRmlInspector(bool visual)
    {
        PreserveInputs();
        FlushPendingAutoSave();
        foreach (var entry in InspectorSections(visual)) rmlInspectorCollapsed.Remove(section + ":" + entry.Key);
        resourceMenuSection = null;
        ComposeResources();
        ComposeInspector();
        (visual ? rmlViewWindow : rmlInspectorWindow)?.FocusNextFrame();
        return true;
    }

    private void UpdateInspectorBounds()
    {
        var panel = layout.Panel("inspector");
        rmlInspectorWindow?.SetDockBounds(panel.X, panel.Y, panel.Width, panel.Height);
        rmlViewWindow?.SetDockBounds(panel.X, panel.Y, panel.Width, panel.Height);
    }

    private static string RmlLabel(string key) => Lang.Get("vsdirector:" + key);

    private void ComposeRmlInspector()
    {
        if (rmlInspectorWindow is null || rmlInspectorWindow.IsDisposed) return;
        string scrollKey = section.ToString();
        float scrollY = scrollKey == rmlInspectorScrollKey
            ? rmlInspectorWindow.Document?.GetElementById("content")?.Bounds.ScrollY ?? 0 : 0;
        composingRmlInspector = true;
        try
        {
            foreach (bool visual in new[] { false, true })
            {
                var form = new DirectorImGuiForm { Docked = true, ClassName = "inspector" };
                switch (section)
                {
                    case DirectorWorkspaceSection.Cameras when cameraEdit is not null: RmlCamera(form, visual); break;
                    case DirectorWorkspaceSection.Paths when pathEdit is not null: RmlPath(form, visual); break;
                    case DirectorWorkspaceSection.Follows when followEdit is not null: RmlFollow(form, visual); break;
                    case DirectorWorkspaceSection.Replay: RmlReplay(form, visual); break;
                    default:
                        form.Note(RmlLabel(section switch
                        {
                            DirectorWorkspaceSection.Paths => "workspace-select-path",
                            DirectorWorkspaceSection.Follows => "workspace-select-follow",
                            _ => "workspace-select-camera"
                        }));
                        break;
                }
                var panel = layout.Panel("inspector");
                (visual ? rmlViewWindow : rmlInspectorWindow)?.Render(RmlLabel(visual ? "replay-right-view" : "replay-right-inspector"), form, (int)panel.Width, (int)panel.Height);
            }
            UpdateInspectorBounds();
            rmlInspectorWindow.Document?.GetElementById("content")?.SetScrollOffset(0, scrollY);
            rmlInspectorScrollKey = scrollKey;
        }
        finally { composingRmlInspector = false; }
    }

    private IEnumerable<(string Key, string Label)> InspectorSections(bool visual)
    {
        if (!visual) return new[] { ("general", RmlLabel("inspector-section-general")), ("details", RmlLabel("inspector-section-details")) };
        if (section == DirectorWorkspaceSection.Replay)
            return ReplayVisualGroupDefinitions.Select(g => (g.Code, RmlLabel("replay-visual-group-" + g.Code)));
        string[] keys = section switch
        {
            DirectorWorkspaceSection.Cameras => ["pose", "lens", "display"],
            DirectorWorkspaceSection.Paths => ["playback", "keyframe"],
            DirectorWorkspaceSection.Follows => ["target", "pose", "motion", "lens"],
            _ => ["general"]
        };
        return keys.Select(key => (key, RmlLabel("inspector-section-" + key)));
    }

    private void InspectorSection(DirectorImGuiForm form, string key)
    {
        string label = InspectorSections(false).Concat(InspectorSections(true)).FirstOrDefault(s => s.Key == key).Label
            ?? RmlLabel("inspector-section-" + key);
        string stateKey = section + ":" + key;
        form.Accordion(label, !rmlInspectorCollapsed.Contains(stateKey),
            expanded => { if (expanded) rmlInspectorCollapsed.Remove(stateKey); else rmlInspectorCollapsed.Add(stateKey); });
    }

    private void RmlCamera(DirectorImGuiForm form, bool visual)
    {
        var window = visual ? rmlViewWindow : rmlInspectorWindow;
        var camera = cameraEdit!;
        void Save()
        {
            owner.PreviewCameraEdit(camera);
            window?.Status(RmlLabel("auto-save-pending"));
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
            form.Text(RmlLabel("name"), camera.Name, v => { camera.Name = v; Save(); });
            form.Text(RmlLabel("tags"), string.Join(", ", camera.Tags), v => { camera.Tags = ParseTags(v); Save(); });
            InspectorSection(form, "details");
            form.Text(RmlLabel("description"), camera.Description, v => { camera.Description = v; Save(); });
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
            form.Slider(RmlLabel("roll"), camera.Roll, -180, 180, 1, v => { camera.Roll = (float)v; Save(); });
            InspectorSection(form, "lens");
            form.Slider(RmlLabel("fov"), camera.Fov, 10, 170, 1, v => { camera.Fov = (float)v; Save(); });
            InspectorSection(form, "display");
            form.Number(RmlLabel("camera-dimension"), camera.Dimension, v => { camera.Dimension = (int)Math.Clamp(v, int.MinValue, int.MaxValue); Save(); });
            RmlQuickSlot(form, camera.QuickSlot, v => { camera.QuickSlot = v; Save(); });
            form.Check(RmlLabel("camera-model-show"), camera.PreviewVisible, v => { camera.PreviewVisible = v; Save(); });
        }
        form.EndSection();
        form.FooterButton(RmlLabel("recapture"), () => { RecaptureCameraEditor(); ComposeRmlInspector(); });
        form.FooterButton(RmlLabel("activate"), () => ActivateCameraEditor());
        form.FooterButton(DeleteLabel(camera.Id), () => DeleteCameraEditor(), "danger");
    }

    private void RmlPath(DirectorImGuiForm form, bool visual)
    {
        var window = visual ? rmlViewWindow : rmlInspectorWindow;
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
            form.Text(RmlLabel("name"), path.Name, v => Edit(() => path.Name = v));
            form.Text(RmlLabel("tags"), string.Join(", ", path.Tags), v => Edit(() => path.Tags = ParseTags(v)));
            InspectorSection(form, "details");
            form.Text(RmlLabel("description"), path.Description, v => Edit(() => path.Description = v));
        }
        else
        {
            InspectorSection(form, "playback");
            form.Enum(RmlLabel("interpolation"), path.Interpolation, v => Edit(() => path.Interpolation = v),
                v => RmlLabel("interpolation-" + v.ToString().ToLowerInvariant()));
            form.Enum(RmlLabel("loop-mode"), path.LoopMode, v => Edit(() => path.LoopMode = v),
                v => RmlLabel("loop-" + v.ToString().ToLowerInvariant()));
            form.NumberStep(RmlLabel("path-speed"), path.PlaybackSpeed, .1f, v => Edit(() => path.PlaybackSpeed = v));
            form.Check(RmlLabel("path-preview"), path.PreviewVisible, v => Edit(() => path.PreviewVisible = v));
            InspectorSection(form, "keyframe");
            if (SelectedPathFrame() is { } frame)
            {
                form.Note(RmlLabel("path-camera-keyframe-help"));
                form.Text(RmlLabel("name"), frame.Name, v => Edit(() => frame.Name = v));
                var displayed = owner.DisplayPosition(frame.X, frame.Y, frame.Z);
                double dx = frame.X - displayed.Item1, dy = frame.Y - displayed.Item2, dz = frame.Z - displayed.Item3;
                form.Number("X", displayed.Item1, v => Edit(() => frame.X = v + dx));
                form.Number("Y", displayed.Item2, v => Edit(() => frame.Y = v + dy));
                form.Number("Z", displayed.Item3, v => Edit(() => frame.Z = v + dz));
                form.Number("Yaw", frame.Yaw, v => Edit(() => frame.Yaw = (float)v));
                form.Number("Pitch", frame.Pitch, v => Edit(() => frame.Pitch = (float)v));
                form.Check(RmlLabel("bezier-custom-handles"), frame.BezierHandlesEnabled, v => { Edit(() => frame.BezierHandlesEnabled = v); ComposeRmlInspector(); });
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
            else form.Note(RmlLabel("inspector-select-keyframe"));
            if (CanManageChunkPreload) form.FooterButton(RmlLabel("inspector-section-preload"), () => OpenPathPreloadEditor());
        }
    }

    private void RmlFollow(DirectorImGuiForm form, bool visual)
    {
        var window = visual ? rmlViewWindow : rmlInspectorWindow;
        var rig = followEdit!;
        void Save() => window?.QueueCommit(() => { if (owner.TrySaveFollowRig(rig, out string message, showNotification: false)) owner.TryPreviewFollowRig(rig); window?.Status(message); });
        form.Note(rig.Name);
        if (!visual)
        {
            InspectorSection(form, "general");
            form.Text(RmlLabel("name"), rig.Name, v => { rig.Name = v; Save(); });
            form.Text(RmlLabel("tags"), string.Join(", ", rig.Tags), v => { rig.Tags = ParseTags(v); Save(); });
            InspectorSection(form, "details");
            form.Text(RmlLabel("description"), rig.Description, v => { rig.Description = v; Save(); });
        }
        else
        {
            InspectorSection(form, "target");
            var targets = owner.GetFollowTargets();
            form.Select(RmlLabel("follow-target"), FollowTargetSelector(rig), targets.Select(t => (t.Selector, t.DisplayName)), v =>
            {
                if (owner.FindFollowTarget(v) is { } target)
                {
                    rig.TargetType = target.TargetType; rig.TargetPlayerUid = target.PlayerUid;
                    rig.TargetEntityId = target.EntityId; rig.TargetDisplayName = target.DisplayName; Save();
                }
            });
            form.Enum(RmlLabel("follow-view-mode"), rig.ViewMode, v => { rig.ViewMode = v; Save(); }, v => RmlLabel(v == CameraFollowViewMode.FirstPerson ? "follow-view-first" : "follow-view-third"));
            form.Enum(RmlLabel("follow-anchor"), rig.Anchor, v => { rig.Anchor = v; Save(); }, v => RmlLabel("follow-anchor-" + v.ToString().ToLowerInvariant()));
            form.Check(RmlLabel("follow-clear-view"), rig.OpenClearView, v => { rig.OpenClearView = v; Save(); });
            InspectorSection(form, "pose");
            form.Number(RmlLabel("follow-offset-x"), rig.OffsetX, v => { rig.OffsetX = v; Save(); });
            form.Number(RmlLabel("follow-offset-y"), rig.OffsetY, v => { rig.OffsetY = v; Save(); });
            form.Number(RmlLabel("follow-offset-z"), rig.OffsetZ, v => { rig.OffsetZ = v; Save(); });
            form.Number(RmlLabel("follow-distance"), rig.Distance, v => { rig.Distance = v; Save(); });
            form.Number(RmlLabel("follow-height"), rig.Height, v => { rig.Height = v; Save(); });
            form.Number(RmlLabel("follow-orbit-angle"), rig.OrbitAngleDegrees, v => { rig.OrbitAngleDegrees = v; Save(); });
            InspectorSection(form, "motion");
            form.Enum(RmlLabel("follow-motion-mode"), rig.MotionMode, v => { rig.MotionMode = v; Save(); }, v => RmlLabel("follow-motion-" + v.ToString().ToLowerInvariant()));
            form.Number(RmlLabel("follow-orbit-speed"), rig.OrbitSpeedDegreesPerSecond, v => { rig.OrbitSpeedDegreesPerSecond = v; Save(); });
            form.NumberStep(RmlLabel("follow-smooth-time"), rig.SmoothTimeSeconds, .01f, v => { rig.SmoothTimeSeconds = v; Save(); });
            form.NumberStep(RmlLabel("follow-delay"), rig.DelaySeconds, .05f, v => { rig.DelaySeconds = v; Save(); });
            InspectorSection(form, "lens");
            form.Slider(RmlLabel("follow-fov"), rig.Fov, 10, 170, 1, v => { rig.Fov = (float)v; Save(); });
            RmlQuickSlot(form, rig.QuickSlot, v => { rig.QuickSlot = v; Save(); });
        }
        form.EndSection();
        form.FooterButton(RmlLabel("activate"), () => ActivateFollowEditor());
        form.FooterButton(DeleteLabel(rig.Id), () => DeleteFollowEditor(), "danger");
    }

    private void RmlQuickSlot(DirectorImGuiForm form, int? slot, Action<int?> changed)
        => form.Select(RmlLabel("quick-slot"), (slot ?? 0).ToString(CultureInfo.InvariantCulture),
            new[] { ("0", RmlLabel("none")) }.Concat(Enumerable.Range(1, 10).Select(i => (i.ToString(CultureInfo.InvariantCulture), owner.QuickSlotHotkeyLabel(i)))),
            v => changed(int.TryParse(v, out int next) && next > 0 ? next : null));

    private void RmlReplay(DirectorImGuiForm form, bool visual)
    {
        var window = visual ? rmlViewWindow : rmlInspectorWindow;
        if (!visual)
        {
            if (SelectedReplayInfo() is not { } info) { form.Note(RmlLabel("workspace-select-replay")); return; }
            string name = info.Name, tags = string.Join(", ", info.Tags), description = info.Description;
            void Save()
            {
                replayMetadataName = name; replayMetadataTags = tags; replayMetadataDescription = description;
                window?.QueueCommit(() => { owner.TryUpdateReplayMetadata(info.Id, name, tags, description, out string message); window?.Status(message); });
            }
            InspectorSection(form, "general");
            form.Text(RmlLabel("name"), name, v => { name = v; Save(); });
            form.Text(RmlLabel("tags"), tags, v => { tags = v; Save(); });
            InspectorSection(form, "details");
            form.Text(RmlLabel("description"), description, v => { description = v; Save(); });
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
                    form.Select(label, value!.ToString()!, Enum.GetNames(type).Select(v => (v, RmlLabel("replay-visual-option-" + ToKebabCase(v)))), v => Set(v));
                else if (type == typeof(string)) form.Text(label, (string?)value ?? "", v => Set(v));
                else form.Number(label, Convert.ToDouble(value, CultureInfo.InvariantCulture), v => Set(v));
            }
        }
        form.EndSection();
        if (CanManageChunkPreload) form.FooterButton(RmlLabel("inspector-section-preload"), () => { preloadEditorExpanded = true; ComposeWorkflowCanvas(); });
    }

    internal void CloseFloatingTool() => CloseFloatingEditorWindow();
}
