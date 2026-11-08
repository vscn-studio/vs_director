using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Replay;

namespace VSDirector.Rendering;

/// <summary>
/// Maintains a detached animation and renderer-time state for entities visible
/// to offscreen cameras. Game logic still runs exactly once in the primary
/// client. Only render-time animation that vanilla skipped because an entity
/// was outside the primary view is advanced here, once per physical frame.
/// </summary>
internal sealed class DirectorOffscreenEntityAnimationScheduler : IDisposable
{
    private static readonly Type? EntityShapeRendererType =
        AccessTools.TypeByName("Vintagestory.GameContent.EntityShapeRenderer");

    private static readonly Type? PlayerShapeRendererType =
        AccessTools.TypeByName("Vintagestory.GameContent.EntityPlayerShapeRenderer");

    private static readonly MethodInfo? LoadEntityModelMatrixMethod =
        EntityShapeRendererType is null
            ? null
            : AccessTools.Method(
                EntityShapeRendererType,
                "loadModelMatrix",
                new[] { typeof(Entity), typeof(float), typeof(bool) });

    private static readonly MethodInfo? LoadPlayerModelMatrixMethod =
        PlayerShapeRendererType is null
            ? null
            : AccessTools.Method(
                PlayerShapeRendererType,
                "loadModelMatrixForPlayer",
                new[] { typeof(Entity), typeof(bool), typeof(float), typeof(bool) });

    private static readonly FieldInfo? ActiveOrRunningField =
        AccessTools.Field(typeof(AnimatorBase), "activeOrRunning");

    private static readonly HashSet<string> RendererStateFieldNames =
        new(StringComparer.Ordinal)
        {
            "color",
            "intoxIntensity",
            "bodyYawLerped",
            "smoothedBodyYaw",
            "renderMode",
            "isSpectator",
            "frostAlpha",
            "targetFrostAlpha",
            "frostAlphaAccum",
            "accum",
            "stepPitch",
            "prevY",
            "prevYAccum",
            "xangle",
            "yangle",
            "zangle",
            "stepingAccum",
            "fallingAccum",
            "targetSwivelRad",
            "nowSwivelRad",
            "prevAngleSwing",
            "prevPosXSwing",
            "prevPosZSwing",
            "swivelaccum",
            "LastJumpMs",
            "lightrgbs",
            "renderOffset",
            "OriginPos",
            "ModelMat",
            "pMatrixHandFov",
            "pMatrixNormalFov"
        };

    private static readonly HashSet<string> RendererResourceFieldNames =
        new(StringComparer.Ordinal)
        {
            "meshRefOpaque"
        };

    private static readonly ConcurrentDictionary<Type, FieldInfo[]> RendererStateFields = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> RendererResourceFields = new();

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly Dictionary<Entity, EntityState> states =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> loggedFailures = new(StringComparer.Ordinal);
    private bool disposed;

    internal DirectorOffscreenEntityAnimationScheduler(ICoreClientAPI capi, ClientMain game)
    {
        this.capi = capi;
        this.game = game;
    }

    internal IDisposable EnterView(
        DirectorOffscreenViewRequest view,
        long physicalFrameNumber,
        float frameDeltaTime)
    {
        if (disposed || !view.RenderEntities)
        {
            return EmptyScope.Instance;
        }

        float safeDeltaTime = float.IsFinite(frameDeltaTime)
            ? Math.Clamp(frameDeltaTime, 0f, 0.1f)
            : 0f;
        double maxDistanceSquared = ClientSettings.ViewDistance * ClientSettings.ViewDistance;
        DirectorReplayManager? replay = DirectorReplayRuntime.Manager;
        List<ViewBinding> bindings = new();

        foreach (EntityRenderer renderer in DirectorOffscreenEngineBindings.GetEntityRenderers(game))
        {
            Entity entity = renderer.entity;
            bool isLocalPlayer = ReferenceEquals(entity, game.EntityPlayer);
            double dx = entity.Pos.X - view.Pose.X;
            double dz = entity.Pos.Z - view.Pose.Z;
            if (entity.Pos.Dimension != view.Pose.Dimension
                || (isLocalPlayer && !view.RenderLocalPlayer)
                || replay?.IsLiveEntitySuppressed(entity) == true
                || replay?.ShouldRenderReplayEntity(entity.EntityId) == false
                || (!entity.AllowOutsideLoadedRange
                    && dx * dx + dz * dz > maxDistanceSquared))
            {
                continue;
            }

            // Recorded replay poses have their own deterministic clock. Apply
            // them before shadows, but do not independently advance them.
            replay?.PrepareReplayEntityAnimationForOffscreenRender(entity);
            bool replayDriven = replay?.ShouldDriveReplayAnimation(entity.EntityId) == true;

            RendererStateSnapshot primaryRendererState =
                RendererStateSnapshot.Capture(renderer);
            RendererStateSnapshot? primaryRendererResources = null;
            EntityTemporalSnapshot primaryEntityState = EntityTemporalSnapshot.Capture(entity);
            IAnimationManager? animationManager = replayDriven
                ? null
                : ResolveAnimationManager(entity);
            IAnimator? sourceAnimator = animationManager?.Animator;
            IHeadController? sourceHeadController = animationManager?.HeadController;
            ClientPlayer? remoteClientPlayer = !isLocalPlayer && entity is EntityPlayer player
                ? player.Player as ClientPlayer
                : null;
            EnumCameraMode? previousRemoteCameraOverride =
                remoteClientPlayer?.OverrideCameraMode;
            EntityState? state = null;
            bool animatorSwapped = false;

            try
            {
                // Some entities create their animator lazily during renderer
                // preparation. Capture the primary renderer state first so the
                // warm-up cannot leak into the main view.
                renderer.BeforeRender(0f);
                primaryRendererResources = RendererStateSnapshot.CaptureResources(renderer);
                replay?.PrepareReplayEntityAnimationForOffscreenRender(entity);
                animationManager = replayDriven ? null : ResolveAnimationManager(entity);
                sourceAnimator = animationManager?.Animator;
                sourceHeadController = animationManager?.HeadController;
                // The local player advances its third-person animator from
                // the first-person manager even while its body is hidden.
                // Treat that path as already advanced to avoid a second tick
                // in an offscreen camera.
                bool primaryAnimationAdvanced = isLocalPlayer
                    || entity.IsRendered
                    || entity.IsShadowRendered
                    || !entity.Alive;
                state = GetOrCreateState(
                    entity,
                    renderer,
                    animationManager,
                    sourceAnimator,
                    primaryRendererState,
                    primaryEntityState,
                    physicalFrameNumber);

                if (state.OffscreenAnimator is not null
                    && animationManager is not null
                    && sourceAnimator is not null)
                {
                    PrepareDetachedAnimator(
                        state,
                        animationManager,
                        sourceAnimator,
                        primaryAnimationAdvanced,
                        physicalFrameNumber,
                        safeDeltaTime);
                    animationManager.Animator = state.OffscreenAnimator;
                    // The primary head controller stores ElementPose references
                    // owned by the primary animator. Never let an offscreen
                    // renderer invoke it while the detached animator is bound.
                    animationManager.HeadController = null;
                    animatorSwapped = true;
                }
                else if (animationManager is not null && sourceAnimator is not null)
                {
                    // A custom animator or an incomplete shape can make a
                    // detached ClientAnimator impossible to construct. Keep
                    // the actor moving in that case: advance the shared
                    // animator once for this physical frame, then restore all
                    // renderer state as usual. This is preferable to leaving
                    // an offscreen-only actor on its last pose.
                    PrepareSharedAnimatorFallback(
                        state,
                        animationManager,
                        sourceAnimator,
                        sourceHeadController,
                        primaryAnimationAdvanced,
                        physicalFrameNumber,
                        safeDeltaTime);
                }

                if (remoteClientPlayer is not null)
                {
                    remoteClientPlayer.OverrideCameraMode = EnumCameraMode.ThirdPerson;
                }

                state.OffscreenRendererState.Apply(renderer);
                primaryRendererResources.Apply(renderer);
                state.OffscreenEntityState.Apply(entity);
                bool resetAfterGap = state.LastRendererAdvancedFrame == long.MinValue
                    || physicalFrameNumber < state.LastRendererAdvancedFrame
                    || physicalFrameNumber - state.LastRendererAdvancedFrame > 1;
                if (resetAfterGap)
                {
                    state.OffscreenRendererState = primaryRendererState;
                    state.OffscreenEntityState = primaryEntityState;
                    state.OffscreenRendererState.Apply(renderer);
                    primaryRendererResources.Apply(renderer);
                    state.OffscreenEntityState.Apply(entity);
                }

                if (state.LastRendererAdvancedFrame != physicalFrameNumber)
                {
                    float rendererDeltaTime = resetAfterGap && primaryAnimationAdvanced
                        ? 0f
                        : safeDeltaTime;
                    renderer.BeforeRender(rendererDeltaTime);
                    AdvanceModelMatrix(renderer, entity, isLocalPlayer, rendererDeltaTime);
                    state.OffscreenRendererState = RendererStateSnapshot.Capture(renderer);
                    state.OffscreenEntityState = EntityTemporalSnapshot.Capture(entity);
                    state.LastRendererAdvancedFrame = physicalFrameNumber;
                }
                else
                {
                    renderer.BeforeRender(0f);
                }

                state.LastSeenFrame = physicalFrameNumber;
                bindings.Add(new ViewBinding(
                    renderer,
                    entity,
                    primaryRendererState,
                    primaryRendererResources,
                    primaryEntityState,
                    animationManager,
                    sourceAnimator,
                    sourceHeadController,
                    animatorSwapped,
                    remoteClientPlayer,
                    previousRemoteCameraOverride));
            }
            catch (Exception exception)
            {
                RestoreImmediately(
                    renderer,
                    entity,
                    primaryRendererState,
                    primaryRendererResources,
                    primaryEntityState,
                    animationManager,
                    sourceAnimator,
                    sourceHeadController,
                    animatorSwapped,
                    remoteClientPlayer,
                    previousRemoteCameraOverride);
                LogFailureOnce(entity, renderer, exception);
            }
        }

        PruneStates(physicalFrameNumber);
        return bindings.Count == 0 ? EmptyScope.Instance : new ViewScope(bindings);
    }

    private EntityState GetOrCreateState(
        Entity entity,
        EntityRenderer renderer,
        IAnimationManager? animationManager,
        IAnimator? sourceAnimator,
        RendererStateSnapshot primaryRendererState,
        EntityTemporalSnapshot primaryEntityState,
        long physicalFrameNumber)
    {
        if (states.TryGetValue(entity, out EntityState? existing)
            && ReferenceEquals(existing.Renderer, renderer)
            && ReferenceEquals(existing.AnimationManager, animationManager)
            && ReferenceEquals(existing.SourceAnimator, sourceAnimator))
        {
            return existing;
        }

        ClientAnimator? detached = null;
        if (sourceAnimator is ClientAnimator clientAnimator)
        {
            try
            {
                detached = CreateDetachedAnimator(entity, clientAnimator, animationManager);
            }
            catch (Exception exception)
            {
                capi.Logger.Debug(
                    "VS Director: detached animator setup failed for entity {0}; using shared animator fallback: {1}",
                    entity.EntityId,
                    exception.GetBaseException().Message);
            }
        }
        EntityState state = new(
            renderer,
            animationManager,
            sourceAnimator,
            detached,
            primaryRendererState,
            primaryEntityState)
        {
            LastSeenFrame = physicalFrameNumber
        };
        state.ResetActiveAnimations(animationManager?.ActiveAnimationsByAnimCode);
        states[entity] = state;
        return state;
    }

    private static void PrepareSharedAnimatorFallback(
        EntityState state,
        IAnimationManager animationManager,
        IAnimator sourceAnimator,
        IHeadController? sourceHeadController,
        bool primaryAnimationAdvanced,
        long physicalFrameNumber,
        float frameDeltaTime)
    {
        if (state.LastAnimationAdvancedFrame == physicalFrameNumber)
        {
            return;
        }

        float delta = primaryAnimationAdvanced ? 0f : frameDeltaTime;
        try
        {
            sourceHeadController?.OnFrame(delta);
            sourceAnimator.OnFrame(animationManager.ActiveAnimationsByAnimCode, delta);
        }
        catch
        {
            // The renderer still gets a valid primary pose even when a custom
            // animation manager rejects an offscreen update.
        }
        state.LastAnimationAdvancedFrame = physicalFrameNumber;
    }

    private static ClientAnimator? CreateDetachedAnimator(
        Entity entity,
        ClientAnimator source,
        IAnimationManager? animationManager)
    {
        if (animationManager is null
            || source.RootElements is null
            || source.jointsById is null)
        {
            return null;
        }

        Animation[] animations = source.Animations
            .Where(animation => animation?.Animation is not null)
            .Select(animation => animation.Animation)
            .ToArray();
        ClientAnimator detached = new(
            () => entity is EntityAgent agent
                ? (double)agent.Controls.MovespeedMultiplier * agent.GetWalkSpeedMultiplier()
                : 1d,
            animations,
            source.RootElements,
            source.jointsById,
            onAnimationStoppedListener: null,
            onShouldPlaySoundListener: null)
        {
            CalculateMatrices = source.CalculateMatrices,
            entityForLogging = entity
        };
        SynchronizeAnimatorFromSource(
            source,
            detached,
            CloneActiveAnimations(animationManager.ActiveAnimationsByAnimCode));
        return detached;
    }

    private static void PrepareDetachedAnimator(
        EntityState state,
        IAnimationManager animationManager,
        IAnimator sourceAnimator,
        bool primaryAnimationAdvanced,
        long physicalFrameNumber,
        float frameDeltaTime)
    {
        ClientAnimator detached = state.OffscreenAnimator!;
        if (state.LastAnimationAdvancedFrame == physicalFrameNumber)
        {
            // Every view in this physical frame consumes the same detached
            // matrices. Per-view camera work must never progress animations.
            return;
        }
        bool resetAfterGap = state.LastAnimationAdvancedFrame == long.MinValue
            || physicalFrameNumber < state.LastAnimationAdvancedFrame
            || physicalFrameNumber - state.LastAnimationAdvancedFrame > 1;
        CopyPoseOffsets(sourceAnimator as ClientAnimator, detached);

        if (resetAfterGap)
        {
            state.ResetActiveAnimations(animationManager.ActiveAnimationsByAnimCode);
            if (sourceAnimator is ClientAnimator sourceClient)
            {
                SynchronizeAnimatorFromSource(
                    sourceClient,
                    detached,
                    state.OffscreenActiveAnimations);
                CopyPoseOffsets(sourceClient, detached);
            }
            float initialDeltaTime = primaryAnimationAdvanced ? 0f : frameDeltaTime;
            UpdateActiveMetadata(detached, state.OffscreenActiveAnimations);
            detached.OnFrame(state.OffscreenActiveAnimations, initialDeltaTime);
            state.RecordCompletedAnimations(animationManager.ActiveAnimationsByAnimCode);
            state.LastAnimationAdvancedFrame = physicalFrameNumber;
            return;
        }

        state.SynchronizeActiveAnimations(animationManager.ActiveAnimationsByAnimCode);
        UpdateActiveMetadata(detached, state.OffscreenActiveAnimations);
        detached.OnFrame(state.OffscreenActiveAnimations, frameDeltaTime);
        state.RecordCompletedAnimations(animationManager.ActiveAnimationsByAnimCode);
        state.LastAnimationAdvancedFrame = physicalFrameNumber;
    }

    private static void SynchronizeAnimatorFromSource(
        ClientAnimator source,
        ClientAnimator destination,
        IReadOnlyDictionary<string, AnimationMetaData> activeAnimations)
    {
        Dictionary<string, AnimationMetaData> active =
            CloneActiveAnimations(activeAnimations);

        // Register active animations once so the detached animator allocates
        // element weights and its private running list.
        destination.OnFrame(active, 0f);
        List<RunningAnimation>? activeOrRunning =
            ActiveOrRunningField?.GetValue(destination) as List<RunningAnimation>;
        activeOrRunning?.Clear();

        Dictionary<string, RunningAnimation> destinationByCode =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (RunningAnimation animation in destination.Animations)
        {
            if (animation?.Animation?.Code is { } code)
            {
                destinationByCode[code] = animation;
            }
        }
        foreach (RunningAnimation sourceAnimation in source.Animations)
        {
            if (sourceAnimation?.Animation?.Code is not { } code
                || !destinationByCode.TryGetValue(code, out RunningAnimation? target))
            {
                continue;
            }

            CopyRunningAnimation(sourceAnimation, target, activeAnimations);
            if (target.Running || target.Active)
            {
                target.LoadWeights(destination.RootElements);
                if (activeOrRunning is not null && !activeOrRunning.Contains(target))
                {
                    activeOrRunning.Add(target);
                }
            }
        }

        CopyPoseOffsets(source, destination);
        destination.OnFrame(CloneActiveAnimations(activeAnimations), 0f);
    }

    private static void CopyRunningAnimation(
        RunningAnimation source,
        RunningAnimation destination,
        IReadOnlyDictionary<string, AnimationMetaData> activeAnimations)
    {
        AnimationMetaData? metadata = source.meta;
        if (metadata is null
            && source.Animation?.Code is { } animationCode
            && activeAnimations.TryGetValue(animationCode, out AnimationMetaData? activeMetadata))
        {
            metadata = activeMetadata;
        }
        destination.meta = metadata is null
            ? new AnimationMetaData
            {
                Code = source.Animation?.Code ?? string.Empty,
                Animation = source.Animation?.Code ?? string.Empty
            }
            : CloneAnimationMetadata(metadata);
        destination.CurrentFrame = source.CurrentFrame;
        destination.Active = source.Active;
        destination.Running = source.Running;
        destination.Iterations = source.Iterations;
        destination.NowPlaying = source.NowPlaying;
        destination.ShouldRewind = source.ShouldRewind;
        destination.ShouldPlayTillEnd = source.ShouldPlayTillEnd;
        destination.EasingFactor = source.EasingFactor;
        destination.BlendedWeight = source.BlendedWeight;
        destination.SoundPlayedAtIteration = source.SoundPlayedAtIteration?.ToArray();
    }

    private static Dictionary<string, AnimationMetaData> CloneActiveAnimations(
        IReadOnlyDictionary<string, AnimationMetaData> source)
    {
        Dictionary<string, AnimationMetaData> clone =
            new(source.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string code, AnimationMetaData metadata) in source)
        {
            if (metadata is not null)
            {
                clone[code] = CloneAnimationMetadata(metadata);
            }
        }
        return clone;
    }

    private static AnimationMetaData CloneAnimationMetadata(AnimationMetaData source)
    {
        AnimationMetaData clone = source.Clone();
        clone.ElementWeight = source.ElementWeight is null
            ? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, float>(source.ElementWeight, StringComparer.OrdinalIgnoreCase);
        clone.ElementBlendMode = source.ElementBlendMode is null
            ? new Dictionary<string, EnumAnimationBlendMode>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, EnumAnimationBlendMode>(
                source.ElementBlendMode,
                StringComparer.OrdinalIgnoreCase);
        return clone;
    }

    private static void UpdateActiveMetadata(
        ClientAnimator animator,
        IReadOnlyDictionary<string, AnimationMetaData> active)
    {
        foreach ((string code, AnimationMetaData metadata) in active)
        {
            RunningAnimation? running = animator.GetAnimationState(code);
            if (running?.Active == true)
            {
                running.meta = metadata;
            }
        }
    }

    private static void CopyPoseOffsets(ClientAnimator? source, ClientAnimator destination)
    {
        if (source?.RootPoses is null || destination.RootPoses is null)
        {
            return;
        }
        CopyPoseOffsets(source.RootPoses, destination.RootPoses);
    }

    private static void CopyPoseOffsets(
        IReadOnlyList<ElementPose> source,
        IReadOnlyList<ElementPose> destination)
    {
        int count = Math.Min(source.Count, destination.Count);
        for (int index = 0; index < count; index++)
        {
            ElementPose sourcePose = source[index];
            ElementPose targetPose = destination[index];
            targetPose.degOffX = sourcePose.degOffX;
            targetPose.degOffY = sourcePose.degOffY;
            targetPose.degOffZ = sourcePose.degOffZ;
            if (sourcePose.ChildElementPoses is not null
                && targetPose.ChildElementPoses is not null)
            {
                CopyPoseOffsets(sourcePose.ChildElementPoses, targetPose.ChildElementPoses);
            }
        }
    }

    private static IAnimationManager? ResolveAnimationManager(Entity entity)
        => entity is EntityPlayer player
            ? player.TpAnimManager
            : entity.AnimManager;

    private static void AdvanceModelMatrix(
        EntityRenderer renderer,
        Entity entity,
        bool isLocalPlayer,
        float deltaTime)
    {
        if (PlayerShapeRendererType?.IsInstanceOfType(renderer) == true
            && LoadPlayerModelMatrixMethod is not null)
        {
            LoadPlayerModelMatrixMethod.Invoke(
                renderer,
                new object[] { entity, isLocalPlayer, deltaTime, false });
            return;
        }
        if (EntityShapeRendererType?.IsInstanceOfType(renderer) == true
            && LoadEntityModelMatrixMethod is not null)
        {
            LoadEntityModelMatrixMethod.Invoke(
                renderer,
                new object[] { entity, deltaTime, false });
        }
    }

    private void PruneStates(long physicalFrameNumber)
    {
        foreach (Entity stale in states
            .Where(pair => physicalFrameNumber - pair.Value.LastSeenFrame > 120)
            .Select(pair => pair.Key)
            .ToArray())
        {
            states.Remove(stale);
        }
    }

    private void LogFailureOnce(
        Entity entity,
        EntityRenderer renderer,
        Exception exception)
    {
        Exception root = exception.GetBaseException();
        string key = renderer.GetType().FullName + "|" + root.GetType().FullName + "|" + root.Message;
        if (!loggedFailures.Add(key))
        {
            return;
        }
        capi.Logger.Warning(
            "VS Director: detached offscreen animation failed for entity {0} ({1}): {2}",
            entity.EntityId,
            renderer.GetType().FullName,
            root.Message);
    }

    private static void RestoreImmediately(
        EntityRenderer renderer,
        Entity entity,
        RendererStateSnapshot primaryRendererState,
        RendererStateSnapshot? primaryRendererResources,
        EntityTemporalSnapshot primaryEntityState,
        IAnimationManager? animationManager,
        IAnimator? sourceAnimator,
        IHeadController? sourceHeadController,
        bool animatorSwapped,
        ClientPlayer? remoteClientPlayer,
        EnumCameraMode? previousRemoteCameraOverride)
    {
        if (remoteClientPlayer is not null)
        {
            remoteClientPlayer.OverrideCameraMode = previousRemoteCameraOverride;
        }
        if (animatorSwapped && animationManager is not null)
        {
            // The scope owns these temporary substitutions. Restore the exact
            // objects captured on entry even if renderer code assigned another
            // value while the detached animator was active.
            if (sourceAnimator is not null)
            {
                animationManager.Animator = sourceAnimator;
            }
            animationManager.HeadController = sourceHeadController;
        }
        primaryEntityState.Apply(entity);
        primaryRendererState.Apply(renderer);
        primaryRendererResources?.Apply(renderer);
    }

    public void Dispose()
    {
        disposed = true;
        states.Clear();
        loggedFailures.Clear();
    }

    private sealed class EntityState
    {
        private readonly Dictionary<string, AnimationMetaData> observedSourceMetadata =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> completedOffscreenAnimations =
            new(StringComparer.OrdinalIgnoreCase);

        internal EntityRenderer Renderer { get; }

        internal IAnimationManager? AnimationManager { get; }

        internal IAnimator? SourceAnimator { get; }

        internal ClientAnimator? OffscreenAnimator { get; }

        internal Dictionary<string, AnimationMetaData> OffscreenActiveAnimations { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        internal RendererStateSnapshot OffscreenRendererState { get; set; }

        internal EntityTemporalSnapshot OffscreenEntityState { get; set; }

        internal long LastAnimationAdvancedFrame { get; set; } = long.MinValue;

        internal long LastRendererAdvancedFrame { get; set; } = long.MinValue;

        internal long LastSeenFrame { get; set; } = long.MinValue;

        internal EntityState(
            EntityRenderer renderer,
            IAnimationManager? animationManager,
            IAnimator? sourceAnimator,
            ClientAnimator? offscreenAnimator,
            RendererStateSnapshot offscreenRendererState,
            EntityTemporalSnapshot offscreenEntityState)
        {
            Renderer = renderer;
            AnimationManager = animationManager;
            SourceAnimator = sourceAnimator;
            OffscreenAnimator = offscreenAnimator;
            OffscreenRendererState = offscreenRendererState;
            OffscreenEntityState = offscreenEntityState;
        }

        internal void ResetActiveAnimations(
            IReadOnlyDictionary<string, AnimationMetaData>? source)
        {
            observedSourceMetadata.Clear();
            completedOffscreenAnimations.Clear();
            OffscreenActiveAnimations.Clear();
            if (source is null)
            {
                return;
            }
            foreach ((string code, AnimationMetaData metadata) in source)
            {
                if (metadata is null)
                {
                    continue;
                }
                observedSourceMetadata[code] = metadata;
                OffscreenActiveAnimations[code] = metadata.Clone();
            }
        }

        internal void SynchronizeActiveAnimations(
            IReadOnlyDictionary<string, AnimationMetaData> source)
        {
            foreach (string removed in observedSourceMetadata.Keys
                .Where(code => !source.ContainsKey(code))
                .ToArray())
            {
                observedSourceMetadata.Remove(removed);
                completedOffscreenAnimations.Remove(removed);
                OffscreenActiveAnimations.Remove(removed);
            }

            foreach ((string code, AnimationMetaData metadata) in source)
            {
                if (metadata is null)
                {
                    continue;
                }

                bool restarted = !observedSourceMetadata.TryGetValue(
                    code,
                    out AnimationMetaData? observed)
                    || !ReferenceEquals(observed, metadata);
                observedSourceMetadata[code] = metadata;
                if (restarted)
                {
                    completedOffscreenAnimations.Remove(code);
                    OffscreenActiveAnimations[code] = metadata.Clone();
                    continue;
                }
                if (completedOffscreenAnimations.Contains(code))
                {
                    continue;
                }

                float consumedStartFrame = OffscreenActiveAnimations.TryGetValue(
                    code,
                    out AnimationMetaData? current)
                    ? current.StartFrameOnce
                    : metadata.StartFrameOnce;
                AnimationMetaData updated = metadata.Clone();
                updated.StartFrameOnce = consumedStartFrame;
                OffscreenActiveAnimations[code] = updated;
            }
        }

        internal void RecordCompletedAnimations(
            IReadOnlyDictionary<string, AnimationMetaData> source)
        {
            foreach (string code in source.Keys)
            {
                if (!OffscreenActiveAnimations.ContainsKey(code))
                {
                    completedOffscreenAnimations.Add(code);
                }
            }
        }
    }

    private sealed class ViewBinding
    {
        private readonly EntityRenderer renderer;
        private readonly Entity entity;
        private readonly RendererStateSnapshot primaryRendererState;
        private readonly RendererStateSnapshot primaryRendererResources;
        private readonly EntityTemporalSnapshot primaryEntityState;
        private readonly IAnimationManager? animationManager;
        private readonly IAnimator? sourceAnimator;
        private readonly IHeadController? sourceHeadController;
        private readonly bool animatorSwapped;
        private readonly ClientPlayer? remoteClientPlayer;
        private readonly EnumCameraMode? previousRemoteCameraOverride;

        internal ViewBinding(
            EntityRenderer renderer,
            Entity entity,
            RendererStateSnapshot primaryRendererState,
            RendererStateSnapshot primaryRendererResources,
            EntityTemporalSnapshot primaryEntityState,
            IAnimationManager? animationManager,
            IAnimator? sourceAnimator,
            IHeadController? sourceHeadController,
            bool animatorSwapped,
            ClientPlayer? remoteClientPlayer,
            EnumCameraMode? previousRemoteCameraOverride)
        {
            this.renderer = renderer;
            this.entity = entity;
            this.primaryRendererState = primaryRendererState;
            this.primaryRendererResources = primaryRendererResources;
            this.primaryEntityState = primaryEntityState;
            this.animationManager = animationManager;
            this.sourceAnimator = sourceAnimator;
            this.sourceHeadController = sourceHeadController;
            this.animatorSwapped = animatorSwapped;
            this.remoteClientPlayer = remoteClientPlayer;
            this.previousRemoteCameraOverride = previousRemoteCameraOverride;
        }

        internal void Restore()
            => RestoreImmediately(
                renderer,
                entity,
                primaryRendererState,
                primaryRendererResources,
                primaryEntityState,
                animationManager,
                sourceAnimator,
                sourceHeadController,
                animatorSwapped,
                remoteClientPlayer,
                previousRemoteCameraOverride);
    }

    private sealed class ViewScope : IDisposable
    {
        private List<ViewBinding>? bindings;

        internal ViewScope(List<ViewBinding> bindings)
        {
            this.bindings = bindings;
        }

        public void Dispose()
        {
            List<ViewBinding>? current = Interlocked.Exchange(ref bindings, null);
            if (current is null)
            {
                return;
            }
            for (int index = current.Count - 1; index >= 0; index--)
            {
                current[index].Restore();
            }
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();

        public void Dispose()
        {
        }
    }

    private readonly record struct EntityTemporalSnapshot(
        float? SidewaysSwivelAngle,
        bool? SelfNowShadowPass)
    {
        internal static EntityTemporalSnapshot Capture(Entity entity)
            => new(
                entity is EntityAgent agent ? agent.sidewaysSwivelAngle : null,
                entity is EntityPlayer player ? player.selfNowShadowPass : null);

        internal void Apply(Entity entity)
        {
            if (SidewaysSwivelAngle is float angle && entity is EntityAgent agent)
            {
                agent.sidewaysSwivelAngle = angle;
            }
            if (SelfNowShadowPass is bool shadowPass && entity is EntityPlayer player)
            {
                player.selfNowShadowPass = shadowPass;
            }
        }
    }

    private sealed class RendererStateSnapshot
    {
        private readonly FieldValue[] values;
        private readonly bool resources;

        private RendererStateSnapshot(FieldValue[] values, bool resources)
        {
            this.values = values;
            this.resources = resources;
        }

        internal static RendererStateSnapshot Capture(EntityRenderer renderer)
        {
            FieldInfo[] fields = RendererStateFields.GetOrAdd(
                renderer.GetType(),
                static type => GetRendererFields(type, RendererStateFieldNames));
            return Capture(renderer, fields, resources: false);
        }

        internal static RendererStateSnapshot CaptureResources(EntityRenderer renderer)
        {
            FieldInfo[] fields = RendererResourceFields.GetOrAdd(
                renderer.GetType(),
                static type => GetRendererFields(type, RendererResourceFieldNames));
            return Capture(renderer, fields, resources: true);
        }

        private static RendererStateSnapshot Capture(
            EntityRenderer renderer,
            IReadOnlyList<FieldInfo> fields,
            bool resources)
        {
            List<FieldValue> values = new(fields.Count);
            foreach (FieldInfo field in fields)
            {
                try
                {
                    values.Add(new FieldValue(field, CloneValue(field.GetValue(renderer))));
                }
                catch
                {
                }
            }
            return new RendererStateSnapshot(values.ToArray(), resources);
        }

        internal void Apply(EntityRenderer renderer)
        {
            foreach (FieldValue value in values)
            {
                try
                {
                    if (resources && !IsUsableResource(value.Value))
                    {
                        continue;
                    }
                    value.Field.SetValue(renderer, CloneValue(value.Value));
                }
                catch
                {
                }
            }
        }

        private static bool IsUsableResource(object? value)
            => value switch
            {
                MultiTextureMeshRef mesh => !mesh.Disposed && mesh.Initialized,
                MeshRef mesh => !mesh.Disposed && mesh.Initialized,
                _ => true
            };

        private static FieldInfo[] GetRendererFields(
            Type rendererType,
            IReadOnlySet<string> fieldNames)
        {
            List<FieldInfo> fields = new();
            for (Type? current = rendererType;
                current is not null && typeof(EntityRenderer).IsAssignableFrom(current);
                current = current.BaseType)
            {
                fields.AddRange(current
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(field => !field.IsInitOnly
                        && fieldNames.Contains(field.Name)));
            }
            return fields.ToArray();
        }

        private static object? CloneValue(object? value)
            => value switch
            {
                float[] array => array.ToArray(),
                double[] array => array.ToArray(),
                int[] array => array.ToArray(),
                Matrixf matrix => new Matrixf(matrix.Values),
                Vec3f vector => new Vec3f(vector.X, vector.Y, vector.Z),
                Vec4f vector => new Vec4f(vector.X, vector.Y, vector.Z, vector.W),
                Vec3d vector => new Vec3d(vector.X, vector.Y, vector.Z),
                _ => value
            };

        private readonly record struct FieldValue(FieldInfo Field, object? Value);
    }
}
