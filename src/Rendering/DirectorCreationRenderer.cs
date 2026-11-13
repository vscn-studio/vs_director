using static VSDirector.Gui.DirectorCreationText;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Data;

namespace VSDirector.Rendering;

/// <summary>Local visual objects; meshes and poses never modify the game's shared shapes.</summary>
internal sealed partial class DirectorCreationRenderer : IRenderer
{
    private readonly ICoreClientAPI api;
    private readonly DirectorCreationSession session;
    private readonly ClientMain game;
    private IShaderProgram? shader;
    private readonly Dictionary<string, MeshRef> meshes = new();
    private readonly Dictionary<string, MultiTextureMeshRef> nativeMeshes = new();
    internal Dictionary<Guid, ItemRenderInfo> HeldItems { get; } = new();
    private readonly Dictionary<string, int> textures = new();
    private readonly List<LoadedTexture> ownedTextures = new();
    private long revision = -1;
    private readonly Dictionary<string, string> geometry = new();
    private MeshRef Geometry(string key, string signature, Func<MeshData> build)
    {
        if (meshes.TryGetValue(key, out var mesh) && geometry.GetValueOrDefault(key) == signature) return mesh;
        mesh?.Dispose(); geometry[key] = signature;
        return meshes[key] = api.Render.UploadMesh(build());
    }
    internal bool Visible { get; set; } = true;
    internal bool NativeParticlesActive { get; set; }
    internal string Error { get; private set; } = "";
    public double RenderOrder => .69;
    public int RenderRange => 9999;
    internal DirectorCreationRenderer(ICoreClientAPI api, DirectorCreationSession session)
    {
        this.api = api; this.session = session; game = (ClientMain)api.World;
        api.Event.RegisterRenderer(this, EnumRenderStage.AfterOIT, "vsdirector-creation");
        api.Event.ReloadShader += ReloadShader;
    }
    private bool ReloadShader()
    {
        DisposeSelectionShaders();
        shader?.Dispose(); shader = api.Shader.NewShaderProgram(); shader.AssetDomain = "vsdirector";
        shader.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader);
        shader.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader);
        api.Shader.RegisterFileShaderProgram("creation", shader); return shader.Compile();
    }
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!Visible || session.Project is not { } project || session.Frame is not { } frame || api.World.Player?.Entity is not { } player) return;
        if (project.WorldId.Length > 0 && project.WorldId != api.World.SavegameIdentifier) return;
        if (project.Dimension != player.Pos.Dimension) return;
        IShaderProgram? previous = api.Render.CurrentActiveShader;
        DirectorCreationRenderState? saved = null;
        try
        {
            saved = new();
            if (shader is null && !ReloadShader()) { Error = L("creation-creation-shader-compilation-failed"); return; }
            bool geometryChanged = revision != session.Revision;
            previous?.Stop();
            game.Platform.GlEnableDepthTest(); game.Platform.GlDepthMask(false); game.Platform.GlDisableCullFace(); game.Platform.GlToggleBlend(on: true); shader!.Use();
            shader.UniformMatrix("projectionMatrix", game.CurrentProjectionMatrix);
            shader.Uniform("lightDirection", game.shUniforms.LightPosition3D);
            shader.Uniform("fogColor", api.Render.FogColor);
            shader.Uniform("fogDensity", api.Render.FogDensity);
            shader.Uniform("fogMinimum", api.Render.FogMin);
            // Share the exact double-precision world-view basis with viewport
            // picking/gizmos, including native eye offsets and offscreen views.
            shader.UniformMatrix("viewMatrix", DirectorCreationMath.ProjectView(game.PerspectiveViewMat, project.Origin, project.Dimension * (double)BlockPos.DimensionBoundary));
            CreationVector origin = default;
            var cameraLocal = new CreationVector(player.CameraPos.X, player.CameraPos.Y - project.Dimension * (double)BlockPos.DimensionBoundary, player.CameraPos.Z) - project.Origin;
            // Far-to-near alpha blending; retain world occlusion without writing particle/plane depth.
            foreach (var state in frame.Objects.Values.Where(s => s.Visible)
                .OrderBy(s => s.Source.Opacity < .999 || s.Source.Kind is CreationObjectKind.Image or CreationObjectKind.Text or CreationObjectKind.Particle)
                .ThenByDescending(s => Vector3.DistanceSquared(s.World.Translation, cameraLocal.Float())))
            {
                var obj = state.Source;
                if (project.Visuals.World.ShowEntities == false && obj.Kind is CreationObjectKind.Model or CreationObjectKind.Item) continue;
                if (!project.Visuals.World.ShowPlayers && obj.ActorSource?.EntityCode.Contains("player", StringComparison.OrdinalIgnoreCase) == true) continue;
                if (!project.Visuals.Particles.ShowParticles && obj.Kind == CreationObjectKind.Particle) continue;
                game.Platform.GlDepthMask(obj.Opacity >= .999 && obj.Kind is CreationObjectKind.Model or CreationObjectKind.Item or CreationObjectKind.Structure);
                shader.Uniform("tint", (float)state.Color.X, (float)state.Color.Y, (float)state.Color.Z, (float)obj.Opacity);
                shader.Uniform("glow", (float)obj.Glow);
                shader.Uniform("surfaceShading", obj.Kind is CreationObjectKind.Model or CreationObjectKind.Item or CreationObjectKind.Structure ? 1 : 0);
                var lightPosition = CreationVector.From(state.World.Translation) + project.Origin;
                var light = api.World.BlockAccessor.GetLightRGBs(new BlockPos(
                    (int)Math.Floor(lightPosition.X),
                    (int)Math.Floor(lightPosition.Y + .75) + project.Dimension * BlockPos.DimensionBoundary,
                    (int)Math.Floor(lightPosition.Z)));
                var ambient = api.Render.AmbientColor;
                shader.Uniform("worldLight", Math.Max(light.X, light.W * ambient.X),
                    Math.Max(light.Y, light.W * ambient.Y), Math.Max(light.Z, light.W * ambient.Z));
                if (obj.Kind is CreationObjectKind.Item or CreationObjectKind.Structure)
                {
                    RenderNativeObject(project, state, obj, origin);
                }
                else if (obj.Kind == CreationObjectKind.Model)
                {
                    var model = project.Models.Find(m => m.Id == obj.ModelId); if (model is null) continue;
                    var deformations=DirectorCreationJointSeams.Deformations(model,state);
                    foreach (var part in model.Parts.Where(p => !p.Group && p.Visible && !state.HiddenParts.Contains(p.Id)))
                    {
                        if (!state.Bones.TryGetValue(part.Id, out var bone)) continue;
                        var matrix = bone * state.World * Matrix4x4.CreateTranslation(origin.Float());
                        shader.UniformMatrix("modelMatrix", Array(matrix));
                        foreach (var face in part.Faces)
                        {
                            if (face.Value is not JsonObject f || f["enabled"]?.ToString() == "false") continue;
                            var mesh = ModelFaceMesh(model, part, face.Key, f, obj.Id, deformations, geometryChanged);
                            string texture = DirectorCreationShape.S(f, "texture", "#all").TrimStart('#');
                            BindTexture(project, model.Textures.GetValueOrDefault(texture, ""));
                            game.Platform.RenderMesh(mesh);
                        }
                    }
                }
                else if (obj.Kind is CreationObjectKind.Image or CreationObjectKind.Text)
                {
                    string key = "plane/" + obj.Id;
                    if (!meshes.TryGetValue(key, out var mesh) || geometryChanged)
                        mesh = Geometry(key, JsonSerializer.Serialize(new { obj.ImageSize, obj.Crop }), () => Plane(obj));
                    Matrix4x4 world = state.World;
                    if (obj.Billboard)
                    {
                        Matrix4x4.Invert(new Matrix4x4(game.MainCamera.CameraMatrixOriginf[0], game.MainCamera.CameraMatrixOriginf[1], game.MainCamera.CameraMatrixOriginf[2], 0,
                            game.MainCamera.CameraMatrixOriginf[4], game.MainCamera.CameraMatrixOriginf[5], game.MainCamera.CameraMatrixOriginf[6], 0,
                            game.MainCamera.CameraMatrixOriginf[8], game.MainCamera.CameraMatrixOriginf[9], game.MainCamera.CameraMatrixOriginf[10], 0, 0, 0, 0, 1), out var facing);
                        world = Matrix4x4.CreateScale(state.Transform.Scale.Float()) * facing * Matrix4x4.CreateTranslation(state.World.Translation);
                    }
                    shader.UniformMatrix("modelMatrix", Array(world * Matrix4x4.CreateTranslation(origin.Float())));
                    if (obj.Kind == CreationObjectKind.Text) BindText(obj);
                    else BindTexture(project, project.Assets.Find(a => a.Id == obj.AssetId)?.Source ?? "");
                    game.Platform.RenderMesh(mesh);
                }
                else if (obj.Kind == CreationObjectKind.Particle)
                {
                    RenderParticles(state, obj, origin);
                }
            }
            revision = session.Revision; Error = "";
        }
        catch (Exception e) { Error = Message(e.Message); }
        finally { shader?.Stop(); saved?.Dispose(); previous?.Use(); }
    }
    private void RenderParticles(CreationObjectState state, CreationSceneObject obj, CreationVector origin)
    {
        if (NativeParticlesActive && obj.NativeParticles) return;
        string key = "particle-plane";
        if (!meshes.TryGetValue(key, out var mesh)) meshes[key] = mesh = api.Render.UploadMesh(Quad(
            [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)], 0, 0, 1, 1, 0));
        Matrix4x4 facing = Matrix4x4.Identity;
        if (Matrix4x4.Invert(new Matrix4x4(game.MainCamera.CameraMatrixOriginf[0], game.MainCamera.CameraMatrixOriginf[1], game.MainCamera.CameraMatrixOriginf[2], 0,
            game.MainCamera.CameraMatrixOriginf[4], game.MainCamera.CameraMatrixOriginf[5], game.MainCamera.CameraMatrixOriginf[6], 0,
            game.MainCamera.CameraMatrixOriginf[8], game.MainCamera.CameraMatrixOriginf[9], game.MainCamera.CameraMatrixOriginf[10], 0, 0, 0, 0, 1), out var inv)) facing = inv;
        shader!.Uniform("textured", 0);
        foreach (var particle in DirectorCreationParticles.Sample(obj, session.Frame!.Time))
        {
            Vector3 center = Vector3.Transform(particle.Position.Float(), state.World) + origin.Float();
            Matrix4x4 world = Matrix4x4.CreateScale((float)particle.Size) * facing * Matrix4x4.CreateTranslation(center);
            shader.Uniform("tint", (float)state.Color.X, (float)state.Color.Y, (float)state.Color.Z, (float)(obj.Opacity * particle.Alpha));
            shader.Uniform("glow", (float)Math.Clamp(obj.Glow + 0.2, 0, 1));
            shader.UniformMatrix("modelMatrix", Array(world));
            game.Platform.RenderMesh(mesh);
        }
    }
    private void RenderNativeObject(DirectorCreationProject project, CreationObjectState state, CreationSceneObject obj, CreationVector origin)
    {
        if (obj.Kind == CreationObjectKind.Item)
        {
            if (HeldItems.TryGetValue(obj.Id, out var held))
            {
                shader!.Uniform("textured", 1);
                shader.UniformMatrix("modelMatrix", Array(state.World * Matrix4x4.CreateTranslation(origin.Float())));
                api.Render.RenderMultiTextureMesh(held.ModelRef, "tex");
                return;
            }
            string code = (state.ResourceOverride ?? obj.ResourceCode).Trim();
            if (code.Length == 0) return;
            string key = "item:" + code;
            if (!nativeMeshes.TryGetValue(key, out var mesh))
            {
                var location = new AssetLocation(code);
                MeshData? data = null;
                var item = api.World.GetItem(location);
                if (item is not null) api.Tesselator.TesselateItem(item, out data);
                else if (api.World.GetBlock(location) is { } block) api.Tesselator.TesselateBlock(block, out data);
                else return;
                mesh = api.Render.UploadMultiTextureMesh(data);
                nativeMeshes[key] = mesh;
            }
            shader!.Uniform("textured", 1);
            shader.UniformMatrix("modelMatrix", Array(state.World * Matrix4x4.CreateTranslation(origin.Float())));
            api.Render.RenderMultiTextureMesh(mesh, "tex");
            return;
        }
        if (obj.Blocks.Count == 0) return;
        foreach (var blockData in obj.Blocks)
        {
            if (string.IsNullOrWhiteSpace(blockData.Code)) continue;
            string key = "block:" + blockData.Code;
            if (!nativeMeshes.TryGetValue(key, out var mesh))
            {
                var block = api.World.GetBlock(new AssetLocation(blockData.Code));
                if (block is null || block.Id == 0) continue;
                api.Tesselator.TesselateBlock(block, out var data);
                mesh = api.Render.UploadMultiTextureMesh(data);
                nativeMeshes[key] = mesh;
            }
            shader!.Uniform("textured", 1);
            Matrix4x4 local = Matrix4x4.CreateTranslation(blockData.Position.Float());
            shader.UniformMatrix("modelMatrix", Array(local * state.World * Matrix4x4.CreateTranslation(origin.Float())));
            api.Render.RenderMultiTextureMesh(mesh, "tex");
        }
    }
    private void BindText(CreationSceneObject obj)
    {
        string key = "text:" + obj.Text;
        if (!textures.TryGetValue(key, out int id))
        {
            var texture = api.Gui.TextTexture.GenTextTexture(obj.Text, CairoFont.WhiteSmallText());
            ownedTextures.Add(texture); textures[key] = id = texture.TextureId;
        }
        shader!.Uniform("textured", 1); shader.BindTexture2D("tex", id, 0);
    }
    private void BindTexture(DirectorCreationProject project, string source, IShaderProgram? target = null)
    {
        var program = target ?? shader!;
        if (source.Length == 0) { program.Uniform("textured", 0); return; }
        string key = project.Id + "/" + source;
        if (!textures.TryGetValue(key, out int texture))
        {
            if (source.StartsWith("assets/", StringComparison.Ordinal))
            {
                string path = session.Store.AssetPath(project.Id, source);
                using var stream = new MemoryStream(File.ReadAllBytes(path)); using var bitmap = new BitmapExternal(stream, api.Logger);
                var loaded = new LoadedTexture(api); api.Render.LoadTexture(bitmap, ref loaded); ownedTextures.Add(loaded); texture = loaded.TextureId;
            }
            else
            {
                var location = new AssetLocation(source); location.Path = "textures/" + location.Path.Replace("textures/", "");
                if (!location.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) location.Path += ".png";
                texture = api.Render.GetOrLoadTexture(location);
            }
            textures[key] = texture;
        }
        program.Uniform("textured", 1); program.BindTexture2D("tex", texture, 0);
    }
    private static MeshData Plane(CreationSceneObject obj)
    {
        float w = (float)obj.ImageSize.X, h = (float)obj.ImageSize.Y;
        var c = obj.Crop.Length == 4 ? obj.Crop : new double[] { 0, 0, 1, 1 };
        return Quad([new(-w / 2, 0, 0), new(w / 2, 0, 0), new(w / 2, h, 0), new(-w / 2, h, 0)], (float)c[0], (float)c[1], (float)c[2], (float)c[3], 0);
    }
    private static MeshData CubeFace(CreationPart part, string side, JsonObject face, CreationModel model)
    {
        var size = ((part.To - part.From) * (1d / 16)).Float(); float d = (float)part.Inflate / 16;
        float x = -d, y = -d, z = -d, X = size.X + d, Y = size.Y + d, Z = size.Z + d;
        Vector3[] corners = side switch
        {
            "north" => [new(X,y,z),new(x,y,z),new(x,Y,z),new(X,Y,z)],
            "south" => [new(x,y,Z),new(X,y,Z),new(X,Y,Z),new(x,Y,Z)],
            "east" => [new(X,y,Z),new(X,y,z),new(X,Y,z),new(X,Y,Z)],
            "west" => [new(x,y,z),new(x,y,Z),new(x,Y,Z),new(x,Y,z)],
            "up" => [new(x,Y,Z),new(X,Y,Z),new(X,Y,z),new(x,Y,z)],
            _ => [new(x,y,z),new(X,y,z),new(X,y,Z),new(x,y,Z)]
        };
        var uv = face["uv"] as JsonArray;
        float U(int i, int dimension) => uv is not null && uv.Count > i ? (float)DirectorCreationShape.Number(uv[i]) / Math.Max(1, dimension) : (i >= 2 ? 1 : 0);
        string textureCode = DirectorCreationShape.S(face, "texture").TrimStart('#');
        var dimensions = (model.Extra["textureSizes"] as JsonObject)?[textureCode] as JsonArray;
        int textureWidth = dimensions is { Count: >= 2 } ? (int)DirectorCreationShape.Number(dimensions[0], model.TextureWidth) : model.TextureWidth;
        int textureHeight = dimensions is { Count: >= 2 } ? (int)DirectorCreationShape.Number(dimensions[1], model.TextureHeight) : model.TextureHeight;
        return Quad(corners, U(0,textureWidth), U(1,textureHeight), U(2,textureWidth), U(3,textureHeight), (int)DirectorCreationShape.N(face,"rotation"));
    }
    private static MeshData Quad(Vector3[] corners, float u, float v, float U, float V, int rotation)
    {
        var mesh = new MeshData(4, 6, withNormals: false, withUv: true); mesh.SetMode(EnumDrawMode.Triangles);
        Vector2[] uv = [new(u,V), new(U,V), new(U,v), new(u,v)];
        for (int i = 0; i < 4; i++) { var p = corners[i]; var t = uv[(i + ((rotation / 90) % 4 + 4) % 4) % 4]; mesh.AddVertex(p.X,p.Y,p.Z,t.X,t.Y,ColorUtil.WhiteArgb); }
        foreach (int i in new[] { 0, 1, 2, 0, 2, 3 }) mesh.AddIndex(i); return mesh;
    }
    internal static float[] Array(Matrix4x4 m) => [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
    internal void ReloadTextures()
    {
        DisposeSelectionBuffers();
        foreach (var mesh in meshes.Values) mesh.Dispose(); meshes.Clear(); geometry.Clear();
        foreach (var t in ownedTextures) t.Dispose(); ownedTextures.Clear(); textures.Clear(); revision = -1;
        foreach (var mesh in nativeMeshes.Values) mesh.Dispose(); nativeMeshes.Clear();
    }
    public void Dispose()
    {
        api.Event.UnregisterRenderer(this, EnumRenderStage.AfterOIT); api.Event.ReloadShader -= ReloadShader;
        foreach (var mesh in meshes.Values) mesh.Dispose(); meshes.Clear(); ReloadTextures(); shader?.Dispose();
        DisposeSelectionShaders(); selectionTriangle?.Dispose(); selectionTriangle = null;
        foreach (var mesh in nativeMeshes.Values) mesh.Dispose(); nativeMeshes.Clear();
    }
}
