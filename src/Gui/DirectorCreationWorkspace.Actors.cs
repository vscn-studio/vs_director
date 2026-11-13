using static VSDirector.Gui.DirectorCreationText;
using ImGuiNET;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private double replayImportStart, replayImportEnd = 5;
    private void DrawActorLibrary()
    {
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##actor-search", L("creation-search-names"), ref search, 256);
        if (ImGui.CollapsingHeader(L("creation-world-actors"), ImGuiTreeNodeFlags.DefaultOpen))
            foreach (var actor in Runtime.WorldActors().Where(a => search.Length == 0 || a.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).Take(100))
            {
                if (ImGui.Selectable(actor.Name + "##world-" + actor.Id))
                    Run(() => { Select(Runtime.AddNativeActor("", actor.Id)); auxiliary = ""; });
                if (ImGui.BeginPopupContextItem("world-record-" + actor.Id))
                { Item(L("creation-record-world-actor"), () => { Runtime.RecordWorldActor(actor.Id); auxiliary = ""; }); ImGui.EndPopup(); }
            }
        if (VSDirector.Replay.DirectorReplayRuntime.Manager is { IsPlaying: true } replay && ImGui.CollapsingHeader(L("creation-replay-actors"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            float from = (float)replayImportStart, to = (float)replayImportEnd;
            ImGui.SetNextItemWidth(-1); if (ImGui.DragFloat(L("creation-replay-start"), ref from, .1f, 0, (float)replay.PlaybackDurationSeconds)) replayImportStart = from;
            ImGui.SetNextItemWidth(-1); if (ImGui.DragFloat(L("creation-replay-end"), ref to, .1f, 0, (float)replay.PlaybackDurationSeconds)) replayImportEnd = to;
            foreach (var source in replay.ActorPathSources)
                if (ImGui.Selectable(source.DisplayName + "##replay-" + source.ActorKey)) Run(() =>
                {
                    if (!replay.TryBuildAction(source.ActorKey, replayImportStart, replayImportEnd, source.DisplayName, out var action, out _, out var error)) throw new InvalidOperationException(error);
                    var entity = replay.ResolveReplayActor(source.ActorKey, null, 0);
                    var actor = Runtime.AddNativeActor(source.EntityCode, entity?.EntityId ?? 0); Select(actor);
                    Runtime.ImportPerformance(action, actor.Id, Session.Time, owner.ActiveReplayId, backgroundStart: replayImportStart); auxiliary = "";
                });
        }
        ImGui.SeparatorText(L("creation-native-actors"));
        foreach (var actor in Runtime.ActorResources(search).Take(200))
            if (ImGui.Selectable(actor.Name + "##native-" + actor.Code))
                Run(() => { Select(Runtime.AddNativeActor(actor.Code)); auxiliary = ""; });
    }

    private Guid ikUpper, ikLower;
    private string boneSearch = "";
    private void DrawActorRig(CreationSceneObject obj)
    {
        if (Project?.Models.Find(m => m.Id == obj.ModelId) is not { } model) return;
        if (obj.ActorSource is not null && Header(L("creation-player-appearance-equipment")))
        {
            foreach (int hand in new[] { 1, 2 })
            {
                ImGui.PushID(hand);
                string anchor = hand == 1 ? "RightHand" : "LeftHand";
                if (model.Parts.Any(p => p.Name.Equals(anchor, StringComparison.OrdinalIgnoreCase)))
                    ActorEquipmentRow(L(hand == 1 ? "creation-right-hand-item" : "creation-left-hand-item"),
                        Runtime.ActorHandName(obj, hand), () => Runtime.PickActorItem(obj, hand), () => Runtime.SetActorHand(obj, hand, null));
                ImGui.PopID();
            }
            if (Runtime.ActorHasClothing(obj) && Header(L("creation-actor-clothing")))
            {
                foreach (var slot in Enum.GetValues<Vintagestory.API.Common.EnumCharacterDressType>().Where(t => (int)t >= 0).OrderBy(t => (int)t))
                {
                    ImGui.PushID("clothing-" + slot);
                    ActorEquipmentRow(L("creation-dress-" + slot.ToString().ToLowerInvariant()),
                        Runtime.ActorWearableName(obj, (int)slot), () => Runtime.PickActorItem(obj, clothingSlot: (int)slot),
                        () => Runtime.SetActorWearable(obj, (int)slot, null));
                    ImGui.PopID();
                }
            }
            if (Runtime.ActorSkinOptions(obj).Count > 0 && Header(L("creation-actor-skin"))
                && ImGui.BeginTable("actor-skin", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, 75 * DirectorImGuiTheme.Scale);
                ImGui.TableSetupColumn("variant", ImGuiTableColumnFlags.WidthStretch);
                foreach (var part in Runtime.ActorSkinOptions(obj).Where(p => !p.Hidden && p.Type != Vintagestory.GameContent.EnumSkinnableType.Voice))
                {
                    string current = Runtime.ActorSkinVariant(obj, part.Code);
                    string Variant(string code) => Vintagestory.API.Config.Lang.Get((part.Type == Vintagestory.GameContent.EnumSkinnableType.Texture ? "color-" : "skinpart-") + code);
                    string label = Vintagestory.API.Config.Lang.Get("skinpart-" + part.Code);
                    ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(EquipmentLabel(label, ImGui.GetContentRegionAvail().X));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
                    ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                    if (ImGui.BeginCombo("##skin-" + part.Code, current.Length == 0 ? L("creation-none") : Variant(current)))
                    {
                        foreach (var variant in part.Variants)
                            if (ImGui.Selectable(Variant(variant.Code) + "##" + variant.Code, current == variant.Code))
                                Continue(() => Runtime.SetActorSkin(obj, part.Code, variant.Code));
                        ImGui.EndCombo();
                    }
                }
                ImGui.EndTable();
            }
        }
        if (Header(L("creation-player-body")))
        {
            var body = obj.PlayerBody;
            Toggle(L("creation-player-body-enabled"), body.Enabled, v => body.Enabled = v);
            Number(L("creation-player-body-width"), body.Width, v => body.Width = v, .1, 4, .01f);
            Number(L("creation-player-body-height"), body.Height, v =>
            { body.Height = v; body.SneakHeight = Math.Min(body.SneakHeight, v); body.EyeHeight = Math.Min(body.EyeHeight, v); }, .2, 8, .01f);
            Number(L("creation-player-body-sneak"), body.SneakHeight, v => body.SneakHeight = v, .1, body.Height, .01f);
            Number(L("creation-player-body-eye"), body.EyeHeight, v => body.EyeHeight = v, .05, body.Height, .01f);
            ImGui.TextWrapped(L("creation-player-body-help"));
            if (Button(L("creation-player-body-apply"))) Run(() => Runtime.ApplyPlayerBody(body));
            SameLineIfFits(L("creation-player-body-restore"));
            if (Button(L("creation-player-body-restore"))) Run(() => Runtime.ApplyPlayerBody(null));
        }
        if (Header(L("creation-recording-channels")))
        {
            foreach (var channel in new[] { CreationRecordingChannels.Position, CreationRecordingChannels.Rotation, CreationRecordingChannels.Pose, CreationRecordingChannels.Equipment })
                Toggle(L("creation-record-" + channel.ToString().ToLowerInvariant()), obj.RecordingChannels.HasFlag(channel), v =>
                    obj.RecordingChannels = v ? obj.RecordingChannels | channel : obj.RecordingChannels & ~channel);
            if (Button(L("creation-record-player-to-selected-actor"))) Continue(() => { Session.SelectedObject = obj.Id; Session.SelectedPart = Guid.Empty; Runtime.TogglePerformance(); });
        }
    }

    private void ActorEquipmentRow(string label, string name, Action pick, Action clear)
    {
        if (!ImGui.BeginTable("equipment", 2, ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, 75 * DirectorImGuiTheme.Scale);
        ImGui.TableSetupColumn("item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(EquipmentLabel(label, ImGui.GetContentRegionAvail().X));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
        ImGui.TableNextColumn();
        float side = ImGui.GetFrameHeight();
        float width = Math.Max(1, ImGui.GetContentRegionAvail().X - side - ImGui.GetStyle().ItemSpacing.X);
        string display = EquipmentLabel(name, width - ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button(display + "###pick", new(width, 0))) Continue(pick);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(name);
        ImGui.SameLine();
        if (DirectorImGuiIcons.Button(DirectorImGuiIcons.Get("x") + "##clear", new(side, side))) Continue(clear);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(L("creation-clear-item"));
        ImGui.EndTable();
    }
    private static string EquipmentLabel(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        int count = text.Length;
        while (count > 0 && ImGui.CalcTextSize(text[..count] + "…").X > width) count--;
        return text[..count] + "…";
    }

    private void CreateIk(CreationSceneObject obj, CreationModel model)
    {
        var upper = model.Parts.Find(p => p.Id == ikUpper); var lower = model.Parts.Find(p => p.Id == ikLower && p.ParentId == ikUpper);
        if (upper is null || lower is null || Session.Frame is not { } frame) throw new InvalidOperationException(L("creation-ik-invalid"));
        if (obj.IkChains.Any(c => c.Upper == upper.Id || c.Lower == lower.Id)) throw new InvalidOperationException(L("creation-ik-invalid"));
        var controls = DirectorCreationIk.CreateControls(obj, model, frame.Objects[obj.Id], upper, lower);
        controls.Target.Name = lower.Name + " · " + L("creation-ik-target");
        controls.Pole.Name = lower.Name + " · " + L("creation-ik-pole");
        Session.Edit(L("creation-ik-create"), p => { p.Objects.Add(controls.Target); p.Objects.Add(controls.Pole); obj.IkChains.Add(controls.Chain); });
    }

    private void BakeIk(CreationSceneObject obj)
    {
        var project = Project!;
        var bones = obj.IkChains.Where(c => c.Enabled).SelectMany(c => new[] { c.Upper, c.Lower }).Distinct().ToArray();
        var editing = Session.EditingAnimation;
        double start = editing is not null ? 0 : project.Loop ? project.LoopStart : 0, end = editing?.Duration ?? (project.Loop ? project.LoopEnd : project.Duration);
        if (bones.Length == 0 || end <= start) throw new InvalidOperationException(L("creation-ik-invalid"));
        var animation = new CreationAnimation { Name = (editing?.Name ?? obj.Name) + " · IK", ModelId = obj.ModelId, Duration = end - start, Loop = editing?.Loop ?? false };
        if (editing is not null) animation.Tracks.AddRange(editing.Tracks.Where(t => t.ObjectId == Guid.Empty && t.Channel != CreationChannel.IkWeight
            && (!bones.Contains(t.PartId) || t.Channel is not (CreationChannel.Position or CreationChannel.Rotation or CreationChannel.Scale))).Select(t => t.Copy()));
        void Key(Guid bone, CreationChannel channel, double time, CreationVector value)
        {
            var track = animation.Tracks.Find(t => t.PartId == bone && t.Channel == channel);
            if (track is null) { track = new() { PartId = bone, Channel = channel }; animation.Tracks.Add(track); }
            DirectorCreationEdits.SetKey(track, time - start, value);
        }
        if ((end - start) * DirectorCreationTiming.FramesPerSecond > 250000) throw new InvalidOperationException(L("creation-ik-bake-limit"));
        for (int i = 0; i <= Math.Ceiling((end - start) * DirectorCreationTiming.FramesPerSecond); i++)
        {
            double time = Math.Min(end, start + i / (double)DirectorCreationTiming.FramesPerSecond);
            var state = DirectorCreationEvaluator.Evaluate(project, editing is null ? time : Session.Time, editingAction: editing, actionActor: obj.Id, actionTime: time).Objects[obj.Id];
            foreach (var bone in bones) if (state.Pose.TryGetValue(bone, out var pose))
            {
                Key(bone, CreationChannel.Rotation, time, pose.Rotation);
                Key(bone, CreationChannel.Position, time, pose.Position);
                Key(bone, CreationChannel.Scale, time, pose.Scale);
            }
        }
        Key(Guid.Empty, CreationChannel.IkWeight, start, default);
        Session.Edit(L("creation-ik-bake"), p =>
        {
            p.Animations.Add(animation);
            if (editing is null) p.Clips.Add(new() { Name = animation.Name, ObjectId = obj.Id, AnimationId = animation.Id, Start = start, Duration = end - start, BakedPose = true });
        });
        if (editing is not null) Session.EnterAnimation(animation.Id, obj.Id);
    }
}
