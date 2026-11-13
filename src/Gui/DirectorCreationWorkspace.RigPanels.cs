using static VSDirector.Gui.DirectorCreationText;
using ImGuiNET;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private Guid selectedIkChain;
    private bool showAllBones;
    private string actionSearch = "";
    private CreationPoseSnapshot? copiedPose;
    private bool freePoseTransform;
    private void StartScenePose(CreationSceneObject actor) => EnterBodySelection(actor);
    private CreationSceneObject? RigActor => SelectedObject is { ModelId: var model } selected && model != Guid.Empty ? selected
        : Project?.Objects.FirstOrDefault(o => o.IkChains.Any(c => c.Target == Session.SelectedObject || c.Pole == Session.SelectedObject));
    private bool IsIkControl(Guid id) => Project?.Objects.Any(o => o.IkChains.Any(c => c.Target == id || c.Pole == id)) == true;
    private CreationIkChain? ActiveIkChain(CreationSceneObject actor) => actor.IkChains.FirstOrDefault(c => c.Target == Session.SelectedObject || c.Pole == Session.SelectedObject)
        ?? actor.IkChains.FirstOrDefault(c => c.Id == selectedIkChain);
    private bool RigContext(out CreationSceneObject actor, out CreationModel model)
    {
        actor = RigActor!; Guid modelId = actor?.ModelId ?? Guid.Empty;
        model = Project?.Models.Find(m => m.Id == modelId)!;
        if (actor is null || model is null) { Hint(L("creation-select-rig-actor")); return false; }
        DrawEditingContext(true);
        return true;
    }
    private void DrawRigDisplay()
    {
        Checkbox(L("creation-show-bones"), ref showBones);
        if (showBones) Checkbox(L("creation-show-all-bones"), ref showAllBones);
    }
    private void DrawBonesPanel()
    {
        if (!RigContext(out var actor, out var model)) return;
        if (Session.PoseEditing)
        {
            if (Session.HasPoseDraft)
            {
                Hint(L("creation-unrecorded"));
                if (Button(L("creation-record-preview"))) Run(Session.CommitTransformDrafts);
                SameLineIfFits(L("creation-revert-preview")); if (Button(L("creation-revert-preview"))) Session.ClearTransformDrafts();
            }
        }
        else
        {
            if(Session.EditingAnimation is null && Button(L("creation-scene-pose"))) StartScenePose(actor);
            if (Button(L("creation-new-animation"))) Ask(L("creation-animation-name"), NewAnimation);
            if (Session.EditingAnimation is not null && Button(L("creation-pose-edit-copy"))) Continue(() => CreatePoseCopy(actor));
        }
        if (ImGui.BeginChild("pose-body", new(0,0), false)) DrawPoseBody(actor,model);
        ImGui.EndChild();
    }
    private void DrawPoseBody(CreationSceneObject actor, CreationModel model)
    {
        if (!PosePicking) return;
        if (Session.PoseEditing)
        {
            if (Button(L("creation-copy-pose"))) copiedPose = Session.CapturePose();
            SameLineIfFits(L("creation-paste-pose")); ImGui.BeginDisabled(copiedPose is null || copiedPose.Bones.Keys.Any(id => model.Parts.All(p => p.Id != id)));
            if (Button(L("creation-paste-pose"))) Run(() => Session.ApplyPose(copiedPose!)); ImGui.EndDisabled();
            SameLineIfFits(L("creation-bone-rest"));
            if (Button(L("creation-bone-rest"))) Run(Session.RestoreRestPose);
        }
        Checkbox(L("creation-pose-exact-parts"), ref exactPoseParts);
        if (model.Parts.Find(p=>p.Id==Session.SelectedPart) is { ParentId: var parentId } && parentId!=Guid.Empty && Button(L("creation-select-parent-part"))) SelectBone(actor.Id,parentId);
        if (Header(L("creation-show-bones"))) DrawRigDisplay();
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##bone-search", L("creation-search-names"), ref boneSearch, 128);
        if (ImGui.BeginListBox("##bone-list", new(-1, Math.Min(180 * DirectorImGuiTheme.Scale, Math.Max(70, ImGui.GetContentRegionAvail().Y * .35f)))))
        {
            void Parts(Guid parent,int depth)
            {
                if(depth>128)return;
                foreach(var part in model.Parts.Where(p=>p.ParentId==parent))
                {
                    bool exposed=exactPoseParts||PoseTarget(model,part.Id)==part.Id;
                    bool matches=part.Name.Contains(boneSearch,StringComparison.OrdinalIgnoreCase)||BoneName(model,part).Contains(boneSearch,StringComparison.OrdinalIgnoreCase);
                    if(!exposed || boneSearch.Length>0&&!matches){Parts(part.Id,depth+1);continue;}
                    if(boneSearch.Length>0)
                    {if(ImGui.Selectable(BoneName(model,part)+"##"+part.Id,selectedBones.Contains(part.Id)||Session.SelectedPart==part.Id))SelectBone(actor.Id,part.Id);Parts(part.Id,depth+1);continue;}
                    bool children=model.Parts.Any(p=>p.ParentId==part.Id);
                    var flags=ImGuiTreeNodeFlags.OpenOnArrow|ImGuiTreeNodeFlags.SpanAvailWidth|(depth<2?ImGuiTreeNodeFlags.DefaultOpen:0)
                        |(children?0:ImGuiTreeNodeFlags.Leaf)|(selectedBones.Contains(part.Id)||Session.SelectedPart==part.Id?ImGuiTreeNodeFlags.Selected:0);
                    bool open=ImGui.TreeNodeEx(BoneName(model,part)+"##"+part.Id,flags);
                    if(ImGui.IsItemClicked()&&!ImGui.IsItemToggledOpen())SelectBone(actor.Id,part.Id);
                    if(open){Parts(part.Id,depth+1);ImGui.TreePop();}
                }
            }
            Parts(Guid.Empty,0);
            ImGui.EndListBox();
        }
        if (Session.SelectedObject != actor.Id || model.Parts.Find(p => p.Id == Session.SelectedPart) is not { } bone)
        { Hint(L("creation-select-bone-first")); return; }
        ImGui.SeparatorText(BoneName(model,bone));
        var chain = actor.IkChains.FirstOrDefault(c => c.Enabled && (c.Upper == bone.Id || c.Lower == bone.Id));
        if (chain is not null && !Session.PoseEditing)
        {
            Hint(L("creation-bone-ik-active"));
            if (Button(L("creation-ik-target"))) Select(Project!.Objects.First(o => o.Id == chain.Target));
            if (Button(L("creation-ik-disable-for-pose"))) Session.Edit(L("creation-ik-disable-for-pose"), _ => chain.Enabled = false);
        }
        DrawRigTransform(actor, bone.Id);
        if (Session.PoseEditing && Header(L("creation-ik-advanced")))
        {
            if(Checkbox(L("creation-free-pose-transform"),ref freePoseTransform) && !freePoseTransform) ChooseTool(CreationTool.Rotate);
            Checkbox(L("creation-extended-chain"),ref extendedPullChain);
            bool seam=model.RigEditing.Seams.Any(s=>s.Child==bone.Id);
            if(Checkbox(L("creation-joint-seam"),ref seam)) Run(()=>
            {
                if(!seam) Session.Edit(L("creation-joint-seam"),_=>model.RigEditing.Seams.RemoveAll(s=>s.Child==bone.Id));
                else
                {
                    var match=DirectorCreationJointSeams.Find(model,bone.Id)??throw new InvalidOperationException(L("creation-joint-seam-unmatched"));
                    Session.Edit(L("creation-joint-seam"),_=>model.RigEditing.Seams.Add(match));
                }
            });
            Hint(L("creation-joint-seam-help"));
            bool anchored = model.RigEditing.Anchors.Contains(bone.Id);
            if (Checkbox(L("creation-pose-anchor"), ref anchored)) Session.Edit(L("creation-pose-anchor"), _ => { if (anchored) model.RigEditing.Anchors.Add(bone.Id); else model.RigEditing.Anchors.Remove(bone.Id); });
            if (Combo(L("creation-ik-upper"), model.Parts.Find(p => p.Id == model.RigEditing.Roots.GetValueOrDefault(bone.Id))?.Name ?? L("creation-none")))
            {
                if (ImGui.Selectable(L("creation-none"))) Session.Edit(L("creation-ik-upper"), _ => model.RigEditing.Roots.Remove(bone.Id));
                var parent = bone;
                while (parent is not null) { Guid id = parent.Id; if (ImGui.Selectable(parent.Name + "##" + id)) Session.Edit(L("creation-ik-upper"), _ => model.RigEditing.Roots[bone.Id] = id); parent = model.Parts.Find(p => p.Id == parent.ParentId); }
                ImGui.EndCombo();
            }
        }
        bool visible = !(Session.Frame?.Objects.GetValueOrDefault(actor.Id)?.HiddenParts ?? actor.HiddenParts).Contains(bone.Id);
        if (Session.PoseEditing)
        {
            if (Checkbox(L("creation-visible"), ref visible))
            { Session.BeginGesture(); Session.SetProperty(actor.Id,bone.Id,CreationChannel.Visible,new(visible?1:0,0,0),()=>{}); Session.Changed(); Session.EndGesture(L("creation-visible")); }
        }
        else Toggle(L("creation-visible"), visible, v => Session.SetProperty(actor.Id, bone.Id, CreationChannel.Visible, new(v ? 1 : 0, 0, 0), () => { if (v) actor.HiddenParts.Remove(bone.Id); else actor.HiddenParts.Add(bone.Id); }));
        if (!Session.PoseEditing && Button(L("creation-bone-rest"))) Session.Edit(L("creation-bone-rest"), _ => Session.SetTransform(actor.Id, bone.Id, new()));
    }
    private void DrawIkPanel()
    {
        if (!RigContext(out var actor, out var model)) return;
        DrawRigDisplay();
        Hint(L("creation-ik-select-chain"));
        var active = ActiveIkChain(actor);
        if (Combo(L("creation-ik-chain"), active?.Name ?? L("creation-none")))
        {
            foreach (var chain in actor.IkChains)
                if (ImGui.Selectable(chain.Name + "##" + chain.Id, chain == active))
                { selectedIkChain = chain.Id; Select(Project!.Objects.First(o => o.Id == chain.Target)); }
            ImGui.EndCombo();
        }
        if (active is not null)
        {
            Toggle(L("creation-enabled-chain"), active.Enabled, v => active.Enabled = v);
            foreach (var control in new[] { (active.Target, "creation-ik-target"), (active.Pole, "creation-ik-pole") })
                if (ImGui.RadioButton(L(control.Item2), Session.SelectedObject == control.Item1)) Select(Project!.Objects.First(o => o.Id == control.Item1));
            if (Project!.Objects.Find(o => o.Id == Session.SelectedObject && (o.Id == active.Target || o.Id == active.Pole)) is { } selected)
            {
                bool auto = Session.AutoKey; if (Checkbox(L("creation-auto-key"), ref auto)) Continue(() => Session.AutoKey = auto);
                DrawRigTransform(selected, Guid.Empty, positionOnly: true);
            }
            if (Header(L("creation-ik-advanced")))
            {
                double weight = Session.Frame?.Objects.GetValueOrDefault(actor.Id)?.IkWeight ?? actor.IkWeight;
                Number(L("creation-ik-weight"), weight, v => Session.SetProperty(actor.Id, Guid.Empty, CreationChannel.IkWeight, new(v,0,0), () => actor.IkWeight = v), 0, 1, .01f);
                if (Button(L("creation-ik-weight-key"))) Session.Edit(L("creation-ik-weight-key"), _ => Session.WriteKey(actor.Id, Guid.Empty, CreationChannel.IkWeight, new(weight,0,0)));
                VectorField(L("creation-ik-end"), active.EndOffset, v => active.EndOffset = v, .01f);
                if (Button(L("creation-delete-ik-chain"))) Continue(() =>
                {
                    Session.SelectedObject = actor.Id; Session.SelectedPart = Guid.Empty;
                    Session.Edit(L("creation-delete-ik-chain"), p =>
                    {
                        actor.IkChains.Remove(active);
                        foreach (var id in new[] { active.Target, active.Pole })
                            if (!p.Objects.Any(o => o.IkChains.Any(c => c.Target == id || c.Pole == id))) DirectorCreationEdits.DeleteObject(p, id);
                    }); selectedIkChain = Guid.Empty;
                });
            }
            if (Button(L("creation-ik-bake"))) Continue(() => BakeIk(actor));
        }
        if (actor.IkChains.Count == 0) ImGui.SetNextItemOpen(true, ImGuiCond.Once);
        if (Header(L("creation-ik-add-chain")))
        {
            foreach (var pair in DirectorCreationIk.SuggestChains(model).Where(pair => !actor.IkChains.Any(c => c.Upper == pair.Upper.Id || c.Lower == pair.Lower.Id)))
                if (Button(pair.Upper.Name + " → " + pair.Lower.Name)) Run(() =>
                { ikUpper = pair.Upper.Id; ikLower = pair.Lower.Id; CreateIk(actor, model); selectedIkChain = actor.IkChains[^1].Id; Select(Project!.Objects.First(o => o.Id == actor.IkChains[^1].Target)); });
            if (Header(L("creation-ik-manual")))
            {
                void Bone(string label, ref Guid id, Func<CreationPart,bool> filter)
                {
                    Guid current = id;
                    if (!Combo(L(label), model.Parts.Find(p => p.Id == current)?.Name ?? L("creation-none"))) return;
                    foreach (var p in model.Parts.Where(filter)) if (ImGui.Selectable(p.Name + "##" + p.Id)) id = p.Id;
                    ImGui.EndCombo();
                }
                Bone("creation-ik-upper", ref ikUpper, _ => true); Bone("creation-ik-lower", ref ikLower, p => p.ParentId == ikUpper);
                if (Button(L("creation-ik-create"))) Run(() => { CreateIk(actor, model); selectedIkChain = actor.IkChains[^1].Id; Select(Project!.Objects.First(o => o.Id == actor.IkChains[^1].Target)); });
            }
        }
    }
    private void CreatePoseCopy(CreationSceneObject actor)
    {
        var source = Session.EditingAnimation!; var model = Project!.Models.First(m => m.Id == actor.ModelId);
        var copy = new CreationAnimation { Name = source.Name + " · " + L("creation-pose"), ModelId = model.Id, Duration = source.Duration, Loop = source.Loop, PoseEditing = true };
        foreach (var track in source.Tracks.Where(t => t.ObjectId == Guid.Empty && t.PartId == Guid.Empty && t.Channel != CreationChannel.IkWeight))
        { var copied = track.Copy(); copied.Id = Guid.NewGuid(); foreach (var key in copied.Keys) key.Id = Guid.NewGuid(); copy.Tracks.Add(copied); }
        int frames = (int)Math.Ceiling(source.Duration * DirectorCreationTiming.FramesPerSecond);
        if (frames > 250000 || (long)(frames + 1) * model.Parts.Count * 5 > 500000) throw new InvalidOperationException(L("creation-ik-bake-limit"));
        for (int i = 0; i <= frames; i++)
        {
            double time = Math.Min(source.Duration,i/(double)DirectorCreationTiming.FramesPerSecond);
            var state = DirectorCreationEvaluator.Evaluate(Project,Session.Time,editingAction:source,actionActor:actor.Id,actionTime:time).Objects[actor.Id];
            DirectorCreationSession.WritePose(copy,new() { Bones = model.Parts.ToDictionary(p=>p.Id,p=>(state.Pose.GetValueOrDefault(p.Id)??new()) with {}), Hidden = new(state.ExplicitHiddenParts) },time);
        }
        RememberSceneContext();
        Session.Edit(L("creation-pose-edit-copy"), p=>p.Animations.Add(copy)); Session.EnterAnimation(copy.Id,actor.Id);wholeObjectSelection=false;RestoreTimelineView();
        if(bonesPanel)focusRigPanel="bones";
    }
    private void DrawActionsPanel()
    {
        if (!RigContext(out var actor, out var model)) return;
        void ForActor(Action action) => Continue(() => { Session.SelectedObject = actor.Id; Session.SelectedPart = Guid.Empty; action(); });
        if (Button(L("creation-new-animation"))) ForActor(() => Ask(L("creation-animation-name"), NewAnimation));
        if (Session.EditingAnimation is { } editing)
        {
            ImGui.TextWrapped(L("creation-action-prefix") + editing.Name);
            if (Header(L("creation-action-settings")))
            {
                TextField(L("creation-animation-name"), editing.Name, v => editing.Name = v);
                TimeNumber(L("creation-animation-duration"), editing.Duration, v => editing.Duration = v, DirectorCreationTiming.FrameDuration, 86400);
                Toggle(L("creation-loop-animation"), editing.Loop, v => editing.Loop = v);
            }
            if (Button(L("creation-add-animation-clip"))) ForActor(() => { Session.LeaveAnimation(); Session.SelectedObject = actor.Id; AddAnimationClip(editing); });
            if (!editing.PoseEditing && Button(L("creation-pose-edit-copy"))) Continue(() => CreatePoseCopy(actor));
        }
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##action-search", L("creation-search-names"), ref actionSearch, 128);
        if (ImGui.BeginListBox("##actions", new(-1, Math.Max(80, ImGui.GetContentRegionAvail().Y))))
        {
            foreach (var action in Project!.Animations.Where(a => a.ModelId == model.Id && a.Name.Contains(actionSearch, StringComparison.OrdinalIgnoreCase)))
            {
                ImGui.PushID(action.Id.ToString());
                if (ImGui.Selectable(action.Name, actionResource==action.Id,ImGuiSelectableFlags.AllowDoubleClick)) { actionResource=action.Id;if(ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))ForActor(()=>EditAnimation(action)); }
                if (ImGui.BeginPopupContextItem()) { Item(L("creation-edit-action-source"),()=>ForActor(()=>EditAnimation(action))); Item(L("creation-add-animation-clip"), () => ForActor(() => { if(SelectedAnimation is not null)ReturnToProject(); Session.SelectedObject = actor.Id; AddAnimationClip(action); })); ImGui.EndPopup(); }
                ImGui.PopID();
            }
            ImGui.EndListBox();
        }
    }
}
