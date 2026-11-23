using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using VSDirector.Replay;
using VSDirector.Runtime;

namespace VSDirector;

/// <summary>
/// Replay camera features used by the preset library: tripod and over-the-shoulder
/// tracking, colour grade and title cards, and pulling the camera in front of blocks
/// that hide its subject.
/// </summary>
public sealed partial class VSDirectorModSystem
{
    private readonly record struct LookAtSmoothing(double Time, float Yaw, float Pitch, float Fov);

    private readonly Dictionary<Guid, LookAtSmoothing> replayLookAtSmoothing = new();

    /// <summary>
    /// Evaluates a TrackEntity keyframe in Tripod or OverShoulder mode: resolves the
    /// primary/secondary actors (or every player for group framing), aims the camera at
    /// the blended subject and optionally auto-frames and smooths the pan.
    /// </summary>
    private bool TryEvaluateReplayLookAtTrack(
        Guid trackId,
        DirectorReplayElementKeyframe tracking,
        double time,
        float fov,
        out CameraPreset pose,
        out DirectorReplayCameraGuideTarget guideTarget,
        out Entity? primaryEntity)
    {
        pose = new CameraPreset();
        guideTarget = default;
        primaryEntity = null;
        if (replay is null)
        {
            return false;
        }

        DirectorReplayCameraElementMath.LookSubject subject;
        int dimension;
        DirectorReplayCameraElementMath.LookSubject? anchor = null;
        Entity? secondary = tracking.SecondaryEntityActorKey.Length > 0 || tracking.SecondaryEntityId != 0L
            ? replay.ResolveReplayActor(tracking.SecondaryEntityActorKey, null, tracking.SecondaryEntityId)
            : null;
        if (tracking.TrackFrameGroup)
        {
            List<DirectorReplayTrackedTargetPose> members = new();
            foreach (DirectorReplayActorPathSource source in replay.ActorPathSources.Where(item => item.IsPlayer))
            {
                Entity? member = replay.ResolveReplayActor(source.ActorKey, null, source.EntityId);
                if (member is not null
                    && TryCreateReplayTrackingTargetPose(member, "body", out DirectorReplayTrackedTargetPose memberPose))
                {
                    members.Add(memberPose);
                    primaryEntity ??= member;
                }
            }
            if (members.Count == 0)
            {
                return false;
            }
            double cx = members.Average(item => item.X);
            double cy = members.Average(item => item.Y);
            double cz = members.Average(item => item.Z);
            double radius = members.Max(item => Math.Sqrt(
                (item.X - cx) * (item.X - cx) + (item.Y - cy) * (item.Y - cy) + (item.Z - cz) * (item.Z - cz))) + 1.2d;
            subject = new(cx, cy, cz, radius);
            dimension = members[0].Dimension;
        }
        else
        {
            primaryEntity = replay.ResolveReplayActor(tracking.EntityActorKey, null, tracking.EntityId);
            if (primaryEntity is null
                || !TryCreateReplayTrackingTargetPose(primaryEntity, tracking.BodyPart,
                    out DirectorReplayTrackedTargetPose primaryPose))
            {
                return false;
            }
            dimension = primaryPose.Dimension;
            double radius = SubjectRadius(primaryEntity);
            double blend = tracking.TrackMode == DirectorReplayTrackMode.OverShoulder ? 0d : tracking.TargetBlend;
            if (secondary is not null
                && blend > 0.0001d
                && TryCreateReplayTrackingTargetPose(secondary, tracking.BodyPart,
                    out DirectorReplayTrackedTargetPose secondaryPose))
            {
                double dx = secondaryPose.X - primaryPose.X;
                double dy = secondaryPose.Y - primaryPose.Y;
                double dz = secondaryPose.Z - primaryPose.Z;
                double separation = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                // Between the two, the frame grows to hold both; at either end only one.
                double both = 1d - Math.Abs(blend * 2d - 1d);
                subject = new(primaryPose.X + dx * blend, primaryPose.Y + dy * blend, primaryPose.Z + dz * blend,
                    Math.Max(radius, both * (separation / 2d + radius)));
            }
            else
            {
                subject = new(primaryPose.X, primaryPose.Y, primaryPose.Z, radius);
            }
        }

        if (tracking.TrackMode == DirectorReplayTrackMode.OverShoulder)
        {
            if (secondary is null
                || !TryCreateReplayTrackingTargetPose(secondary, "head", out DirectorReplayTrackedTargetPose shoulder))
            {
                return false;
            }
            anchor = new(shoulder.X, shoulder.Y, shoulder.Z, 0.5d);
        }

        pose = DirectorReplayCameraElementMath.EvaluateLookAt(tracking, subject, anchor, dimension, fov);
        if (tracking.TrackSmoothTimeSeconds > 0.0001d)
        {
            if (replayLookAtSmoothing.TryGetValue(trackId, out LookAtSmoothing previous)
                && time >= previous.Time - 1e-6d
                && time - previous.Time <= 0.5d)
            {
                double amount = 1d - Math.Exp(-(time - previous.Time) / tracking.TrackSmoothTimeSeconds);
                pose.Yaw = (float)(previous.Yaw + DeltaDegrees(previous.Yaw, pose.Yaw) * amount);
                pose.Pitch = (float)(previous.Pitch + (pose.Pitch - previous.Pitch) * amount);
                pose.Fov = (float)(previous.Fov + (pose.Fov - previous.Fov) * amount);
            }
            replayLookAtSmoothing[trackId] = new LookAtSmoothing(time, pose.Yaw, pose.Pitch, pose.Fov);
        }
        guideTarget = new DirectorReplayCameraGuideTarget(subject.X, subject.Y, subject.Z, dimension,
            primaryEntity?.EntityId ?? 0L);
        return true;
    }

    private static double SubjectRadius(Entity entity)
        => entity.SelectionBox is Cuboidf box
            ? Math.Clamp(Math.Max(box.Y2 - box.Y1, Math.Max(box.X2 - box.X1, box.Z2 - box.Z1)) * 0.6d, 0.4d, 8d)
            : 1.1d;

    private static double DeltaDegrees(double from, double to)
    {
        double delta = (to - from) % 360d;
        if (delta > 180d) delta -= 360d;
        if (delta < -180d) delta += 360d;
        return delta;
    }

    /// <summary>
    /// Publishes the colour grade, title card and rewind glitch for the current camera
    /// time. Renderers read them from <see cref="DirectorReplayVisualRuntime"/>.
    /// </summary>
    private void UpdateReplayScreenEffects(DirectorReplayViewportTimeline timeline, double time)
    {
        DirectorReplayScreenGrade grade = default;
        if (DirectorReplayElementTimeline.TrySample(timeline, DirectorReplayElementTrackType.ColorGrade, time,
                out DirectorReplayElementKeyframe sampled))
        {
            grade = new DirectorReplayScreenGrade(sampled.GradeSepia, sampled.GradeContrast,
                sampled.GradeVignette, sampled.GradeBloom, sampled.GradeGlitch, sampled.GradeFade);
        }
        double rewind = replay?.PreparedRewindAmount ?? 0d;
        if (rewind > 0d)
        {
            grade = grade with { Glitch = Math.Max(grade.Glitch, (float)(0.35d * rewind)) };
        }
        DirectorReplayVisualRuntime.Grade = grade.IsNeutral ? null : grade;
        DirectorReplayVisualRuntime.Title = ActiveReplayTitle(timeline, time);
    }

    private static DirectorReplayActiveTitle? ActiveReplayTitle(DirectorReplayViewportTimeline timeline, double time)
    {
        foreach (DirectorReplayElementTrack track in Enumerable.Reverse(timeline.Tracks))
        {
            if (!track.Enabled || track.TrackType != DirectorReplayElementTrackType.Title)
            {
                continue;
            }
            DirectorReplayElementKeyframe? card = timeline.ElementKeyframes
                .Where(item => item.TrackId == track.Id
                    && item.TimeSeconds <= time + 1e-6d
                    && time < item.TimeSeconds + item.TitleDurationSeconds)
                .OrderByDescending(item => item.TimeSeconds)
                .FirstOrDefault();
            if (card is null || (card.Text.Length == 0 && card.SubText.Length == 0))
            {
                continue;
            }
            double elapsed = time - card.TimeSeconds;
            double remaining = card.TimeSeconds + card.TitleDurationSeconds - time;
            double fade = Math.Min(0.5d, card.TitleDurationSeconds / 4d);
            double alpha = Math.Clamp(Math.Min(elapsed, remaining) / Math.Max(0.01d, fade), 0d, 1d);
            return new DirectorReplayActiveTitle(card.Text, card.SubText, card.TitlePosition, (float)alpha);
        }
        return null;
    }

    /// <summary>
    /// Moves the camera toward its subject until no solid block is between them, like a
    /// third-person camera does against walls.
    /// </summary>
    private CameraPreset AvoidReplayCameraOcclusion(CameraPreset pose, DirectorReplayCameraGuideTarget target)
    {
        if (capi is null)
        {
            return pose;
        }
        double dx = pose.X - target.X;
        double dy = pose.Y - target.Y;
        double dz = pose.Z - target.Z;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!double.IsFinite(length) || length < 0.6d || length > 256d)
        {
            return pose;
        }
        dx /= length;
        dy /= length;
        dz /= length;
        // Start a little away from the subject so its own block does not count.
        const double step = 0.2d;
        for (double distance = 0.6d; distance <= length; distance += step)
        {
            if (!IsReplayPointSolid(target.X + dx * distance, target.Y + dy * distance, target.Z + dz * distance,
                    target.Dimension))
            {
                continue;
            }
            double safe = Math.Max(0.3d, distance - 0.3d);
            CameraPreset moved = pose.DeepClone();
            moved.X = target.X + dx * safe;
            moved.Y = target.Y + dy * safe;
            moved.Z = target.Z + dz * safe;
            return moved;
        }
        return pose;
    }

    private bool IsReplayPointSolid(double x, double y, double z, int dimension)
    {
        int blockX = (int)Math.Floor(x);
        int blockY = (int)Math.Floor(y);
        int blockZ = (int)Math.Floor(z);
        BlockPos position = new(blockX, blockY, blockZ, dimension);
        Block block = capi!.World.BlockAccessor.GetBlock(position, BlockLayersAccess.Solid);
        if (block.Id == 0)
        {
            return false;
        }
        Cuboidf[] boxes = block.GetCollisionBoxes(capi.World.BlockAccessor, position) ?? Array.Empty<Cuboidf>();
        double localX = x - blockX;
        double localY = y - blockY;
        double localZ = z - blockZ;
        return boxes.Any(box => localX >= box.X1 && localX <= box.X2
            && localY >= box.Y1 && localY <= box.Y2
            && localZ >= box.Z1 && localZ <= box.Z2);
    }

    private void ResetReplayScreenEffects()
    {
        replayLookAtSmoothing.Clear();
        DirectorReplayVisualRuntime.Grade = null;
        DirectorReplayVisualRuntime.Title = null;
    }
}
