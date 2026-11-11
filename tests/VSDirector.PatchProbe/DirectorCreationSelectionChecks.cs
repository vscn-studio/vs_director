using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckCreationSelectionContour(Assembly assembly)
    {
        var rendererType = assembly.GetType("VSDirector.Rendering.DirectorCreationRenderer", true)!;
        var root = new CreationPart { Group = true };
        var child = new CreationPart { ParentId = root.Id };
        var clothing = new CreationPart { ParentId = child.Id };
        var other = new CreationPart();
        var model = new CreationModel { Parts = [root, child, clothing, other] };
        var branch = (HashSet<Guid>)AccessTools.Method(rendererType, "SelectionBranch").Invoke(null, [model, root.Id])!;
        if (!branch.SetEquals([root.Id, child.Id, clothing.Id])
            || ((HashSet<Guid>)AccessTools.Method(rendererType, "SelectionBranch").Invoke(null, [model, Guid.Empty])!).Count != 0)
            throw new Exception("Pose silhouette must merge only the chosen branch, including attached clothing.");

        var stateType = assembly.GetType("VSDirector.Rendering.DirectorCreationRenderState", true)!;
        var targetType = assembly.GetType("VSDirector.Rendering.DirectorCreationSelectionState", true)!;
        using var original = (IDisposable)Activator.CreateInstance(stateType, true)!;
        using var originalTarget = (IDisposable)Activator.CreateInstance(targetType, true)!;
        int maskProgram = 0, outlineProgram = 0, vao = GL.GenVertexArray(), buffer = GL.GenBuffer(), alphaTexture = GL.GenTexture();
        int[] textures = [GL.GenTexture(), GL.GenTexture()], targets = [GL.GenFramebuffer(), GL.GenFramebuffer()];
        string shaders = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/vsdirector/shaders"));
        int Program(string name)
        {
            int program = GL.CreateProgram();
            try
            {
                foreach (var (type, extension) in new[] { (ShaderType.VertexShader, ".vsh"), (ShaderType.FragmentShader, ".fsh") })
                {
                    int shader = GL.CreateShader(type);
                    try
                    {
                        GL.ShaderSource(shader, File.ReadAllText(Path.Combine(shaders, name + extension))); GL.CompileShader(shader);
                        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
                        if (ok == 0) throw new Exception(name + ": " + GL.GetShaderInfoLog(shader));
                        GL.AttachShader(program, shader);
                    }
                    finally { GL.DeleteShader(shader); }
                }
                GL.LinkProgram(program); GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
                if (linked == 0) throw new Exception(name + ": " + GL.GetProgramInfoLog(program));
                return program;
            }
            catch { GL.DeleteProgram(program); throw; }
        }
        object Begin(int index, bool union)
        {
            var saved = Activator.CreateInstance(targetType, true)!;
            AccessTools.Method(targetType, "Begin").Invoke(saved, [new FrameBufferRef { FboId = targets[index], Width = 64, Height = 64 }, union]);
            return saved;
        }
        void Quad(float x, float y, float X, float Y, float red, float green, bool textured = false)
        {
            float[] data = [x,y,0,0,0, X,y,0,1,0, X,Y,0,1,1, x,y,0,0,0, X,Y,0,1,1, x,Y,0,0,1];
            GL.BindVertexArray(vao); GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StreamDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), 0); GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), 3 * sizeof(float)); GL.EnableVertexAttribArray(1);
            GL.UseProgram(maskProgram);
            GL.Uniform2(GL.GetUniformLocation(maskProgram, "channels"), red, green);
            GL.Uniform1(GL.GetUniformLocation(maskProgram, "textured"), textured ? 1 : 0);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }
        byte[] Pixel(int x, int y)
        {
            byte[] result = new byte[4]; GL.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, result); return result;
        }
        try
        {
            maskProgram = Program("creationselectionmask"); outlineProgram = Program("creationselectionoutline");
            for (int i = 0; i < 2; i++)
            {
                GL.BindTexture(TextureTarget.Texture2D, textures[i]);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 64, 64, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, targets[i]);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, textures[i], 0);
                if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                    throw new Exception("Pose selection framebuffer is incomplete.");
            }
            GL.UseProgram(maskProgram);
            float[] identity = [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
            foreach (string matrix in new[] { "projectionMatrix", "viewMatrix", "modelMatrix" })
                GL.UniformMatrix4(GL.GetUniformLocation(maskProgram, matrix), 1, false, identity);
            GL.Uniform1(GL.GetUniformLocation(maskProgram, "tex"), 0);

            // Deliberately inherit hostile UI state and different read/draw targets.
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, targets[1]);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, targets[0]);
            GL.Viewport(7, 9, 50, 40); GL.Enable(EnableCap.ScissorTest); GL.Scissor(1, 1, 2, 2);
            GL.Enable(EnableCap.StencilTest); GL.Enable(EnableCap.FramebufferSrgb);
            GL.ColorMask(true, false, true, false); GL.ClearColor(.2f, .3f, .4f, .5f);
            using ((IDisposable)Begin(0, true))
            {
                Quad(-.75f, -.5f, 0, .5f, 1, 0);
                Quad(0, -.5f, .75f, .5f, 1, 0);
                Quad(-.25f, 0, .25f, .75f, 0, 1);
                var overlap = Pixel(32, 40);
                if (overlap[0] != 255 || overlap[1] != 255) throw new Exception("Overlapping selected and hovered branches must preserve both mask channels.");
            }
            int[] viewport = new int[4]; bool[] colorMask = new bool[4]; float[] clear = new float[4];
            GL.GetInteger(GetPName.Viewport, viewport); GL.GetBoolean(GetPName.ColorWritemask, colorMask); GL.GetFloat(GetPName.ColorClearValue, clear);
            if (!viewport.SequenceEqual(new[] { 7,9,50,40 }) || !colorMask.SequenceEqual(new[] { true,false,true,false })
                || Math.Abs(clear[2] - .4f) > .001 || !GL.IsEnabled(EnableCap.ScissorTest) || !GL.IsEnabled(EnableCap.StencilTest)
                || !GL.IsEnabled(EnableCap.FramebufferSrgb) || GL.GetInteger(GetPName.DrawFramebufferBinding) != targets[1]
                || GL.GetInteger(GetPName.ReadFramebufferBinding) != targets[0])
                throw new Exception("Pose selection leaked framebuffer/viewport/UI clipping or color state.");

            using ((IDisposable)Begin(1, false))
            {
                GL.UseProgram(outlineProgram); GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, textures[0]);
                GL.Uniform1(GL.GetUniformLocation(outlineProgram, "maskTex"), 0);
                GL.Uniform1(GL.GetUniformLocation(outlineProgram, "radius"), 2);
                GL.Uniform4(GL.GetUniformLocation(outlineProgram, "selectedColor"), 1f, .82f, .44f, 1f);
                GL.Uniform4(GL.GetUniformLocation(outlineProgram, "hoveredColor"), .5f, .82f, .94f, 1f);
                float[] triangle = [-1,-1,0, 3,-1,0, -1,3,0];
                GL.BindVertexArray(vao); GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
                GL.BufferData(BufferTarget.ArrayBuffer, triangle.Length * sizeof(float), triangle, BufferUsageHint.StreamDraw);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
                GL.DisableVertexAttribArray(1); GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                if (Pixel(31, 24)[3] > 25 || Pixel(32, 24)[3] > 25) throw new Exception("Adjacent selected cubes show an internal edge.");
                if (Pixel(7, 24)[3] != 255 || Pixel(6, 24)[3] != 255 || Pixel(5, 24)[3] != 0)
                    throw new Exception("Pose contour must be exactly two pixels outside the silhouette.");
                if (Pixel(32, 40)[0] != 255 || Pixel(32, 56)[2] < 230 || Pixel(32, 56)[0] > 140)
                    throw new Exception("Selected and hovered silhouettes do not use distinct colors/selection priority.");
                if (Pixel(0, 0)[3] != 0) throw new Exception("Pose contour texture is not transparent outside the selection.");
            }
            using ((IDisposable)Begin(0, true))
            {
                GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, alphaTexture);
                byte[] alpha = [255,255,255,255, 255,255,255,0];
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 2, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, alpha);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                Quad(-1, -1, 1, 1, 1, 0, true);
                if (Pixel(8, 32)[0] != 255 || Pixel(56, 32)[0] != 0) throw new Exception("Pose silhouette fills transparent texture areas.");
            }
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Pose silhouette rendering produced an OpenGL error.");
            Console.WriteLine("PASS pose silhouette: merged branch/clothing, no cube seams, distinct hover/selection, two-pixel contour, texture alpha and exact framebuffer/UI state restoration");
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteProgram(maskProgram); GL.DeleteProgram(outlineProgram);
            GL.DeleteVertexArray(vao); GL.DeleteBuffer(buffer); GL.DeleteTexture(alphaTexture);
            foreach (int texture in textures) GL.DeleteTexture(texture);
            foreach (int target in targets) GL.DeleteFramebuffer(target);
        }
    }
}
