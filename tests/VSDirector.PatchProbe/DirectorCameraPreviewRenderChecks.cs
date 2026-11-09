using System.Numerics;
using System.Reflection;
using HarmonyLib;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
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
            // Compare against native world-up LookAt, independently of the
            // editor basis helper so both implementations cannot share a flip.
            var sightline = EntityPos.GetViewVector(pitch * GameMath.DEG2RAD, yaw * GameMath.DEG2RAD);
            var native = Mat4d.Create();
            Mat4d.LookAt(native, [0d, 0, 0], [(double)sightline.X, sightline.Y, sightline.Z], [0d, 1, 0]);
            var nativeRight = new Vector3((float)native[0], (float)native[4], (float)native[8]);
            var nativeUp = new Vector3((float)native[1], (float)native[5], (float)native[9]);
            // Native float trigonometry is ambiguous exactly at a pole; keep
            // the specified heading there rather than comparing float noise.
            if (Math.Abs(Math.Cos(pitch * Math.PI / 180)) < 1e-6)
            {
                double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180;
                nativeRight = new((float)Math.Cos(y), 0, (float)-Math.Sin(y));
                nativeUp = new((float)(Math.Sin(y) * Math.Sin(p)), 0, (float)(Math.Cos(y) * Math.Sin(p)));
            }
            float r = roll * MathF.PI / 180;
            var basis = (Right: nativeRight * MathF.Cos(r) - nativeUp * MathF.Sin(r),
                Up: nativeRight * MathF.Sin(r) + nativeUp * MathF.Cos(r));
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

        // Exercise the actual editor -> Euler -> preview matrix chain. A basis
        // test alone can miss rotating the silhouette instead of the shot.
        foreach (float pitch in new[] { 180f, 200f, 560f, -160f })
        foreach (float yaw in new[] { 100f, 460f })
        foreach (float roll in new[] { 0f, 30f, 380f })
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        foreach (float angle in new[] { 0f, .0001f, -.0001f, .4f })
        {
            var source = new CameraPreset { Pitch = pitch, Yaw = yaw, Roll = roll };
            var edited = DirectorCameraManipulation.Rotate(source, axis, angle);
            double[] Matrix(CameraPreset camera)
                => (((double[], double[], Vec3f))matrices.Invoke(null,
                    [new DirectorOffscreenCameraPose(0, 0, 0, 0, camera.Yaw, camera.Pitch, camera.Roll, 70)])!).Item2;
            var before = Matrix(source);
            var after = Matrix(edited);
            var rotation = Quaternion.CreateFromAxisAngle(axis, angle);
            for (int row = 0; row < 3; row++)
            {
                float sourceSign = row < 2 && Math.Cos(source.Pitch * Math.PI / 180) < 0 ? -1 : 1;
                float editedSign = row < 2 && Math.Cos(edited.Pitch * Math.PI / 180) < 0 ? -1 : 1;
                var expected = Vector3.Transform(new Vector3((float)before[row], (float)before[row + 4], (float)before[row + 8]) * sourceSign, rotation) * editedSign;
                var actual = new Vector3((float)after[row], (float)after[row + 4], (float)after[row + 8]);
                if (Vector3.Distance(expected, actual) > .00002f)
                    throw new Exception($"Rotation changed preview image axes at {pitch}/{yaw}/{roll}, axis {axis}, angle {angle}.");
            }
        }
        Console.WriteLine("PASS editor rotation preserves actual preview matrices at 200/100 and multi-turn Euler values, including zero and tiny drags on XYZ");

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
            var matrices = AccessTools.Method(assembly.GetType("VSDirector.Rendering.DirectorOffscreenEngineState", true)!, "CameraMatrices");
            foreach (float pitch in new[] { 0f, 35f, 180f, 200f })
            foreach (float yaw in new[] { 0f, 73f })
            foreach (float roll in new[] { 0f, 30f })
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, buffers[0]);
                GL.Enable(EnableCap.ScissorTest);
                // A composed world framebuffer retains OpenGL row order. Distinct
                // top-left, top-right, bottom-left and bottom-right landmarks must
                // arrive at the corresponding ImGui image corners.
                var colors = new[] { new Vector4(1, 0, 0, 1), new(0, 1, 0, 1), new(0, 0, 1, 1), new(1, 1, 0, 1) };
                var pose = new DirectorOffscreenCameraPose(30000000.125, 100, -30000000.875, 2, yaw, pitch, roll, 90);
                var (view, _, _) = ((double[], double[], Vec3f))matrices.Invoke(null, [pose])!;
                var forward = EntityPos.GetViewVector(pitch * GameMath.DEG2RAD, yaw * GameMath.DEG2RAD);
                double[] native = Mat4d.Create();
                Mat4d.LookAt(native, [0d, 0, 0], [(double)forward.X, forward.Y, forward.Z], [0d, 1, 0]);
                var right = new Vector3((float)native[0], (float)native[4], (float)native[8]);
                var up = new Vector3((float)native[1], (float)native[5], (float)native[9]);
                float angle = roll * MathF.PI / 180;
                (right, up) = (right * MathF.Cos(angle) - up * MathF.Sin(angle), right * MathF.Sin(angle) + up * MathF.Cos(angle));
                for (int corner = 0; corner < 4; corner++)
                {
                    // Place landmarks in world space using the native shooting
                    // frame, then project using the actual offscreen view matrix.
                    Vector3 offset = new Vector3(forward.X, forward.Y, forward.Z) * 4
                        + right * (corner % 2 == 0 ? -2 : 2) + up * (corner / 2 == 0 ? 2 : -2);
                    var point = new Vec4d(pose.X + offset.X, pose.Y + 2 * 32768d + offset.Y, pose.Z + offset.Z, 1);
                    var cameraPoint = new Vec4d(); Mat4d.MulWithVec4(view, point, cameraPoint);
                    if (cameraPoint.Z >= 0) throw new Exception("Native shooting landmarks ended up behind the preview camera.");
                    int x = (int)Math.Round((cameraPoint.X / -cameraPoint.Z * .5 + .5) * 64) - 16;
                    int y = (int)Math.Round((cameraPoint.Y / -cameraPoint.Z * .5 + .5) * 64) - 16;
                    GL.Scissor(x, y, 32, 32);
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
                        throw new Exception($"Preview framebuffer/ImGui image was inverted or mirrored at {yaw}/{pitch}/{roll}, corner {corner}: {pixel[0]},{pixel[1]},{pixel[2]} expected {color.X * 255},{color.Y * 255},{color.Z * 255}");
                }
            }
            Console.WriteLine("PASS native shooting world landmarks through preview matrices, real OpenGL blit and ImGui: top/bottom and left/right at normal/inverted pitch, headings, roll and large/dimension coordinates");
        }
        finally
        {
            io.DisplaySize = savedDisplay; GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffers(2, buffers); GL.DeleteTextures(2, textures);
        }
    }
}
