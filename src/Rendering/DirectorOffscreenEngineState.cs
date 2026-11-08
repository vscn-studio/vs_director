using System.Collections;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VSDirector.Replay;

namespace VSDirector.Rendering;

internal static class DirectorOffscreenEngineBindings
{
    private static readonly FieldInfo? CurrentRenderStageField =
        AccessTools.Field(typeof(ClientMain), "currentRenderStage");
    private static readonly FieldInfo? EntityRenderersField =
        AccessTools.Field(typeof(ClientMain), "EntityRenderers");
    private static readonly FieldInfo? ShadowMvpMatrixField =
        AccessTools.Field(typeof(ClientMain), "shadowMvpMatrix");
    private static readonly FieldInfo? PointLightsField =
        AccessTools.Field(typeof(ClientMain), "pointlights");
    private static readonly FieldInfo? ChunkRendererField =
        AccessTools.Field(typeof(ClientMain), "chunkRenderer");
    private static readonly FieldInfo? ChunkMasterPoolField =
        AccessTools.Field(typeof(ChunkRenderer), "masterPool");
    private static readonly FieldInfo? MainThreadParticlePoolsField =
        AccessTools.Field(typeof(SystemRenderParticles), "mainthreadpools");
    private static readonly FieldInfo? OffThreadParticlePoolsField =
        AccessTools.Field(typeof(SystemRenderParticles), "offthreadpools");
    private static readonly FieldInfo? ParticleUpdateBufferField =
        AccessTools.Field(typeof(ParticlePoolQuads), "updateBuffer");
    private static readonly FieldInfo? ParticleUpdateBuffersField =
        AccessTools.Field(typeof(ParticlePoolQuads), "updateBuffers");
    private static readonly FieldInfo? ParticleCameraPositionsField =
        AccessTools.Field(typeof(ParticlePoolQuads), "cameraPos");
    private static readonly FieldInfo? ParticleReadPositionField =
        AccessTools.Field(typeof(ParticlePoolQuads), "readPosition");

    internal static bool IsValid
        => CurrentRenderStageField is not null
            && EntityRenderersField is not null
            && ChunkRendererField is not null
            && ChunkMasterPoolField is not null
            && MainThreadParticlePoolsField is not null
            && OffThreadParticlePoolsField is not null
            && ParticleUpdateBufferField is not null
            && ParticleUpdateBuffersField is not null
            && ParticleCameraPositionsField is not null
            && ParticleReadPositionField is not null;

    internal static string Validate()
    {
        List<string> missing = new();
        AddMissing(CurrentRenderStageField, "ClientMain.currentRenderStage", missing);
        AddMissing(EntityRenderersField, "ClientMain.EntityRenderers", missing);
        AddMissing(ChunkRendererField, "ClientMain.chunkRenderer", missing);
        AddMissing(ChunkMasterPoolField, "ChunkRenderer.masterPool", missing);
        AddMissing(MainThreadParticlePoolsField, "SystemRenderParticles.mainthreadpools", missing);
        AddMissing(OffThreadParticlePoolsField, "SystemRenderParticles.offthreadpools", missing);
        AddMissing(ParticleUpdateBufferField, "ParticlePoolQuads.updateBuffer", missing);
        AddMissing(ParticleUpdateBuffersField, "ParticlePoolQuads.updateBuffers", missing);
        AddMissing(ParticleCameraPositionsField, "ParticlePoolQuads.cameraPos", missing);
        AddMissing(ParticleReadPositionField, "ParticlePoolQuads.readPosition", missing);
        return string.Join(", ", missing);
    }

    internal static EnumRenderStage GetCurrentRenderStage(ClientMain game)
        => CurrentRenderStageField?.GetValue(game) is EnumRenderStage stage
            ? stage
            : EnumRenderStage.Done;

    internal static void SetCurrentRenderStage(ClientMain game, EnumRenderStage stage)
        => CurrentRenderStageField?.SetValue(game, stage);

    internal static float[]? GetShadowMvpMatrix(ClientMain game)
        => ShadowMvpMatrixField?.GetValue(game) as float[];

    internal static IReadOnlyList<EntityRenderer> GetEntityRenderers(ClientMain game)
    {
        if (EntityRenderersField?.GetValue(game) is not IDictionary dictionary)
        {
            return Array.Empty<EntityRenderer>();
        }

        List<EntityRenderer> renderers = new(dictionary.Count);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Value is EntityRenderer renderer)
            {
                renderers.Add(renderer);
            }
        }

        return renderers;
    }

    internal static bool TryGetEntityRenderer(
        ClientMain game,
        long entityId,
        out EntityRenderer? renderer)
    {
        renderer = null;
        if (EntityRenderersField?.GetValue(game) is not IDictionary dictionary
            || !dictionary.Contains(entityId))
        {
            return false;
        }

        renderer = dictionary[entityId] as EntityRenderer;
        return renderer is not null;
    }

    internal static bool TryDetachEntityRenderer(
        ClientMain game,
        long entityId,
        out EntityRenderer? renderer)
    {
        renderer = null;
        if (EntityRenderersField?.GetValue(game) is not IDictionary dictionary
            || !dictionary.Contains(entityId)
            || dictionary[entityId] is not EntityRenderer current)
        {
            return false;
        }

        dictionary.Remove(entityId);
        if (dictionary.Contains(entityId))
        {
            return false;
        }

        renderer = current;
        return true;
    }

    internal static bool TryRestoreEntityRenderer(
        ClientMain game,
        long entityId,
        EntityRenderer renderer)
    {
        if (EntityRenderersField?.GetValue(game) is not IDictionary dictionary)
        {
            return false;
        }

        if (dictionary.Contains(entityId))
        {
            return ReferenceEquals(dictionary[entityId], renderer);
        }

        dictionary.Add(entityId, renderer);
        return ReferenceEquals(dictionary[entityId], renderer);
    }

    internal static bool TryReplaceEntityRenderer(
        ClientMain game,
        long entityId,
        EntityRenderer expected,
        EntityRenderer replacement)
    {
        if (EntityRenderersField?.GetValue(game) is not IDictionary dictionary
            || !dictionary.Contains(entityId)
            || !ReferenceEquals(dictionary[entityId], expected))
        {
            return false;
        }

        dictionary[entityId] = replacement;
        return true;
    }

    internal static IReadOnlyList<IPointLight> GetPointLights(ClientMain game)
    {
        if (PointLightsField?.GetValue(game) is not IEnumerable values)
        {
            return Array.Empty<IPointLight>();
        }

        return values.Cast<object>().OfType<IPointLight>().ToArray();
    }

    internal static bool TryGetParticlePools(
        SystemRenderParticles system,
        out IParticlePool[] mainThread,
        out IParticlePool[] offThread)
    {
        mainThread = MainThreadParticlePoolsField?.GetValue(system) as IParticlePool[]
            ?? Array.Empty<IParticlePool>();
        offThread = OffThreadParticlePoolsField?.GetValue(system) as IParticlePool[]
            ?? Array.Empty<IParticlePool>();
        return mainThread.Length >= 2 && offThread.Length >= 2;
    }

    internal static bool RenderOpaqueTerrain(ClientMain game)
    {
        if (ChunkRendererField?.GetValue(game) is not ChunkRenderer renderer
            || ChunkMasterPoolField?.GetValue(renderer) is not MeshDataPoolMasterManager masterPool)
        {
            return false;
        }

        float savedDeltaTime = masterPool.currentDt;
        float[] savedModelView = masterPool.currentModelViewMatrix;
        float[] savedShadowMatrix = masterPool.shadowMVPMatrix;
        try
        {
            masterPool.currentDt = 0f;
            masterPool.currentModelViewMatrix = game.CurrentModelViewMatrix;
            renderer.RenderOpaque(0f);
            return true;
        }
        finally
        {
            masterPool.currentDt = savedDeltaTime;
            masterPool.currentModelViewMatrix = savedModelView;
            masterPool.shadowMVPMatrix = savedShadowMatrix;
        }
    }

    internal static bool RenderLiquidDepth(ClientMain game)
    {
        if (ChunkRendererField?.GetValue(game) is not ChunkRenderer renderer)
        {
            return false;
        }

        renderer.OnRenderBefore(0f);
        return true;
    }

    internal static bool TryGetParticleBuffer(
        ParticlePoolQuads pool,
        bool offThread,
        Vec3d mainCameraPosition,
        out MeshData buffer,
        out Vec3d origin)
    {
        buffer = null!;
        origin = mainCameraPosition.Clone();
        if (!offThread)
        {
            if (ParticleUpdateBufferField?.GetValue(pool) is not MeshData mainBuffer)
            {
                return false;
            }

            buffer = mainBuffer;
            return true;
        }

        if (ParticleUpdateBuffersField?.GetValue(pool) is not MeshData[] buffers
            || ParticleCameraPositionsField?.GetValue(pool) is not Vec3d[] positions
            || ParticleReadPositionField?.GetValue(pool) is not int readPosition
            || readPosition < 0
            || readPosition >= buffers.Length
            || readPosition >= positions.Length
            || buffers[readPosition] is null
            || positions[readPosition] is null)
        {
            return false;
        }

        buffer = buffers[readPosition];
        origin = positions[readPosition].Clone();
        return true;
    }

    private static void AddMissing(MemberInfo? member, string name, ICollection<string> missing)
    {
        if (member is null)
        {
            missing.Add(name);
        }
    }
}

internal static class DirectorOffscreenPointLights
{
    private const float SearchRange = 120f;

    internal static void Apply(
        ICoreClientAPI capi,
        ClientMain game,
        DirectorOffscreenCameraPose pose)
    {
        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        uniforms.PointLightsCount = 0;
        int maximum = Math.Min(
            Math.Max(0, ClientSettings.MaxDynamicLights),
            Math.Min(uniforms.PointLights3.Length, uniforms.PointLightColors3.Length) / 3);
        if (maximum == 0)
        {
            return;
        }

        Vec3d cameraPosition = new(
            pose.X,
            pose.Y + pose.Dimension * 32768d,
            pose.Z);
        Entity[] entityLights = capi.World.GetEntitiesAround(
            cameraPosition,
            SearchRange,
            SearchRange,
            entity => entity.LightHsv is { Length: >= 3 } light && light[2] > 0);
        foreach (Entity entity in entityLights
            .OrderBy(entity => entity.Pos.SquareDistanceTo(cameraPosition)))
        {
            if (!AddEntityLight(game, uniforms, maximum, entity))
            {
                return;
            }
        }

        foreach (IPointLight pointLight in DirectorOffscreenEngineBindings.GetPointLights(game))
        {
            if (!AddPointLight(game, uniforms, maximum, pointLight.Pos, pointLight.Color))
            {
                return;
            }
        }
    }

    private static bool AddEntityLight(
        ClientMain game,
        DefaultShaderUniforms uniforms,
        int maximum,
        Entity entity)
    {
        byte[] light = entity.LightHsv;
        int level = light[2];
        if (level < 0
            || level >= game.WorldMap.BlockLightLevels.Length
            || game.WorldMap.hueLevels.Length == 0
            || game.WorldMap.satLevels.Length == 0)
        {
            return uniforms.PointLightsCount < maximum;
        }

        int hueIndex = Math.Clamp(light[0], 0, game.WorldMap.hueLevels.Length - 1);
        int saturationIndex = Math.Clamp(light[1], 0, game.WorldMap.satLevels.Length - 1);
        int value = (int)(game.WorldMap.BlockLightLevels[level] * 255f);
        Vec3f color = new();
        ColorUtil.ToRGBVec3f(
            ColorUtil.HsvToRgba(
                game.WorldMap.hueLevels[hueIndex],
                game.WorldMap.satLevels[saturationIndex],
                value),
            ref color);
        color.Set(color.X * level, color.Y * level, color.Z * level);
        return AddPointLight(
            game,
            uniforms,
            maximum,
            new Vec3d(entity.Pos.X, entity.Pos.InternalY, entity.Pos.Z),
            color);
    }

    private static bool AddPointLight(
        ClientMain game,
        DefaultShaderUniforms uniforms,
        int maximum,
        Vec3d position,
        Vec3f color)
    {
        int index = uniforms.PointLightsCount;
        if (index >= maximum)
        {
            return false;
        }

        Vec4d transformed = new();
        Mat4d.MulWithVec4(
            game.CurrentModelViewMatrixd,
            new Vec4d(position.X, position.Y, position.Z, 1d),
            transformed);
        int offset = index * 3;
        uniforms.PointLights3[offset] = (float)transformed.X;
        uniforms.PointLights3[offset + 1] = (float)transformed.Y;
        uniforms.PointLights3[offset + 2] = (float)transformed.Z;
        uniforms.PointLightColors3[offset] = color.Z;
        uniforms.PointLightColors3[offset + 1] = color.Y;
        uniforms.PointLightColors3[offset + 2] = color.X;
        uniforms.PointLightsCount = index + 1;
        return uniforms.PointLightsCount < maximum;
    }
}

internal sealed class DirectorOffscreenEngineState
{
    private readonly ClientMain game;
    private readonly ICoreClientAPI capi;
    private readonly FrameBufferRef? frameBuffer;
    private readonly int viewportWidth;
    private readonly int viewportHeight;
    private readonly bool matrixModeProjection;
    private readonly EnumRenderStage renderStage;
    private readonly int projectionStackCount;
    private readonly int modelViewStackCount;
    private readonly double[] projectionMatrix;
    private readonly double[] modelViewMatrix;
    private readonly double[] perspectiveProjection;
    private readonly double[] perspectiveView;
    private readonly BlockPos frustumOrigin;
    private readonly CameraState camera;
    private readonly Vec3d entityCameraPosition;
    private readonly float playerCameraYaw;
    private readonly float playerCameraPitch;
    private readonly float playerCameraRoll;
    private readonly float mouseYaw;
    private readonly float mousePitch;
    private readonly Vec3f shaderPlayerView;
    private readonly Vec3f shaderPlayerPosition;
    private readonly Vec3f shaderPlayerPositionForFoam;
    private readonly int shaderPointLightsCount;
    private readonly float[] shaderPointLights;
    private readonly float[] shaderPointLightColors;
    private readonly float shaderCameraUnderwater;
    private readonly Vec4f shaderWaterMurkColor;
    private readonly float shaderZNear;
    private readonly float shaderZFar;
    private readonly int shaderFrameWidth;
    private readonly int renderedTriangles;
    private readonly int availableTriangles;
    private readonly int renderedEntities;
    private readonly IReadOnlyList<EntityVisibility> entityVisibility;
    private readonly AmbientBlendState ambientBlendState;
    private readonly WaterModifierState? waterModifierState;
    private readonly WaterModifierState? deepWaterModifierState;

    private DirectorOffscreenEngineState(ICoreClientAPI capi, ClientMain game)
    {
        this.capi = capi;
        this.game = game;
        frameBuffer = capi.Render.CurrentFrameBuffer;
        viewportWidth = frameBuffer?.Width ?? game.Width;
        viewportHeight = frameBuffer?.Height ?? game.Height;
        matrixModeProjection = game.CurrentMatrixModeProjection;
        renderStage = DirectorOffscreenEngineBindings.GetCurrentRenderStage(game);
        projectionStackCount = game.PMatrix.Count;
        modelViewStackCount = game.MvMatrix.Count;
        projectionMatrix = CopyTopOrIdentity(game.PMatrix);
        modelViewMatrix = CopyTopOrIdentity(game.MvMatrix);
        perspectiveProjection = (double[])game.PerspectiveProjectionMat.Clone();
        perspectiveView = (double[])game.PerspectiveViewMat.Clone();
        camera = CameraState.Capture(game.MainCamera);
        frustumOrigin = CameraBlockPosition(game.MainCamera.CameraEyePos);
        entityCameraPosition = game.EntityPlayer.CameraPos.Clone();
        playerCameraYaw = game.player?.CameraYaw ?? 0f;
        playerCameraPitch = game.player?.CameraPitch ?? 0f;
        playerCameraRoll = game.player?.CameraRoll ?? 0f;
        mouseYaw = game.mouseYaw;
        mousePitch = game.mousePitch;
        shaderPlayerView = capi.Render.ShaderUniforms.PlayerViewVector?.Clone() ?? new Vec3f();
        shaderPlayerPosition = capi.Render.ShaderUniforms.PlayerPos.Clone();
        shaderPlayerPositionForFoam = capi.Render.ShaderUniforms.PlayerPosForFoam.Clone();
        shaderPointLightsCount = capi.Render.ShaderUniforms.PointLightsCount;
        shaderPointLights = (float[])capi.Render.ShaderUniforms.PointLights3.Clone();
        shaderPointLightColors = (float[])capi.Render.ShaderUniforms.PointLightColors3.Clone();
        shaderCameraUnderwater = capi.Render.ShaderUniforms.CameraUnderwater;
        shaderWaterMurkColor = capi.Render.ShaderUniforms.WaterMurkColor;
        shaderZNear = capi.Render.ShaderUniforms.ZNear;
        shaderZFar = capi.Render.ShaderUniforms.ZFar;
        shaderFrameWidth = capi.Render.ShaderUniforms.FrameWidth;
        renderedTriangles = RuntimeStats.renderedTriangles;
        availableTriangles = RuntimeStats.availableTriangles;
        renderedEntities = RuntimeStats.renderedEntities;
        ambientBlendState = AmbientBlendState.Capture(game.AmbientManager);
        waterModifierState = CaptureModifier("water");
        deepWaterModifierState = CaptureModifier("deepwater");
        entityVisibility = DirectorOffscreenEngineBindings
            .GetEntityRenderers(game)
            .Select(renderer => new EntityVisibility(
                renderer.entity,
                renderer.entity.IsRendered,
                renderer.entity.IsShadowRendered))
            .ToArray();
    }

    internal static DirectorOffscreenEngineState Capture(ICoreClientAPI capi, ClientMain game)
        => new(capi, game);

    internal void Apply(
        DirectorOffscreenViewRequest view,
        FrameBufferRef target,
        float[] clearColor)
    {
        DirectorOffscreenCameraPose pose = view.Pose;
        double yaw = pose.Yaw * Math.PI / 180d;
        double pitch = pose.Pitch * Math.PI / 180d;
        double roll = pose.Roll * Math.PI / 180d;
        float fov = (float)(Math.Clamp(pose.Fov, 10f, 170f) * Math.PI / 180d);
        float zNear = Math.Clamp(0.1f - Math.Clamp(pose.Fov, 10f, 170f) / 90f / 25f, 0.025f, 0.1f);
        double absoluteY = pose.Y + pose.Dimension * 32768d;
        Vec3d eye = new(pose.X, absoluteY, pose.Z);
        Vec3f forward = EntityPos.GetViewVector((float)pitch, (float)yaw);
        double[] center = { eye.X + forward.X, eye.Y + forward.Y, eye.Z + forward.Z };
        double[] viewMatrix = Mat4d.Create();
        Mat4d.LookAt(viewMatrix, eye.ToDoubleArray(), center, new[] { 0d, 1d, 0d });
        if (Math.Abs(roll) > 0.0000001d)
        {
            Mat4d.Rotate(viewMatrix, viewMatrix, roll, new[] { 1d, 0d, 0d });
        }

        double[] originMatrix = Mat4d.Create();
        Mat4d.LookAt(
            originMatrix,
            new[] { 0d, 0d, 0d },
            new[] { (double)forward.X, (double)forward.Y, (double)forward.Z },
            new[] { 0d, 1d, 0d });
        if (Math.Abs(roll) > 0.0000001d)
        {
            Mat4d.Rotate(originMatrix, originMatrix, roll, new[] { 1d, 0d, 0d });
        }

        double[] projection = Mat4d.Create();
        Mat4d.Perspective(projection, fov, view.Width / (double)view.Height, zNear, game.MainCamera.ZFar);

        game.MainCamera.Yaw = yaw;
        game.MainCamera.Pitch = pitch;
        game.MainCamera.Roll = roll;
        game.MainCamera.Fov = fov;
        game.MainCamera.ZNear = zNear;
        game.MainCamera.CameraEyePos.Set(eye);
        game.MainCamera.CamSourcePosition.Set(eye);
        game.MainCamera.OriginPosition.Set(0d, 0d, 0d);
        game.MainCamera.forwardVec.Set(forward.X, forward.Y, forward.Z);
        game.MainCamera.CameraMatrix = viewMatrix;
        game.MainCamera.CameraMatrixOrigin = originMatrix;
        for (int index = 0; index < 16; index++)
        {
            game.MainCamera.CameraMatrixOriginf[index] = (float)originMatrix[index];
            game.PerspectiveProjectionMat[index] = projection[index];
            game.PerspectiveViewMat[index] = viewMatrix[index];
        }

        game.EntityPlayer.CameraPos.Set(eye);
        if (game.player is not null)
        {
            game.player.CameraYaw = (float)yaw;
            game.player.CameraPitch = (float)pitch;
            game.player.CameraRoll = (float)roll;
        }

        game.mouseYaw = (float)yaw;
        game.mousePitch = (float)pitch;
        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        uniforms.PlayerViewVector.Set(forward);
        if (uniforms.playerReferencePos is not null)
        {
            uniforms.PlayerPos.Set(eye.SubCopy(uniforms.playerReferencePos));
        }
        if (uniforms.playerReferencePosForFoam is not null)
        {
            uniforms.PlayerPosForFoam.Set(eye.SubCopy(uniforms.playerReferencePosForFoam));
        }
        uniforms.ZNear = zNear;
        uniforms.ZFar = game.MainCamera.ZFar;
        uniforms.FrameWidth = target.Width;
        ApplyDirectorWaterState(pose, uniforms);

        SetStack(game.PMatrix, projection, Math.Max(1, projectionStackCount));
        SetStack(game.MvMatrix, viewMatrix, Math.Max(1, modelViewStackCount));
        game.CurrentMatrixModeProjection = false;
        DirectorOffscreenEngineBindings.SetCurrentRenderStage(game, EnumRenderStage.Opaque);
        BlockPos cameraBlock = new(
            (int)Math.Floor(pose.X),
            (int)Math.Floor(pose.Y),
            (int)Math.Floor(pose.Z),
            pose.Dimension);
        capi.Render.DefaultFrustumCuller.CalcFrustumEquations(cameraBlock, projection, viewMatrix);

        game.Platform.CurrentFrameBuffer = target;
        game.Platform.GlViewport(0, 0, target.Width, target.Height);
        game.Platform.ClearFrameBuffer(target, clearColor, clearDepthBuffer: true, clearColorBuffers: true);
        game.Platform.GlEnableDepthTest();
        game.Platform.GlDepthMask(flag: true);
        game.Platform.GlCullFaceBack();
        game.Platform.GlEnableCullFace();
    }

    internal void Restore()
    {
        try
        {
            capi.Render.CurrentActiveShader?.Stop();
        }
        catch
        {
        }

        camera.Restore(game.MainCamera);
        game.EntityPlayer.CameraPos.Set(entityCameraPosition);
        if (game.player is not null)
        {
            game.player.CameraYaw = playerCameraYaw;
            game.player.CameraPitch = playerCameraPitch;
            game.player.CameraRoll = playerCameraRoll;
        }

        game.mouseYaw = mouseYaw;
        game.mousePitch = mousePitch;
        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        uniforms.PlayerViewVector.Set(shaderPlayerView);
        uniforms.PlayerPos.Set(shaderPlayerPosition);
        uniforms.PlayerPosForFoam.Set(shaderPlayerPositionForFoam);
        uniforms.PointLightsCount = shaderPointLightsCount;
        Array.Copy(
            shaderPointLights,
            uniforms.PointLights3,
            Math.Min(shaderPointLights.Length, uniforms.PointLights3.Length));
        Array.Copy(
            shaderPointLightColors,
            uniforms.PointLightColors3,
            Math.Min(shaderPointLightColors.Length, uniforms.PointLightColors3.Length));
        uniforms.CameraUnderwater = shaderCameraUnderwater;
        uniforms.WaterMurkColor = shaderWaterMurkColor;
        uniforms.ZNear = shaderZNear;
        uniforms.ZFar = shaderZFar;
        uniforms.FrameWidth = shaderFrameWidth;
        RuntimeStats.renderedTriangles = renderedTriangles;
        RuntimeStats.availableTriangles = availableTriangles;
        RuntimeStats.renderedEntities = renderedEntities;
        waterModifierState?.Restore();
        deepWaterModifierState?.Restore();
        ambientBlendState.Restore(game.AmbientManager);
        Array.Copy(perspectiveProjection, game.PerspectiveProjectionMat, 16);
        Array.Copy(perspectiveView, game.PerspectiveViewMat, 16);
        SetStack(game.PMatrix, projectionMatrix, projectionStackCount);
        SetStack(game.MvMatrix, modelViewMatrix, modelViewStackCount);
        game.CurrentMatrixModeProjection = matrixModeProjection;
        DirectorOffscreenEngineBindings.SetCurrentRenderStage(game, renderStage);
        capi.Render.DefaultFrustumCuller.CalcFrustumEquations(
            frustumOrigin,
            projectionMatrix,
            modelViewMatrix);
        foreach (EntityVisibility visibility in entityVisibility)
        {
            visibility.Entity.IsRendered = visibility.IsRendered;
            visibility.Entity.IsShadowRendered = visibility.IsShadowRendered;
        }

        game.Platform.CurrentFrameBuffer = frameBuffer!;
        game.Platform.GlViewport(0, 0, viewportWidth, viewportHeight);
        // RenderToDefaultFramebuffer enters this patch with the screen-manager
        // UI state: depth enabled, depth writes enabled, culling disabled, and
        // standard alpha blending enabled. Restore that boundary explicitly so
        // an offscreen pass cannot leak terrain/particle GL state.
        game.Platform.GlEnableDepthTest();
        game.Platform.GlDepthMask(flag: true);
        game.Platform.GlDisableCullFace();
        game.Platform.GlToggleBlend(on: true);
        game.Platform.GlDepthFunc(EnumDepthFunction.Less);
    }

    private WaterModifierState? CaptureModifier(string code)
        => game.AmbientManager.CurrentModifiers.TryGetValue(code, out AmbientModifier? modifier)
            ? WaterModifierState.Capture(modifier)
            : null;

    private void ApplyDirectorWaterState(
        DirectorOffscreenCameraPose pose,
        DefaultShaderUniforms uniforms)
    {
        float colorShift = game.GetEyesInWaterColorShift() / 100f;
        float depth = game.EyesInWaterDepth();
        uniforms.CameraUnderwater = colorShift;

        if (game.AmbientManager.CurrentModifiers.TryGetValue("water", out AmbientModifier? water))
        {
            water.FogColor.Weight = colorShift * colorShift;
            water.FogDensity.Weight = colorShift;
            water.FogMin.Weight = colorShift;
            SetDirectorWaterColors(pose, depth, water, uniforms);
        }

        if (game.AmbientManager.CurrentModifiers.TryGetValue("deepwater", out AmbientModifier? deepWater))
        {
            float deepWeight = GameMath.Clamp(depth / 70f, 0f, 1f);
            deepWater.FogColor.Weight = 0.95f * deepWeight;
            deepWater.AmbientColor.Weight = 0.85f * deepWeight;
        }

        RecalculateAmbientBlend(pose.Y);
    }

    private void SetDirectorWaterColors(
        DirectorOffscreenCameraPose pose,
        float depth,
        AmbientModifier water,
        DefaultShaderUniforms uniforms)
    {
        int rgba;
        try
        {
            rgba = game.WorldMap.ApplyColorMapOnRgba(
                "climateWaterTint",
                null,
                -1,
                (int)pose.X,
                (int)pose.Y,
                (int)pose.Z,
                flipRb: false);
        }
        catch
        {
            water.AmbientColor.Weight = depth > 0f
                ? GameMath.Clamp(depth / 30f, 0f, 1f)
                : 0f;
            return;
        }

        int[] hsv = ColorUtil.RgbToHsvInts(
            rgba & 0xFF,
            rgba >> 8 & 0xFF,
            rgba >> 16 & 0xFF);
        hsv[2] /= 2;
        hsv[2] = (int)(hsv[2] * Math.Max(0.2f, game.Calendar.DayLightStrength));
        int[] rgb = ColorUtil.Hsv2RgbInts(hsv[0], hsv[1], hsv[2]);
        float red = rgb[0] / 255f;
        float green = rgb[1] / 255f;
        float blue = rgb[2] / 255f;
        water.FogColor.Value[0] = red;
        water.FogColor.Value[1] = green;
        water.FogColor.Value[2] = blue;
        water.AmbientColor.Value[0] = red * 2f;
        water.AmbientColor.Value[1] = green * 2f;
        water.AmbientColor.Value[2] = blue * 2f;
        water.AmbientColor.Weight = depth > 0f
            ? GameMath.Clamp(depth / 30f, 0f, 1f)
            : 0f;

        hsv[1] /= 2;
        rgb = ColorUtil.Hsv2RgbInts(hsv[0], hsv[1], hsv[2]);
        uniforms.WaterMurkColor = new Vec4f(
            rgb[0] / 255f,
            rgb[1] / 255f,
            rgb[2] / 255f,
            1f);
    }

    private void RecalculateAmbientBlend(double cameraY)
    {
        AmbientManager manager = game.AmbientManager;
        AmbientModifier basis = manager.Base;
        float[] fog =
        {
            basis.FogColor.Value[0],
            basis.FogColor.Value[1],
            basis.FogColor.Value[2],
            1f
        };
        float[] ambient =
        {
            basis.AmbientColor.Value[0],
            basis.AmbientColor.Value[1],
            basis.AmbientColor.Value[2]
        };
        float fogDensity = basis.FogDensity.Value;
        float fogMin = basis.FogMin.Value;
        float flatFogDensity = basis.FlatFogDensity.Value;
        float flatFogOffset = basis.FlatFogYPos.Value;
        float cloudBrightness = basis.CloudBrightness.Value;
        float cloudDensity = basis.CloudDensity.Value;
        float sceneBrightness = basis.SceneBrightness.Value;
        float fogBrightness = basis.FogBrightness.Value;

        foreach ((string _, AmbientModifier modifier) in manager.CurrentModifiers)
        {
            float weight = modifier.FogColor.Weight;
            fog[0] = weight * modifier.FogColor.Value[0] + (1f - weight) * fog[0];
            fog[1] = weight * modifier.FogColor.Value[1] + (1f - weight) * fog[1];
            fog[2] = weight * modifier.FogColor.Value[2] + (1f - weight) * fog[2];
            weight = modifier.AmbientColor.Weight;
            ambient[0] = weight * modifier.AmbientColor.Value[0] + (1f - weight) * ambient[0];
            ambient[1] = weight * modifier.AmbientColor.Value[1] + (1f - weight) * ambient[1];
            ambient[2] = weight * modifier.AmbientColor.Value[2] + (1f - weight) * ambient[2];
            weight = modifier.FogDensity.Weight;
            fogDensity = weight * weight * modifier.FogDensity.Value
                + (1f - weight) * (1f - weight) * fogDensity;
            weight = modifier.FlatFogDensity.Weight;
            flatFogDensity = weight * modifier.FlatFogDensity.Value + (1f - weight) * flatFogDensity;
            weight = modifier.FogMin.Weight;
            fogMin = weight * modifier.FogMin.Value + (1f - weight) * fogMin;
            weight = modifier.FlatFogYPos.Weight;
            flatFogOffset = weight * modifier.FlatFogYPos.Value + (1f - weight) * flatFogOffset;
            weight = modifier.CloudBrightness.Weight;
            cloudBrightness = weight * modifier.CloudBrightness.Value + (1f - weight) * cloudBrightness;
            weight = modifier.CloudDensity.Weight;
            cloudDensity = weight * modifier.CloudDensity.Value + (1f - weight) * cloudDensity;
            weight = modifier.SceneBrightness.Weight;
            sceneBrightness = weight * modifier.SceneBrightness.Value + (1f - weight) * sceneBrightness;
            weight = modifier.FogBrightness.Weight;
            fogBrightness = weight * modifier.FogBrightness.Value + (1f - weight) * fogBrightness;
        }

        fog[0] *= sceneBrightness * fogBrightness;
        fog[1] *= sceneBrightness * fogBrightness;
        fog[2] *= sceneBrightness * fogBrightness;
        ambient[0] *= sceneBrightness;
        ambient[1] *= sceneBrightness;
        ambient[2] *= sceneBrightness;
        double highAltitude = Math.Max(0d, (cameraY - game.SeaLevel - 5000d) / 10000d);

        manager.BlendedFogColor.Set(fog[0], fog[1], fog[2], fog[3]);
        manager.BlendedAmbientColor.Set(ambient[0], ambient[1], ambient[2]);
        manager.BlendedFogDensity = Math.Max(0f, fogDensity - (float)highAltitude);
        manager.BlendedFogMin = Math.Max(0f, fogMin - (float)highAltitude);
        manager.BlendedFlatFogDensity = float.IsNaN(flatFogDensity)
            ? 0f
            : (float)(Math.Sign(flatFogDensity)
                * Math.Max(0d, Math.Abs(flatFogDensity) - highAltitude));
        manager.BlendedFlatFogYOffset = flatFogOffset;
        manager.BlendedFlatFogYPosForShader = flatFogOffset + game.SeaLevel;
        manager.BlendedCloudBrightness = cloudBrightness;
        manager.BlendedCloudDensity = cloudDensity;
        manager.BlendedSceneBrightness = sceneBrightness;
        manager.BlendedFogBrightness = fogBrightness;
        DirectorReplayVisualRuntime.ApplyAmbientOverrides(manager);
    }

    private static BlockPos CameraBlockPosition(Vec3d eye)
    {
        int dimension = (int)Math.Floor(eye.Y / 32768d);
        double localY = eye.Y - dimension * 32768d;
        return new BlockPos(
            (int)Math.Floor(eye.X),
            (int)Math.Floor(localY),
            (int)Math.Floor(eye.Z),
            dimension);
    }

    private static double[] CopyTopOrIdentity(StackMatrix4 stack)
        => stack.Count > 0 ? (double[])stack.Top.Clone() : Mat4d.Create();

    private static void SetStack(StackMatrix4 stack, double[] matrix, int desiredCount)
    {
        while (stack.Count > desiredCount)
        {
            stack.Pop();
        }

        while (stack.Count < desiredCount)
        {
            stack.Push(matrix);
        }

        if (desiredCount > 0)
        {
            Mat4d.Copy(stack.Top, matrix);
        }
    }

    private readonly record struct EntityVisibility(
        Entity Entity,
        bool IsRendered,
        bool IsShadowRendered);

    private sealed class WaterModifierState
    {
        private readonly AmbientModifier target;
        private readonly float[] fogColor;
        private readonly float fogColorWeight;
        private readonly float[] ambientColor;
        private readonly float ambientColorWeight;
        private readonly float fogDensity;
        private readonly float fogDensityWeight;
        private readonly float fogMin;
        private readonly float fogMinWeight;

        private WaterModifierState(AmbientModifier target)
        {
            this.target = target;
            fogColor = (float[])target.FogColor.Value.Clone();
            fogColorWeight = target.FogColor.Weight;
            ambientColor = (float[])target.AmbientColor.Value.Clone();
            ambientColorWeight = target.AmbientColor.Weight;
            fogDensity = target.FogDensity.Value;
            fogDensityWeight = target.FogDensity.Weight;
            fogMin = target.FogMin.Value;
            fogMinWeight = target.FogMin.Weight;
        }

        internal static WaterModifierState Capture(AmbientModifier target) => new(target);

        internal void Restore()
        {
            target.FogColor.Set((float[])fogColor.Clone(), fogColorWeight);
            target.AmbientColor.Set((float[])ambientColor.Clone(), ambientColorWeight);
            target.FogDensity.Set(fogDensity, fogDensityWeight);
            target.FogMin.Set(fogMin, fogMinWeight);
        }
    }

    private readonly record struct AmbientBlendState(
        Vec4f FogColor,
        Vec3f AmbientColor,
        float FogDensity,
        float FogMin,
        float FlatFogDensity,
        float FlatFogOffset,
        float FlatFogShaderY,
        float CloudBrightness,
        float CloudDensity,
        float SceneBrightness,
        float FogBrightness)
    {
        internal static AmbientBlendState Capture(AmbientManager manager)
            => new(
                manager.BlendedFogColor.Clone(),
                manager.BlendedAmbientColor.Clone(),
                manager.BlendedFogDensity,
                manager.BlendedFogMin,
                manager.BlendedFlatFogDensity,
                manager.BlendedFlatFogYOffset,
                manager.BlendedFlatFogYPosForShader,
                manager.BlendedCloudBrightness,
                manager.BlendedCloudDensity,
                manager.BlendedSceneBrightness,
                manager.BlendedFogBrightness);

        internal void Restore(AmbientManager manager)
        {
            manager.BlendedFogColor.Set(FogColor.X, FogColor.Y, FogColor.Z, FogColor.W);
            manager.BlendedAmbientColor.Set(AmbientColor.X, AmbientColor.Y, AmbientColor.Z);
            manager.BlendedFogDensity = FogDensity;
            manager.BlendedFogMin = FogMin;
            manager.BlendedFlatFogDensity = FlatFogDensity;
            manager.BlendedFlatFogYOffset = FlatFogOffset;
            manager.BlendedFlatFogYPosForShader = FlatFogShaderY;
            manager.BlendedCloudBrightness = CloudBrightness;
            manager.BlendedCloudDensity = CloudDensity;
            manager.BlendedSceneBrightness = SceneBrightness;
            manager.BlendedFogBrightness = FogBrightness;
        }
    }

    private sealed class CameraState
    {
        public required double[] CameraMatrixReference { get; init; }
        public required double[] CameraMatrixValues { get; init; }
        public required double[] CameraMatrixOriginReference { get; init; }
        public required double[] CameraMatrixOriginValues { get; init; }
        public required float[] CameraMatrixOriginFloatReference { get; init; }
        public required float[] CameraMatrixOriginFloatValues { get; init; }
        public required Vec3d EyePosition { get; init; }
        public required Vec3d SourcePosition { get; init; }
        public required Vec3d OriginPosition { get; init; }
        public required Vec3d ForwardVector { get; init; }
        private double Yaw { get; init; }
        private double Pitch { get; init; }
        private double Roll { get; init; }
        private float Fov { get; init; }
        private float ZNear { get; init; }
        private float ZFar { get; init; }

        internal static CameraState Capture(PlayerCamera source)
        {
            return new CameraState
            {
                CameraMatrixReference = source.CameraMatrix,
                CameraMatrixValues = (double[])source.CameraMatrix.Clone(),
                CameraMatrixOriginReference = source.CameraMatrixOrigin,
                CameraMatrixOriginValues = (double[])source.CameraMatrixOrigin.Clone(),
                CameraMatrixOriginFloatReference = source.CameraMatrixOriginf,
                CameraMatrixOriginFloatValues = (float[])source.CameraMatrixOriginf.Clone(),
                EyePosition = source.CameraEyePos.Clone(),
                SourcePosition = source.CamSourcePosition.Clone(),
                OriginPosition = source.OriginPosition.Clone(),
                ForwardVector = source.forwardVec.Clone(),
                Yaw = source.Yaw,
                Pitch = source.Pitch,
                Roll = source.Roll,
                Fov = source.Fov,
                ZNear = source.ZNear,
                ZFar = source.ZFar
            };
        }

        internal void Restore(PlayerCamera target)
        {
            target.CameraMatrix = CameraMatrixReference;
            Array.Copy(CameraMatrixValues, CameraMatrixReference, 16);
            target.CameraMatrixOrigin = CameraMatrixOriginReference;
            Array.Copy(CameraMatrixOriginValues, CameraMatrixOriginReference, 16);
            target.CameraMatrixOriginf = CameraMatrixOriginFloatReference;
            Array.Copy(CameraMatrixOriginFloatValues, CameraMatrixOriginFloatReference, 16);
            target.CameraEyePos.Set(EyePosition);
            target.CamSourcePosition.Set(SourcePosition);
            target.OriginPosition.Set(OriginPosition);
            target.forwardVec.Set(ForwardVector);
            target.Yaw = Yaw;
            target.Pitch = Pitch;
            target.Roll = Roll;
            target.Fov = Fov;
            target.ZNear = ZNear;
            target.ZFar = ZFar;
        }
    }
}
