using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using VSDirector.Replay;

namespace VSDirector.Gui;

/// <summary>
/// One-click replay element presets. A preset writes ordinary element tracks, keyframes,
/// camera keyframes, markers or scene visuals at the playhead, so everything it creates
/// stays editable afterwards. The catalog lives in ReplayPresets.Catalog.cs, the
/// recording-analysis presets in ReplayPresets.Smart.cs and the browser in
/// ReplayPresetBrowser.cs.
/// </summary>
internal sealed partial class DirectorWorkspaceHud
{
    private const string PresetFollowGroup = "follow";
    private const string PresetTrackGroup = "track";
    private const string PresetOrbitGroup = "orbit";
    private const string PresetMoveGroup = "move";
    private const string PresetShakeGroup = "shake";
    private const string PresetFovGroup = "fov";
    private const string PresetTimeGroup = "time";
    private const string PresetWorldGroup = "world";
    private const string PresetCinematicGroup = "cinematic";
    private const string PresetSmartGroup = "smart";
    private const string PresetStyleGroup = "style";

    private const DirectorReplayElementInterpolation IHold = DirectorReplayElementInterpolation.Hold;
    private const DirectorReplayElementInterpolation ILinear = DirectorReplayElementInterpolation.Linear;
    private const DirectorReplayElementInterpolation IEase = DirectorReplayElementInterpolation.EaseInOut;
    private const DirectorReplayElementInterpolation IEaseIn = DirectorReplayElementInterpolation.EaseIn;
    private const DirectorReplayElementInterpolation IEaseOut = DirectorReplayElementInterpolation.EaseOut;
    private const DirectorReplayElementInterpolation ISmooth = DirectorReplayElementInterpolation.Smooth;

    /// <summary>What a preset needs before it can run.</summary>
    [Flags]
    private enum PresetNeeds
    {
        None = 0,
        /// <summary>A recorded player (or other actor) to aim at.</summary>
        Player = 1
    }

    private sealed record ReplayPreset(
        string Id,
        string Group,
        System.Func<ReplayPresetContext, Guid?> Apply,
        PresetNeeds Needs = PresetNeeds.None);

    private readonly record struct PresetPoint(double X, double Y, double Z, int Dimension)
    {
        internal PresetPoint Offset(double x, double y, double z) => new(X + x, Y + y, Z + z, Dimension);
    }

    /// <summary>Everything a preset may read about the moment it is applied at.</summary>
    private sealed record ReplayPresetContext(
        DirectorWorkspaceHud Hud,
        DirectorReplayViewportTimeline Timeline,
        double Start,
        double Duration,
        float BaseFov,
        CameraPreset Pose,
        PresetPoint Look,
        DirectorReplayActorPathSource? Player,
        PresetPoint? PlayerHead,
        double PlayerYaw,
        DirectorReplayDocument? Document)
    {
        /// <summary>Horizontal forward of the current camera.</summary>
        internal (double X, double Z) Forward
            => (-Math.Sin(Pose.Yaw * Math.PI / 180d), -Math.Cos(Pose.Yaw * Math.PI / 180d));

        /// <summary>Horizontal right of the current camera.</summary>
        internal (double X, double Z) Right
            => (Math.Cos(Pose.Yaw * Math.PI / 180d), -Math.Sin(Pose.Yaw * Math.PI / 180d));

        internal PresetPoint CameraPoint => new(Pose.X, Pose.Y, Pose.Z, Pose.Dimension);

        /// <summary>The player's head, or the looked-at point when there is no player.</summary>
        internal PresetPoint Subject => PlayerHead ?? Look;
    }

    private readonly record struct ShakeStep(double At, double Scale,
        DirectorReplayElementInterpolation Interpolation = DirectorReplayElementInterpolation.Linear);

    private readonly record struct FovStep(double At, float Fov,
        DirectorReplayElementInterpolation Interpolation = DirectorReplayElementInterpolation.EaseInOut);

    private readonly record struct RateStep(double At, double Rate,
        DirectorReplayElementInterpolation Interpolation = DirectorReplayElementInterpolation.EaseInOut);

    /// <summary>Camera keyframe relative to the pose at the playhead (blocks and degrees).</summary>
    private readonly record struct MoveStep(
        double At,
        double Right = 0d,
        double Up = 0d,
        double Forward = 0d,
        float Yaw = 0f,
        float Pitch = 0f,
        float Roll = 0f,
        CameraPathInterpolation Interpolation = CameraPathInterpolation.EaseInOut);

    private readonly record struct OrbitStep(double At, double Yaw, double Pitch, double Distance,
        float Roll = 0f, DirectorReplayElementInterpolation Interpolation = DirectorReplayElementInterpolation.Linear);

    private readonly record struct GradeStep(double At, float Sepia = 0f, float Contrast = 0f, float Vignette = 0f,
        float Bloom = 0f, float Glitch = 0f, float Fade = 0f,
        DirectorReplayElementInterpolation Interpolation = DirectorReplayElementInterpolation.EaseInOut);

    private static string PresetName(string id) => Lang.Get("vsdirector:replay-preset-" + id);

    private static string PresetHelp(string id) => Lang.Get("vsdirector:replay-preset-" + id + "-help");

    private static string PresetGroupName(string group) => Lang.Get("vsdirector:replay-preset-group-" + group);

    private bool ApplyReplayPreset(ReplayPreset preset)
    {
        CloseReplayFloatingEditor();
        double duration = owner.ReplayDurationSeconds;
        if (!owner.IsReplayPlaying || duration <= 1e-9d)
        {
            return true;
        }
        CameraPreset pose = owner.TryCaptureReplayCameraKeyframe(PresetName(preset.Id),
                out CameraPreset captured, out _)
            ? captured
            : new CameraPreset { Fov = CameraFollowMath.DefaultFov };
        // Presets move along the view's forward/right and clamp pitch to +-89.5, so they
        // need the -90..90 form (capture already gives it; this guards other sources).
        CameraViewAngles.Normalize(pose);
        float baseFov = float.IsFinite(pose.Fov) && pose.Fov > 1f ? pose.Fov : CameraFollowMath.DefaultFov;
        double start = ReplayTimelineTickAt(Math.Clamp(owner.ReplayTimeSeconds, 0d, duration))
            / ReplayTimelineTicksPerSecond;
        DirectorReplayActorPathSource? player = PreferredReplayPlayer();
        PresetPoint? head = null;
        double playerYaw = pose.Yaw;
        if (player is DirectorReplayActorPathSource source
            && owner.TryResolveReplayActor(source.ActorKey, out _, out Entity? entity)
            && entity is not null
            && owner.TryGetReplayTrackingTargetPose(entity, "head", out DirectorReplayTrackedTargetPose headPose))
        {
            head = new PresetPoint(headPose.X, headPose.Y, headPose.Z, headPose.Dimension);
            playerYaw = headPose.YawDegrees;
        }
        if (preset.Needs.HasFlag(PresetNeeds.Player) && (player is null || head is null))
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-preset-no-player"));
            return true;
        }

        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        var context = new ReplayPresetContext(this, timeline, start, duration, baseFov, pose,
            LookPoint(pose), player, head, playerYaw, owner.ActiveReplayDocument);
        Guid? select;
        try
        {
            select = preset.Apply(context);
        }
        catch (Exception exception)
        {
            capi.Logger.Warning("VS Director: preset {0} failed: {1}", preset.Id, exception);
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-preset-failed", PresetName(preset.Id)));
            return true;
        }
        presetUsage.RecordUse(preset.Id);
        if (!CommitReplayViewportTimeline(timeline))
        {
            return true;
        }
        if (select is Guid trackId && trackId != Guid.Empty)
        {
            return SelectReplayTimelineElementTrack(trackId);
        }
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    /// <summary>The block the camera looks at (up to 160 blocks), else 20 blocks ahead.</summary>
    private PresetPoint LookPoint(CameraPreset pose)
    {
        double yaw = pose.Yaw * Math.PI / 180d;
        double pitch = pose.Pitch * Math.PI / 180d;
        double fx = -Math.Sin(yaw) * Math.Cos(pitch);
        double fy = Math.Sin(pitch);
        double fz = -Math.Cos(yaw) * Math.Cos(pitch);
        IBlockAccessor blocks = capi.World.BlockAccessor;
        for (double distance = 1d; distance <= 160d; distance += 0.5d)
        {
            double x = pose.X + fx * distance;
            double y = pose.Y + fy * distance;
            double z = pose.Z + fz * distance;
            BlockPos position = new((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), pose.Dimension);
            Block block = blocks.GetBlock(position, BlockLayersAccess.Solid);
            if (block.Id != 0 && block.CollisionBoxes is { Length: > 0 })
            {
                return new PresetPoint(x, y, z, pose.Dimension);
            }
        }
        return new PresetPoint(pose.X + fx * 20d, pose.Y + fy * 20d, pose.Z + fz * 20d, pose.Dimension);
    }

    private static double PresetTime(ReplayPresetContext c, double offset)
        => ReplayTimelineTickAt(Math.Clamp(c.Start + offset, 0d, c.Duration)) / ReplayTimelineTicksPerSecond;

    /// <summary>
    /// Shake, FOV, speed, freeze, grade and title presets add to the first enabled track
    /// of their type, so the timeline does not fill with one row per preset.
    /// </summary>
    private static DirectorReplayElementTrack PresetSharedTrack(ReplayPresetContext c,
        DirectorReplayElementTrackType type)
    {
        DirectorReplayElementTrack? track = c.Timeline.Tracks.FirstOrDefault(item =>
            item.Enabled && item.TrackType == type);
        if (track is null)
        {
            track = new DirectorReplayElementTrack { TrackType = type };
            c.Timeline.Tracks.Add(track);
        }
        return track;
    }

    private static DirectorReplayElementTrack PresetNewTrack(ReplayPresetContext c,
        DirectorReplayElementTrackType type, string id, string? suffix = null)
    {
        var track = new DirectorReplayElementTrack
        {
            TrackType = type,
            Name = PresetName(id) + (suffix is null ? string.Empty : " · " + suffix)
        };
        c.Timeline.Tracks.Add(track);
        return track;
    }

    private static void AddPresetKeyframe(ReplayPresetContext c, DirectorReplayElementTrack track,
        DirectorReplayElementKeyframe keyframe)
    {
        keyframe.TrackId = track.Id;
        keyframe.TrackType = track.TrackType;
        int tick = ReplayTimelineTickAt(keyframe.TimeSeconds);
        c.Timeline.ElementKeyframes.RemoveAll(item =>
            item.TrackId == track.Id && ReplayTimelineTickAt(item.TimeSeconds) == tick);
        c.Timeline.ElementKeyframes.Add(keyframe);
    }

    // ----- Follow -----------------------------------------------------------------

    private static DirectorReplayElementKeyframe NewFollowKeyframe(DirectorReplayActorPathSource actor) => new()
    {
        InterpolationToNext = DirectorReplayElementInterpolation.Hold,
        FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.Start,
        EntityActorKey = actor.ActorKey,
        EntityId = actor.EntityId,
        FollowViewMode = CameraFollowViewMode.ThirdPerson,
        FollowMotionMode = CameraFollowMotionMode.Smooth,
        FollowAnchor = CameraFollowAnchor.Head,
        FollowDistance = CameraFollowMath.DefaultDistance,
        FollowHeight = CameraFollowMath.DefaultHeight,
        FollowSmoothTimeSeconds = CameraFollowMath.DefaultSmoothTimeSeconds,
        FollowDelaySeconds = CameraFollowMath.DefaultDelaySeconds,
        FollowFov = CameraFollowMath.DefaultFov
    };

    /// <summary>
    /// Adds one follow interval [at, at + length) to <paramref name="track"/>. With an
    /// end configuration and a blend curve the rig animates between the two keyframes.
    /// </summary>
    private static void AddFollowInterval(ReplayPresetContext c, DirectorReplayElementTrack track,
        DirectorReplayActorPathSource actor, double at, double length,
        Action<DirectorReplayElementKeyframe> configure,
        Action<DirectorReplayElementKeyframe>? configureEnd = null,
        DirectorReplayElementInterpolation blend = DirectorReplayElementInterpolation.Hold)
    {
        double start = PresetTime(c, at);
        double end = PresetTime(c, at + length);
        if (end - start < 1d / ReplayTimelineTicksPerSecond)
        {
            return;
        }
        DirectorReplayElementKeyframe first = NewFollowKeyframe(actor);
        first.TimeSeconds = start;
        configure(first);
        first.FollowBlend = configureEnd is null ? DirectorReplayElementInterpolation.Hold : blend;
        DirectorReplayElementKeyframe second = first.DeepClone();
        second.Id = Guid.NewGuid();
        second.TimeSeconds = end;
        second.FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.End;
        second.FollowBlend = DirectorReplayElementInterpolation.Hold;
        configureEnd?.Invoke(second);
        AddPresetKeyframe(c, track, first);
        AddPresetKeyframe(c, track, second);
    }

    private static Guid? FollowPreset(ReplayPresetContext c, string id,
        Action<DirectorReplayElementKeyframe> configure, double length = 8d,
        Action<DirectorReplayElementKeyframe>? configureEnd = null,
        DirectorReplayElementInterpolation blend = DirectorReplayElementInterpolation.EaseInOut,
        double at = 0d)
    {
        if (c.Player is not DirectorReplayActorPathSource player)
        {
            return null;
        }
        DirectorReplayElementTrack track = PresetNewTrack(c, DirectorReplayElementTrackType.FollowEntity, id,
            PlayerLabel(player));
        double startOffset = at;
        if (PresetTime(c, at + length) - PresetTime(c, at) < 1d / ReplayTimelineTicksPerSecond)
        {
            // At the very end of the replay: put the interval before the playhead instead.
            startOffset = -Math.Min(length, c.Start);
        }
        AddFollowInterval(c, track, player, startOffset, length, configure, configureEnd, blend);
        return track.Id;
    }

    // ----- Camera keyframes, orbits and tracking ---------------------------------------

    /// <summary>
    /// Camera keyframes relative to the pose at the playhead. The row ends shortly after
    /// the last step so the camera is released again.
    /// </summary>
    private static Guid? MovePreset(ReplayPresetContext c, string id, double holdAfter, params MoveStep[] steps)
    {
        if (steps.Length == 0)
        {
            return null;
        }
        DirectorReplayElementTrack track = PresetNewTrack(c, DirectorReplayElementTrackType.Camera, id);
        CameraPreset basePose = c.Pose;
        double yaw = basePose.Yaw * Math.PI / 180d;
        double pitch = basePose.Pitch * Math.PI / 180d;
        (double X, double Y, double Z) forward = (-Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch));
        (double X, double Z) right = (Math.Cos(yaw), -Math.Sin(yaw));
        List<(MoveStep Step, CameraPreset Pose)> poses = steps
            .Select(step =>
            {
                CameraPreset pose = basePose.DeepClone();
                pose.X += right.X * step.Right + forward.X * step.Forward;
                pose.Y += step.Up + forward.Y * step.Forward;
                pose.Z += right.Z * step.Right + forward.Z * step.Forward;
                pose.Yaw = basePose.Yaw + step.Yaw;
                pose.Pitch = Math.Clamp(basePose.Pitch + step.Pitch, -89.5f, 89.5f);
                pose.Roll = basePose.Roll + step.Roll;
                return (step, pose);
            })
            .ToList();
        for (int index = 0; index < poses.Count; index++)
        {
            AddPresetCameraClip(c, track, id, poses[index].Step.At, poses[index].Pose,
                poses[index].Step.Interpolation, endsShot: false);
        }
        AddPresetCameraClip(c, track, id, steps[^1].At + Math.Max(1d / ReplayTimelineTicksPerSecond, holdAfter),
            poses[^1].Pose, CameraPathInterpolation.Step, endsShot: true);
        return track.Id;
    }

    /// <summary>Camera keyframes from absolute poses.</summary>
    private static Guid? PosePreset(ReplayPresetContext c, string id, double holdAfter,
        params (double At, CameraPreset Pose, CameraPathInterpolation Interpolation)[] steps)
    {
        if (steps.Length == 0)
        {
            return null;
        }
        DirectorReplayElementTrack track = PresetNewTrack(c, DirectorReplayElementTrackType.Camera, id);
        foreach ((double at, CameraPreset pose, CameraPathInterpolation interpolation) in steps)
        {
            AddPresetCameraClip(c, track, id, at, pose, interpolation, endsShot: false);
        }
        AddPresetCameraClip(c, track, id, steps[^1].At + Math.Max(1d / ReplayTimelineTicksPerSecond, holdAfter),
            steps[^1].Pose, CameraPathInterpolation.Step, endsShot: true);
        return track.Id;
    }

    private static void AddPresetCameraClip(ReplayPresetContext c, DirectorReplayElementTrack track, string id,
        double at, CameraPreset pose, CameraPathInterpolation interpolation, bool endsShot)
    {
        double time = PresetTime(c, at);
        int tick = ReplayTimelineTickAt(time);
        c.Timeline.Clips.RemoveAll(clip => clip.TrackId == track.Id && ReplayTimelineTickAt(clip.TimelineStartSeconds) == tick);
        CameraPreset stored = pose.DeepClone();
        stored.Id = Guid.Empty;
        stored.Name = PresetName(id);
        c.Timeline.Clips.Add(new DirectorReplayViewportClip
        {
            Name = stored.Name,
            SourceType = DirectorReplayViewportSourceType.StaticCamera,
            SourceId = Guid.Empty,
            CameraPose = stored,
            CameraInterpolationToNext = interpolation,
            TrackId = track.Id,
            TimelineStartSeconds = time,
            DurationSeconds = 1d,
            PlaybackRate = 1d,
            TrackOrder = c.Timeline.Clips.Count,
            Enabled = true,
            EndsShot = endsShot
        });
    }

    /// <summary>A camera pose at <paramref name="from"/> looking at <paramref name="to"/>.</summary>
    private static CameraPreset LookPose(ReplayPresetContext c, PresetPoint from, PresetPoint to, float roll = 0f)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double dz = to.Z - from.Z;
        double horizontal = Math.Sqrt(dx * dx + dz * dz);
        CameraPreset pose = c.Pose.DeepClone();
        pose.X = from.X;
        pose.Y = from.Y;
        pose.Z = from.Z;
        pose.Dimension = from.Dimension;
        if (horizontal > 0.0001d || Math.Abs(dy) > 0.0001d)
        {
            pose.Yaw = (float)(Math.Atan2(-dx, -dz) * 180d / Math.PI);
            pose.Pitch = (float)Math.Clamp(Math.Atan2(dy, Math.Max(0.0001d, horizontal)) * 180d / Math.PI, -89.5d, 89.5d);
        }
        pose.Roll = roll;
        return pose;
    }

    private static Guid? OrbitPreset(ReplayPresetContext c, string id, PresetPoint center, params OrbitStep[] steps)
    {
        DirectorReplayElementTrack track = PresetNewTrack(c, DirectorReplayElementTrackType.CameraOrbit, id);
        foreach (OrbitStep step in steps)
        {
            AddPresetKeyframe(c, track, new DirectorReplayElementKeyframe
            {
                TimeSeconds = PresetTime(c, step.At),
                InterpolationToNext = step.Interpolation,
                X = center.X,
                Y = center.Y,
                Z = center.Z,
                Dimension = center.Dimension,
                Yaw = (float)step.Yaw,
                Pitch = (float)step.Pitch,
                Distance = Math.Max(0.5d, step.Distance),
                Roll = step.Roll
            });
        }
        EndPresetTrack(c, track, steps[^1].At + 0.05d);
        return track.Id;
    }

    /// <summary>Adds the "element ends here" keyframe (a copy of the last one).</summary>
    private static void EndPresetTrack(ReplayPresetContext c, DirectorReplayElementTrack track, double at)
    {
        DirectorReplayElementKeyframe? last = c.Timeline.ElementKeyframes
            .Where(item => item.TrackId == track.Id)
            .OrderBy(item => item.TimeSeconds)
            .LastOrDefault();
        if (last is null)
        {
            return;
        }
        DirectorReplayElementKeyframe end = last.DeepClone();
        end.Id = Guid.NewGuid();
        end.TimeSeconds = Math.Max(PresetTime(c, at), last.TimeSeconds + 1d / ReplayTimelineTicksPerSecond);
        if (end.TimeSeconds > c.Duration)
        {
            return;
        }
        end.InterpolationToNext = DirectorReplayElementInterpolation.Hold;
        end.EndsElement = true;
        AddPresetKeyframe(c, track, end);
    }

    /// <summary>A tripod/over-shoulder tracking keyframe.</summary>
    private static DirectorReplayElementKeyframe LookKey(double time, DirectorReplayActorPathSource target,
        DirectorReplayTrackMode mode, PresetPoint camera,
        DirectorReplayElementInterpolation interpolation = DirectorReplayElementInterpolation.Linear) => new()
    {
        TimeSeconds = time,
        InterpolationToNext = interpolation,
        TrackMode = mode,
        EntityActorKey = target.ActorKey,
        EntityId = target.EntityId,
        BodyPart = "head",
        X = camera.X,
        Y = camera.Y,
        Z = camera.Z,
        Dimension = camera.Dimension,
        ViewOffsetX = 0.55d,
        ViewOffsetY = 0.3d,
        ViewOffsetZ = 1.8d,
        TrackFrameSize = 0.45d
    };

    /// <summary>
    /// Tripod shot on the player: the camera moves through the given positions (linear
    /// between them, or holds when only one is given) and always looks at the player.
    /// </summary>
    private static Guid? TripodPreset(ReplayPresetContext c, string id, double length,
        Action<DirectorReplayElementKeyframe>? configure, params (double At, PresetPoint Camera)[] positions)
    {
        if (c.Player is not DirectorReplayActorPathSource player || positions.Length == 0)
        {
            return null;
        }
        DirectorReplayElementTrack track = PresetNewTrack(c, DirectorReplayElementTrackType.TrackEntity, id, PlayerLabel(player));
        foreach ((double at, PresetPoint camera) in positions)
        {
            DirectorReplayElementKeyframe key = LookKey(PresetTime(c, at), player, DirectorReplayTrackMode.Tripod, camera);
            configure?.Invoke(key);
            AddPresetKeyframe(c, track, key);
        }
        EndPresetTrack(c, track, length);
        return track.Id;
    }

    // ----- Shake, FOV, speed, time effects, grade, titles -------------------------------

    private static Guid? ShakePreset(ReplayPresetContext c, string id,
        float frequencyX, float amplitudeX, float frequencyY, float amplitudeY, params ShakeStep[] steps)
        => ShakePreset(c, frequencyX, amplitudeX, frequencyY, amplitudeY, 0f, steps);

    private static Guid? ShakePreset(ReplayPresetContext c,
        float frequencyX, float amplitudeX, float frequencyY, float amplitudeY, float roll, params ShakeStep[] steps)
    {
        DirectorReplayElementTrack track = PresetSharedTrack(c, DirectorReplayElementTrackType.CameraShake);
        for (int index = 0; index < steps.Length; index++)
        {
            ShakeStep step = steps[index];
            AddPresetKeyframe(c, track, new DirectorReplayElementKeyframe
            {
                TimeSeconds = PresetTime(c, step.At),
                InterpolationToNext = step.Interpolation,
                SplitParameters = true,
                FrequencyX = frequencyX,
                FrequencyY = frequencyY,
                AmplitudeX = (float)(amplitudeX * step.Scale),
                AmplitudeY = (float)(amplitudeY * step.Scale),
                Roll = (float)(roll * step.Scale),
                // A zero-amplitude last step releases the camera afterwards.
                EndsElement = index == steps.Length - 1 && step.Scale <= 0.0001d
            });
        }
        return track.Id;
    }

    private static Guid? FovPreset(ReplayPresetContext c, bool endAtLast, params FovStep[] steps)
    {
        DirectorReplayElementTrack track = PresetSharedTrack(c, DirectorReplayElementTrackType.Fov);
        for (int index = 0; index < steps.Length; index++)
        {
            FovStep step = steps[index];
            AddPresetKeyframe(c, track, new DirectorReplayElementKeyframe
            {
                TimeSeconds = PresetTime(c, step.At),
                InterpolationToNext = step.Interpolation,
                Fov = Math.Clamp(step.Fov, DirectorReplayElementTimeline.MinFovDegrees,
                    DirectorReplayElementTimeline.MaxFovDegrees),
                EndsElement = endAtLast && index == steps.Length - 1
            });
        }
        return track.Id;
    }

    /// <summary>Playback-rate keyframes; the last step releases the speed row.</summary>
    private static Guid? SpeedPreset(ReplayPresetContext c, params RateStep[] steps)
    {
        DirectorReplayElementTrack track = PresetSharedTrack(c, DirectorReplayElementTrackType.Speed);
        for (int index = 0; index < steps.Length; index++)
        {
            RateStep step = steps[index];
            AddPresetKeyframe(c, track, new DirectorReplayElementKeyframe
            {
                TimeSeconds = PresetTime(c, step.At),
                InterpolationToNext = step.Interpolation,
                TickRate = (float)(Math.Clamp(step.Rate, 0.05d, 20d) * DirectorReplayElementTimeline.TicksPerSecond),
                EndsElement = index == steps.Length - 1
            });
        }
        return track.Id;
    }

    /// <summary>A freeze/rewind/stutter window [at, until).</summary>
    private static Guid? TimeEffectPreset(ReplayPresetContext c, double at, double until,
        DirectorReplayTimeEffect effect, double seconds = 3d, double rate = 1d, bool cameraContinues = false,
        int delayTicks = 0)
    {
        DirectorReplayElementTrack track = PresetSharedTrack(c, DirectorReplayElementTrackType.Freeze);
        AddPresetKeyframe(c, track, new DirectorReplayElementKeyframe
        {
            TimeSeconds = PresetTime(c, at),
            InterpolationToNext = DirectorReplayElementInterpolation.Hold,
            Frozen = true,
            TimeEffect = effect,
            TimeEffectSeconds = seconds,
            TimeEffectRate = rate,
            FreezeCameraContinues = cameraContinues,
            FreezeDelayTicks = Math.Clamp(delayTicks, 0, DirectorReplayElementTimeline.MaxFreezeDelayTicks)
        });
        AddPresetKeyframe(c, track, new DirectorReplayElementKeyframe
        {
            TimeSeconds = PresetTime(c, until),
            InterpolationToNext = DirectorReplayElementInterpolation.Hold,
            Frozen = false
        });
        return track.Id;
    }

    /// <summary>Colour grade keyframes; a final neutral step ends the grade.</summary>
    private static Guid? GradePreset(ReplayPresetContext c, bool endAtLast, params GradeStep[] steps)
    {
        DirectorReplayElementTrack track = PresetSharedTrack(c, DirectorReplayElementTrackType.ColorGrade);
        for (int index = 0; index < steps.Length; index++)
        {
            GradeStep step = steps[index];
            AddPresetKeyframe(c, track, new DirectorReplayElementKeyframe
            {
                TimeSeconds = PresetTime(c, step.At),
                InterpolationToNext = step.Interpolation,
                GradeSepia = step.Sepia,
                GradeContrast = step.Contrast,
                GradeVignette = step.Vignette,
                GradeBloom = step.Bloom,
                GradeGlitch = step.Glitch,
                GradeFade = step.Fade,
                EndsElement = endAtLast && index == steps.Length - 1
            });
        }
        return track.Id;
    }

    private static Guid? TitlePreset(ReplayPresetContext c, double at, string text, string subText, double duration,
        DirectorReplayTitlePosition position)
    {
        DirectorReplayElementTrack track = PresetSharedTrack(c, DirectorReplayElementTrackType.Title);
        AddPresetKeyframe(c, track, new DirectorReplayElementKeyframe
        {
            TimeSeconds = PresetTime(c, at),
            InterpolationToNext = DirectorReplayElementInterpolation.Hold,
            Text = text,
            SubText = subText,
            TitleDurationSeconds = duration,
            TitlePosition = position
        });
        return track.Id;
    }

    /// <summary>Time-of-day keyframes in game ticks (0 = midnight, 12000 = noon).</summary>
    private static Guid? TimeOfDayPreset(ReplayPresetContext c, params (double At, int Tick, DirectorReplayElementInterpolation Interpolation)[] steps)
    {
        DirectorReplayElementTrack track = PresetSharedTrack(c, DirectorReplayElementTrackType.TimeOfDay);
        foreach ((double at, int tick, DirectorReplayElementInterpolation interpolation) in steps)
        {
            AddPresetKeyframe(c, track, new DirectorReplayElementKeyframe
            {
                TimeSeconds = PresetTime(c, at),
                InterpolationToNext = interpolation,
                TimeOfDayTick = Math.Clamp(tick, 0, 24000)
            });
        }
        return track.Id;
    }

    /// <summary>Timelapse segment: [at, at + span) of replay plays back in <paramref name="playSeconds"/>.</summary>
    private static Guid? TimelapsePreset(ReplayPresetContext c, string id, double at, double span, double playSeconds)
    {
        DirectorReplayElementTrack track = PresetSharedTrack(c, DirectorReplayElementTrackType.Timelapse);
        double start = PresetTime(c, at);
        double end = PresetTime(c, at + span);
        if (end - start < 1d / ReplayTimelineTicksPerSecond)
        {
            return null;
        }
        Guid pair = Guid.NewGuid();
        AddPresetKeyframe(c, track, new DirectorReplayElementKeyframe
        {
            TimeSeconds = start,
            InterpolationToNext = DirectorReplayElementInterpolation.Linear,
            TimelapseRole = DirectorReplayTimelapseKeyframeRole.Start,
            TimelapsePairId = pair,
            TimelapseTick = ReplayTimelineTickAt(start),
            TimelapseDurationSeconds = playSeconds
        });
        AddPresetKeyframe(c, track, new DirectorReplayElementKeyframe
        {
            TimeSeconds = end,
            InterpolationToNext = DirectorReplayElementInterpolation.Linear,
            TimelapseRole = DirectorReplayTimelapseKeyframeRole.End,
            TimelapsePairId = pair,
            TimelapseTick = ReplayTimelineTickAt(end),
            TimelapseDurationSeconds = playSeconds
        });
        return track.Id;
    }

    // ----- Players --------------------------------------------------------------------

    private static string PlayerLabel(DirectorReplayActorPathSource source)
        => source.DisplayName.Length > 0 ? source.DisplayName
            : source.PlayerUid.Length > 0 ? source.PlayerUid
            : source.ActorKey;

    /// <summary>Replay players (falls back to every actor when no player was recorded).</summary>
    private IReadOnlyList<DirectorReplayActorPathSource> ReplayPlayerSources()
    {
        IReadOnlyList<DirectorReplayActorPathSource> sources = owner.ReplayActorPathSources;
        var players = sources.Where(source => source.IsPlayer).ToList();
        return (players.Count > 0 ? players : sources.ToList())
            .OrderBy(PlayerLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The player a preset targets: the actor of the selected
    /// element keyframe, else this client's own player, else the first recorded player.
    /// </summary>
    private DirectorReplayActorPathSource? PreferredReplayPlayer()
    {
        IReadOnlyList<DirectorReplayActorPathSource> sources = owner.ReplayActorPathSources;
        if (sources.Count == 0)
        {
            return null;
        }
        string selectedKey = SelectedReplayElementKeyframe()?.EntityActorKey ?? string.Empty;
        if (selectedKey.Length > 0)
        {
            foreach (DirectorReplayActorPathSource source in sources)
            {
                if (source.ActorKey == selectedKey) return source;
            }
        }
        string uid = capi.World.Player?.PlayerUID ?? string.Empty;
        DirectorReplayActorPathSource? firstPlayer = null;
        foreach (DirectorReplayActorPathSource source in sources)
        {
            if (!source.IsPlayer) continue;
            if (uid.Length > 0 && source.PlayerUid == uid) return source;
            firstPlayer ??= source;
        }
        return firstPlayer ?? sources[0];
    }

    private bool OpenReplayPlayerPicker()
    {
        IReadOnlyList<DirectorReplayActorPathSource> players = ReplayPlayerSources();
        if (players.Count == 0)
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-preset-no-player"));
            return true;
        }
        if (players.Count == 1)
        {
            return MoveCameraToReplayPlayer(players[0].ActorKey);
        }
        ShowReplayFloatingEditor(ReplayFloatingEditorKind.PlayerPicker);
        return true;
    }

    private bool ToggleReplayShotCamera()
    {
        _ = owner.TryToggleReplayTimelineCamera(out _);
        ComposeTimeline();
        return true;
    }

    private bool MoveCameraToReplayPlayer(string actorKey)
    {
        if (replayFloatingEditorKind == ReplayFloatingEditorKind.PlayerPicker)
        {
            CloseReplayFloatingEditor();
        }
        _ = owner.TryMoveCameraToReplayActor(actorKey, out _);
        return true;
    }

    private void RmlReplayPlayerPicker(DirectorImGuiForm form)
        => form.ChoiceList(ReplayPlayerSources()
            .Select(source =>
            {
                string key = source.ActorKey;
                return new DirectorImGuiChoice("player-" + key, PlayerLabel(source),
                    () => MoveCameraToReplayPlayer(key), "focus-2");
            })
            .ToArray());

    /// <summary>Player-by-name picker for follow and tracking keyframes.</summary>
    private void RmlReplayPlayerSelect(DirectorImGuiForm form)
        => RmlReplayActorSelect(form, "entityActorKey", "entityId", "replay-element-player", allowNone: false);

    /// <summary>Second actor for two-shots and over-the-shoulder tracking.</summary>
    private void RmlReplaySecondaryPlayerSelect(DirectorImGuiForm form)
        => RmlReplayActorSelect(form, "secondaryActorKey", "secondaryEntityId", "replay-track-secondary", allowNone: true);

    private void RmlReplayActorSelect(DirectorImGuiForm form, string keyField, string idField, string label, bool allowNone)
    {
        IReadOnlyList<DirectorReplayActorPathSource> players = allowNone
            ? owner.ReplayActorPathSources.OrderByDescending(item => item.IsPlayer).ThenBy(PlayerLabel).ToList()
            : ReplayPlayerSources();
        if (players.Count == 0)
        {
            return;
        }
        string current = ReplayElementField(keyField);
        var options = new List<(string Value, string Name)>();
        if (allowNone)
        {
            options.Add((string.Empty, Lang.Get("vsdirector:replay-track-secondary-none")));
        }
        else if (!players.Any(player => player.ActorKey == current))
        {
            options.Add((current, Lang.Get("vsdirector:replay-element-player-other")));
        }
        options.AddRange(players.Select(player => (player.ActorKey, PlayerLabel(player))));
        form.Select(RmlLabel(label), current, options, value =>
        {
            if (allowNone && string.IsNullOrEmpty(value))
            {
                replayElementFieldValues[keyField] = string.Empty;
                replayElementFieldValues[idField] = "0";
                return;
            }
            DirectorReplayActorPathSource? match = players
                .Select(player => (DirectorReplayActorPathSource?)player)
                .FirstOrDefault(player => player?.ActorKey == value);
            if (match is not DirectorReplayActorPathSource chosen || string.IsNullOrEmpty(chosen.ActorKey))
            {
                return;
            }
            replayElementFieldValues[keyField] = chosen.ActorKey;
            replayElementFieldValues[idField] = chosen.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!allowNone) ApplySelectedReplayElementKeyframe();
        });
    }
}
