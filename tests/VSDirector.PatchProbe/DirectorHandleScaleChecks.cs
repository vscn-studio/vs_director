using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using ImGuizmoNET;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VSDirector;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    internal static unsafe void RunHandleChecks()
    {
        nint previous = ImGui.GetCurrentContext(), context = ImGui.CreateContext();
        float previousScale = RuntimeEnv.GUIScale;
        string previousAssets = GamePaths.AssetsPath;
        try
        {
            var io = ImGui.GetIO();
            io.DisplaySize = new(1920, 1080); io.DeltaTime = 1f / 60;
            io.NativePtr->IniFilename = null;
            io.Fonts.AddFontDefault(); io.Fonts.Build();
            var logger = ProbeDispatchProxy.Create<ILogger>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
            string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            AccessTools.Property(typeof(GamePaths), nameof(GamePaths.AssetsPath)).SetValue(null,
                Path.GetFullPath(Path.Combine(project, "../Vintagestory/assets")));
            Lang.PreLoad(logger, GamePaths.AssetsPath, "en");
            var assembly = typeof(VSDirectorModSystem).Assembly;
            CheckEditorCameraProjection(assembly);
            CheckCameraMoveCones(assembly);
            CheckCameraHandles(assembly, _ => { });
            CheckHandleEndShapes(assembly);
            CheckCreationHandleScale(assembly);
        }
        finally
        {
            RuntimeEnv.GUIScale = previousScale;
            AccessTools.Property(typeof(GamePaths), nameof(GamePaths.AssetsPath)).SetValue(null, previousAssets);
            ImGui.DestroyContext(context); ImGui.SetCurrentContext(previous);
        }
    }

    private static void CheckHandleEndShapes(Assembly assembly)
    {
        var type = assembly.GetType("VSDirector.Gui.DirectorEditorHandles", true)!;
        var handles = Activator.CreateInstance(type, true)!;
        void Set(string name, object value) => AccessTools.Field(type, name).SetValue(handles, value);
        var faces = (List<(Vector2 A, Vector2 B, Vector2 C, int Axis)>)AccessTools.Field(type, "cameraToolTriangles").GetValue(handles)!;
        var lines = (List<(Vector2 A, Vector2 B, int Axis, Guid CameraId)>)AccessTools.Field(type, "cameraToolLines").GetValue(handles)!;
        Set("units", 1f); Set("center", new Vector2(400, 300));
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        foreach (var rotation in new[] { Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(.6f, -.4f, .3f) })
        foreach (var eye in new[] { Vector3.UnitZ, -Vector3.UnitZ, new Vector3(3, 2, 4), new Vector3(-3, -2, -4) })
        for (int axis = 0; axis < 3; axis++)
        {
            RuntimeEnv.GUIScale = scale;
            Vector3[] basis = [Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitY, rotation), Vector3.Transform(Vector3.UnitZ, rotation)];
            Set("axes", basis);
            var m = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
            Set("viewMatrix", new double[] { m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,
                m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44 });
            foreach (bool box in new[] { true, false })
            {
                faces.Clear(); lines.Clear();
                ImGui.NewFrame();
                var draw = ImGui.GetForegroundDrawList();
                var flags = draw.Flags;
                AccessTools.Method(type, box ? "DrawCameraScaleBox" : "DrawCameraMoveCone")
                    .Invoke(handles, [draw, new Vec3d(), 100 * scale, axis, 0xff77cf75u]);
                ImGui.Render();
                if (draw.Flags != flags || Enumerable.Range(0, draw.VtxBuffer.Size).Any(i => draw.VtxBuffer[i].col != 0xff77cf75u))
                    throw new Exception("Axis caps have translucent seams, outlines, or leaked draw flags");
                if (box && (faces.Count < 2 || faces.Count > 6 || faces.Count % 2 != 0))
                    throw new Exception("Scale cuboid renders hidden faces or loses its visible face");
                foreach (var face in faces)
                {
                    float area = Math.Abs((face.B.X-face.A.X)*(face.C.Y-face.A.Y)-(face.B.Y-face.A.Y)*(face.C.X-face.A.X));
                    if (area < .01f) continue;
                    int hit = (int)AccessTools.Method(type, "CameraHandleAxisAt").Invoke(handles, [(face.A+face.B+face.C)/3, 0f])!;
                    if (hit != axis) throw new Exception("Visible axis cap cannot be picked");
                }
                if (rotation == Quaternion.Identity && eye == Vector3.UnitZ)
                {
                    var points = faces.SelectMany(f => new[] { f.A, f.B, f.C }).ToArray();
                    var size = (points.Aggregate(Vector2.Max) - points.Aggregate(Vector2.Min)) / scale;
                    Vector2 expected = box ? axis switch { 0 => new(4, 16), 1 => new(16, 4), _ => new(16, 16) }
                        : axis switch { 0 => new(24, 18), 1 => new(18, 24), _ => new(18, 18) };
                    if (Vector2.Distance(size, expected) > .01f)
                        throw new Exception($"Incorrect {(box ? "flat square cuboid" : "enlarged cone")} dimensions: axis {axis}, {size}");
                }
            }
        }
        RuntimeEnv.GUIScale = 1;
        Console.WriteLine("PASS flat 16x16x4 scale caps with hidden faces culled and enlarged 24x18 move cones: front/back/oblique views, local axes, DPI and face picking");
    }

    private static void CheckCreationHandleScale(Assembly assembly)
    {
        Type T(string name) => assembly.GetType("VSDirector." + name, true)!;
        void Set(object instance, string name, object value) => AccessTools.Field(instance.GetType(), name).SetValue(instance, value);
        object Get(object instance, string name) => AccessTools.Field(instance.GetType(), name).GetValue(instance)!;
        var session = new DirectorCreationSession(new(Path.Combine(Path.GetTempPath(), "vsdirector-handle-probe")));
        var actor = new CreationSceneObject();
        session.Open(new DirectorCreationProject { Objects = [actor] }); session.SelectedObject = actor.Id;
        var runtime = RuntimeHelpers.GetUninitializedObject(T("Runtime.DirectorCreationRuntime"));
        Set(runtime, "<Session>k__BackingField", session);
        var owner = new VSDirectorModSystem(); Set(owner, "creation", runtime);
        var workspace = RuntimeHelpers.GetUninitializedObject(T("Gui.DirectorCreationWorkspace"));
        Set(workspace, "owner", owner);
        var handles = Activator.CreateInstance(T("Gui.DirectorEditorHandles"), true)!;
        Set(workspace, "creationHandles", handles);
        Set(workspace, "objectRotationAxis", -1); Set(workspace, "poseRotationAxis", -1);
        Set(workspace, "transformMode", MODE.WORLD);
        var manipulate = AccessTools.Method(workspace.GetType(), "ManipulateCreation");
        var io = ImGui.GetIO();
        static float[] Array(Matrix4x4 m) => [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,
            m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
        var min = new Vector2(80, 60);
        int scenarios = 0;
        foreach (float uiScale in new[] { 1f, 1.5f, 2f })
        foreach (float fov in new[] { 45f, 90f, 120f })
        foreach (var size in new[] { new Vector2(1440, 900), new Vector2(600, 850) })
        foreach (var operation in new[] { OPERATION.TRANSLATE, OPERATION.ROTATE, OPERATION.SCALE })
        foreach (bool camera in new[] { false, true })
        {
            RuntimeEnv.GUIScale = uiScale;
            Set(workspace, "operation", operation);
            Vector2[]? reference = null;
            foreach (float distance in new[] { .02f, .05f, .1f, .5f, 4f, 1000f })
            foreach (float modelScale in new[] { .01f, 1f, 100f })
            {
                var world = Matrix4x4.CreateScale(modelScale);
                var view = Matrix4x4.CreateLookAt(Vector3.Normalize(new(3, 2, 4)) * distance, Vector3.Zero, Vector3.UnitY);
                // Keep the pivot away from the centre to exercise perspective axes.
                view.M41 += distance * .05f; view.M42 += distance * .03f;
                var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov * MathF.PI / 180, size.X / size.Y, .1f, 10000);
                Set(workspace, "handleAxis", -1); Set(workspace, "handleHover", -1);
                io.MouseDown[0] = false; io.MousePos = new(-100, -100);
                Matrix4x4 Frame()
                {
                    ImGui.NewFrame();
                    ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(io.DisplaySize);
                    ImGui.Begin("handle-probe", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
                    object?[] args = [ImGui.GetForegroundDrawList(), world, Array(view), Array(projection), min, size, camera, null];
                    manipulate.Invoke(workspace, args);
                    ImGui.End();
                    ImGui.Render();
                    return (Matrix4x4)args[^1]!;
                }
                Frame();
                var lines = (IEnumerable<(Vector2 A, Vector2 B, int Axis, Guid CameraId)>)AccessTools.Property(handles.GetType(), "Lines").GetValue(handles)!;
                var faces = ((IEnumerable<(Vector2 A, Vector2 B, Vector2 C, int Axis)>)AccessTools.Property(handles.GetType(), "Triangles").GetValue(handles)!).ToArray();
                var points = lines.SelectMany(l => new[] { l.A, l.B }).ToArray();
                if (points.Length == 0 || points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y)))
                    throw new Exception("Creation handles disappeared or produced non-finite geometry");
                reference ??= points;
                if (reference.Length != points.Length || points.Where((p, i) => Vector2.Distance(p, reference[i]) > .15f).Any())
                    throw new Exception($"Handle size/shape changed with proximity or model scale: {operation}, {distance}, {modelScale}, {fov}, {uiScale}");
                for (int axis = 0; axis < 3; axis++)
                {
                    var face = faces.Where(f => f.Axis == axis).MaxBy(f => Math.Abs((f.B.X-f.A.X)*(f.C.Y-f.A.Y)-(f.B.Y-f.A.Y)*(f.C.X-f.A.X)));
                    io.MousePos = (face.A + face.B + face.C) / 3;
                    Frame();
                    if ((int)Get(workspace, "handleHover") != axis)
                        throw new Exception($"Visible handle face cannot be picked: {operation}/{axis}, {distance}, {modelScale}, FOV={fov}, scale={uiScale}, viewport={size}; hover={Get(workspace, "handleHover")}, mouse={io.MousePos}");
                }
                // A visible close-up move handle must also start a real constrained drag.
                if (modelScale == 1 && operation == OPERATION.TRANSLATE)
                {
                    var face = faces.Where(f => f.Axis == 0).MaxBy(f => Math.Abs((f.B.X-f.A.X)*(f.C.Y-f.A.Y)-(f.B.Y-f.A.Y)*(f.C.X-f.A.X)));
                    io.MousePos = (face.A + face.B + face.C) / 3; Frame();
                    io.MouseDown[0] = true; Frame();
                    if ((int)Get(workspace, "handleAxis") != 0)
                        throw new Exception($"Visible near-camera X handle rejects drag start at distance {distance}");
                    io.MousePos += new Vector2(8, 0);
                    var moved = Frame();
                    if (Math.Abs(moved.M41) < 1e-7 || Math.Abs(moved.M42) > 1e-6 || Math.Abs(moved.M43) > 1e-6)
                        throw new Exception("Dragging the X handle failed or moved another axis");
                    io.MouseDown[0] = false; Frame();
                }
                scenarios++;
            }
        }
        Console.WriteLine($"PASS {scenarios} real ImGui handle scenarios: near/far, model scale, camera/model, move/rotate/scale, DPI, FOV, viewports, face picking and close-up drags");
    }
}
