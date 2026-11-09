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
                    float component = axis == 0 ? facing.X : axis == 1 ? facing.Y : facing.Z;
                    bool faceOn = 1 - component * component < .001f;
                    if (lines.Count != (faceOn ? 96 : 192) || faces.Count != 64 || ImGui.GetDrawData().TotalVtxCount == 0)
                        throw new Exception("A rotation ring or its solid double cone is missing.");
                    Vector2 firstArc = (Vector2)AccessTools.Property(lines[0]!.GetType(), "A").GetValue(lines[0])!;
                    Vector2 lastArc = (Vector2)AccessTools.Property(lines[^1]!.GetType(), "B").GetValue(lines[^1])!;
                    if (Vector2.Distance(firstArc, lastArc) > .01f)
                        throw new Exception("A rotation ring hides its rear half instead of closing the full circle.");
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
                    uint dark = (uint)Call("DarkCameraRingColor", 0xff77cf75u)!;
                    if (!faceOn && !Enumerable.Range(0, draw.VtxBuffer.Size).Any(i => draw.VtxBuffer[i].col == dark))
                        throw new Exception("The rear arc is missing its dark translucent color.");
                    for (int i = draw.VtxBuffer.Size - 64 * 3; i < draw.VtxBuffer.Size; i++)
                        if (draw.VtxBuffer[i].col != 0xff77cf75u)
                            throw new Exception("Rotation double cone rendering introduced lighting or an outline.");
                    Vector3 normal = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
                    Vector3 front = facing - normal * Vector3.Dot(facing, normal);
                    if (front.LengthSquared() > .001f)
                    {
                        front = Vector3.Normalize(front) * .475f;
                        object?[] args = [new Vec3d(), new Vec3d(front.X, front.Y, front.Z), null];
                        if (!(bool)Call("ProjectCameraHandlePoint", args)!) throw new Exception("Front midpoint was clipped.");
                        var points = faces.Cast<object>().SelectMany(face => new[] { "A", "B", "C" }
                            .Select(name => (Vector2)AccessTools.Property(face.GetType(), name).GetValue(face)!)).ToArray();
                        Vector2 midpoint = (points.Aggregate(Vector2.Min) + points.Aggregate(Vector2.Max)) / 2;
                        // A straight double cone spans a chord, so its centre
                        // sits just inside the arc, on the same radial line.
                        if ((float)Call("CameraLineDistance", midpoint, Project(new Vec3d()), (Vector2)args[2]!)! > .1f)
                            throw new Exception("The double cone does not follow the front arc midpoint.");
                    }
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
            // The blue outer ring is circular in screen pixels and encloses
            // the projected model, including rotated heads and offset pivots.
            RuntimeEnv.GUIScale = 1;
            Set("cameraTool", Mode("Rotate"));
            foreach (var eye in new[] { new Vector3(3, 2, 4), new Vector3(-3, -2, -4), Vector3.UnitZ })
            foreach (float distance in new[] { .5f, 1f, 4f, 1000f })
            foreach (float yaw in new[] { 0f, 75f, 160f })
            {
                Configure(eye, distance: distance);
                Clear(); ImGui.NewFrame();
                var pose = new CameraPreset { Yaw = yaw, Pitch = 35, EditorPivotOffsetX = .3 };
                Guid id = Guid.NewGuid();
                Call("DrawCameraHead", ImGui.GetForegroundDrawList(), id, pose, 0xffffffffu);
                var body = lines.Cast<object>().ToArray();
                Call("DrawCameraHandles", ImGui.GetForegroundDrawList(), pose);
                ImGui.Render();
                Vec3d pivot = (Vec3d)Call("CameraPivot", pose)!;
                Vector2 center = Project(pivot);
                var outer = lines.Cast<object>().Where(line => (int)AccessTools.Property(line.GetType(), "Axis").GetValue(line)! == 6).ToArray();
                if (outer.Length != 128) throw new Exception("Missing blue screen rotation ring.");
                Vector2 A(object line) => (Vector2)AccessTools.Property(line.GetType(), "A").GetValue(line)!;
                Vector2 B(object line) => (Vector2)AccessTools.Property(line.GetType(), "B").GetValue(line)!;
                float radius = Vector2.Distance(A(outer[0]), center);
                if (outer.Any(line => Math.Abs(Vector2.Distance(A(line), center) - radius) > .01f))
                    throw new Exception("Screen rotation ring becomes an ellipse when the view changes.");
                float bodyRadius = body.SelectMany(line => new[] { A(line), B(line) }).Max(point => Vector2.Distance(point, center));
                if (radius < bodyRadius + 39 || radius < 81.9f)
                    throw new Exception("The rotation rings do not enclose the camera outline.");
                Vector2 midpoint = (A(outer[8]) + B(outer[8])) / 2;
                if ((int)Call("CameraHandleAxisAt", midpoint, 5f)! != 6)
                    throw new Exception("The screen ring cannot be picked independently of XYZ.");
            }
            // Drive real pointer rotation in both directions around an oblique
            // screen normal. Check the full camera frame and preserved pivot.
            foreach (var eye in new[] { new Vector3(3, 2, 4), new Vector3(-3, -2, -4), Vector3.UnitZ })
            foreach (double worldX in new[] { 0d, 30000000.125 })
            foreach (bool snap in new[] { false, true })
            foreach (float angle in new[] { -.7f, .2f, 1.4f })
            {
                Configure(eye, worldX);
                keys[(int)GlKeys.LControl] = snap;
                var original = new CameraPreset { X = worldX, Yaw = 42, Pitch = 18, Roll = -13,
                    EditorPivotOffsetX = .3, EditorPivotOffsetY = .2 };
                Vec3d pivot = (Vec3d)Call("CameraPivot", original)!;
                Vector3 axis = (Vector3)Call("CameraScreenRotationAxis")!;
                Vector3 right = new((float)view[0], (float)view[4], (float)view[8]);
                Vector3 up = new((float)view[1], (float)view[5], (float)view[9]);
                object?[] args = [pivot, new Vec3d(pivot.X + right.X * MathF.Cos(angle) + up.X * MathF.Sin(angle),
                    pivot.Y + right.Y * MathF.Cos(angle) + up.Y * MathF.Sin(angle),
                    pivot.Z + right.Z * MathF.Cos(angle) + up.Z * MathF.Sin(angle)), null];
                if (!(bool)Call("ProjectCameraHandlePoint", args)!) throw new Exception("Screen rotation pointer was clipped.");
                Vector2 point = (Vector2)args[2]!;
                var clip = new DirectorReplayViewportClip { Id = Guid.NewGuid(), CameraPose = original.DeepClone() };
                Set("cameraToolSelection", clip.Id); Set("cameraDragOriginal", original); Set("cameraDragPose", original.DeepClone());
                Set("cameraDragTimeline", new DirectorReplayViewportTimeline { Clips = [clip] });
                Set("cameraDragAxisIndex", 6); Set("cameraDragAxis", axis); Set("cameraDragAngle", 0f);
                Set("cameraDragStartDirection", right); Set("cameraDragPreviousDirection", right);
                Call("MoveCameraToolDrag", (int)MathF.Round(point.X), (int)MathF.Round(point.Y));
                float expectedAngle = snap ? MathF.Round(angle / (MathF.PI / 12)) * (MathF.PI / 12) : angle;
                var result = clip.CameraPose!;
                var before = DirectorCameraManipulation.Basis(original); var after = DirectorCameraManipulation.Basis(result);
                Quaternion rotation = Quaternion.CreateFromAxisAngle(axis, expectedAngle);
                if (Vector3.Distance(after.Right, Vector3.Transform(before.Right, rotation)) > .012f
                    || Vector3.Distance(after.Up, Vector3.Transform(before.Up, rotation)) > .012f
                    || Vector3.Distance(after.Back, Vector3.Transform(before.Back, rotation)) > .012f
                    || ((Vec3d)Call("CameraPivot", result)!).DistanceTo(pivot) > .00001)
                    throw new Exception("Screen rotation uses a world axis, wrong sign, snap, or moves its pivot.");
            }
            keys[(int)GlKeys.LControl] = false;
            Set("cameraDragOriginal", null); Set("cameraDragPose", null); Set("cameraDragAxisIndex", -1);
            Console.WriteLine("PASS circular blue screen rotation ring surrounds projected cameras/pivots at 0.5-1000 blocks; picking and real pointer rotation preserve the full camera frame and pivot, both directions, snap and large coordinates");
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            foreach (float distance in new[] { .5f, 1f, 4f, 1000f })
            foreach (string mode in new[] { "Move", "Pivot" })
            {
                RuntimeEnv.GUIScale = scale;
                Configure(new(3, 2, 4), distance: distance);
                Set("cameraTool", Mode(mode));
                Clear(); ImGui.NewFrame();
                var draw = ImGui.GetForegroundDrawList();
                Guid id = Guid.NewGuid();
                var pose = new CameraPreset { Yaw = 73, Pitch = 180, EditorPivotOffsetX = .3 };
                Set("cameraToolSelection", id);
                Call("DrawCameraHead", draw, Guid.NewGuid(), pose, 0xff000000u);
                if (Enumerable.Range(0, draw.VtxBuffer.Size).Any(i => (draw.VtxBuffer[i].col & 0x00ffffff) != 0))
                    throw new Exception("Unselected camera contains a coloured centre point or axes.");
                Clear();
                int start = draw.VtxBuffer.Size;
                Call("DrawCameraOriginAxes", draw, new Vec3d());
                uint[] axisColors = [0xff6666ee, 0xff77cf75, 0xffedaa62];
                if (lines.Count != 0 || faces.Count != 0 || heads.Count != 0
                    || axisColors.Any(color => !Enumerable.Range(start, draw.VtxBuffer.Size - start).Any(i => draw.VtxBuffer[i].col == color)))
                    throw new Exception("Camera origin axes are missing RGB lines or intercept picking.");
                Call("DrawCameraHead", draw, id, pose, 0xffffffffu);
                var body = lines.Cast<object>().ToArray();
                Clear();
                Call("DrawCameraHandles", draw, pose);
                ImGui.Render();
                Vector2 A(object line) => (Vector2)AccessTools.Property(line.GetType(), "A").GetValue(line)!;
                Vector2 B(object line) => (Vector2)AccessTools.Property(line.GetType(), "B").GetValue(line)!;
                Vector2 center = Project((Vec3d)Call("CameraPivot", pose)!);
                float bodyRadius = body.SelectMany(line => new[] { A(line), B(line) }).Max(p => Vector2.Distance(p, center));
                float handleRadius = lines.Cast<object>().SelectMany(line => new[] { A(line), B(line) }).Max(p => Vector2.Distance(p, center));
                if (handleRadius <= bodyRadius || faces.Count == 0)
                    throw new Exception($"{mode} controls are hidden inside the camera outline at {distance}/{scale}.");
                for (int axis = 0; axis < 3; axis++)
                    if (!faces.Cast<object>().Any(face => (int)AccessTools.Property(face.GetType(), "Axis").GetValue(face)! == axis))
                        throw new Exception($"{mode} axis {axis} disappears near the camera.");
            }
            RuntimeEnv.GUIScale = 1;
            Console.WriteLine("PASS move/pivot controls enclose close and distant camera outlines at 100/150/200%; plain RGB camera-origin axes do not intercept picking; unselected heads have no coloured centre");
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
                object?[] pointerArgs = [new Vec3d(worldX, 0, 0), new Vec3d(worldX + offset.X, offset.Y, offset.Z), null];
                if (!(bool)Call("ProjectCameraHandlePoint", pointerArgs)!) throw new Exception("Plane pointer was clipped.");
                Vector2 pointer = (Vector2)pointerArgs[2]!;
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
            Set("cameraToolSelection", Guid.Empty);
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
