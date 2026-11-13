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
        public int LayoutVersion { get; set; }
        public bool Tools { get; set; } = true;
        public bool Properties { get; set; } = true;
        public bool View { get; set; } = true;
        public bool Bones { get; set; }
        public bool Ik { get; set; }
        public bool Actions { get; set; }
        public int ObjectTool { get; set; } = 1;
        public int PartTool { get; set; } = 2;
        public int ObjectAxis { get; set; } = -1;
        public int PartAxis { get; set; } = -1;
        public bool ObjectLocal { get; set; }
        public bool PartLocal { get; set; } = true;
        public bool ExtendedChain { get; set; }
        public bool AutoKey { get; set; } = true;
        public Dictionary<string,TimelineView> Views { get; set; } = new();
    }
    private EditorPreferences editorPreferences = new();
    private float timelineScroll, restoreTimelineScroll = -1;
    private sealed record SceneContext(Guid Object, Guid Part, Guid Key, Guid Track, Guid Clip, double Time, CreationEditTask Task,
        Guid PoseActor, HashSet<Guid> Objects, HashSet<Guid> Bones, HashSet<Guid> Keys, bool WholeObject);
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
    private void RestoreEditorPreferences()
    {
        editorPreferences.Views ??= new();
        toolsPanel=editorPreferences.Tools;properties=editorPreferences.Properties;view=editorPreferences.View;
        if(editorPreferences.LayoutVersion>=2){bonesPanel=editorPreferences.Bones;ikPanel=editorPreferences.Ik;actionsPanel=editorPreferences.Actions;}
        if(editorPreferences.LayoutVersion<3)host.MigrateCreationTools=true;
        objectTool=Enum.IsDefined(typeof(CreationTool),editorPreferences.ObjectTool)?(CreationTool)editorPreferences.ObjectTool:CreationTool.Move;
        partTool=Enum.IsDefined(typeof(CreationTool),editorPreferences.PartTool)?(CreationTool)editorPreferences.PartTool:CreationTool.Rotate;
        objectRotationAxis=new[]{-1,0,1,2,6}.Contains(editorPreferences.ObjectAxis)?editorPreferences.ObjectAxis:-1;
        poseRotationAxis=editorPreferences.LayoutVersion<3?-1:new[]{-1,0,1,2,6}.Contains(editorPreferences.PartAxis)?editorPreferences.PartAxis:-1;
        objectSpace=editorPreferences.ObjectLocal?ImGuizmoNET.MODE.LOCAL:ImGuizmoNET.MODE.WORLD;
        partSpace=editorPreferences.PartLocal?ImGuizmoNET.MODE.LOCAL:ImGuizmoNET.MODE.WORLD;
        extendedPullChain=editorPreferences.ExtendedChain;
    }
    private void PersistEditorPreferences()
    {
        SaveTimelineView();editorPreferences.LayoutVersion=host.MigrateCreationTools?0:3;
        editorPreferences.Tools=toolsPanel;editorPreferences.Properties=properties;editorPreferences.View=view;
        editorPreferences.Bones=bonesPanel;editorPreferences.Ik=ikPanel;editorPreferences.Actions=actionsPanel;
        editorPreferences.ObjectTool=(int)objectTool;editorPreferences.PartTool=(int)partTool;
        editorPreferences.ObjectAxis=objectRotationAxis;editorPreferences.PartAxis=poseRotationAxis;
        editorPreferences.ObjectLocal=objectSpace==ImGuizmoNET.MODE.LOCAL;editorPreferences.PartLocal=partSpace==ImGuizmoNET.MODE.LOCAL;
        editorPreferences.ExtendedChain=extendedPullChain;
        api.StoreModConfig(editorPreferences,"vsdirector-creation-editor.json");
    }
    private void RememberSceneContext()
    {
        SaveTimelineView();
        if(SelectedAnimation is null) sceneContext=new(Session.SelectedObject,Session.SelectedPart,keyId,trackId,clipId,Session.Time,Session.EditTask,
            Session.ScenePoseActor,new(selectedObjects),new(selectedBones),new(selectedTimelineKeys),wholeObjectSelection);
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
        bool first=true;
        if(SelectedAnimation is {} action)
        {
            if(ImGui.SmallButton(L("creation-project")))ReturnToProject();
            ImGui.SameLine();ImGui.TextWrapped("› "+action.Name+(Project!.Clips.Count(c=>c.AnimationId==action.Id)>1?" · "+L("creation-shared-action"):""));
            if(Project!.Clips.Count(c=>c.AnimationId==action.Id)>1 && Button(L("creation-copy-action-source")))Continue(MakeActionIndependent);
            if(ImGui.BeginPopupContextItem("action-source-options"))
            {
                Item(L("creation-copy-action-source"),MakeActionIndependent);ImGui.EndPopup();
            }
        }
        else
        {
            foreach(var task in Enum.GetValues<CreationEditTask>())
                if(InlineChoice(L(task==CreationEditTask.Placement?"creation-placement-short":"creation-animation"),Session.EditTask==task,ref first))
                    Continue(()=>{Session.SetEditTask(task);if(task==CreationEditTask.Placement)wholeObjectSelection=true;});
        }
        string recording=L(!Session.AnimationEditing?"creation-base-edit":Session.AutoKey?"creation-record-auto":"creation-record-manual");
        SameLineIfFits(recording);
        ImGui.TextWrapped(recording);
        first=false;
        foreach(var filter in Enum.GetValues<TimelineFilter>())
            if(InlineChoice(L("creation-filter-"+filter.ToString().ToLowerInvariant()),timelineFilter==filter,ref first))
            {timelineFilter=filter;ClearTimelineSelection();}
        if(InlineChoice(L("creation-selected-only"),selectedTracksOnly,ref first)){selectedTracksOnly=!selectedTracksOnly;ClearTimelineSelection();}
        if(InlineChoice(L("creation-keyed-only"),keyedTracksOnly,ref first)){keyedTracksOnly=!keyedTracksOnly;ClearTimelineSelection();}
        if(InlineChoice(L(timelineCurves?"creation-curve-view":"creation-keyframe-view"),timelineCurves,ref first))timelineCurves=!timelineCurves;
        if(timelineCurves)foreach(int axis in new[]{0,1,2})if(InlineChoice("XYZ"[axis].ToString(),curveAxis==axis,ref first))curveAxis=axis;
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
