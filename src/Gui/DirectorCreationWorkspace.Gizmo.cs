using static VSDirector.Gui.DirectorCreationText;
using System.Numerics;
using ImGuiNET;
using ImGuizmoNET;
using VSDirector.Data;
namespace VSDirector.Gui;
internal sealed partial class DirectorCreationWorkspace
{
    private readonly DirectorEditorHandles creationHandles=new();
    private int handleAxis=-1,handleHover=-1;
    private Matrix4x4 handleStart;
    private Vector2 handlePointer,handleCenter;
    private Vector3 handleDirection,handleRight,handleUp,handleView;
    private float handleUnits,handleAngle,handleLastAngle;
    private OPERATION handleOperation;
    private int poseRotationAxis=-1;
    private Vector2 poseDragTangent;
    private bool simplePoseDrag;
    private DirectorCreationDrag? translationDrag;
    private Vector3[] handleBasis = [];
    private Guid handleOwner,handlePart,handleAction;
    private bool ManipulateCreation(ImDrawListPtr draw,Matrix4x4 world,float[] view,float[] projection,Vector2 min,Vector2 size,bool camera,out Matrix4x4 result)
    {
        result=world; var io=ImGui.GetIO();
        if(handleOwner!=Session.SelectedObject || handlePart!=Session.SelectedPart || handleAction!=Session.SelectedAnimation)
        { handleAxis=-1;handleHover=-1;handleOwner=Session.SelectedObject;handlePart=Session.SelectedPart;handleAction=Session.SelectedAnimation; }
        var v=new Matrix4x4(view[0],view[1],view[2],view[3],view[4],view[5],view[6],view[7],view[8],view[9],view[10],view[11],view[12],view[13],view[14],view[15]);
        var p=new Matrix4x4(projection[0],projection[1],projection[2],projection[3],projection[4],projection[5],projection[6],projection[7],projection[8],projection[9],projection[10],projection[11],projection[12],projection[13],projection[14],projection[15]);
        Vector2? Screen(Vector3 at) { var c=Vector4.Transform(new Vector4(at,1),v*p); return c.W<=.001f?null:min+new Vector2((c.X/c.W+1)*size.X/2,(1-c.Y/c.W)*size.Y/2); }
        if(Screen(world.Translation) is not { } center) { handleAxis=-1;handleHover=-1;return false; }
        float units=Math.Max(.000001f,Math.Abs(Vector4.Transform(new Vector4(world.Translation,1),v*p).W)/(Math.Abs(projection[5])*size.Y/2));
        var right=Vector3.Normalize(new Vector3(view[0],view[4],view[8]));var up=Vector3.Normalize(new Vector3(view[1],view[5],view[9]));var eye=Vector3.Normalize(new Vector3(view[2],view[6],view[10]));
        var axes=new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ};
        if(transformMode==MODE.LOCAL && Matrix4x4.Decompose(world,out _,out var orientation,out _))
            for(int i=0;i<3;i++)axes[i]=Vector3.Transform(axes[i],Quaternion.Normalize(orientation));
        float radius=DirectorEditorHandles.Radius(units,operation==OPERATION.ROTATE);
        var pivotClip=Vector4.Transform(new Vector4(world.Translation,1),v*p);
        float displayScale=DirectorEditorHandles.ProjectionScale(pivotClip.W,radius);
        Vector2? ProjectHandle(Vector3 offset)
        {
            // Project offsets separately to avoid losing small controls at large
            // world coordinates. Bound their depth so no tip crosses the eye.
            var delta=Vector4.Transform(new Vector4(offset*displayScale,0),v*p);
            float w=pivotClip.W+delta.W;
            if(w<=0) return null;
            var ndcDelta=new Vector2(delta.X-pivotClip.X/pivotClip.W*delta.W,
                delta.Y-pivotClip.Y/pivotClip.W*delta.W)/w;
            return center+ndcDelta*new Vector2(size.X/2,-size.Y/2)/displayScale;
        }
        bool interactive=ImGui.IsMouseHoveringRect(min,min+size)&&(!ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow)||ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows));
        bool pose=Session.PoseEditing&&Session.SelectedPart!=Guid.Empty;
        handleHover=creationHandles.Draw(draw,center,units,radius,view.Select(x=>(double)x).ToArray(),axes,operation==OPERATION.ROTATE,operation==OPERATION.SCALE,handleAxis,interactive,handleHover,pose?poseRotationAxis:objectRotationAxis,
            operation==OPERATION.ROTATE ? null : ProjectHandle);
        if(handleAxis<0 && handleHover>=0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            handleAxis=handleHover;handleStart=world;handlePointer=io.MousePos;handleCenter=center;handleUnits=units;
            handleRight=right;handleUp=up;handleView=eye;handleDirection=handleAxis==6?eye:axes[handleAxis%3];handleOperation=operation;
            handleBasis=axes.ToArray();
            translationDrag=operation==OPERATION.ROTATE?null:DirectorCreationDrag.Begin(v*p,min,size,io.MousePos,world.Translation,handleDirection,handleAxis>=3);
            if(operation!=OPERATION.ROTATE && translationDrag is null) { handleAxis=-1; return false; }
            handleLastAngle=PointerAngle(io.MousePos);handleAngle=0;
            simplePoseDrag=pose&&operation==OPERATION.ROTATE&&handleAxis!=6;
            var radial=right*(io.MousePos.X-center.X)-up*(io.MousePos.Y-center.Y);
            var tangent=Vector3.Cross(handleDirection,radial);
            poseDragTangent=new(Vector3.Dot(tangent,right),-Vector3.Dot(tangent,up));
            poseDragTangent=poseDragTangent.LengthSquared()<1?Vector2.UnitX:Vector2.Normalize(poseDragTangent);
        }
        if(handleAxis<0) return false;
        if(!ImGui.IsMouseDown(ImGuiMouseButton.Left)) { handleAxis=-1;return false; }
        var delta=io.MousePos-handlePointer;
        if(handleOperation==OPERATION.ROTATE)
        {
            float angle=PointerAngle(io.MousePos);float step=angle-handleLastAngle;
            handleAngle+=MathF.Atan2(MathF.Sin(step),MathF.Cos(step));handleLastAngle=angle;
            if(simplePoseDrag) handleAngle=Vector2.Dot(delta,poseDragTangent)*MathF.PI/(180*DirectorImGuiTheme.Scale);
            float applied=io.KeyShift?handleAngle*.1f:handleAngle;
            applied=io.KeyCtrl?MathF.Round(applied/(MathF.PI/12))*(MathF.PI/12):applied;
            result=handleStart*Matrix4x4.CreateTranslation(-handleStart.Translation)*Matrix4x4.CreateFromAxisAngle(handleDirection,applied)*Matrix4x4.CreateTranslation(handleStart.Translation);
            string label=(handleAxis==6?L("camera-rotation-screen"):"XYZ"[handleAxis].ToString())+"  "+(applied*180/MathF.PI).ToString("+0.0;-0.0;0.0")+"°";
            var textSize=ImGui.CalcTextSize(label);var at=Vector2.Clamp(io.MousePos+new Vector2(18,24)*DirectorImGuiTheme.Scale,min+new Vector2(8),Vector2.Max(min+new Vector2(8),min+size-textSize-new Vector2(8)));
            draw.AddRectFilled(at-new Vector2(6),at+textSize+new Vector2(6),0xdd242424,4);draw.AddText(at,0xffeeeeee,label);
        }
        else
        {
            if(translationDrag is null || !translationDrag.TryOffset(io.MousePos,out var offset)) return false;
            float amount=Vector3.Dot(offset,handleDirection);
            if(handleOperation==OPERATION.SCALE)
            {
                float factor=Math.Max(.01f,1+amount/DirectorEditorHandles.Radius(handleUnits,false));
                var scale=Vector3.One;scale[handleAxis]=factor;result=Matrix4x4.CreateScale(scale)*handleStart;
            }
            else
            {
                if(io.KeyShift) offset*=.1f;
                if(io.KeyCtrl)
                {
                    var snapped=Vector3.Zero;
                    for(int i=0;i<3;i++) if(handleAxis<3?i==handleAxis:i!=handleAxis-3)
                        snapped+=handleBasis[i]*(MathF.Round(Vector3.Dot(offset,handleBasis[i])*10)/10);
                    offset=snapped;
                }
                result=handleStart*Matrix4x4.CreateTranslation(offset);
            }
        }
        return result!=world;
    }
    private float PointerAngle(Vector2 mouse)
    {
        var point=handleRight*(mouse.X-handleCenter.X)-handleUp*(mouse.Y-handleCenter.Y);
        float denominator=Vector3.Dot(handleView,handleDirection);
        if(Math.Abs(denominator)<.08f) return (mouse.X-handlePointer.X-mouse.Y+handlePointer.Y)*.01f;
        point-=handleView*(Vector3.Dot(point,handleDirection)/denominator);
        var u=Vector3.Cross(handleDirection,Math.Abs(Vector3.Dot(handleDirection,handleUp))<.95f?handleUp:handleRight);u=Vector3.Normalize(u);
        var w=Vector3.Cross(handleDirection,u);return MathF.Atan2(Vector3.Dot(point,w),Vector3.Dot(point,u));
    }
    private void TransformObjectSelection(CreationObjectState active,Matrix4x4 manipulated)
    {
        var frame=Session.Frame!;if(!Matrix4x4.Invert(active.World,out var inverseActive)) return;
        var delta=inverseActive*manipulated;var selected=selectedObjects.Append(active.Source.Id).ToHashSet();
        var updates=new List<(Guid Id,CreationTransform Transform)>();
        foreach(var id in selected)
        {
            if(!frame.Objects.TryGetValue(id,out var state)) continue;
            bool inherited=false;var ancestor=state.Source.ParentId;
            for(int depth=0;ancestor!=Guid.Empty&&depth<128;depth++) { if(selected.Contains(ancestor)) { inherited=true;break; } ancestor=Project!.Objects.Find(o=>o.Id==ancestor)?.ParentId??Guid.Empty; }
            if(inherited) continue;
            Matrix4x4 parent=DirectorCreationParenting.Space(frame,state.Source);
            if(!Matrix4x4.Invert(parent,out var inverse)) throw new InvalidOperationException("Pose transform is singular.");
            var near=state.Transform;var tr=DirectorCreationMath.FromMatrix(state.World*delta*inverse,near.Rotation,near.Scale);tr.Pivot=near.Pivot;
            tr.Position-=near.Pivot-CreationVector.From(Vector3.TransformNormal(near.Pivot.Float(),Matrix4x4.CreateScale(tr.Scale.Float())*CreationTransform.RotationMatrix(tr.Rotation)));
            var expected=state.World*delta*inverse;var actual=tr.Matrix();
            foreach(var point in new[]{Vector3.Zero,Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ})
                if(Vector3.Distance(Vector3.Transform(point,expected),Vector3.Transform(point,actual))>.002f) throw new InvalidOperationException("Pose cannot represent this transform without shear.");
            if(!Session.AnimationEditing)
            {
                var basis=state.Source.Transform;
                tr=tr with { Position=basis.Position+(tr.Position-near.Position),Rotation=basis.Rotation+(tr.Rotation-near.Rotation),
                    Scale=basis.Scale+(tr.Scale-near.Scale),Pivot=basis.Pivot };
            }
            updates.Add((id,tr));
        }
        foreach(var update in updates) Session.SetTransform(update.Id,Guid.Empty,update.Transform);
    }
}
