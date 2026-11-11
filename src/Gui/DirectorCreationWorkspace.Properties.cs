using static VSDirector.Gui.DirectorCreationText;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private bool exportTextures = true;
    private string faceSide = "north", textureSlot = "all";
    private static bool Button(string label)
    {
        float natural = ImGui.CalcTextSize(label, true).X + 2 * ImGui.GetStyle().FramePadding.X;
        float width = Math.Max(1, Math.Min(natural, ImGui.GetContentRegionAvail().X));
        bool clicked = ImGui.Button(label, new(width, 0));
        if (width < natural && ImGui.IsItemHovered()) ImGui.SetTooltip(label);
        return clicked;
    }
    private static bool Header(string label)
    {
        float width = Math.Max(1, ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - ImGui.GetStyle().FramePadding.X * 2);
        string display = label;
        if (ImGui.CalcTextSize(display).X > width)
        {
            int length = display.Length;
            while (length > 0 && ImGui.CalcTextSize(display[..length] + "…").X > width) length--;
            display = display[..length] + "…";
        }
        ImGui.PushID(label);
        bool open = ImGui.CollapsingHeader(display + "###header");
        if (display != label && ImGui.IsItemHovered()) ImGui.SetTooltip(label);
        ImGui.PopID();
        return open;
    }
    private static void SameLineIfFits(string label, bool checkbox = false)
    {
        float width = ImGui.CalcTextSize(label, true).X + (checkbox ? ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X : 2 * ImGui.GetStyle().FramePadding.X);
        float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= right) ImGui.SameLine();
    }
    private static void Hint(string text)
    { ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]); ImGui.TextWrapped(text); ImGui.PopStyleColor(); }
    private static bool KeyButton(string id = "key") => DirectorImGuiIcons.Button(DirectorImGuiIcons.Get("square-rotated-filled") + "##" + id, new(ImGui.GetFrameHeight()));
    private static bool Combo(string label, string preview)
    { ImGui.TextWrapped(label); ImGui.SetNextItemWidth(-1); return ImGui.BeginCombo("##" + label, preview); }
    private void Widget(string label, bool changed, Action apply, bool finished = false)
    {
        if (changed) Session.BeginGesture();
        if (changed) { apply(); Session.Changed(); }
        if (finished || ImGui.IsItemDeactivated()) Run(() => Session.EndGesture(label));
    }
    private void TextField(string label, string value, Action<string> set)
    {
        ImGui.PushID(label); ImGui.TextWrapped(label); ImGui.SetNextItemWidth(-1);
        bool changed = ImGui.InputText("##value", ref value, 2048);
        Widget(label, changed, () => set(value)); ImGui.PopID();
    }
    private void Number(string label, double value, Action<double> set, double min = -1e8, double max = 1e8, float step = .1f)
    {
        ImGui.PushID(label);
        bool inline = ImGui.CalcTextSize(label).X + ImGui.GetStyle().ItemSpacing.X + 85 * DirectorImGuiTheme.Scale <= ImGui.GetContentRegionAvail().X;
        if (inline) ImGui.AlignTextToFramePadding();
        ImGui.TextWrapped(label);
        if (inline) ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        bool changed = DirectorImGuiNumeric.Double("##value", ref value, step, min, max);
        Widget(label, changed, () => set(value)); ImGui.PopID();
    }
    private void Toggle(string label, bool value, Action<bool> set)
    { if (Checkbox(label, ref value)) Session.Edit(label, _ => set(value)); }
    private static bool Checkbox(string label, ref bool value)
    {
        ImGui.PushID(label);
        bool changed = ImGui.Checkbox("##value", ref value);
        ImGui.SameLine(); ImGui.TextWrapped(label);
        if (ImGui.IsItemClicked()) { value = !value; changed = true; }
        ImGui.PopID(); return changed;
    }
    private void Choice<T>(string label, T value, Action<T> set, string[] names) where T : struct, Enum
    {
        int selected = Convert.ToInt32(value); ImGui.PushID(label); ImGui.TextWrapped(label); ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##choice", ref selected, names, names.Length)) Session.Edit(label, _ => set((T)Enum.ToObject(typeof(T), selected)));
        ImGui.PopID();
    }
    private void VectorField(string label, CreationVector value, Action<CreationVector> set, float step = .1f, Action? key = null, bool inline = false)
    {
        ImGui.PushID(label);
        if (inline)
        {
            DrawTransformRow(label, value, set, step, key);
            ImGui.PopID();
            return;
        }
        ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(label);
        if (key is not null && !Session.PoseEditing) { ImGui.SameLine(); if (KeyButton()) Run(key); if (ImGui.IsItemHovered()) ImGui.SetTooltip(L("creation-add-keyframe-at-current-time")); }
        float spacing = Math.Max(4, ImGui.GetStyle().ItemSpacing.X);
        float width = Math.Max(18, (ImGui.GetContentRegionAvail().X - 2 * spacing) / 3);
        double[] v = [value.X, value.Y, value.Z]; string[] axes = ["X", "Y", "Z"];
        uint[] colors = [0xff5c5ce8, 0xff77cb65, 0xffe8a35c];
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine(0, spacing);
            ImGui.SetNextItemWidth(width);
            bool changed = DirectorImGuiNumeric.TransformDouble("##" + axes[i], ref v[i], step, colors[i], axes[i], out bool finished);
            Widget(label, changed, () => set(new(v[0], v[1], v[2])), finished);
        }
        ImGui.PopID();
    }
    private void DrawTransformRow(string label, CreationVector value, Action<CreationVector> set, float step, Action? key)
    {
        bool showKey = key is not null && !Session.PoseEditing;
        float available = ImGui.GetContentRegionAvail().X;
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(3, 3) * DirectorImGuiTheme.Scale);
        if (ImGui.BeginTable("axes", showKey ? 5 : 4, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoPadOuterX | ImGuiTableFlags.NoSavedSettings))
        {
            float labelWidth = new[] { "creation-position", "creation-world-position", "creation-rotation", "creation-scale", "creation-pivot" }
                .Max(name => ImGui.CalcTextSize(L(name)).X);
            labelWidth = Math.Min(labelWidth, Math.Max(1, available * .25f));
            ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, labelWidth);
            for (int i = 0; i < 3; i++) ImGui.TableSetupColumn("XYZ"[i].ToString(), ImGuiTableColumnFlags.WidthStretch, 1);
            if (showKey) ImGui.TableSetupColumn("key", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            string display = label;
            float textWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
            while (display.Length > 0 && ImGui.CalcTextSize(display).X > textWidth)
            {
                string prefix = display.EndsWith('…') ? display[..^1] : display;
                display = prefix.Length > 1 ? prefix[..^1] + "…" : "";
            }
            ImGui.TextUnformatted(display);
            if (display != label && ImGui.IsItemHovered()) ImGui.SetTooltip(label);
            double[] v = [value.X, value.Y, value.Z];
            uint[] colors = [0xff5c5ce8, 0xff77cb65, 0xffe8a35c];
            for (int i = 0; i < 3; i++)
            {
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X));
                bool changed = DirectorImGuiNumeric.TransformDouble("##" + "XYZ"[i], ref v[i], step, colors[i], "XYZ"[i].ToString(), out bool finished);
                Widget(label, changed, () => set(new(v[0], v[1], v[2])), finished);
            }
            if (showKey)
            {
                ImGui.TableNextColumn();
                if (KeyButton()) Run(key!);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(L("creation-add-keyframe-at-current-time"));
            }
            ImGui.EndTable();
        }
        ImGui.PopStyleVar();
    }
    private void DrawProperties()
    {
        if (Project is null) return;
        if (modelContext.Length > 0) { Hint(L("creation-shared-model")); DrawModelProperties(); return; }
        DrawObjectProperties();
    }
    private IReadOnlyList<string>? exportIssues;
    private long exportCheckRevision = -1, exportCheckGeneration = -1;
    private void DrawBackgroundSettings()
    {
        if (Project is not { } project) return;
        ImGui.SeparatorText(L("creation-background-source"));
        ImGui.TextWrapped(Runtime.BackgroundStatus);
        ImGui.BeginDisabled(owner.ActiveReplayId == Guid.Empty);
        if (Button(L("creation-link-current-replay-as-background"))) Run(Runtime.LinkCurrentBackground);
        ImGui.EndDisabled();
        if (project.BackgroundReplayId != Guid.Empty)
        {
            if (Button(L("creation-load-background"))) Run(Runtime.LoadLinkedBackground);
            if (project.BackgroundClipId == Guid.Empty)
                TimeNumber(L("creation-background-replay-offset"), project.BackgroundOffset, v => project.BackgroundOffset = v, -1e8, 1e8);
            else Hint(L("creation-background-clip-timing") + project.Clips.Find(c => c.Id == project.BackgroundClipId)?.Name);
        }
        if (Button(L("creation-use-current-world"))) Run(Runtime.UseCurrentWorld);
    }
    private void DrawProjectSettings(bool output)
    {
        if (Project is not { } project) return;
        DrawBackgroundSettings();
        if (!output)
        {
                TextField(L("creation-project-name"), project.Name, v => project.Name = v);
                TextField(L("creation-category-folder"), project.Folder, v => project.Folder = v);
                TimeNumber(L("creation-duration"), project.Duration, v => { project.Duration = v; project.LoopStart = Math.Min(project.LoopStart, v - .001); project.LoopEnd = Math.Min(project.LoopEnd, v); Session.Seek(Session.Time); }, DirectorCreationTiming.FrameDuration, 86400);
                VectorField(L("creation-scene-origin"), project.Origin, v => project.Origin = v);
                TimeNumber(L("creation-loop-start"), project.LoopStart, v => project.LoopStart = v, 0, project.LoopEnd - .001);
                TimeNumber(L("creation-loop-end"), project.LoopEnd, v => project.LoopEnd = v, project.LoopStart + .001, project.Duration);
            return;
        }
                ImGui.SeparatorText(L("creation-video-output"));
                Number(L("creation-width"), project.Output.Width, v => project.Output.Width = (int)v / 2 * 2, 16, 8192, 2);
                Number(L("creation-height"), project.Output.Height, v => project.Output.Height = (int)v / 2 * 2, 16, 8192, 2);
                Number(L("creation-frame-rate"), project.Output.Fps, v => project.Output.Fps = (int)v, 1, 240, 1);
                Number(L("creation-quality-crf"), project.Output.Quality, v => project.Output.Quality = (int)v, 0, 51, 1);
                TextField(L("creation-encoder"), project.Output.Encoder, v => project.Output.Encoder = v);
                TextField(L("creation-output-directory"), project.Output.Directory, v => project.Output.Directory = v);
                ImGui.BeginDisabled(project.BackgroundReplayId == Guid.Empty);
                Toggle(L("creation-mix-background-replay-audio"), project.Output.SceneAudio, v => project.Output.SceneAudio = v);
                ImGui.EndDisabled();
                if (project.BackgroundReplayId == Guid.Empty) Hint(L("creation-world-audio-note"));
                ImGui.SeparatorText(L("creation-export-dependencies"));
                Hint(L("creation-export-dependencies-note"));
                if (Button(L("creation-check-resources"))) Run(() =>
                {
                    exportIssues = Runtime.CheckExportResources();
                    exportCheckRevision = Session.Revision; exportCheckGeneration = Session.DocumentGeneration;
                });
                if (exportCheckRevision != Session.Revision || exportCheckGeneration != Session.DocumentGeneration) exportIssues = null;
                if (exportIssues is { Count: 0 }) Hint(L("creation-export-resources-ready"));
                else if (exportIssues is not null) foreach (string issue in exportIssues) ImGui.TextWrapped(issue);
                Checkbox(L("creation-include-textures-in-model-export"), ref exportTextures);
                if (Button(L("creation-save-project"))) Continue(Session.Save);
                if (Button(L("creation-screenshot-145"))) FileDialog(L("creation-save-screenshot"), Runtime.Screenshot, "frame.png");
                if (Button(L("creation-export-video-146"))) FileDialog(L("creation-export-video"), ExportProject, "film.mp4");
    }
    private CreationTransform EvaluatedTransform(CreationSceneObject obj)
    {
        if (Session.Frame?.Objects.TryGetValue(obj.Id, out var state) == true)
            return Session.SelectedPart == Guid.Empty ? state.Transform : state.Pose.GetValueOrDefault(Session.SelectedPart) ?? new();
        return Session.SelectedPart == Guid.Empty ? obj.Transform : obj.Pose.GetValueOrDefault(Session.SelectedPart) ?? new();
    }
    private List<CreationTrack> EditTracks => SelectedAnimation?.Tracks ?? Project!.Tracks;
    private double EditTime => Session.EditTime;
    private void AddKey(CreationChannel channel, CreationVector value)
    {
        if (SelectedObject is not { } obj) return;
        Session.Edit(L("creation-add") + ChannelName(channel) + L("creation-keyframe"), _ =>
        {
            keyId = Session.WriteKey(obj.Id, Session.SelectedPart, channel, value).Id;
            trackId = EditTracks.Find(t => t.Keys.Any(k => k.Id == keyId))!.Id;
        });
    }
    private void SetTransform(CreationSceneObject obj, CreationTransform transform)
    {
        if(Session.SelectedPart!=Guid.Empty && selectedBones.Count>1 && Session.Frame?.Objects.GetValueOrDefault(obj.Id) is {} state)
        {
            var original=state.Pose.GetValueOrDefault(Session.SelectedPart)??new();
            foreach(var part in selectedBones.Where(id=>id!=Session.SelectedPart))
            {
                var value=state.Pose.GetValueOrDefault(part)??new();
                Session.SetTransform(obj.Id,part,value with {Position=value.Position+(transform.Position-original.Position),Rotation=value.Rotation+(transform.Rotation-original.Rotation),Scale=value.Scale+(transform.Scale-original.Scale),Pivot=value.Pivot+(transform.Pivot-original.Pivot)});
            }
        }
        Session.SetTransform(obj.Id, Session.SelectedPart, transform);
    }
    private void DrawRigTransform(CreationSceneObject obj, Guid part, bool positionOnly = false)
    {
        var project = Project!;
        void Transform(CreationTransform value) { if(part!=Guid.Empty && part==Session.SelectedPart)SetTransform(obj,value);else Session.SetTransform(obj.Id,part,value); }
        void Key(CreationChannel channel, CreationVector value) => Session.Edit(L("creation-add-keyframe-at-current-time"), _ => Session.WriteKey(obj.Id, part, channel, value));
        // Edit the evaluated pose, with an explicit preview draft until the user inserts keys.
        var tr = (!Session.AnimationEditing ? (part==Guid.Empty?obj.Transform:obj.Pose.GetValueOrDefault(part)??new()) : Session.Frame?.Objects.GetValueOrDefault(obj.Id) is { } state ? (part == Guid.Empty ? state.Transform : state.Pose.GetValueOrDefault(part) ?? new()) : (part == Guid.Empty ? obj.Transform : obj.Pose.GetValueOrDefault(part) ?? new()));
        bool worldPosition = part == Guid.Empty && obj.ParentId == Guid.Empty;
        var origin = worldPosition ? project.Origin : default;
        bool connected=Session.PoseEditing && part!=Guid.Empty && !freePoseTransform;
        ImGui.BeginDisabled(connected);
        VectorField(L(worldPosition ? "creation-world-position" : "creation-position"), tr.Position + origin, v => Transform(tr with { Position = v - origin }), .1f, () => Key(CreationChannel.Position, tr.Position), inline: true);
        ImGui.EndDisabled();
        if (!positionOnly)
        {
        VectorField(L("creation-rotation"), tr.Rotation, v => Transform(tr with { Rotation = v }), 1, () => Key(CreationChannel.Rotation, tr.Rotation), inline: true);
        ImGui.BeginDisabled(connected);
        VectorField(L("creation-scale"), tr.Scale, v => Transform(tr with { Scale = v }), .01f, () => Key(CreationChannel.Scale, tr.Scale), inline: true);
        VectorField(L("creation-pivot"), tr.Pivot, v => Transform(tr with { Pivot = v }), .1f, () => Key(CreationChannel.Pivot, tr.Pivot), inline: true);
        ImGui.EndDisabled();
        }
        Hint(L("creation-drag-to-adjust-ctrl-fine-shift-faster"));
        if (Session.HasTransformDraft && !Session.PoseEditing)
        {
            ImGui.TextWrapped(L("creation-unrecorded"));
            if (Button(L("creation-record-preview"))) Run(Session.CommitTransformDrafts);
            SameLineIfFits(L("creation-revert-preview")); if (Button(L("creation-revert-preview"))) Session.ClearTransformDrafts();
        }
        if (!Session.PoseEditing && Button(L("creation-add-transform-keys-from-preview-pose")))
        {
            Key(CreationChannel.Position, tr.Position);
            if (!positionOnly) { Key(CreationChannel.Rotation, tr.Rotation); Key(CreationChannel.Scale, tr.Scale); }
        }
    }
    private void DrawObjectProperties()
    {
        if (Project is not { } project) return;
        if ((SelectedObject ?? RigActor) is not { } obj) { Hint(L("creation-no-selection")); if (Button(L("creation-project-settings"))) auxiliary = "settings"; return; }
        DrawEditingContext();
        TextField(L("creation-name"), obj.Name, v => obj.Name = v);
        Hint(L("creation-object-transform"));
        ImGui.BeginDisabled(Session.PoseEditing);
        DrawRigTransform(obj, Guid.Empty);
        ImGui.EndDisabled();
        var evaluated = Session.Frame?.Objects.GetValueOrDefault(obj.Id);
        bool visible = Session.AnimationEditing ? evaluated?.Visible ?? obj.Visible : obj.Visible;
        Toggle(L("creation-visible"), visible, v => Session.SetProperty(obj.Id, Guid.Empty, CreationChannel.Visible, new(v ? 1 : 0, 0, 0), () => obj.Visible = v));
        if (obj.Kind == CreationObjectKind.Model)
        {
            DrawActorRig(obj);
            if (Button(L("creation-edit-selected-model"))) Continue(() => { Session.LeaveAnimation(); Session.SelectedObject = obj.Id; Session.SelectedPart = Guid.Empty; modelId = obj.ModelId; modelContext = "model"; });
            if (Button(L("creation-apply-preset"))) Continue(() => { Session.SelectedObject = obj.Id; Session.SelectedPart = Guid.Empty; auxiliary = "presets"; });
        }
        if (Header(L("creation-appearance")))
        {
        Vector3 color = (Session.AnimationEditing ? evaluated?.Color ?? obj.Color : obj.Color).Float();
        ImGui.TextUnformatted(L("creation-color")); ImGui.SetNextItemWidth(-1);
        bool changed = ImGui.ColorEdit3("##object-color", ref color); Widget(L("creation-color"), changed, () => Session.SetProperty(obj.Id, Guid.Empty, CreationChannel.Color, CreationVector.From(color), () => obj.Color = CreationVector.From(color)));
        if (ImGui.SmallButton(L("creation-color-keyframe"))) Session.Edit(L("creation-color-keyframe"), _ => Session.WriteKey(obj.Id, Guid.Empty, CreationChannel.Color, CreationVector.From(color)));
        Number(L("creation-opacity"), obj.Opacity, v => obj.Opacity = v, 0, 1, .01f); Number(L("creation-glow"), obj.Glow, v => obj.Glow = v, 0, 1, .01f);
        }
        if (Header(L("creation-group-target-binding")))
        {
            if (Combo(L("creation-parent-object"), project.Objects.Find(o => o.Id == obj.ParentId)?.Name ?? L("creation-world")))
            {
                if (ImGui.Selectable(L("creation-world"), obj.ParentId == Guid.Empty)) Session.Edit(L("creation-remove-parent"), _ => { obj.ParentId = obj.ParentPartId = Guid.Empty; });
                foreach (var other in project.Objects.Where(o => o.Id != obj.Id))
                    if (ImGui.Selectable(other.Name + "##" + other.Id, other.Id == obj.ParentId)) Run(() => Session.Edit(L("creation-set-parent"), p => { obj.ParentId = other.Id; obj.ParentPartId = Guid.Empty; p.Validate(); }));
                ImGui.EndCombo();
            }
            var parent = project.Objects.Find(o => o.Id == obj.ParentId);
            var parentModel = project.Models.Find(m => m.Id == parent?.ModelId);
            if (parentModel is not null && Combo(L("creation-attachment-bone"), parentModel.Parts.Find(p => p.Id == obj.ParentPartId)?.Name ?? L("creation-object-origin")))
            {
                if (ImGui.Selectable(L("creation-object-origin"))) Session.Edit(L("creation-change-attachment"), _ => obj.ParentPartId = Guid.Empty);
                foreach (var part in parentModel.Parts) if (ImGui.Selectable(part.Name + "##" + part.Id)) Session.Edit(L("creation-change-attachment"), _ => obj.ParentPartId = part.Id);
                ImGui.EndCombo();
            }
            if (obj.Kind==CreationObjectKind.Camera && obj.CameraMode is CreationCameraMode.Follow or CreationCameraMode.LookAt or CreationCameraMode.Orbit && Combo(L("creation-target"), project.Objects.Find(o => o.Id == obj.TargetId)?.Name ?? L("creation-none")))
            {
                if (ImGui.Selectable(L("creation-none"))) Session.Edit(L("creation-clear-target"), _ => obj.TargetId = Guid.Empty);
                foreach (var other in project.Objects.Where(o => o.Id != obj.Id)) if (ImGui.Selectable(other.Name + "##" + other.Id)) Session.Edit(L("creation-set-target"), _ => obj.TargetId = other.Id);
                ImGui.EndCombo();
            }
        }
        if (obj.Kind == CreationObjectKind.Camera)
        {
            ImGui.SeparatorText(L("creation-camera"));
            if (Button(project.CameraId == obj.Id ? L("creation-current-shooting-camera") : L("creation-use-as-shooting-camera"))) Session.Edit(L("creation-select-shooting-camera"), p => p.CameraId = obj.Id);
            if (Button(L("creation-set-from-observer-view"))) Runtime.CaptureCamera(obj);
            if (Button(L("creation-open-camera-preview"))) Runtime.Preview = true;
            Choice(L("creation-camera-mode"), obj.CameraMode, v => obj.CameraMode = v, [L("creation-keyframes-fixed"), L("creation-linear-motion"), L("creation-path"), L("creation-follow"), L("creation-look-at"), L("creation-orbit")]);
            double fov = Session.AnimationEditing ? evaluated?.Fov ?? obj.Fov : obj.Fov;
            Number(L("creation-field-of-view"), fov, v => Session.SetProperty(obj.Id, Guid.Empty, CreationChannel.Fov, new(v, 0, 0), () => obj.Fov = v), 1, 170, 1);
            if (ImGui.SmallButton(L("creation-fov-keyframe"))) AddKey(CreationChannel.Fov, new(fov, 0, 0));
            if(obj.CameraMode==CreationCameraMode.Orbit) { Number(L("creation-orbit-radius"), obj.OrbitRadius, v => obj.OrbitRadius = v, .01, 10000);
            Number(L("creation-orbit-speed"), obj.OrbitSpeed, v => obj.OrbitSpeed = v, -360, 360, 1); }
            Number(L("creation-camera-shake"), obj.Shake, v => obj.Shake = v, 0, 30, .1f);
            if (obj.CameraMode is CreationCameraMode.Linear or CreationCameraMode.Path) ImGui.TextWrapped(L("creation-use-position-keys-for-path-points-choose-linear-interpolation-for-stra"));
        }
        if (obj.Kind is CreationObjectKind.Text or CreationObjectKind.Image)
        {
            if (obj.Kind == CreationObjectKind.Text) TextField(L("creation-text"), obj.Text, v => obj.Text = v);
            VectorField(L("creation-display-size"), obj.ImageSize, v => obj.ImageSize = v);
            Toggle(L("creation-face-observer"), obj.Billboard, v => obj.Billboard = v);
            for (int i = 0; i < 4; i++) { int n = i; Number(new[] { L("creation-crop-left"), L("creation-crop-top"), L("creation-crop-right"), L("creation-crop-bottom") }[i], obj.Crop[i], v => obj.Crop[n] = v, 0, 1, .01f); }
        }
        if (obj.Kind == CreationObjectKind.Particle)
        {
            static string ParticleName(string code) => code switch { "smoke" => L("creation-smoke"), "snow" => L("creation-snow"), "glow" => L("creation-glow-particles"), _ => L("creation-sparks") };
            if (Combo(L("creation-particle-preset"), ParticleName(obj.ResourceCode)))
            { foreach (string preset in new[] { "smoke", "sparks", "snow", "glow" }) if (ImGui.Selectable(ParticleName(preset))) Session.Edit(L("creation-select-particles"), _ => obj.ResourceCode = preset); ImGui.EndCombo(); }
            Toggle(L("creation-use-game-particles-during-playback"), obj.NativeParticles, v => obj.NativeParticles = v);
            Hint(L("creation-seeking-and-frame-export-use-repeatable-particle-previews-motion-may-d"));
            TimeNumber(L("creation-start-time"), obj.ParticleStart, v => obj.ParticleStart = v, 0, project.Duration);
            TimeNumber(L("creation-emission-duration"), obj.ParticleDuration, v => obj.ParticleDuration = v, DirectorCreationTiming.FrameDuration, 86400);
            Number(L("creation-particles-per-second"), obj.ParticleCount, v => obj.ParticleCount = (int)v, 1, 1000, 1);
            Number(L("creation-particle-lifetime"), obj.ParticleLife, v => obj.ParticleLife = v, .05, 30);
        }
        if (obj.Kind == CreationObjectKind.Item)
        {
            TextField(L("creation-item-block-code"), obj.ResourceCode, v => obj.ResourceCode = v.Trim());
            if (obj.ResourceCode.Length > 0)
            {
                try { Runtime.ValidateItem(obj.ResourceCode); ImGui.TextColored(new System.Numerics.Vector4(.35f, .85f, .45f, 1), L("creation-game-resource-found")); }
                catch { ImGui.TextColored(new System.Numerics.Vector4(.95f, .45f, .35f, 1), L("creation-item-or-block-not-found")); }
            }
            Hint(L("creation-use-game-item-or-block-codes-meshes-use-the-game-s-textures"));
        }
        if (obj.Kind == CreationObjectKind.Structure)
        {
            ImGui.TextWrapped(L("creation-structure-block-count", obj.Blocks.Count));
            if (Button(L("creation-capture-new-structure"))) Run(() => { structureFirst = Runtime.SelectedBlock(); structureCapture = true; });
            SameLineIfFits(L("creation-clear-structure"));
            if (Button(L("creation-clear-structure"))) Session.Edit(L("creation-clear-structure"), _ => obj.Blocks.Clear());
        }
        if (obj.Kind == CreationObjectKind.Model)
        {
            if (Button(L("creation-replace-local-player-appearance"))) Run(() => Runtime.ReplaceAppearance(obj));
            SameLineIfFits(L("creation-restore")); if (Button(L("creation-restore"))) Runtime.RestoreAppearance();
        }
    }
    private void AddPart(bool group)
    {
        if (CurrentModel is not { } model) return;
        var part = new CreationPart { Name = group ? L("creation-part-group") : L("creation-part-cube"), Group = group, ParentId = Session.SelectedPart, To = new(4, 4, 4) };
        Session.LeaveAnimation();
        Session.Edit(L("creation-add-part"), _ => model.Parts.Add(part)); Session.SelectedPart = part.Id; modelId = model.Id; modelContext = "model";
    }
    private void DrawModelProperties()
    {
        if (CurrentModel is not { } model) { ImGui.TextWrapped(L("creation-select-a-model-actor-or-import-a-shape-model-from-the-toolbar")); return; }
        TextField(L("creation-model-name"), model.Name, v => model.Name = v); TextField(L("creation-resource-category"), model.Folder, v => model.Folder = v);
        Number(L("creation-texture-width"), model.TextureWidth, v => model.TextureWidth = (int)v, 1, 8192, 1);
        Number(L("creation-texture-height"), model.TextureHeight, v => model.TextureHeight = (int)v, 1, 8192, 1);
        if (Button(L("creation-add-cube"))) AddPart(false); SameLineIfFits(L("creation-add-group")); if (Button(L("creation-add-group"))) AddPart(true);
        if (Header(L("creation-model-textures")))
        {
            foreach (var entry in model.Textures.ToArray())
            {
                TextField(entry.Key, entry.Value, v => model.Textures[entry.Key] = v);
                ImGui.PushID(entry.Key); if (ImGui.SmallButton(L("creation-choose-image"))) FileDialog(L("creation-assign-texture"), f => { var a = ImportAsset(f, CreationAssetKind.Texture); Session.Edit(L("creation-assign-texture"), _ => model.Textures[entry.Key] = a.Source); }); ImGui.PopID();
            }
            TextField(L("creation-new-texture-slot"), textureSlot, v => textureSlot = v);
            if (Button(L("creation-add-texture"))) FileDialog(L("creation-assign-texture"), f => { var a = ImportAsset(f, CreationAssetKind.Texture); Session.Edit(L("creation-assign-texture"), _ => model.Textures[textureSlot.TrimStart('#')] = a.Source); });
        }
        if (Header(L("creation-basic-animation-body-mapping")))
            foreach (string role in new[] { "head", "body", "leftarm", "rightarm", "leftleg", "rightleg" })
                if (Combo(L("creation-role-" + role), model.Parts.Find(p => p.Id == model.BodyMapping.GetValueOrDefault(role))?.Name ?? L("creation-unassigned")))
                {
                    if (ImGui.Selectable(L("creation-unassigned"))) Session.Edit(L("creation-body-mapping"), _ => model.BodyMapping.Remove(role));
                    foreach (var p in model.Parts) if (ImGui.Selectable(p.Name + "##" + p.Id)) Session.Edit(L("creation-body-mapping"), _ => model.BodyMapping[role] = p.Id);
                    ImGui.EndCombo();
                }
        if (SelectedPart is not { } part) { ImGui.TextWrapped(L("creation-select-a-bone-part-in-the-list-model-edits-affect-all-actors-using-the")); return; }
        ImGui.SeparatorText(L("creation-geometry-part")); TextField(L("creation-part-name"), part.Name, v => part.Name = v);
        VectorField(L("creation-from-model-units"), part.From, v => part.From = v, 1);
        VectorField(L("creation-to-model-units"), part.To, v => part.To = v, 1);
        VectorField(L("creation-rotation-origin"), part.Origin, v => part.Origin = v, 1);
        VectorField(L("creation-default-rotation"), part.Rotation, v => part.Rotation = v, 1);
        Number(L("creation-inflation"), part.Inflate, v => part.Inflate = v, -16, 128);
        Toggle(L("creation-part-visible-by-default"), part.Visible, v => part.Visible = v);
        if (Combo(L("creation-parent-part"), model.Parts.Find(p => p.Id == part.ParentId)?.Name ?? L("creation-root")))
        {
            if (ImGui.Selectable(L("creation-root"))) Session.Edit(L("creation-change-hierarchy"), _ => part.ParentId = Guid.Empty);
            foreach (var p in model.Parts.Where(p => p.Id != part.Id)) if (ImGui.Selectable(p.Name + "##" + p.Id)) Run(() => Session.Edit(L("creation-change-hierarchy"), doc => { part.ParentId = p.Id; doc.Validate(); }));
            ImGui.EndCombo();
        }
        if (!part.Group && Header(L("creation-face-uv-properties")))
        {
            if (Combo(L("creation-face"), L("creation-face-" + faceSide))) { foreach (string side in new[] { "north", "east", "south", "west", "up", "down" }) if (ImGui.Selectable(L("creation-face-" + side))) faceSide = side; ImGui.EndCombo(); }
            if (part.Faces[faceSide] is not JsonObject face) { if (Button(L("creation-enable-face"))) Session.Edit(L("creation-enable-face"), _ => part.Faces[faceSide] = CreationPart.DefaultFaces()[faceSide]!.DeepClone()); }
            else
            {
                TextField(L("creation-texture-slot"), DirectorCreationShape.S(face, "texture", "#all"), v => face["texture"] = v.StartsWith('#') ? v : "#" + v);
                var uv = face["uv"] as JsonArray ?? new JsonArray(0, 0, 4, 4);
                for (int i = 0; i < 4; i++) { int n = i; Number(new[] { "U1", "V1", "U2", "V2" }[i], DirectorCreationShape.Number(uv[i]), v => { uv[n] = v; if (!ReferenceEquals(face["uv"], uv)) face["uv"] = uv; }, -8192, 8192, 1); }
                Number(L("creation-uv-rotation"), DirectorCreationShape.N(face, "rotation"), v => face["rotation"] = Math.Round(v / 90) * 90, 0, 270, 90);
                if (Button(L("creation-mirror-horizontally"))) Session.Edit(L("creation-mirror-uv"), _ => { var a = uv[0]!.DeepClone(); var b = uv[2]!.DeepClone(); uv[0] = b; uv[2] = a; if (!ReferenceEquals(face["uv"], uv)) face["uv"] = uv; });
                Number(L("creation-glow-strength"), DirectorCreationShape.N(face, "glow"), v => face["glow"] = (int)v, 0, 255, 1);
                Number(L("creation-wind-effect-mode"), DirectorCreationShape.N(face, "windMode"), v => face["windMode"] = (int)v, 0, 15, 1);
            }
        }
        if (Header(L("creation-equipment-item-attachments")))
        {
            var points = part.Extra["attachmentpoints"] as JsonArray;
            if (points is not null) for (int i = 0; i < points.Count; i++)
            {
                if (points[i] is not JsonObject point) continue;
                ImGui.PushID(i); TextField(L("creation-attachment-name"), DirectorCreationShape.S(point, "code"), v => point["code"] = v);
                foreach (string axis in new[] { "posX", "posY", "posZ", "rotationX", "rotationY", "rotationZ" }) Number(L("creation-attachment-" + axis), DirectorCreationShape.N(point, axis), v => point[axis] = v);
                if (ImGui.SmallButton(L("creation-delete-attachment"))) { int n = i; Session.Edit(L("creation-delete-attachment"), _ => points.RemoveAt(n)); ImGui.PopID(); break; } ImGui.PopID();
            }
            if (Button(L("creation-add-attachment"))) Session.Edit(L("creation-add-attachment"), _ => { if (part.Extra["attachmentpoints"] is not JsonArray a) part.Extra["attachmentpoints"] = a = new(); a.Add(new JsonObject { ["code"] = "mount", ["posX"] = 0, ["posY"] = 0, ["posZ"] = 0 }); });
        }
        if (Button(L("creation-delete-part"))) Session.Edit(L("creation-delete-part"), p => { DirectorCreationEdits.DeletePart(p, model, part.Id); Session.SelectedPart = Guid.Empty; });
    }
}
