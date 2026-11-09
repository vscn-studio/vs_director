using System.Reflection;
using System.Numerics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static object? handoffController;
    private static CameraPreset? handoffPose;

    private static bool CaptureFreeCameraActivation(object __instance, CameraPreset camera,
        ref string error, ref bool __result)
    {
        if (!ReferenceEquals(__instance, handoffController)) return true;
        // Isolate engine activation, but exercise the real handoff and input code.
        handoffPose = camera.DeepClone();
        AccessTools.Field(__instance.GetType(), "activeCamera").SetValue(__instance, camera.DeepClone());
        AccessTools.Field(__instance.GetType(), "preparedCameraPose").SetValue(__instance, null);
        error = string.Empty;
        __result = true;
        return false;
    }

    private static void CheckEditorCameraHandoff(Assembly assembly)
    {
        Type type = assembly.GetType("VSDirector.Runtime.DirectorCameraController", true)!;
        object controller = RuntimeHelpers.GetUninitializedObject(type);
        void Set(string name, object? value) => AccessTools.Field(type, name).SetValue(controller, value);
        object? Call(string name, params object?[] args) => AccessTools.Method(type, name).Invoke(controller, args);
        var config = new DirectorConfig { ReplayFreeCameraSpeed = 10 };
        var keys = new bool[512];
        keys[(int)GlKeys.W] = true;
        var input = ProbeDispatchProxy.Create<IInputAPI>((m, _) => m.Name == "get_KeyboardKeyStateRaw"
            ? keys : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name == "get_Input"
            ? input : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        Set("capi", api); Set("config", config);
        Set("game", RuntimeHelpers.GetUninitializedObject(typeof(ClientMain)));
        var harmony = new Harmony("vsdirector.probe.free-camera-handoff");
        var activate = AccessTools.Method(type, "TryActivatePose");
        handoffController = controller;
        try
        {
            harmony.Patch(activate, prefix: new HarmonyMethod(AccessTools.Method(typeof(DirectorImGuiProbe), nameof(CaptureFreeCameraActivation))));
            foreach (float yaw in new[] { -720f, -37, 0, 42, 180, 359, 720 })
            foreach (float pitch in new[] { -720f, -270, -180, -100, -90, -45, 0, 45, 90, 100, 180, 260, 270, 360, 720 })
            foreach (float roll in new[] { -180f, 0, 90, 180, 360 })
            foreach (bool prepared in new[] { false, true })
            {
                var source = new CameraPreset { Id = Guid.NewGuid(), Name = "Inverted shooting keyframe",
                    X = 30000000.125, Y = 124.25, Z = -30000000.875, Dimension = 2,
                    Yaw = yaw, Pitch = pitch, Roll = roll, Fov = 85 };
                Set("activeCamera", prepared ? new CameraPreset { X = 99 } : source);
                Set("preparedCameraPose", prepared ? source : null);
                Set("freeCameraActive", false);
                handoffPose = null;
                object?[] args = [null];
                if (Call("TryStartFreeCamera", args) is not true || handoffPose is not { } pose)
                    throw new Exception("Free-camera handoff failed: " + args[0]);
                Vec3f Forward(CameraPreset p) => EntityPos.GetViewVector(p.Pitch * GameMath.DEG2RAD, p.Yaw * GameMath.DEG2RAD);
                var before = Forward(source); var after = Forward(pose);
                double difference = Math.Sqrt(Math.Pow(before.X - after.X, 2) + Math.Pow(before.Y - after.Y, 2) + Math.Pow(before.Z - after.Z, 2));
                // Exact poles are moved only by the native 0.86-degree safety margin.
                if (pose.Roll != 0 || pose.Pitch < 90.86f || pose.Pitch > 269.14f || difference > .016
                    || pose.X != source.X || pose.Y != source.Y || pose.Z != source.Z || pose.Dimension != 2 || pose.Fov != 85
                    || pose.Id != Guid.Empty || source.Yaw != yaw || source.Pitch != pitch || source.Roll != roll || source.Name != "Inverted shooting keyframe")
                    throw new Exception($"Inverted shooting pose leaked into free look: {yaw}/{pitch}/{roll}, prepared={prepared}, direction error={difference}.");
                var camera = (CameraPreset)AccessTools.Field(type, "activeCamera").GetValue(controller)!;
                float initialPitch = camera.Pitch;
                Call("OnMouseMove", new MouseEvent(0, 0, 0, 0));
                if (camera.Pitch != initialPitch || camera.Roll != 0)
                    throw new Exception("The first mouse event snapped or inverted the free camera.");
                Call("AdvanceFreeCamera", .1f);
                if (Math.Abs(camera.X - source.X - after.X) > .00001
                    || Math.Abs(camera.Y - source.Y - after.Y) > .00001
                    || Math.Abs(camera.Z - source.Z - after.Z) > .00001)
                    throw new Exception("Free-camera forward movement changed after an inverted shooting pose.");
            }
            Console.WriteLine("PASS inverted/rolled shooting keyframes enter upright free look without changing view direction, position, dimension, FOV or source; first mouse/movement stable (rendered and active poses)");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            handoffController = null;
            handoffPose = null;
        }
    }

    private static void CheckCameraHeadOrientation(Assembly assembly)
    {
        Type type = assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud", true)!;
        var basisMethod = AccessTools.Method(type, "CameraHeadBasis");
        foreach (float yaw in new[] { -37f, 0, 90, 180, 270, 359 })
        foreach (float pitch in new[] { -260f, -180, -100, -45, 0, 45, 100, 150, 180, 230, 260, 360, 540 })
        foreach (float roll in new[] { 0f, 30, 90, 180, 270 })
        {
            var pose = new CameraPreset { Yaw = yaw, Pitch = pitch, Roll = roll };
            var (right, up, back) = ((Vector3, Vector3, Vector3))basisMethod.Invoke(null, [pose])!;
            var forward = EntityPos.GetViewVector(pitch * GameMath.DEG2RAD, yaw * GameMath.DEG2RAD);
            double[] view = new double[16];
            Mat4d.LookAt(view, [0d, 0, 0], [(double)forward.X, forward.Y, forward.Z], [0d, 1, 0]);
            var nativeRight = new Vector3((float)view[0], (float)view[4], (float)view[8]);
            var nativeUp = new Vector3((float)view[1], (float)view[5], (float)view[9]);
            float angle = roll * GameMath.DEG2RAD;
            Vector3 expectedRight = nativeRight * MathF.Cos(angle) - nativeUp * MathF.Sin(angle);
            Vector3 expectedUp = nativeRight * MathF.Sin(angle) + nativeUp * MathF.Cos(angle);
            if (Vector3.Distance(right, expectedRight) > .00002 || Vector3.Distance(up, expectedUp) > .00002
                || Vector3.Distance(-back, new Vector3(forward.X, forward.Y, forward.Z)) > .00002)
                throw new Exception($"Camera silhouette disagrees with native view: {yaw}/{pitch}/{roll}.");
            if (roll == 0 && up.Y <= 0)
                throw new Exception("The unrolled camera's film reels are upside down.");
            if (pose.Yaw != yaw || pose.Pitch != pitch || pose.Roll != roll)
                throw new Exception("Drawing the camera silhouette changed the shooting view.");
        }
        Console.WriteLine("PASS camera silhouette matches native world-up frame at normal 180-degree pitch and other headings/pitches, preserves lens direction and intentional roll without changing shooting pose");
    }

    private static void CheckCameraConeFill(Assembly assembly, IDisposable renderer)
    {
        Type type = assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud", true)!;
        Type viewport = assembly.GetType("VSDirector.Rendering.DirectorWorkspaceViewportRuntime", true)!;
        object hud = RuntimeHelpers.GetUninitializedObject(type);
        double[] view = new double[16], projection = new double[16];
        projection[0] = 640d / 960; projection[5] = 1;
        projection[10] = -1.002; projection[11] = -1; projection[14] = -.2002;
        var render = ProbeDispatchProxy.Create<IRenderAPI>((m, _) => m.Name switch
        {
            "get_FrameWidth" => 960, "get_FrameHeight" => 640,
            "get_PerspectiveViewMat" => view, "get_PerspectiveProjectionMat" => projection,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name == "get_Render"
            ? render : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        AccessTools.Field(type, "capi").SetValue(hud, api);
        foreach (string name in new[] { "cameraToolLines", "cameraToolTriangles", "cameraToolHeads" })
            AccessTools.Field(type, name).SetValue(hud, Activator.CreateInstance(AccessTools.Field(type, name).FieldType));
        Vector2 display = ImGui.GetIO().DisplaySize;
        try
        {
            ImGui.GetIO().DisplaySize = new(960, 640);
            AccessTools.Method(viewport, "Configure").Invoke(null, [hud, api, 0, 0, 960, 640, false]);
            for (int axis = 0; axis < 3; axis++)
            foreach (int sign in new[] { -1, 1 })
            {
                Vector3 back = (axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ) * sign;
                Vector3 right = Vector3.Normalize(Vector3.Cross(Math.Abs(back.Y) > .99 ? Vector3.UnitZ : Vector3.UnitY, back));
                Vector3 up = Vector3.Cross(back, right);
                Array.Clear(view);
                view[0] = right.X; view[4] = right.Y; view[8] = right.Z;
                view[1] = up.X; view[5] = up.Y; view[9] = up.Z;
                view[2] = back.X; view[6] = back.Y; view[10] = back.Z;
                view[14] = -1.5; view[15] = 1;
                ImGui.NewFrame();
                var draw = ImGui.GetForegroundDrawList();
                var flags = draw.Flags;
                AccessTools.Method(type, "DrawCameraMoveCone").Invoke(hud, [draw, new Vec3d(), 1f, axis, 0xff77cf75u]);
                if (draw.Flags != flags) throw new Exception("Cone rendering leaked its antialiasing flags.");
                ImGui.Render();
                GL.Viewport(0, 0, 960, 640); GL.ClearColor(0, 0, 0, 1); GL.Clear(ClearBufferMask.ColorBufferBit);
                AccessTools.Method(renderer.GetType(), "Render").Invoke(renderer, null);
                int radius = 2;
                int size = radius * 2 + 1;
                byte[] pixels = new byte[size * size * 4];
                GL.ReadPixels(480 - radius, 320 - radius, size, size, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if ((x - radius) * (x - radius) + (y - radius) * (y - radius) > radius * radius) continue;
                    if (pixels[(y * size + x) * 4 + 1] < 80)
                        throw new Exception($"The rendered cone is hollow or has a transparent seam: axis={axis}, sign={sign}, pixel={x}/{y}.");
                }
            }
            Console.WriteLine("PASS real OpenGL: solid cone interiors/caps without transparent seams from both ends of XYZ; antialiasing state restored");
            AccessTools.Field(type, "cameraDragAxisIndex").SetValue(hud, -1);
            for (int frame = 0; frame < 2; frame++)
            {
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(960, 640));
                ImGui.Begin("camera-overlay-host", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
                    | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus);
                AccessTools.Method(type, "DrawCameraRotationRing").Invoke(hud,
                    [ImGui.GetWindowDrawList(), new Vec3d(), .55f, 2, 0xff77cf75u, Vector3.UnitZ]);
                if (frame == 0) ImGui.OpenPopup("camera-overlay-popup");
                ImGui.SetNextWindowPos(new(300, 170)); ImGui.SetNextWindowSize(new(180, 300));
                if (ImGui.BeginPopup("camera-overlay-popup", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings))
                {
                    ImGui.GetWindowDrawList().AddRectFilled(new(300, 170), new(480, 470), 0xff0000ffu);
                    if (frame == 1) ImGui.CloseCurrentPopup();
                    ImGui.EndPopup();
                }
                ImGui.End(); ImGui.Render();
            }
            GL.Viewport(0, 0, 960, 640); GL.ClearColor(0, 0, 0, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            // Simulate a world depth buffer that rejects every overlay fragment.
            // The UI backend must disable depth testing and restore it afterwards.
            GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Never);
            try
            {
                AccessTools.Method(renderer.GetType(), "Render").Invoke(renderer, null);
                if (!GL.IsEnabled(EnableCap.DepthTest)) throw new Exception("Overlay rendering leaked depth state.");
                byte[] pixels = new byte[960 * 640 * 4];
                GL.ReadPixels(0, 0, 960, 640, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
                int visible = 0, covered = 0;
                for (int y = 170; y < 470; y++)
                for (int x = 300; x < 660; x++)
                {
                    int i = (y * 960 + x) * 4;
                    if (pixels[i + 1] < 140 || pixels[i + 2] > 150) continue;
                    if (x < 475) covered++;
                    if (x > 490) visible++;
                }
                if (visible < 100 || covered != 0)
                    throw new Exception($"Camera overlay depth/popup ordering is wrong: visible={visible}, abovePopup={covered}.");
            }
            finally { GL.Disable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Less); }
            Console.WriteLine("PASS real OpenGL: full camera ring remains visible over rejecting world depth, while a popup covers it; depth state restored");
        }
        finally
        {
            ImGui.GetIO().DisplaySize = display;
            AccessTools.Method(viewport, "Deactivate").Invoke(null, [hud]);
        }
    }

    private static void CheckEditorCameraInput(Assembly assembly)
    {
        Type type = assembly.GetType("VSDirector.Runtime.DirectorCameraController", true)!;
        object controller = RuntimeHelpers.GetUninitializedObject(type);
        var camera = new CameraPreset { Pitch = 180 };
        var config = new DirectorConfig { ReplayFreeCameraSpeed = 10 };
        bool[] keys = new bool[512];
        keys[(int)GlKeys.W] = keys[(int)GlKeys.LShift] = keys[(int)GlKeys.LControl] = true;
        var input = ProbeDispatchProxy.Create<IInputAPI>((m, _) => m.Name == "get_KeyboardKeyStateRaw"
            ? keys : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name == "get_Input"
            ? input : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        void Set(string name, object value) => AccessTools.Field(type, name).SetValue(controller, value);
        void Call(string name, params object[] args) => AccessTools.Method(type, name).Invoke(controller, args);
        Set("capi", api); Set("config", config);
        Set("game", RuntimeHelpers.GetUninitializedObject(typeof(ClientMain)));
        Set("activeCamera", camera); Set("freeCameraActive", true);
        foreach (bool workspace in new[] { false, true })
        foreach (bool gui in new[] { false, true })
        foreach (bool pointer in new[] { false, true })
        {
            camera.X = camera.Y = camera.Z = 0;
            Call("SetWorkspaceInputOverride", workspace); Set("guiOverride", gui);
            Call("SetGuiPointerCameraInput", pointer);
            Call("AdvanceFreeCamera", .1f);
            double distance = Math.Sqrt(camera.X * camera.X + camera.Y * camera.Y + camera.Z * camera.Z);
            bool navigation = (!workspace && !gui) || pointer;
            if (Math.Abs(distance - (navigation ? 4 : 0)) > .0001)
                throw new Exception($"Free-camera modifiers escaped editor ownership: workspace={workspace}, gui={gui}, pointer={pointer}, distance={distance}.");
            if (!keys[(int)GlKeys.LShift] || !keys[(int)GlKeys.LControl])
                throw new Exception("Free-camera isolation cleared the raw editing modifiers.");
        }
        // Releasing viewport navigation must stop movement on the very next frame.
        Call("SetWorkspaceInputOverride", true); Call("SetGuiPointerCameraInput", false);
        double before = camera.Y;
        Call("AdvanceFreeCamera", .1f);
        if (camera.Y != before) throw new Exception("Released viewport navigation kept moving the camera.");
        var hudType = assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud", true)!;
        object hud = RuntimeHelpers.GetUninitializedObject(hudType);
        AccessTools.Field(hudType, "cameraDragOriginal").SetValue(hud, new CameraPreset());
        foreach (var key in new[] { GlKeys.Left, GlKeys.Right, GlKeys.Z, GlKeys.Space, GlKeys.LControl, GlKeys.LShift })
        {
            var e = new KeyEvent { KeyCode = (int)key, CtrlPressed = true };
            AccessTools.Method(hudType, "OnKeyDown").Invoke(hud, [e]);
            if (!e.Handled) throw new Exception("A gizmo drag also dispatched a workspace shortcut: " + key);
        }
        Console.WriteLine("PASS editor modifiers/shortcuts stay with gizmos; free-camera navigation retains movement/sprint and stops on release without clearing raw modifiers");
    }

    private static void CheckCameraMoveCones(Assembly assembly)
    {
        var type = assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud", true)!;
        var viewport = assembly.GetType("VSDirector.Rendering.DirectorWorkspaceViewportRuntime", true)!;
        object hud = RuntimeHelpers.GetUninitializedObject(type);
        double[] view = new double[16], projection = new double[16];
        projection[0] = 1080d / 1920; projection[5] = 1;
        projection[10] = -1; projection[11] = -1; projection[14] = -.2;
        var render = ProbeDispatchProxy.Create<IRenderAPI>((m, _) => m.Name switch
        {
            "get_FrameWidth" => 1920, "get_FrameHeight" => 1080,
            "get_PerspectiveViewMat" => view, "get_PerspectiveProjectionMat" => projection,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name == "get_Render"
            ? render : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        AccessTools.Field(type, "capi").SetValue(hud, api);
        foreach (string name in new[] { "cameraToolLines", "cameraToolTriangles", "cameraToolHeads" })
        {
            var field = AccessTools.Field(type, name);
            field.SetValue(hud, Activator.CreateInstance(field.FieldType));
        }
        var lines = (System.Collections.IList)AccessTools.Field(type, "cameraToolLines").GetValue(hud)!;
        var faces = (System.Collections.IList)AccessTools.Field(type, "cameraToolTriangles").GetValue(hud)!;
        var heads = (List<(Guid, Vector2)>)AccessTools.Field(type, "cameraToolHeads").GetValue(hud)!;
        var io = ImGui.GetIO();
        Vector2 display = io.DisplaySize;
        float savedScale = RuntimeEnv.GUIScale;
        try
        {
            io.DisplaySize = new(1920, 1080);
            AccessTools.Method(viewport, "Configure").Invoke(null, [hud, api, 0, 0, 1920, 1080, false]);
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            foreach (float distance in new[] { 1f, 20f })
            foreach (Vector3 eye in new[] { new Vector3(3, 2, 4), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
                -Vector3.UnitX, -Vector3.UnitY, -Vector3.UnitZ })
            {
                RuntimeEnv.GUIScale = scale;
                Vector3 back = Vector3.Normalize(eye);
                Vector3 right = Vector3.Normalize(Vector3.Cross(Math.Abs(back.Y) > .99f ? Vector3.UnitZ : Vector3.UnitY, back));
                Vector3 up = Vector3.Cross(back, right);
                Array.Clear(view);
                view[0] = right.X; view[4] = right.Y; view[8] = right.Z;
                view[1] = up.X; view[5] = up.Y; view[9] = up.Z;
                view[2] = back.X; view[6] = back.Y; view[10] = back.Z;
                view[14] = -distance; view[15] = 1;
                float length = (float)AccessTools.Method(type, "CameraHandleRadius").Invoke(hud, [new Vec3d()])!;
                for (int axis = 0; axis < 3; axis++)
                {
                    lines.Clear(); faces.Clear(); heads.Clear();
                    ImGui.NewFrame();
                    var draw = ImGui.GetForegroundDrawList();
                    try
                    {
                        AccessTools.Method(type, "DrawCameraMoveCone").Invoke(hud,
                            [draw, new Vec3d(), length, axis, 0xff77cf75u]);
                    }
                    finally { ImGui.Render(); }
                    var vertices = draw.VtxBuffer;
                    for (int i = 0; i < vertices.Size; i++)
                        if (vertices[i].col != 0xff77cf75u)
                            throw new Exception("A move cone face has shading instead of the solid axis colour.");
                    int visibleFaces = 0;
                    foreach (object face in faces)
                    {
                        Vector2 a = (Vector2)AccessTools.Property(face.GetType(), "A").GetValue(face)!;
                        Vector2 b = (Vector2)AccessTools.Property(face.GetType(), "B").GetValue(face)!;
                        Vector2 c = (Vector2)AccessTools.Property(face.GetType(), "C").GetValue(face)!;
                        if (Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) < .01) continue;
                        visibleFaces++;
                        Vector2 point = (a + b + c) / 3;
                        if ((int)AccessTools.Method(type, "CameraHandleAxisAt").Invoke(hud, [point, 0f])! != axis)
                            throw new Exception("A projected cone face cannot be picked from an axial or oblique view.");
                    }
                    if (visibleFaces == 0 || ImGui.GetDrawData().TotalVtxCount == 0)
                        throw new Exception("The move cone disappeared when viewed along its axis.");
                    heads.Add((Guid.NewGuid(), new Vector2(960, 540)));
                    if ((int)AccessTools.Method(type, "CameraHandleAxisAt").Invoke(hud, [new Vector2(960, 540), 1000f])! != -1)
                        throw new Exception("An axis intercepts the camera's central selection point.");
                }
            }
            Console.WriteLine("PASS unlit closed move cones and face picking from both ends of XYZ and oblique views, near/far and DPI 100/150/200%; camera centre takes priority");
        }
        finally
        {
            io.DisplaySize = display;
            RuntimeEnv.GUIScale = savedScale;
            AccessTools.Method(viewport, "Deactivate").Invoke(null, [hud]);
        }
    }

    private static void CheckEditorCameraProjection(Assembly assembly)
    {
        var hudType = assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud", true)!;
        var outline = ((ValueTuple<Vector3, Vector3>[])AccessTools.Field(hudType, "CameraOutline").GetValue(null)!)
            .SelectMany(edge => new[] { edge.Item1, edge.Item2 }).ToArray();
        if (outline.Length == 0 || outline.Length > 100 || outline.Any(point => !float.IsFinite(point.Length()) || point.Length() > .5f))
            throw new Exception("The camera outline exceeds its one-block diameter around the camera origin.");
        Vector3 min = outline.Aggregate(Vector3.Min), max = outline.Aggregate(Vector3.Max);
        if (max.X - min.X < .3f || max.Y - min.Y < .3f || max.Z - min.Z < .6f)
            throw new Exception("The camera outline no longer has a visible body and forward lens.");
        var viewportType = assembly.GetType("VSDirector.Rendering.DirectorWorkspaceViewportRuntime", true)!;
        object hud = RuntimeHelpers.GetUninitializedObject(hudType);
        double[] view = new double[16], projection = new double[16];
        var render = ProbeDispatchProxy.Create<IRenderAPI>((m, _) => m.Name switch
        {
            "get_FrameWidth" => 1920, "get_FrameHeight" => 1080,
            "get_PerspectiveViewMat" => view, "get_PerspectiveProjectionMat" => projection,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_Render" => render, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        AccessTools.Field(hudType, "capi").SetValue(hud, api);
        var project = AccessTools.Method(hudType, "ProjectCameraPoint");
        var units = AccessTools.Method(hudType, "CameraWorldUnitsPerPixel");
        var handleRadius = AccessTools.Method(hudType, "CameraHandleRadius");
        float savedScale = RuntimeEnv.GUIScale;
        Vector2 Project(Vec3d point)
        {
            object?[] args = [point, null];
            if (!(bool)project.Invoke(hud, args)!) throw new Exception("Visible editor camera was clipped: " + point);
            return (Vector2)args[1]!;
        }
        try
        {
            foreach (float uiScale in new[] { 1f, 1.5f, 2f })
            foreach (double worldX in new[] { 0d, 30000000.125 })
            foreach (double fov in new[] { 45d, 90d, 120d })
            {
                RuntimeEnv.GUIScale = uiScale;
                Array.Clear(view); Array.Clear(projection);
                view[0] = view[5] = view[10] = view[15] = 1; view[12] = -worldX;
                double focal = 1 / Math.Tan(fov * Math.PI / 360), near = .1, far = 128;
                projection[0] = focal / (1920d / 1080); projection[5] = focal;
                projection[10] = -(far + near) / (far - near); projection[11] = -1;
                projection[14] = -2 * far * near / (far - near);
                foreach (int width in new[] { 960, 1440 })
                {
                    AccessTools.Method(viewportType, "Configure").Invoke(null, [hud, api, 0, 30, width, width * 9 / 16, false]);
                    foreach (double distance in new[] { .05, .2, 1d, 2d, 10d, 1000d, 100000d })
                    foreach (double horizontal in new[] { 0d, .9 })
                    foreach (double vertical in new[] { 0d, .8 })
                    {
                        var point = new Vec3d(worldX + horizontal * distance / projection[0], vertical * distance / focal, -distance);
                        Vector2 center = Project(point);
                        double step = (float)units.Invoke(hud, [point])!;
                        Vector2 edge = Project(new(point.X + step * 50, point.Y, -distance));
                        if (Math.Abs(Vector2.Distance(center, edge) - 50) > .1)
                            throw new Exception($"Editor camera pixel scale changed with distance/FOV/viewport/position: {distance}, {fov}, {width}, {horizontal}/{vertical}.");
                        double radius = (float)handleRadius.Invoke(hud, [point])!;
                        if (Math.Abs(radius / step / uiScale - 60) > .01)
                            throw new Exception($"Constant handle size failed: handle={radius / step / uiScale}, distance={distance}.");
                        foreach (Vector3 corner in outline)
                        {
                            var worldCorner = new Vec3d(point.X + corner.X, point.Y + corner.Y, point.Z + corner.Z);
                            object?[] cornerArgs = [worldCorner, null];
                            bool visible = (bool)project.Invoke(hud, cornerArgs)!;
                            if (visible != (worldCorner.Z < 0))
                                throw new Exception("Fixed-size camera outline has incorrect close-up/behind-eye projection.");
                        }
                        object?[] behind = [new Vec3d(worldX, 0, distance), null];
                        if ((bool)project.Invoke(hud, behind)!) throw new Exception("Editor camera behind the eye remains visible/pickable.");
                    }
                }
            }
            Console.WriteLine("PASS simplified one-block camera projection and constant screen-sized handles; DPI 100/150/200%, 0.05-100000 blocks, off-centre/large coordinates, FOVs/viewports and behind-eye rejection");
        }
        finally
        {
            RuntimeEnv.GUIScale = savedScale;
            AccessTools.Method(viewportType, "Deactivate").Invoke(null, [hud]);
        }
    }

    private static void CheckIndependentEditorCamera(Assembly assembly)
    {
        Type type = assembly.GetType("VSDirector.Runtime.DirectorCameraController", true)!;
        object controller = RuntimeHelpers.GetUninitializedObject(type);
        var editor = new CameraPreset { X = 200, Y = 100, Z = 300, Yaw = 42, Fov = 85 };
        AccessTools.Field(type, "activeCamera").SetValue(controller, editor);
        AccessTools.Field(type, "freeCameraActive").SetValue(controller, true);
        var actor = new EntityItem();
        actor.Pos.SetPos(10, 20, 30);
        var rig = new CameraFollowRig { Id = Guid.NewGuid(), Anchor = CameraFollowAnchor.Body,
            MotionMode = CameraFollowMotionMode.Smooth, SmoothTimeSeconds = .5f };
        var evaluate = AccessTools.Method(type, "TrySetReplayFollowElementPose");
        CameraPreset Sample(double time)
        {
            object?[] args = [rig, actor, time, null, null, false];
            if (!(bool)evaluate.Invoke(controller, args)!) throw new Exception("Shooting follow evaluation failed: " + args[4]);
            if (!ReferenceEquals(AccessTools.Field(type, "activeCamera").GetValue(controller), editor)
                || !(bool)AccessTools.Field(type, "freeCameraActive").GetValue(controller)!
                || AccessTools.Field(type, "replayFollowExportState").GetValue(controller) is not null
                || editor.X != 200 || editor.Yaw != 42 || editor.Fov != 85)
                throw new Exception("Evaluating the shooting camera changed the editor camera or export history.");
            return (CameraPreset)args[3]!;
        }
        var first = Sample(0);
        actor.Pos.X += 10;
        var second = Sample(.1);
        if (!(second.X > first.X && second.X < first.X + 10))
            throw new Exception("Independent shooting follow lost its smoothing history.");
        var seek = Sample(0);
        if (Math.Abs(seek.X - first.X - 10) > .001)
            throw new Exception("Seeking backward retained future shooting follow history.");
        AccessTools.Method(type, "ResetReplayPreviewFollowState").Invoke(controller, null);
        if (AccessTools.Field(type, "replayFollowPreviewState").GetValue(controller) is not null)
            throw new Exception("Replay shutdown retained shooting follow history.");
        Console.WriteLine("PASS independent shooting follow preserves editor pose/free input and export state, smooths and resets on seek");
    }
}
