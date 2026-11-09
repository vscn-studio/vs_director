using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VSDirector;
using VSDirector.Data;
using VSDirector.Rendering;

internal static partial class DirectorImGuiProbe
{
    private sealed class ObservationProbeSink : IDirectorOffscreenFrameSink
    {
        public void OnFrame(in DirectorOffscreenFrame frame) { }
    }

    private static void CheckCameraObservationChannels(Assembly assembly)
    {
        var type = typeof(DirectorOffscreenMultiViewRenderer);
        var renderer = (DirectorOffscreenMultiViewRenderer)RuntimeHelpers.GetUninitializedObject(type);
        foreach (string name in new[] { "sync", "views", "previewViews", "sinks" })
        {
            var field = AccessTools.Field(type, name);
            field.SetValue(renderer, Activator.CreateInstance(field.FieldType));
        }
        var owner = new VSDirectorModSystem();
        AccessTools.Field(owner.GetType(), "offscreenRenderer").SetValue(owner, renderer);
        Guid Id(string name) => (Guid)AccessTools.Property(owner.GetType(), name).GetValue(owner)!;
        Guid preview = Id("ReplayCameraPreviewViewId"), observation = Id("ReplayCameraObservationViewId");
        var shotSink = new ObservationProbeSink(); var observationSink = new ObservationProbeSink();
        void View(Guid id) => AccessTools.Method(type, "SetPreviewView").Invoke(renderer, [id,
            new DirectorOffscreenViewRequest { ViewId = id, Width = 64, Height = 64, Pose = new(0, 0, 0, 0, 0, 0, 0, 50) }]);
        void AddChannels()
        {
            AccessTools.Field(owner.GetType(), "replayPreviewSink").SetValue(owner, shotSink);
            AccessTools.Field(owner.GetType(), "replayObservationSink").SetValue(owner, observationSink);
            renderer.AddSink(shotSink); renderer.AddSink(observationSink); View(preview); View(observation);
        }
        renderer.SetViews([new() { Width = 64, Height = 64, Pose = new(0, 0, 0, 0, 0, 0, 0, 70) }]);
        AddChannels();
        AccessTools.Method(owner.GetType(), "StopReplayCameraPreview").Invoke(owner, null);
        if (renderer.ActiveViewCount != 2 || ((IList)AccessTools.Field(type, "sinks").GetValue(renderer)!).Count != 1)
            throw new Exception("Closing preview stopped observation or capture.");
        AddChannels();
        AccessTools.Method(owner.GetType(), "OnReplayPreviewRenderFailed").Invoke(owner, [observation, new Exception("observation-only")]);
        if (renderer.ActiveViewCount != 2 || (string)AccessTools.Property(owner.GetType(), "ReplayCameraObservationError").GetValue(owner)! != "observation-only"
            || (string)AccessTools.Property(owner.GetType(), "ReplayCameraPreviewError").GetValue(owner)! != "")
            throw new Exception("Observation failure affected another preview/capture channel.");
        AccessTools.Method(owner.GetType(), "StopReplayCameraPreview").Invoke(owner, null);
        if (renderer.ActiveViewCount != 1) throw new Exception("Stopping workspace previews changed capture views.");
        Console.WriteLine("PASS independent preview/observation view slots, sink cleanup and failure isolation preserve capture");
    }

    private static void CheckCameraObservationViewport(Assembly assembly, Action<string> capture)
    {
        var hudType = assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud", true)!;
        var stateType = assembly.GetType("VSDirector.Data.DirectorCameraObservation", true)!;
        object hud = RuntimeHelpers.GetUninitializedObject(hudType);
        object state = Activator.CreateInstance(stateType, true)!;
        AccessTools.Field(hudType, "cameraObservation").SetValue(hud, state);
        var subject = new CameraPreset { X = 30000000.125, Y = 120.5, Z = -30000000.875, Dimension = 2, Yaw = 35, Pitch = 200, Roll = 25 };
        void Snap(int side) => AccessTools.Method(stateType, "Snap").Invoke(state, [side]);
        CameraPreset Eye() => (CameraPreset)AccessTools.Method(stateType, "Eye").Invoke(state, [subject])!;
        int Side() => (int)AccessTools.Property(stateType, "Side").GetValue(state)!;
        var matrices = AccessTools.Method(assembly.GetType("VSDirector.Rendering.DirectorOffscreenEngineState", true)!, "CameraMatrices");
        var project = AccessTools.Method(stateType, "Project");
        foreach (int side in Enumerable.Range(-1, 7))
        {
            Snap(side); var eye = Eye();
            var (view, _, _) = ((double[], double[], Vec3f))matrices.Invoke(null,
                [new DirectorOffscreenCameraPose(eye.X, eye.Y, eye.Z, eye.Dimension, eye.Yaw, eye.Pitch, 0, eye.Fov)])!;
            double[] projection = Mat4d.Create(); Mat4d.Perspective(projection, eye.Fov * Math.PI / 180, 16d / 9, .1, 128);
            foreach (var offset in new[] { Vector3.Zero, new Vector3(.3f, .2f, -.1f), new(-.2f, .1f, .3f) })
            {
                double x = subject.X + offset.X, y = subject.Y + offset.Y, z = subject.Z + offset.Z;
                var native = MatrixToolsd.Project(new(x, y + subject.Dimension * 32768d, z), projection, view, 480, 270);
                object?[] args = [eye, x, y, z, 16f / 9, new Vector2(12, 40), new Vector2(480, 270), null];
                if (!(bool)project.Invoke(null, args)! || Vector2.Distance((Vector2)args[7]!, new((float)native.X + 12, 270 - (float)native.Y + 40)) > .002)
                    throw new Exception("Observer camera overlay does not match engine projection at a cardinal/pole/large dimension coordinate.");
            }
        }

        int texture = GL.GenTexture();
        float savedScale = RuntimeEnv.GUIScale;
        var io = ImGui.GetIO(); Vector2 display = io.DisplaySize;
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            byte[] pixels = [29, 33, 39, 255];
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            io.DisplaySize = new(960, 640); RuntimeEnv.GUIScale = 1;
            Vector2 min = new(12, 40), size = new(480, 270);
            void Viewport(Vector2 origin, Vector2 extent) => AccessTools.Method(hudType, "DrawCameraObservationViewport")
                .Invoke(hud, [origin, extent, Eye(), subject, texture, 16f / 9]);
            void Frame()
            {
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(520, 330));
                ImGui.Begin("observer-input-probe", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDecoration);
                ImGui.SetCursorScreenPos(min); Viewport(min, size); ImGui.End(); ImGui.Render();
            }
            foreach (int side in new[] { 0, 1, 2, 3, 4, 5 })
            {
                Snap(-1); io.MouseDown[0] = false; io.MousePos = new(-100); Frame(); Frame();
                Vector3 direction = side / 2 == 0 ? Vector3.UnitX : side / 2 == 1 ? Vector3.UnitY : Vector3.UnitZ;
                direction *= side % 2 == 0 ? 1 : -1;
                var projected = (Vector3)AccessTools.Method(stateType, "ViewDirection").Invoke(null, [Eye(), direction])!;
                Vector2 center = min + size - new Vector2(63);
                io.MousePos = center + new Vector2(projected.X, projected.Y) * 38.5f; Frame(); Frame();
                io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
                if (Side() != side) throw new Exception("Orientation gizmo did not snap when clicking endpoint " + side);
            }
            // The overlaid direction selector must receive clicks before the orbit surface.
            io.MousePos = min + new Vector2(50, 15); Frame(); Frame();
            io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
            if (!ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId))
                throw new Exception("Observer orbit surface blocked the direction dropdown.");
            io.MousePos = new(800, 500); Frame(); io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
            Snap(-1); io.MousePos = min + size / 2; Frame(); Frame();
            float distance = (float)AccessTools.Property(stateType, "Distance").GetValue(state)!;
            io.MouseWheel = 1; Frame();
            if ((float)AccessTools.Property(stateType, "Distance").GetValue(state)! >= distance)
                throw new Exception("Observer viewport ignored wheel zoom.");
            io.MouseDown[0] = true; Frame(); io.MousePos += new Vector2(40, 20); Frame(); Frame(); io.MouseDown[0] = false; Frame();
            if (Math.Abs((float)AccessTools.Property(stateType, "Yaw").GetValue(state)! - 35) < 1)
                throw new Exception("Observer viewport ignored orbit dragging.");

            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                for (int frame = 0; frame < 2; frame++)
                {
                    ImGui.NewFrame();
                    int[] sides = [-1, 0, 2, 1, 3, 5];
                    for (int i = 0; i < sides.Length; i++)
                    {
                        Snap(sides[i]);
                        ImGui.SetNextWindowPos(new(i % 3 * 320, i / 3 * 300)); ImGui.SetNextWindowSize(new(320, 290));
                        ImGui.Begin("观察视角 · " + i, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar);
                        Vector2 origin = ImGui.GetCursorScreenPos(), extent = new(300, 300 * 9f / 16);
                        ImGui.SetCursorScreenPos(origin); Viewport(origin, extent);
                        ImGui.End();
                    }
                    ImGui.Render();
                }
                capture("camera-observation-" + (int)(scale * 100));
            }
            Console.WriteLine("PASS observation overlay matches actual offscreen matrices; six clickable gizmo directions, orbit/zoom, dropdown input and UI at 100/150/200%");
        }
        finally
        {
            RuntimeEnv.GUIScale = savedScale; io.DisplaySize = display;
            io.MouseDown[0] = false; io.MouseWheel = 0; GL.DeleteTexture(texture);
        }
    }
}
