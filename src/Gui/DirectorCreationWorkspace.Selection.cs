using static VSDirector.Gui.DirectorCreationText;
using ImGuiNET;
using VSDirector.Data;
namespace VSDirector.Gui;
internal sealed partial class DirectorCreationWorkspace
{
    private readonly HashSet<Guid> selectedObjects = new();
    private bool exactPoseParts;
    private Guid selectionAnchor;
    private bool PosePicking => (Session.ScenePoseEditing || Session.EditingAnimation is not null) && modelContext.Length == 0;
    private bool ObjectSelected(Guid id) => id == Session.SelectedObject || selectedObjects.Contains(id);
    private void SyncSelection()
    {
        if(Session.ScenePoseEditing && Project!.Objects.All(o=>o.Id!=Session.ScenePoseActor)) Session.EndScenePose();
        if (scenePoseOwner != Guid.Empty && !Project!.Objects.Any(o=>o.Id==scenePoseOwner && o.PoseKeys.Any(k=>k.Id==scenePoseDrag))) scenePoseDrag = scenePoseOwner = Guid.Empty;
        selectedObjects.RemoveWhere(id => Project!.Objects.All(o => o.Id != id));
        selectedBones.RemoveWhere(id=>CurrentModel?.Parts.Any(p=>p.Id==id)!=true);
        if (Session.SelectedObject != Guid.Empty && !selectedObjects.Contains(Session.SelectedObject))
        { selectedObjects.Clear(); selectedObjects.Add(Session.SelectedObject); }
        if (SelectedObject is null) { Session.SelectedObject = Session.SelectedPart = Guid.Empty; selectedObjects.Clear(); }
        if (!PosePicking && modelContext.Length == 0) Session.SelectedPart = Guid.Empty;
    }
    private void SelectObjectClick(CreationSceneObject obj, bool context = false)
    {
        var io = ImGui.GetIO(); bool toggle = !context && io.KeyCtrl, range = !context && io.KeyShift;
        if (context && ObjectSelected(obj.Id)) return;
        var previous = selectedObjects.ToHashSet(); if (Session.SelectedObject != Guid.Empty) previous.Add(Session.SelectedObject);
        SelectWithinPose(obj.Id, () =>
        {
            Select(obj);
            if (!PosePicking && (toggle || range))
            {
                selectedObjects.UnionWith(previous);
                if (range)
                {
                    var list = VisibleObjectOrder(); int a = list.IndexOf(selectionAnchor), b = list.IndexOf(obj.Id);
                    if (a >= 0 && b >= 0) foreach (var id in list.Skip(Math.Min(a,b)).Take(Math.Abs(a-b)+1)) selectedObjects.Add(id);
                }
                else if (previous.Contains(obj.Id)) { selectedObjects.Remove(obj.Id); Session.SelectedObject = selectedObjects.FirstOrDefault(); }
            }
            if (!range) selectionAnchor = obj.Id;
        });
    }
    private List<Guid> VisibleObjectOrder()
    {
        var result = new List<Guid>();
        void Visit(Guid parent, int depth) { if(depth>128) return; foreach(var obj in Project!.Objects.Where(o=>o.ParentId==parent && !IsIkControl(o.Id))) { if(Match(obj.Name)) result.Add(obj.Id); Visit(obj.Id,depth+1); } }
        Visit(Guid.Empty,0); return result;
    }
    private void DeleteSelection()
    {
        if (Project is null || modelContext.Length > 0) return;
        // A selected animation part is not permission to delete its actor.
        if (PosePicking && Session.SelectedPart != Guid.Empty) return;
        var ids = selectedObjects.Append(Session.SelectedObject).Where(id=>id!=Guid.Empty).ToHashSet();
        if (ids.Count == 0) return;
        Continue(() =>
        {
            if (Session.EditingAnimation is not null) Session.LeaveAnimation();
            Session.Edit(L("creation-delete-object"),p=>DirectorCreationEdits.DeleteObjects(p,ids));
            selectedObjects.Clear(); Session.SelectedObject=Session.SelectedPart=Guid.Empty;
            clipId=keyId=trackId=Guid.Empty; gizmoUsing=false; handleAxis=-1;
        });
    }
    private void HandleSelectionKeys()
    {
        if (ImGui.GetIO().WantTextInput || ImGui.IsAnyItemActive() || ImGui.IsPopupOpen("",ImGuiPopupFlags.AnyPopupId|ImGuiPopupFlags.AnyPopupLevel)) return;
        var io=ImGui.GetIO();
        if(selectionArea==SelectionArea.Timeline)
        {
            HandleTimelineKeys();return;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Delete,false) && keyId!=Guid.Empty && Project!.Objects.FirstOrDefault(o=>o.PoseKeys.Any(k=>k.Id==keyId)) is { } poseOwner)
        { Continue(()=>Session.Edit(L("creation-delete-keyframe"),_=> { poseOwner.PoseKeys.RemoveAll(k=>k.Id==keyId);keyId=Guid.Empty; })); return; }
        if(ImGui.IsKeyPressed(ImGuiKey.Delete,false) && selectionArea==SelectionArea.Objects && clipId==Guid.Empty && keyId==Guid.Empty) DeleteSelection();
        if(io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A,false) && !PosePicking && modelContext.Length==0)
        { selectedObjects.Clear(); selectedObjects.UnionWith(VisibleObjectOrder()); Session.SelectedObject=selectedObjects.FirstOrDefault(); Session.SelectedPart=Guid.Empty; }
        if(!io.KeyCtrl && !io.KeyAlt && !io.KeyShift)
        {
            if(ImGui.IsKeyPressed(ImGuiKey.G,false)) { operation=ImGuizmoNET.OPERATION.TRANSLATE; posePull=Session.PoseEditing&&!freePoseTransform; }
            if(ImGui.IsKeyPressed(ImGuiKey.R,false)) { operation=ImGuizmoNET.OPERATION.ROTATE; posePull=false; }
            if(ImGui.IsKeyPressed(ImGuiKey.S,false) && (!Session.PoseEditing||freePoseTransform)) { operation=ImGuizmoNET.OPERATION.SCALE; posePull=false; }
        }
    }
}
