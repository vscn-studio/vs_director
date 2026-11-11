using static VSDirector.Gui.DirectorCreationText;
using ImGuiNET;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private enum TimelineFilter { All, Properties, Pose, Clips }
    private enum SelectionArea { Objects, Bones, Timeline }
    private SelectionArea selectionArea;
    private TimelineFilter timelineFilter;
    private bool selectedTracksOnly, keyedTracksOnly, timelineCurves;
    private int curveAxis;
    private Guid actionResource, editingSourceClip, revealTrackObject, revealTrackPart;
    private readonly HashSet<Guid> pinnedTimelineObjects = new();
    private readonly HashSet<Guid> selectedBones = new();
    private sealed class TimelineView
    {
        public double Zoom { get; set; } = 1;
        public double Start { get; set; }
        public float Scroll { get; set; }
        public Dictionary<Guid,bool> Expanded { get; set; } = new();
        public HashSet<Guid> Pinned { get; set; } = new();
        public int Filter { get; set; }
        public bool SelectedOnly { get; set; }
        public bool KeyedOnly { get; set; }
        public bool Curves { get; set; }
        public int CurveAxis { get; set; }
        public HashSet<Guid> PoseDetails { get; set; } = new();
    }
    private sealed class EditorPreferences
    {
        public bool AutoKey { get; set; } = true;
        public Dictionary<string,TimelineView> Views { get; set; } = new();
    }
    private EditorPreferences editorPreferences = new();
    private float timelineScroll, restoreTimelineScroll = -1;
    private sealed record SceneContext(Guid Object, Guid Part, Guid Key, Guid Track, Guid Clip, double Time, CreationEditTask Task,
        Guid PoseActor, HashSet<Guid> Objects, HashSet<Guid> Bones, HashSet<Guid> Keys);
    private SceneContext? sceneContext;
    private string? activeViewKey;
    private string ViewKey => Project!.Id.ToString("N") + ":" + Session.SelectedAnimation.ToString("N");
    private void SaveTimelineView()
    {
        if (Project is null) return;
        if(displayedProject==Guid.Empty || displayedGeneration==Session.DocumentGeneration)editorPreferences.AutoKey = Session.AutoKey;
        editorPreferences.Views[activeViewKey ?? ViewKey] = new() { Zoom=timelineZoom,Start=timelineStart,Scroll=timelineScroll,
            Expanded=new(timelineObjectExpanded),Pinned=new(pinnedTimelineObjects),Filter=(int)timelineFilter,SelectedOnly=selectedTracksOnly,KeyedOnly=keyedTracksOnly,Curves=timelineCurves,CurveAxis=curveAxis,PoseDetails=new(expandedPoseObjects) };
    }
    private void RestoreTimelineView()
    {
        activeViewKey=ViewKey;
        selectedTimelineKeys.Clear();
        editorPreferences.Views ??= new();
        var state=editorPreferences.Views.GetValueOrDefault(ViewKey)??new();
        timelineZoom=double.IsFinite(state.Zoom)?Math.Clamp(state.Zoom,.05,256):1;timelineStart=double.IsFinite(state.Start)?Math.Max(0,state.Start):0;
        restoreTimelineScroll=float.IsFinite(state.Scroll)?Math.Max(0,state.Scroll):0;
        timelineObjectExpanded.Clear();foreach(var p in state.Expanded ?? new())timelineObjectExpanded[p.Key]=p.Value;
        pinnedTimelineObjects.Clear();pinnedTimelineObjects.UnionWith(state.Pinned ?? new());
        timelineFilter=Enum.IsDefined(typeof(TimelineFilter),state.Filter)?(TimelineFilter)state.Filter:TimelineFilter.All;
        selectedTracksOnly=state.SelectedOnly;keyedTracksOnly=state.KeyedOnly;timelineCurves=state.Curves;
        curveAxis=Math.Clamp(state.CurveAxis,0,2);expandedPoseObjects.Clear();expandedPoseObjects.UnionWith(state.PoseDetails ?? new());
    }
    private void PersistEditorPreferences()
    { SaveTimelineView(); api.StoreModConfig(editorPreferences,"vsdirector-creation-editor.json"); }
    private void RememberSceneContext()
    {
        SaveTimelineView();
        if(SelectedAnimation is null) sceneContext=new(Session.SelectedObject,Session.SelectedPart,keyId,trackId,clipId,Session.Time,Session.EditTask,
            Session.ScenePoseActor,new(selectedObjects),new(selectedBones),new(selectedTimelineKeys));
    }
    private static string ObjectName(CreationSceneObject obj) => obj.ActorHand==1 && obj.Name.Equals("RightHand",StringComparison.OrdinalIgnoreCase)?L("creation-right-hand-item")
        :obj.ActorHand==2 && obj.Name.Equals("LeftHand",StringComparison.OrdinalIgnoreCase)?L("creation-left-hand-item"):obj.Name;
    private string BoneName(CreationModel model, CreationPart part)
    {
        string? role=model.BodyMapping.FirstOrDefault(p=>p.Value==part.Id).Key;
        return role is "head" or "body" or "leftarm" or "rightarm" or "leftleg" or "rightleg" ? L("creation-role-"+role):part.Name;
    }
    private void DrawEditingContext(bool bone = false)
    {
        var actor=bone?RigActor:SelectedObject;
        string context=Session.AnimationEditing?L("creation-task-animation"):L("creation-task-placement");
        if(actor is not null)context+=" · "+ObjectName(actor);
        if(bone && CurrentModel is {} model && SelectedPart is {} part)context+=" › "+BoneName(model,part);
        if(SelectedAnimation is {} action)context+=" › "+action.Name;
        if(Session.AnimationEditing)context+=" · "+FormatTime(Session.EditTime);
        ImGui.TextWrapped(context);
        if(!Session.AnimationEditing && actor is not null && (Project!.Tracks.Any(t=>t.ObjectId==actor.Id&&t.Enabled&&t.Keys.Count>0)||Project.Clips.Any(c=>c.ObjectId==actor.Id&&c.Enabled&&Session.Time>=c.Start&&Session.Time<c.Start+c.Duration)))
        { Hint(L("creation-base-covered"));if(Button(L("creation-task-animation")))Continue(()=>Session.SetEditTask(CreationEditTask.Animation)); }
        if(selectedBones.Count>1 && bone)Hint(L("creation-selected-bones",selectedBones.Count));
        if(actor is not null && Session.Frame?.Objects.GetValueOrDefault(actor.Id)?.HasPerformance==true)
        {
            Hint(L("creation-source-recording"));
            var clip=Project!.Clips.LastOrDefault(c=>c.Enabled&&c.ObjectId==actor.Id&&c.PerformanceAssetId!=Guid.Empty&&Session.Time>=c.Start&&Session.Time<c.Start+c.Duration);
            if(clip is not null && Button(L("creation-edit-clip-keys")))EditClipAnimation(clip.Id);
        }
    }
    private void DrawTimelineContext()
    {
        if(SelectedAnimation is {} action)
        {
            if(ImGui.SmallButton(L("creation-project")))ReturnToProject();
            ImGui.SameLine();ImGui.TextWrapped("› "+action.Name+(Project!.Clips.Count(c=>c.AnimationId==action.Id)>1?" · "+L("creation-shared-action"):""));
            if(ImGui.BeginPopupContextItem("action-source-options"))
            {
                Item(L("creation-copy-action-source"),MakeActionIndependent);ImGui.EndPopup();
            }
        }
        else
        {
            ImGui.SetNextItemWidth(Math.Min(160*DirectorImGuiTheme.Scale,ImGui.GetContentRegionAvail().X));
            if(ImGui.BeginCombo("##editing-task",L(Session.AnimationEditing?"creation-task-animation":"creation-task-placement")))
            {
                foreach(var task in Enum.GetValues<CreationEditTask>())
                    Item(L(task==CreationEditTask.Placement?"creation-task-placement":"creation-task-animation"),()=>Session.SetEditTask(task),selected:Session.EditTask==task);
                ImGui.EndCombo();
            }
        }
    }
    private void TimelineOptions()
    {
        if(!ImGui.BeginPopupContextWindow("timeline-view-options",ImGuiPopupFlags.MouseButtonRight|ImGuiPopupFlags.NoOpenOverItems))return;
        foreach(var filter in Enum.GetValues<TimelineFilter>())
            if(ImGui.MenuItem(L("creation-filter-"+filter.ToString().ToLowerInvariant()),"",timelineFilter==filter)) { timelineFilter=filter; ClearTimelineSelection(); }
        ImGui.Separator();
        if(ImGui.MenuItem(L("creation-selected-only"),"",selectedTracksOnly)) { selectedTracksOnly=!selectedTracksOnly;ClearTimelineSelection(); }
        if(ImGui.MenuItem(L("creation-keyed-only"),"",keyedTracksOnly)) { keyedTracksOnly=!keyedTracksOnly;ClearTimelineSelection(); }
        if(ImGui.MenuItem(L("creation-curve-view"),"",timelineCurves))timelineCurves=!timelineCurves;
        if(timelineCurves)foreach(int axis in new[]{0,1,2})if(ImGui.MenuItem("XYZ"[axis].ToString(),"",curveAxis==axis))curveAxis=axis;
        if(ImGui.MenuItem(L("creation-reset-filters"))) { timelineFilter=TimelineFilter.All;selectedTracksOnly=keyedTracksOnly=false;timelineCurves=false; }
        ImGui.EndPopup();
    }
    private void RevealTimelineSelection(Guid actor, Guid part) { revealTrackObject=actor;revealTrackPart=part;timelineObjectExpanded.Remove(actor); }
    private void MakeActionIndependent()
    {
        if(SelectedAnimation is not {} action)return;
        var copy=CreationJson.Clone(action);copy.Id=Guid.NewGuid();copy.Name+=" · "+L("creation-copy");
        foreach(var t in copy.Tracks){t.Id=Guid.NewGuid();foreach(var k in t.Keys)k.Id=Guid.NewGuid();}
        Session.Edit(L("creation-copy-action-source"),p=>{p.Animations.Add(copy);if(p.Clips.Find(c=>c.Id==editingSourceClip) is {} clip)clip.AnimationId=copy.Id;});
        var sourceTime=Session.EditTime;SaveTimelineView();Session.EnterAnimation(copy.Id,Session.AnimationObject);Session.Seek(sourceTime);RestoreTimelineView();
    }
    private void RevealRow(Guid actor,Guid part,float top,float bottom)
    {
        if(actor!=revealTrackObject||part!=revealTrackPart)return;
        float lo=ImGui.GetWindowPos().Y,hi=lo+ImGui.GetWindowHeight();
        if(top<lo)ImGui.SetScrollY(Math.Max(0,ImGui.GetScrollY()+top-lo));
        else if(bottom>hi)ImGui.SetScrollY(ImGui.GetScrollY()+bottom-hi);
        revealTrackObject=revealTrackPart=Guid.Empty;
    }
}
