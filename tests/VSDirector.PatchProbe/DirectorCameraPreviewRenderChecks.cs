using System.Numerics;
using System.Reflection;
using HarmonyLib;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using VSDirector.Rendering;

internal static partial class DirectorImGuiProbe
{
    private static void CheckOffscreenCamera(Assembly assembly)
    {
        var matrices = AccessTools.Method(assembly.GetType("VSDirector.Rendering.DirectorOffscreenEngineState", true)!, "CameraMatrices");
        var enter = AccessTools.Method(assembly.GetType("VSDirector.Rendering.DirectorOffscreenRenderRuntime", true)!, "Enter");
        foreach (float yaw in new[] { 0f, 45, 90, 180, 270, 359 })
        foreach (float pitch in new[] { -100f, -90, -89.999f, -45, 0, 45, 89.999f, 90, 100 })
        foreach (float roll in new[] { 0f, 30, 90, 180, 270 })
        foreach (int dimension in new[] { 0, 2 })
        {
            var pose = new DirectorOffscreenCameraPose(30000000.125, 100.25, -30000000.875, dimension, yaw, pitch, roll, 70);
            var (view, origin, forward) = ((double[], double[], Vec3f))matrices.Invoke(null, [pose])!;
            var basis = DirectorCameraManipulation.Basis(new CameraPreset { Yaw = yaw, Pitch = pitch, Roll = roll });
            var eye = new Vec4d(pose.X, pose.Y + dimension * 32768d, pose.Z, 1);
            Vec4d Transform(Vec4d point)
            {
                var result = new Vec4d(); Mat4d.MulWithVec4(view, point, result); return result;
            }
            void Near(double actual, double expected)
            {
                if (!double.IsFinite(actual) || Math.Abs(actual - expected) > .00002)
                    throw new Exception($"Offscreen camera orientation mismatch at {yaw}/{pitch}/{roll}: {actual} != {expected}.");
            }
            var eyeInView = Transform(eye);
            Near(eyeInView.X, 0); Near(eyeInView.Y, 0); Near(eyeInView.Z, 0);
            var ahead = Transform(new(eye.X + forward.X * 10, eye.Y + forward.Y * 10, eye.Z + forward.Z * 10, 1));
            Near(ahead.X, 0); Near(ahead.Y, 0); Near(ahead.Z, -10);
            var up = Transform(new(eye.X + basis.Up.X, eye.Y + basis.Up.Y, eye.Z + basis.Up.Z, 1));
            var right = Transform(new(eye.X + basis.Right.X, eye.Y + basis.Right.Y, eye.Z + basis.Right.Z, 1));
            Near(up.X, 0); Near(up.Y, 1); Near(right.X, 1); Near(right.Y, 0);
            for (int row = 0; row < 3; row++)
            for (int other = 0; other < 3; other++)
                Near(origin[row] * origin[other] + origin[row + 4] * origin[other + 4] + origin[row + 8] * origin[other + 8], row == other ? 1 : 0);
        }

        // Real engine culler and patched visibility: chunks occluded in the
        // editor must still appear when an offscreen camera faces them.
        var culler = new FrustumCulling { lod0BiasSq = 4096, shadowRangeX = 128, shadowRangeZ = 128 };
        culler.UpdateViewDistance(128);
        double[] projection = Mat4d.Create(); Mat4d.Perspective(projection, Math.PI / 2, 16d / 9, .1, 128);
        foreach (float yaw in new[] { 0f, 90, 180, 270 })
        {
            var pose = new DirectorOffscreenCameraPose(0, 100, 0, 0, yaw, 0, 0, 90);
            var (view, _, forward) = ((double[], double[], Vec3f))matrices.Invoke(null, [pose])!;
            var location = new ModelDataPoolLocation { FrustumCullSphere = new Sphere(forward.X * 20, 100, forward.Z * 20, 1, 1, 1), LodLevel = 1, FrustumVisible = true };
            location.CullVisible[0] = location.CullVisible[1] = false;
            culler.CalcFrustumEquations(new(0, 100, 0, 0), projection, view);
            if (location.IsVisible(EnumFrustumCullMode.CullNormal, culler)) throw new Exception("Primary terrain ignored its native cull cache.");
            using ((IDisposable)enter.Invoke(null, [new DirectorOffscreenViewRequest { Pose = pose }, 1L])!)
            {
                culler.CalcFrustumEquations(new(999, 100, 999, 0), projection, view);
                if (!location.IsVisible(EnumFrustumCullMode.CullNormal, culler)
                    || !location.IsVisible(EnumFrustumCullMode.CullInstant, culler)
                    || !location.IsVisible(EnumFrustumCullMode.CullInstantShadowPassNear, culler)
                    || !location.IsVisible(EnumFrustumCullMode.CullInstantShadowPassFar, culler))
                    throw new Exception("Preview terrain still depends on the editor's visibility or range origin.");
                location.Hide = true;
                if (location.IsVisible(EnumFrustumCullMode.CullNormal, culler)) throw new Exception("Preview showed hidden terrain.");
                location.Hide = false;
                location.FrustumCullSphere = new Sphere(-forward.X * 20, 100, -forward.Z * 20, 1, 1, 1);
                if (location.IsVisible(EnumFrustumCullMode.CullNormal, culler)) throw new Exception("Preview rendered terrain behind its camera.");
            }
            if (location.CullVisible[0] || location.CullVisible[1] || !location.FrustumVisible)
                throw new Exception("Preview changed the primary terrain visibility buffers.");
        }
        Console.WriteLine("PASS offscreen yaw/pitch/roll, vertical crossings, large coordinates/dimensions and independent terrain/frustum/shadow visibility");
    }

    private static void CheckCameraPreviewTexture(Assembly assembly, IDisposable renderer)
    {
        var apiType = assembly.GetType("VSDirector.Rendering.DirectorOpenGlFramebufferApi", true)!;
        object?[] create = [null, null];
        if (!(bool)AccessTools.Method(apiType, "TryCreate").Invoke(null, create)!) throw new Exception("Preview framebuffer API unavailable.");
        int[] textures = [GL.GenTexture(), GL.GenTexture()], buffers = [GL.GenFramebuffer(), GL.GenFramebuffer()];
        var io = ImGui.GetIO(); Vector2 savedDisplay = io.DisplaySize;
        try
        {
            for (int i = 0; i < 2; i++)
            {
                GL.BindTexture(TextureTarget.Texture2D, textures[i]);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 64, 64, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, buffers[i]);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, textures[i], 0);
                if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) throw new Exception("Preview test framebuffer incomplete.");
            }
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, buffers[0]);
            GL.Enable(EnableCap.ScissorTest);
            // The preview's final frame has image row order (v=0 is the top).
            // Simulate that input explicitly, rather than treating this as a
            // raw world framebuffer whose OpenGL bottom should stay at bottom.
            var colors = new[] { new Vector4(1, 0, 0, 1), new(0, 1, 0, 1), new(0, 0, 1, 1), new(1, 1, 0, 1) };
            for (int corner = 0; corner < 4; corner++)
            {
                GL.Scissor(corner % 2 * 32, corner / 2 * 32, 32, 32);
                GL.ClearColor(colors[corner].X, colors[corner].Y, colors[corner].Z, 1); GL.Clear(ClearBufferMask.ColorBufferBit);
            }
            GL.Disable(EnableCap.ScissorTest);
            AccessTools.Method(apiType, "BlitColor").Invoke(create[0], [new FrameBufferRef { FboId = buffers[0], Width = 64, Height = 64 }, new FrameBufferRef { FboId = buffers[1], Width = 64, Height = 64 }]);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, 960, 640); GL.ClearColor(0, 0, 0, 1); GL.Clear(ClearBufferMask.ColorBufferBit);
            io.DisplaySize = new(960, 640);
            Vector2 imagePosition = default;
            for (int frame = 0; frame < 2; frame++)
            {
                ImGui.NewFrame(); ImGui.SetNextWindowPos(new(0)); ImGui.SetNextWindowSize(new(200));
                ImGui.Begin("preview-texture-probe", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
                imagePosition = ImGui.GetCursorScreenPos();
                AccessTools.Method(assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud", true)!, "DrawCameraPreviewImage").Invoke(null, [textures[1], new Vector2(64)]);
                ImGui.End(); ImGui.Render();
            }
            AccessTools.Method(renderer.GetType(), "Render").Invoke(renderer, null);
            for (int corner = 0; corner < 4; corner++)
            {
                byte[] pixel = new byte[4];
                GL.ReadPixels((int)imagePosition.X + corner % 2 * 32 + 16,
                    640 - (int)imagePosition.Y - corner / 2 * 32 - 16, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
                var color = colors[corner];
                if (Math.Abs(pixel[0] - color.X * 255) > 2 || Math.Abs(pixel[1] - color.Y * 255) > 2 || Math.Abs(pixel[2] - color.Z * 255) > 2)
                    throw new Exception($"Preview framebuffer/ImGui image was inverted or mirrored at corner {corner}: {pixel[0]},{pixel[1]},{pixel[2]} expected {color.X * 255},{color.Y * 255},{color.Z * 255}");
            }
            Console.WriteLine("PASS preview image row order through real OpenGL blit and ImGui: top/bottom and left/right corners remain in place");
        }
        finally
        {
            io.DisplaySize = savedDisplay; GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffers(2, buffers); GL.DeleteTextures(2, textures);
        }
    }
}
