using static VSDirector.Gui.DirectorCreationText;
using ImGuiNET;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private sealed class TimelineClipboard
    {
        public string Kind { get; set; } = "";
        public Guid Model { get; set; }
        public CreationChannel Channel { get; set; }
        public Guid Part { get; set; }
        public CreationKeyframe? Key { get; set; }
        public CreationScenePoseKey? Pose { get; set; }
        public CreationClip? Clip { get; set; }
        public Guid Object { get; set; }
        public Guid Project { get; set; }
        public List<TimelineClipboard>? Items { get; set; }
    }
    private void HandleTimelineKeys() => ExecuteTimelineCommand(null);
    private void ExecuteTimelineCommand(string? command)
    {
        var io=ImGui.GetIO();var project=Project!;
        if(command=="select-all" || command is null && io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A,false))
        {
            selectedTimelineKeys.Clear();
            selectedTimelineKeys.UnionWith(TimelineKeys().Select(k=>k.Id));
            keyId=selectedTimelineKeys.FirstOrDefault();clipId=Guid.Empty;
            selectionArea=SelectionArea.Timeline;return;
        }
        if(command is null && !io.WantTextInput)
        {
            int direction=ImGui.IsKeyPressed(ImGuiKey.LeftArrow)?-1:ImGui.IsKeyPressed(ImGuiKey.RightArrow)?1:0;
            if(direction!=0 && io.KeyAlt)
            {
                Continue(()=>
                {
                    BeginKeyMove();
                    MoveSelectedKeys(direction*DirectorCreationTiming.FrameDuration*(io.KeyShift?10:1));
                    FinishKeyMove();
                });
                return;
            }
            if(direction!=0)
            {
                double next=EditTime+direction*DirectorCreationTiming.FrameDuration*(io.KeyShift?10:1);
                if(io.KeyCtrl)
                {
                    var times=TimelineKeys().Select(k=>k.Time);
                    next=direction<0?times.Where(t=>t<EditTime-1e-6).DefaultIfEmpty(0).Max()
                        :times.Where(t=>t>EditTime+1e-6).DefaultIfEmpty(Session.EditDuration).Min();
                }
                SeekEditTime(Math.Clamp(next,0,Session.EditDuration));return;
            }
            if(ImGui.IsKeyPressed(ImGuiKey.Home,false)){SeekEditTime(0);return;}
            if(ImGui.IsKeyPressed(ImGuiKey.End,false)){SeekEditTime(Session.EditDuration);return;}
        }
        var track=EditTracks.Find(t=>t.Keys.Any(k=>k.Id==keyId));var key=track?.Keys.Find(k=>k.Id==keyId);
        var poseActor=project.Objects.Find(o=>o.PoseKeys.Any(k=>k.Id==keyId));var pose=poseActor?.PoseKeys.Find(k=>k.Id==keyId);
        var clip=project.Clips.Find(c=>c.Id==clipId);
        if(command=="delete" || command is null && ImGui.IsKeyPressed(ImGuiKey.Delete,false))
        {
            if(key is not null || pose is not null) { Continue(DeleteTimelineSelection);return; }
            else if(actionPoseEditorTime is {} poseTime && SelectedAnimation is {} action)
                Continue(()=>Session.Edit(L("creation-delete-keyframe"),_=>{foreach(var t in action.Tracks.Where(t=>t.PartId!=Guid.Empty))t.Keys.RemoveAll(k=>Math.Abs(k.Time-poseTime)<1e-6);actionPoseEditorTime=null;}));
            else if(clip is not null)Continue(()=>Session.Edit(L("creation-delete-clip"),_=>project.Clips.Remove(clip)));
            keyId=clipId=Guid.Empty;return;
        }
        if(!io.KeyCtrl && command is null)return;
        bool cut=command=="cut" || command is null && ImGui.IsKeyPressed(ImGuiKey.X,false);
        if(command=="copy" || cut || command is null && ImGui.IsKeyPressed(ImGuiKey.C,false))
        {
            TimelineClipboard? data=key is not null?new(){Kind="key",Key=key,Channel=track!.Channel,Part=track.PartId,Model=SelectedObject?.ModelId??Guid.Empty}
                :pose is not null?new(){Kind="pose",Pose=pose,Model=poseActor!.ModelId}:clip is not null?new(){Kind="clip",Clip=clip,Model=project.Objects.Find(o=>o.Id==clip.ObjectId)?.ModelId??Guid.Empty}:null;
            if(data is null && actionPoseEditorTime is {} time && SelectedAnimation is {} action)
            {
                var state=DirectorCreationEvaluator.Evaluate(project,Session.Time,editingAction:action,actionActor:Session.AnimationObject,actionTime:time).Objects[Session.AnimationObject];
                data=new(){Kind="pose",Model=action.ModelId,Pose=new(){Time=time,Bones=state.Pose.ToDictionary(p=>p.Key,p=>p.Value with{}),Hidden=new(state.ExplicitHiddenParts)}};
            }
            if(selectedTimelineKeys.Contains(keyId)&&selectedTimelineKeys.Count>1)
            {
                var items=new List<TimelineClipboard>();
                foreach(var t in EditTracks)foreach(var k in t.Keys.Where(k=>selectedTimelineKeys.Contains(k.Id)))
                {
                    Guid actor=t.ObjectId==Guid.Empty?Session.AnimationObject:t.ObjectId;
                    items.Add(new(){Kind="key",Object=actor,Model=project.Objects.Find(o=>o.Id==actor)?.ModelId??Guid.Empty,Key=k,Part=t.PartId,Channel=t.Channel});
                }
                if(SelectedAnimation is null)foreach(var actor in project.Objects)foreach(var k in actor.PoseKeys.Where(k=>selectedTimelineKeys.Contains(k.Id)))
                    items.Add(new(){Kind="pose",Object=actor.Id,Model=actor.ModelId,Pose=k});
                if(items.Count>1)data=new(){Kind="selection",Project=project.Id,Items=items};
            }
            if(data is not null)
            {
                ImGui.SetClipboardText("VSDirectorTimeline:"+System.Text.Json.JsonSerializer.Serialize(data,CreationJson.Options));
                if(cut)ExecuteTimelineCommand("delete");
            }
        }
        if(command=="paste" || command is null && ImGui.IsKeyPressed(ImGuiKey.V,false))Run(()=>
        {
            const string prefix="VSDirectorTimeline:";string value=ImGui.GetClipboardText();if(!value.StartsWith(prefix,StringComparison.Ordinal))return;
            var data=System.Text.Json.JsonSerializer.Deserialize<TimelineClipboard>(value[prefix.Length..],CreationJson.Options);if(data is null)return;
            if(data.Model!=Guid.Empty&&data.Model!=SelectedObject?.ModelId)throw new InvalidOperationException(L("creation-clipboard-model"));
            Continue(()=>Session.Edit(L("creation-paste-keyframes"),p=>
            {
                if(data.Kind=="selection"&&data.Items is {Count:>0} items)
                {
                    double start=items.Min(i=>i.Key?.Time??i.Pose?.Time??0);
                    bool multiple=items.Select(i=>i.Object).Distinct().Count()>1;
                    if(multiple&&(data.Project!=p.Id||SelectedAnimation is not null))throw new InvalidOperationException(L("creation-clipboard-model"));
                    selectedTimelineKeys.Clear();
                    foreach(var item in items)
                    {
                        var actor=multiple?p.Objects.Find(o=>o.Id==item.Object):SelectedObject;
                        if(actor is null||item.Model!=Guid.Empty&&actor.ModelId!=item.Model)throw new InvalidOperationException(L("creation-clipboard-model"));
                        double time=EditTime+(item.Key?.Time??item.Pose?.Time??start)-start;
                        if(time>Session.EditDuration)throw new InvalidOperationException(L("creation-paste-outside-duration"));
                        if(item.Key is {} copied)
                        {
                            Guid target=SelectedAnimation is null?actor.Id:Guid.Empty;
                            var destination=EditTracks.Find(t=>t.ObjectId==target&&t.PartId==item.Part&&t.Channel==item.Channel);
                            if(destination is null){destination=new(){ObjectId=target,PartId=item.Part,Channel=item.Channel};EditTracks.Add(destination);}
                            copied.Id=Guid.NewGuid();copied.Time=time;destination.Keys.RemoveAll(k=>Math.Abs(k.Time-time)<1e-6);destination.Keys.Add(copied);selectedTimelineKeys.Add(copied.Id);keyId=copied.Id;trackId=destination.Id;
                        }
                        else if(item.Pose is {} copiedPose)
                        {
                            if(SelectedAnimation is {} action)DirectorCreationSession.WritePose(action,CompleteCopiedPose(actor,copiedPose),time);
                            else{copiedPose.Id=Guid.NewGuid();copiedPose.Time=time;actor.PoseKeys.RemoveAll(k=>Math.Abs(k.Time-time)<1e-6);actor.PoseKeys.Add(copiedPose);selectedTimelineKeys.Add(copiedPose.Id);keyId=copiedPose.Id;}
                        }
                    }
                }
                else if(data.Kind=="key"&&data.Key is {} k&&SelectedObject is {} obj)
                {
                    Guid target=SelectedAnimation is null?obj.Id:Guid.Empty;
                    var destination=EditTracks.Find(t=>t.ObjectId==target&&t.PartId==data.Part&&t.Channel==data.Channel);
                    if(destination is null){destination=new(){ObjectId=target,PartId=data.Part,Channel=data.Channel};EditTracks.Add(destination);}
                    k.Id=Guid.NewGuid();k.Time=EditTime;destination.Keys.RemoveAll(old=>Math.Abs(old.Time-k.Time)<1e-6);destination.Keys.Add(k);keyId=k.Id;trackId=destination.Id;
                }
                else if(data.Kind=="pose"&&data.Pose is {} s&&SelectedObject is {} actor)
                {
                    if(SelectedAnimation is {} action) { DirectorCreationSession.WritePose(action,CompleteCopiedPose(actor,s),EditTime);actionPoseEditorTime=EditTime;keyId=Guid.Empty; }
                    else {s.Id=Guid.NewGuid();s.Time=EditTime;actor.PoseKeys.RemoveAll(old=>Math.Abs(old.Time-s.Time)<1e-6);actor.PoseKeys.Add(s);keyId=s.Id;}
                }
                else if(data.Kind=="clip"&&data.Clip is {} c)
                {c.Id=Guid.NewGuid();c.Start=Session.Time;if(c.ObjectId!=Guid.Empty)c.ObjectId=Session.SelectedObject;p.Duration=Math.Max(p.Duration,c.Start+c.Duration);p.Clips.Add(c);clipId=c.Id;}
            }));
        });
    }
    private CreationPoseSnapshot CompleteCopiedPose(CreationSceneObject actor,CreationScenePoseKey pose)
        =>new(){Bones=Project!.Models.First(m=>m.Id==actor.ModelId).Parts.ToDictionary(p=>p.Id,p=>(pose.Bones.GetValueOrDefault(p.Id)??actor.Pose.GetValueOrDefault(p.Id)??new()) with{}),Hidden=new(pose.Hidden)};
}
