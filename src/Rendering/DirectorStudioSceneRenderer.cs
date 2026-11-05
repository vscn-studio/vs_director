using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Data;

namespace VSDirector.Rendering;

/// <summary>Static Studio instances rendered without spawning gameplay entities.</summary>
internal sealed class DirectorStudioSceneRenderer : IRenderer
{
    private readonly ICoreClientAPI api;
    private readonly DirectorStudioResourceProvider resources;
    private readonly System.Func<Guid, DirectorModelAsset?> findModel;
    private readonly DirectorStudioSceneRuntime<StudioMesh> runtime;
    private DirectorStudioProject? project;
    private readonly Matrixf matrix = new();
    private bool disposed;
    private bool dirty = true;
    internal double Time { get; set; }
    internal Guid Selected { get; set; }
    internal bool DrawInWorld { get; set; } = true;
    private readonly Dictionary<Guid, (MeshData Mesh, float[] Matrix)> pickMeshes = new();
    private float[]? pickView, pickProjection;
    private readonly Dictionary<Guid, DirectorActionEvaluator> evaluators = new();
    public double RenderOrder => .7;
    public int RenderRange => int.MaxValue;

    internal DirectorStudioSceneRenderer(ICoreClientAPI api, DirectorStudioResourceProvider resources, System.Func<Guid, DirectorModelAsset?> findModel)
    {
        this.api = api; this.resources = resources; this.findModel = findModel;
        runtime = new(BuildMesh);
        api.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "vsdirector-studio-scene");
    }
    internal void SetProject(DirectorStudioProject? value)
    {
        if (project?.Id != value?.Id) runtime.Clear();
        if (!ReferenceEquals(project, value)) dirty = true;
        project = value;
    }
    internal string? Error(DirectorStudioSceneObject item) => runtime.Error(item);
    internal void Invalidate() { dirty = true; evaluators.Clear(); }
    internal void Reload() { runtime.Clear(); dirty = true; }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!DrawInWorld || disposed || project is null || api.World is not ClientMain game || game.EntityPlayer is null) return;
        if (DirectorOffscreenRenderRuntime.IsRendering) return;
        RenderScene(game.EntityPlayer.CameraPos);
    }

    internal void RenderScene(Vec3d camera)
    {
        if (disposed || project is null) return;
        if (dirty) { runtime.Synchronize(project); dirty = false; }
        bool picking = !DirectorOffscreenRenderRuntime.IsRendering;
        if (picking) { pickMeshes.Clear(); pickView = (float[])api.Render.CameraMatrixOriginf.Clone(); pickProjection = (float[])api.Render.CurrentProjectionMatrix.Clone(); }
        var previous = api.Render.CurrentActiveShader;
        previous?.Stop();
        api.Render.GlDisableCullFace();
        try
        {
            runtime.Draw((instance, model) =>
            {
                var item = DirectorStudioTimeline.Sample(instance, Time);
                if (!item.Visible) return;
                var clip = instance.Actions.FirstOrDefault(c => Time >= c.Start && Time < c.Start + c.Duration);
                DirectorActionEvaluation? evaluation = null;
                if (clip is not null)
                {
                    if (!evaluators.TryGetValue(clip.Id, out var evaluator))
                    {
                        var action = clip.Action.DeepClone(); action.PlaybackSpeed = 1; action.LoopMode = DirectorActionLoopMode.Once;
                        evaluators[clip.Id] = evaluator = new(action);
                    }
                    evaluation = evaluator.Sample(clip.SourceTime(Time));
                    if (clip.RootMotion && evaluation.Layers.FirstOrDefault() is { } layer)
                    {
                        var start = evaluator.Sample(clip.Offset).Layers.FirstOrDefault()?.State;
                        if (start is not null)
                        {
                            double dx = layer.State.X - start.X, dz = layer.State.Z - start.Z, yaw = item.Yaw * GameMath.DEG2RAD;
                            item.X += (dx * Math.Cos(yaw) + dz * Math.Sin(yaw)) * item.Scale;
                            item.Y += (layer.State.Y - start.Y) * item.Scale;
                            item.Z += (dz * Math.Cos(yaw) - dx * Math.Sin(yaw)) * item.Scale;
                        }
                    }
                }
                model.Pose(api, evaluation, Time, clip?.Id ?? Guid.Empty);
                var shader = api.Render.PreparedStandardShader((int)item.X, (int)item.Y, (int)item.Z);
                try
                {
                    shader.ModelMatrix = matrix.Identity().Translate(item.X - camera.X, item.Y - camera.Y, item.Z - camera.Z)
                        .RotateY(item.Yaw * GameMath.DEG2RAD).RotateX(item.Pitch * GameMath.DEG2RAD)
                        .RotateZ(item.Roll * GameMath.DEG2RAD).Scale(item.Scale, item.Scale, item.Scale).Values;
                    if (picking) pickMeshes[item.Id] = (model.Geometry, (float[])matrix.Values.Clone());
                    shader.ViewMatrix = api.Render.CameraMatrixOriginf;
                    shader.ProjectionMatrix = api.Render.CurrentProjectionMatrix;
                    // Standard's sampler is "tex", not "tex2d". Use its typed
                    // API so the engine owns sampler naming and texture units.
                    for (int index = 0; index < model.Mesh.meshrefs.Length; index++)
                    {
                        shader.Tex2D = model.Mesh.textureids[index];
                        api.Render.RenderMesh(model.Mesh.meshrefs[index]);
                    }
                }
                finally { shader.Stop(); }
            }, (item, error) => api.Logger.Error("VS Director Studio: disabled rendering for model {0}: {1}. Reload scene models to retry.", item.Name, error));
        }
        finally { api.Render.GlEnableCullFace(); previous?.Use(); }
    }

    internal Guid Pick(double x, double y)
    {
        if (pickView is null || pickProjection is null || !DirectorStudioPicking.Ray(x, y, pickView, pickProjection, out var origin, out var direction)) return Guid.Empty;
        double closest = double.MaxValue; Guid selected = Guid.Empty;
        foreach (var (id, (mesh, transform)) in pickMeshes)
        {
            var m = DirectorStudioPicking.Matrix(transform);
            System.Numerics.Vector3 Vertex(int index) => System.Numerics.Vector3.Transform(new(mesh.xyz[index * 3], mesh.xyz[index * 3 + 1], mesh.xyz[index * 3 + 2]), m);
            for (int i = 0; i + 2 < mesh.IndicesCount; i += 3)
                if (DirectorStudioPicking.Triangle(origin, direction, Vertex(mesh.Indices[i]), Vertex(mesh.Indices[i + 1]), Vertex(mesh.Indices[i + 2])) is double t && t < closest)
                { closest = t; selected = id; }
        }
        return selected;
    }

    internal (double X, double Y, double Width, double Height)? SelectionBounds()
    {
        if (pickView is null || pickProjection is null || !pickMeshes.TryGetValue(Selected, out var target)) return null;
        var m = DirectorStudioPicking.Matrix(target.Matrix) * DirectorStudioPicking.Matrix(pickView) * DirectorStudioPicking.Matrix(pickProjection);
        double left = 1, right = 0, top = 1, bottom = 0;
        for (int v = 0; v < target.Mesh.VerticesCount; v++)
        {
            int p = v * 3; var point = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(target.Mesh.xyz[p], target.Mesh.xyz[p+1], target.Mesh.xyz[p+2], 1), m);
            if (point.W <= .001) return null;
            double x = .5 + point.X / point.W * .5, y = .5 - point.Y / point.W * .5;
            left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        return (left, top, right - left, bottom - top);
    }

    internal (double X, double Y) AxisProjection(string axis)
    {
        if (pickView is null || pickProjection is null || !pickMeshes.TryGetValue(Selected, out var target)) return default;
        var m = DirectorStudioPicking.Matrix(pickView) * DirectorStudioPicking.Matrix(pickProjection);
        var start = new System.Numerics.Vector4(target.Matrix[12], target.Matrix[13], target.Matrix[14], 1);
        var end = start; if (axis == "X") end.X++; else if (axis == "Y") end.Y++; else end.Z++;
        start = System.Numerics.Vector4.Transform(start, m); end = System.Numerics.Vector4.Transform(end, m);
        if (start.W <= .001 || end.W <= .001) return default;
        return ((end.X / end.W - start.X / start.W) * .5, (start.Y / start.W - end.Y / end.W) * .5);
    }

    private StudioMesh BuildMesh(DirectorStudioProject source, DirectorStudioSceneObject item)
    {
        try
        {
            Shape shape;
            DirectorStudioResourceLink? origin = null;
            if (item.ResourceLink.StartsWith("library:model/", StringComparison.Ordinal))
            {
                var model = findModel(Guid.Parse(item.ResourceLink[14..])) ?? throw new FileNotFoundException("Model library entry is missing.");
                if (model.ShapeSourcePath.Length > 0)
                {
                    if (new FileInfo(model.ShapeSourcePath).Length > DirectorStudioProject.MaximumAssetBytes) throw new InvalidDataException("Model exceeds 64 MiB.");
                    shape = Parse(File.ReadAllBytes(model.ShapeSourcePath));
                    // Explicit model library file selection authorizes this resource directory.
                    string prefix = "model-" + model.Id.ToString("N")[..20];
                    if (!resources.SourceNames.Contains(prefix)) resources.RegisterSource(prefix, Path.GetDirectoryName(Path.GetFullPath(model.ShapeSourcePath))!);
                    origin = new(prefix, Path.GetFileName(model.ShapeSourcePath));
                }
                else if (model.ShapeAssetLocation.Length > 0) shape = LoadAssetShape(new AssetLocation(model.ShapeAssetLocation))
                    ?? throw new FileNotFoundException(model.ShapeAssetLocation);
                else shape = EntityShape(model.EntityCode);
            }
            else if (item.ResourceLink.StartsWith("entity:", StringComparison.Ordinal))
            {
                string path = DirectorStudioResourceLink.Parse(item.ResourceLink).Path;
                int slash = path.IndexOf('/');
                shape = EntityShape(slash < 0 ? path : path[..slash] + ":" + path[(slash + 1)..]);
            }
            else
            {
                byte[] data;
                if (item.ResourceLink.Length > 0)
                { origin = DirectorStudioResourceLink.Parse(item.ResourceLink); data = resources.Read(origin.Value); }
                else data = source.Assets.FirstOrDefault(a => a.Id == item.AssetId)?.Content
                    ?? throw new FileNotFoundException("Embedded model is missing.");
                shape = Parse(data);
            }
            if (shape.Elements is not { Length: > 0 }) throw new InvalidDataException("Model has no Shape elements.");
            shape.CollectAndResolveReferences(api.Logger, "Studio");
            bool animated = item.Actions.Count > 0;
            if (animated) shape.InitForAnimations(api.Logger, "Studio", shape.CollectAndResolveReferences(api.Logger, "Studio").Keys.ToArray());
            var textures = new TextureSource(api, resources, shape, origin, source);
            try
            {
                MeshData mesh;
                if (animated) api.Tesselator.TesselateShapeWithJointIds("Studio", shape, out mesh, textures, null, null, null);
                else api.Tesselator.TesselateShape("Studio", shape, out mesh, textures);
                if (mesh.VerticesCount == 0) throw new InvalidDataException("Model has no visible faces.");
                return new(api.Render.UploadMultiTextureMesh(mesh), textures, mesh, animated ? new DirectorStudioAnimator(shape) : null);
            }
            catch { textures.Dispose(); throw; }
        }
        catch (Exception e)
        { api.Logger.Warning("VS Director Studio: model {0}: {1}", item.Name, e.GetBaseException().Message); throw; }
    }

    private Shape EntityShape(string code)
    {
        var properties = api.World.GetEntityType(new AssetLocation(code)) ?? throw new FileNotFoundException(code);
        var client = properties.Client;
        var shape = client.LoadedShape?.Clone()
            ?? (client.Shape?.Base is { } location ? LoadAssetShape(location) : null)
            ?? throw new InvalidDataException("Entity has no static Shape: " + code);
        shape.Textures = shape.Textures is null ? new() : new(shape.Textures);
        if (client.Textures is not null)
            foreach (var (name, texture) in client.Textures)
                if (texture.Base is not null) shape.Textures[name] = texture.Base;
        return shape;
    }

    internal static Shape Parse(byte[] bytes)
        => JsonUtil.FromString<Shape>(Encoding.UTF8.GetString(bytes)) ?? throw new InvalidDataException("Empty Shape JSON.");

    private Shape? LoadAssetShape(AssetLocation location)
        => Shape.TryGet(api, location.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json"))?.Clone();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; project = null;
        api.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        runtime.Dispose();
    }

    private sealed class StudioMesh : IDisposable
    {
        private MultiTextureMeshRef mesh;
        private readonly TextureSource textures;
        private readonly MeshData source;
        private readonly DirectorStudioAnimator? animator;
        internal MultiTextureMeshRef Mesh => mesh;
        internal MeshData Geometry { get; private set; }
        private double lastTime = double.NaN;
        private Guid lastClip;
        internal StudioMesh(MultiTextureMeshRef mesh, TextureSource textures, MeshData source, DirectorStudioAnimator? animator)
        {
            this.mesh = mesh;
            this.textures = textures;
            this.source = source;
            this.animator = animator;
            Geometry = source;
        }
        internal void Pose(ICoreClientAPI api, DirectorActionEvaluation? evaluation, double time, Guid clip)
        {
            if (animator is null || (lastTime == time && lastClip == clip)) return;
            var geometry = DirectorStudioAnimator.Deform(source, animator.Sample(evaluation));
            var replacement = api.Render.UploadMultiTextureMesh(geometry);
            mesh.Dispose(); mesh = replacement; Geometry = geometry; lastTime = time; lastClip = clip;
        }
        public void Dispose() { mesh.Dispose(); textures.Dispose(); }
    }

    private sealed class TextureSource : ITexPositionSource, IDisposable
    {
        private readonly ICoreClientAPI api;
        private readonly DirectorStudioResourceProvider resources;
        private readonly Shape shape;
        private readonly DirectorStudioResourceLink? origin;
        private readonly DirectorStudioProject project;
        private readonly Dictionary<string, TextureAtlasPosition> positions = new();
        private readonly List<int> owned = new();
        public Size2i AtlasSize => api.BlockTextureAtlas.Size;
        public TextureSource(ICoreClientAPI api, DirectorStudioResourceProvider resources, Shape shape, DirectorStudioResourceLink? origin, DirectorStudioProject project)
        { this.api = api; this.resources = resources; this.shape = shape; this.origin = origin; this.project = project; }
        public TextureAtlasPosition this[string textureCode]
        {
            get
            {
                if (positions.TryGetValue(textureCode, out var position)) return position;
                textureCode = textureCode.TrimStart('#');
                if (shape.Textures?.TryGetValue(textureCode, out AssetLocation? location) == true)
                {
                    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { textureCode };
                    while (location.Path.StartsWith('#'))
                    {
                        string alias = location.Path[1..];
                        if (!visited.Add(alias) || !shape.Textures.TryGetValue(alias, out location))
                            throw new InvalidDataException("Cyclic or missing texture alias: " + alias);
                    }
                    string path = location.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? location.Path : location.Path + ".png";
                    byte[]? local = null;
                    if (origin is { Source: not "builtin" } root)
                        foreach (string candidate in new[] { path, "textures/" + path, (Path.GetDirectoryName(root.Path)?.Replace('\\', '/') + "/" + path).TrimStart('/') }.Distinct())
                        {
                            try { local = resources.Read(new(root.Source, candidate)); break; }
                            catch (FileNotFoundException) { }
                        }
                    local ??= project.Assets.FirstOrDefault(a => a.Kind == DirectorStudioAssetKind.Texture
                        && (a.SourceName == path || a.SourceName == Path.GetFileName(path)))?.Content;
                    if (local is not null) position = Insert(local);
                    else
                    {
                        var asset = new AssetLocation(location.Domain, path.StartsWith("textures/") ? path : "textures/" + path);
                        if (!api.BlockTextureAtlas.GetOrInsertTexture(asset, out _, out position))
                            throw new FileNotFoundException("Model texture is missing: " + asset);
                    }
                }
                else if (shape.Textures is null || shape.Textures.Count == 0) position = White();
                else throw new InvalidDataException("Undefined model texture: " + textureCode);
                positions[textureCode] = position;
                return position;
            }
        }
        private TextureAtlasPosition White()
        {
            if (positions.TryGetValue("__studio_white", out var pos)) return pos;
            // A plain material for shapes that intentionally have no textures.
            pos = Insert(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII="));
            positions["__studio_white"] = pos; return pos;
        }
        private TextureAtlasPosition Insert(byte[] png)
        {
            if (!api.BlockTextureAtlas.InsertTexture(png, out int id, out var pos)) throw new InvalidDataException("Cannot allocate model texture.");
            owned.Add(id); return pos;
        }
        public void Dispose() { foreach (int id in owned) api.BlockTextureAtlas.FreeTextureSpace(id); owned.Clear(); }
    }
}
