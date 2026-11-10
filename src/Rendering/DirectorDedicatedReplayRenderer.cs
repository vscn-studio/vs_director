using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using VSDirector.Replay;
using VSDirector.Runtime;

namespace VSDirector.Rendering;

/// <summary>
/// Experimental replay-only render coordinator. It removes replay actors from
/// the engine registry without destroying their renderers, then invokes the
/// same public renderer entry points from isolated render stages.
/// </summary>
internal sealed class DirectorDedicatedReplayRenderer : IDisposable
{
    private const int DefaultQuadCapacity = 8192;
    private const int DefaultCubeCapacity = 4096;
    private static readonly FieldInfo? ParticleGameSpeedField = AccessTools.Field(
        typeof(ParticlePoolQuads),
        "currentGamespeed");

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly List<DetachedActor> actors = new();
    private readonly List<DetachedActor> visibleActors = new();
    private readonly List<SavedVisibility> savedVisibility = new();
    private readonly List<StageRegistration> stages = new();
    private readonly Dictionary<long, DetachedActor> actorsById = new();
    private readonly Dictionary<VisibilityCacheKey, bool[]> visibilityCache = new();
    private readonly HashSet<long> warmupAttemptedActors = new();
    private readonly HashSet<long> preparedOffscreenActors = new();
    private readonly HashSet<long> failedOffscreenActors = new();
    private readonly HashSet<long> preparedOffscreenShadowActors = new();
    private readonly HashSet<long> failedOffscreenShadowActors = new();
    private ParticlePoolQuads? quadPool;
    private ParticlePoolCubes? cubePool;
    private IParticlePool[] particlePools = Array.Empty<IParticlePool>();
    private long mainVisibilityFrameNumber;
    private long offscreenVisibilityFrameNumber = long.MinValue;
    private long warmupFrameNumber = long.MinValue;
    private long preparedOffscreenFrameNumber = long.MinValue;
    private Guid preparedOffscreenViewId = Guid.Empty;
    private long warmupWorkTicks;
    private int warmupAttempts;
    private int warmupCursor;
    private bool mainFramePassedAfterOit;
    private bool stagesRegistered;
    private bool active;
    private bool disposed;

    internal DirectorDedicatedReplayRenderer(ICoreClientAPI capi, ClientMain game)
    {
        this.capi = capi;
        this.game = game;
    }

    internal bool IsActive => active;

    internal int ActorCount => actors.Count;

    internal void Start(IEnumerable<Entity> replayActors, int particleEventCount = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(replayActors);
        Stop();
        if (stagesRegistered)
        {
            throw new InvalidOperationException(
                "A previous dedicated replay render stage is still registered.");
        }

        ParticlePoolQuads? createdQuads = null;
        ParticlePoolCubes? createdCubes = null;
        try
        {
            if (particleEventCount > 0)
            {
                createdQuads = new ParticlePoolQuads(DefaultQuadCapacity, game, offthread: false);
                createdCubes = new ParticlePoolCubes(DefaultCubeCapacity, game, offthread: false);
                PrimeParticlePool(createdQuads);
                PrimeParticlePool(createdCubes);
            }

            foreach (Entity entity in replayActors)
            {
                if (!TryTakeActor(entity))
                {
                    throw new InvalidOperationException(
                        $"Replay actor renderer {entity.EntityId} could not be detached.");
                }
            }

            quadPool = createdQuads;
            cubePool = createdCubes;
            particlePools = quadPool is not null && cubePool is not null
                ? new IParticlePool[] { quadPool, cubePool }
                : Array.Empty<IParticlePool>();
            RegisterStages();
            active = true;

            if (particleEventCount > DefaultQuadCapacity + DefaultCubeCapacity)
            {
                capi.Logger.Debug(
                    "VS Director: dedicated replay particle pools initialized for {0} recorded events ({1} quad, {2} cube slots).",
                    particleEventCount,
                    DefaultQuadCapacity,
                    DefaultCubeCapacity);
            }
        }
        catch
        {
            active = false;
            UnregisterStages();
            RestoreActors();
            DisposeParticlePool(createdQuads);
            DisposeParticlePool(createdCubes);
            quadPool = null;
            cubePool = null;
            particlePools = Array.Empty<IParticlePool>();
            throw;
        }
    }

    internal void Stop()
    {
        active = false;
        UnregisterStages();
        RestoreActors();
        DisposeParticlePool(quadPool);
        DisposeParticlePool(cubePool);
        quadPool = null;
        cubePool = null;
        particlePools = Array.Empty<IParticlePool>();
        actorsById.Clear();
        visibleActors.Clear();
        savedVisibility.Clear();
        visibilityCache.Clear();
        warmupAttemptedActors.Clear();
        preparedOffscreenActors.Clear();
        failedOffscreenActors.Clear();
        preparedOffscreenShadowActors.Clear();
        failedOffscreenShadowActors.Clear();
        mainFramePassedAfterOit = false;
        offscreenVisibilityFrameNumber = long.MinValue;
        warmupFrameNumber = long.MinValue;
        preparedOffscreenFrameNumber = long.MinValue;
        preparedOffscreenViewId = Guid.Empty;
        warmupWorkTicks = 0L;
        warmupAttempts = 0;
        warmupCursor = 0;
    }

    internal bool TryGetRenderer(long entityId, out EntityRenderer? renderer)
    {
        if (DirectorOffscreenEngineBindings.TryGetEntityRenderer(game, entityId, out renderer))
        {
            return true;
        }

        if (actorsById.TryGetValue(entityId, out DetachedActor? actor))
        {
            renderer = actor.Renderer;
            return true;
        }

        renderer = null;
        return false;
    }

    internal IParticlePool? GetParticlePool(EnumParticleModel model)
        => active
            ? model switch
            {
                EnumParticleModel.Quad => quadPool,
                EnumParticleModel.Cube => cubePool,
                _ => null
            }
            : null;

    internal IParticlePool[] GetParticlePools()
        => active ? particlePools : Array.Empty<IParticlePool>();

    internal bool OwnsParticlePool(IParticlePool pool)
        => active
            && (ReferenceEquals(pool, quadPool) || ReferenceEquals(pool, cubePool));

    private bool TryTakeActor(Entity entity)
    {
        // The local player's renderer is also used by the camera and by the
        // optional offscreen local-player pass. Keep that engine-owned entry
        // in place; a replay ghost can still be detached normally.
        if (ReferenceEquals(entity, game.EntityPlayer))
        {
            return true;
        }

        if (actorsById.TryGetValue(entity.EntityId, out DetachedActor? existingActor))
        {
            return ReferenceEquals(existingActor.Entity, entity);
        }

        if (!DirectorOffscreenEngineBindings.TryGetEntityRenderer(
                game,
                entity.EntityId,
                out EntityRenderer? current)
            || current is null
            || !ReferenceEquals(current.entity, entity))
        {
            capi.Logger.Warning(
                "VS Director: replay actor {0} has no matching renderer to detach.",
                entity.EntityId);
            return false;
        }

        if (!DirectorOffscreenEngineBindings.TryDetachEntityRenderer(
                game,
                entity.EntityId,
                out EntityRenderer? detached)
            || !ReferenceEquals(detached, current))
        {
            capi.Logger.Warning(
                "VS Director: replay actor renderer {0} changed before it could be detached.",
                entity.EntityId);
            return false;
        }

        DetachedActor actor = new(entity, current);
        actors.Add(actor);
        actorsById.Add(entity.EntityId, actor);
        return true;
    }

    private void RestoreActors()
    {
        foreach (DetachedActor actor in actors)
        {
            bool ownsRendererPointer = actor.Entity.Properties?.Client is { } clientState
                && ReferenceEquals(clientState.Renderer, actor.Renderer);
            if (!ownsRendererPointer
                && (!DirectorOffscreenEngineBindings.TryGetEntityRenderer(
                        game,
                        actor.Entity.EntityId,
                        out EntityRenderer? existing)
                    || !ReferenceEquals(existing, actor.Renderer)))
            {
                DisposeDetachedActor(actor);
                continue;
            }

            bool restored = false;
            try
            {
                restored = DirectorOffscreenEngineBindings.TryRestoreEntityRenderer(
                    game,
                    actor.Entity.EntityId,
                    actor.Renderer);
            }
            catch (Exception exception)
            {
                capi.Logger.Warning(
                    "VS Director: replay actor renderer {0} could not be restored: {1}",
                    actor.Entity.EntityId,
                    exception.GetBaseException().Message);
            }

            if (restored)
            {
                if (actor.Entity.Properties?.Client is { } client
                    && client.Renderer is null)
                {
                    client.Renderer = actor.Renderer;
                }
                continue;
            }

            capi.Logger.Warning(
                "VS Director: replay actor renderer {0} was replaced while detached; disposing the stale renderer.",
                actor.Entity.EntityId);
            DirectorOffscreenEngineBindings.TryGetEntityRenderer(
                game,
                actor.Entity.EntityId,
                out EntityRenderer? replacement);
            DisposeDetachedActor(actor);
            if (actor.Entity.Properties?.Client is { } clientProperties
                && ReferenceEquals(clientProperties.Renderer, actor.Renderer))
            {
                clientProperties.Renderer = replacement;
            }
        }

        actors.Clear();
        actorsById.Clear();
    }

    private void DisposeDetachedActor(DetachedActor actor)
    {
        try
        {
            actor.Renderer.Dispose();
        }
        catch (Exception exception)
        {
            capi.Logger.Debug(
                "VS Director: stale replay actor renderer cleanup failed: {0}",
                exception.GetBaseException().Message);
        }
    }

    private void RegisterStages()
    {
        if (stagesRegistered)
        {
            throw new InvalidOperationException(
                "A previous dedicated replay render stage could not be unregistered.");
        }

        stages.Clear();
        AddStage(EnumRenderStage.Before, 0.41d, "actors-before", RenderActorsBefore);
        AddStage(EnumRenderStage.Opaque, 0.41d, "actors-opaque", RenderActorsOpaque);
        AddStage(EnumRenderStage.AfterOIT, 0.71d, "actors-after-oit", RenderActorsAfterOit);
        AddStage(EnumRenderStage.ShadowFar, 0.41d, "actors-shadow-far", RenderActorShadows);
        AddStage(EnumRenderStage.ShadowNear, 0.41d, "actors-shadow-near", RenderActorShadows);
        AddStage(EnumRenderStage.Ortho, 0.41d, "actors-ortho", RenderActorsOrtho);
        if (particlePools.Length > 0)
        {
            AddStage(
                EnumRenderStage.Opaque,
                0.61d,
                "particles-cube",
                (deltaTime, _) => RenderParticles(EnumParticleModel.Cube, deltaTime));
            AddStage(
                EnumRenderStage.OIT,
                0.61d,
                "particles-quad",
                (deltaTime, _) => RenderParticles(EnumParticleModel.Quad, deltaTime));
        }
        stagesRegistered = true;
    }

    private void AddStage(
        EnumRenderStage stage,
        double order,
        string name,
        Action<float, EnumRenderStage> callback)
    {
        StageRenderer renderer = new(stage, order, callback);
        capi.Event.RegisterRenderer(renderer, stage, "vsdirector-dedicated-" + name);
        stages.Add(new StageRegistration(stage, renderer));
    }

    private void UnregisterStages()
    {
        if (stages.Count == 0)
        {
            stagesRegistered = false;
            return;
        }

        List<StageRegistration> failed = new();
        for (int index = stages.Count - 1; index >= 0; index--)
        {
            StageRegistration registration = stages[index];
            try
            {
                capi.Event.UnregisterRenderer(registration.Renderer, registration.Stage);
            }
            catch (Exception exception)
            {
                capi.Logger.Debug(
                    "VS Director: dedicated replay stage {0} could not be unregistered: {1}",
                    registration.Stage,
                    exception.GetBaseException().Message);
                failed.Add(registration);
            }
        }

        stages.Clear();
        failed.Reverse();
        stages.AddRange(failed);
        stagesRegistered = stages.Count > 0;
    }

    private void RenderActorsBefore(float deltaTime, EnumRenderStage stage)
    {
        if (!active)
        {
            return;
        }

        BeginMainVisibilityFrameIfNeeded(stage);
        WarmPendingActorRenderers();
        PrepareVisibility(shadowPass: false, stage);
        try
        {
            foreach (DetachedActor actor in visibleActors)
            {
                actor.Renderer.BeforeRender(deltaTime);
            }
        }
        finally
        {
            RestoreVisibility();
        }
    }

    private void RenderActorsOpaque(float deltaTime, EnumRenderStage stage)
    {
        if (!active)
        {
            return;
        }

        BeginMainVisibilityFrameIfNeeded(stage);
        WarmPendingActorRenderers();
        PrepareVisibility(shadowPass: false, stage);
        ShaderProgramEntityanimated? shader = null;
        bool matrixPushed = false;
        try
        {
            if (DirectorOffscreenRenderRuntime.IsRendering)
            {
                PrepareActorsForOffscreenView();
            }

            game.GlMatrixModeModelView();
            game.Platform.GlDisableCullFace();
            game.Platform.GlToggleBlend(on: true);
            game.Platform.GlEnableDepthTest();
            foreach (DetachedActor actor in visibleActors)
            {
                actor.Renderer.DoRender3DOpaque(deltaTime, isShadowPass: false);
                RuntimeStats.renderedEntities++;
            }

            shader = ShaderPrograms.Entityanimated;
            shader.Use();
            shader.RgbaAmbientIn = capi.Render.AmbientColor;
            shader.RgbaFogIn = capi.Render.FogColor;
            shader.FogMinIn = capi.Render.FogMin;
            shader.FogDensityIn = capi.Render.FogDensity;
            shader.ProjectionMatrix = game.CurrentProjectionMatrix;
            shader.EntityTex2D = game.EntityAtlasManager.AtlasTextures[0].TextureId;
            shader.AlphaTest = 0.05f;
            shader.LightPosition = game.shUniforms.LightPosition3D;
            game.Platform.GlDisableCullFace();
            game.GlMatrixModeModelView();
            game.GlPushMatrix();
            matrixPushed = true;
            game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);
            game.Platform.GlToggleBlend(on: true);
            foreach (DetachedActor actor in visibleActors)
            {
                actor.Renderer.DoRender3DOpaqueBatched(deltaTime, isShadowPass: false);
            }
        }
        finally
        {
            if (matrixPushed)
            {
                game.GlPopMatrix();
            }
            shader?.Stop();
            game.Platform.GlToggleBlend(on: false);
            RestoreVisibility();
        }
    }

    private void RenderActorsAfterOit(float deltaTime, EnumRenderStage stage)
    {
        if (!active)
        {
            return;
        }

        PrepareVisibility(shadowPass: false, stage);
        try
        {
            if (DirectorOffscreenRenderRuntime.IsRendering)
            {
                // Opaque preparation failures are scoped to the color path.
                // Reusing the same set removes failed actors here without
                // invoking BeforeRender while the OIT shader is active.
                PrepareActorsForOffscreenView(invokeRenderer: false);
            }

            game.GlMatrixModeModelView();
            game.Platform.GlDisableCullFace();
            game.Platform.GlToggleBlend(on: true);
            game.Platform.GlEnableDepthTest();
            foreach (DetachedActor actor in visibleActors)
            {
                actor.Renderer.DoRender3DAfterOIT(deltaTime, isShadowPass: false);
            }
        }
        finally
        {
            RestoreVisibility();
            if (!DirectorOffscreenRenderRuntime.IsRendering)
            {
                // The next Opaque stage is the first reliable boundary when
                // a client build omits Before or Ortho for a frame.  Marking
                // this edge lets it invalidate the previous frame's cache.
                mainFramePassedAfterOit = true;
            }
        }
    }

    private void RenderActorShadows(float deltaTime, EnumRenderStage stage)
    {
        if (!active)
        {
            return;
        }

        BeginMainVisibilityFrameIfNeeded(stage);
        // Offscreen views can enter the shadow stages before their opaque
        // stage. Run the same bounded animator warm-up here so an actor that
        // becomes ready in this frame can contribute to both shadow and color
        // passes. Restore the incoming shadow shader after renderer hooks.
        ShaderProgramBase? shaderBeforeWarmup = ShaderProgramBase.CurrentShaderProgram;
        try
        {
            WarmPendingActorRenderers();
            PrepareVisibility(shadowPass: true, stage);
            if (DirectorOffscreenRenderRuntime.IsRendering)
            {
                PrepareActorsForOffscreenView(shadowPass: true);
            }
        }
        catch
        {
            RestoreVisibility();
            throw;
        }
        finally
        {
            try
            {
                if (shaderBeforeWarmup is not null)
                {
                    // BeforeRender hooks can change the raw GL program without
                    // updating CurrentShaderProgram. Rebind unconditionally so
                    // the shadow pass never trusts a stale managed reference.
                    shaderBeforeWarmup.Use();
                }
            }
            catch
            {
                RestoreVisibility();
                throw;
            }
        }
        ShaderProgramChunkshadowmap? chunkShader = null;
        ShaderProgramShadowmapentityanimated? entityShader = null;
        try
        {
            if (ShaderProgramBase.CurrentShaderProgram is not ShaderProgramChunkshadowmap currentChunkShader)
            {
                return;
            }

            chunkShader = currentChunkShader;
            if (capi.Render.UseSSBOs)
            {
                if (capi.Shader.GetProgram(42) is not ShaderProgramChunkshadowmap ssboChunkShader)
                {
                    return;
                }

                chunkShader.Stop();
                chunkShader = ssboChunkShader;
                chunkShader.Use();
            }

            foreach (DetachedActor actor in visibleActors)
            {
                actor.Renderer.DoRender3DOpaque(deltaTime, isShadowPass: true);
            }

            chunkShader.Stop();
            entityShader = ShaderPrograms.Shadowmapentityanimated;
            entityShader.Use();
            entityShader.ProjectionMatrix = game.CurrentProjectionMatrix;
            entityShader.EntityTex2D = game.EntityAtlasManager.AtlasTextures[0].TextureId;
            foreach (DetachedActor actor in visibleActors)
            {
                actor.Renderer.DoRender3DOpaqueBatched(deltaTime, isShadowPass: true);
            }
        }
        finally
        {
            entityShader?.Stop();
            if (chunkShader is not null)
            {
                chunkShader.Use();
                if (DirectorOffscreenEngineBindings.GetShadowMvpMatrix(game) is float[] shadowMvp)
                {
                    chunkShader.MvpMatrix = shadowMvp;
                }
            }
            RestoreVisibility();
        }
    }

    private void RenderActorsOrtho(float deltaTime, EnumRenderStage stage)
    {
        if (!active)
        {
            return;
        }

        PrepareVisibility(shadowPass: false, stage);
        try
        {
            foreach (DetachedActor actor in visibleActors)
            {
                actor.Renderer.DoRender2D(deltaTime);
            }
        }
        finally
        {
            RestoreVisibility();
        }
    }

    private void PrepareActorsForOffscreenView(
        bool shadowPass = false,
        bool invokeRenderer = true)
    {
        DirectorOffscreenViewRequest? view = DirectorOffscreenRenderRuntime.CurrentView;
        if (view is null)
        {
            return;
        }

        long frameNumber = DirectorOffscreenRenderRuntime.CurrentPhysicalFrameNumber
            ?? long.MinValue;
        if (preparedOffscreenFrameNumber != frameNumber
            || preparedOffscreenViewId != view.ViewId)
        {
            preparedOffscreenActors.Clear();
            failedOffscreenActors.Clear();
            preparedOffscreenShadowActors.Clear();
            failedOffscreenShadowActors.Clear();
            preparedOffscreenFrameNumber = frameNumber;
            preparedOffscreenViewId = view.ViewId;
        }

        HashSet<long> preparedActors = shadowPass
            ? preparedOffscreenShadowActors
            : preparedOffscreenActors;
        HashSet<long> failedActors = shadowPass
            ? failedOffscreenShadowActors
            : failedOffscreenActors;
        DirectorReplayManager? replay = DirectorReplayRuntime.Manager;
        for (int index = visibleActors.Count - 1; index >= 0; index--)
        {
            DetachedActor actor = visibleActors[index];
            long entityId = actor.Entity.EntityId;
            if (failedActors.Contains(entityId))
            {
                actor.Entity.IsRendered = false;
                actor.Entity.IsShadowRendered = false;
                visibleActors.RemoveAt(index);
                continue;
            }
            if (preparedActors.Contains(entityId))
            {
                continue;
            }
            if (!invokeRenderer)
            {
                // AfterOIT must not invoke arbitrary renderer preparation while
                // the weighted-OIT shader is active. An actor not prepared by
                // Opaque is omitted from this late pass instead.
                visibleActors.RemoveAt(index);
                continue;
            }

            try
            {
                replay?.PrepareReplayEntityAnimationForOffscreenRender(actor.Entity);
                actor.Renderer.BeforeRender(0f);
                preparedActors.Add(entityId);
            }
            catch (Exception exception)
            {
                failedActors.Add(entityId);
                actor.Entity.IsRendered = false;
                actor.Entity.IsShadowRendered = false;
                visibleActors.RemoveAt(index);
                capi.Logger.Debug(
                    "VS Director: replay actor {0} offscreen preparation failed: {1}",
                    entityId,
                    exception.GetBaseException().Message);
            }
        }
    }

    private void WarmPendingActorRenderers()
    {
        DirectorReplayManager? replay = DirectorReplayRuntime.Manager;
        if (replay is null || actors.Count == 0)
        {
            return;
        }

        long frameNumber = replay.DedicatedReplayAnimationFrame;
        if (warmupFrameNumber != frameNumber)
        {
            warmupFrameNumber = frameNumber;
            warmupWorkTicks = 0L;
            warmupAttempts = 0;
            warmupAttemptedActors.Clear();
        }

        int attemptBudget = replay.DedicatedReplayAnimationWarmupBudget;
        long workBudgetTicks = Math.Max(
            1L,
            (long)Math.Ceiling(
                DirectorReplayActorSchedulingPolicy.DedicatedWarmupWorkBudgetMilliseconds
                * Stopwatch.Frequency
                / 1000d));
        if (attemptBudget <= 0
            || warmupAttempts >= attemptBudget
            || warmupWorkTicks >= workBudgetTicks)
        {
            return;
        }

        int attemptsBefore = warmupAttempts;
        long workBefore = warmupWorkTicks;
        int start = warmupCursor % actors.Count;
        for (int offset = 0; offset < actors.Count; offset++)
        {
            if (warmupAttempts >= attemptBudget || warmupWorkTicks >= workBudgetTicks)
            {
                break;
            }

            int index = (start + offset) % actors.Count;
            DetachedActor actor = actors[index];
            long entityId = actor.Entity.EntityId;
            if (warmupAttemptedActors.Contains(entityId))
            {
                continue;
            }
            if (!replay.NeedsDedicatedReplayAnimationWarmup(actor.Entity))
            {
                continue;
            }

            Entity entity = actor.Entity;
            bool rendered = entity.IsRendered;
            bool shadowRendered = entity.IsShadowRendered;
            bool? fallingBlockRendered = replay.TryGetDedicatedFallingBlockRenderState(
                entity,
                out bool currentFallingBlockRendered)
                    ? currentFallingBlockRendered
                    : null;
            if (!replay.BeginDedicatedReplayAnimationWarmup(entity))
            {
                continue;
            }

            warmupAttemptedActors.Add(entityId);
            warmupAttempts++;
            warmupCursor = (index + 1) % actors.Count;
            long workStart = Stopwatch.GetTimestamp();
            try
            {
                // A gated actor is normally invisible, but EntityRenderer's
                // preparation hook is what creates its animator on some client
                // builds. Keep the hook isolated and never draw this warm-up.
                entity.IsRendered = true;
                entity.IsShadowRendered = true;
                if (fallingBlockRendered.HasValue)
                {
                    replay.SetDedicatedFallingBlockRenderState(entity, true);
                }
                actor.Renderer.BeforeRender(0f);
                replay.CompleteDedicatedReplayAnimationWarmup(entity);
            }
            catch (Exception exception)
            {
                replay.FailDedicatedReplayAnimationWarmup(entity);
                capi.Logger.Debug(
                    "VS Director: replay actor {0} animator warm-up failed: {1}",
                    entity.EntityId,
                    exception.GetBaseException().Message);
            }
            finally
            {
                entity.IsRendered = rendered;
                entity.IsShadowRendered = shadowRendered;
                if (fallingBlockRendered is bool savedFallingBlockRendered)
                {
                    replay.SetDedicatedFallingBlockRenderState(
                        entity,
                        savedFallingBlockRendered);
                }
                replay.EndDedicatedReplayAnimationWarmup(entity);
                warmupWorkTicks += Stopwatch.GetTimestamp() - workStart;
            }
        }

        replay.RecordDedicatedReplayAnimationWarmupWork(
            warmupWorkTicks - workBefore,
            warmupAttempts - attemptsBefore);
    }

    private void BeginMainVisibilityFrameIfNeeded(EnumRenderStage stage)
    {
        if (DirectorOffscreenRenderRuntime.IsRendering)
        {
            return;
        }

        // Before is the normal frame-start boundary. If a client build does
        // not emit it, the first stage after AfterOIT starts the next epoch
        // instead of reusing the previous frame's culling result.
        bool startsFrame = stage == EnumRenderStage.Before || mainFramePassedAfterOit;
        if (!startsFrame)
        {
            return;
        }

        mainVisibilityFrameNumber++;
        mainFramePassedAfterOit = false;
        RemoveCachedVisibility(isOffscreen: false);
    }

    private void PrepareVisibility(bool shadowPass, EnumRenderStage stage)
    {
        visibleActors.Clear();
        savedVisibility.Clear();
        DirectorReplayManager? replay = DirectorReplayRuntime.Manager;
        try
        {
            VisibilityCacheKey key = GetVisibilityCacheKey(shadowPass, stage);
            if (!visibilityCache.TryGetValue(key, out bool[]? cached)
                || cached.Length != actors.Count)
            {
                cached = new bool[actors.Count];
                for (int index = 0; index < actors.Count; index++)
                {
                    cached[index] = IsActorVisible(actors[index].Entity, shadowPass);
                }
                visibilityCache[key] = cached;
            }

            for (int index = 0; index < actors.Count; index++)
            {
                DetachedActor actor = actors[index];
                Entity entity = actor.Entity;
                bool? fallingBlockRendered = replay is not null
                    && replay.TryGetDedicatedFallingBlockRenderState(
                        entity,
                        out bool currentFallingBlockRendered)
                        ? currentFallingBlockRendered
                        : null;
                savedVisibility.Add(new SavedVisibility(
                    entity,
                    entity.IsRendered,
                    entity.IsShadowRendered,
                    fallingBlockRendered));
                bool visible = cached[index] && replay?.IsReplacedByCreation(entity.EntityId) != true
                    && VSDirector.Runtime.DirectorCreationRuntime.Current?.ShouldHideEntity(entity) != true;
                if (shadowPass)
                {
                    entity.IsShadowRendered = visible;
                }
                else
                {
                    entity.IsRendered = visible;
                }
                if (fallingBlockRendered.HasValue)
                {
                    replay?.SetDedicatedFallingBlockRenderState(entity, visible);
                }

                if (visible)
                {
                    visibleActors.Add(actor);
                }
            }
        }
        catch
        {
            RestoreVisibility();
            throw;
        }
    }

    private VisibilityCacheKey GetVisibilityCacheKey(bool shadowPass, EnumRenderStage stage)
    {
        VisibilityDomain domain = shadowPass
            ? stage switch
            {
                EnumRenderStage.ShadowFar => VisibilityDomain.ShadowFar,
                EnumRenderStage.ShadowNear => VisibilityDomain.ShadowNear,
                _ => VisibilityDomain.ShadowOther
            }
            : stage == EnumRenderStage.Before
                ? VisibilityDomain.Before
                : VisibilityDomain.Regular;

        if (DirectorOffscreenRenderRuntime.CurrentView is { } view)
        {
            long frameNumber = DirectorOffscreenRenderRuntime.CurrentPhysicalFrameNumber
                ?? long.MinValue;
            if (frameNumber != offscreenVisibilityFrameNumber)
            {
                RemoveCachedVisibility(isOffscreen: true);
                offscreenVisibilityFrameNumber = frameNumber;
            }

            return new VisibilityCacheKey(
                IsOffscreen: true,
                frameNumber,
                view.ViewId,
                domain);
        }

        return new VisibilityCacheKey(
            IsOffscreen: false,
            mainVisibilityFrameNumber,
            Guid.Empty,
            domain);
    }

    private void RemoveCachedVisibility(bool isOffscreen)
    {
        foreach (VisibilityCacheKey key in visibilityCache.Keys
            .Where(key => key.IsOffscreen == isOffscreen)
            .ToArray())
        {
            visibilityCache.Remove(key);
        }
    }

    private void RestoreVisibility()
    {
        DirectorReplayManager? replay = DirectorReplayRuntime.Manager;
        foreach (SavedVisibility saved in savedVisibility)
        {
            saved.Entity.IsRendered = saved.Rendered;
            saved.Entity.IsShadowRendered = saved.ShadowRendered;
            if (saved.FallingBlockRendered is bool fallingBlockRendered)
            {
                replay?.SetDedicatedFallingBlockRenderState(
                    saved.Entity,
                    fallingBlockRendered);
            }
        }
        savedVisibility.Clear();
        visibleActors.Clear();
    }

    private bool IsActorVisible(Entity entity, bool shadowPass)
    {
        DirectorReplayManager? replay = DirectorReplayRuntime.Manager;
        if (replay is not null
            && (replay.IsLiveEntitySuppressed(entity)
                || !replay.ShouldRenderReplayEntity(entity.EntityId)))
        {
            return false;
        }

        DirectorOffscreenViewRequest? view = DirectorOffscreenRenderRuntime.CurrentView;
        if (view is not null)
        {
            if (!view.RenderEntities
                || entity.Pos.Dimension != view.Pose.Dimension
                || (ReferenceEquals(entity, game.EntityPlayer) && !view.RenderLocalPlayer))
            {
                return false;
            }
            if (shadowPass
                && !ReferenceEquals(entity, game.EntityPlayer)
                && (!game.WorldMap.IsValidPos(
                        (int)entity.Pos.X,
                        (int)entity.Pos.InternalY,
                        (int)entity.Pos.Z)
                    || !game.WorldMap.IsChunkRendered(entity.Pos)))
            {
                return false;
            }

            double absoluteY = view.Pose.Y + view.Pose.Dimension * 32768d;
            double dx = entity.Pos.X - view.Pose.X;
            double dy = entity.Pos.InternalY - absoluteY;
            double dz = entity.Pos.Z - view.Pose.Z;
            double maximumDistanceSquared = ClientSettings.ViewDistance * ClientSettings.ViewDistance;
            double radius = shadowPass
                ? 3d
                : Math.Max(
                    entity.FrustumSphereRadius,
                    Math.Abs(dy) < 1d ? 1d : entity.FrustumSphereRadius);
            return (entity.AllowOutsideLoadedRange
                    || dx * dx + dz * dz <= maximumDistanceSquared)
                && capi.Render.DefaultFrustumCuller.SphereInFrustum(
                    entity.Pos.X,
                    entity.Pos.InternalY,
                    entity.Pos.Z,
                    radius);
        }

        CameraPreset? primaryPose = DirectorCameraRuntime.Controller?.IsActive == true
            ? DirectorCameraRuntime.Controller.CurrentPose
            : null;
        int primaryDimension = primaryPose?.Dimension ?? game.EntityPlayer.Pos.Dimension;
        if (entity.Pos.Dimension != primaryDimension)
        {
            return false;
        }
        if (shadowPass
            && !ReferenceEquals(entity, game.EntityPlayer)
            && !game.WorldMap.IsValidPos(
                (int)entity.Pos.X,
                (int)entity.Pos.InternalY,
                (int)entity.Pos.Z))
        {
            return false;
        }

        double cameraX = primaryPose?.X ?? game.MainCamera.CameraEyePos.X;
        double cameraZ = primaryPose?.Z ?? game.MainCamera.CameraEyePos.Z;
        double distanceX = entity.Pos.X - cameraX;
        double distanceZ = entity.Pos.Z - cameraZ;
        double maximumPrimaryDistanceSquared = ClientSettings.ViewDistance * ClientSettings.ViewDistance;
        return (entity.AllowOutsideLoadedRange
                || (distanceX * distanceX + distanceZ * distanceZ <= maximumPrimaryDistanceSquared
                    && game.WorldMap.IsChunkRendered(entity.Pos)))
            && game.frustumCuller.SphereInFrustum(
                (float)entity.Pos.X,
                (float)entity.Pos.InternalY,
                (float)entity.Pos.Z,
                shadowPass ? 3d : entity.FrustumSphereRadius);
    }

    private void RenderParticles(EnumParticleModel model, float deltaTime)
    {
        if (!active
            || !ClientSettings.RenderParticles
            || DirectorOffscreenRenderRuntime.CurrentView is { RenderOpaqueParticles: false })
        {
            return;
        }

        IParticlePool? pool = GetParticlePool(model);
        if (pool is null)
        {
            return;
        }

        ShaderProgramBase shader = model == EnumParticleModel.Cube
            ? ShaderPrograms.Particlescube
            : ShaderPrograms.Particlesquad;
        bool isOpaqueParticlePass = model == EnumParticleModel.Cube;
        bool matrixPushed = false;
        try
        {
            shader.Use();
            // The cube pass follows the native opaque-particle contract. The
            // quad pass runs inside weighted OIT: its per-attachment blend
            // functions are configured by the engine before OIT and must not
            // be replaced with the ordinary alpha blend state here.
            if (isOpaqueParticlePass)
            {
                game.Platform.GlToggleBlend(on: true);
            }
            game.GlPushMatrix();
            matrixPushed = true;
            game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);
            IShaderProgram activeShader = ShaderProgramBase.CurrentShaderProgram;
            activeShader.Uniform("rgbaFogIn", game.AmbientManager.BlendedFogColor);
            activeShader.Uniform("rgbaAmbientIn", game.AmbientManager.BlendedAmbientColor);
            activeShader.Uniform("fogMinIn", game.AmbientManager.BlendedFogMin);
            activeShader.Uniform("fogDensityIn", game.AmbientManager.BlendedFogDensity);
            activeShader.UniformMatrix("projectionMatrix", game.CurrentProjectionMatrix);
            activeShader.UniformMatrix("modelViewMatrix", game.CurrentModelViewMatrix);
            // This call is Harmony-routed through ReplayParticlePoolTimePatch.
            // Owned pools consume the replay frame clock; offscreen passes publish
            // a zero delta and only rebase the mesh to the current camera.
            pool.OnNewFrame(deltaTime, game.EntityPlayer.CameraPos);
            if (pool.ShouldRender())
            {
                game.Platform.RenderMeshInstanced(pool.Model, pool.QuantityAlive);
            }
        }
        finally
        {
            shader.Stop();
            if (matrixPushed)
            {
                game.GlPopMatrix();
            }
        }
    }

    private void PrimeParticlePool(ParticlePoolQuads pool)
    {
        // ParticlePoolQuads skips OnNewFrame while the client is paused, but
        // SpawnParticles still reads currentGamespeed. Seed that private
        // value so a replay started from a paused screen does not lose its
        // first particle burst.
        float gameSpeed = game.Calendar.SpeedOfTime / 60f;
        if (!float.IsFinite(gameSpeed) || gameSpeed <= 0f)
        {
            gameSpeed = 1f;
        }
        ParticleGameSpeedField?.SetValue(pool, gameSpeed);
        if (!game.IsPaused)
        {
            pool.OnNewFrame(0f, game.EntityPlayer.CameraPos);
        }
    }

    private void DisposeParticlePool(IParticlePool? pool)
    {
        if (pool is null)
        {
            return;
        }

        try
        {
            pool.Dipose();
        }
        catch (Exception exception)
        {
            capi.Logger.Debug(
                "VS Director: dedicated replay particle pool cleanup failed: {0}",
                exception.GetBaseException().Message);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Stop();
        disposed = true;
    }

    private sealed record DetachedActor(Entity Entity, EntityRenderer Renderer);

    private enum VisibilityDomain
    {
        Before,
        Regular,
        ShadowFar,
        ShadowNear,
        ShadowOther
    }

    private readonly record struct VisibilityCacheKey(
        bool IsOffscreen,
        long FrameNumber,
        Guid ViewId,
        VisibilityDomain Domain);

    private readonly record struct SavedVisibility(
        Entity Entity,
        bool Rendered,
        bool ShadowRendered,
        bool? FallingBlockRendered);

    private readonly record struct StageRegistration(
        EnumRenderStage Stage,
        StageRenderer Renderer);

    private sealed class StageRenderer : IRenderer
    {
        private readonly EnumRenderStage stage;
        private readonly Action<float, EnumRenderStage> callback;

        internal StageRenderer(
            EnumRenderStage stage,
            double renderOrder,
            Action<float, EnumRenderStage> callback)
        {
            this.stage = stage;
            this.callback = callback;
            RenderOrder = renderOrder;
        }

        public double RenderOrder { get; }

        public int RenderRange => int.MaxValue;

        public void OnRenderFrame(float deltaTime, EnumRenderStage currentStage)
        {
            if (currentStage == stage)
            {
                callback(deltaTime, currentStage);
            }
        }

        public void Dispose()
        {
        }
    }
}
