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
    private bool posePull;
    private Guid hoveredPosePart, hoveredSceneObject, hoveredScenePart;
    // VSImGui 1.2.7's managed IsOver() imports ImGuizmo_IsOverNil, but its bundled
    // native library exports ImGuizmo_IsOver_Nil.
    [DllImport("cimguizmo", EntryPoint = "ImGuizmo_IsOver_Nil", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte IsGizmoOver();
    private readonly List<(Guid Id, Vector2 Point)> objectPoints = new();
    private readonly List<(Guid Id, Vector2 Point)> bonePoints = new();
    private bool showBones;
    internal bool CapturePointer(int x, int y) => Active && Project is not null && (hoveredPosePart != Guid.Empty || hoveredSceneObject != Guid.Empty || gizmoUsing || gizmoHover || objectPoints.Concat(bonePoints).Any(p => Vector2.Distance(p.Point, new(x, y)) < 12 * DirectorImGuiTheme.Scale));
    internal bool Manipulating => Active && gizmoUsing;
    private (Vector2 Position, Vector2 Size) GameImageRect()
    {
        // The world image is letterboxed inside the dock, not stretched to it.
        // Use the same physical mapping as the final framebuffer blit.
        if (DirectorWorkspaceViewportRuntime.TryMapGamePointToPhysical(0, 0, out double x, out double y)
            && DirectorWorkspaceViewportRuntime.TryMapGamePointToPhysical(api.Render.FrameWidth, api.Render.FrameHeight, out double right, out double bottom))
            return (new((float)x, (float)y), new((float)(right - x), (float)(bottom - y)));
        return (host.ViewportPosition, host.ViewportSize);
    }
    private void DrawViewportTools()
    {
        if(gizmoUsing && ImGui.IsKeyPressed(ImGuiKey.Escape,false)) { Session.CancelGesture();gizmoUsing=false;handleAxis=-1;return; }
        if (gizmoUsing && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) { Session.EndGesture(L("creation-gizmo-transform")); gizmoUsing=false; handleAxis=-1; }
        gizmoHover = false; hoveredPosePart = hoveredSceneObject = hoveredScenePart = Guid.Empty; objectPoints.Clear(); bonePoints.Clear();
        if (Project is not { } project || Session.Frame is not { } frame || !Runtime.Enabled || host.ViewportSize.X < 1) return;
        var (min, size) = GameImageRect();
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
            void Line(Vector3 a, Vector3 b, uint color) { if (Screen(a) is { } s && Screen(b) is { } e) draw.AddLine(s,e,color,1.5f*DirectorImGuiTheme.Scale); }
            var rigActor = RigActor;
            if (PosePicking && rigActor is not null && frame.Objects.TryGetValue(rigActor.Id, out var pickState)
                && project.Models.Find(m => m.Id == rigActor.ModelId) is { } pickModel)
            {
                bool hover = !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
                    && ImGui.IsMouseHoveringRect(min, min + size)
                    && (!ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) || ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows));
                if (hover && !gizmoUsing && Matrix4x4.Invert(vp, out var inverseVp))
                {
                    var mouse = (ImGui.GetIO().MousePos - min) / size;
                    var a = Vector4.Transform(new Vector4(mouse.X * 2 - 1, 1 - mouse.Y * 2, -1, 1), inverseVp);
                    var b = Vector4.Transform(new Vector4(mouse.X * 2 - 1, 1 - mouse.Y * 2, .9f, 1), inverseVp);
                    var near = new Vector3(a.X,a.Y,a.Z) / a.W; var far = new Vector3(b.X,b.Y,b.Z) / b.W;
                    hoveredPosePart = DirectorCreationPoseGeometry.Pick(pickModel,pickState,near,Vector3.Normalize(far-near));
                    if (!exactPoseParts) hoveredPosePart = PoseTarget(pickModel,hoveredPosePart);
                }
                Guid selectedPosePart = Session.SelectedObject == rigActor.Id ? Session.SelectedPart : Guid.Empty;
                if (!exactPoseParts)
                {
                    Runtime.Renderer.AdditionalSelectedParts=selectedBones;
                    Runtime.Renderer.SemanticTargets=PoseTargets(pickModel);
                    int texture = Runtime.Renderer.PoseSelectionTexture(pickModel, pickState, selectedPosePart,
                        gizmoUsing ? Guid.Empty : hoveredPosePart, view, projection, size, DirectorImGuiTheme.Scale);
                    if (texture > 0) draw.AddImage((nint)texture, min, min + size, new(0, 1), new(1, 0));
                }
                else if (pickState.Visible && pickModel.Parts.Find(p => p.Id == selectedPosePart && !p.Group && p.Visible
                    && !pickState.HiddenParts.Contains(p.Id)) is { } part && pickState.Bones.TryGetValue(part.Id, out var bone))
                {
                    // Precision mode exposes only the selected cube, never all
                    // descendants, and uses the same deformed geometry as rendering.
                    var deformations = DirectorCreationJointSeams.Deformations(pickModel, pickState);
                    var extent = ((part.To - part.From) * (1d / 16)).Float();
                    float inflate = (float)part.Inflate / 16;
                    var points = new Vector2?[8];
                    for (int i = 0; i < 8; i++)
                    {
                        var local = new Vector3((i & 1) == 0 ? -inflate : extent.X + inflate,
                            (i & 2) == 0 ? -inflate : extent.Y + inflate, (i & 4) == 0 ? -inflate : extent.Z + inflate);
                        if (deformations.TryGetValue(part.Id, out var vertices) && vertices.TryGetValue(local, out var deformed)) local = deformed;
                        points[i] = Screen(Vector3.Transform(local, bone * pickState.World));
                    }
                    for (int i = 0; i < 8; i++)
                    for (int bit = 1; bit <= 4; bit *= 2)
                        if ((i & bit) == 0 && points[i] is { } a && points[i | bit] is { } b)
                            draw.AddLine(a, b, 0xff70d1ff, 1.5f * DirectorImGuiTheme.Scale);
                }
                if (!gizmoUsing && pickState.Visible && pickModel.Parts.Find(p => p.Id == hoveredPosePart) is { } hoveredPart)
                {
                    float scale = DirectorImGuiTheme.Scale;
                    string label=BoneName(pickModel,hoveredPart);
                    Vector2 padding = new(5 * scale, 3 * scale), textSize = ImGui.CalcTextSize(label);
                    Vector2 position = Vector2.Max(min + padding, Vector2.Min(ImGui.GetIO().MousePos + new Vector2(14, 18) * scale, min + size - textSize - padding * 2));
                    draw.AddRectFilled(position - padding, position + textSize + padding, 0xe6222222, 3 * scale);
                    draw.AddText(position, hoveredPosePart == selectedPosePart ? 0xff70d1ff : 0xffeed080, label);
                }
            }
            if (!PosePicking && modelContext.Length == 0 && Matrix4x4.Invert(vp,out var sceneInverse) && ImGui.IsMouseHoveringRect(min,min+size))
            {
                var mouse=(ImGui.GetIO().MousePos-min)/size;
                var a=Vector4.Transform(new Vector4(mouse.X*2-1,1-mouse.Y*2,-1,1),sceneInverse);
                var b=Vector4.Transform(new Vector4(mouse.X*2-1,1-mouse.Y*2,.9f,1),sceneInverse);
                var near=new Vector3(a.X,a.Y,a.Z)/a.W; var far=new Vector3(b.X,b.Y,b.Z)/b.W;
                float best=float.PositiveInfinity;
                foreach(var state in frame.Objects.Values.Where(s=>s.Visible))
                {
                    if(project.Models.Find(m=>m.Id==state.Source.ModelId) is not { } model) continue;
                    var hit=DirectorCreationPoseGeometry.Pick(model,state,near,Vector3.Normalize(far-near),out float distance);
                    if(hit!=Guid.Empty && distance<best)
                    { best=distance; hoveredSceneObject=state.Source.Id;hoveredScenePart=exactPoseParts?hit:PoseTarget(model,hit); }
                }
            }
            var activeChain = rigActor is null ? null : ActiveIkChain(rigActor);
            bool ShowControl(Guid id) => id==Session.SelectedObject || showBones && rigActor is not null && (showAllBones
                ? rigActor.IkChains.Any(c => c.Enabled && (c.Target == id || c.Pole == id))
                : activeChain?.Enabled == true && (activeChain.Target == id || activeChain.Pole == id));
            foreach (var state in frame.Objects.Values.Where(s => s.Visible || s.Source.Kind == CreationObjectKind.Camera))
            {
                if (IsIkControl(state.Source.Id) && !ShowControl(state.Source.Id)) continue;
                if (Screen(state.World.Translation) is not { } point || point.X < min.X || point.Y < min.Y || point.X > min.X+size.X || point.Y > min.Y+size.Y) continue;
                objectPoints.Add((state.Source.Id,point));
                bool selected = ObjectSelected(state.Source.Id);
                bool target = project.Objects.Any(o => o.IkChains.Any(c => c.Target == state.Source.Id));
                bool pole = project.Objects.Any(o => o.IkChains.Any(c => c.Pole == state.Source.Id));
                if (target || pole)
                {
                    float radius = 7 * DirectorImGuiTheme.Scale; uint color = target ? 0xff70d1ff : 0xffd788eb;
                    draw.AddRect(point - new Vector2(radius), point + new Vector2(radius), color, 0, ImDrawFlags.None, selected ? 2 : 1);
                    draw.AddCircleFilled(point, 3, color);
                }
                else if (state.Source.Kind != CreationObjectKind.Camera) draw.AddCircleFilled(point, selected ? 5 : 4, selected ? 0xfff6c565 : 0xffbcbcbc);
                else if (selected)
                {
                    draw.AddCircleFilled(point,4*DirectorImGuiTheme.Scale,0xff55d9ff);
                    uint[] colors=[0xff6666ee,0xff77cf75,0xffedaa62];
                    for(int axis=0;axis<3;axis++) Line(state.World.Translation,state.World.Translation+(axis==0?Vector3.UnitX:axis==1?Vector3.UnitY:Vector3.UnitZ)*.35f,colors[axis]);
                }
                if (selected) draw.AddText(point + new Vector2(8,8),0xffeeeeee,ObjectName(state.Source));
                if (state.Source.Kind == CreationObjectKind.Camera)
                {
                    foreach (var edge in DirectorEditorGeometry.CameraOutline)
                        Line(Vector3.Transform(edge.A,state.World),Vector3.Transform(edge.B,state.World),selected?0xffffffff:0xff000000);

                }
            }
            if (PosePicking && showBones && rigActor is not null && project.Models.Find(m => m.Id == rigActor.ModelId) is { } rig
                && frame.Objects.TryGetValue(rigActor.Id, out var rigState))
            {
                bool InScope(CreationPart part) => showAllBones || (Session.SelectedPart != Guid.Empty && Session.SelectedObject == rigActor.Id
                    ? posePull && Session.PoseEditing ? (gizmoUsing && pullChain is not null ? pullChain : exactPoseParts ? DirectorCreationPoseGeometry.Chain(rig,Session.SelectedPart) : DirectorCreationRigSelection.Chain(project,rig,Session.SelectedPart,extendedPullChain?8:4)).Any(p => p.Id == part.Id) : part.Id == Session.SelectedPart
                    : activeChain is not null && (part.Id == activeChain.Upper || part.Id == activeChain.Lower));
                foreach (var part in rig.Parts.Where(InScope))
                {
                    var joint = Vector3.Transform(DirectorCreationIk.Joint(part, rigState, rig.AnimationVersion), rigState.World);
                    if (rig.Parts.Find(p => p.Id == part.ParentId) is { } parentPart && InScope(parentPart))
                        Line(Vector3.Transform(DirectorCreationIk.Joint(parentPart, rigState, rig.AnimationVersion), rigState.World), joint, 0xff85c6e8);
                    if (Screen(joint) is not { } point || point.X < min.X || point.Y < min.Y || point.X > min.X + size.X || point.Y > min.Y + size.Y) continue;
                    bonePoints.Add((part.Id, point));
                    bool selected = part.Id == Session.SelectedPart;
                    draw.AddCircleFilled(point, (selected ? 5 : 3) * DirectorImGuiTheme.Scale, selected ? 0xff70d1ff : 0xffe8e8e8);
                }
                foreach (var chain in rigActor.IkChains.Where(c => c.Enabled && (showAllBones || c == activeChain)))
                {
                    if (rig.Parts.Find(p => p.Id == chain.Lower) is not { } lower) continue;
                    var joint = Vector3.Transform(DirectorCreationIk.Joint(lower, rigState, rig.AnimationVersion), rigState.World);
                    var end = Vector3.Transform(chain.EndOffset.Float(), rigState.Bones[lower.Id] * rigState.World);
                    if (frame.Objects.TryGetValue(chain.Target, out var targetState)) Line(end, targetState.World.Translation, 0xff70d1ff);
                    if (frame.Objects.TryGetValue(chain.Pole, out var poleState)) Line(joint, poleState.World.Translation, 0xffd788eb);
                }
            }
            bool popup = ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);
            bool enterPart=ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)&&hoveredSceneObject!=Guid.Empty&&hoveredScenePart!=Guid.Empty;
            if (!popup && !selectionTool && !enterPart && modelContext.Length == 0 && SelectedObject is { } obj && (!PosePicking || Session.SelectedPart != Guid.Empty || IsIkControl(obj.Id)) && (!IsIkControl(obj.Id) || ShowControl(obj.Id)) && frame.Objects.TryGetValue(obj.Id, out var selectedState))
            {
                ImGuizmo.SetImGuiContext(ImGui.GetCurrentContext());
                ImGuizmo.BeginFrame(); ImGuizmo.SetDrawlist(draw); ImGuizmo.SetRect(min.X,min.Y,size.X,size.Y);
                Matrix4x4 parent = Matrix4x4.Identity;
                if (frame.Objects.TryGetValue(obj.ParentId,out var parentState)) { parent = parentState.World; if (parentState.Bones.TryGetValue(obj.ParentPartId,out var attachment)) parent = attachment * parent; }
                var baseline = EvaluatedTransform(obj);
                Matrix4x4 world = Matrix4x4.CreateTranslation(baseline.Pivot.Float()) * selectedState.World;
                bool bone = Session.SelectedPart != Guid.Empty;
                if (bone && selectedState.Bones.TryGetValue(Session.SelectedPart,out var boneMatrix) && SelectedPart is { } selectedPart && CurrentModel is { } selectedModel)
                    world = posePull && Session.PoseEditing
                        ? Matrix4x4.CreateTranslation(Vector3.Transform(selectedPart.Group ? DirectorCreationMath.BonePivot(selectedPart,baseline,selectedModel.AnimationVersion) : ((selectedPart.To-selectedPart.From)*(1d/32)).Float(), boneMatrix * selectedState.World))
                        : DirectorCreationMath.BoneHandle(selectedPart, baseline, selectedModel.AnimationVersion, boneMatrix * selectedState.World);
                bool changed=ManipulateCreation(draw,world,view,projection,min,size,!bone&&obj.Kind==CreationObjectKind.Camera,out var manipulated);
                bool usingNow=handleAxis>=0;gizmoHover=handleHover>=0;
                if(usingNow&&!gizmoUsing)
                {
                    Session.BeginGesture();pullStart=null;pullChain=null;
                    if(bone && posePull && CurrentModel is {} pullModel)BeginPull(pullModel,selectedState,Session.SelectedPart);
                }
                if(changed&&usingNow)
                {
                    if(!bone) TransformObjectSelection(selectedState,Matrix4x4.CreateTranslation(-baseline.Pivot.Float())*manipulated);
                    else if (bone)
                    {
                        if (CurrentModel is { } model && SelectedPart is { } part)
                        {
                            if (posePull && Session.PoseEditing)
                            {
                                if(pullStart is null)BeginPull(model,selectedState,part.Id);
                                var result = DirectorCreationPoseGeometry.Pull(model,pullStart!,part.Id,manipulated.Translation,pullChain!);
                                foreach (var pair in result) Session.SetPosePart(pair.Key,pair.Value);
                            }
                            else
                            {
                            Matrix4x4 boneParent = selectedState.World;
                            if (selectedState.Bones.TryGetValue(part.ParentId, out var parentBone)) boneParent = parentBone * boneParent;
                            if (Matrix4x4.Invert(boneParent, out var inverseBoneParent))
                                SetTransform(obj, DirectorCreationMath.PoseFromMatrix(part,
                                    DirectorCreationMath.BoneFromHandle(part, baseline, model.AnimationVersion, manipulated) * inverseBoneParent, baseline, model.AnimationVersion));
                            }
                        }
                    }
                    Session.Changed();
                }
                if (!usingNow && gizmoUsing) Session.EndGesture(L("creation-gizmo-transform")); gizmoUsing = usingNow;
            }
            if (!popup && !gizmoUsing && !gizmoHover && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                && ImGui.IsMouseHoveringRect(min, min + size)
                && (!ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) || ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows)))
            {
                var nearest = objectPoints.OrderBy(p => Vector2.DistanceSquared(p.Point,ImGui.GetIO().MousePos)).FirstOrDefault();
                var nearestBone = bonePoints.OrderBy(p => Vector2.DistanceSquared(p.Point, ImGui.GetIO().MousePos)).FirstOrDefault();
                bool controlHit = project.Objects.Any(o => o.IkChains.Any(c => c.Target == nearest.Id || c.Pole == nearest.Id))
                    && Vector2.Distance(nearest.Point, ioMouse()) < 10 * DirectorImGuiTheme.Scale;
                if (!controlHit && rigActor is not null && hoveredPosePart != Guid.Empty) SelectBone(rigActor.Id,hoveredPosePart);
                else if (!controlHit && rigActor is not null && nearestBone.Id != Guid.Empty && Vector2.Distance(nearestBone.Point, ioMouse()) < 10 * DirectorImGuiTheme.Scale)
                    SelectBone(rigActor.Id, nearestBone.Id);
                else if (hoveredSceneObject != Guid.Empty && project.Objects.Find(o=>o.Id==hoveredSceneObject) is { } sceneObject)
                { if(enterPart)EnterBodySelection(sceneObject,hoveredScenePart);else SelectObjectClick(sceneObject); }
                else if (nearest.Id != Guid.Empty && Vector2.Distance(nearest.Point, ioMouse()) < 12 * DirectorImGuiTheme.Scale && project.Objects.Find(o => o.Id == nearest.Id) is { } picked) SelectObjectClick(picked);
                Vector2 ioMouse() => ImGui.GetIO().MousePos;
            }
        }
        finally { draw.PopClipRect(); }
        bool blocked = ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);
        if (!blocked && !gizmoUsing && ImGui.IsMouseClicked(ImGuiMouseButton.Right)
            && ImGui.IsMouseHoveringRect(min, min + size)
            && (!ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) || ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows)))
        {
            var nearest = objectPoints.OrderBy(p => Vector2.DistanceSquared(p.Point, ImGui.GetIO().MousePos)).FirstOrDefault();
            if (hoveredSceneObject != Guid.Empty && project.Objects.Find(o=>o.Id==hoveredSceneObject) is { } surface) SelectObjectClick(surface,true);
            else if (nearest.Id != Guid.Empty && Vector2.Distance(nearest.Point, ImGui.GetIO().MousePos) < 18 * DirectorImGuiTheme.Scale)
                if (project.Objects.Find(o => o.Id == nearest.Id) is { } picked) SelectObjectClick(picked,true);
            ImGui.OpenPopup("creation-viewport-context");
        }
        ConstrainMenu();
        if (ImGui.BeginPopup("creation-viewport-context"))
        {
            if (ImGui.MenuItem(L("creation-show-bones"), "", showBones)) showBones = !showBones;
            foreach (var tool in new[] { ("creation-select-tool",CreationTool.Select),("creation-move",CreationTool.Move),("creation-rotation",CreationTool.Rotate),("creation-scale",CreationTool.Scale),("creation-pull",CreationTool.Pull) })
                if (ImGui.MenuItem(L(tool.Item1), "", ActiveTool == tool.Item2, ToolAvailable(tool.Item2))) ChooseTool(tool.Item2);
            bool screen=ActiveTool==CreationTool.Rotate&&(PartScope?poseRotationAxis:objectRotationAxis)==6;
            if (ImGui.MenuItem(L("creation-local-axes"), "", transformMode == MODE.LOCAL,!screen))
            {transformMode = transformMode == MODE.WORLD ? MODE.LOCAL : MODE.WORLD;if(PartScope)partSpace=transformMode;else objectSpace=transformMode;}
            if(RigActor is {} contextActor && ImGui.MenuItem(L("creation-part-scope"),"",PosePicking))EnterBodySelection(contextActor);
            if(PosePicking && ImGui.MenuItem(L("creation-object-scope")))LeaveBodySelection();
            Item(L("creation-preview"), () => Runtime.Preview = !Runtime.Preview);
            if (SelectedObject is { } obj)
            {
                if (Session.PoseEditing) { if (ImGui.MenuItem(L("creation-save-pose"))) Run(Session.SavePose); }
                else Item(L("creation-add-transform-key"), () => { var tr = EvaluatedTransform(obj); AddKey(CreationChannel.Position, tr.Position); AddKey(CreationChannel.Rotation, tr.Rotation); AddKey(CreationChannel.Scale, tr.Scale); });
                ImGui.Separator(); DrawObjectContext(obj);
            }
            ImGui.EndPopup();
        }
    }
}
