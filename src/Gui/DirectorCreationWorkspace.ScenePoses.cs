using static VSDirector.Gui.DirectorCreationText;
using System.Numerics;
using ImGuiNET;
using VSDirector.Data;
namespace VSDirector.Gui;
internal sealed partial class DirectorCreationWorkspace
{
    private Guid scenePoseDrag,scenePoseOwner;
    private double scenePoseDrop;
    private void DrawScenePoseRow(CreationSceneObject actor,float x0,float width,float labelWidth,float height,Func<double,float> x,Func<float,double> time)
    {
        var row=ImGui.GetCursorScreenPos();var draw=ImGui.GetWindowDrawList();var io=ImGui.GetIO();
        ImGui.InvisibleButton("scene-pose-row",new(Math.Max(1,ImGui.GetContentRegionAvail().X),height),ImGuiButtonFlags.MouseButtonLeft|ImGuiButtonFlags.MouseButtonRight);
        bool hovered=ImGui.IsItemHovered();
        if(scenePoseOwner==actor.Id && scenePoseDrag!=Guid.Empty && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) scenePoseDrop=time(io.MousePos.X);
        TimelineRowBackground(row,labelWidth+width,height,Session.ScenePoseActor==actor.Id?DirectorImGuiTheme.Selected:DirectorImGuiTheme.Panel);
        draw.AddText(row+new Vector2(8*DirectorImGuiTheme.Scale,(height-ImGui.GetFontSize())/2),DirectorImGuiTheme.Text,DirectorImGuiIcons.Get(expandedPoseObjects.Contains(actor.Id)?"chevron-down":"chevron-right"));
        draw.AddText(row+new Vector2(30*DirectorImGuiTheme.Scale,(height-ImGui.GetFontSize())/2),DirectorImGuiTheme.Text,L("creation-pose"));
        draw.PushClipRect(new(x0,row.Y),new(x0+width,row.Y+height),true);
        foreach(var key in actor.PoseKeys)
        {
            var center=new Vector2(x(scenePoseDrag==key.Id?scenePoseDrop:key.Time),row.Y+height/2);
            string glyph=DirectorImGuiIcons.Get("square-rotated-filled");float size=24*DirectorImGuiTheme.Scale;
            draw.AddText(ImGui.GetFont(),size,center-ImGui.CalcTextSize(glyph)*(size/ImGui.GetFontSize())*.5f,keyId==key.Id||selectedTimelineKeys.Contains(key.Id)?DirectorImGuiTheme.Accent:DirectorImGuiTheme.Text,glyph);
        }
        draw.PopClipRect();
        var nearest=actor.PoseKeys.MinBy(k=>Math.Abs(x(k.Time)-io.MousePos.X));
        var hit=nearest is not null && io.MousePos.X>=x0 && Math.Abs(x(nearest.Time)-io.MousePos.X)<12*DirectorImGuiTheme.Scale?nearest:null;
        if(hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if(io.MousePos.X<row.X+28*DirectorImGuiTheme.Scale)
            {if(!expandedPoseObjects.Add(actor.Id))expandedPoseObjects.Remove(actor.Id);return;}
            bool drag=!Session.HasTransformDraft;
            Continue(()=>
            {
                Session.BeginScenePose(actor.Id);wholeObjectSelection=false;showBones=false;
                trackId=clipId=Guid.Empty;poseEditorActor=Guid.Empty;actionPoseEditorTime=null;selectionArea=SelectionArea.Timeline;
                if(hit is not null) { SelectTimelineKey(hit.Id,actor.PoseKeys.Select(k=>(k.Id,k.Time)),io.KeyCtrl,io.KeyShift);Session.Seek(hit.Time);if(ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))OpenPoseEditor(actor,hit);else if(drag&&selectedTimelineKeys.Contains(hit.Id)) { scenePoseDrag=hit.Id;scenePoseOwner=actor.Id;scenePoseDrop=hit.Time;BeginKeyMove(); } }
                else {keyId=Guid.Empty;selectedTimelineKeys.Clear();if(io.MousePos.X>=x0) Session.Seek(time(io.MousePos.X));}
            });
        }
        if(scenePoseOwner==actor.Id && scenePoseDrag!=Guid.Empty)
        {
            if(ImGui.IsMouseDragging(ImGuiMouseButton.Left)) scenePoseDrop=time(io.MousePos.X);
            if(actor.PoseKeys.Find(k=>k.Id==scenePoseDrag) is { } source) TimelineDrag(source.Time,scenePoseDrop);
            if(ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                Guid id=scenePoseDrag;scenePoseDrag=Guid.Empty;
                if(keyDragTimes.TryGetValue(id,out var start))MoveSelectedKeys(scenePoseDrop-start);
                FinishKeyMove();
                Session.Seek(scenePoseDrop);
            }
        }
        if(hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) contextScenePose=hit?.Id??Guid.Empty;
        if(ImGui.BeginPopupContextItem("scene-pose-menu"))
        {
            if(ImGui.MenuItem(L("creation-pose-details"),"",expandedPoseObjects.Contains(actor.Id))) { if(!expandedPoseObjects.Add(actor.Id)){expandedPoseObjects.Remove(actor.Id);keyId=Guid.Empty;} }
            var selected=actor.PoseKeys.Find(k=>k.Id==contextScenePose);
            if(selected is not null)
            {
                Item(L("creation-edit-keyframe"),()=>OpenPoseEditor(actor,selected));
                Item(L("creation-pose-copy-current"),()=>Session.Edit(L("creation-copy-pose"),_=>DirectorCreationScenePoses.Move(actor,selected.Id,Session.Time,true)));
                Item(L("creation-delete-keyframe"),()=>Session.Edit(L("creation-delete-keyframe"),_=>actor.PoseKeys.Remove(selected)));
                if(ImGui.BeginMenu(L("creation-interpolation")))
                {
                    foreach(var mode in Enum.GetValues<CreationInterpolation>().Where(m=>m!=CreationInterpolation.Bezier))
                        Item(L("creation-scene-interp-"+mode.ToString().ToLowerInvariant()),()=>Session.Edit(L("creation-interpolation"),_=>selected.Interpolation=mode),selected:selected.Interpolation==mode);
                    ImGui.EndMenu();
                }
            }
            else Item(L("creation-scene-pose"),()=>StartScenePose(actor));
            ImGui.EndPopup();
        }
    }
    private Guid contextScenePose;
}
