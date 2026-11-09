using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckCameraHandles(Assembly assembly, Action<string> capture)
    {
        Type type = assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud", true)!;
        Type viewport = assembly.GetType("VSDirector.Rendering.DirectorWorkspaceViewportRuntime", true)!;
        object hud = RuntimeHelpers.GetUninitializedObject(type);
        double[] view = new double[16], projection = new double[16];
        bool[] keys = new bool[512];
        var input = ProbeDispatchProxy.Create<IInputAPI>((m, _) => m.Name == "get_KeyboardKeyStateRaw"
            ? keys : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var render = ProbeDispatchProxy.Create<IRenderAPI>((m, _) => m.Name switch
        {
            "get_FrameWidth" => 1920, "get_FrameHeight" => 1080,
            "get_PerspectiveViewMat" => view, "get_PerspectiveProjectionMat" => projection,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_Render" => render, "get_Input" => input, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        void Set(string name, object? value) => AccessTools.Field(type, name).SetValue(hud, value);
        object? Call(string name, params object?[] args) => AccessTools.Method(type, name).Invoke(hud, args);
        Set("capi", api);
        Set("cameraDragAxisIndex", -1); Set("cameraHoverAxis", -1);
        foreach (string name in new[] { "cameraToolLines", "cameraToolTriangles", "cameraToolHeads" })
            Set(name, Activator.CreateInstance(AccessTools.Field(type, name).FieldType));
        var faces = (IList)AccessTools.Field(type, "cameraToolTriangles").GetValue(hud)!;
        var lines = (IList)AccessTools.Field(type, "cameraToolLines").GetValue(hud)!;
        var heads = (IList)AccessTools.Field(type, "cameraToolHeads").GetValue(hud)!;
        object Mode(string name) => Enum.Parse(AccessTools.Field(type, "cameraTool").FieldType, name);
        void Clear() { faces.Clear(); lines.Clear(); heads.Clear(); }
        void Configure(Vector3 eye, double worldX = 0, float width = 960, float height = 640, float x = 0, float y = 0, float distance = 4)
        {
            Vector3 back = Vector3.Normalize(eye), right = Vector3.Normalize(Vector3.Cross(Math.Abs(back.Y) > .99 ? Vector3.UnitZ : Vector3.UnitY, back));
            Vector3 up = Vector3.Cross(back, right);
            Array.Clear(view); Array.Clear(projection);
            view[0] = right.X; view[4] = right.Y; view[8] = right.Z;
            view[1] = up.X; view[5] = up.Y; view[9] = up.Z;
            view[2] = back.X; view[6] = back.Y; view[10] = back.Z;
            view[12] = -right.X * worldX; view[13] = -up.X * worldX;
            view[14] = -back.X * worldX - distance; view[15] = 1;
            projection[0] = 1080d / 1920; projection[5] = 1;
            projection[10] = -1.002; projection[11] = -1; projection[14] = -.2002;
            AccessTools.Method(viewport, "Configure").Invoke(null, [hud, api, (int)x, (int)y, (int)width, (int)height, false]);
        }
        Vector2 Project(Vec3d point)
        {
            object?[] args = [point, null];
            if (!(bool)Call("ProjectCameraPoint", args)!) throw new Exception("Handle test point was clipped.");
            return (Vector2)args[1]!;
        }
        Vector2 FaceCenter(object face) => ((Vector2)AccessTools.Property(face.GetType(), "A").GetValue(face)!
            + (Vector2)AccessTools.Property(face.GetType(), "B").GetValue(face)!
            + (Vector2)AccessTools.Property(face.GetType(), "C").GetValue(face)!) / 3;
        var io = ImGui.GetIO();
        Vector2 display = io.DisplaySize;
        float savedScale = RuntimeEnv.GUIScale;
        try
        {
            io.DisplaySize = new(960, 640);
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            foreach (var eye in new[] { new Vector3(3, 2, 4), new Vector3(-3, -2, -4), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            {
                RuntimeEnv.GUIScale = scale;
                Configure(eye);
                var facing = (Vector3)Call("CameraViewDirection", new Vec3d())!;
                if (Vector3.Distance(facing, Vector3.Normalize(eye)) > .0001)
                    throw new Exception("Rotation visibility does not follow the editor view matrix.");
                for (int axis = 0; axis < 3; axis++)
                {
                    Clear();
                    ImGui.NewFrame();
                    var draw = ImGui.GetForegroundDrawList();
                    Call("DrawCameraRotationRing", draw, new Vec3d(), .475f, axis, 0xff77cf75u, facing);
                    ImGui.Render();
                    if (lines.Count != 96 || faces.Count != 64 || ImGui.GetDrawData().TotalVtxCount == 0)
                        throw new Exception("A rotation ring or its solid double cone is missing.");
                    int pickable = 0;
                    foreach (object face in faces)
                    {
                        Vector2 a = (Vector2)AccessTools.Property(face.GetType(), "A").GetValue(face)!;
                        Vector2 b = (Vector2)AccessTools.Property(face.GetType(), "B").GetValue(face)!;
                        Vector2 c = (Vector2)AccessTools.Property(face.GetType(), "C").GetValue(face)!;
                        if (Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) < .01f) continue;
                        if ((int)Call("CameraHandleAxisAt", FaceCenter(face), 0f)! != axis)
                            throw new Exception("A rotation double-cone face cannot be picked.");
                        pickable++;
                    }
                    if (pickable == 0) throw new Exception("The rotation double cone collapsed from this view.");
                    float RingDistance(Vector2 point) => lines.Cast<object>().Min(line => (float)Call("CameraLineDistance", point,
                        (Vector2)AccessTools.Property(line.GetType(), "A").GetValue(line)!,
                        (Vector2)AccessTools.Property(line.GetType(), "B").GetValue(line)!)!);
                    var firstTip = (Vector2)AccessTools.Property(faces[0]!.GetType(), "A").GetValue(faces[0])!;
                    if (RingDistance(firstTip) > .2f * scale)
                        throw new Exception("The rotation double-cone tip left the ring.");
                    for (int i = 0; i < draw.VtxBuffer.Size; i++)
                        if ((draw.VtxBuffer[i].col & 0x00ffffff) != 0x0077cf75)
                            throw new Exception("Rotation handle rendering introduced lighting or a dark outline.");
                    Clear();
                    ImGui.NewFrame();
                    Call("DrawCameraPivotBox", ImGui.GetForegroundDrawList(), new Vec3d(), .475f, axis, 0xff77cf75u);
                    ImGui.Render();
                    if (faces.Count < 4 || faces.Cast<object>().Any(f => (int)Call("CameraHandleAxisAt", FaceCenter(f), 0f)! != axis))
                        throw new Exception("A pivot cube cannot be picked across its filled faces.");
                    Clear();
                    ImGui.NewFrame();
                    Call("DrawCameraMovePlane", ImGui.GetForegroundDrawList(), new Vec3d(), .475f, axis, 0xff77cf75u, facing);
                    ImGui.Render();
                    float component = axis == 0 ? facing.X : axis == 1 ? facing.Y : facing.Z;
                    if (Math.Abs(component) > .08 && (faces.Count != 2
                        || (int)Call("CameraHandleAxisAt", FaceCenter(faces[0]!), 0f)! != axis + 3))
                        throw new Exception("A visible move plane cannot be picked inside its square.");
                    if (Math.Abs(component) < .08 && faces.Count != 0)
                        throw new Exception("An edge-on plane remains selectable.");
                }
                Vector2 p = (Vector2)Call("CameraReadoutPosition", new Vector2(100, 100), new Vector2(180, 24), Vector2.Zero, new Vector2(960, 640), scale)!;
                Vector2 edge = (Vector2)Call("CameraReadoutPosition", new Vector2(959, 639), new Vector2(180, 24), Vector2.Zero, new Vector2(960, 640), scale)!;
                if (p.X <= 100 || p.Y <= 100 || edge.X + 180 > 960 || edge.Y + 24 > 640)
                    throw new Exception("Angle feedback does not stay beside the mouse and within the viewport.");
            }
            // The body has fixed world dimensions. Undoing perspective on the
            // front body edge must recover the same width across the old screen
            // size thresholds, including close-ups and distant views.
            float? worldWidth = null;
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                float previousWidth = float.PositiveInfinity;
                foreach (float distance in new[] { .5f, 1f, 1.4f, 1.9f, 2.7f, 2.9f, 4f, 6.5f, 9f, 13f, 14f, 100f, 1000f })
                {
                    Configure(Vector3.UnitZ, distance: distance);
                    Clear(); ImGui.NewFrame();
                    Call("DrawCameraHead", ImGui.GetForegroundDrawList(), Guid.NewGuid(), new CameraPreset(), 0xffffffffu);
                    ImGui.Render();
                    var edge = lines[0]!;
                    Vector2 a = (Vector2)AccessTools.Property(edge.GetType(), "A").GetValue(edge)!;
                    Vector2 b = (Vector2)AccessTools.Property(edge.GetType(), "B").GetValue(edge)!;
                    float width = Vector2.Distance(a, b);
                    float recovered = width * (distance + .12f);
                    worldWidth ??= recovered;
                    if (width >= previousWidth || Math.Abs(recovered / worldWidth.Value - 1) > .002f)
                        throw new Exception($"Camera body changed world size with distance or UI scale: distance={distance}, scale={scale}, width={width}, recovered={recovered}.");
                    previousWidth = width;
                }
            }
            Console.WriteLine("PASS camera body retains fixed world dimensions from 0.5 to 1000 blocks at 100/150/200% UI scale; perspective size decreases naturally");
            // Close and distant rotation handles must keep a usable screen size.
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            foreach (float distance in new[] { .05f, 1f, 4f, 100000f })
            {
                RuntimeEnv.GUIScale = scale;
                Configure(new(3, 2, 4), distance: distance);
                float radius = (float)Call("CameraHandleRadius", new Vec3d())!;
                var facing = (Vector3)Call("CameraViewDirection", new Vec3d())!;
                for (int axis = 0; axis < 3; axis++)
                {
                    Clear(); ImGui.NewFrame();
                    Call("DrawCameraRotationRing", ImGui.GetForegroundDrawList(), new Vec3d(), radius, axis, 0xff77cf75u, facing);
                    ImGui.Render();
                    Vector2 Corner(object face, string name) => (Vector2)AccessTools.Property(face.GetType(), name).GetValue(face)!;
                    Vector2 tip = Corner(faces[0]!, "A");
                    Vector2[] vertices = faces.Cast<object>().SelectMany(face => new[] { Corner(face, "A"), Corner(face, "B"), Corner(face, "C") }).ToArray();
                    Vector2 end = vertices.MaxBy(p => Vector2.DistanceSquared(p, tip));
                    float span = Vector2.Distance(tip, end) / scale;
                    Vector2 tangent = Vector2.Normalize(end - tip), transverse = new(-tangent.Y, tangent.X);
                    float width = (vertices.Max(p => Vector2.Dot(p, transverse)) - vertices.Min(p => Vector2.Dot(p, transverse))) / scale;
                    if (span < 28 || span > 32 || width < 8 || width > 14)
                        throw new Exception($"Rotation double-cone screen size changed with distance: distance={distance}, axis={axis}, scale={scale}, span={span}, width={width}.");
                }
            }
            // Circular sections taper to two tips on the ring. The cone axis
            // must remain straight, while the endpoints follow rotation.
            for (int axis = 0; axis < 3; axis++)
            foreach (float angle in new[] { -.7f, .4f, 2.1f })
            foreach (float along in new[] { -1f, -.5f, 0f, .5f, 1f })
            {
                Vector3 Point(float around, float? position = null)
                {
                    var p = (Vec3d)Call("CameraRotationHandlePoint", new Vec3d(), .475f, axis,
                        angle, .4f, .03f, position ?? along, around)!;
                    return new((float)p.X, (float)p.Y, (float)p.Z);
                }
                Vector3 centre = (Point(0) + Point(MathF.PI)) / 2;
                Vector3 start = Point(0, -1), end = Point(0, 1);
                float expectedWidth = .03f * (1 - Math.Abs(along));
                if (Math.Abs(start.Length() - .475f) > .00001 || Math.Abs(end.Length() - .475f) > .00001
                    || Vector3.Distance(centre, Vector3.Lerp(start, end, (along + 1) / 2)) > .00001
                    || Math.Abs(Vector3.Dot(Point(0) - centre, end - start)) > .00001
                    || Math.Abs(Vector3.Dot(Point(MathF.PI / 2) - centre, end - start)) > .00001
                    || Math.Abs(Vector3.Distance(Point(0), centre) - expectedWidth) > .00001
                    || Math.Abs(Vector3.Distance(Point(MathF.PI / 2), centre) - expectedWidth) > .00001
                    || Vector3.Distance(centre, (Point(MathF.PI / 2) + Point(3 * MathF.PI / 2)) / 2) > .00001)
                    throw new Exception("Rotation double-cone cross sections lost their straight axis, ring tips or circular taper.");
            }
            RuntimeEnv.GUIScale = 1;
            foreach (double worldX in new[] { 0d, 30000000.125 })
            foreach (bool snap in new[] { false, true })
            foreach (string mode in new[] { "Move", "Pivot" })
            for (int normal = 0; normal < 3; normal++)
            {
                Configure(new(3, 2, 4), worldX);
                keys[(int)GlKeys.LControl] = snap;
                var original = new CameraPreset { X = worldX };
                Vector3 offset = normal == 0 ? new(0, .75f, .5f) : normal == 1 ? new(.75f, 0, .5f) : new(.75f, .5f, 0);
                Vector2 pointer = Project(new(worldX + offset.X, offset.Y, offset.Z));
                var clip = new DirectorReplayViewportClip { Id = Guid.NewGuid(), CameraPose = original.DeepClone() };
                Set("cameraTool", Mode(mode)); Set("cameraToolSelection", clip.Id);
                Set("cameraDragOriginal", original); Set("cameraDragPose", original.DeepClone());
                Set("cameraDragTimeline", new DirectorReplayViewportTimeline { Clips = [clip] });
                Set("cameraDragAxisIndex", normal + 3); Set("cameraDragAxis", normal == 0 ? Vector3.UnitX : normal == 1 ? Vector3.UnitY : Vector3.UnitZ);
                Set("cameraDragPlanePoint", Vector3.Zero);
                if (!(bool)Call("MoveCameraToolDrag", (int)Math.Round(pointer.X), (int)Math.Round(pointer.Y))!)
                    throw new Exception("Plane drag was not consumed.");
                var result = clip.CameraPose!;
                Vector3 moved = mode == "Move" ? new((float)(result.X - original.X), (float)result.Y, (float)result.Z)
                    : new((float)result.EditorPivotOffsetX, (float)result.EditorPivotOffsetY, (float)result.EditorPivotOffsetZ);
                if (Vector3.Distance(offset, moved) > (snap ? .0001f : .025f)
                    || Math.Abs(normal == 0 ? moved.X : normal == 1 ? moved.Y : moved.Z) > .000001
                    || mode == "Pivot" && (result.X != original.X || result.Y != original.Y || result.Z != original.Z))
                    throw new Exception($"Plane drag changed the wrong coordinates: {mode}/{normal}/snap={snap}, {moved}.");
            }
            keys[(int)GlKeys.LControl] = false;
            Set("cameraDragOriginal", null); Set("cameraDragPose", null); Set("cameraDragAxisIndex", -1);
            ImGui.NewFrame();
            string[] modes = ["Rotate", "Move", "Pivot"];
            for (int row = 0; row < 2; row++)
            for (int column = 0; column < 3; column++)
            {
                Clear();
                Configure(row == 0 ? new(3, 2, 4) : new(-4, 3, -2), width: 320, height: 280, x: column * 320, y: 50 + row * 280, distance: 1.5f);
                Set("cameraTool", Mode(modes[column]));
                Call("DrawCameraHead", ImGui.GetForegroundDrawList(), Guid.Empty, new CameraPreset(),
                    (uint)AccessTools.Field(type, "CameraOutlineColor").GetRawConstantValue()!);
                Call("DrawCameraHandles", ImGui.GetForegroundDrawList(), new CameraPreset());
                ImGui.GetForegroundDrawList().AddText(new Vector2(column * 320 + 20, row * 280 + 60), 0xffffffff, modes[column]);
            }
            ImGui.Render(); capture("camera-handles");
            var layout = new DirectorWorkspaceDocument();
            var panel = layout.Panel("preview");
            panel.X = panel.Y = 0; panel.Width = 960; panel.Height = 640;
            Set("layout", layout);
            Set("cameraTool", Mode("Rotate")); Set("cameraDragAxisIndex", 1);
            Set("cameraDragAxis", Vector3.UnitY); Set("cameraDragStartDirection", Vector3.UnitX);
            Set("cameraDragAngle", MathF.PI / 6);
            Configure(new(3, 2, 4)); Clear();
            io.MousePos = new(550, 350);
            ImGui.NewFrame();
            Call("DrawCameraHandles", ImGui.GetForegroundDrawList(), new CameraPreset { Yaw = 30 });
            ImGui.Render(); capture("camera-rotation-feedback");
            Console.WriteLine("PASS unlit straight 3D rotation double cones with tips on arcs and face picking, pivot cubes, XY/XZ/YZ plane picking and actual pointer drags/snap at large coordinates, mouse angle badge bounds and DPI 100/150/200%");
        }
        finally
        {
            RuntimeEnv.GUIScale = savedScale; io.DisplaySize = display;
            AccessTools.Method(viewport, "Deactivate").Invoke(null, [hud]);
        }
    }
}
