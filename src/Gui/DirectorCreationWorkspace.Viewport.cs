using static VSDirector.Gui.DirectorCreationText;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using ImGuizmoNET;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using VSDirector.Rendering;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private OPERATION operation = OPERATION.TRANSLATE;
    private MODE transformMode = MODE.WORLD;
    private bool gizmoUsing, gizmoHover;
    // VSImGui 1.2.7's managed IsOver() imports ImGuizmo_IsOverNil, but its bundled
    // native library exports ImGuizmo_IsOver_Nil.
    [DllImport("cimguizmo", EntryPoint = "ImGuizmo_IsOver_Nil", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte IsGizmoOver();
    private readonly List<(Guid Id, Vector2 Point)> objectPoints = new();
    internal bool CapturePointer(int x, int y) => Active && Project is not null && (gizmoUsing || gizmoHover || objectPoints.Any(p => Vector2.Distance(p.Point, new(x, y)) < 12 * DirectorImGuiTheme.Scale));
    internal bool Manipulating => Active && gizmoUsing;
    private void DrawViewportTools()
    {
        gizmoHover = false; objectPoints.Clear();
        if (Project is not { } project || Session.Frame is not { } frame || !Runtime.Enabled || host.ViewportSize.X < 1) return;
        Vector2 min = host.ViewportPosition, size = host.ViewportSize;
        var draw = ImGui.GetWindowDrawList(); draw.PushClipRect(min, min + size, true);
        try
        {
            var engineView = api.Render.PerspectiveViewMat;
            float[] view = DirectorCreationMath.ProjectView(engineView, project.Origin, project.Dimension * (double)BlockPos.DimensionBoundary),
                projection = api.Render.PerspectiveProjectionMat.Select(d => (float)d).ToArray();
            Matrix4x4 Matrix(float[] v) => new(v[0],v[1],v[2],v[3],v[4],v[5],v[6],v[7],v[8],v[9],v[10],v[11],v[12],v[13],v[14],v[15]);
            Matrix4x4 vp = Matrix(view) * Matrix(projection);
            Vector2? Screen(Vector3 point)
            {
                Vector4 c = Vector4.Transform(new Vector4(point,1), vp);
                if (c.W <= .001f) return null;
                return min + new Vector2((c.X/c.W+1)*.5f*size.X,(1-c.Y/c.W)*.5f*size.Y);
            }
            void Line(Vector3 a, Vector3 b, uint color) { if (Screen(a) is { } s && Screen(b) is { } e) draw.AddLine(s,e,color); }
            foreach (var state in frame.Objects.Values.Where(s => s.Visible || s.Source.Kind == CreationObjectKind.Camera))
            {
                if (Screen(state.World.Translation) is not { } point || point.X < min.X || point.Y < min.Y || point.X > min.X+size.X || point.Y > min.Y+size.Y) continue;
                objectPoints.Add((state.Source.Id,point));
                bool selected = state.Source.Id == Session.SelectedObject;
                draw.AddCircleFilled(point, selected ? 5 : 4, selected ? 0xfff6c565 : 0xffbcbcbc);
                if (selected) draw.AddText(point + new Vector2(8,8),0xffeeeeee,state.Source.Name);
                if (state.Source.Kind == CreationObjectKind.Camera)
                {
                    Vector3[] corners = [new(-.22f,-.15f,-.4f),new(.22f,-.15f,-.4f),new(.22f,.15f,-.4f),new(-.22f,.15f,-.4f)];
                    for (int i = 0; i < 4; i++) { Vector3 a = Vector3.Transform(corners[i],state.World), b = Vector3.Transform(corners[(i+1)%4],state.World); Line(a,b,selected?0xffffffff:0xff111111); Line(state.World.Translation,a,selected?0xffffffff:0xff111111); }
                }
            }
            bool popup = ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);
            if (!popup && modelContext.Length == 0 && SelectedObject is { } obj && frame.Objects.TryGetValue(obj.Id, out var selectedState))
            {
                ImGuizmo.SetImGuiContext(ImGui.GetCurrentContext());
                ImGuizmo.BeginFrame(); ImGuizmo.SetDrawlist(draw); ImGuizmo.SetRect(min.X,min.Y,size.X,size.Y);
                Matrix4x4 parent = Matrix4x4.Identity;
                if (frame.Objects.TryGetValue(obj.ParentId,out var parentState)) { parent = parentState.World; if (parentState.Bones.TryGetValue(obj.ParentPartId,out var attachment)) parent = attachment * parent; }
                var baseline = EvaluatedTransform(obj);
                Matrix4x4 world = selectedState.World;
                bool bone = Session.SelectedPart != Guid.Empty;
                if (bone && selectedState.Bones.TryGetValue(Session.SelectedPart,out var boneMatrix)) world = boneMatrix * world;
                float[] values = DirectorCreationRenderer.Array(world);
                bool changed = ImGuizmo.Manipulate(ref view[0],ref projection[0],operation,transformMode,ref values[0]);
                bool usingNow = ImGuizmo.IsUsing(); gizmoHover = IsGizmoOver() != 0;
                if (usingNow && !gizmoUsing) Session.BeginGesture();
                if (changed && usingNow)
                {
                    Matrix4x4 manipulated = Matrix(values);
                    if (!bone && Matrix4x4.Invert(parent,out var inverse))
                    {
                        var tr = DirectorCreationMath.FromMatrix(manipulated * inverse,baseline.Rotation);
                        tr.Pivot = baseline.Pivot;
                        // Decomposition returns matrix translation; compensate for the stored pivot.
                        Vector3 pivotShift = Vector3.TransformNormal(baseline.Pivot.Float(), Matrix4x4.CreateScale(tr.Scale.Float()) * CreationTransform.RotationMatrix(tr.Rotation));
                        tr.Position -= baseline.Pivot - CreationVector.From(pivotShift);
                        SetTransform(obj, tr);
                    }
                    else if (bone)
                    {
                        if (CurrentModel is { } model && SelectedPart is { } part)
                        {
                            Matrix4x4 boneParent = selectedState.World;
                            if (selectedState.Bones.TryGetValue(part.ParentId, out var parentBone)) boneParent = parentBone * boneParent;
                            if (Matrix4x4.Invert(boneParent, out var inverseBoneParent))
                                SetTransform(obj, DirectorCreationMath.PoseFromMatrix(part, manipulated * inverseBoneParent, baseline, model.AnimationVersion));
                        }
                    }
                    Session.Changed();
                }
                if (!usingNow && gizmoUsing) Session.EndGesture(L("creation-gizmo-transform")); gizmoUsing = usingNow;
            }
            if (!popup && !gizmoUsing && !gizmoHover && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                && ImGui.IsMouseHoveringRect(min, min + size) && ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
            {
                var nearest = objectPoints.OrderBy(p => Vector2.DistanceSquared(p.Point,ImGui.GetIO().MousePos)).FirstOrDefault();
                if (nearest.Id != Guid.Empty && Vector2.Distance(nearest.Point,ImGui.GetIO().MousePos) < 12 * DirectorImGuiTheme.Scale && project.Objects.Find(o => o.Id == nearest.Id) is { } picked) Select(picked);
            }
        }
        finally { draw.PopClipRect(); }
        bool blocked = ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);
        if (!blocked && !gizmoUsing && ImGui.IsMouseClicked(ImGuiMouseButton.Right)
            && ImGui.IsMouseHoveringRect(min, min + size)
            && (!ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) || ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows)))
        {
            var nearest = objectPoints.OrderBy(p => Vector2.DistanceSquared(p.Point, ImGui.GetIO().MousePos)).FirstOrDefault();
            if (nearest.Id != Guid.Empty && Vector2.Distance(nearest.Point, ImGui.GetIO().MousePos) < 18 * DirectorImGuiTheme.Scale)
                if (project.Objects.Find(o => o.Id == nearest.Id) is { } picked) Select(picked);
            ImGui.OpenPopup("creation-viewport-context");
        }
        ConstrainMenu();
        if (ImGui.BeginPopup("creation-viewport-context"))
        {
            foreach (var tool in new[] { (L("creation-move"), OPERATION.TRANSLATE), (L("creation-rotation"), OPERATION.ROTATE), (L("creation-scale"), OPERATION.SCALE) })
                if (ImGui.MenuItem(tool.Item1, "", operation == tool.Item2)) operation = tool.Item2;
            if (ImGui.MenuItem(L("creation-local-axes"), "", transformMode == MODE.LOCAL)) transformMode = transformMode == MODE.WORLD ? MODE.LOCAL : MODE.WORLD;
            Item(L("creation-preview"), () => Runtime.Preview = !Runtime.Preview);
            if (SelectedObject is { } obj)
            {
                Item(L("creation-add-transform-key"), () => { var tr = EvaluatedTransform(obj); AddKey(CreationChannel.Position, tr.Position); AddKey(CreationChannel.Rotation, tr.Rotation); AddKey(CreationChannel.Scale, tr.Scale); });
                ImGui.Separator(); DrawObjectContext(obj);
            }
            ImGui.EndPopup();
        }
    }
}
