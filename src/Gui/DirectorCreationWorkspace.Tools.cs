using static VSDirector.Gui.DirectorCreationText;
using ImGuiNET;
using ImGuizmoNET;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private enum CreationTool { Select, Move, Rotate, Scale, Pull }
    private bool toolsPanel = true, wholeObjectSelection, selectionTool;
    private CreationTool objectTool = CreationTool.Move, partTool = CreationTool.Rotate;
    private MODE objectSpace = MODE.WORLD, partSpace = MODE.LOCAL;
    private int objectRotationAxis = -1;
    private bool extendedPullChain;
    private long rigTargetsRevision=-1;
    private DirectorCreationProject? rigTargetsProject;
    private readonly Dictionary<Guid,IReadOnlyDictionary<Guid,Guid>> rigTargets=new();
    private Guid PoseTarget(CreationModel model,Guid part)
        => PoseTargets(model).GetValueOrDefault(part,part);
    private IReadOnlyDictionary<Guid,Guid> PoseTargets(CreationModel model)
    {
        if(rigTargetsRevision!=Session.Revision || !ReferenceEquals(rigTargetsProject,Project))
        {rigTargets.Clear();rigTargetsRevision=Session.Revision;rigTargetsProject=Project;}
        if(!rigTargets.TryGetValue(model.Id,out var targets))rigTargets[model.Id]=targets=DirectorCreationRigSelection.Targets(Project!,model);
        return targets;
    }
    private CreationObjectState? pullStart;
    private IReadOnlyList<CreationPart>? pullChain;
    private void BeginPull(CreationModel model,CreationObjectState state,Guid part)
    {
        pullChain=exactPoseParts?DirectorCreationPoseGeometry.Chain(model,part):DirectorCreationRigSelection.Chain(Project!,model,part,extendedPullChain?8:4);
        pullStart=new(){Source=state.Source,World=state.World};
        foreach(var pair in state.Pose)pullStart.Pose[pair.Key]=pair.Value with{};
        DirectorCreationEvaluator.EvaluateBones(model,pullStart);
    }
    private CreationTool ActiveTool => selectionTool ? CreationTool.Select : posePull ? CreationTool.Pull
        : operation == OPERATION.ROTATE ? CreationTool.Rotate : operation == OPERATION.SCALE ? CreationTool.Scale : CreationTool.Move;
    private bool PartScope => PosePicking && Session.SelectedPart != Guid.Empty;
    private bool ToolAvailable(CreationTool tool) => SelectedObject is not null && modelContext.Length == 0
        && (tool != CreationTool.Pull || PartScope && Session.PoseEditing)
        && (tool is not (CreationTool.Move or CreationTool.Scale) || !PartScope || !Session.PoseEditing || freePoseTransform);
    private void ChooseTool(CreationTool tool)
    {
        if(!ToolAvailable(tool))return;
        selectionTool=tool==CreationTool.Select;posePull=tool==CreationTool.Pull;
        operation=tool==CreationTool.Rotate?OPERATION.ROTATE:tool==CreationTool.Scale?OPERATION.SCALE:OPERATION.TRANSLATE;
        if(PartScope)partTool=tool;else objectTool=tool;
    }
    private void RestoreTool(bool part)
    {
        transformMode=part?partSpace:objectSpace;
        var tool=part?partTool:objectTool;
        if(!ToolAvailable(tool))tool=part?CreationTool.Rotate:CreationTool.Move;
        ChooseTool(tool);
    }
    private void EnterBodySelection(CreationSceneObject actor, Guid part = default) => Continue(() =>
    {
        if(actor.ModelId==Guid.Empty)return;
        if(Session.EditingAnimation is null || Session.AnimationObject!=actor.Id)Session.BeginScenePose(actor.Id);
        wholeObjectSelection=false;Session.SelectedObject=actor.Id;selectedObjects.Clear();selectedObjects.Add(actor.Id);
        selectedBones.Clear();Session.SelectedPart=Guid.Empty;modelContext="";showBones=false;
        if(part==Guid.Empty && Project!.Models.Find(m=>m.Id==actor.ModelId) is {} model)
            part=model.BodyMapping.GetValueOrDefault("body",model.Parts.FirstOrDefault()?.Id??Guid.Empty);
        if(part!=Guid.Empty)SelectBone(actor.Id,part);
        RestoreTool(true);RevealTimelineSelection(actor.Id,part);
    });
    private void LeaveBodySelection() => Continue(() =>
    {
        if(Session.ScenePoseEditing)Session.EndScenePose();
        wholeObjectSelection=true;Session.SelectedPart=Guid.Empty;selectedBones.Clear();
        selectionArea=SelectionArea.Objects;ClearTimelineSelection();RestoreTool(false);
    });
    // Reused by the tools window and timeline controls. Groups wrap instead of hiding controls.
    private static bool InlineChoice(string label, bool active, ref bool first)
    {
        if(!first)SameLineIfFits(label);first=false;
        using var style=DirectorImGuiTheme.Button(active?"active":"normal");
        return Button(label);
    }
    private void DrawTools()
    {
        bool first=true;
        bool Choice(string label,bool active)
        {
            if(!first)SameLineIfFits(label);first=false;
            using var style=DirectorImGuiTheme.Button(active?"active":"normal");
            return Button(label);
        }
        if(Choice(L("creation-object-scope"),!PosePicking))LeaveBodySelection();
        ImGui.BeginDisabled(RigActor is null);
        if(Choice(L("creation-part-scope"),PosePicking)&&RigActor is {} actor)EnterBodySelection(actor);
        ImGui.EndDisabled();
        float side=ImGui.GetFrameHeight();
        float right=ImGui.GetCursorScreenPos().X+ImGui.GetContentRegionAvail().X;
        foreach(var entry in new[]{(CreationTool.Select,"cursor","creation-select-tool"),(CreationTool.Move,"arrows-move","creation-move"),
            (CreationTool.Rotate,"rotate","creation-rotation"),(CreationTool.Scale,"arrows-maximize","creation-scale"),(CreationTool.Pull,"hand-move","creation-pull")})
        {
            if(ImGui.GetItemRectMax().X+ImGui.GetStyle().ItemSpacing.X+side<=right)ImGui.SameLine();
            bool enabled=ToolAvailable(entry.Item1);ImGui.BeginDisabled(!enabled);
            using(DirectorImGuiTheme.Button(ActiveTool==entry.Item1?"active":"normal"))
                if(DirectorImGuiIcons.Button(DirectorImGuiIcons.Get(entry.Item2)+"##tool-"+entry.Item1,new(side,side)))ChooseTool(entry.Item1);
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))ImGui.SetTooltip(L(enabled?entry.Item3:SelectedObject is null?"creation-no-selection":"creation-tool-unavailable"));
            ImGui.EndDisabled();
        }
        if(ActiveTool==CreationTool.Rotate)
        {
            int axis=PartScope?poseRotationAxis:objectRotationAxis;
            foreach(int next in new[]{6,0,1,2,-1})
                if(Choice(next==6?L("creation-screen-axis"):next<0?L("creation-all-axes"):"XYZ"[next].ToString(),axis==next))
                {if(PartScope)poseRotationAxis=next;else objectRotationAxis=next;}
        }
        bool screen=ActiveTool==CreationTool.Rotate&&(PartScope?poseRotationAxis:objectRotationAxis)==6;
        ImGui.BeginDisabled(screen);
        if(ActiveTool is not CreationTool.Select && Choice(L(transformMode==MODE.LOCAL?"creation-local-axes":"creation-world-axes"),false))
        {
            transformMode=transformMode==MODE.LOCAL?MODE.WORLD:MODE.LOCAL;
            if(PartScope)partSpace=transformMode;else objectSpace=transformMode;
        }
        if(ActiveTool is not CreationTool.Select && ImGui.IsItemHovered())ImGui.SetTooltip(L("creation-axis-space-help"));
        ImGui.EndDisabled();
        host.MeasureCreationToolsHeight(ImGui.GetCursorPosY()+ImGui.GetStyle().WindowPadding.Y);
    }
    private void DrawActorActionPicker(CreationSceneObject actor)
    {
        var actions=Project!.Animations.Where(a=>a.ModelId==actor.ModelId).ToArray();
        if(actions.Length==0)return;
        ImGui.SeparatorText(L("creation-animation"));
        var selected=actions.FirstOrDefault(a=>a.Id==actionResource)??actions[0];
        if(Combo(L("creation-animation"),selected.Name))
        {foreach(var a in actions)if(ImGui.Selectable(a.Name+"##"+a.Id,a==selected))actionResource=a.Id;ImGui.EndCombo();}
        if(Button(L("creation-add-animation-clip")))Continue(()=>{if(SelectedAnimation is not null)ReturnToProject();Session.SelectedObject=actor.Id;AddAnimationClip(selected);});
        SameLineIfFits(L("creation-edit-action-source"));
        if(Button(L("creation-edit-action-source")))EditAnimation(selected);
    }
    private void FocusSelection()
    {
        if(Project is not {} project || Session.Frame is not {} frame || SelectedObject is null)return;
        var points=new List<System.Numerics.Vector3>();
        foreach(var state in frame.Objects.Values.Where(s=>PartScope?s.Source.Id==Session.SelectedObject:ObjectSelected(s.Source.Id)))
        {
            if(project.Models.Find(m=>m.Id==state.Source.ModelId) is {} model)
            {
                foreach(var part in model.Parts.Where(p=>!p.Group && p.Visible && !state.HiddenParts.Contains(p.Id)
                    && (!PartScope || p.Id==Session.SelectedPart || !exactPoseParts && PoseTarget(model,p.Id)==Session.SelectedPart)))
                {
                    if(!state.Bones.TryGetValue(part.Id,out var bone))continue;
                    var size=((part.To-part.From)*(1d/16)).Float();
                    for(int i=0;i<8;i++)points.Add(System.Numerics.Vector3.Transform(new((i&1)==0?0:size.X,(i&2)==0?0:size.Y,(i&4)==0?0:size.Z),bone*state.World));
                }
            }
            else points.Add(state.World.Translation);
        }
        if(points.Count==0)points.Add(frame.Objects[Session.SelectedObject].World.Translation);
        var min=points.Aggregate(System.Numerics.Vector3.Min);var max=points.Aggregate(System.Numerics.Vector3.Max);
        owner.FocusCreationView(project.Origin+CreationVector.From((min+max)*.5f),Math.Max(.2,(max-min).Length()*.5),project.Dimension);
    }
}
