using static VSDirector.Gui.DirectorCreationText;
using ImGuiNET;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private readonly HashSet<Guid> selectedTimelineKeys = new();
    private readonly Dictionary<Guid,double> keyDragTimes = new();
    private IEnumerable<(Guid Id,double Time)> TimelineKeys()
    {
        foreach(var track in EditTracks)foreach(var key in track.Keys)yield return(key.Id,key.Time);
        if(SelectedAnimation is null)foreach(var actor in Project!.Objects)foreach(var key in actor.PoseKeys)yield return(key.Id,key.Time);
    }
    private void SelectTimelineKey(Guid id, IEnumerable<(Guid Id,double Time)> row, bool toggle, bool range)
    {
        if(range && row.FirstOrDefault(k=>k.Id==keyId) is var anchor && anchor.Id!=Guid.Empty)
        {
            double time=row.First(k=>k.Id==id).Time;
            selectedTimelineKeys.UnionWith(row.Where(k=>k.Time>=Math.Min(time,anchor.Time)&&k.Time<=Math.Max(time,anchor.Time)).Select(k=>k.Id));
        }
        else if(toggle) { if(!selectedTimelineKeys.Add(id))selectedTimelineKeys.Remove(id); }
        else if(!selectedTimelineKeys.Contains(id)) { selectedTimelineKeys.Clear();selectedTimelineKeys.Add(id); }
        keyId=id;clipId=Guid.Empty;selectionArea=SelectionArea.Timeline;
    }
    private void BeginKeyMove()
    {
        keyDragTimes.Clear();
        foreach(var key in TimelineKeys().Where(k=>selectedTimelineKeys.Contains(k.Id)))keyDragTimes[key.Id]=key.Time;
        Session.BeginGesture();
    }
    private void MoveSelectedKeys(double delta)
    {
        if(keyDragTimes.Count==0)return;
        delta=Math.Clamp(delta,-keyDragTimes.Values.Min(),Session.EditDuration-keyDragTimes.Values.Max());
        bool changed=false;
        foreach(var track in EditTracks)foreach(var key in track.Keys)
            if(keyDragTimes.TryGetValue(key.Id,out var start)&&Math.Abs(key.Time-start-delta)>1e-9){key.Time=start+delta;changed=true;}
        if(SelectedAnimation is null)foreach(var actor in Project!.Objects)foreach(var key in actor.PoseKeys)
            if(keyDragTimes.TryGetValue(key.Id,out var start)&&Math.Abs(key.Time-start-delta)>1e-9){key.Time=start+delta;changed=true;}
        if(changed)Session.Changed();
    }
    private void FinishKeyMove()
    {
        // Only moved keys replace keys at the destination; neighbouring times stay intact.
        bool moved=TimelineKeys().Any(k=>keyDragTimes.TryGetValue(k.Id,out var t)&&Math.Abs(k.Time-t)>1e-9);
        if(moved)
        {
            foreach(var track in EditTracks)
            {
                var times=track.Keys.Where(k=>keyDragTimes.ContainsKey(k.Id)).Select(k=>k.Time).ToArray();
                track.Keys.RemoveAll(k=>!keyDragTimes.ContainsKey(k.Id)&&times.Any(t=>Math.Abs(t-k.Time)<1e-6));
            }
            if(SelectedAnimation is null)foreach(var actor in Project!.Objects)
            {
                var times=actor.PoseKeys.Where(k=>keyDragTimes.ContainsKey(k.Id)).Select(k=>k.Time).ToArray();
                actor.PoseKeys.RemoveAll(k=>!keyDragTimes.ContainsKey(k.Id)&&times.Any(t=>Math.Abs(t-k.Time)<1e-6));
            }
        }
        Session.EndGesture(L("creation-move-keyframe-clip"));keyDragTimes.Clear();
    }
    private void DeleteTimelineSelection()
    {
        var ids=selectedTimelineKeys.Contains(keyId)?new HashSet<Guid>(selectedTimelineKeys):new HashSet<Guid>{keyId};
        Session.Edit(L("creation-delete-keyframe"),_=>
        {
            foreach(var track in EditTracks)track.Keys.RemoveAll(k=>ids.Contains(k.Id));
            if(SelectedAnimation is null)foreach(var actor in Project!.Objects)actor.PoseKeys.RemoveAll(k=>ids.Contains(k.Id));
        });
        selectedTimelineKeys.Clear();keyId=Guid.Empty;
    }
    private void ClearTimelineSelection()
    { selectedTimelineKeys.Clear();keyId=trackId=clipId=poseEditorActor=Guid.Empty;actionPoseEditorTime=null;timelineEditor=false; }
}
