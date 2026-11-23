using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using VSDirector.Replay;

namespace VSDirector.Gui;

/// <summary>The replay preset library. Times are seconds from the playhead.</summary>
internal sealed partial class DirectorWorkspaceHud
{
    private static readonly string[] PresetGroupOrder =
    {
        PresetFollowGroup, PresetTrackGroup, PresetOrbitGroup, PresetMoveGroup, PresetShakeGroup, PresetFovGroup,
        PresetTimeGroup, PresetWorldGroup, PresetCinematicGroup, PresetSmartGroup, PresetStyleGroup
    };

    private static string PresetGroupIcon(string group) => group switch
    {
        PresetFollowGroup => "walk",
        PresetTrackGroup => "focus-2",
        PresetOrbitGroup => "rotate-360",
        PresetMoveGroup => "route",
        PresetShakeGroup => "camera-rotate",
        PresetFovGroup => "eye",
        PresetTimeGroup => "gauge",
        PresetWorldGroup => "sun",
        PresetCinematicGroup => "movie",
        PresetSmartGroup => "robot",
        PresetStyleGroup => "palette",
        _ => "bookmark"
    };

    private static IReadOnlyList<ReplayPreset>? presetCatalog;

    private static IReadOnlyList<ReplayPreset> ReplayPresets() => presetCatalog ??= BuildPresetCatalog();

    private static IReadOnlyList<ReplayPreset> BuildPresetCatalog()
    {
        var list = new List<ReplayPreset>();
        // Names and descriptions are the lang keys replay-preset-<id> and replay-preset-<id>-help.
        void Add(string id, string group, System.Func<ReplayPresetContext, Guid?> apply,
            PresetNeeds needs = PresetNeeds.None)
            => list.Add(new ReplayPreset(id, group, apply, needs));
        void Follow(string id, Action<DirectorReplayElementKeyframe> configure, double length = 8d)
            => Add(id, PresetFollowGroup, c => FollowPreset(c, id, configure, length), PresetNeeds.Player);

        // ----- Follow rigs ---------------------------------------------------------
        Follow("follow-shoulder", k =>
        {
            k.FollowDistance = 2.2d; k.FollowHeight = 0.35d; k.FollowOffsetX = 0.6d;
            k.FollowSmoothTimeSeconds = 0.25d; k.FollowFov = 65f;
        });
        Follow("follow-chase", k =>
        {
            k.FollowDistance = 5d; k.FollowHeight = 1.5d; k.FollowSmoothTimeSeconds = 0.5d; k.FollowFov = 70f;
        });
        Follow("follow-wide", k =>
        {
            k.FollowDistance = 14d; k.FollowHeight = 5d; k.FollowSmoothTimeSeconds = 1.2d; k.FollowFov = 55f;
        });
        Follow("follow-pov", k =>
        {
            k.FollowViewMode = CameraFollowViewMode.FirstPerson;
            k.FollowMotionMode = CameraFollowMotionMode.Immediate;
            k.FollowFov = 80f;
        });
        Follow("follow-front", k =>
        {
            k.FollowDistance = 3d; k.FollowHeight = 0.2d; k.FollowOrbitAngleDegrees = 180d;
            k.FollowSmoothTimeSeconds = 0.4d; k.FollowFov = 60f;
        });
        Follow("follow-orbit", k =>
        {
            k.FollowDistance = 6d; k.FollowHeight = 2d; k.FollowOrbitSpeedDegreesPerSecond = 20d;
            k.FollowSmoothTimeSeconds = 0.3d; k.FollowFov = 65f;
        });
        Follow("follow-drone", k =>
        {
            k.FollowMotionMode = CameraFollowMotionMode.Delayed; k.FollowDelaySeconds = 1d;
            k.FollowDistance = 9d; k.FollowHeight = 6d; k.FollowFov = 60f;
        });
        Follow("follow-low-hero", k =>
            {
                k.FollowDistance = 3d; k.FollowHeight = -0.9d; k.FollowOffsetX = 0.4d;
                k.FollowSmoothTimeSeconds = 0.3d; k.FollowFov = 60f;
            });
        Follow("follow-left-shoulder", k =>
            {
                k.FollowDistance = 2.2d; k.FollowHeight = 0.35d; k.FollowOffsetX = -0.6d;
                k.FollowSmoothTimeSeconds = 0.25d; k.FollowFov = 65f;
            });
        Follow("follow-hip", k =>
            {
                k.FollowDistance = 1.2d; k.FollowHeight = -0.35d; k.FollowOffsetX = 0.5d;
                k.FollowSmoothTimeSeconds = 0.15d; k.FollowFov = 75f;
            });
        Add("follow-side-scroller", PresetFollowGroup, c => FollowPreset(c, "follow-side-scroller", k =>
            {
                k.FollowWorldAligned = true; k.FollowOrbitAngleDegrees = c.Pose.Yaw;
                k.FollowDistance = 6d; k.FollowHeight = 0.2d; k.FollowSmoothTimeSeconds = 0.35d; k.FollowFov = 60f;
            }), PresetNeeds.Player);
        Add("follow-top-down", PresetFollowGroup, c => FollowPreset(c, "follow-top-down", k =>
            {
                k.FollowWorldAligned = true; k.FollowOrbitAngleDegrees = c.Pose.Yaw;
                k.FollowDistance = 0.05d; k.FollowHeight = 12d; k.FollowSmoothTimeSeconds = 0.4d; k.FollowFov = 70f;
            }), PresetNeeds.Player);
        Add("follow-satellite", PresetFollowGroup, c => FollowPreset(c, "follow-satellite", k =>
            {
                k.FollowWorldAligned = true; k.FollowOrbitAngleDegrees = c.Pose.Yaw;
                k.FollowDistance = 0.05d; k.FollowHeight = 60d; k.FollowSmoothTimeSeconds = 1d; k.FollowFov = 40f;
            }, length: 12d), PresetNeeds.Player);
        Follow("follow-lead", k =>
            {
                k.FollowDistance = 6d; k.FollowHeight = 0.6d; k.FollowOrbitAngleDegrees = 180d;
                k.FollowSmoothTimeSeconds = 0.5d; k.FollowFov = 60f;
            });
        Follow("follow-lazy", k =>
            {
                k.FollowMotionMode = CameraFollowMotionMode.Delayed; k.FollowDelaySeconds = 2.5d;
                k.FollowDistance = 8d; k.FollowHeight = 3d; k.FollowFov = 65f;
            });
        Follow("follow-locked", k =>
            {
                k.FollowMotionMode = CameraFollowMotionMode.Immediate;
                k.FollowDistance = 4d; k.FollowHeight = 1.2d; k.FollowFov = 70f;
            });
        Follow("follow-telephoto", k =>
            {
                k.FollowDistance = 18d; k.FollowHeight = 2d; k.FollowSmoothTimeSeconds = 0.6d; k.FollowFov = 25f;
            });
        Follow("follow-intro-orbit", k =>
            {
                k.FollowDistance = 2.5d; k.FollowHeight = 0d; k.FollowOrbitAngleDegrees = 150d;
                k.FollowOrbitSpeedDegreesPerSecond = 8d; k.FollowSmoothTimeSeconds = 0.3d; k.FollowFov = 55f;
            }, length: 10d);
        Follow("follow-hype-spin", k =>
            {
                k.FollowDistance = 4d; k.FollowHeight = 1d; k.FollowOrbitSpeedDegreesPerSecond = 60d;
                k.FollowSmoothTimeSeconds = 0.1d; k.FollowFov = 70f;
            }, length: 6d);
        Add("follow-back-to-face", PresetFollowGroup, c => FollowPreset(c, "follow-back-to-face", k =>
            {
                k.FollowDistance = 3d; k.FollowHeight = 0.3d; k.FollowSmoothTimeSeconds = 0.1d; k.FollowFov = 60f;
            }, length: 5d, configureEnd: k => k.FollowOrbitAngleDegrees = 179d, blend: IEase), PresetNeeds.Player);
        Add("follow-crane-up", PresetFollowGroup, c => FollowPreset(c, "follow-crane-up", k =>
            {
                k.FollowDistance = 3d; k.FollowHeight = 0.5d; k.FollowSmoothTimeSeconds = 0.3d; k.FollowFov = 60f;
            }, configureEnd: k => { k.FollowDistance = 12d; k.FollowHeight = 15d; }, blend: IEase), PresetNeeds.Player);
        Add("follow-crane-down", PresetFollowGroup, c => FollowPreset(c, "follow-crane-down", k =>
            {
                k.FollowDistance = 12d; k.FollowHeight = 15d; k.FollowSmoothTimeSeconds = 0.3d; k.FollowFov = 60f;
            }, configureEnd: k => { k.FollowDistance = 2.5d; k.FollowHeight = 0.4d; }, blend: IEase), PresetNeeds.Player);
        Add("follow-pull-away", PresetFollowGroup, c => FollowPreset(c, "follow-pull-away", k =>
            {
                k.FollowDistance = 3d; k.FollowHeight = 0.8d; k.FollowSmoothTimeSeconds = 0.2d; k.FollowFov = 60f;
            }, length: 10d, configureEnd: k => { k.FollowDistance = 30d; k.FollowHeight = 6d; }, blend: IEaseIn), PresetNeeds.Player);
        Add("follow-whip-in", PresetFollowGroup, c =>
            {
                if (c.Player is not DirectorReplayActorPathSource player) return null;
                DirectorReplayElementTrack track = PresetNewTrack(c, DirectorReplayElementTrackType.FollowEntity, "follow-whip-in", PlayerLabel(player));
                void Close(DirectorReplayElementKeyframe k)
                {
                    k.FollowDistance = 3d; k.FollowHeight = 0.5d; k.FollowSmoothTimeSeconds = 0.05d; k.FollowFov = 60f;
                }
                AddFollowInterval(c, track, player, 0d, 2.5d, k =>
                {
                    Close(k); k.FollowDistance = 25d; k.FollowHeight = 6d;
                }, Close, IEaseIn);
                AddFollowInterval(c, track, player, 2.5d, 3d, Close);
                return track.Id;
            }, PresetNeeds.Player);
        Follow("follow-cave", k =>
            {
                k.FollowDistance = 1.8d; k.FollowHeight = 0.2d; k.FollowSmoothTimeSeconds = 0.15d; k.FollowFov = 75f;
                k.FollowOpenClearView = true;
            });
        Follow("follow-forest-clear", k =>
            {
                k.FollowDistance = 10d; k.FollowHeight = 4d; k.FollowSmoothTimeSeconds = 0.8d; k.FollowFov = 60f;
                k.FollowOpenClearView = true;
            });
        Follow("follow-stabilized-pov", k =>
            {
                k.FollowViewMode = CameraFollowViewMode.FirstPerson; k.FollowMotionMode = CameraFollowMotionMode.Smooth;
                k.FollowSmoothTimeSeconds = 0.35d; k.FollowFov = 75f;
            });
        Add("follow-fall-cam", PresetFollowGroup, c => FollowPreset(c, "follow-fall-cam", k =>
            {
                k.FollowWorldAligned = true; k.FollowOrbitAngleDegrees = c.Pose.Yaw;
                k.FollowMotionMode = CameraFollowMotionMode.Immediate;
                k.FollowDistance = 0.05d; k.FollowHeight = 4d; k.FollowFov = 80f;
            }, length: 6d), PresetNeeds.Player);
        Follow("follow-dutch", k =>
            {
                k.FollowDistance = 2.4d; k.FollowHeight = 0.35d; k.FollowOffsetX = 0.6d;
                k.FollowSmoothTimeSeconds = 0.25d; k.FollowFov = 65f; k.Roll = 12f;
            });

        // ----- Fixed camera that tracks the player ------------------------------------
        Add("track-tripod", PresetTrackGroup, c => TripodPreset(c, "track-tripod", 8d, k => k.TrackSmoothTimeSeconds = 0.25d, (0d, c.CameraPoint)),
            PresetNeeds.Player);
        Add("track-shaky-operator", PresetTrackGroup, c =>
            {
                Guid? id = TripodPreset(c, "track-shaky-operator", 8d, k => k.TrackSmoothTimeSeconds = 1.2d, (0d, c.CameraPoint));
                ShakePreset(c, 0.8f, 0.35f, 1.1f, 0.25f, 0.4f, new(0, 0), new(0.5, 1), new(7.5, 1), new(8, 0));
                return id;
            }, PresetNeeds.Player);
        Add("track-cctv", PresetTrackGroup, c =>
            {
                PresetPoint head = c.Subject;
                (double dx, double dz) = Direction(head, c.CameraPoint);
                PresetPoint camera = head.Offset(dx * 8d, 6d, dz * 8d);
                Guid? id = TripodPreset(c, "track-cctv", 8d, null, (0d, camera));
                FovPreset(c, true, new FovStep(0, 95f, IHold), new FovStep(8, 95f, IHold));
                GradePreset(c, true, new GradeStep(0, Sepia: 0.35f, Contrast: 0.15f, Vignette: 0.35f, Interpolation: IHold),
                    new GradeStep(8, Interpolation: IHold));
                return id;
            }, PresetNeeds.Player);
        Add("track-flyby", PresetTrackGroup, c =>
            {
                PresetPoint head = c.Subject;
                (double rx, double rz) = c.Right;
                (double fx, double fz) = c.Forward;
                PresetPoint from = head.Offset(-rx * 14d - fx * 4d, 1.2d, -rz * 14d - fz * 4d);
                PresetPoint to = head.Offset(rx * 14d - fx * 4d, 1.2d, rz * 14d - fz * 4d);
                return TripodPreset(c, "track-flyby", 4d, null, (0d, from), (4d, to));
            }, PresetNeeds.Player);
        Add("track-flyover", PresetTrackGroup, c =>
            {
                PresetPoint head = c.Subject;
                (double fx, double fz) = c.Forward;
                return TripodPreset(c, "track-flyover", 3d, null,
                    (0d, head.Offset(-fx * 25d, 12d, -fz * 25d)), (3d, head.Offset(fx * 25d, 12d, fz * 25d)));
            }, PresetNeeds.Player);
        Add("track-switch-subject", PresetTrackGroup, c =>
            {
                if (c.Player is not DirectorReplayActorPathSource player || NearestOtherActor(c, false) is not { } other)
                {
                    c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-no-second-actor"));
                    return null;
                }
                DirectorReplayElementTrack track = PresetNewTrack(c, DirectorReplayElementTrackType.TrackEntity, "track-switch-subject");
                foreach ((double at, double blend, DirectorReplayElementInterpolation interpolation) in new[]
                         { (0d, 0d, IHold), (1.5d, 0d, IEase), (2.5d, 1d, IHold) })
                {
                    DirectorReplayElementKeyframe key = LookKey(PresetTime(c, at), player, DirectorReplayTrackMode.Tripod, c.CameraPoint, interpolation);
                    key.SecondaryEntityActorKey = other.ActorKey;
                    key.SecondaryEntityId = other.EntityId;
                    key.TargetBlend = blend;
                    AddPresetKeyframe(c, track, key);
                }
                EndPresetTrack(c, track, 6d);
                return track.Id;
            }, PresetNeeds.Player);
        Add("track-auto-zoom", PresetTrackGroup, c => TripodPreset(c, "track-auto-zoom", 10d, k =>
            {
                k.TrackAutoFrame = true; k.TrackFrameSize = 0.45d; k.TrackSmoothTimeSeconds = 0.4d;
            }, (0d, c.CameraPoint)), PresetNeeds.Player);
        Add("track-two-shot", PresetTrackGroup, c =>
            {
                if (NearestOtherActor(c, false) is not { } other)
                {
                    c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-no-second-actor"));
                    return null;
                }
                return TripodPreset(c, "track-two-shot", 8d, k =>
                {
                    k.SecondaryEntityActorKey = other.ActorKey; k.SecondaryEntityId = other.EntityId;
                    k.TargetBlend = 0.5d; k.TrackAutoFrame = true; k.TrackFrameSize = 0.8d; k.TrackSmoothTimeSeconds = 0.5d;
                }, (0d, c.CameraPoint));
            }, PresetNeeds.Player);
        Add("track-group", PresetTrackGroup, c => TripodPreset(c, "track-group", 10d, k =>
            {
                k.TrackFrameGroup = true; k.TrackAutoFrame = true; k.TrackFrameSize = 0.85d; k.TrackSmoothTimeSeconds = 0.6d;
            }, (0d, c.CameraPoint)), PresetNeeds.Player);
        Add("track-over-shoulder", PresetTrackGroup, c =>
            {
                if (c.Player is not DirectorReplayActorPathSource player || NearestOtherActor(c, false) is not { } other)
                {
                    c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-no-second-actor"));
                    return null;
                }
                DirectorReplayElementTrack track = PresetNewTrack(c, DirectorReplayElementTrackType.TrackEntity, "track-over-shoulder");
                DirectorReplayElementKeyframe key = LookKey(PresetTime(c, 0), other, DirectorReplayTrackMode.OverShoulder, c.CameraPoint);
                key.SecondaryEntityActorKey = player.ActorKey;
                key.SecondaryEntityId = player.EntityId;
                key.TrackSmoothTimeSeconds = 0.3d;
                AddPresetKeyframe(c, track, key);
                EndPresetTrack(c, track, 8d);
                return track.Id;
            }, PresetNeeds.Player);

        // ----- Orbits ---------------------------------------------------------------------
        Add("orbit-build-showcase", PresetOrbitGroup, c =>
            {
                double distance = Math.Max(20d, Distance(c.CameraPoint, c.Look));
                return OrbitPreset(c, "orbit-build-showcase", c.Look,
                    new(0, c.Pose.Yaw, -35, distance), new(24, c.Pose.Yaw + 360, -35, distance));
            });
        Add("orbit-turntable", PresetOrbitGroup, c =>
            {
                double distance = Math.Clamp(Distance(c.CameraPoint, c.Look), 3d, 8d);
                return OrbitPreset(c, "orbit-turntable", c.Look,
                    new(0, c.Pose.Yaw, -10, distance), new(12, c.Pose.Yaw + 360, -10, distance));
            });
        Add("orbit-spiral-up", PresetOrbitGroup, c => OrbitPreset(c, "orbit-spiral-up", c.Look,
                new(0, c.Pose.Yaw, -5, 8, Interpolation: IEase), new(14, c.Pose.Yaw + 270, -50, 35)));
        Add("orbit-spiral-in", PresetOrbitGroup, c => OrbitPreset(c, "orbit-spiral-in", c.Look,
                new(0, c.Pose.Yaw, -50, 35, Interpolation: IEase), new(12, c.Pose.Yaw + 270, -10, 6)));
        Add("orbit-parallax-arc", PresetOrbitGroup, c =>
            {
                double distance = Math.Max(2d, Distance(c.CameraPoint, c.Look));
                return OrbitPreset(c, "orbit-parallax-arc", c.Look,
                    new(0, c.Pose.Yaw, c.Pose.Pitch, distance, Interpolation: IEase), new(8, c.Pose.Yaw + 90, c.Pose.Pitch, distance));
            });
        Add("orbit-dizzy", PresetOrbitGroup, c =>
            {
                PresetPoint center = c.Subject;
                var steps = new List<OrbitStep>();
                float[] rolls = { 0f, 15f, -15f, 15f, -15f, 12f, 0f };
                for (int i = 0; i < rolls.Length; i++)
                {
                    double at = i * 1d;
                    steps.Add(new OrbitStep(at, c.Pose.Yaw + 120d * i, -15d, 4d, rolls[i], ISmooth));
                }
                Guid? id = OrbitPreset(c, "orbit-dizzy", center, steps.ToArray());
                ShakePreset(c, 0.3f, 1.5f, 0.2f, 1f, 4f, new(0, 0), new(1, 1), new(5, 1), new(6, 0));
                return id;
            });

        // ----- Camera moves from the current pose --------------------------------------------
        Add("move-dolly-in", PresetMoveGroup, c => MovePreset(c, "move-dolly-in", 0.5d, new MoveStep(0), new MoveStep(6, Forward: 5)));
        Add("move-dolly-out", PresetMoveGroup, c => MovePreset(c, "move-dolly-out", 0.5d, new MoveStep(0), new MoveStep(6, Forward: -5)));
        Add("move-truck-left", PresetMoveGroup, c => MovePreset(c, "move-truck-left", 0.5d, new MoveStep(0), new MoveStep(6, Right: -6)));
        Add("move-truck-right", PresetMoveGroup, c => MovePreset(c, "move-truck-right", 0.5d, new MoveStep(0), new MoveStep(6, Right: 6)));
        Add("move-pedestal-up", PresetMoveGroup, c => MovePreset(c, "move-pedestal-up", 0.5d, new MoveStep(0), new MoveStep(6, Up: 6)));
        Add("move-crane-reveal", PresetMoveGroup, c => MovePreset(c, "move-crane-reveal", 0.5d, new MoveStep(0, Up: -3, Forward: -2, Pitch: 10),
                new MoveStep(7, Up: 8, Forward: 4, Pitch: -15)));
        Add("move-tilt-reveal", PresetMoveGroup, c => MovePreset(c, "move-tilt-reveal", 0.5d, new MoveStep(0, Pitch: -60f - c.Pose.Pitch),
                new MoveStep(6, Pitch: 5f - c.Pose.Pitch)));
        Add("move-landscape-pan", PresetMoveGroup, c => MovePreset(c, "move-landscape-pan", 0.5d, new MoveStep(0), new MoveStep(12, Yaw: -90f)));
        Add("move-whip-pan", PresetMoveGroup, c => MovePreset(c, "move-whip-pan", 1.5d, new MoveStep(0), new MoveStep(0.25, Yaw: -90f)));
        Add("move-vertigo", PresetMoveGroup, c => DollyZoom(c, "move-vertigo", toward: true));
        Add("move-reverse-vertigo", PresetMoveGroup, c => DollyZoom(c, "move-reverse-vertigo", toward: false));
        Add("move-barrel-roll", PresetMoveGroup, c => MovePreset(c, "move-barrel-roll", 0.3d, new MoveStep(0, Interpolation: CameraPathInterpolation.Linear),
                new MoveStep(0.33, Roll: 120f, Interpolation: CameraPathInterpolation.Linear),
                new MoveStep(0.66, Roll: 240f, Interpolation: CameraPathInterpolation.Linear), new MoveStep(1, Roll: 360f)));
        Add("move-dutch-creep", PresetMoveGroup, c => MovePreset(c, "move-dutch-creep", 2d, new MoveStep(0), new MoveStep(6, Roll: 15f)));
        Add("move-snap-zoom", PresetMoveGroup, c => FovPreset(c, true, new FovStep(0, c.BaseFov * 0.5f, IHold), new FovStep(3, c.BaseFov, IHold)));
        Add("move-crash-zoom", PresetMoveGroup, c =>
            {
                Guid? id = FovPreset(c, true, new FovStep(0, c.BaseFov, IEaseIn), new FovStep(0.15, 20f, IHold),
                    new FovStep(2.2, 20f, IEase), new FovStep(2.8, c.BaseFov, IHold));
                ShakePreset(c, 9f, 0.8f, 8f, 1f, 0f, new(0.12, 0), new(0.16, 1, IEaseOut), new(0.6, 0));
                return id;
            });
        Add("move-establishing", PresetMoveGroup, c => MovePreset(c, "move-establishing", 0.5d, new MoveStep(0, Interpolation: CameraPathInterpolation.Step),
                new MoveStep(4), new MoveStep(10, Forward: 4)));
        Add("move-drop-in", PresetMoveGroup, c =>
            {
                PresetPoint subject = c.Subject;
                (double fx, double fz) = c.Forward;
                CameraPreset top = LookPose(c, subject.Offset(0.01d, 80d, 0.01d), subject);
                top.Yaw = c.Pose.Yaw;
                top.Pitch = -89.5f;
                CameraPreset land = LookPose(c, subject.Offset(-fx * 6d, 2d, -fz * 6d), subject);
                return PosePreset(c, "move-drop-in", 1d, (0d, top, CameraPathInterpolation.EaseInOut),
                    (3.5d, land, CameraPathInterpolation.EaseInOut));
            });
        Add("move-rise-to-sky", PresetMoveGroup, c => MovePreset(c, "move-rise-to-sky", 1d, new MoveStep(0), new MoveStep(8, Up: 40, Pitch: 40f - c.Pose.Pitch)));
        Add("move-ground-skim", PresetMoveGroup, c =>
            {
                PresetPoint ground = Ground(c, c.CameraPoint);
                (double fx, double fz) = c.Forward;
                CameraPreset from = c.Pose.DeepClone();
                from.X = ground.X; from.Y = ground.Y + 0.4d; from.Z = ground.Z; from.Pitch = 0f;
                CameraPreset to = from.DeepClone();
                to.X += fx * 30d; to.Z += fz * 30d;
                return PosePreset(c, "move-ground-skim", 0.3d, (0d, from, CameraPathInterpolation.Linear),
                    (3d, to, CameraPathInterpolation.Linear));
            });
        Add("move-through-wall", PresetMoveGroup, c => MovePreset(c, "move-through-wall", 0.5d, new MoveStep(0, Interpolation: CameraPathInterpolation.Linear),
                new MoveStep(4, Forward: 12)));
        Add("move-corner-peek", PresetMoveGroup, c => MovePreset(c, "move-corner-peek", 1d, new MoveStep(0, Right: -2), new MoveStep(2.5, Right: 2)));
        Add("move-handheld-drift", PresetMoveGroup, c =>
            {
                Guid? id = MovePreset(c, "move-handheld-drift", 0.3d,
                    new MoveStep(0, Interpolation: CameraPathInterpolation.Bezier),
                    new MoveStep(1.5, Right: 0.15, Up: 0.1, Yaw: 1.5f, Interpolation: CameraPathInterpolation.Bezier),
                    new MoveStep(3, Right: -0.1, Up: -0.05, Yaw: -1f, Pitch: 0.8f, Interpolation: CameraPathInterpolation.Bezier),
                    new MoveStep(4.5, Right: 0.12, Up: 0.08, Yaw: 0.8f, Pitch: -0.6f, Interpolation: CameraPathInterpolation.Bezier),
                    new MoveStep(6));
                ShakePreset(c, 0.7f, 0.35f, 0.9f, 0.25f, 0.3f, new(0, 0), new(0.4, 1), new(5.6, 1), new(6, 0));
                return id;
            });

        // ----- Shakes -------------------------------------------------------------------------
        Add("shake-handheld", PresetShakeGroup, c => ShakePreset(c, "shake-handheld", 0.7f, 0.35f, 0.9f, 0.25f, new(0, 0), new(0.6, 1), new(8, 1), new(9, 0)));
        Add("shake-walk", PresetShakeGroup, c => ShakePreset(c, "shake-walk", 0.9f, 0.25f, 1.8f, 0.5f, new(0, 0), new(0.5, 1), new(6, 1), new(6.8, 0)));
        Add("shake-tension", PresetShakeGroup, c => ShakePreset(c, "shake-tension", 0.25f, 0.9f, 0.3f, 0.5f,
                new(0, 0, IEase), new(1.5, 1), new(8, 1, IEase), new(10, 0)));
        Add("shake-impact", PresetShakeGroup, c => ShakePreset(c, "shake-impact", 10f, 1.6f, 9f, 2.2f, new(0, 0), new(0.04, 1, IEaseOut), new(0.4, 0)));
        Add("shake-explosion", PresetShakeGroup, c => ShakePreset(c, "shake-explosion", 8f, 3.5f, 9f, 4.5f, new(0, 0), new(0.05, 1, IEaseOut), new(1.6, 0)));
        Add("shake-earthquake", PresetShakeGroup, c => ShakePreset(c, "shake-earthquake", 6f, 1.4f, 7f, 1.1f,
                new(0, 0, IEaseIn), new(1, 1), new(4, 1, IEaseOut), new(5.5, 0)));
        Add("shake-heartbeat", PresetShakeGroup, c =>
            {
                var steps = new List<ShakeStep>();
                for (int beat = 0; beat < 8; beat++)
                {
                    double t = beat * 0.9d;
                    steps.Add(new(t, 0));
                    steps.Add(new(t + 0.06, 1, IEaseOut));
                    steps.Add(new(t + 0.18, 0.15));
                    steps.Add(new(t + 0.26, 0.7, IEaseOut));
                    steps.Add(new(t + 0.45, 0));
                }
                return ShakePreset(c, 6f, 0.35f, 6f, 0.6f, 0f, steps.ToArray());
            });
        Add("shake-giant-steps", PresetShakeGroup, c =>
            {
                var steps = new List<ShakeStep>();
                for (int step = 0; step < 6; step++)
                {
                    double t = step * 1.4d;
                    steps.Add(new(t, 0));
                    steps.Add(new(t + 0.04, 1, IEaseOut));
                    steps.Add(new(t + 0.6, 0));
                }
                return ShakePreset(c, 7f, 0.5f, 8f, 1.6f, 0f, steps.ToArray());
            });
        Add("shake-distant-rumble", PresetShakeGroup, c => ShakePreset(c, 2.5f, 0.25f, 3.1f, 0.35f, 0f, new(0, 0, IEase), new(3, 1), new(9, 1, IEase), new(12, 0)));
        Add("shake-boat-sway", PresetShakeGroup, c => ShakePreset(c, 0.25f, 1.2f, 0.35f, 0.8f, 4f, new(0, 0, IEase), new(2, 1), new(14, 1, IEase), new(16, 0)));
        Add("shake-gallop", PresetShakeGroup, c => ShakePreset(c, 1.3f, 0.3f, 2.6f, 1f, 0f, new(0, 0), new(0.5, 1), new(8.5, 1), new(9, 0)));
        Add("shake-drunk", PresetShakeGroup, c => ShakePreset(c, 0.3f, 3f, 0.2f, 2f, 8f, new(0, 0, IEase), new(2, 1), new(12, 1, IEase), new(14, 0)));
        Add("shake-rattle", PresetShakeGroup, c => ShakePreset(c, 9f, 0.12f, 10f, 0.15f, 0f, new(0, 0), new(0.3, 1), new(6.3, 1), new(6.6, 0)));
        Add("shake-landing", PresetShakeGroup, c => ShakePreset(c, 0.5f, 0.2f, 3f, 3f, 0f, new(0, 0), new(0.03, 1, IEaseOut), new(0.6, 0)));
        Add("shake-temporal-storm", PresetShakeGroup, c =>
            {
                Guid? id = ShakePreset(c, 7f, 2.5f, 5.5f, 2f, 3f, new(0, 0, IEaseIn), new(3, 0.3), new(7, 0.7), new(10, 1),
                    new(12, 1, IEaseOut), new(13, 0));
                GradePreset(c, true, new GradeStep(0), new GradeStep(10, Sepia: 0.3f, Glitch: 0.45f), new GradeStep(12),
                    new GradeStep(13));
                return id;
            });
        Add("shake-anxiety", PresetShakeGroup, c => ShakePreset(c, 8f, 0.08f, 9f, 0.1f, 0f, new(0, 0), new(0.5, 1), new(8.5, 1), new(9, 0)));

        // ----- Field of view ---------------------------------------------------------------------
        Add("fov-punch-in", PresetFovGroup, c => FovPreset(c, true, new(0, c.BaseFov, IEaseOut), new(0.35, c.BaseFov - 22f, ILinear),
                new(2.4, c.BaseFov - 22f), new(3.4, c.BaseFov, IHold)));
        Add("fov-push-in", PresetFovGroup, c => FovPreset(c, false, new(0, c.BaseFov), new(6, c.BaseFov - 20f, IHold)));
        Add("fov-reveal", PresetFovGroup, c => FovPreset(c, false, new(0, c.BaseFov), new(3, c.BaseFov + 25f, IHold)));
        Add("fov-pulse", PresetFovGroup, c => FovPreset(c, true, new(0, c.BaseFov, IEaseOut), new(0.07, c.BaseFov + 12f, IEaseOut),
                new(0.6, c.BaseFov, IHold)));
        Add("fov-breathe", PresetFovGroup, c =>
            {
                var steps = new List<FovStep>();
                for (int i = 0; i <= 8; i++)
                {
                    steps.Add(new(i * 1.5d, c.BaseFov + (i == 0 || i == 8 ? 0f : i % 2 == 1 ? 3f : -3f), ISmooth));
                }
                return FovPreset(c, true, steps.ToArray());
            });
        Add("fov-sprint", PresetFovGroup, c => FovPreset(c, true, new(0, c.BaseFov, IEaseOut), new(1, c.BaseFov + 15f), new(5, c.BaseFov + 15f, IEase),
                new(6, c.BaseFov, IHold)));
        Add("fov-telephoto", PresetFovGroup, c => FovPreset(c, true, new(0, c.BaseFov), new(1.5, 20f), new(7.5, 20f), new(9, c.BaseFov, IHold)));
        Add("fov-fisheye", PresetFovGroup, c => FovPreset(c, true, new(0, c.BaseFov, IEaseOut), new(0.6, 115f), new(8.6, 115f, IEaseIn),
                new(9.2, c.BaseFov, IHold)));
        Add("fov-creep", PresetFovGroup, c => FovPreset(c, true, new(0, c.BaseFov, ILinear), new(20, c.BaseFov - 10f, IEase), new(21, c.BaseFov, IHold)));

        // ----- Speed and time ---------------------------------------------------------------------
        Add("speed-ramp", PresetTimeGroup, c => SpeedPreset(c, new(0, 1, IEaseIn), new(0.4, 0.25, IHold), new(3.4, 0.25, IEaseOut), new(3.8, 1)));
        Add("speed-bullet-time", PresetTimeGroup, c =>
            {
                TimeEffectPreset(c, 0, 5, DirectorReplayTimeEffect.Slow, rate: 0.1d);
                return OrbitPreset(c, "speed-bullet-time", c.Subject,
                    new(0, c.Pose.Yaw, -8, 4, Interpolation: IEase), new(5, c.Pose.Yaw + 180, -8, 4));
            }, PresetNeeds.Player);
        Add("speed-freeze-orbit", PresetTimeGroup, c =>
            {
                TimeEffectPreset(c, 0, 5, DirectorReplayTimeEffect.Freeze, cameraContinues: true);
                return OrbitPreset(c, "speed-freeze-orbit", c.Subject,
                    new(0, c.Pose.Yaw, -10, 4.5, Interpolation: IEase), new(5, c.Pose.Yaw + 180, -10, 4.5));
            }, PresetNeeds.Player);
        Add("speed-hitstop", PresetTimeGroup, c =>
            {
                Guid? id = TimeEffectPreset(c, 0, 0.1, DirectorReplayTimeEffect.Freeze, cameraContinues: true);
                ShakePreset(c, 10f, 1.2f, 9f, 1.6f, 0f, new(0, 0), new(0.03, 1, IEaseOut), new(0.4, 0));
                return id;
            });
        Add("speed-stutter", PresetTimeGroup, c =>
            {
                Guid? id = null;
                for (int i = 0; i < 4; i++)
                {
                    id = TimeEffectPreset(c, i * 0.25d, i * 0.25d + 0.1d, DirectorReplayTimeEffect.Freeze, cameraContinues: true);
                }
                return id;
            });
        Add("speed-fast-forward", PresetTimeGroup, c => SpeedPreset(c, new(0, 4, IHold), new(10, 1, IHold)));
        Add("speed-hyperlapse", PresetTimeGroup, c =>
            {
                Guid? id = TimelapsePreset(c, "speed-hyperlapse", 0, 60, 6);
                MovePreset(c, "speed-hyperlapse", 0.5d, new MoveStep(0, Interpolation: CameraPathInterpolation.Linear),
                    new MoveStep(60, Forward: 30));
                return id;
            });
        Add("speed-rewind", PresetTimeGroup, c => TimeEffectPreset(c, 0, 4, DirectorReplayTimeEffect.Rewind, seconds: 3d, rate: 1d));

        // ----- Time of day, sky and weather ----------------------------------------------------------
        Add("tod-golden-hour", PresetWorldGroup, c => TimeOfDayPreset(c, (0, 18300, IHold)));
        Add("tod-day-cycle", PresetWorldGroup, c => TimeOfDayPreset(c, (0, 5500, ILinear), (5, 12500, ILinear), (10, 19500, IHold)));
        Add("tod-nightfall", PresetWorldGroup, c => TimeOfDayPreset(c, (0, 17500, IEase), (4, 19500, IEase), (8, 22000, IHold)));
        Add("sky-green-screen", PresetWorldGroup, c =>
            {
                c.Timeline.Visuals.Sky.Mode = DirectorReplaySkyMode.GreenScreen;
                return PosePreset(c, "sky-green-screen", 10d, (0d, c.Pose, CameraPathInterpolation.Step));
            });
        Add("weather-storm-mood", PresetWorldGroup, c =>
            {
                c.Timeline.Visuals.Environment.Weather = DirectorReplayWeatherMode.Storm;
                c.Timeline.Visuals.Environment.Precipitation = 1f;
                TimeOfDayPreset(c, (0, 20500, IHold));
                return ShakePreset(c, 2.5f, 0.25f, 3.1f, 0.35f, 0f, new(0, 0, IEase), new(3, 1), new(9, 1, IEase), new(12, 0));
            });

        // ----- Combined shots -----------------------------------------------------------------------
        Add("tragic-slowmo", PresetCinematicGroup, c => TragicPreset(c, slowMotion: true), PresetNeeds.Player);
        Add("tragic", PresetCinematicGroup, c => TragicPreset(c, slowMotion: false), PresetNeeds.Player);
        Add("impact-hit", PresetCinematicGroup, c =>
            {
                Guid? shake = ShakePreset(c, "shake-impact", 10f, 1.8f, 9f, 2.4f, new(0, 0), new(0.04, 1, IEaseOut), new(0.5, 0));
                FovPreset(c, true, new(0, c.BaseFov, IEaseOut), new(0.07, c.BaseFov + 14f, IEaseOut), new(0.7, c.BaseFov, IHold));
                return shake;
            });
        Add("cine-intro", PresetCinematicGroup, c =>
            {
                TimeOfDayPreset(c, (0, 18300, IHold));
                Guid? id = MovePreset(c, "cine-intro", 0.05d, new MoveStep(0), new MoveStep(6, Up: 10, Forward: 4, Pitch: -20));
                if (c.Player is DirectorReplayActorPathSource player)
                {
                    DirectorReplayElementTrack track = PresetNewTrack(c, DirectorReplayElementTrackType.FollowEntity, "cine-intro", PlayerLabel(player));
                    AddFollowInterval(c, track, player, 6d, 8d, k =>
                    {
                        k.FollowDistance = 12d; k.FollowHeight = 8d; k.FollowSmoothTimeSeconds = 0.3d; k.FollowFov = 60f;
                    }, k => { k.FollowDistance = 2.2d; k.FollowHeight = 0.35d; k.FollowOffsetX = 0.6d; }, IEase);
                }
                return id;
            });
        Add("cine-outro", PresetCinematicGroup, c =>
            {
                Guid? id = FollowPreset(c, "cine-outro", k =>
                {
                    k.FollowDistance = 3d; k.FollowHeight = 1d; k.FollowSmoothTimeSeconds = 0.2d; k.FollowFov = 60f;
                }, length: 10d, configureEnd: k => { k.FollowDistance = 35d; k.FollowHeight = 20d; }, blend: IEaseIn);
                GradePreset(c, false, new GradeStep(0), new GradeStep(7), new GradeStep(10, Vignette: 0.4f, Fade: 1f, Interpolation: IHold));
                return id;
            }, PresetNeeds.Player);
        Add("cine-hero-landing", PresetCinematicGroup, c =>
            {
                Guid? id = FollowPreset(c, "cine-hero-landing", k =>
                {
                    k.FollowDistance = 3d; k.FollowHeight = -0.9d; k.FollowOffsetX = 0.4d; k.FollowSmoothTimeSeconds = 0.2d; k.FollowFov = 60f;
                }, length: 6d);
                SpeedPreset(c, new(0, 1, IEaseIn), new(0.3, 0.3, IHold), new(2.2, 0.3, IEaseOut), new(2.6, 1));
                ShakePreset(c, 0.5f, 0.2f, 3f, 3f, 0f, new(0.25, 0), new(0.28, 1, IEaseOut), new(0.9, 0));
                FovPreset(c, true, new(0.25, c.BaseFov, IEaseOut), new(0.32, c.BaseFov + 12f, IEaseOut), new(0.9, c.BaseFov, IHold));
                return id;
            }, PresetNeeds.Player);
        Add("cine-victory-spin", PresetCinematicGroup, c =>
            {
                Guid? id = FollowPreset(c, "cine-victory-spin", k =>
                {
                    k.FollowDistance = 4d; k.FollowHeight = 1d; k.FollowOrbitSpeedDegreesPerSecond = 60d;
                    k.FollowSmoothTimeSeconds = 0.1d; k.FollowFov = 70f;
                }, length: 6d);
                FovPreset(c, true, new(0, c.BaseFov, IEaseOut), new(0.07, c.BaseFov + 12f, IEaseOut), new(0.6, c.BaseFov, IHold));
                SpeedPreset(c, new(0, 0.7, IHold), new(6, 1));
                return id;
            }, PresetNeeds.Player);
        Add("cine-boss-intro", PresetCinematicGroup, c =>
            {
                if (NearestOtherActor(c, true) is not { } boss)
                {
                    c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-no-creature"));
                    return null;
                }
                DirectorReplayElementTrack track = PresetNewTrack(c, DirectorReplayElementTrackType.TrackEntity, "cine-boss-intro", PlayerLabel(boss));
                DirectorReplayElementKeyframe first = LookKey(PresetTime(c, 0), boss, DirectorReplayTrackMode.Tripod, c.CameraPoint, IEase);
                first.BodyPart = "body"; first.TrackAutoFrame = true; first.TrackFrameSize = 0.3d;
                DirectorReplayElementKeyframe second = first.DeepClone();
                second.Id = Guid.NewGuid(); second.TimeSeconds = PresetTime(c, 4); second.TrackFrameSize = 0.6d;
                second.InterpolationToNext = IHold;
                AddPresetKeyframe(c, track, first);
                AddPresetKeyframe(c, track, second);
                EndPresetTrack(c, track, 6.5d);
                TimeEffectPreset(c, 4, 6.5, DirectorReplayTimeEffect.Freeze, cameraContinues: true);
                TitlePreset(c, 4, PrettyActorName(boss), PresetLang("vsdirector:replay-preset-boss-subtitle"), 2.5d, DirectorReplayTitlePosition.Center);
                ShakePreset(c, 10f, 1.2f, 9f, 1.6f, 0f, new(4, 0), new(4.04, 1, IEaseOut), new(4.5, 0));
                return track.Id;
            });
        Add("cine-jump-scare", PresetCinematicGroup, c =>
            {
                Guid? id = PosePreset(c, "cine-jump-scare", 3d, (0d, c.Pose, CameraPathInterpolation.Step));
                FovPreset(c, true, new(0, c.BaseFov, IHold), new(2.5, c.BaseFov, IEaseIn), new(2.62, 18f, IHold),
                    new(4.5, 18f, IEase), new(5.5, c.BaseFov, IHold));
                ShakePreset(c, 10f, 1.8f, 9f, 2.4f, 0f, new(2.5, 0), new(2.55, 1, IEaseOut), new(3.2, 0));
                GradePreset(c, true, new GradeStep(2.5, Interpolation: IEaseOut), new GradeStep(2.6, Contrast: 0.5f, Vignette: 0.6f),
                    new GradeStep(4.5));
                return id;
            });
        Add("cine-chase", PresetCinematicGroup, c => CutSequence(c, "cine-chase", Enumerable.Range(0, 5).Select(i => (3d, i % 2 == 0
                ? (Action<DirectorReplayElementKeyframe>)(k => { k.FollowDistance = 5d; k.FollowHeight = 1.5d; k.FollowSmoothTimeSeconds = 0.4d; })
                : k => { k.FollowDistance = 6d; k.FollowHeight = 0.6d; k.FollowOrbitAngleDegrees = 180d; k.FollowSmoothTimeSeconds = 0.4d; }))
                .ToArray()), PresetNeeds.Player);
        Add("cine-arrival", PresetCinematicGroup, c =>
            {
                PresetPoint head = c.Subject;
                double yaw = c.PlayerYaw * Math.PI / 180d;
                PresetPoint camera = head.Offset(-Math.Sin(yaw) * 40d, 2d, -Math.Cos(yaw) * 40d);
                Guid? id = TripodPreset(c, "cine-arrival", 8d, k => k.TrackSmoothTimeSeconds = 0.3d, (0d, camera));
                FovPreset(c, true, new FovStep(0, 18f, IHold), new FovStep(8, 18f, IHold));
                return id;
            }, PresetNeeds.Player);
        Add("cine-build-timelapse", PresetCinematicGroup, c =>
            {
                double distance = Math.Max(16d, Distance(c.CameraPoint, c.Look));
                TimelapsePreset(c, "cine-build-timelapse", 0, 120, 12);
                return OrbitPreset(c, "cine-build-timelapse", c.Look,
                    new(0, c.Pose.Yaw, -30, distance), new(120, c.Pose.Yaw + 360, -30, distance));
            });
        Add("cine-death", PresetCinematicGroup, c =>
            {
                Guid? id = FollowPreset(c, "cine-death", k =>
                {
                    k.FollowWorldAligned = true; k.FollowOrbitAngleDegrees = c.Pose.Yaw;
                    k.FollowDistance = 2d; k.FollowHeight = 1d; k.FollowSmoothTimeSeconds = 0.2d; k.FollowFov = 60f;
                }, length: 7d, configureEnd: k => { k.FollowDistance = 1d; k.FollowHeight = 10d; }, blend: IEase);
                TimeEffectPreset(c, 0, 4, DirectorReplayTimeEffect.Slow, rate: 0.3d);
                GradePreset(c, true, new GradeStep(0), new GradeStep(3, Sepia: 0.9f, Contrast: 0.1f, Vignette: 0.5f),
                    new GradeStep(6.5, Sepia: 0.9f, Contrast: 0.1f, Vignette: 0.5f), new GradeStep(7.5));
                return id;
            }, PresetNeeds.Player);
        Add("cine-trailer", PresetCinematicGroup, c =>
            {
                Action<DirectorReplayElementKeyframe>[] rigs = TrailerRigs(c);
                Guid? id = CutSequence(c, "cine-trailer", rigs.Select(rig => (1.2d, rig)).ToArray());
                for (int i = 0; i < rigs.Length; i++)
                {
                    double t = i * 1.2d;
                    FovPreset(c, i == rigs.Length - 1, new FovStep(t, c.BaseFov - 12f, IEaseOut), new FovStep(t + 0.4, c.BaseFov, IHold));
                }
                return id;
            }, PresetNeeds.Player);

        // ----- Recording analysis ---------------------------------------------------------------------
        AddSmartPresets(Add);

        // ----- Styles -------------------------------------------------------------------------------
        Add("style-sports-broadcast", PresetStyleGroup, c => FollowPreset(c, "style-sports-broadcast", k =>
            {
                k.FollowWorldAligned = true; k.FollowOrbitAngleDegrees = c.Pose.Yaw;
                k.FollowDistance = 25d; k.FollowHeight = 10d; k.FollowSmoothTimeSeconds = 0.8d; k.FollowFov = 28f;
            }, length: 10d), PresetNeeds.Player);
        Add("style-nature-doc", PresetStyleGroup, c =>
            {
                DirectorReplayActorPathSource? subject = NearestOtherActor(c, true) ?? c.Player;
                if (subject is not DirectorReplayActorPathSource target) return null;
                DirectorReplayElementTrack track = PresetNewTrack(c, DirectorReplayElementTrackType.TrackEntity, "style-nature-doc", PlayerLabel(target));
                DirectorReplayElementKeyframe key = LookKey(PresetTime(c, 0), target, DirectorReplayTrackMode.Tripod, c.CameraPoint);
                key.BodyPart = "body"; key.TrackAutoFrame = true; key.TrackFrameSize = 0.5d; key.TrackSmoothTimeSeconds = 1.5d;
                AddPresetKeyframe(c, track, key);
                EndPresetTrack(c, track, 12d);
                return track.Id;
            });
        Add("style-symmetric", PresetStyleGroup, c =>
            {
                c.Timeline.Visuals.Composition.CenterGuide = true;
                CameraPreset square = c.Pose.DeepClone();
                square.Yaw = (float)(Math.Round(c.Pose.Yaw / 90d) * 90d);
                square.Pitch = 0f;
                square.Roll = 0f;
                double yaw = square.Yaw * Math.PI / 180d;
                CameraPreset end = square.DeepClone();
                end.X += Math.Cos(yaw) * 4d;
                end.Z += -Math.Sin(yaw) * 4d;
                return PosePreset(c, "style-symmetric", 0.5d, (0d, square, CameraPathInterpolation.Linear),
                    (8d, end, CameraPathInterpolation.Linear));
            });
        Add("style-noir", PresetStyleGroup, c =>
            {
                c.Timeline.Visuals.Environment.Weather = DirectorReplayWeatherMode.Rain;
                TimeOfDayPreset(c, (0, 22000, IHold));
                Guid? id = MovePreset(c, "style-noir", 0.5d, new MoveStep(0, Roll: 10f), new MoveStep(12, Roll: 10f, Forward: 1.5));
                GradePreset(c, true, new GradeStep(0, Sepia: 1f, Contrast: 0.35f, Vignette: 0.45f, Interpolation: IHold),
                    new GradeStep(12, Interpolation: IHold));
                return id;
            });
        Add("style-dream", PresetStyleGroup, c =>
            {
                SpeedPreset(c, new(0, 0.6, IHold), new(10, 1));
                var fov = new List<FovStep>();
                for (int i = 0; i <= 5; i++) fov.Add(new(i * 2d, c.BaseFov + (i % 2 == 1 ? 4f : 0f), ISmooth));
                FovPreset(c, true, fov.ToArray());
                GradePreset(c, true, new GradeStep(0), new GradeStep(1.5, Sepia: 0.15f, Bloom: 1.2f), new GradeStep(9, Sepia: 0.15f, Bloom: 1.2f),
                    new GradeStep(10));
                return MovePreset(c, "style-dream", 0.3d, new MoveStep(0, Interpolation: CameraPathInterpolation.Bezier),
                    new MoveStep(5, Right: 0.6, Up: 0.3, Yaw: 3f, Interpolation: CameraPathInterpolation.Bezier),
                    new MoveStep(10, Right: 1.2, Up: 0.1, Yaw: -2f));
            });
        Add("style-found-footage", PresetStyleGroup, c =>
            {
                Guid? id = MovePreset(c, "style-found-footage", 0.3d,
                    new MoveStep(0, Interpolation: CameraPathInterpolation.Bezier),
                    new MoveStep(2.5, Right: 0.2, Up: -0.1, Yaw: 3f, Pitch: -1f, Interpolation: CameraPathInterpolation.Bezier),
                    new MoveStep(5, Right: -0.15, Up: 0.1, Yaw: -2f, Pitch: 1.5f, Interpolation: CameraPathInterpolation.Bezier),
                    new MoveStep(7.5, Right: 0.1, Yaw: 2f, Interpolation: CameraPathInterpolation.Bezier), new MoveStep(10));
                ShakePreset(c, 8f, 0.15f, 7f, 0.2f, 0.5f, new(0, 0), new(0.3, 1), new(9.7, 1), new(10, 0));
                FovPreset(c, true, new FovStep(0, 95f, IHold), new FovStep(10, 95f, IHold));
                GradePreset(c, true, new GradeStep(0, Contrast: 0.15f, Vignette: 0.5f, Sepia: 0.1f, Interpolation: IHold),
                    new GradeStep(10, Interpolation: IHold));
                return id;
            });
        Add("style-silent-film", PresetStyleGroup, c =>
            {
                Guid? id = TimeEffectPreset(c, 0, 15, DirectorReplayTimeEffect.Stutter, rate: 12d);
                GradePreset(c, true, new GradeStep(0, Sepia: 1f, Contrast: 0.3f, Vignette: 0.55f, Interpolation: IHold),
                    new GradeStep(15, Interpolation: IHold));
                return id;
            });

        return list;
    }

    // ----- Helpers for the catalog ----------------------------------------------------------

    private static string PresetLang(string key) => Vintagestory.API.Config.Lang.Get(key);

    private static double Distance(PresetPoint a, PresetPoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Horizontal unit direction from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static (double X, double Z) Direction(PresetPoint from, PresetPoint to)
    {
        double dx = to.X - from.X;
        double dz = to.Z - from.Z;
        double length = Math.Sqrt(dx * dx + dz * dz);
        return length < 0.0001d ? (0d, 1d) : (dx / length, dz / length);
    }

    /// <summary>The first solid block surface at or below <paramref name="point"/> (32 blocks max).</summary>
    private static PresetPoint Ground(ReplayPresetContext c, PresetPoint point)
    {
        IBlockAccessor blocks = c.Hud.capi.World.BlockAccessor;
        for (int dy = 0; dy < 32; dy++)
        {
            BlockPos position = new((int)Math.Floor(point.X), (int)Math.Floor(point.Y) - dy, (int)Math.Floor(point.Z), point.Dimension);
            Block block = blocks.GetBlock(position, BlockLayersAccess.Solid);
            if (block.Id != 0 && block.CollisionBoxes is { Length: > 0 })
            {
                return new PresetPoint(point.X, position.Y + 1d, point.Z, point.Dimension);
            }
        }
        return point;
    }

    /// <summary>
    /// The replay actor nearest to the player (or to the camera's look point): a creature
    /// when <paramref name="creatureOnly"/>, otherwise preferring other players.
    /// </summary>
    private static DirectorReplayActorPathSource? NearestOtherActor(ReplayPresetContext c, bool creatureOnly)
    {
        PresetPoint origin = c.Subject;
        DirectorReplayActorPathSource? best = null;
        double bestScore = double.MaxValue;
        foreach (DirectorReplayActorPathSource source in c.Hud.owner.ReplayActorPathSources)
        {
            if (source.ActorKey == c.Player?.ActorKey || (creatureOnly && source.IsPlayer))
            {
                continue;
            }
            if (!c.Hud.owner.TryResolveReplayActor(source.ActorKey, out _, out Entity? entity)
                || entity is not EntityAgent agent
                || !agent.Alive)
            {
                continue;
            }
            double distance = Math.Sqrt((entity.Pos.X - origin.X) * (entity.Pos.X - origin.X)
                + (entity.Pos.Y - origin.Y) * (entity.Pos.Y - origin.Y)
                + (entity.Pos.Z - origin.Z) * (entity.Pos.Z - origin.Z));
            // Other players win over creatures for dialogue presets.
            double score = distance + (!creatureOnly && !source.IsPlayer ? 64d : 0d);
            if (distance < 96d && score < bestScore)
            {
                bestScore = score;
                best = source;
            }
        }
        return best;
    }

    private static string PrettyActorName(DirectorReplayActorPathSource source)
    {
        if (source.DisplayName.Length > 0 && !source.DisplayName.Contains(':')) return source.DisplayName;
        string code = source.EntityCode.Length > 0 ? source.EntityCode : source.DisplayName;
        int colon = code.IndexOf(':');
        if (colon >= 0) code = code[(colon + 1)..];
        string[] parts = code.Split('-', StringSplitOptions.RemoveEmptyEntries);
        string name = parts.Length > 0 ? parts[0] : code;
        return name.Length == 0 ? "???" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Back-to-back follow intervals on the player, alternating between two rows so each
    /// cut is a complete start/end pair.
    /// </summary>
    private static Guid? CutSequence(ReplayPresetContext c, string id,
        (double Length, Action<DirectorReplayElementKeyframe> Configure)[] shots,
        DirectorReplayActorPathSource? actor = null)
    {
        if ((actor ?? c.Player) is not DirectorReplayActorPathSource player || shots.Length == 0)
        {
            return null;
        }
        DirectorReplayElementTrack a = PresetNewTrack(c, DirectorReplayElementTrackType.FollowEntity, id, "A");
        DirectorReplayElementTrack b = PresetNewTrack(c, DirectorReplayElementTrackType.FollowEntity, id, "B");
        double at = 0d;
        for (int i = 0; i < shots.Length; i++)
        {
            AddFollowInterval(c, i % 2 == 0 ? a : b, player, at, shots[i].Length, k =>
            {
                k.FollowSmoothTimeSeconds = 0.25d;
                k.FollowFov = 65f;
                shots[i].Configure(k);
            });
            at += shots[i].Length;
        }
        return a.Id;
    }

    private static Action<DirectorReplayElementKeyframe>[] TrailerRigs(ReplayPresetContext c) => new Action<DirectorReplayElementKeyframe>[]
    {
        k => { k.FollowDistance = 3d; k.FollowHeight = -0.9d; k.FollowOffsetX = 0.4d; },
        k => { k.FollowDistance = 3d; k.FollowHeight = 0.2d; k.FollowOrbitAngleDegrees = 180d; },
        k => { k.FollowWorldAligned = true; k.FollowOrbitAngleDegrees = c.Pose.Yaw + 90d; k.FollowDistance = 6d; k.FollowHeight = 0.2d; },
        k => { k.FollowWorldAligned = true; k.FollowOrbitAngleDegrees = c.Pose.Yaw; k.FollowDistance = 0.05d; k.FollowHeight = 12d; },
        k => { k.FollowDistance = 1.2d; k.FollowHeight = -0.35d; k.FollowOffsetX = 0.5d; k.FollowFov = 75f; },
        k => { k.FollowDistance = 18d; k.FollowHeight = 2d; k.FollowFov = 25f; },
        k => { k.FollowDistance = 4d; k.FollowHeight = 1d; k.FollowOrbitSpeedDegreesPerSecond = 60d; },
        k => { k.FollowDistance = 14d; k.FollowHeight = 5d; k.FollowFov = 55f; }
    };

    /// <summary>Dolly toward (or away from) the looked-at point with a matching FOV change.</summary>
    private static Guid? DollyZoom(ReplayPresetContext c, string id, bool toward)
    {
        double start = Math.Max(2d, Distance(c.CameraPoint, c.Look));
        double move = toward ? Math.Min(6d, start * 0.6d) : -6d;
        double halfTan = Math.Tan(c.BaseFov * Math.PI / 360d);
        var fov = new List<FovStep>();
        const int samples = 6;
        for (int i = 0; i <= samples; i++)
        {
            double u = i / (double)samples;
            double eased = (1d - Math.Cos(u * Math.PI)) / 2d;
            double distance = start - move * eased;
            float value = (float)Math.Clamp(Math.Atan(halfTan * start / distance) * 360d / Math.PI, 5d, 150d);
            fov.Add(new FovStep(u * 5d, value, ILinear));
        }
        fov.Add(new FovStep(5.5d, c.BaseFov, IHold));
        FovPreset(c, true, fov.ToArray());
        return MovePreset(c, id, 0.5d, new MoveStep(0), new MoveStep(5, Forward: move));
    }

    /// <summary>
    /// The "something terrible just happened" shot: a face-on camera locked to the
    /// player's head while the field of view narrows into a close-up, optionally with
    /// the replay slowing down, then everything eases back.
    /// </summary>
    private static Guid? TragicPreset(ReplayPresetContext c, bool slowMotion)
    {
        const float closeUpFov = 26f;
        Guid? follow = FollowPreset(c, slowMotion ? "tragic-slowmo" : "tragic", k =>
        {
            k.FollowDistance = 3d;
            k.FollowHeight = 0.15d;
            k.FollowOrbitAngleDegrees = 180d;
            k.FollowSmoothTimeSeconds = 0.8d;
            k.FollowFov = c.BaseFov;
        }, length: 7d);
        FovPreset(c, true,
            new(0, c.BaseFov),
            new(3.2, closeUpFov, ILinear),
            new(6.2, closeUpFov),
            new(7, c.BaseFov, IHold));
        if (slowMotion)
        {
            SpeedPreset(c, new(0, 1, IEase), new(1.2, 0.3, ILinear), new(6, 0.3, IEase), new(7, 1));
        }
        return follow;
    }
}
