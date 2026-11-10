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
        if (ImGui.CollapsingHeader(L("creation-world-actors")))
            foreach (var actor in Runtime.WorldActors().Where(a => search.Length == 0 || a.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).Take(100))
            {
                if (ImGui.Selectable(actor.Name + "##world-" + actor.Id))
                    Run(() => { Select(Runtime.AddNativeActor("", actor.Id)); auxiliary = ""; });
                if (ImGui.BeginPopupContextItem("world-record-" + actor.Id))
                { Item(L("creation-record-world-actor"), () => { Runtime.RecordWorldActor(actor.Id); auxiliary = ""; }); ImGui.EndPopup(); }
            }
        if (VSDirector.Replay.DirectorReplayRuntime.Manager is { IsPlaying: true } replay && ImGui.CollapsingHeader(L("creation-replay-actors")))
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
    private void DrawActorRig(CreationSceneObject obj)
    {
        if (CurrentModel is not { } model) return;
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
            if (Button(L("creation-player-body-restore"))) Run(() => Runtime.ApplyPlayerBody(null));
        }
        if (Header(L("creation-recording-channels")))
        {
            foreach (var channel in new[] { CreationRecordingChannels.Position, CreationRecordingChannels.Rotation, CreationRecordingChannels.Pose, CreationRecordingChannels.Equipment })
                Toggle(L("creation-record-" + channel.ToString().ToLowerInvariant()), obj.RecordingChannels.HasFlag(channel), v =>
                    obj.RecordingChannels = v ? obj.RecordingChannels | channel : obj.RecordingChannels & ~channel);
            if (Button(L("creation-record-player-to-selected-actor"))) Continue(Runtime.TogglePerformance);
        }
        if (Header(L("creation-native-actions")))
            foreach (var animation in Project!.Animations.Where(a => a.ModelId == model.Id))
            {
                ImGui.PushID(animation.Id.ToString());
                if (ImGui.Selectable(animation.Name)) Continue(() => AddAnimationClip(animation));
                if (ImGui.BeginPopupContextItem()) { Item(L("creation-edit"), () => EditAnimation(animation)); ImGui.EndPopup(); }
                ImGui.PopID();
            }
        if (!Header(L("creation-ik"))) return;
        if (Button(L("creation-ik-detect"))) Run(() =>
        {
            int added = 0;
            foreach (var pair in DirectorCreationIk.SuggestChains(model))
            {
                if (obj.IkChains.Any(c => c.Upper == pair.Upper.Id)) continue;
                ikUpper = pair.Upper.Id; ikLower = pair.Lower.Id; CreateIk(obj, model); added++;
            }
            if (added == 0) Runtime.SetStatus(L("creation-ik-no-segments"));
        });
        double weight = Session.Frame?.Objects.GetValueOrDefault(obj.Id)?.IkWeight ?? obj.IkWeight;
        Number(L("creation-ik-weight"), weight, v => Session.SetProperty(obj.Id, Guid.Empty, CreationChannel.IkWeight, new(v, 0, 0), () => obj.IkWeight = v), 0, 1, .01f);
        if (Button(L("creation-ik-weight-key"))) Session.Edit(L("creation-ik-weight-key"), _ => Session.WriteKey(obj.Id, Guid.Empty, CreationChannel.IkWeight, new(weight, 0, 0)));
        void Bone(string title, ref Guid selection, Func<CreationPart,bool> filter)
        {
            Guid selected = selection;
            if (!Combo(title, model.Parts.Find(p => p.Id == selected)?.Name ?? L("creation-none"))) return;
            foreach (var part in model.Parts.Where(filter)) if (ImGui.Selectable(part.Name + "##" + part.Id)) selection = part.Id;
            ImGui.EndCombo();
        }
        Bone(L("creation-ik-upper"), ref ikUpper, _ => true);
        Bone(L("creation-ik-lower"), ref ikLower, p => p.ParentId == ikUpper);
        if (Button(L("creation-ik-create"))) Run(() => CreateIk(obj, model));
        foreach (var chain in obj.IkChains.ToArray())
        {
            ImGui.PushID(chain.Id.ToString());
            Toggle(chain.Name, chain.Enabled, v => chain.Enabled = v);
            VectorField(L("creation-ik-end"), chain.EndOffset, v => chain.EndOffset = v, .01f);
            if (Button(L("creation-ik-target"))) Select(Project!.Objects.First(o => o.Id == chain.Target));
            SameLineIfFits(L("creation-ik-pole"));
            if (Button(L("creation-ik-pole"))) Select(Project!.Objects.First(o => o.Id == chain.Pole));
            if (Button(L("creation-delete-selected-object"))) Session.Edit(L("creation-delete-selected-object"), _ => obj.IkChains.Remove(chain));
            ImGui.PopID();
        }
        if (obj.IkChains.Count > 0 && Button(L("creation-ik-bake"))) Run(() => BakeIk(obj));
    }

    private void CreateIk(CreationSceneObject obj, CreationModel model)
    {
        var upper = model.Parts.Find(p => p.Id == ikUpper); var lower = model.Parts.Find(p => p.Id == ikLower && p.ParentId == ikUpper);
        if (upper is null || lower is null || Session.Frame is not { } frame) throw new InvalidOperationException(L("creation-ik-invalid"));
        if (obj.IkChains.Any(c => c.Upper == upper.Id || c.Lower == lower.Id)) throw new InvalidOperationException(L("creation-ik-invalid"));
        var state = frame.Objects[obj.Id];
        var offset = ((lower.To - lower.From) * (1d / 16)).Float();
        int axis = Math.Abs(offset.X) > Math.Abs(offset.Y) ? 0 : 1; if (Math.Abs(offset.Z) > Math.Abs(axis == 0 ? offset.X : offset.Y)) axis = 2;
        var pivot = DirectorCreationIk.Pivot(lower);
        var end = pivot; end[axis] = Math.Abs(pivot[axis]) < Math.Abs(offset[axis] - pivot[axis]) ? offset[axis] : 0;
        if (System.Numerics.Vector3.DistanceSquared(System.Numerics.Vector3.Transform(DirectorCreationIk.Pivot(upper), state.Bones[upper.Id]), System.Numerics.Vector3.Transform(pivot, state.Bones[lower.Id])) < 1e-10f
            || System.Numerics.Vector3.DistanceSquared(end, pivot) < 1e-10f) throw new InvalidOperationException(L("creation-ik-invalid"));
        var target = new CreationSceneObject { Kind = CreationObjectKind.Group, Name = lower.Name + " · " + L("creation-ik-target"), Transform = new() { Position = CreationVector.From(System.Numerics.Vector3.Transform(end, state.Bones[lower.Id] * state.World)) } };
        var pole = new CreationSceneObject { Kind = CreationObjectKind.Group, Name = lower.Name + " · " + L("creation-ik-pole"), Transform = new() { Position = CreationVector.From(System.Numerics.Vector3.Transform(pivot, state.Bones[lower.Id] * state.World)) + new CreationVector(0, 0, 1) } };
        Session.Edit(L("creation-ik-create"), p => { p.Objects.Add(target); p.Objects.Add(pole); obj.IkChains.Add(new() { Name = upper.Name, Upper = upper.Id, Lower = lower.Id, Target = target.Id, Pole = pole.Id, EndOffset = CreationVector.From(end) }); });
    }

    private void BakeIk(CreationSceneObject obj)
    {
        var project = Project!;
        var bones = obj.IkChains.Where(c => c.Enabled).SelectMany(c => new[] { c.Upper, c.Lower }).Distinct().ToArray();
        double start = project.Loop ? project.LoopStart : 0, end = project.Loop ? project.LoopEnd : project.Duration;
        var animation = new CreationAnimation { Name = obj.Name + " · IK", ModelId = obj.ModelId, Duration = end - start };
        void Key(Guid bone, CreationChannel channel, double time, CreationVector value)
        {
            var track = animation.Tracks.Find(t => t.PartId == bone && t.Channel == channel);
            if (track is null) { track = new() { PartId = bone, Channel = channel }; animation.Tracks.Add(track); }
            DirectorCreationEdits.SetKey(track, time - start, value);
        }
        if ((end - start) * project.Output.Fps > 250000) throw new InvalidOperationException(L("creation-ik-bake-limit"));
        for (int i = 0; i <= Math.Ceiling((end - start) * project.Output.Fps); i++)
        {
            double time = Math.Min(end, start + i / (double)project.Output.Fps);
            var state = DirectorCreationEvaluator.Evaluate(project, time).Objects[obj.Id];
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
            p.Clips.Add(new() { Name = animation.Name, ObjectId = obj.Id, AnimationId = animation.Id, Start = start, Duration = end - start, BakedPose = true });
        });
    }
}
