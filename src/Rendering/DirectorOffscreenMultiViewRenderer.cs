using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Replay;

namespace VSDirector.Rendering;

public sealed class DirectorOffscreenMultiViewRenderer : IDisposable
{
    public const int MaximumViews = 8;
    public const int MaximumDimension = 8192;

    private const DirectorOffscreenCapabilities ImplementedCapabilities =
        DirectorOffscreenCapabilities.IndependentColorDepthTarget
        | DirectorOffscreenCapabilities.IndependentCameraPose
        | DirectorOffscreenCapabilities.SinglePhysicalTick
        | DirectorOffscreenCapabilities.OpaqueTerrain
        | DirectorOffscreenCapabilities.OpaqueEntities
        | DirectorOffscreenCapabilities.IsolatedOpaqueParticles
        | DirectorOffscreenCapabilities.TransparentOit
        | DirectorOffscreenCapabilities.IndependentShadows
        | DirectorOffscreenCapabilities.IndependentPostProcessing;

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly object sync = new();
    private readonly Dictionary<Guid, ViewTarget> targets = new();
    private readonly DirectorIndependentPlayerRenderer independentPlayerRenderer;
    private readonly DirectorOffscreenEntityAnimationScheduler entityAnimationScheduler;
    private readonly DirectorOffscreenLocalPlayerAnimationContext localPlayerAnimation;
    private List<DirectorOffscreenViewRequest> views = new();
    private List<IDirectorOffscreenFrameSink> sinks = new();
    private List<IDirectorOffscreenPassRenderer> contributors = new();
    private long physicalFrameNumber;
    private DirectorOpenGlFramebufferApi? framebufferApi;
    private bool enabled;
    private bool rendering;
    private bool disposed;

    public bool UseIndependentPlayerRenderer { get; set; }

    public bool Enabled
    {
        get
        {
            lock (sync)
            {
                return enabled;
            }
        }
        set
        {
            lock (sync)
            {
                enabled = value;
            }
        }
    }

    public DirectorOffscreenCapabilities Capabilities => ImplementedCapabilities;

    public int ActiveViewCount
    {
        get
        {
            lock (sync)
            {
                return views.Count;
            }
        }
    }

    public event Action<Exception>? RenderFailed;

    public DirectorOffscreenMultiViewRenderer(ICoreClientAPI capi)
    {
        this.capi = capi ?? throw new ArgumentNullException(nameof(capi));
        game = capi.World as ClientMain
            ?? throw new InvalidOperationException("Offscreen rendering requires the Vintage Story desktop client.");
        if (!DirectorOffscreenEngineBindings.IsValid)
        {
            throw new NotSupportedException(
                "Vintage Story offscreen render bindings are unavailable: "
                + DirectorOffscreenEngineBindings.Validate());
        }

        independentPlayerRenderer = new DirectorIndependentPlayerRenderer(capi, game);
        entityAnimationScheduler = new DirectorOffscreenEntityAnimationScheduler(capi, game);
        localPlayerAnimation = new DirectorOffscreenLocalPlayerAnimationContext(capi, game);
        DirectorOffscreenRenderRuntime.Renderer = this;
    }

    public void SetViews(IEnumerable<DirectorOffscreenViewRequest> requestedViews)
    {
        ArgumentNullException.ThrowIfNull(requestedViews);
        List<DirectorOffscreenViewRequest> normalized = requestedViews.Select(NormalizeView).ToList();
        if (normalized.Count > MaximumViews)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedViews),
                $"At most {MaximumViews} simultaneous offscreen views are supported.");
        }
        if (normalized.Select(view => view.ViewId).Distinct().Count() != normalized.Count)
        {
            throw new ArgumentException("Every offscreen view must have a unique id.", nameof(requestedViews));
        }

        lock (sync)
        {
            ThrowIfDisposed();
            views = normalized;
        }
    }

    public void AddSink(IDirectorOffscreenFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (sync)
        {
            ThrowIfDisposed();
            if (!sinks.Contains(sink))
            {
                sinks.Add(sink);
            }
        }
    }

    public void RemoveSink(IDirectorOffscreenFrameSink sink)
    {
        lock (sync)
        {
            sinks.Remove(sink);
        }
    }

    public void AddPassRenderer(IDirectorOffscreenPassRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        lock (sync)
        {
            ThrowIfDisposed();
            if (!contributors.Contains(renderer))
            {
                contributors.Add(renderer);
                contributors = contributors.OrderBy(item => item.RenderOrder).ToList();
            }
        }
    }

    public void RemovePassRenderer(IDirectorOffscreenPassRenderer renderer)
    {
        lock (sync)
        {
            contributors.Remove(renderer);
        }
    }

    /// <summary>
    /// Clears view definitions and releases their GPU targets on the calling
    /// render thread. Call after disabling the renderer and stopping sinks.
    /// </summary>
    public void ReleaseViews()
    {
        lock (sync)
        {
            ThrowIfDisposed();
            if (rendering)
            {
                throw new InvalidOperationException("Cannot release offscreen views during a render pass.");
            }
            views.Clear();
        }

        foreach (ViewTarget target in targets.Values)
        {
            target.Dispose();
        }
        targets.Clear();
    }

    internal void RenderAtFrameBoundary(float deltaTime)
    {
        float frameDeltaTime = float.IsFinite(deltaTime)
            ? Math.Clamp(deltaTime, 0f, 0.1f)
            : 0f;
        List<DirectorOffscreenViewRequest> frameViews;
        List<IDirectorOffscreenFrameSink> frameSinks;
        List<IDirectorOffscreenPassRenderer> frameContributors;
        long frameNumber;
        lock (sync)
        {
            physicalFrameNumber++;
            frameNumber = physicalFrameNumber;
            if (disposed || !enabled || rendering)
            {
                return;
            }

            rendering = true;
            frameViews = new List<DirectorOffscreenViewRequest>(views);
            frameSinks = new List<IDirectorOffscreenFrameSink>(sinks);
            frameContributors = new List<IDirectorOffscreenPassRenderer>(contributors);
        }

        try
        {
            ReconcileTargets(frameViews);
            if (frameViews.Count == 0)
            {
                return;
            }
            if (UseIndependentPlayerRenderer
                && frameViews.Any(view => view.RenderLocalPlayer))
            {
                independentPlayerRenderer.Prepare(frameNumber);
            }
            DirectorOffscreenEngineState baseline = DirectorOffscreenEngineState.Capture(capi, game);
            float[] clearColor = ClearColor();
            foreach (DirectorOffscreenViewRequest view in frameViews)
            {
                try
                {
                    baseline.Restore();
                    RenderView(
                        view,
                        targets[view.ViewId],
                        frameNumber,
                        frameDeltaTime,
                        frameSinks,
                        frameContributors,
                        clearColor);
                }
                catch (Exception exception)
                {
                    capi.Logger.Error(
                        "VS Director: offscreen view '{0}' failed: {1}",
                        view.Name,
                        exception);
                    RenderFailed?.Invoke(exception);
                }
                finally
                {
                    baseline.Restore();
                }
            }
        }
        catch (Exception exception)
        {
            capi.Logger.Error("VS Director: offscreen multi-view frame failed: {0}", exception);
            RenderFailed?.Invoke(exception);
        }
        finally
        {
            lock (sync)
            {
                rendering = false;
            }
        }
    }

    private void RenderView(
        DirectorOffscreenViewRequest view,
        ViewTarget target,
        long frameNumber,
        float frameDeltaTime,
        IReadOnlyList<IDirectorOffscreenFrameSink> frameSinks,
        IReadOnlyList<IDirectorOffscreenPassRenderer> frameContributors,
        float[] clearColor)
    {
        long captureMilliseconds = view.CaptureClockMilliseconds ?? game.ElapsedMilliseconds;
        using IDisposable scope = DirectorOffscreenRenderRuntime.Enter(view, frameNumber);
        DirectorOffscreenEngineState state = DirectorOffscreenEngineState.Capture(capi, game);
        FrameBufferRef primary = game.Platform.FrameBuffers[(int)EnumFrameBuffer.Primary];
        state.Apply(view, primary, clearColor);
        using IDisposable? playerRendererScope = UseIndependentPlayerRenderer && view.RenderLocalPlayer
            ? independentPlayerRenderer.Enter()
            : null;
        using IDisposable entityAnimationScope = entityAnimationScheduler.EnterView(
            view,
            frameNumber,
            frameDeltaTime);
        using IDisposable? localPlayerAnimationScope = localPlayerAnimation.Enter(
            view,
            frameNumber,
            frameDeltaTime);

        DirectorOffscreenEngineBindings.RenderLiquidDepth(game);
        RenderShadows();

        // Shadow passes leave their own frustum and matrices active. Reapply the
        // director camera and clear only the primary color/depth target.
        state.Apply(view, primary, clearColor);
        DirectorOffscreenPointLights.Apply(capi, game, view.Pose);
        PrepareEntityVisibility(view);

        game.TriggerRenderStage(EnumRenderStage.Opaque, 0f);
        RenderContributors(
            DirectorOffscreenPass.OpaqueWorld,
            view,
            target,
            frameNumber,
            captureMilliseconds,
            frameContributors);

        if (game.doTransparentRenderPass)
        {
            game.Platform.LoadFrameBuffer(EnumFrameBuffer.Transparent);
            game.Platform.ClearFrameBuffer(EnumFrameBuffer.Transparent);
            game.TriggerRenderStage(EnumRenderStage.OIT, 0f);
            game.Platform.UnloadFrameBuffer(EnumFrameBuffer.Transparent);
            game.Platform.MergeTransparentRenderPass();
        }
        game.Platform.GlDepthMask(flag: true);
        game.Platform.GlEnableDepthTest();
        game.Platform.GlCullFaceBack();
        game.Platform.GlEnableCullFace();
        game.TriggerRenderStage(EnumRenderStage.AfterOIT, 0f);

        RenderContributors(
            DirectorOffscreenPass.OpaqueParticles,
            view,
            target,
            frameNumber,
            captureMilliseconds,
            frameContributors);

        game.Platform.RenderPostprocessingEffects(game.CurrentProjectionMatrix);
        game.RenderAfterPostProcessing(0f);
        game.Platform.RenderFinalComposition();

        EnsureFramebufferApi().BlitColor(primary, target.FrameBuffer);
        game.Platform.CurrentFrameBuffer = target.FrameBuffer;
        game.Platform.GlViewport(0, 0, target.FrameBuffer.Width, target.FrameBuffer.Height);

        DirectorOffscreenFrame frame = new(
            view.ViewId,
            view.Name,
            frameNumber,
            captureMilliseconds,
            target.FrameBuffer.Width,
            target.FrameBuffer.Height,
            target.FrameBuffer.FboId,
            target.FrameBuffer.ColorTextureIds[0],
            target.FrameBuffer.DepthTextureId,
            ImplementedCapabilities);
        foreach (IDirectorOffscreenFrameSink sink in frameSinks)
        {
            try
            {
                sink.OnFrame(in frame);
            }
            catch (Exception exception)
            {
                capi.Logger.Warning(
                    "VS Director: offscreen frame sink {0} failed: {1}",
                    sink.GetType().FullName,
                    exception.Message);
            }
        }
    }

    private void RenderShadows()
    {
        if (game.AmbientManager.ShadowQuality <= 0
            || capi.Render.ShaderUniforms.DropShadowIntensity <= 0.01f)
        {
            return;
        }

        game.TriggerRenderStage(EnumRenderStage.ShadowFar, 0f);
        game.TriggerRenderStage(EnumRenderStage.ShadowFarDone, 0f);
        if (game.AmbientManager.ShadowQuality > 1)
        {
            game.TriggerRenderStage(EnumRenderStage.ShadowNear, 0f);
            game.TriggerRenderStage(EnumRenderStage.ShadowNearDone, 0f);
        }
    }

    private DirectorOpenGlFramebufferApi EnsureFramebufferApi()
    {
        if (framebufferApi is not null)
        {
            return framebufferApi;
        }
        if (!DirectorOpenGlFramebufferApi.TryCreate(out framebufferApi, out string error)
            || framebufferApi is null)
        {
            throw new NotSupportedException("OpenGL framebuffer blit is unavailable: " + error);
        }
        return framebufferApi;
    }

    private void PrepareEntityVisibility(DirectorOffscreenViewRequest view)
    {
        double maxDistanceSquared = ClientSettings.ViewDistance * ClientSettings.ViewDistance;
        double absoluteY = view.Pose.Y + view.Pose.Dimension * 32768d;
        DirectorReplayManager? replay = DirectorReplayRuntime.Manager;
        foreach (EntityRenderer renderer in DirectorOffscreenEngineBindings.GetEntityRenderers(game))
        {
            Entity entity = renderer.entity;
            double dx = entity.Pos.X - view.Pose.X;
            double dy = entity.Pos.InternalY - absoluteY;
            double dz = entity.Pos.Z - view.Pose.Z;
            bool isLocalPlayer = ReferenceEquals(entity, game.EntityPlayer);
            bool visible = view.RenderEntities
                && entity.Pos.Dimension == view.Pose.Dimension
                && (!isLocalPlayer || view.RenderLocalPlayer)
                && (replay is null
                    || (!replay.IsLiveEntitySuppressed(entity)
                        && replay.ShouldRenderReplayEntity(entity.EntityId)))
                && (entity.AllowOutsideLoadedRange || dx * dx + dz * dz <= maxDistanceSquared)
                && capi.Render.DefaultFrustumCuller.SphereInFrustum(
                    entity.Pos.X,
                    entity.Pos.InternalY,
                    entity.Pos.Z,
                    Math.Max(entity.FrustumSphereRadius, Math.Abs(dy) < 1d ? 1d : entity.FrustumSphereRadius));
            entity.IsRendered = visible;
            if (visible)
            {
                replay?.PrepareReplayEntityAnimationForOffscreenRender(entity);
                renderer.BeforeRender(0f);
            }
        }
    }

    private static void RenderContributors(
        DirectorOffscreenPass pass,
        DirectorOffscreenViewRequest view,
        ViewTarget target,
        long frameNumber,
        long worldElapsedMilliseconds,
        IReadOnlyList<IDirectorOffscreenPassRenderer> contributors)
    {
        DirectorOffscreenPassContext context = new(
            view,
            frameNumber,
            worldElapsedMilliseconds,
            target.FrameBuffer.ColorTextureIds[0],
            target.FrameBuffer.DepthTextureId);
        foreach (IDirectorOffscreenPassRenderer contributor in contributors)
        {
            if (contributor.Pass == pass)
            {
                contributor.Render(in context);
            }
        }
    }

    private void ReconcileTargets(IReadOnlyList<DirectorOffscreenViewRequest> frameViews)
    {
        HashSet<Guid> active = frameViews.Select(view => view.ViewId).ToHashSet();
        foreach (Guid stale in targets.Keys.Where(id => !active.Contains(id)).ToArray())
        {
            targets.Remove(stale, out ViewTarget? target);
            target?.Dispose();
        }

        foreach (DirectorOffscreenViewRequest view in frameViews)
        {
            if (targets.TryGetValue(view.ViewId, out ViewTarget? existing)
                && existing.FrameBuffer.Width == view.Width
                && existing.FrameBuffer.Height == view.Height)
            {
                continue;
            }

            existing?.Dispose();
            targets[view.ViewId] = new ViewTarget(capi, game, view);
        }
    }

    private float[] ClearColor()
    {
        Vec4f fog = capi.Render.FogColor;
        return new[] { fog.R, fog.G, fog.B, 1f };
    }

    private static DirectorOffscreenViewRequest NormalizeView(DirectorOffscreenViewRequest source)
    {
        ArgumentNullException.ThrowIfNull(source);
        DirectorOffscreenCameraPose pose = source.Pose;
        if (source.ViewId == Guid.Empty)
        {
            throw new ArgumentException("An offscreen view id cannot be empty.", nameof(source));
        }
        if (source.Width < 16 || source.Width > MaximumDimension
            || source.Height < 16 || source.Height > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                $"Offscreen dimensions must be between 16 and {MaximumDimension} pixels.");
        }
        if (!double.IsFinite(pose.X) || !double.IsFinite(pose.Y) || !double.IsFinite(pose.Z)
            || !float.IsFinite(pose.Yaw) || !float.IsFinite(pose.Pitch)
            || !float.IsFinite(pose.Roll) || !float.IsFinite(pose.Fov))
        {
            throw new ArgumentException("The offscreen camera pose must contain finite values.", nameof(source));
        }

        return new DirectorOffscreenViewRequest
        {
            ViewId = source.ViewId,
            Name = string.IsNullOrWhiteSpace(source.Name) ? "Offscreen View" : source.Name.Trim(),
            Width = source.Width,
            Height = source.Height,
            Pose = pose with { Fov = Math.Clamp(pose.Fov, 10f, 170f) },
            CaptureClockMilliseconds = source.CaptureClockMilliseconds,
            RenderTerrain = source.RenderTerrain,
            RenderEntities = source.RenderEntities,
            RenderLocalPlayer = source.RenderLocalPlayer,
            RenderOpaqueParticles = source.RenderOpaqueParticles
        };
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            enabled = false;
            views.Clear();
            sinks.Clear();
            contributors.Clear();
        }

        foreach (ViewTarget target in targets.Values)
        {
            target.Dispose();
        }
        targets.Clear();
        localPlayerAnimation.Dispose();
        entityAnimationScheduler.Dispose();
        independentPlayerRenderer.Dispose();
        if (ReferenceEquals(DirectorOffscreenRenderRuntime.Renderer, this))
        {
            DirectorOffscreenRenderRuntime.Renderer = null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class ViewTarget : IDisposable
    {
        private readonly ClientMain game;
        private bool disposed;

        internal FrameBufferRef FrameBuffer { get; }

        internal DirectorOffscreenParticleTarget Particles { get; }

        internal ViewTarget(
            ICoreClientAPI capi,
            ClientMain game,
            DirectorOffscreenViewRequest view)
        {
            this.game = game;
            FramebufferAttrs attrs = new(
                "VSDirector-" + view.ViewId.ToString("N"),
                view.Width,
                view.Height)
            {
                Attachments = new[]
                {
                    new FramebufferAttrsAttachment
                    {
                        AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                        Texture = new RawTexture
                        {
                            Width = view.Width,
                            Height = view.Height,
                            PixelInternalFormat = EnumTextureInternalFormat.Rgba8,
                            PixelFormat = EnumTexturePixelFormat.Rgba
                        }
                    },
                    new FramebufferAttrsAttachment
                    {
                        AttachmentType = EnumFramebufferAttachment.DepthAttachment,
                        Texture = new RawTexture
                        {
                            Width = view.Width,
                            Height = view.Height,
                            PixelInternalFormat = EnumTextureInternalFormat.DepthComponent32,
                            PixelFormat = EnumTexturePixelFormat.DepthComponent,
                            MinFilter = EnumTextureFilter.Nearest,
                            MagFilter = EnumTextureFilter.Nearest
                        }
                    }
                }
            };
            FrameBuffer = game.Platform.CreateFramebuffer(attrs);
            Particles = new DirectorOffscreenParticleTarget(capi, game);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Particles.Dispose();
            game.Platform.DisposeFrameBuffer(FrameBuffer, disposeTextures: true);
        }
    }
}

internal sealed class DirectorOffscreenLocalPlayerAnimationContext : IDisposable
{
    private static readonly Type? PlayerRendererType =
        AccessTools.TypeByName("Vintagestory.GameContent.EntityPlayerShapeRenderer");

    private static readonly MethodInfo? CalculateClientMatricesMethod =
        AccessTools.Method(
            typeof(ClientAnimator),
            "calculateMatrices",
            new[] { typeof(float) });

    private static readonly FieldInfo? BodyYawLerpedField = PlayerRendererType is null
        ? null
        : AccessTools.Field(PlayerRendererType, "bodyYawLerped");

    private static readonly FieldInfo? SmoothedBodyYawField = PlayerRendererType is null
        ? null
        : AccessTools.Field(PlayerRendererType, "smoothedBodyYaw");

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly Dictionary<EntityPlayer, PlayerPoseState> states =
        new(ReferenceEqualityComparer.Instance);
    private bool failureLogged;
    private bool disposed;

    internal DirectorOffscreenLocalPlayerAnimationContext(
        ICoreClientAPI capi,
        ClientMain game)
    {
        this.capi = capi;
        this.game = game;
    }

    internal IDisposable? Enter(
        DirectorOffscreenViewRequest view,
        long physicalFrameNumber,
        float frameDeltaTime)
    {
        if (disposed
            || !view.RenderEntities
            || !view.RenderLocalPlayer
            || game.player is not { } clientPlayer
            || clientPlayer.CameraMode != EnumCameraMode.FirstPerson
            || game.EntityPlayer is not { } player
            || player.Pos.Dimension != view.Pose.Dimension
            || DirectorReplayRuntime.Manager?.IsLiveEntitySuppressed(player) == true
            || player.TpAnimManager is not { Animator: { } animator } animationManager
            || ResolveShape(player) is not { } shape
            || !DirectorOffscreenEngineBindings.TryGetEntityRenderer(
                game,
                player.EntityId,
                out EntityRenderer? renderer)
            || renderer is null)
        {
            return null;
        }

        PlayerPoseSnapshot primaryPose = PlayerPoseSnapshot.Capture(player);
        RendererYawSnapshot rendererYaw = RendererYawSnapshot.Capture(renderer);
        EnumCameraMode? previousOverride = clientPlayer.OverrideCameraMode;
        PlayerPoseState state = GetOrCreateState(player);
        try
        {
            state.EnsureController(animationManager, animator, player, shape);
            if (state.LastAdvancedFrame == long.MinValue
                || physicalFrameNumber < state.LastAdvancedFrame
                || physicalFrameNumber - state.LastAdvancedFrame > 1)
            {
                state.Pose = primaryPose;
            }

            clientPlayer.OverrideCameraMode = EnumCameraMode.ThirdPerson;
            state.Pose.Apply(player);
            // Select the third-person renderer mode before shadow rendering;
            // swimming, gliding, mounted transforms, and mesh selection read
            // this cached mode in addition to ClientPlayer.CameraMode.
            renderer.BeforeRender(0f);
            float poseDeltaTime = state.LastAdvancedFrame == physicalFrameNumber
                ? 0f
                : Math.Clamp(frameDeltaTime, 0f, 0.1f);
            state.Controller!.OnFrame(poseDeltaTime);
            state.Pose = PlayerPoseSnapshot.Capture(player);
            state.LastAdvancedFrame = physicalFrameNumber;

            // Animation clips already advanced during the primary frame. Only
            // rebuild joint matrices after applying the detached third-person
            // head/body state, otherwise actions would run twice as fast.
            RebuildAnimatorMatrices(animator, animationManager.ActiveAnimationsByAnimCode);
            rendererYaw.Apply(state.Pose.BodyYaw);
            failureLogged = false;
            return new RestoreScope(
                this,
                player,
                clientPlayer,
                primaryPose,
                previousOverride,
                rendererYaw);
        }
        catch (Exception exception)
        {
            clientPlayer.OverrideCameraMode = previousOverride;
            primaryPose.Apply(player);
            rendererYaw.Restore();
            state.Reset(primaryPose);
            LogFailureOnce(exception);
            return null;
        }
    }

    private PlayerPoseState GetOrCreateState(EntityPlayer player)
    {
        if (states.TryGetValue(player, out PlayerPoseState? state))
        {
            return state;
        }

        state = new PlayerPoseState();
        states[player] = state;
        if (states.Count > 2)
        {
            foreach (EntityPlayer stale in states.Keys
                .Where(candidate => !ReferenceEquals(candidate, player))
                .ToArray())
            {
                states.Remove(stale);
            }
        }
        return state;
    }

    private void Restore(
        EntityPlayer player,
        ClientPlayer clientPlayer,
        PlayerPoseSnapshot primaryPose,
        EnumCameraMode? previousOverride,
        RendererYawSnapshot rendererYaw)
    {
        clientPlayer.OverrideCameraMode = previousOverride;
        primaryPose.Apply(player);
        rendererYaw.Restore();
    }

    private void LogFailureOnce(Exception exception)
    {
        if (failureLogged)
        {
            return;
        }
        failureLogged = true;
        capi.Logger.Warning(
            "VS Director: detached offscreen third-person player animation failed: {0}",
            exception.GetBaseException().Message);
    }

    private static Shape? ResolveShape(EntityPlayer player)
        => player.Properties?.Client?.LoadedShapeForEntity
            ?? player.Properties?.Client?.LoadedShape;

    private static void RebuildAnimatorMatrices(
        IAnimator animator,
        IReadOnlyDictionary<string, AnimationMetaData> activeAnimations)
    {
        if (animator is ClientAnimator clientAnimator
            && CalculateClientMatricesMethod is not null)
        {
            CalculateClientMatricesMethod.Invoke(clientAnimator, new object[] { 0f });
            return;
        }

        // Compatibility fallback for custom animators. Give them a detached
        // metadata dictionary so a zero-time rebuild cannot remove or mutate
        // entries owned by the primary animation manager.
        animator.OnFrame(
            activeAnimations.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase),
            0f);
    }

    public void Dispose()
    {
        disposed = true;
        states.Clear();
    }

    private sealed class PlayerPoseState
    {
        internal IAnimator? Animator { get; private set; }

        internal PlayerHeadController? Controller { get; private set; }

        internal PlayerPoseSnapshot Pose { get; set; }

        internal long LastAdvancedFrame { get; set; } = long.MinValue;

        internal void EnsureController(
            PlayerAnimationManager animationManager,
            IAnimator animator,
            EntityPlayer player,
            Shape shape)
        {
            if (ReferenceEquals(Animator, animator) && Controller is not null)
            {
                return;
            }

            Animator = animator;
            Controller = new PlayerHeadController(animationManager, player, shape);
            LastAdvancedFrame = long.MinValue;
        }

        internal void Reset(PlayerPoseSnapshot pose)
        {
            Pose = pose;
            LastAdvancedFrame = long.MinValue;
        }
    }

    private readonly record struct PlayerPoseSnapshot(
        float BodyYaw,
        float HeadYaw,
        float HeadPitch)
    {
        internal static PlayerPoseSnapshot Capture(EntityPlayer player)
            => new(player.BodyYaw, player.Pos.HeadYaw, player.Pos.HeadPitch);

        internal void Apply(EntityPlayer player)
        {
            player.BodyYaw = BodyYaw;
            player.Pos.HeadYaw = HeadYaw;
            player.Pos.HeadPitch = HeadPitch;
        }
    }

    private readonly record struct RendererYawSnapshot(
        EntityRenderer Renderer,
        float? BodyYawLerped,
        float? SmoothedBodyYaw,
        bool Supported)
    {
        internal static RendererYawSnapshot Capture(EntityRenderer renderer)
        {
            bool supported = PlayerRendererType?.IsInstanceOfType(renderer) == true;
            return new RendererYawSnapshot(
                renderer,
                supported && BodyYawLerpedField?.GetValue(renderer) is float bodyYaw
                    ? bodyYaw
                    : null,
                supported && SmoothedBodyYawField?.GetValue(renderer) is float smoothedYaw
                    ? smoothedYaw
                    : null,
                supported);
        }

        internal void Apply(float bodyYaw)
        {
            if (!Supported)
            {
                return;
            }
            BodyYawLerpedField?.SetValue(Renderer, bodyYaw);
            SmoothedBodyYawField?.SetValue(Renderer, bodyYaw);
        }

        internal void Restore()
        {
            if (BodyYawLerped is float bodyYaw)
            {
                BodyYawLerpedField?.SetValue(Renderer, bodyYaw);
            }
            if (SmoothedBodyYaw is float smoothedYaw)
            {
                SmoothedBodyYawField?.SetValue(Renderer, smoothedYaw);
            }
        }
    }

    private sealed class RestoreScope : IDisposable
    {
        private DirectorOffscreenLocalPlayerAnimationContext? owner;
        private readonly EntityPlayer player;
        private readonly ClientPlayer clientPlayer;
        private readonly PlayerPoseSnapshot primaryPose;
        private readonly EnumCameraMode? previousOverride;
        private readonly RendererYawSnapshot rendererYaw;

        internal RestoreScope(
            DirectorOffscreenLocalPlayerAnimationContext owner,
            EntityPlayer player,
            ClientPlayer clientPlayer,
            PlayerPoseSnapshot primaryPose,
            EnumCameraMode? previousOverride,
            RendererYawSnapshot rendererYaw)
        {
            this.owner = owner;
            this.player = player;
            this.clientPlayer = clientPlayer;
            this.primaryPose = primaryPose;
            this.previousOverride = previousOverride;
            this.rendererYaw = rendererYaw;
        }

        public void Dispose()
        {
            DirectorOffscreenLocalPlayerAnimationContext? context =
                Interlocked.Exchange(ref owner, null);
            context?.Restore(
                player,
                clientPlayer,
                primaryPose,
                previousOverride,
                rendererYaw);
        }
    }
}
