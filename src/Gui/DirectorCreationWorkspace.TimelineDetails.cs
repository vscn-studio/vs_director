using static VSDirector.Gui.DirectorCreationText;
using System.Numerics;
using ImGuiNET;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private Guid poseEditorActor, poseEditorKey, poseEditorPart;
    private double? actionPoseEditorTime;
    private readonly HashSet<Guid> expandedPoseObjects = new();
    private double CurveValue(CreationVector v)=>curveAxis==0?v.X:curveAxis==1?v.Y:v.Z;
    private (double Min,double Max) CurveRange(CreationTrack track)
    {
        if(draggedKey!=Guid.Empty&&track.Keys.Any(k=>k.Id==draggedKey)&&curveDragRange>0)return(curveDragMin,curveDragMin+curveDragRange);
        double lo=track.Keys.Count==0?0:track.Keys.Min(k=>CurveValue(k.Value)),hi=track.Keys.Count==0?1:track.Keys.Max(k=>CurveValue(k.Value));
        if(hi-lo<1e-6){lo-=1;hi+=1;}return(lo,hi);
    }
    private float CurveY(CreationTrack track,CreationVector value,float top,float height)
    {var range=CurveRange(track);return top+height-5-(float)((CurveValue(value)-range.Min)/(range.Max-range.Min))*(height-10);}
    private void OpenPoseEditor(CreationSceneObject actor,CreationScenePoseKey key,Guid part=default)
    { poseEditorActor=actor.Id;poseEditorKey=key.Id;poseEditorPart=part;actionPoseEditorTime=null;timelineEditor=true;keyId=key.Id;clipId=trackId=Guid.Empty;selectionArea=SelectionArea.Timeline; }
    private void DrawScenePoseDetails(CreationSceneObject actor,float x0,float width,float labelWidth,float height,Func<double,float> x,Func<float,double> time)
    {
        if(revealTrackObject==actor.Id && revealTrackPart!=Guid.Empty)expandedPoseObjects.Add(actor.Id);
        if(!expandedPoseObjects.Contains(actor.Id)||Project!.Models.Find(m=>m.Id==actor.ModelId) is not {} model)return;
        foreach(var part in model.Parts.Where(p=>exactPoseParts||DirectorCreationPoseGeometry.PoseTarget(Project,model,p.Id)==p.Id))
        {
            var keys=actor.PoseKeys.Where(k=>!keyedTracksOnly||k.Bones.ContainsKey(part.Id)||k.Hidden.Contains(part.Id)).ToArray();
            if(keyedTracksOnly&&keys.Length==0)continue;
            var row=ImGui.GetCursorScreenPos();var draw=ImGui.GetWindowDrawList();
            ImGui.InvisibleButton("pose-part-"+part.Id,new(Math.Max(1,ImGui.GetContentRegionAvail().X),height),ImGuiButtonFlags.MouseButtonLeft|ImGuiButtonFlags.MouseButtonRight);
            bool hovered=ImGui.IsItemHovered();
            RevealRow(actor.Id,part.Id,row.Y,row.Y+height);
            TimelineRowBackground(row,labelWidth+width,height,Session.SelectedObject==actor.Id&&Session.SelectedPart==part.Id?DirectorImGuiTheme.Selected:DirectorImGuiTheme.Panel);
            draw.PushClipRect(row,new(x0,row.Y+height),true);draw.AddText(row+new Vector2(24*DirectorImGuiTheme.Scale,(height-ImGui.GetFontSize())/2),DirectorImGuiTheme.Text,BoneName(model,part));draw.PopClipRect();
            draw.PushClipRect(new(x0,row.Y),new(x0+width,row.Y+height),true);
            foreach(var key in keys)
            {
                var center=new Vector2(x(key.Time),row.Y+height/2);string glyph=DirectorImGuiIcons.Get("square-rotated-filled");float size=24*DirectorImGuiTheme.Scale;
                draw.AddText(ImGui.GetFont(),size,center-ImGui.CalcTextSize(glyph)*(size/ImGui.GetFontSize())*.5f,keyId==key.Id?DirectorImGuiTheme.Accent:DirectorImGuiTheme.Text,glyph);
            }
            draw.PopClipRect();
            if(hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var nearest=keys.MinBy(k=>Math.Abs(x(k.Time)-ImGui.GetIO().MousePos.X));
                if(nearest is not null && ImGui.GetIO().MousePos.X>=x0 && Math.Abs(x(nearest.Time)-ImGui.GetIO().MousePos.X)<12*DirectorImGuiTheme.Scale)
                { keyId=nearest.Id;clipId=trackId=Guid.Empty;selectionArea=SelectionArea.Timeline;if(ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))OpenPoseEditor(actor,nearest,part.Id); }
                else SelectBone(actor.Id,part.Id);
            }
            if(ImGui.BeginPopupContextItem("pose-part-menu-"+part.Id))
            {
                var nearest=keys.MinBy(k=>Math.Abs(x(k.Time)-ImGui.GetIO().MousePos.X));
                if(nearest is not null)Item(L("creation-edit-keyframe"),()=>OpenPoseEditor(actor,nearest,part.Id));
                ImGui.EndPopup();
            }
        }
    }
    private void DrawTrackCurve(ImDrawListPtr draw,CreationTrack track,Vector2 row,float left,float right,float height,Func<double,float> x)
    {
        if(track.Keys.Count<2)return;
        var keys=track.Keys.OrderBy(k=>k.Time).ToArray();
        var range=CurveRange(track);
        uint color=new[]{0xff5c5ce8u,0xff77cb65u,0xffe8a35cu}[curveAxis];
        for(int n=0;n<keys.Length-1;n++)
        {
            float a=Math.Max(left,x(keys[n].Time)),b=Math.Min(right,x(keys[n+1].Time));if(b<a)continue;
            var segment=new CreationTrack{Enabled=track.Enabled,Channel=track.Channel,ShortestRotation=track.ShortestRotation,Keys=[keys[n],keys[n+1]]};
            Vector2 previous=default;
            int steps=Math.Max(1,(int)((b-a)/3));
            for(int i=0;i<=steps;i++)
            {
                float px=a+(b-a)*i/steps;
                double t=keys[n].Time+(keys[n+1].Time-keys[n].Time)*(px-x(keys[n].Time))/Math.Max(.001f,x(keys[n+1].Time)-x(keys[n].Time));
                double value=CurveValue(DirectorCreationEvaluator.Sample(segment,t,default));
                var point=new Vector2(px,row.Y+height-5-(float)((value-range.Min)/(range.Max-range.Min))*(height-10));
                if(i>0)draw.AddLine(previous,point,color,1.5f*DirectorImGuiTheme.Scale);previous=point;
            }
        }
    }
    private bool PoseEditorValid => poseEditorActor!=Guid.Empty && Project!.Objects.Any(o=>o.Id==poseEditorActor&&o.PoseKeys.Any(k=>k.Id==poseEditorKey))
        || actionPoseEditorTime is {} time && SelectedAnimation?.Tracks.Any(t=>t.PartId!=Guid.Empty&&t.Keys.Any(k=>Math.Abs(k.Time-time)<1e-6))==true;
    private void DrawPoseKeyEditor()
    {
        bool scene=poseEditorActor!=Guid.Empty;
        var actor=scene?Project!.Objects.First(o=>o.Id==poseEditorActor):Project!.Objects.First(o=>o.Id==Session.AnimationObject);
        var model=Project.Models.First(m=>m.Id==actor.ModelId);
        var key=scene?actor.PoseKeys.First(k=>k.Id==poseEditorKey):null;
        double time=key?.Time??actionPoseEditorTime!.Value;
        ImGui.TextWrapped(ObjectName(actor)+" · "+FormatTime(time));
        if(Button(L("creation-delete-keyframe")))
        {
            Session.Edit(L("creation-delete-keyframe"),_=>{if(scene)actor.PoseKeys.Remove(key!);else foreach(var track in EditTracks.Where(t=>t.PartId!=Guid.Empty))track.Keys.RemoveAll(k=>Math.Abs(k.Time-time)<1e-6);});
            timelineEditor=false;return;
        }
        TimeNumber(L("creation-time"),time,value=>
        {
            if(scene)DirectorCreationScenePoses.Move(actor,key!.Id,value,false);
            else {DirectorCreationSession.CopyPoseTime(SelectedAnimation!,time,value,true);actionPoseEditorTime=value;}
        },0,Session.EditDuration);
        if(scene && Combo(L("creation-interpolation"),L("creation-scene-interp-"+key!.Interpolation.ToString().ToLowerInvariant())))
        {
            foreach(var mode in Enum.GetValues<CreationInterpolation>().Where(m=>m!=CreationInterpolation.Bezier))
                Item(L("creation-scene-interp-"+mode.ToString().ToLowerInvariant()),()=>Session.Edit(L("creation-interpolation"),_=>key.Interpolation=mode),selected:key.Interpolation==mode);
            ImGui.EndCombo();
        }
        if(Combo(L("creation-bone"),model.Parts.Find(p=>p.Id==poseEditorPart)?.Name??L("creation-select-bone-first")))
        {foreach(var part in model.Parts.Where(p=>exactPoseParts||DirectorCreationPoseGeometry.PoseTarget(Project,model,p.Id)==p.Id))if(ImGui.Selectable(BoneName(model,part)+"##"+part.Id))poseEditorPart=part.Id;ImGui.EndCombo();}
        if(model.Parts.All(p=>p.Id!=poseEditorPart))return;
        var basis=actor.Pose.GetValueOrDefault(poseEditorPart)??new();
        var value=scene?key!.Bones.GetValueOrDefault(poseEditorPart)??basis:DirectorCreationEvaluator.Evaluate(Project,Session.Time,editingAction:SelectedAnimation,actionActor:actor.Id,actionTime:time).Objects[actor.Id].Pose.GetValueOrDefault(poseEditorPart)??basis;
        void Field(CreationChannel channel,CreationVector v,Action<CreationTransform,CreationVector> assign)
        {
            VectorField(ChannelName(channel),v,next=>
            {
                if(scene){var changed=value with{};assign(changed,next);key!.Bones[poseEditorPart]=changed;}
                else {var track=EditTracks.Find(t=>t.PartId==poseEditorPart&&t.Channel==channel);if(track is null){track=new(){PartId=poseEditorPart,Channel=channel};EditTracks.Add(track);}DirectorCreationEdits.SetKey(track,time,next);}
            },channel==CreationChannel.Rotation?1:.1f,inline:true);
        }
        Field(CreationChannel.Position,value.Position,(t,v)=>t.Position=v);Field(CreationChannel.Rotation,value.Rotation,(t,v)=>t.Rotation=v);
        Field(CreationChannel.Scale,value.Scale,(t,v)=>t.Scale=v);Field(CreationChannel.Pivot,value.Pivot,(t,v)=>t.Pivot=v);
        bool visible=scene?!key!.Hidden.Contains(poseEditorPart):!DirectorCreationEvaluator.Evaluate(Project,Session.Time,editingAction:SelectedAnimation,actionActor:actor.Id,actionTime:time).Objects[actor.Id].HiddenParts.Contains(poseEditorPart);
        Toggle(L("creation-visible"),visible,v=>{if(scene){if(v)key!.Hidden.Remove(poseEditorPart);else key!.Hidden.Add(poseEditorPart);}else{var track=EditTracks.Find(t=>t.PartId==poseEditorPart&&t.Channel==CreationChannel.Visible);if(track is null){track=new(){PartId=poseEditorPart,Channel=CreationChannel.Visible};EditTracks.Add(track);}DirectorCreationEdits.SetKey(track,time,new(v?1:0,0,0));}});
    }
}
