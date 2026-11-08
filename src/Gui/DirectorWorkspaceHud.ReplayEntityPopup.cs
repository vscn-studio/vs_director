using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using VSDirector.Replay;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private DirectorReplayEntityInfo? replayEntityPopupEntity;
    private string replayEntityPopupBodyPart = "head";

    private bool TryOpenReplayEntityPopup(int mouseX, int mouseY)
    {
        if (section != DirectorWorkspaceSection.Replay
            || !owner.IsReplayPlaying
            || !PointInsideGameViewport(mouseX, mouseY)
            || !TryGetWorldRay(mouseX, mouseY, out Vec3d rayStart, out Vec3d rayEnd)
            || !owner.TryPickReplayEntity(rayStart, rayEnd, out DirectorReplayEntityInfo entity))
        {
            return false;
        }

        replayEntityPopupEntity = entity;
        replayEntityPopupBodyPart = "head";
        replayFloatingEditorKind = ReplayFloatingEditorKind.EntityPopup;
        replayFloatingTrackId = Guid.Empty;
        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        keyframeAnchorX = (float)(mouseX / scale);
        keyframeAnchorY = (float)(mouseY / scale);
        replayFloatingX = mouseX / scale + 12d;
        replayFloatingY = mouseY / scale + 8d;
        ComposeWorkflowCanvas();
        return true;
    }

    private void RmlReplayEntityPopup(DirectorImGuiForm form)
    {
        if (replayEntityPopupEntity is not { } info) return;
        var entity = info.Entity;
        var position = owner.DisplayPosition(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
        string runtimeId = info.EntityId.ToString(CultureInfo.InvariantCulture)
            + (entity.EntityId == info.EntityId ? "" : " / " + entity.EntityId.ToString(CultureInfo.InvariantCulture));
        form.Group(info.DisplayName);
        Detail("replay-element-actor-key", info.ActorKey);
        Detail("replay-element-entity-id", runtimeId);
        Detail("replay-entity-popup-code", info.EntityCode);
        Detail("replay-entity-popup-position", FormattableString.Invariant($"{position.Item1:0.###}, {position.Item2:0.###}, {position.Item3:0.###}  D{entity.Pos.Dimension}"));
        form.Note(RmlLabel(info.IsGhost ? "replay-entity-popup-ghost" : "replay-entity-popup-live"));
        form.Select(RmlLabel("replay-entity-popup-body"), replayEntityPopupBodyPart,
            new[] { "head", "body", "root" }.Select(v => (v, RmlLabel("replay-entity-popup-" + (v == "body" ? "center" : v)))),
            v => OnReplayEntityPopupBodyPartChanged(v, true));
        Row("look-at", LookAtReplayPopupEntity, "track", AddReplayEntityTrackingKeyframe);
        Row("orbit", AddReplayEntityOrbitKeyframe, "follow", AddReplayEntityFollowSegment);
        Row("spectate-first", () => SpectateReplayPopupEntity(CameraFollowViewMode.FirstPerson),
            "spectate-third", () => SpectateReplayPopupEntity(CameraFollowViewMode.ThirdPerson));
        Row("copy-key", CopyReplayPopupActorKey, "copy-id", CopyReplayPopupEntityId);

        void Detail(string label, string value)
            => form.Detail(RmlLabel(label), value);
        void Row(string left, Func<bool> leftAction, string right, Func<bool> rightAction)
            => form.ButtonRow((RmlLabel("replay-entity-popup-" + left), () => leftAction(), true, "normal", ""),
                (RmlLabel("replay-entity-popup-" + right), () => rightAction(), true, "normal", ""));
    }

    private void ComposeReplayEntityPopup(
        GuiComposer composer,
        CairoFont small,
        CairoFont detail,
        ref int y)
    {
        if (replayEntityPopupEntity is not DirectorReplayEntityInfo info)
        {
            return;
        }

        Entity entity = info.Entity;
        string actorKey = string.IsNullOrWhiteSpace(info.ActorKey) ? "-" : info.ActorKey;
        string entityCode = string.IsNullOrWhiteSpace(info.EntityCode) ? "-" : info.EntityCode;
        string runtimeId = entity.EntityId == info.EntityId
            ? info.EntityId.ToString(CultureInfo.InvariantCulture)
            : info.EntityId.ToString(CultureInfo.InvariantCulture)
                + " / " + entity.EntityId.ToString(CultureInfo.InvariantCulture);
        string state = Lang.Get(info.IsGhost
            ? "vsdirector:replay-entity-popup-ghost"
            : "vsdirector:replay-entity-popup-live");
        (double displayX, double displayY, double displayZ) = owner.DisplayPosition(
            entity.Pos.X,
            entity.Pos.Y,
            entity.Pos.Z);

        composer
            .AddDirectorSingleLineText(
                Ellipsize(info.DisplayName, 46),
                DirectorFont.WhiteSmallBoldText(),
                ElementBounds.Fixed(10d, y, ReplayPropertyPopupWidth - 20d, 24d))
            .AddDirectorSingleLineText(
                Lang.Get("vsdirector:replay-element-actor-key") + ": " + Ellipsize(actorKey, 42),
                detail,
                ElementBounds.Fixed(10d, y + 24d, ReplayPropertyPopupWidth - 20d, 22d))
            .AddDirectorSingleLineText(
                Lang.Get("vsdirector:replay-element-entity-id") + ": " + runtimeId,
                detail,
                ElementBounds.Fixed(10d, y + 46d, ReplayPropertyPopupWidth - 20d, 22d))
            .AddDirectorSingleLineText(
                Lang.Get("vsdirector:replay-entity-popup-code") + ": " + Ellipsize(entityCode, 44),
                detail,
                ElementBounds.Fixed(10d, y + 68d, ReplayPropertyPopupWidth - 20d, 22d))
            .AddDirectorSingleLineText(
                Lang.Get("vsdirector:replay-entity-popup-position") + ": "
                    + displayX.ToString("0.###", CultureInfo.InvariantCulture) + ", "
                    + displayY.ToString("0.###", CultureInfo.InvariantCulture) + ", "
                    + displayZ.ToString("0.###", CultureInfo.InvariantCulture)
                    + "  D" + entity.Pos.Dimension.ToString(CultureInfo.InvariantCulture),
                detail,
                ElementBounds.Fixed(10d, y + 90d, ReplayPropertyPopupWidth - 20d, 22d))
            .AddDirectorSingleLineText(
                state,
                detail,
                ElementBounds.Fixed(10d, y + 112d, ReplayPropertyPopupWidth - 20d, 22d));
        y += 142;

        string[] bodyCodes = { "head", "body", "root" };
        string[] bodyNames =
        {
            Lang.Get("vsdirector:replay-entity-popup-head"),
            Lang.Get("vsdirector:replay-entity-popup-center"),
            Lang.Get("vsdirector:replay-entity-popup-root")
        };
        composer
            .AddDirectorSingleLineText(
                Lang.Get("vsdirector:replay-entity-popup-body"),
                small,
                ElementBounds.Fixed(10d, y + 6d, ReplayPropertyLabelWidth, 22d))
            .AddDropDown(
                bodyCodes,
                bodyNames,
                Math.Max(0, Array.IndexOf(bodyCodes, replayEntityPopupBodyPart)),
                OnReplayEntityPopupBodyPartChanged,
                ElementBounds.Fixed(
                    ReplayPropertyControlX,
                    y,
                    ReplayPropertyDropDownWidth,
                    28d),
                "replayEntityPopupBodyPart");
        y += 38;

        const double gap = 8d;
        double buttonWidth = (ReplayPropertyPopupWidth - 28d) / 2d;
        int buttonY = y;
        AddPopupButton(
            Lang.Get("vsdirector:replay-entity-popup-look-at"),
            LookAtReplayPopupEntity,
            0,
            0,
            "replayEntityLookAt");
        AddPopupButton(
            Lang.Get("vsdirector:replay-entity-popup-track"),
            AddReplayEntityTrackingKeyframe,
            1,
            0,
            "replayEntityTrack");
        AddPopupButton(
            Lang.Get("vsdirector:replay-entity-popup-orbit"),
            AddReplayEntityOrbitKeyframe,
            0,
            1,
            "replayEntityOrbit");
        AddPopupButton(
            Lang.Get("vsdirector:replay-entity-popup-spectate-first"),
            () => SpectateReplayPopupEntity(CameraFollowViewMode.FirstPerson),
            1,
            1,
            "replayEntityFirstPerson");
        AddPopupButton(
            Lang.Get("vsdirector:replay-entity-popup-spectate-third"),
            () => SpectateReplayPopupEntity(CameraFollowViewMode.ThirdPerson),
            0,
            2,
            "replayEntityThirdPerson");
        AddPopupButton(
            Lang.Get("vsdirector:replay-entity-popup-copy-key"),
            CopyReplayPopupActorKey,
            1,
            2,
            "replayEntityCopyKey");
        AddPopupButton(
            Lang.Get("vsdirector:replay-entity-popup-copy-id"),
            CopyReplayPopupEntityId,
            0,
            3,
            "replayEntityCopyId");
        AddPopupButton(
            Lang.Get("vsdirector:replay-entity-popup-follow"),
            AddReplayEntityFollowSegment,
            1,
            3,
            "replayEntityFollow");

        void AddPopupButton(
            string label,
            ActionConsumable action,
            int column,
            int row,
            string key)
        {
            composer.AddDirectorButton(
                label,
                action,
                ElementBounds.Fixed(
                    10d + column * (buttonWidth + gap),
                    buttonY + row * 36d,
                    buttonWidth,
                    30d),
                row == 0 ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                key);
        }
    }

    private void OnReplayEntityPopupBodyPartChanged(string value, bool selected)
    {
        if (selected && value is "head" or "body" or "root")
        {
            replayEntityPopupBodyPart = value;
        }
    }

    private bool LookAtReplayPopupEntity()
    {
        if (replayEntityPopupEntity is DirectorReplayEntityInfo info)
        {
            _ = owner.TryLookAtReplayEntity(info.Entity, out _);
        }
        return true;
    }

    private bool SpectateReplayPopupEntity(CameraFollowViewMode viewMode)
    {
        if (replayEntityPopupEntity is DirectorReplayEntityInfo info)
        {
            _ = owner.TryAttachReplayActor(info.ActorKey, viewMode, out _);
        }
        return true;
    }

    private bool CopyReplayPopupActorKey()
    {
        if (replayEntityPopupEntity is DirectorReplayEntityInfo info)
        {
            capi.Input.ClipboardText = info.ActorKey;
        }
        return true;
    }

    private bool CopyReplayPopupEntityId()
    {
        if (replayEntityPopupEntity is DirectorReplayEntityInfo info)
        {
            capi.Input.ClipboardText = info.EntityId.ToString(CultureInfo.InvariantCulture);
        }
        return true;
    }

    private bool AddReplayEntityTrackingKeyframe()
    {
        if (replayEntityPopupEntity is not DirectorReplayEntityInfo info
            || !owner.TryCaptureReplayCameraKeyframe(
                Lang.Get("vsdirector:replay-element-track-entity"),
                out CameraPreset pose,
                out _))
        {
            return true;
        }

        if (!owner.TryGetReplayTrackingTargetPose(
                info.Entity,
                replayEntityPopupBodyPart,
                out DirectorReplayTrackedTargetPose target))
        {
            return true;
        }

        DirectorReplayElementKeyframe keyframe = CreateReplayEntityKeyframe(
            DirectorReplayElementTrackType.TrackEntity,
            info);
        keyframe.BodyPart = replayEntityPopupBodyPart;
        DirectorReplayCameraElementMath.ConfigureTrackFromCamera(keyframe, target, pose);
        return CommitReplayEntityKeyframe(keyframe);
    }

    private bool AddReplayEntityFollowSegment()
    {
        if (replayEntityPopupEntity is not DirectorReplayEntityInfo info)
        {
            return true;
        }

        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        DirectorReplayElementTrack? track = timeline.Tracks.FirstOrDefault(item =>
            item.TrackType == DirectorReplayElementTrackType.FollowEntity);
        if (track is null)
        {
            track = new DirectorReplayElementTrack
            {
                TrackType = DirectorReplayElementTrackType.FollowEntity
            };
            timeline.Tracks.Add(track);
            if (!CommitReplayViewportTimeline(timeline))
            {
                return true;
            }
        }

        CloseReplayFloatingEditor();
        return AddFollowEntitySegment(track, info.ActorKey, info.EntityId);
    }

    private bool AddReplayEntityOrbitKeyframe()
    {
        if (replayEntityPopupEntity is not DirectorReplayEntityInfo info
            || !owner.TryCaptureReplayCameraKeyframe(
                Lang.Get("vsdirector:replay-element-camera-orbit"),
                out CameraPreset pose,
                out _))
        {
            return true;
        }

        if (!owner.TryGetReplayTrackingTargetPose(
                info.Entity,
                "body",
                out DirectorReplayTrackedTargetPose target))
        {
            return true;
        }

        double dx = pose.X - target.X;
        double dy = pose.Y - target.Y;
        double dz = pose.Z - target.Z;
        double horizontal = Math.Sqrt(dx * dx + dz * dz);
        double distance = Math.Sqrt(horizontal * horizontal + dy * dy);
        if (!double.IsFinite(distance) || distance < 0.01d)
        {
            distance = 8d;
            dx = 0d;
            dy = 0d;
            dz = distance;
            horizontal = distance;
        }

        DirectorReplayElementKeyframe keyframe = CreateReplayEntityKeyframe(
            DirectorReplayElementTrackType.CameraOrbit,
            info);
        keyframe.X = target.X;
        keyframe.Y = target.Y;
        keyframe.Z = target.Z;
        keyframe.Dimension = target.Dimension;
        keyframe.Distance = distance;
        keyframe.Yaw = (float)(Math.Atan2(dx, dz) * 180d / Math.PI);
        keyframe.Pitch = (float)(Math.Atan2(-dy, horizontal) * 180d / Math.PI);
        keyframe.Roll = 0f;
        // Capture the selected point as a stationary orbit center.
        keyframe.EntityActorKey = string.Empty;
        keyframe.EntityId = 0L;
        return CommitReplayEntityKeyframe(keyframe);
    }

    private DirectorReplayElementKeyframe CreateReplayEntityKeyframe(
        DirectorReplayElementTrackType type,
        DirectorReplayEntityInfo info)
        => new()
        {
            TrackType = type,
            TimeSeconds = ReplayTimelineTickAt(Math.Clamp(
                owner.ReplayTimeSeconds,
                0d,
                owner.ReplayDurationSeconds)) / ReplayTimelineTicksPerSecond,
            InterpolationToNext = DirectorReplayElementInterpolation.Smooth,
            EntityActorKey = info.ActorKey,
            EntityId = info.EntityId
        };

    private bool CommitReplayEntityKeyframe(DirectorReplayElementKeyframe keyframe)
    {
        DirectorReplayViewportTimeline timeline = owner.ReplayViewportTimeline;
        DirectorReplayElementTrack? track = timeline.Tracks.FirstOrDefault(item =>
            item.TrackType == keyframe.TrackType);
        if (track is null)
        {
            track = new DirectorReplayElementTrack { TrackType = keyframe.TrackType };
            timeline.Tracks.Add(track);
        }
        keyframe.TrackId = track.Id;
        int tick = ReplayTimelineTickAt(keyframe.TimeSeconds);
        DirectorReplayElementKeyframe? existing = timeline.ElementKeyframes.FirstOrDefault(item =>
            item.TrackId == track.Id
            && ReplayTimelineTickAt(item.TimeSeconds) == tick);
        if (existing is not null)
        {
            keyframe.Id = existing.Id;
            timeline.ElementKeyframes.Remove(existing);
        }
        timeline.ElementKeyframes.Add(keyframe);

        selectedReplayElementTrackId = track.Id;
        selectedReplayTimelineElementTrack = ReplayTimelineElementKindFor(keyframe.TrackType);
        selectedReplayElementKeyframeId = keyframe.Id;
        selectedReplayViewportClipId = Guid.Empty;
        selectedReplayMarkerId = Guid.Empty;
        selectedReplaySpeedKeyframeId = Guid.Empty;
        replayElementFieldsId = Guid.Empty;
        if (CommitReplayViewportTimeline(timeline))
        {
            ComposeInspector();
            ComposeTimeline();
            OpenSelectedReplayElementKeyframeEditor();
        }
        return true;
    }

}
