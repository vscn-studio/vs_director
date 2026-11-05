using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VSDirector.Rendering;

internal sealed class DirectorModelOutlineRenderer : IRenderer
{
    private const int MaxOutlineRadius = 16;
    private const float MaskBufferScale = 0.5f;
    private const float DepthAwareMaskBufferScale = 1f;

    private static readonly float[] ClearMask = { 0f, 0f, 0f, 0f };

    private static readonly Type? EntityShapeRendererType =
        AccessTools.TypeByName("Vintagestory.GameContent.EntityShapeRenderer");

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly DirectorConfig config;
    private readonly Dictionary<Type, RendererFields?> rendererFields = new();
    private readonly HashSet<string> selectedEntityCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly MeshRef screenTriangle;
    private string[] selectedCodeSnapshot = Array.Empty<string>();
    private IShaderProgram? maskShader;
    private IShaderProgram? dilationShader;
    private IShaderProgram? outlineShader;
    private FrameBufferRef? maskFrameBuffer;
    private FrameBufferRef? horizontalFrameBuffer;
    private bool targetCreationErrorLogged;
    private bool missingDestinationLogged;
    private bool noTargetsLogged;
    private bool targetBindingErrorLogged;
    private bool successfulFrameLogged;
    private bool disposed;

    internal DirectorModelOutlineRenderer(ICoreClientAPI capi, DirectorConfig config)
    {
        this.capi = capi;
        this.config = config;
        game = capi.World as ClientMain
            ?? throw new InvalidOperationException("VS Director model outlines require the desktop client.");
        screenTriangle = capi.Render.UploadMesh(BuildScreenTriangle());
        capi.Event.ReloadShader += LoadShaders;
        LoadShaders();
        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterOIT, "vsdirector-model-outlines");
    }

    public double RenderOrder => 1.1d;

    public int RenderRange => int.MaxValue;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        _ = deltaTime;
        _ = stage;
        if (!config.EnableModelOutlines
            || config.ModelOutlineEntityCodes.Count == 0
            || !ShadersReady()
            || EntityShapeRendererType is null)
        {
            return;
        }

        FrameBufferRef? destination = ResolveDestinationFrameBuffer(out bool usedPrimaryFallback);
        if (destination is null)
        {
            LogMissingDestinationOnce();
            return;
        }
        missingDestinationLogged = false;

        List<OutlineTarget> targets = CollectTargets();
        if (targets.Count == 0)
        {
            LogNoTargetsOnce();
            return;
        }
        noTargetsLogged = false;
        bool throughObstacles = config.ModelOutlineThroughObstacles;
        bool respectSceneDepth = !throughObstacles && destination.DepthTextureId > 0;
        float maskBufferScale = respectSceneDepth
            ? DepthAwareMaskBufferScale
            : MaskBufferScale;
        if (!EnsureFrameBuffers(destination.Width, destination.Height, maskBufferScale))
        {
            return;
        }
        int fullResolutionRadius = Math.Clamp(
            (int)MathF.Ceiling(config.ModelOutlineThickness),
            1,
            MaxOutlineRadius);
        int maskRadius = CalculateMaskRadius(fullResolutionRadius, destination.Width);
        Vec4f color = ParseColor(config.ModelOutlineColor);
        bool matrixPushed = false;
        game.GlMatrixModeModelView();
        game.GlPushMatrix();
        matrixPushed = true;
        game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);
        try
        {
            game.Platform.GlDepthMask(flag: false);
            game.Platform.GlDisableCullFace();
            game.Platform.GlToggleBlend(on: false);
            int renderedTargets = RenderEntityMask(destination, targets, respectSceneDepth);
            if (renderedTargets == 0)
            {
                LogTargetBindingErrorOnce(targets);
                return;
            }
            targetBindingErrorLogged = false;
            bool verifyGl = !successfulFrameLogged;
            if (verifyGl)
            {
                game.Platform.CheckGlErrorAlways("VS Director model outline mask pass");
            }
            RenderHorizontalDilation(maskRadius);
            if (verifyGl)
            {
                game.Platform.CheckGlErrorAlways("VS Director model outline dilation pass");
            }
            CompositeOuterContour(destination, maskRadius, color, throughObstacles);
            if (verifyGl)
            {
                game.Platform.CheckGlErrorAlways("VS Director model outline composite pass");
                capi.Logger.Notification(
                    "VS Director: outer-contour outline pipeline active ({0} target(s), frame={1}x{2}, mask={3}x{4}, {5}).",
                    renderedTargets,
                    destination.Width,
                    destination.Height,
                    maskFrameBuffer?.Width ?? 0,
                    maskFrameBuffer?.Height ?? 0,
                    usedPrimaryFallback ? "primary framebuffer fallback" : "current framebuffer");
                successfulFrameLogged = true;
            }
        }
        finally
        {
            TryStopShaders();
            game.Platform.CurrentFrameBuffer = destination;
            game.Platform.GlViewport(0, 0, destination.Width, destination.Height);
            if (matrixPushed)
            {
                game.GlPopMatrix();
            }
            game.Platform.GlDepthFunc(EnumDepthFunction.Less);
            game.Platform.GlDepthMask(flag: true);
            game.Platform.GlEnableDepthTest();
            game.Platform.GlCullFaceBack();
            game.Platform.GlEnableCullFace();
            game.Platform.GlToggleBlend(on: false);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterOIT);
        capi.Event.ReloadShader -= LoadShaders;
        DisposeShader(maskShader);
        maskShader = null;
        DisposeShader(dilationShader);
        dilationShader = null;
        DisposeShader(outlineShader);
        outlineShader = null;
        DisposeFrameBuffers();
        screenTriangle.Dispose();
        rendererFields.Clear();
    }

    private List<OutlineTarget> CollectTargets()
    {
        SyncSelectedEntityCodes();
        List<OutlineTarget> targets = new();
        foreach (Entity entity in game.LoadedEntities.Values)
        {
            EntityRenderer? renderer = ResolveActiveRenderer(entity);
            if (!entity.IsRendered
                || !MatchesSelectedEntity(entity, selectedEntityCodes)
                || entity.AnimManager?.Animator is null
                || renderer is null
                || !EntityShapeRendererType!.IsAssignableFrom(renderer.GetType()))
            {
                continue;
            }
            RendererFields? fields = ResolveFields(renderer.GetType());
            if (fields?.Mesh.GetValue(renderer) is not MultiTextureMeshRef mesh
                || mesh.Disposed
                || !mesh.Initialized
                || fields.ModelMatrix.GetValue(renderer) is not float[] modelMatrix)
            {
                continue;
            }
            targets.Add(new OutlineTarget(entity, mesh, modelMatrix));
        }
        return targets;
    }

    private EntityRenderer? ResolveActiveRenderer(Entity entity)
    {
        // The independent offscreen player renderer temporarily replaces the
        // engine registry entry without changing Properties.Client.Renderer.
        // Read the renderer that actually drew this view so its ModelMat and
        // mesh line up with the recorded image.
        if (DirectorOffscreenEngineBindings.TryGetEntityRenderer(
                game,
                entity.EntityId,
                out EntityRenderer? active)
            && active is not null
            && ReferenceEquals(active.entity, entity))
        {
            return active;
        }

        // Dedicated replay actors are intentionally detached from the engine
        // registry, but retain their renderer on the entity client state.
        return entity.Properties?.Client?.Renderer as EntityRenderer;
    }

    private static bool MatchesSelectedEntity(
        Entity entity,
        IReadOnlySet<string> selectedCodes)
    {
        if (entity is EntityPlayer
            && selectedCodes.Contains(VSDirectorModSystem.VanillaPlayerModelCode))
        {
            return true;
        }
        string entityCode = entity.Code?.ToShortString() ?? string.Empty;
        return entityCode.Length > 0 && selectedCodes.Contains(entityCode);
    }

    private int RenderEntityMask(
        FrameBufferRef destination,
        IReadOnlyList<OutlineTarget> targets,
        bool respectSceneDepth)
    {
        if (maskShader is null || maskFrameBuffer is null)
        {
            return 0;
        }
        game.Platform.CurrentFrameBuffer = maskFrameBuffer;
        game.Platform.GlViewport(0, 0, maskFrameBuffer.Width, maskFrameBuffer.Height);
        game.Platform.GlDepthFunc(EnumDepthFunction.Less);
        game.Platform.GlDepthMask(flag: true);
        game.Platform.GlEnableDepthTest();
        game.Platform.ClearFrameBuffer(
            maskFrameBuffer,
            ClearMask,
            clearDepthBuffer: true,
            clearColorBuffers: true);
        int renderedTargets = 0;
        maskShader.Use();
        try
        {
            maskShader.UniformMatrix("projectionMatrix", game.CurrentProjectionMatrix);
            maskShader.UniformMatrix("viewMatrix", game.CurrentModelViewMatrix);
            maskShader.Uniform("respectSceneDepth", respectSceneDepth ? 1 : 0);
            maskShader.Uniform("depthEpsilon", 0.0000005f);
            maskShader.BindTexture2D(
                "entityTex",
                game.EntityAtlasManager.AtlasTextures[0].TextureId,
                0);
            maskShader.BindTexture2D("sceneDepthTex", destination.DepthTextureId, 1);
            foreach (OutlineTarget target in targets)
            {
                if (!BindTarget(maskShader, target))
                {
                    continue;
                }
                capi.Render.RenderMultiTextureMesh(target.Mesh, "entityTex");
                renderedTargets++;
            }
        }
        finally
        {
            maskShader.Stop();
            game.Platform.GlDepthMask(flag: false);
            game.Platform.GlDisableDepthTest();
        }
        return renderedTargets;
    }

    private void RenderHorizontalDilation(int radius)
    {
        if (dilationShader is null || maskFrameBuffer is null || horizontalFrameBuffer is null)
        {
            return;
        }
        game.Platform.CurrentFrameBuffer = horizontalFrameBuffer;
        game.Platform.GlViewport(0, 0, horizontalFrameBuffer.Width, horizontalFrameBuffer.Height);
        game.Platform.ClearFrameBuffer(
            horizontalFrameBuffer,
            ClearMask,
            clearDepthBuffer: false,
            clearColorBuffers: true);
        game.Platform.GlDisableDepthTest();
        dilationShader.Use();
        dilationShader.BindTexture2D("maskTex", maskFrameBuffer.ColorTextureIds[0], 0);
        dilationShader.Uniform(
            "texelSize",
            1f / horizontalFrameBuffer.Width,
            1f / horizontalFrameBuffer.Height);
        dilationShader.Uniform("outlineRadius", radius);
        game.Platform.RenderMesh(screenTriangle);
        dilationShader.Stop();
    }

    private void CompositeOuterContour(
        FrameBufferRef destination,
        int radius,
        Vec4f color,
        bool throughObstacles)
    {
        if (outlineShader is null || maskFrameBuffer is null || horizontalFrameBuffer is null)
        {
            return;
        }
        game.Platform.CurrentFrameBuffer = destination;
        game.Platform.GlViewport(0, 0, destination.Width, destination.Height);
        game.Platform.GlDisableDepthTest();
        outlineShader.Use();
        outlineShader.BindTexture2D("maskTex", maskFrameBuffer.ColorTextureIds[0], 0);
        outlineShader.BindTexture2D(
            "horizontalMaskTex",
            horizontalFrameBuffer.ColorTextureIds[0],
            1);
        outlineShader.Uniform(
            "screenTexelSize",
            1f / destination.Width,
            1f / destination.Height);
        outlineShader.Uniform(
            "maskTexelSize",
            1f / maskFrameBuffer.Width,
            1f / maskFrameBuffer.Height);
        outlineShader.Uniform("outlineRadius", radius);
        outlineShader.Uniform("outlineColor", color);
        outlineShader.Uniform("throughObstacles", throughObstacles ? 1 : 0);
        game.Platform.RenderMesh(screenTriangle);
        outlineShader.Stop();
    }

    private bool BindTarget(IShaderProgram shader, OutlineTarget target)
    {
        if (target.Entity.AnimManager?.Animator is not { } animator
            || !shader.UBOs.ContainsKey("Animation")
            || shader.UBOs["Animation"] is not { } animationUbo)
        {
            return false;
        }
        shader.UniformMatrix("modelMatrix", target.ModelMatrix);
        animationUbo.Update(
            animator.Matrices,
            0,
            animator.MaxJointId * 16 * sizeof(float));
        return true;
    }

    private int CalculateMaskRadius(int fullResolutionRadius, int fullResolutionWidth)
    {
        if (maskFrameBuffer is null || fullResolutionWidth <= 0)
        {
            return fullResolutionRadius;
        }
        float scale = maskFrameBuffer.Width / (float)fullResolutionWidth;
        return Math.Clamp(
            (int)MathF.Ceiling(fullResolutionRadius * scale),
            1,
            MaxOutlineRadius);
    }

    private void SyncSelectedEntityCodes()
    {
        IReadOnlyList<string> configured = config.ModelOutlineEntityCodes;
        bool unchanged = selectedCodeSnapshot.Length == configured.Count;
        for (int index = 0; unchanged && index < configured.Count; index++)
        {
            unchanged = string.Equals(
                selectedCodeSnapshot[index],
                configured[index],
                StringComparison.OrdinalIgnoreCase);
        }
        if (unchanged)
        {
            return;
        }

        selectedCodeSnapshot = configured.ToArray();
        selectedEntityCodes.Clear();
        foreach (string code in configured)
        {
            selectedEntityCodes.Add(code);
        }
    }

    private FrameBufferRef? ResolveDestinationFrameBuffer(out bool usedPrimaryFallback)
    {
        usedPrimaryFallback = false;
        if (capi.Render.CurrentFrameBuffer is { Disposed: false } current
            && current.Width > 0
            && current.Height > 0)
        {
            return current;
        }

        int primaryIndex = (int)EnumFrameBuffer.Primary;
        if (primaryIndex >= 0
            && primaryIndex < game.Platform.FrameBuffers.Count
            && game.Platform.FrameBuffers[primaryIndex] is { Disposed: false } primary
            && primary.Width > 0
            && primary.Height > 0)
        {
            usedPrimaryFallback = true;
            return primary;
        }
        return null;
    }

    private void LogMissingDestinationOnce()
    {
        if (missingDestinationLogged)
        {
            return;
        }
        missingDestinationLogged = true;
        FrameBufferRef? current = capi.Render.CurrentFrameBuffer;
        capi.Logger.Warning(
            "VS Director: model outline skipped because no usable destination framebuffer exists (current={0}, framebufferCount={1}).",
            current is null
                ? "null"
                : $"disposed={current.Disposed},size={current.Width}x{current.Height}",
            game.Platform.FrameBuffers.Count);
    }

    private void LogNoTargetsOnce()
    {
        if (noTargetsLogged)
        {
            return;
        }
        noTargetsLogged = true;
        SyncSelectedEntityCodes();
        string[] matchingEntities = game.LoadedEntities.Values
            .Where(entity => MatchesSelectedEntity(entity, selectedEntityCodes))
            .Take(8)
            .Select(entity =>
            {
                EntityRenderer? renderer = entity.Properties?.Client?.Renderer;
                return string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0}(rendered={1},animator={2},renderer={3})",
                    entity.Code?.ToShortString() ?? "<unknown>",
                    entity.IsRendered,
                    entity.AnimManager?.Animator is not null,
                    renderer?.GetType().FullName ?? "null");
            })
            .ToArray();
        capi.Logger.Warning(
            "VS Director: model outline found no renderable target for [{0}]. Matching loaded entities: {1}.",
            string.Join(",", selectedEntityCodes),
            matchingEntities.Length == 0 ? "none" : string.Join("; ", matchingEntities));
    }

    private void LogTargetBindingErrorOnce(IReadOnlyList<OutlineTarget> targets)
    {
        if (targetBindingErrorLogged)
        {
            return;
        }
        targetBindingErrorLogged = true;
        capi.Logger.Warning(
            "VS Director: model outline collected {0} target(s), but none could bind the Animation uniform buffer (available UBOs: {1}).",
            targets.Count,
            maskShader is null ? "shader unavailable" : string.Join(",", maskShader.UBOs.Keys));
    }

    private bool EnsureFrameBuffers(int width, int height, float scale)
    {
        float safeScale = float.IsFinite(scale)
            ? Math.Clamp(scale, MaskBufferScale, DepthAwareMaskBufferScale)
            : MaskBufferScale;
        int maskWidth = Math.Max(1, (int)MathF.Ceiling(width * safeScale));
        int maskHeight = Math.Max(1, (int)MathF.Ceiling(height * safeScale));
        if (maskFrameBuffer is { Disposed: false } mask
            && horizontalFrameBuffer is { Disposed: false } horizontal
            && mask.Width == maskWidth
            && mask.Height == maskHeight
            && mask.DepthTextureId > 0
            && horizontal.Width == maskWidth
            && horizontal.Height == maskHeight)
        {
            return true;
        }
        try
        {
            DisposeFrameBuffers();
            maskFrameBuffer = CreateMaskFrameBuffer(
                "VSDirector-EntityOutlineMask",
                maskWidth,
                maskHeight,
                withDepth: true);
            horizontalFrameBuffer = CreateMaskFrameBuffer(
                "VSDirector-EntityOutlineHorizontal",
                maskWidth,
                maskHeight,
                withDepth: false);
            targetCreationErrorLogged = false;
            return true;
        }
        catch (Exception exception)
        {
            DisposeFrameBuffers();
            if (!targetCreationErrorLogged)
            {
                targetCreationErrorLogged = true;
                capi.Logger.Error(
                    "VS Director: model outline framebuffers could not be created: {0}",
                    exception.Message);
            }
            return false;
        }
    }

    private FrameBufferRef CreateMaskFrameBuffer(
        string name,
        int width,
        int height,
        bool withDepth)
    {
        FramebufferAttrsAttachment colorAttachment = new()
        {
            AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
            Texture = new RawTexture
            {
                Width = width,
                Height = height,
                PixelInternalFormat = EnumTextureInternalFormat.Rgba8,
                PixelFormat = EnumTexturePixelFormat.Rgba,
                MinFilter = EnumTextureFilter.Nearest,
                MagFilter = EnumTextureFilter.Nearest
            }
        };
        FramebufferAttrs attrs = new(name, width, height)
        {
            Attachments = withDepth
                ? new[]
                {
                    colorAttachment,
                    new FramebufferAttrsAttachment
                    {
                        AttachmentType = EnumFramebufferAttachment.DepthAttachment,
                        Texture = new RawTexture
                        {
                            Width = width,
                            Height = height,
                            PixelInternalFormat = EnumTextureInternalFormat.DepthComponent32,
                            PixelFormat = EnumTexturePixelFormat.DepthComponent,
                            MinFilter = EnumTextureFilter.Nearest,
                            MagFilter = EnumTextureFilter.Nearest
                        }
                    }
                }
                : new[] { colorAttachment }
        };
        return game.Platform.CreateFramebuffer(attrs);
    }

    private void DisposeFrameBuffers()
    {
        if (maskFrameBuffer is not null && !maskFrameBuffer.Disposed)
        {
            game.Platform.DisposeFrameBuffer(maskFrameBuffer, disposeTextures: true);
        }
        maskFrameBuffer = null;
        if (horizontalFrameBuffer is not null && !horizontalFrameBuffer.Disposed)
        {
            game.Platform.DisposeFrameBuffer(horizontalFrameBuffer, disposeTextures: true);
        }
        horizontalFrameBuffer = null;
    }

    private RendererFields? ResolveFields(Type rendererType)
    {
        if (rendererFields.TryGetValue(rendererType, out RendererFields? cached))
        {
            return cached;
        }
        FieldInfo? mesh = AccessTools.Field(rendererType, "meshRefOpaque");
        FieldInfo? modelMatrix = AccessTools.Field(rendererType, "ModelMat");
        RendererFields? resolved = mesh is null || modelMatrix is null
            ? null
            : new RendererFields(mesh, modelMatrix);
        rendererFields[rendererType] = resolved;
        return resolved;
    }

    private bool LoadShaders()
    {
        IShaderProgram nextMask = CreateShader("modelmask");
        IShaderProgram nextDilation = CreateShader("modeloutlinedilate");
        IShaderProgram nextOutline = CreateShader("modeloutline");
        if (!nextMask.Compile() || !nextDilation.Compile() || !nextOutline.Compile())
        {
            DisposeShader(nextMask);
            DisposeShader(nextDilation);
            DisposeShader(nextOutline);
            return false;
        }
        try
        {
            ConfigureAnimationUbo(nextMask);
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "VS Director: model outline Animation UBO could not be created: {0}",
                exception.Message);
            DisposeShader(nextMask);
            DisposeShader(nextDilation);
            DisposeShader(nextOutline);
            return false;
        }

        IShaderProgram? previousMask = maskShader;
        IShaderProgram? previousDilation = dilationShader;
        IShaderProgram? previousOutline = outlineShader;
        maskShader = nextMask;
        dilationShader = nextDilation;
        outlineShader = nextOutline;
        DisposeShader(previousMask);
        DisposeShader(previousDilation);
        DisposeShader(previousOutline);
        return true;
    }

    private void ConfigureAnimationUbo(IShaderProgram shader)
    {
        DisposeShaderUbos(shader);
        shader.UBOs["Animation"] = capi.Render.CreateUBO(
            shader,
            0,
            "Animation",
            GlobalConstants.MaxAnimatedElements * 16 * sizeof(float));
    }

    private static void DisposeShader(IShaderProgram? shader)
    {
        if (shader is null)
        {
            return;
        }
        DisposeShaderUbos(shader);
        shader.Dispose();
    }

    private static void DisposeShaderUbos(IShaderProgram shader)
    {
        foreach (UBORef ubo in shader.UBOs.Values)
        {
            ubo.Dispose();
        }
        shader.UBOs.Clear();
    }

    private IShaderProgram CreateShader(string name)
    {
        IShaderProgram program = capi.Shader.NewShaderProgram();
        program.AssetDomain = "vsdirector";
        program.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        program.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram(name, program);
        return program;
    }

    private bool ShadersReady()
        => maskShader is { Disposed: false }
            && dilationShader is { Disposed: false }
            && outlineShader is { Disposed: false };

    private void TryStopShaders()
    {
        try
        {
            maskShader?.Stop();
        }
        catch
        {
        }
        try
        {
            dilationShader?.Stop();
        }
        catch
        {
        }
        try
        {
            outlineShader?.Stop();
        }
        catch
        {
        }
    }

    private static MeshData BuildScreenTriangle()
    {
        int color = ColorUtil.ToRgba(255, 255, 255, 255);
        MeshData data = new(3, 3, withNormals: false, withUv: false);
        data.SetMode(EnumDrawMode.Triangles);
        data.AddVertexSkipTex(-1f, -1f, 0f, color);
        data.AddVertexSkipTex(3f, -1f, 0f, color);
        data.AddVertexSkipTex(-1f, 3f, 0f, color);
        data.AddIndex(0);
        data.AddIndex(1);
        data.AddIndex(2);
        return data;
    }

    private static Vec4f ParseColor(string? value)
    {
        string code = value?.Trim().TrimStart('#') ?? string.Empty;
        if (code.Length != 6
            || !uint.TryParse(
                code,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out uint color))
        {
            return new Vec4f(0f, 1f, 0f, 1f);
        }
        return new Vec4f(
            ((color >> 16) & 0xff) / 255f,
            ((color >> 8) & 0xff) / 255f,
            (color & 0xff) / 255f,
            1f);
    }

    private sealed record RendererFields(FieldInfo Mesh, FieldInfo ModelMatrix);

    private sealed record OutlineTarget(
        Entity Entity,
        MultiTextureMeshRef Mesh,
        float[] ModelMatrix);

}
