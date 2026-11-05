using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using VSDirector.Replay;
using DirectorKeyframe = VSDirector.Data.CameraKeyframe;

namespace VSDirector.Runtime;

internal sealed class DirectorCameraController : IDisposable
{
    private const double RadiansPerDegree = Math.PI / 180d;
    private const double DegreesPerRadian = 180d / Math.PI;
    private const double MinNativePitchDegrees = 90.86d;
    private const double MaxNativePitchDegrees = 269.14d;
    private const double FollowTargetLostTimeoutSeconds = 5d;
    private static readonly FieldInfo? TargetCameraDistanceField = AccessTools.Field(typeof(PlayerCamera), "targetCameraDistance");
    private static readonly PropertyInfo? MountedOnProperty = typeof(EntityAgent).GetProperty(
        nameof(EntityAgent.MountedOn),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? MountedOnSetter = MountedOnProperty?.GetSetMethod(nonPublic: true);

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly DirectorConfig config;

    private CameraPreset? activeCamera;
    private PathPlayback? activePath;
    private SequencePlayback? activeSequence;
    private FollowPlayback? activeFollow;
    private SavedCameraState? savedState;
    private Entity? hiddenFollowEntity;
    private bool hiddenFollowEntityWasRendered;
    private bool pathCompletionPending;
    private bool sequenceCompletionPending;
    private bool followTargetLostPending;
    private bool freeCameraActive;
    private bool guiPointerCameraInput;
    private ReplayFollowExportState? replayFollowExportState;
    private bool guiOverride;
    private bool workspaceInputOverride;
    private bool? guiOverrideSavedOverlay;
    private bool externalInputLocked;
    private bool? externalSavedAllowCameraControl;
    private bool? externalSavedAllowCharacterControl;
    private CameraPreset? preparedCameraPose;
    private bool replayElementClearViewEnabled;
    private Entity? replayElementClearViewTarget;
    private bool disposed;

    public bool IsActive => activeCamera != null;

    public bool ShouldLockPlayerInput => externalInputLocked || (IsActive && config.LockPlayerInput);

    public string? ActiveCameraName => activeFollow?.Rig.Name ?? activePath?.Path.Name ?? activeCamera?.Name;

    public Guid? ActiveCameraId => activePath is null && activeFollow is null ? activeCamera?.Id : null;

    internal bool UpdateActiveCamera(CameraPreset camera)
    {
        if (disposed || freeCameraActive || activePath is not null || activeSequence is not null
            || activeFollow is not null || activeCamera?.Id != camera.Id
            || !double.IsFinite(camera.X) || !double.IsFinite(camera.Y) || !double.IsFinite(camera.Z)
            || !float.IsFinite(camera.Yaw) || !float.IsFinite(camera.Pitch) || !float.IsFinite(camera.Roll)
            || !float.IsFinite(camera.Fov) || camera.Fov < 10 || camera.Fov > 170) return false;
        // The next render frame consumes this pose without reactivating or resetting camera control.
        activeCamera = camera.DeepClone();
        return true;
    }

    public Guid? ActivePathId => activePath?.Path.Id;

    public Guid? ActiveSequenceId => activeSequence?.Sequence.Id;

    public Guid? ActiveFollowRigId => activeFollow?.Rig.Id;

    public bool IsPlayingPath => activePath != null;

    public bool IsFollowing => activeFollow != null;

    // Clear View only needs to be bridged for follow rigs that explicitly opt in.
    internal bool IsClearViewFollowEnabled
        => activeFollow?.Rig.OpenClearView == true || replayElementClearViewEnabled;

    internal bool IsFollowingNonLocalTarget
        => activeFollow is { } follow
            && capi.World.Player?.Entity is EntityPlayer localPlayer
            && follow.LastTargetEntityId != localPlayer.EntityId;

    public bool IsFreeCamera => freeCameraActive;

    internal bool UsesDetachedThirdPersonRendering(long entityId)
    {
        EntityPlayer? local = capi.World.Player?.Entity as EntityPlayer;
        return IsActive
            && local is not null
            && local.EntityId == entityId
            && !IsFirstPersonFollowTarget(local)
            && DirectorReplayRuntime.Manager?.IsLiveEntitySuppressed(local) != true;
    }

    public double ActivePathTimeSeconds => activePath?.CurrentTimeSeconds ?? 0d;

    public int ActiveSequenceShotIndex => activeSequence?.CurrentShotIndex ?? -1;

    public string? ActiveSequenceName => activeSequence?.Sequence.Name;

    public string? ActiveFollowTargetName => activeFollow?.Rig.TargetDisplayName;

    public string? ActiveFollowTargetPlayerUid => activeFollow?.Rig.TargetType == CameraFollowTargetType.Player
        ? activeFollow.Rig.TargetPlayerUid
        : null;

    public long? ActiveFollowTargetEntityId => activeFollow is { } follow
        ? ResolveFollowTarget(follow)?.EntityId ?? follow.Rig.TargetEntityId
        : null;

    public double ActiveFollowDistance => activeFollow?.Rig.Distance ?? 0d;

    public double ActiveFollowOrbitAngleDegrees => activeFollow is { } follow
        ? CurrentFollowOrbitAngle(follow)
        : 0d;

    public double ActiveFollowElevationDegrees => activeFollow?.ManualElevationDegrees ?? 0d;

    public CameraPreset? CurrentPose => (preparedCameraPose ?? activeCamera)?.DeepClone();

    internal bool IsLocalPlayerEntity(Entity entity)
        => ReferenceEquals(game.EntityPlayer, entity);

    public event Action<string>? PathCompleted;

    public event Action<string>? SequenceCompleted;

    public event Action<string>? FollowTargetLost;

    public DirectorCameraController(ICoreClientAPI capi, DirectorConfig config)
    {
        this.capi = capi;
        this.config = config;
        game = capi.World as ClientMain
            ?? throw new InvalidOperationException("VS Director requires the Vintage Story desktop client.");
        capi.Input.InWorldAction += OnInWorldAction;
        capi.Event.MouseMove += OnMouseMove;
    }

    internal bool TrySuspendLocalMount(out EntityAgent? agent, out IMountableSeat? seat)
    {
        agent = capi.World.Player?.Entity as EntityAgent;
        seat = IsActive ? agent?.MountedOn : null;
        if (agent is null || seat is null || MountedOnSetter is null)
        {
            return false;
        }

        try
        {
            MountedOnSetter.Invoke(agent, new object?[] { null });
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("VS Director: mounted camera isolation could not detach the client mount: {0}",
                exception.Message);
            agent = null;
            seat = null;
            return false;
        }
    }

    internal void RestoreLocalMount(EntityAgent? agent, IMountableSeat? seat)
    {
        if (agent is null
            || seat is null
            || !ReferenceEquals(capi.World.Player?.Entity, agent)
            || agent.MountedOn is not null
            || MountedOnSetter is null)
        {
            return;
        }

        try
        {
            MountedOnSetter.Invoke(agent, new object?[] { seat });
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("VS Director: mounted camera isolation could not restore the client mount: {0}",
                exception.Message);
        }
    }

    public bool TryCaptureCurrentView(string name, out CameraPreset preset)
    {
        preset = new CameraPreset();

        if (capi.World.Player?.Entity is not EntityPlayer entity)
        {
            return false;
        }

        Vec3d eye = game.MainCamera.CameraEyePos;
        if (!IsFinite(eye.X) || !IsFinite(eye.Y) || !IsFinite(eye.Z))
        {
            return false;
        }

        int dimension = preparedCameraPose?.Dimension
            ?? activeCamera?.Dimension
            ?? entity.Pos.Dimension;
        preset = new CameraPreset
        {
            Name = name,
            X = eye.X,
            Y = eye.Y - dimension * 32768d,
            Z = eye.Z,
            Dimension = dimension,
            Yaw = (float)(capi.World.Player.CameraYaw * DegreesPerRadian),
            Pitch = (float)(capi.World.Player.CameraPitch * DegreesPerRadian),
            Roll = (float)(capi.World.Player.CameraRoll * DegreesPerRadian),
            Fov = (float)(game.MainCamera.Fov * DegreesPerRadian),
            CreatedUtc = DateTimeOffset.UtcNow,
            ModifiedUtc = DateTimeOffset.UtcNow
        };

        return true;
    }

    public bool TryActivate(CameraPreset camera, out string error)
    {
        if (!TryActivatePose(camera, out error))
        {
            return false;
        }

        activePath = null;
        activeSequence = null;
        activeFollow = null;
        replayFollowExportState = null;
        SetReplayElementClearView(false);
        freeCameraActive = false;
        RestoreHiddenFollowEntity();
        pathCompletionPending = false;
        sequenceCompletionPending = false;
        followTargetLostPending = false;
        return true;
    }

    public bool TryStartFreeCamera(out string error)
    {
        CameraPreset? pose = CurrentPose;
        if (pose is null && !TryCaptureCurrentView("Replay Free Camera", out pose))
        {
            error = "The current view could not be captured.";
            return false;
        }

        pose.Id = Guid.Empty;
        pose.Name = "Replay Free Camera";

        if (!TryActivatePose(pose, out error))
        {
            return false;
        }

        activePath = null;
        activeSequence = null;
        activeFollow = null;
        replayFollowExportState = null;
        RestoreHiddenFollowEntity();
        pathCompletionPending = false;
        sequenceCompletionPending = false;
        followTargetLostPending = false;
        guiPointerCameraInput = false;
        freeCameraActive = true;
        SetReplayElementClearView(false);
        return true;
    }

    public bool TrySetReplayPose(CameraPreset pose, out string error)
    {
        if (!TryActivatePose(pose, out error))
        {
            return false;
        }

        activePath = null;
        activeSequence = null;
        activeFollow = null;
        RestoreHiddenFollowEntity();
        pathCompletionPending = false;
        sequenceCompletionPending = false;
        followTargetLostPending = false;
        freeCameraActive = false;
        replayFollowExportState = null;
        SetReplayElementClearView(false);
        return true;
    }

    internal void SetReplayElementClearView(bool enabled, Entity? target = null)
    {
        replayElementClearViewEnabled = enabled;
        replayElementClearViewTarget = enabled ? target : null;
    }

    internal bool TryGetClearViewTarget(out Vec3d target)
    {
        Entity? entity = activeFollow is { } follow
            ? ResolveFollowTarget(follow)
            : replayElementClearViewTarget ?? replayFollowExportState?.Target;
        if (entity is not null
            && DirectorEntityViewResolver.TryResolveHeadPosition(entity, out double x, out double y, out double z))
        {
            target = new Vec3d(x, DirectorChunkWindow.ToAbsoluteY(y, entity.Pos.Dimension), z);
            return true;
        }

        target = new Vec3d();
        return false;
    }

    internal bool TrySetReplayFollowElementPose(
        CameraFollowRig rig,
        Entity target,
        double timeSeconds,
        out CameraPreset pose,
        out string error)
    {
        pose = new CameraPreset();
        if (!TrySetReplayFollowPose(rig, target, timeSeconds, out error))
        {
            return false;
        }

        pose = activeCamera?.DeepClone() ?? new CameraPreset();
        replayElementClearViewEnabled = rig.OpenClearView;
        replayElementClearViewTarget = rig.OpenClearView ? target : null;
        return true;
    }

    internal bool TrySetReplayFollowElementAdjustedPose(CameraPreset pose, out string error)
    {
        if (!TryActivatePose(pose, out error))
        {
            return false;
        }

        activePath = null;
        activeSequence = null;
        activeFollow = null;
        freeCameraActive = false;
        RestoreHiddenFollowEntity();
        pathCompletionPending = false;
        sequenceCompletionPending = false;
        followTargetLostPending = false;
        return true;
    }

    /// <summary>
    /// Evaluates a follow rig against the actor state currently applied by a
    /// replay export. Keeping the small amount of history here preserves
    /// smooth and delayed follow modes while the exporter seeks frame by
    /// frame.
    /// </summary>
    public bool TrySetReplayFollowPose(
        CameraFollowRig rig,
        Entity target,
        double timeSeconds,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(target);
        error = string.Empty;
        if (!double.IsFinite(timeSeconds) || !TryCreateFollowTargetPose(rig, target, out CameraFollowTargetPose targetPose))
        {
            error = "The replay follow target has no valid camera pose.";
            return false;
        }

        double orbitOffset = timeSeconds * rig.OrbitSpeedDegreesPerSecond;
        CameraFollowPose desired = CameraFollowMath.Evaluate(rig, targetPose, orbitOffset);
        CameraFollowPose output = desired;
        ReplayFollowExportState? previous = replayFollowExportState;
        if (previous is not null
            && previous.RigId == rig.Id
            && timeSeconds >= previous.TimeSeconds - 0.000001d)
        {
            double delta = Math.Max(0d, timeSeconds - previous.TimeSeconds);
            output = rig.MotionMode switch
            {
                CameraFollowMotionMode.Smooth => CameraFollowMath.Smooth(
                    previous.Pose,
                    desired,
                    delta,
                    rig.SmoothTimeSeconds),
                CameraFollowMotionMode.Delayed => ResolveReplayDelayedPose(
                    previous,
                    rig,
                    timeSeconds,
                    desired),
                _ => desired
            };
        }

        if (!TryActivatePose(output.ToPreset(rig.Id, rig.Name), out error))
        {
            return false;
        }

        activePath = null;
        activeSequence = null;
        activeFollow = null;
        freeCameraActive = false;
        RestoreHiddenFollowEntity();
        pathCompletionPending = false;
        sequenceCompletionPending = false;
        followTargetLostPending = false;
        ReplayFollowExportState next = previous is not null && previous.RigId == rig.Id
            && timeSeconds >= previous.TimeSeconds - 0.000001d
                ? previous
                : new ReplayFollowExportState(rig.Id, target, rig.ViewMode, timeSeconds, output);
        next.Target = target;
        next.ViewMode = rig.ViewMode;
        next.TimeSeconds = timeSeconds;
        next.Pose = output;
        if (next.History.Count == 0)
        {
            next.History.Add(timeSeconds, desired, rig.DelaySeconds);
        }

        replayFollowExportState = next;
        return true;
    }

    public bool TryPlayPath(CameraPath path, bool reverse, out string error)
    {
        ArgumentNullException.ThrowIfNull(path);
        error = string.Empty;
        if (path.Keyframes.Count < 2 || path.DurationSeconds <= 0.000001d)
        {
            error = "A camera path needs at least two timed keyframes.";
            return false;
        }

        CameraPath normalizedPath = path.DeepClone();
        normalizedPath.PlaybackSpeed = CameraPathTiming.NormalizePlaybackSpeed(normalizedPath.PlaybackSpeed);
        DirectorPathElementTimeline.Normalize(normalizedPath);
        CameraPathTime initialTime = CameraPathSampler.ResolvePlaybackTime(
            normalizedPath.DurationSeconds,
            0d,
            normalizedPath.LoopMode,
            reverse);
        if (!TryEvaluatePathPose(normalizedPath, initialTime.TimeSeconds, out CameraPreset initialPose))
        {
            error = "The camera path could not be sampled.";
            return false;
        }

        if (!TryActivatePose(initialPose, out error))
        {
            return false;
        }

        activePath = new PathPlayback(normalizedPath, reverse)
        {
            CurrentTimeSeconds = initialTime.TimeSeconds
        };
        activeSequence = null;
        activeFollow = null;
        replayFollowExportState = null;
        freeCameraActive = false;
        RestoreHiddenFollowEntity();
        pathCompletionPending = false;
        sequenceCompletionPending = false;
        followTargetLostPending = false;
        return true;
    }

    public bool TryPlaySequence(
        CameraSequence sequence,
        IReadOnlyDictionary<Guid, CameraPreset> cameras,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(cameras);
        error = string.Empty;
        if (sequence.Shots is null
            || sequence.Shots.Count == 0
            || sequence.Shots.Any(shot => shot is null))
        {
            error = "A camera sequence needs at least one shot.";
            return false;
        }

        CameraSequence normalizedSequence = sequence.DeepClone();
        normalizedSequence.LoopMode = Enum.IsDefined(normalizedSequence.LoopMode)
            ? normalizedSequence.LoopMode
            : CameraSequenceLoopMode.Once;
        List<CameraPreset> resolvedCameras = new(normalizedSequence.Shots.Count);
        foreach (CameraSequenceShot shot in normalizedSequence.Shots)
        {
            if (!double.IsFinite(shot.DurationSeconds)
                || shot.DurationSeconds < CameraSequenceSampler.MinShotDurationSeconds
                || shot.DurationSeconds > CameraSequenceSampler.MaxShotDurationSeconds)
            {
                error = "Every sequence shot needs a valid duration.";
                return false;
            }

            if (!cameras.TryGetValue(shot.CameraId, out CameraPreset? camera))
            {
                error = "A camera referenced by the sequence no longer exists.";
                return false;
            }

            resolvedCameras.Add(camera.DeepClone());
        }

        if (!CameraSequenceSampler.TryResolve(normalizedSequence, 0d, out CameraSequenceTime initialTime))
        {
            error = "The camera sequence could not be timed.";
            return false;
        }

        CameraPreset initialCamera = resolvedCameras[initialTime.ShotIndex];
        if (!TryActivatePose(initialCamera, out error))
        {
            return false;
        }

        activePath = null;
        activeSequence = new SequencePlayback(normalizedSequence, resolvedCameras)
        {
            CurrentShotIndex = initialTime.ShotIndex
        };
        activeFollow = null;
        replayFollowExportState = null;
        freeCameraActive = false;
        RestoreHiddenFollowEntity();
        pathCompletionPending = false;
        sequenceCompletionPending = false;
        followTargetLostPending = false;
        return true;
    }

    public bool TryFollow(CameraFollowRig rig, Entity target, out string error)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(target);
        error = string.Empty;

        CameraFollowRig normalized = rig.DeepClone();
        if (!TryCreateFollowTargetPose(normalized, target, out CameraFollowTargetPose targetPose))
        {
            error = "The follow target has no valid camera pose in the current dimension.";
            return false;
        }

        CameraFollowPose initialPose = CameraFollowMath.Evaluate(normalized, targetPose);
        if (!TryActivatePose(initialPose.ToPreset(normalized.Id, normalized.Name), out error))
        {
            return false;
        }

        FollowPlayback playback = new(normalized, target.EntityId)
        {
            LastPose = initialPose,
            HasStableLocalPlayerView = IsLocalPlayerEntity(target),
            StableLocalPlayerViewAnchor = normalized.Anchor,
            LocalPlayerYawDegrees = targetPose.YawDegrees,
            LocalPlayerPitchDegrees = targetPose.PitchDegrees
        };
        playback.History.Add(0d, initialPose, normalized.DelaySeconds);
        activePath = null;
        activeSequence = null;
        activeFollow = playback;
        replayFollowExportState = null;
        SetReplayElementClearView(false);
        freeCameraActive = false;
        RestoreHiddenFollowEntity();
        pathCompletionPending = false;
        sequenceCompletionPending = false;
        followTargetLostPending = false;
        DirectorFollowSoundIsolation.UpdatePlayingObserverRelativeSounds();
        return true;
    }

    /// <summary>
    /// Applies an edited follow rig to the current playback without restarting
    /// its elapsed time, smoothing state, or delayed pose history.
    /// </summary>
    public bool TryUpdateFollow(CameraFollowRig rig, Entity target, out string error)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(target);
        error = string.Empty;

        FollowPlayback? playback = activeFollow;
        if (playback is null || playback.Rig.Id != rig.Id)
        {
            return false;
        }

        CameraFollowRig normalized = rig.DeepClone();
        if (playback.LastTargetEntityId != target.EntityId
            || playback.StableLocalPlayerViewAnchor != normalized.Anchor)
        {
            playback.HasStableLocalPlayerView = false;
        }

        if (!TryCreateFollowTargetPoseWithStableView(
                normalized,
                target,
                playback.HasStableLocalPlayerView ? playback.LocalPlayerYawDegrees : null,
                playback.HasStableLocalPlayerView ? playback.LocalPlayerPitchDegrees : null,
                out CameraFollowTargetPose targetPose))
        {
            error = "The follow target has no valid camera pose in the current dimension.";
            return false;
        }

        if (IsLocalPlayerEntity(target)
            && !playback.HasStableLocalPlayerView)
        {
            playback.LocalPlayerYawDegrees = targetPose.YawDegrees;
            playback.LocalPlayerPitchDegrees = targetPose.PitchDegrees;
            playback.StableLocalPlayerViewAnchor = normalized.Anchor;
            playback.HasStableLocalPlayerView = true;
        }

        playback.Rig = normalized;
        playback.LastTargetEntityId = target.EntityId;
        if (normalized.MotionMode == CameraFollowMotionMode.Delayed
            && playback.History.Count == 0)
        {
            CameraFollowPose seed = CameraFollowMath.Evaluate(
                normalized,
                targetPose,
                playback.ManualOrbitOffsetDegrees
                    + playback.ElapsedSeconds * normalized.OrbitSpeedDegreesPerSecond,
                playback.ManualElevationDegrees);
            playback.History.Add(playback.ElapsedSeconds, seed, normalized.DelaySeconds);
        }

        return true;
    }

    internal bool TryEvaluatePathPose(
        CameraPath path,
        double timeSeconds,
        out CameraPreset pose)
    {
        ArgumentNullException.ThrowIfNull(path);
        pose = new CameraPreset();
        if (!CameraPathSampler.TryEvaluate(path, timeSeconds, out DirectorKeyframe frame))
        {
            return false;
        }

        pose = frame.ToPreset(path.Id, path.Name);
        ApplyPathElements(path, timeSeconds, pose);
        return true;
    }

    public bool TrySetReplayFov(float fov, out string error)
    {
        error = string.Empty;
        if (!float.IsFinite(fov)
            || fov < DirectorReplayElementTimeline.MinFovDegrees
            || fov > DirectorReplayElementTimeline.MaxFovDegrees)
        {
            error = "The replay FOV must be between 1 and 150 degrees.";
            return false;
        }
        if (activeCamera is null)
        {
            if (!TryCaptureCurrentView("Replay FOV", out CameraPreset pose)
                || !TryActivatePose(pose, out error))
            {
                return false;
            }
        }
        activeCamera!.Fov = fov;
        return true;
    }

    internal bool TryEvaluateFollowPose(
        CameraFollowRig rig,
        Entity target,
        out CameraPreset pose)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(target);
        pose = new CameraPreset();
        CameraFollowRig normalized = rig.DeepClone();
        if (!TryCreateFollowTargetPose(normalized, target, out CameraFollowTargetPose targetPose))
        {
            return false;
        }

        pose = CameraFollowMath.Evaluate(normalized, targetPose)
            .ToPreset(normalized.Id, normalized.Name);
        return true;
    }

    public bool TrySetFollowOrbit(double angleDegrees, out double appliedAngle)
    {
        FollowPlayback? follow = activeFollow;
        if (follow is null || follow.Rig.ViewMode != CameraFollowViewMode.ThirdPerson)
        {
            appliedAngle = 0d;
            return false;
        }

        double automaticAngle = follow.Rig.OrbitAngleDegrees
            + follow.ElapsedSeconds * follow.Rig.OrbitSpeedDegreesPerSecond;
        follow.ManualOrbitOffsetDegrees = CameraFollowMath.NormalizeDegrees(angleDegrees - automaticAngle);
        appliedAngle = CurrentFollowOrbitAngle(follow);
        return true;
    }

    public bool TrySetFollowDistance(double distance, out double appliedDistance)
    {
        FollowPlayback? follow = activeFollow;
        if (follow is null || follow.Rig.ViewMode != CameraFollowViewMode.ThirdPerson)
        {
            appliedDistance = 0d;
            return false;
        }

        follow.Rig.Distance = CameraFollowMath.NormalizeDistance(distance);
        appliedDistance = follow.Rig.Distance;
        return true;
    }

    private bool TryActivatePose(CameraPreset camera, out string error)
    {
        error = string.Empty;

        if (disposed)
        {
            error = "Camera controller is disposed.";
            return false;
        }

        if (!IsActive && savedState != null)
        {
            error = "The previous camera state could not be restored; leave the world before activating another camera.";
            return false;
        }

        if (capi.World.Player?.Entity is not EntityPlayer || capi.World.Player is not ClientPlayer player)
        {
            error = "The local player is not ready.";
            return false;
        }

        if (activeFollow is not null)
        {
            DirectorFollowSoundIsolation.RestoreObserverRelativeSounds();
        }

        try
        {
            bool enteringDetachedCamera = !IsActive;
            if (!IsActive)
            {
                savedState = SavedCameraState.Capture(
                    game,
                    player,
                    externalInputLocked ? externalSavedAllowCameraControl : null,
                    externalInputLocked ? externalSavedAllowCharacterControl : null);
            }

            activeCamera = camera.DeepClone();
            if (enteringDetachedCamera)
            {
                player.Entity.TpAnimManager.UseFpAnmations = false;
                player.Entity.SelfFpAnimManager.UseFpAnmations = false;
                player.Entity.MarkShapeModified();
            }
            game.AllowCameraControl = false;
            if (config.HideHudOnCamera)
            {
                if (guiOverride)
                {
                    guiOverrideSavedOverlay = false;
                    game.ShouldRender2DOverlays = true;
                }
                else
                {
                    game.ShouldRender2DOverlays = false;
                }
            }

            PrepareCameraFrame(game.MainCamera);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Deactivate();
            return false;
        }
    }

    public void SetHideHud(bool hide)
    {
        if (!IsActive || savedState == null)
        {
            return;
        }

        bool desired = hide ? false : savedState.ShouldRender2DOverlays;
        if (guiOverride)
        {
            guiOverrideSavedOverlay = desired;
            game.ShouldRender2DOverlays = true;
            return;
        }

        game.ShouldRender2DOverlays = desired;
    }

    public void SetGuiOverride(bool enabled)
    {
        bool requested = enabled && IsActive;
        if (requested)
        {
            if (!guiOverride)
            {
                guiOverrideSavedOverlay = game.ShouldRender2DOverlays;
            }

            guiOverride = true;
            game.ShouldRender2DOverlays = true;
            return;
        }

        if (guiOverride)
        {
            guiOverride = false;
            game.ShouldRender2DOverlays = guiOverrideSavedOverlay
                ?? (config.HideHudOnCamera ? false : savedState?.ShouldRender2DOverlays ?? true);
            guiOverrideSavedOverlay = null;
        }
        else if (savedState is not null)
        {
            game.ShouldRender2DOverlays = config.HideHudOnCamera
                ? false
                : savedState.ShouldRender2DOverlays;
        }
    }

    // Keep the editor's mouse ownership independent of both HUD visibility and
    // camera activation. The workspace may open before the first replay camera.
    internal void SetWorkspaceInputOverride(bool enabled) => workspaceInputOverride = enabled;

    public void SetInputLock(bool locked)
    {
        if (!IsActive || savedState == null)
        {
            return;
        }

        game.AllowCharacterControl = locked || externalInputLocked
            ? false
            : savedState.AllowCharacterControl;
        if (locked || externalInputLocked)
        {
            capi.World.Player?.Entity?.Controls?.StopAllMovement();
        }
    }

    public void SetExternalInputLock(bool locked)
    {
        if (externalInputLocked == locked)
        {
            return;
        }

        if (locked)
        {
            externalSavedAllowCameraControl = savedState?.AllowCameraControl ?? game.AllowCameraControl;
            externalSavedAllowCharacterControl = savedState?.AllowCharacterControl ?? game.AllowCharacterControl;
            externalInputLocked = true;
            game.AllowCameraControl = false;
            game.AllowCharacterControl = false;
            game.InWorldMouseState.Clear();
            StopAllControlledMovement();
            return;
        }

        externalInputLocked = false;
        if (IsActive)
        {
            game.AllowCameraControl = false;
            game.AllowCharacterControl = config.LockPlayerInput
                ? false
                : savedState?.AllowCharacterControl ?? externalSavedAllowCharacterControl ?? true;
        }
        else
        {
            if (externalSavedAllowCameraControl is bool allowCameraControl)
            {
                game.AllowCameraControl = allowCameraControl;
            }

            if (externalSavedAllowCharacterControl is bool allowCharacterControl)
            {
                game.AllowCharacterControl = allowCharacterControl;
            }
        }

        externalSavedAllowCameraControl = null;
        externalSavedAllowCharacterControl = null;
    }

    public void OnGameTick(float deltaTime)
    {
        if (followTargetLostPending && activeFollow is not null)
        {
            string rigName = activeFollow.Rig.Name;
            Deactivate();
            FollowTargetLost?.Invoke(rigName);
            return;
        }

        if (sequenceCompletionPending && activeSequence is not null)
        {
            string sequenceName = activeSequence.Sequence.Name;
            Deactivate();
            SequenceCompleted?.Invoke(sequenceName);
            return;
        }

        if (pathCompletionPending && activePath is not null)
        {
            string pathName = activePath.Path.Name;
            Deactivate();
            PathCompleted?.Invoke(pathName);
            return;
        }

        if (!ShouldLockPlayerInput)
        {
            return;
        }

        game.AllowCameraControl = false;
        game.AllowCharacterControl = false;
        game.InWorldMouseState.Clear();
        StopAllControlledMovement();
    }

    public void PreparePlayerControlTick(
        int forwardKey,
        int backwardKey,
        int leftKey,
        int rightKey,
        int jumpKey,
        int sneakKey,
        int sprintKey,
        int sittingKey,
        int ctrlKey,
        int shiftKey)
    {
        if (!ShouldLockPlayerInput)
        {
            return;
        }

        ClearControlKey(forwardKey);
        ClearControlKey(backwardKey);
        ClearControlKey(leftKey);
        ClearControlKey(rightKey);
        ClearControlKey(jumpKey);
        ClearControlKey(sneakKey);
        ClearControlKey(sprintKey);
        ClearControlKey(sittingKey);
        ClearControlKey(ctrlKey);
        ClearControlKey(shiftKey);

        game.InWorldMouseState.Clear();
        StopAllControlledMovement();
    }

    public void PrepareCameraFrameSafe(PlayerCamera camera, float deltaTime)
    {
        try
        {
            if (freeCameraActive)
            {
                AdvanceFreeCamera(deltaTime);
            }
            AdvancePlayback(deltaTime);
            PrepareCameraFrame(camera);
        }
        catch (Exception ex)
        {
            capi.Logger.Error("VS Director: camera frame failed, leaving director camera: {0}", ex);
            Deactivate();
        }
    }

    public void PrepareCameraFrame(PlayerCamera camera)
    {
        CameraPreset? target = activeCamera;
        if (target == null || capi.World.Player?.Entity is not EntityPlayer entity || capi.World.Player is not ClientPlayer player)
        {
            preparedCameraPose = null;
            return;
        }
        target = ResolveCollisionSafePose(target);
        preparedCameraPose = target;

        player.OverrideCameraMode = null;
        ForceCameraMode(camera, player, EnumCameraMode.ThirdPerson);

        camera.TppCameraDistanceMin = 0;
        camera.TppCameraDistanceMax = 0;
        camera.Tppcameradistance = 0f;
        SetTargetCameraDistance(camera, 0f);
        camera.UpdateCameraPos = false;

        float yaw = DegreesToRadians(target.Yaw);
        float pitch = DegreesToRadians(target.Pitch);
        Vec3f forward = EntityPos.GetViewVector(pitch, yaw);
        double absoluteTargetY = DirectorChunkWindow.ToAbsoluteY(target.Y, target.Dimension);
        entity.CameraPos.Set(
            target.X - entity.LocalEyePos.X - forward.X * 0.2d,
            absoluteTargetY - entity.LocalEyePos.Y - entity.Pos.DimensionYAdjustment - forward.Y * 0.2d,
            target.Z - entity.LocalEyePos.Z - forward.Z * 0.2d);

        player.CameraYaw = yaw;
        player.CameraPitch = pitch;
        player.CameraRoll = DegreesToRadians(target.Roll);
        float targetFov = DegreesToRadians(Math.Clamp(target.Fov, 1f, 170f));
        float targetZNear = Math.Clamp(0.1f - Math.Clamp(target.Fov, 1f, 170f) / 90f / 25f, 0.025f, 0.1f);
        if (Math.Abs(camera.Fov - targetFov) > 0.00001f || Math.Abs(camera.ZNear - targetZNear) > 0.00001f)
        {
            camera.Fov = targetFov;
            camera.ZNear = targetZNear;
            game.Reset3DProjection();
        }

        game.AllowCameraControl = false;
        entity.IsRendered = !IsFirstPersonFollowTarget(entity)
            && DirectorReplayRuntime.Manager?.IsLiveEntitySuppressed(entity) != true;
        game.AllowCharacterControl = ShouldLockPlayerInput
            ? false
            : savedState?.AllowCharacterControl ?? game.AllowCharacterControl;
        if (guiOverride)
        {
            game.ShouldRender2DOverlays = true;
        }
        else if (config.HideHudOnCamera)
        {
            game.ShouldRender2DOverlays = false;
        }

        if (ShouldLockPlayerInput)
        {
            StopAllControlledMovement();
        }
    }

    public void HideLocalPlayerForFrame()
    {
        RestoreHiddenFollowEntity();
        if (!IsActive || capi.World.Player?.Entity is not EntityPlayer localEntity)
        {
            return;
        }

        bool renderLocalTarget = !IsFirstPersonFollowTarget(localEntity)
            && DirectorReplayRuntime.Manager?.IsLiveEntitySuppressed(localEntity) != true;
        localEntity.IsRendered = renderLocalTarget;
        Entity? target = activeFollow is { Rig.ViewMode: CameraFollowViewMode.FirstPerson } follow
            ? ResolveFollowTarget(follow)
            : replayFollowExportState is { ViewMode: CameraFollowViewMode.FirstPerson } replayFollow
                ? replayFollow.Target
                : null;
        if (target is null)
        {
            return;
        }

        if (!ReferenceEquals(target, localEntity))
        {
            hiddenFollowEntity = target;
            hiddenFollowEntityWasRendered = target.IsRendered;
            target.IsRendered = false;
        }
    }

    public void RestoreFollowTargetForFrame()
    {
        RestoreHiddenFollowEntity();
    }

    public bool TryGetLockedAngles(out double yaw, out double pitch)
    {
        CameraPreset? target = activeCamera;
        if (target == null)
        {
            yaw = 0d;
            pitch = 0d;
            return false;
        }

        yaw = target.Yaw * RadiansPerDegree;
        pitch = target.Pitch * RadiansPerDegree;
        return true;
    }

    public void UpdateAudioListenerForFrame()
    {
        CameraPreset? target = preparedCameraPose ?? activeCamera;
        if (target == null)
        {
            return;
        }

        float yaw = DegreesToRadians(target.Yaw);
        float pitch = DegreesToRadians(target.Pitch);
        Vec3f forward = EntityPos.GetViewVector(pitch, yaw);
        game.Platform.UpdateAudioListener(
            (float)target.X,
            (float)DirectorChunkWindow.ToAbsoluteY(target.Y, target.Dimension),
            (float)target.Z,
            forward.X,
            forward.Y,
            forward.Z);
    }

    public void Deactivate()
    {
        SavedCameraState? state = savedState;
        activeCamera = null;
        preparedCameraPose = null;
        activePath = null;
        activeSequence = null;
        activeFollow = null;
        freeCameraActive = false;
        guiPointerCameraInput = false;
        RestoreHiddenFollowEntity();
        pathCompletionPending = false;
        sequenceCompletionPending = false;
        followTargetLostPending = false;
        guiOverride = false;
        guiOverrideSavedOverlay = null;
        replayFollowExportState = null;
        replayElementClearViewEnabled = false;
        replayElementClearViewTarget = null;
        DirectorFollowSoundIsolation.RestoreObserverRelativeSounds();

        if (state == null)
        {
            return;
        }

        try
        {
            state.Restore(game, capi.World.Player as ClientPlayer);
            savedState = null;
            if (externalInputLocked)
            {
                game.AllowCameraControl = false;
                game.AllowCharacterControl = false;
            }
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("VS Director: failed to restore one or more camera state values: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        capi.Input.InWorldAction -= OnInWorldAction;
        capi.Event.MouseMove -= OnMouseMove;
        Deactivate();
        SetExternalInputLock(false);
    }

    private static void ForceCameraMode(PlayerCamera camera, ClientPlayer player, EnumCameraMode target)
    {
        for (int i = 0; i < 4 && player.CameraMode != target; i++)
        {
            camera.CycleMode();
        }
    }

    private static float DegreesToRadians(float degrees)
    {
        return (float)(degrees * RadiansPerDegree);
    }

    private void AdvancePlayback(float deltaTime)
    {
        if (activeFollow is not null)
        {
            AdvanceFollow(deltaTime);
        }
        else if (activePath is not null)
        {
            AdvancePath(deltaTime);
        }
        else if (activeSequence is not null)
        {
            AdvanceSequence(deltaTime);
        }
    }

    private void AdvanceFollow(float deltaTime)
    {
        FollowPlayback? playback = activeFollow;
        if (playback is null || IsAutomatedPlaybackPaused || followTargetLostPending)
        {
            return;
        }

        double dt = float.IsFinite(deltaTime) && deltaTime > 0f
            ? Math.Min(deltaTime, 1f)
            : 0d;
        Entity? target = ResolveFollowTarget(playback);
        if (target is null)
        {
            playback.MissingSeconds += dt;
            followTargetLostPending = playback.MissingSeconds >= FollowTargetLostTimeoutSeconds;
            return;
        }

        if (playback.LastTargetEntityId != target.EntityId
            || playback.StableLocalPlayerViewAnchor != playback.Rig.Anchor)
        {
            playback.HasStableLocalPlayerView = false;
        }

        if (!TryCreateFollowTargetPoseWithStableView(
                playback.Rig,
                target,
                playback.HasStableLocalPlayerView ? playback.LocalPlayerYawDegrees : null,
                playback.HasStableLocalPlayerView ? playback.LocalPlayerPitchDegrees : null,
                out CameraFollowTargetPose targetPose))
        {
            playback.MissingSeconds += dt;
            followTargetLostPending = playback.MissingSeconds >= FollowTargetLostTimeoutSeconds;
            return;
        }

        if (IsLocalPlayerEntity(target)
            && !playback.HasStableLocalPlayerView)
        {
            playback.LocalPlayerYawDegrees = targetPose.YawDegrees;
            playback.LocalPlayerPitchDegrees = targetPose.PitchDegrees;
            playback.StableLocalPlayerViewAnchor = playback.Rig.Anchor;
            playback.HasStableLocalPlayerView = true;
        }

        playback.MissingSeconds = 0d;
        playback.LastTargetEntityId = target.EntityId;
        playback.ElapsedSeconds += dt;
        double orbitOffset = playback.ManualOrbitOffsetDegrees
            + playback.ElapsedSeconds * playback.Rig.OrbitSpeedDegreesPerSecond;
        CameraFollowPose desired = CameraFollowMath.Evaluate(
            playback.Rig,
            targetPose,
            orbitOffset,
            playback.ManualElevationDegrees);
        CameraFollowPose output = playback.Rig.MotionMode switch
        {
            CameraFollowMotionMode.Immediate => desired,
            CameraFollowMotionMode.Smooth => CameraFollowMath.Smooth(
                playback.LastPose,
                desired,
                dt,
                playback.Rig.SmoothTimeSeconds),
            CameraFollowMotionMode.Delayed => ResolveDelayedFollowPose(playback, desired),
            _ => desired
        };
        playback.LastPose = output;
        activeCamera = output.ToPreset(playback.Rig.Id, playback.Rig.Name);
    }

    private static CameraFollowPose ResolveDelayedFollowPose(FollowPlayback playback, CameraFollowPose desired)
    {
        playback.History.Add(playback.ElapsedSeconds, desired, playback.Rig.DelaySeconds);
        return playback.History.TrySample(
            playback.ElapsedSeconds - playback.Rig.DelaySeconds,
            out CameraFollowPose delayed)
            ? delayed
            : desired;
    }

    private Entity? ResolveFollowTarget(FollowPlayback playback)
    {
        if (playback.Rig.TargetType == CameraFollowTargetType.Player)
        {
            if (string.IsNullOrWhiteSpace(playback.Rig.TargetPlayerUid))
            {
                return null;
            }

            Entity? replayActor = DirectorReplayRuntime.Manager?.ResolveReplayActor(
                playback.Rig.TargetPlayerUid,
                playback.LastTargetEntityId);
            return replayActor
                ?? capi.World.AllOnlinePlayers.FirstOrDefault(player => string.Equals(
                    player.PlayerUID,
                    playback.Rig.TargetPlayerUid,
                    StringComparison.Ordinal))?.Entity
                ?? capi.World.PlayerByUid(playback.Rig.TargetPlayerUid)?.Entity;
        }

        long entityId = playback.Rig.TargetEntityId > 0
            ? playback.Rig.TargetEntityId
            : playback.LastTargetEntityId;
        return DirectorReplayRuntime.Manager?.ResolveReplayActor(null, entityId)
            ?? (entityId > 0 ? capi.World.GetEntityById(entityId) : null);
    }

    private void AdvanceFreeCamera(float deltaTime)
    {
        CameraPreset? camera = activeCamera;
        if (camera is null || game.IsPaused)
        {
            return;
        }

        double dt = float.IsFinite(deltaTime) && deltaTime > 0f
            ? Math.Min(deltaTime, 0.25f)
            : 0d;
        if (dt <= 0d)
        {
            return;
        }

        bool[] keys = capi.Input.KeyboardKeyStateRaw;
        int forwardKey = ResolveHotkeyKey("walkforward", GlKeys.W);
        int backwardKey = ResolveHotkeyKey("walkbackward", GlKeys.S);
        int leftKey = ResolveHotkeyKey("walkleft", GlKeys.A);
        int rightKey = ResolveHotkeyKey("walkright", GlKeys.D);
        int jumpKey = ResolveHotkeyKey("jump", GlKeys.Space);
        int sneakKey = ResolveHotkeyKey("sneak", GlKeys.LShift);
        int sprintKey = ResolveHotkeyKey("sprint", GlKeys.LControl);
        double forwardInput = KeyDown(keys, forwardKey) ? 1d : 0d;
        forwardInput -= KeyDown(keys, backwardKey) ? 1d : 0d;
        double rightInput = KeyDown(keys, rightKey) ? 1d : 0d;
        rightInput -= KeyDown(keys, leftKey) ? 1d : 0d;
        double upInput = KeyDown(keys, jumpKey) ? 1d : 0d;
        upInput -= KeyDown(keys, sneakKey) ? 1d : 0d;
        if (Math.Abs(forwardInput) + Math.Abs(rightInput) + Math.Abs(upInput) <= 0d)
        {
            return;
        }

        float yaw = DegreesToRadians(camera.Yaw);
        float pitch = DegreesToRadians(camera.Pitch);
        Vec3f forward = EntityPos.GetViewVector(pitch, yaw);
        // EntityControls uses the opposite horizontal perpendicular from the
        // previous implementation: at yaw 0, the game's Right action is -X.
        double rightX = -forward.Z;
        double rightZ = forward.X;
        double rightLength = Math.Sqrt(rightX * rightX + rightZ * rightZ);
        if (rightLength > 0.000001d)
        {
            rightX /= rightLength;
            rightZ /= rightLength;
        }

        double moveX = forward.X * forwardInput + rightX * rightInput;
        double moveY = forward.Y * forwardInput + upInput;
        double moveZ = forward.Z * forwardInput + rightZ * rightInput;
        double length = Math.Sqrt(moveX * moveX + moveY * moveY + moveZ * moveZ);
        if (length <= 0.000001d)
        {
            return;
        }

        double speed = config.ReplayFreeCameraSpeed;
        if (KeyDown(keys, sprintKey))
        {
            speed *= 4d;
        }

        double distance = speed * dt / length;
        camera.X += moveX * distance;
        camera.Y += moveY * distance;
        camera.Z += moveZ * distance;
    }

    private void OnMouseMove(MouseEvent e)
    {
        if ((guiOverride || workspaceInputOverride) && (!freeCameraActive || !guiPointerCameraInput))
        {
            return;
        }

        double sensitivity = config.ReplayFreeCameraMouseSensitivity;
        CameraPreset? camera = activeCamera;
        if (freeCameraActive && camera is not null)
        {
            camera.Yaw = (float)CameraFollowMath.NormalizeDegrees(camera.Yaw - e.DeltaX * sensitivity);
            double pitchDelta = e.DeltaY * sensitivity * (ClientSettings.InvertMouseYAxis ? -1d : 1d);
            camera.Pitch = (float)Math.Clamp(
                camera.Pitch + pitchDelta,
                MinNativePitchDegrees,
                MaxNativePitchDegrees);
            e.Handled = true;
            return;
        }

        FollowPlayback? follow = activeFollow;
        if (follow is { Rig.ViewMode: CameraFollowViewMode.ThirdPerson })
        {
            if (IsLocalPlayerFollowTarget(follow))
            {
                e.Handled = true;
                return;
            }

            follow.ManualOrbitOffsetDegrees = CameraFollowMath.NormalizeDegrees(
                follow.ManualOrbitOffsetDegrees - e.DeltaX * sensitivity);
            double elevationDelta = e.DeltaY * sensitivity
                * (ClientSettings.InvertMouseYAxis ? -1d : 1d);
            follow.ManualElevationDegrees = Math.Clamp(
                follow.ManualElevationDegrees + elevationDelta,
                -CameraFollowMath.MaxElevationDegrees,
                CameraFollowMath.MaxElevationDegrees);
            e.Handled = true;
        }
    }

    private bool IsLocalPlayerFollowTarget(FollowPlayback follow)
    {
        IClientPlayer? localPlayer = capi.World.Player;
        if (localPlayer is null)
        {
            return false;
        }

        return follow.Rig.TargetType == CameraFollowTargetType.Player
                && string.Equals(
                    follow.Rig.TargetPlayerUid,
                    localPlayer.PlayerUID,
                    StringComparison.Ordinal)
            || ReferenceEquals(ResolveFollowTarget(follow), localPlayer.Entity);
    }

    private int ResolveHotkeyKey(string code, GlKeys fallback)
    {
        try
        {
            return capi.Input.GetHotKeyByCode(code)?.CurrentMapping?.KeyCode ?? (int)fallback;
        }
        catch
        {
            return (int)fallback;
        }
    }

    private static bool KeyDown(bool[] keys, int key)
    {
        return (uint)key < (uint)keys.Length && keys[key];
    }

    private bool TryCreateFollowTargetPose(
        CameraFollowRig rig,
        Entity target,
        out CameraFollowTargetPose pose)
        => TryCreateFollowTargetPoseWithStableView(rig, target, null, null, out pose);

    private bool TryCreateFollowTargetPoseWithStableView(
        CameraFollowRig rig,
        Entity target,
        double? stableYawDegrees,
        double? stablePitchDegrees,
        out CameraFollowTargetPose pose)
    {
        pose = default;
        if (!IsFinite(target.Pos.X)
            || !IsFinite(target.Pos.Y)
            || !IsFinite(target.Pos.Z))
        {
            return false;
        }

        double x;
        double y;
        double z;
        double yawRadians;
        double pitchRadians;
        if (rig.Anchor == CameraFollowAnchor.Head)
        {
            if (!DirectorEntityViewResolver.TryResolveHeadPosition(target, out x, out y, out z))
            {
                return false;
            }
            yawRadians = stableYawDegrees is double stableYaw
                ? stableYaw * RadiansPerDegree
                : DirectorEntityViewResolver.ResolveCameraYaw(target);
            pitchRadians = stablePitchDegrees is double stablePitch
                ? stablePitch * RadiansPerDegree
                : DirectorEntityViewResolver.ResolveCameraPitch(target);
        }
        else
        {
            float centerX = target.SelectionBox is null ? 0f : (target.SelectionBox.X1 + target.SelectionBox.X2) / 2f;
            float centerY = target.SelectionBox is null ? 0.9f : (target.SelectionBox.Y1 + target.SelectionBox.Y2) / 2f;
            float centerZ = target.SelectionBox is null ? 0f : (target.SelectionBox.Z1 + target.SelectionBox.Z2) / 2f;
            x = target.Pos.X + centerX;
            y = target.Pos.Y + centerY;
            z = target.Pos.Z + centerZ;
            yawRadians = DirectorEntityViewResolver.ResolveBodyYaw(target);
            pitchRadians = 0d;
        }

        if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z)
            || !IsFinite(yawRadians) || !IsFinite(pitchRadians))
        {
            return false;
        }

        pose = new CameraFollowTargetPose(
            x,
            y,
            z,
            target.Pos.Dimension,
            yawRadians * DegreesPerRadian,
            pitchRadians * DegreesPerRadian);
        return true;
    }

    private static CameraFollowPose ResolveReplayDelayedPose(
        ReplayFollowExportState previous,
        CameraFollowRig rig,
        double timeSeconds,
        CameraFollowPose desired)
    {
        previous.History.Add(timeSeconds, desired, rig.DelaySeconds);
        return previous.History.TrySample(
            timeSeconds - CameraFollowMath.NormalizeDelay(rig.DelaySeconds),
            out CameraFollowPose delayed)
                ? delayed
                : desired;
    }

    private bool IsFirstPersonFollowTarget(Entity localEntity)
    {
        return (activeFollow is { Rig.ViewMode: CameraFollowViewMode.FirstPerson } follow
                && ReferenceEquals(ResolveFollowTarget(follow), localEntity))
            || (replayFollowExportState is { ViewMode: CameraFollowViewMode.FirstPerson } replayFollow
                && ReferenceEquals(replayFollow.Target, localEntity));
    }

    private void RestoreHiddenFollowEntity()
    {
        if (hiddenFollowEntity is not null)
        {
            hiddenFollowEntity.IsRendered = hiddenFollowEntityWasRendered;
            hiddenFollowEntity = null;
        }
    }

    private static double CurrentFollowOrbitAngle(FollowPlayback follow)
    {
        return CameraFollowMath.NormalizeDegrees(
            follow.Rig.OrbitAngleDegrees
            + follow.ManualOrbitOffsetDegrees
            + follow.ElapsedSeconds * follow.Rig.OrbitSpeedDegreesPerSecond);
    }

    private void AdvancePath(float deltaTime)
    {
        PathPlayback? playback = activePath;
        if (playback is null || IsAutomatedPlaybackPaused || pathCompletionPending)
        {
            return;
        }

        if (float.IsFinite(deltaTime) && deltaTime > 0f)
        {
            double elementRate = DirectorPathElementTimeline.SamplePlaybackRate(
                playback.Path,
                playback.CurrentTimeSeconds,
                playback.Path.PlaybackSpeed);
            playback.ElapsedSeconds += Math.Min(deltaTime, 1f)
                * elementRate;
        }

        CameraPathTime timeline = CameraPathSampler.ResolvePlaybackTime(
            playback.Path.DurationSeconds,
            playback.ElapsedSeconds,
            playback.Path.LoopMode,
            playback.Reverse);
        if (TryEvaluatePathPose(playback.Path, timeline.TimeSeconds, out CameraPreset pose))
        {
            activeCamera = pose;
            playback.CurrentTimeSeconds = timeline.TimeSeconds;
        }

        pathCompletionPending = timeline.Completed;
    }

    private CameraPreset ResolveCollisionSafePose(CameraPreset requested)
    {
        if (DirectorReplayVisualRuntime.Current?.Camera.CameraCollision != true
            || !IsCameraPointBlocked(requested.X, requested.Y, requested.Z, requested.Dimension))
        {
            return requested;
        }

        float yaw = DegreesToRadians(requested.Yaw);
        float pitch = DegreesToRadians(requested.Pitch);
        Vec3f forward = EntityPos.GetViewVector(pitch, yaw);
        (double X, double Y, double Z)[] directions =
        {
            (0d, 1d, 0d),
            (-forward.X, -forward.Y, -forward.Z),
            (1d, 0d, 0d),
            (-1d, 0d, 0d),
            (0d, 0d, 1d),
            (0d, 0d, -1d),
            (0d, -1d, 0d)
        };
        for (double distance = 0.2d; distance <= 2.0001d; distance += 0.2d)
        {
            foreach ((double x, double y, double z) in directions)
            {
                double candidateX = requested.X + x * distance;
                double candidateY = requested.Y + y * distance;
                double candidateZ = requested.Z + z * distance;
                if (IsCameraPointBlocked(
                        candidateX,
                        candidateY,
                        candidateZ,
                        requested.Dimension))
                {
                    continue;
                }
                CameraPreset resolved = requested.DeepClone();
                resolved.X = candidateX;
                resolved.Y = candidateY;
                resolved.Z = candidateZ;
                return resolved;
            }
        }
        return requested;
    }

    private bool IsCameraPointBlocked(double x, double y, double z, int dimension)
    {
        int blockX = (int)Math.Floor(x);
        int blockY = (int)Math.Floor(y);
        int blockZ = (int)Math.Floor(z);
        BlockPos position = new(blockX, blockY, blockZ, dimension);
        Block block = capi.World.BlockAccessor.GetBlock(position, BlockLayersAccess.Solid);
        Cuboidf[] boxes = block.GetCollisionBoxes(capi.World.BlockAccessor, position)
            ?? Array.Empty<Cuboidf>();
        const double margin = 0.055d;
        double localX = x - blockX;
        double localY = y - blockY;
        double localZ = z - blockZ;
        return boxes.Any(box =>
            localX >= box.X1 - margin && localX <= box.X2 + margin
            && localY >= box.Y1 - margin && localY <= box.Y2 + margin
            && localZ >= box.Z1 - margin && localZ <= box.Z2 + margin);
    }

    private void ApplyPathElements(CameraPath path, double timeSeconds, CameraPreset pose)
    {
        if (DirectorPathElementTimeline.TrySample(
                path,
                DirectorPathElementTrackType.LookAt,
                timeSeconds,
                out DirectorPathElementKeyframe lookAt)
            && TryResolvePathLookAtTarget(lookAt, pose.Dimension,
                out double targetX, out double targetY, out double targetZ))
        {
            double dx = targetX - pose.X;
            double dy = targetY - pose.Y;
            double dz = targetZ - pose.Z;
            double horizontal = Math.Sqrt(dx * dx + dz * dz);
            if (horizontal > 0.0000001d || Math.Abs(dy) > 0.0000001d)
            {
                pose.Yaw = (float)(Math.Atan2(-dx, -dz) * DegreesPerRadian
                    + lookAt.YawOffset);
                pose.Pitch = (float)(Math.Atan2(dy, horizontal) * DegreesPerRadian
                    + lookAt.PitchOffset);
            }
        }

        if (DirectorPathElementTimeline.TrySample(
                path,
                DirectorPathElementTrackType.Roll,
                timeSeconds,
                out DirectorPathElementKeyframe roll))
        {
            pose.Roll = roll.Roll;
        }
        if (DirectorPathElementTimeline.TrySample(
                path,
                DirectorPathElementTrackType.Fov,
                timeSeconds,
                out DirectorPathElementKeyframe fov))
        {
            pose.Fov = Math.Clamp(
                fov.Fov,
                DirectorPathElementTimeline.MinFovDegrees,
                DirectorPathElementTimeline.MaxFovDegrees);
        }
        if (DirectorPathElementTimeline.TrySample(
                path,
                DirectorPathElementTrackType.CameraShake,
                timeSeconds,
                out DirectorPathElementKeyframe shake))
        {
            double phaseX = timeSeconds * Math.PI * 2d * shake.FrequencyX;
            double phaseY = timeSeconds * Math.PI * 2d * shake.FrequencyY;
            pose.Yaw += (float)(Math.Sin(phaseX) * shake.AmplitudeX);
            pose.Pitch += (float)(Math.Sin(phaseY + Math.PI / 2d) * shake.AmplitudeY);
        }
    }

    private bool TryResolvePathLookAtTarget(
        DirectorPathElementKeyframe lookAt,
        int cameraDimension,
        out double x,
        out double y,
        out double z)
    {
        x = lookAt.X;
        y = lookAt.Y;
        z = lookAt.Z;
        if (lookAt.TargetEntityId <= 0)
        {
            return lookAt.Dimension == cameraDimension;
        }

        Entity? target = capi.World.GetEntityById(lookAt.TargetEntityId);
        if (target is null || target.Pos.Dimension != cameraDimension)
        {
            return false;
        }
        x = target.Pos.X;
        y = target.Pos.Y;
        z = target.Pos.Z;
        if (lookAt.BodyPart == "head")
        {
            x += target.LocalEyePos.X;
            y += target.LocalEyePos.Y;
            z += target.LocalEyePos.Z;
        }
        else if (lookAt.BodyPart == "body" && target.SelectionBox is Cuboidf box)
        {
            x += (box.X1 + box.X2) * 0.5d;
            y += (box.Y1 + box.Y2) * 0.5d;
            z += (box.Z1 + box.Z2) * 0.5d;
        }
        x += lookAt.OffsetX;
        y += lookAt.OffsetY;
        z += lookAt.OffsetZ;
        return true;
    }

    private void AdvanceSequence(float deltaTime)
    {
        SequencePlayback? playback = activeSequence;
        if (playback is null || IsAutomatedPlaybackPaused || sequenceCompletionPending)
        {
            return;
        }

        if (float.IsFinite(deltaTime) && deltaTime > 0f)
        {
            playback.ElapsedSeconds += Math.Min(deltaTime, 1f);
        }

        if (CameraSequenceSampler.TryResolve(
            playback.Sequence,
            playback.ElapsedSeconds,
            out CameraSequenceTime timeline))
        {
            if (timeline.ShotIndex != playback.CurrentShotIndex)
            {
                playback.CurrentShotIndex = timeline.ShotIndex;
                activeCamera = playback.Cameras[timeline.ShotIndex].DeepClone();
            }

            sequenceCompletionPending = timeline.Completed;
        }
    }

    internal void SetGuiPointerCameraInput(bool enabled)
        => guiPointerCameraInput = enabled && freeCameraActive;

    private bool IsAutomatedPlaybackPaused
        => game.IsPaused || DirectorReplayRuntime.Manager?.IsPaused == true;

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private void StopAllControlledMovement()
    {
        if (capi.World.Player?.Entity is not EntityPlayer entity)
        {
            return;
        }

        entity.Controls?.StopAllMovement();
        entity.MountedOn?.Controls?.StopAllMovement();
    }

    private void ClearControlKey(int key)
    {
        if ((uint)key < (uint)game.KeyboardState.Length)
        {
            game.KeyboardState[key] = false;
        }
    }

    private static float GetTargetCameraDistance(PlayerCamera camera)
    {
        return TargetCameraDistanceField?.GetValue(camera) is float value ? value : camera.Tppcameradistance;
    }

    private static void SetTargetCameraDistance(PlayerCamera camera, float value)
    {
        TargetCameraDistanceField?.SetValue(camera, value);
    }

    private void OnInWorldAction(EnumEntityAction action, bool on, ref EnumHandling handling)
    {
        if (ShouldLockPlayerInput)
        {
            handling = EnumHandling.PreventSubsequent;
        }
    }

    private sealed class PathPlayback
    {
        public PathPlayback(CameraPath path, bool reverse)
        {
            Path = path;
            Reverse = reverse;
        }

        public CameraPath Path { get; }

        public bool Reverse { get; }

        public double ElapsedSeconds { get; set; }

        public double CurrentTimeSeconds { get; set; }
    }

    private sealed class SequencePlayback
    {
        public SequencePlayback(CameraSequence sequence, IReadOnlyList<CameraPreset> cameras)
        {
            Sequence = sequence;
            Cameras = cameras;
        }

        public CameraSequence Sequence { get; }

        public IReadOnlyList<CameraPreset> Cameras { get; }

        public double ElapsedSeconds { get; set; }

        public int CurrentShotIndex { get; set; }
    }

    private sealed class FollowPlayback
    {
        public FollowPlayback(CameraFollowRig rig, long targetEntityId)
        {
            Rig = rig;
            LastTargetEntityId = targetEntityId;
        }

        public CameraFollowRig Rig { get; set; }

        public CameraFollowPoseHistory History { get; } = new();

        public CameraFollowPose LastPose { get; set; }

        public long LastTargetEntityId { get; set; }

        public double ElapsedSeconds { get; set; }

        public double MissingSeconds { get; set; }

        public double ManualOrbitOffsetDegrees { get; set; }

        public double ManualElevationDegrees { get; set; }

        public bool HasStableLocalPlayerView { get; set; }

        public CameraFollowAnchor StableLocalPlayerViewAnchor { get; set; }

        public double LocalPlayerYawDegrees { get; set; }

        public double LocalPlayerPitchDegrees { get; set; }
    }

    private sealed class ReplayFollowExportState
    {
        public ReplayFollowExportState(
            Guid rigId,
            Entity target,
            CameraFollowViewMode viewMode,
            double timeSeconds,
            CameraFollowPose pose)
        {
            RigId = rigId;
            Target = target;
            ViewMode = viewMode;
            TimeSeconds = timeSeconds;
            Pose = pose;
        }

        public Guid RigId { get; }

        public Entity Target { get; set; }

        public CameraFollowViewMode ViewMode { get; set; }

        public double TimeSeconds { get; set; }

        public CameraFollowPose Pose { get; set; }

        public CameraFollowPoseHistory History { get; } = new();
    }

    private sealed class SavedCameraState
    {
        public required Vec3d CameraPos { get; init; }

        public required bool UpdateCameraPos { get; init; }

        public required bool AllowCameraControl { get; init; }

        public required bool AllowCharacterControl { get; init; }

        public required bool ShouldRender2DOverlays { get; init; }

        public required float Fov { get; init; }

        public required float ZNear { get; init; }

        public required float Yaw { get; init; }

        public required float Pitch { get; init; }

        public required float Roll { get; init; }

        public required EnumCameraMode CameraMode { get; init; }

        public required EnumCameraMode? OverrideCameraMode { get; init; }

        public required bool EntityIsRendered { get; init; }

        public required bool TpUseFpAnimations { get; init; }

        public required bool SelfUseFpAnimations { get; init; }

        public required int TppMin { get; init; }

        public required int TppMax { get; init; }

        public required float TppDistance { get; init; }

        public required float TargetTppDistance { get; init; }

        public static SavedCameraState Capture(
            ClientMain game,
            ClientPlayer player,
            bool? allowCameraControlOverride = null,
            bool? allowCharacterControlOverride = null)
        {
            EnumCameraMode? savedOverride = player.OverrideCameraMode;
            EnumCameraMode underlyingMode;
            try
            {
                player.OverrideCameraMode = null;
                underlyingMode = player.CameraMode;
            }
            finally
            {
                player.OverrideCameraMode = savedOverride;
            }

            return new SavedCameraState
            {
                CameraPos = game.EntityPlayer.CameraPos.Clone(),
                UpdateCameraPos = game.MainCamera.UpdateCameraPos,
                AllowCameraControl = allowCameraControlOverride ?? game.AllowCameraControl,
                AllowCharacterControl = allowCharacterControlOverride ?? game.AllowCharacterControl,
                ShouldRender2DOverlays = game.ShouldRender2DOverlays,
                Fov = game.MainCamera.Fov,
                ZNear = game.MainCamera.ZNear,
                Yaw = player.CameraYaw,
                Pitch = player.CameraPitch,
                Roll = player.CameraRoll,
                CameraMode = underlyingMode,
                OverrideCameraMode = savedOverride,
                EntityIsRendered = game.EntityPlayer.IsRendered,
                TpUseFpAnimations = player.Entity.TpAnimManager.UseFpAnmations,
                SelfUseFpAnimations = player.Entity.SelfFpAnimManager.UseFpAnmations,
                TppMin = game.MainCamera.TppCameraDistanceMin,
                TppMax = game.MainCamera.TppCameraDistanceMax,
                TppDistance = game.MainCamera.Tppcameradistance,
                TargetTppDistance = GetTargetCameraDistance(game.MainCamera)
            };
        }

        public void Restore(ClientMain game, ClientPlayer? player)
        {
            game.MainCamera.UpdateCameraPos = UpdateCameraPos;
            game.MainCamera.TppCameraDistanceMin = TppMin;
            game.MainCamera.TppCameraDistanceMax = TppMax;
            game.MainCamera.Tppcameradistance = TppDistance;
            SetTargetCameraDistance(game.MainCamera, TargetTppDistance);
            game.MainCamera.Fov = Fov;
            game.MainCamera.ZNear = ZNear;
            game.AllowCameraControl = AllowCameraControl;
            game.AllowCharacterControl = AllowCharacterControl;
            game.ShouldRender2DOverlays = ShouldRender2DOverlays;
            game.EntityPlayer.CameraPos.Set(CameraPos);

            if (player != null)
            {
                player.Entity.IsRendered = EntityIsRendered;
                player.Entity.TpAnimManager.UseFpAnmations = TpUseFpAnimations;
                player.Entity.SelfFpAnimManager.UseFpAnmations = SelfUseFpAnimations;
                player.OverrideCameraMode = null;
                ForceCameraMode(game.MainCamera, player, CameraMode);
                player.OverrideCameraMode = OverrideCameraMode;
                player.CameraYaw = Yaw;
                player.CameraPitch = Pitch;
                player.CameraRoll = Roll;
                player.Entity.MarkShapeModified();
                player.Entity?.Controls?.StopAllMovement();
            }

            game.Reset3DProjection();
        }
    }
}
