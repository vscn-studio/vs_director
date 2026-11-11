using static VSDirector.Gui.DirectorCreationText;
using System.Numerics;
using ImGuiNET;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private double? draggingPose, poseContextTime;
    private double poseDropTime;
    private Guid poseTimelineAction;
    private void DrawPoseTimelineRow(float x0, float width, float labelWidth, float height, Func<double,float> x, Func<float,double> time)
    {
        var action = Session.EditingAnimation!;
        if (poseTimelineAction != action.Id) { poseTimelineAction = action.Id; draggingPose = poseContextTime = null; }
        if (draggingPose is not null && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) poseDropTime = time(ImGui.GetIO().MousePos.X);
        var times = action.Tracks.Where(t => t.PartId != Guid.Empty).SelectMany(t => t.Keys).Select(k => k.Time).Distinct().Order().ToArray();
        var row = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        ImGui.InvisibleButton("pose-summary", new(Math.Max(1,ImGui.GetContentRegionAvail().X),height), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        TimelineRowBackground(row,labelWidth+width,height,DirectorImGuiTheme.Panel);
        draw.AddText(row+new Vector2(5,(height-ImGui.GetFontSize())/2),DirectorImGuiTheme.Text,L("creation-pose"));
        draw.PushClipRect(new(x0,row.Y),new(x0+width,row.Y+height),true);
        foreach (double t in times)
        {
            double shown = draggingPose == t ? poseDropTime : t;
            var center = new Vector2(x(shown),row.Y+height/2);
            string glyph = DirectorImGuiIcons.Get("square-rotated-filled"); float size = 24*DirectorImGuiTheme.Scale;
            var extent = ImGui.CalcTextSize(glyph)*(size/ImGui.GetFontSize());
            draw.AddText(ImGui.GetFont(),size,center-extent*.5f,Math.Abs(Session.EditTime-t)<1e-6?DirectorImGuiTheme.Accent:0xffe6e0de,glyph);
        }
        draw.PopClipRect();
        var io = ImGui.GetIO();
        double? nearest = times.Length == 0 ? null : times.MinBy(t => Math.Abs(x(t)-io.MousePos.X));
        bool hit = nearest is { } n && io.MousePos.X >= x0 && Math.Abs(x(n)-io.MousePos.X)<12*DirectorImGuiTheme.Scale;
        if (hovered && hit && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            double selected = nearest!.Value; bool canDrag = !Session.HasTransformDraft;
            Continue(() => { Session.Seek(selected);poseEditorActor=Guid.Empty;actionPoseEditorTime=selected;keyId=clipId=trackId=Guid.Empty;selectionArea=SelectionArea.Timeline;
                if(ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)){timelineEditor=true;}else if (canDrag) { draggingPose=selected; poseDropTime=selected; } });
        }
        if (draggingPose is { } source)
        {
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Left)) poseDropTime=time(io.MousePos.X);
            TimelineDrag(source, poseDropTime);
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                draggingPose=null;
                if (Math.Abs(source-poseDropTime)>1e-6) Session.Edit(L("creation-move-keyframe-clip"), _=>DirectorCreationSession.CopyPoseTime(action,source,poseDropTime,true));
                Session.Seek(poseDropTime);
                actionPoseEditorTime=poseDropTime;
            }
        }
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) poseContextTime=hit?nearest:null;
        if (ImGui.BeginPopupContextItem("pose-summary-menu"))
        {
            if (poseContextTime is { } sourceTime)
            {
                Item(L("creation-edit-keyframe"),()=> { poseEditorActor=Guid.Empty;actionPoseEditorTime=sourceTime;timelineEditor=true; });
                Item(L("creation-pose-copy-current"),()=>Session.Edit(L("creation-copy-pose"),_=>DirectorCreationSession.CopyPoseTime(action,sourceTime,Session.EditTime,false)));
                Item(L("creation-delete-keyframe"),()=>Session.Edit(L("creation-delete-keyframe"),_=> { foreach(var track in action.Tracks.Where(t=>t.PartId!=Guid.Empty)) track.Keys.RemoveAll(k=>Math.Abs(k.Time-sourceTime)<1e-6); }));
            }
            ImGui.EndPopup();
        }
    }
}
