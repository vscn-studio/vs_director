using System.Globalization;
using Vintagestory.API.Client;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private DirectorImGuiWindow? keyframeRmlWindow;
    private readonly Dictionary<string, string> keyframeRmlValues = new();
    private float keyframeAnchorX, keyframeAnchorY;
    private bool keyframeAnchorPending;

    private void CaptureKeyframeAnchor()
    {
        float scale = (float)Math.Max(.001, GuiElement.scaled(1));
        keyframeAnchorX = capi.Input.MouseX / scale;
        keyframeAnchorY = capi.Input.MouseY / scale;
        keyframeAnchorPending = true;
    }

    private void DisposeKeyframeRml()
    {
        var window = keyframeRmlWindow;
        keyframeRmlWindow = null;
        window?.Dispose();
    }

    private string PathFloatingText(GuiComposer? composer, string key, string fallback)
        => keyframeRmlWindow is not null ? keyframeRmlValues.GetValueOrDefault(key, fallback)
            : composer?.GetTextInput(key)?.GetText() ?? fallback;

    private void ComposeKeyframeRml(bool replay)
    {
        floatingEditorDialog.RemoveComposer();
        bool opening = keyframeRmlWindow?.IsOpened != true;
        if (keyframeRmlWindow is null)
        {
            var window = new DirectorImGuiWindow(capi, "keyframe");
            keyframeRmlWindow = window;
            window.Closed += () =>
            {
                if (keyframeRmlWindow != window) return;
                keyframeRmlWindow = null;
                pathFloatingEditorKind = PathFloatingEditorKind.None;
                replayFloatingEditorKind = ReplayFloatingEditorKind.None;
                replayEntityPopupEntity = null;
                window.Dispose();
            };
        }
        bool entityPopup = replay && replayFloatingEditorKind == ReplayFloatingEditorKind.EntityPopup;
        bool addMenu = replay ? replayFloatingEditorKind is ReplayFloatingEditorKind.AddElement or ReplayFloatingEditorKind.PlayerPicker : pathFloatingEditorKind == PathFloatingEditorKind.AddElement;
        var form = new DirectorImGuiForm
        {
            // A scrolling property body needs a stable height. Auto-fitting a
            // child that fills the available space feeds its size back into itself.
            AutoHeight = entityPopup || addMenu,
            MinimumWidth = entityPopup || addMenu ? 220 : 360,
            ClassName = entityPopup || addMenu ? "keyframe-popover entity-popover" : "keyframe-popover"
        };
        if (replay)
        {
            if (entityPopup) RmlReplayEntityPopup(form);
            else if (replayFloatingEditorKind == ReplayFloatingEditorKind.PlayerPicker) RmlReplayPlayerPicker(form);
            else if (replayFloatingEditorKind is ReplayFloatingEditorKind.AddElement or ReplayFloatingEditorKind.Track or ReplayFloatingEditorKind.NewScene or ReplayFloatingEditorKind.RenameScene)
                RmlReplayTimelineMenu(form);
            else if (replayFloatingEditorKind == ReplayFloatingEditorKind.CameraKeyframe) RmlReplayCameraKeyframe(form);
            else RmlReplayElementKeyframe(form);
        }
        else
        {
            keyframeRmlValues.Clear();
            if (pathFloatingEditorKind is PathFloatingEditorKind.AddElement or PathFloatingEditorKind.Track) RmlPathTimelineMenu(form);
            else if (pathFloatingEditorKind == PathFloatingEditorKind.CameraKeyframe) RmlPathCameraKeyframe(form);
            else RmlPathElementKeyframe(form);
        }
        keyframeRmlWindow.Render(replay ? ReplayFloatingEditorTitle() : PathFloatingEditorTitle(), form,
            entityPopup ? 400 : addMenu ? (replay ? 680 : 280) : 440, entityPopup ? 460 : addMenu ? (replay ? 640 : 330) : Math.Clamp(180 + form.FieldCount * 56, 300, 680));
        if (opening || keyframeAnchorPending)
        {
            keyframeRmlWindow.SetAnchor(keyframeAnchorX, keyframeAnchorY);
            keyframeAnchorPending = false;
        }
        keyframeRmlWindow.Open();
    }

    private void RmlPathTimelineMenu(DirectorImGuiForm form)
    {
        if (pathFloatingEditorKind == PathFloatingEditorKind.AddElement)
        {
            var types = Enum.GetValues<DirectorPathElementTrackType>().Where(t => t != DirectorPathElementTrackType.CameraPath).ToArray();
            form.ChoiceList(types.Select(t => (t.ToString(), PathElementTrackName(t), (Action)(() => AddPathElementTrack(t)), "plus")).ToArray());
            return;
        }
        var track = pathEdit?.ElementTracks.FirstOrDefault(t => t.Id == pathFloatingTrackId);
        if (track is null) return;
        form.Text(RmlLabel("name"), pathFloatingTrackName, value => pathFloatingTrackName = value);
        KeyframeAction(form, "apply", ApplyPathTrackEditor, "primary");
        KeyframeAction(form, "replay-element-track-clear", ClearPathTrackKeyframes);
        if (track.TrackType != DirectorPathElementTrackType.CameraPath)
            KeyframeAction(form, "delete", DeletePathTrackFromEditor, "danger");
    }

    private void RmlReplayTimelineMenu(DirectorImGuiForm form)
    {
        if (replayFloatingEditorKind == ReplayFloatingEditorKind.AddElement)
        {
            RmlReplayPresetBrowser(form);
            return;
        }
        if (replayFloatingEditorKind is ReplayFloatingEditorKind.NewScene or ReplayFloatingEditorKind.RenameScene)
        {
            form.Text(RmlLabel("name"), replayFloatingSceneName, value => replayFloatingSceneName = value);
            KeyframeAction(form, "apply", SaveReplaySceneName, "primary");
            return;
        }
        var track = CurrentReplayViewportTimeline().Tracks.FirstOrDefault(t => t.Id == replayFloatingTrackId);
        if (track is null) return;
        if (track.TrackType == DirectorReplayElementTrackType.VoiceAudio)
        {
            form.Group(track.Name);
            KeyframeAction(form, "replay-element-track-clear", ClearReplayTrackKeyframes);
            return;
        }
        form.Text(RmlLabel("name"), replayFloatingTrackName, value => replayFloatingTrackName = value);
        KeyframeAction(form, "apply", ApplyReplayTrackEditor, "primary");
        KeyframeAction(form, "replay-element-track-clear", ClearReplayTrackKeyframes);
        KeyframeAction(form, "delete", DeleteReplayTrackFromEditor, "danger");
    }

    private void KeyframeAction(DirectorImGuiForm form, string label, Func<bool> action, string tone = "normal", bool refresh = false)
        => form.FooterButton(RmlLabel(label), () =>
        {
            action();
            if (refresh && keyframeRmlWindow is not null) ComposeKeyframeRml(section == DirectorWorkspaceSection.Replay);
        }, tone);

    private void PathFrameText(DirectorImGuiForm form, string label, string key, string value)
    {
        keyframeRmlValues[key] = value;
        form.Text(label, value, next => keyframeRmlValues[key] = next);
    }

    private void PathFrameNumber(DirectorImGuiForm form, string label, string field, double value)
    {
        string key = "pathFloating" + UpperFirst(field);
        keyframeRmlValues[key] = DirectorNumberFormatting.Format(value);
        float step = field.Contains("Time") || field.Contains("Duration") || field.Contains("Hold") ? .05f
            : field.Contains("Speed") || field.Contains("Smooth") || field.Contains("Delay") ? .1f : 1;
        form.NumberStep(label, value, step, next => keyframeRmlValues[key] = DirectorNumberFormatting.Format(next));
    }

    private void RmlPathCameraKeyframe(DirectorImGuiForm form)
    {
        if (SelectedPathFloatingCameraKeyframe() is not { } frame) return;
        PathFrameText(form, RmlLabel("name"), "pathFloatingCameraName", frame.Name);
        PathFrameNumber(form, RmlLabel("replay-element-time-seconds"), "cameraTime", frame.TimeSeconds);
        PathFrameNumber(form, RmlLabel("keyframe-duration"), "cameraDuration", frame.DurationSeconds);
        PathFrameNumber(form, RmlLabel("keyframe-hold"), "cameraHold", frame.HoldSeconds);
        pathFloatingInterpolation = (frame.InterpolationToNext ?? pathEdit!.Interpolation) switch
        {
            CameraPathInterpolation.Step => "hold", CameraPathInterpolation.EaseInOut => "ease",
            CameraPathInterpolation.Bezier => "bezier", _ => "linear"
        };
        RmlPathFrameInterpolation(form, true);
        PathFrameNumber(form, RmlLabel("segment-start-speed"), "cameraStartSpeed", frame.SegmentStartSpeed);
        PathFrameNumber(form, RmlLabel("segment-end-speed"), "cameraEndSpeed", frame.SegmentEndSpeed);
        var p = owner.DisplayPosition(frame.X, frame.Y, frame.Z);
        PathFrameNumber(form, "X", "cameraX", p.Item1);
        PathFrameNumber(form, "Y", "cameraY", p.Item2);
        PathFrameNumber(form, "Z", "cameraZ", p.Item3);
        PathFrameNumber(form, RmlLabel("camera-dimension"), "cameraDimension", frame.Dimension);
        PathFrameNumber(form, "Yaw", "cameraYaw", frame.Yaw);
        PathFrameNumber(form, "Pitch", "cameraPitch", frame.Pitch);
        KeyframeAction(form, "apply", ApplyPathCameraKeyframe, "primary");
        KeyframeAction(form, "recapture", RecapturePathCameraKeyframe, refresh: true);
        KeyframeAction(form, "delete", DeletePathCameraKeyframe, "danger");
    }

    private void RmlPathFrameInterpolation(DirectorImGuiForm form, bool camera)
    {
        string[] codes = camera ? ["hold", "linear", "ease", "bezier"] : ["hold", "linear", "ease"];
        form.Select(RmlLabel("replay-element-interpolation"), pathFloatingInterpolation,
            codes.Select(code => (code, RmlLabel(code == "bezier" ? "interpolation-bezier" : "replay-speed-interpolation-" + code))),
            value => pathFloatingInterpolation = value);
    }

    private void RmlPathElementKeyframe(DirectorImGuiForm form)
    {
        if (SelectedPathElementKeyframe() is not { } frame) return;
        void Number(string field, double value, string label) => PathFrameNumber(form, RmlLabel(label), field, value);
        Number("elementTime", frame.TimeSeconds, "replay-element-time-seconds");
        pathFloatingInterpolation = frame.InterpolationToNext switch
        {
            DirectorPathElementInterpolation.Hold => "hold", DirectorPathElementInterpolation.EaseInOut => "ease", _ => "linear"
        };
        if (frame.TrackType != DirectorPathElementTrackType.Marker) RmlPathFrameInterpolation(form, false);
        switch (frame.TrackType)
        {
            case DirectorPathElementTrackType.Fov:
                keyframeRmlValues["pathFloatingElementFov"] = DirectorNumberFormatting.Format(frame.Fov);
                form.Slider(RmlLabel("fov"), frame.Fov, DirectorPathElementTimeline.MinFovDegrees, DirectorPathElementTimeline.MaxFovDegrees, 1,
                    v => keyframeRmlValues["pathFloatingElementFov"] = DirectorNumberFormatting.Format(v));
                break;
            case DirectorPathElementTrackType.Roll: Number("elementRoll", frame.Roll, "roll"); break;
            case DirectorPathElementTrackType.Speed: Number("elementRate", frame.PlaybackRate, "path-element-playback-rate"); break;
            case DirectorPathElementTrackType.Marker:
                PathFrameText(form, RmlLabel("name"), "pathFloatingMarkerLabel", frame.Label); break;
            case DirectorPathElementTrackType.CameraShake:
                Number("elementFrequencyX", frame.FrequencyX, "replay-element-frequency-x");
                Number("elementAmplitudeX", frame.AmplitudeX, "replay-element-amplitude-x");
                Number("elementFrequencyY", frame.FrequencyY, "replay-element-frequency-y");
                Number("elementAmplitudeY", frame.AmplitudeY, "replay-element-amplitude-y");
                break;
            case DirectorPathElementTrackType.LookAt:
                PathFrameText(form, RmlLabel("replay-element-entity-id"), "pathFloatingTargetEntity", frame.TargetEntityId.ToString(CultureInfo.InvariantCulture));
                pathFloatingBodyPart = frame.BodyPart;
                form.Select(RmlLabel("replay-element-body-part"), frame.BodyPart,
                    new[] { "head", "body", "root" }.Select(v => (v, RmlLabel("replay-element-body-" + (v == "body" ? "center" : v)))),
                    v => pathFloatingBodyPart = v);
                var p = owner.DisplayPosition(frame.X, frame.Y, frame.Z);
                PathFrameNumber(form, "X", "elementX", p.Item1);
                PathFrameNumber(form, "Y", "elementY", p.Item2);
                PathFrameNumber(form, "Z", "elementZ", p.Item3);
                Number("elementDimension", frame.Dimension, "camera-dimension");
                Number("elementOffsetX", frame.OffsetX, "replay-element-offset-x");
                Number("elementOffsetY", frame.OffsetY, "replay-element-offset-y");
                Number("elementOffsetZ", frame.OffsetZ, "replay-element-offset-z");
                Number("elementYawOffset", frame.YawOffset, "replay-element-yaw-offset");
                Number("elementPitchOffset", frame.PitchOffset, "replay-element-pitch-offset");
                break;
        }
        KeyframeAction(form, "apply", ApplyPathElementKeyframe, "primary");
        KeyframeAction(form, "delete", DeletePathElementKeyframe, "danger");
    }

    private void RmlReplayCameraKeyframe(DirectorImGuiForm form)
    {
        if (SelectedReplayViewportClip() is not { } clip) return;
        SyncReplayViewportFields(clip);
        form.Text(RmlLabel("name"), replayViewportNameText, v => replayViewportNameText = v);
        form.NumericText(RmlLabel("replay-element-time-seconds"), replayViewportStartText, v => replayViewportStartText = v, .05f);
        form.Select(RmlLabel("replay-camera-interpolation"), replayViewportInterpolationCode,
            new[] { "step", "linear", "ease", "bezier" }.Select(v => (v, RmlLabel("replay-camera-interpolation-" + v))),
            v => replayViewportInterpolationCode = v);
        form.NumericText("X", replayViewportXText, v => replayViewportXText = v);
        form.NumericText("Y", replayViewportYText, v => replayViewportYText = v);
        form.NumericText("Z", replayViewportZText, v => replayViewportZText = v);
        form.NumericText(RmlLabel("camera-dimension"), replayViewportDimensionText, v => replayViewportDimensionText = v);
        form.NumericText("Yaw", replayViewportYawText, v => replayViewportYawText = v);
        form.NumericText("Pitch", replayViewportPitchText, v => replayViewportPitchText = v);
        form.NumericText(RmlLabel("roll"), replayViewportRollText, v => replayViewportRollText = v);
        form.Check(RmlLabel("replay-element-ends"), replayViewportEndsShot, v => replayViewportEndsShot = v);
        KeyframeAction(form, "apply", ApplyReplayViewport, "primary");
        KeyframeAction(form, "recapture", RecaptureSelectedReplayCameraKeyframe, refresh: true);
        KeyframeAction(form, "delete", DeleteReplayViewport, "danger");
    }

    private void RmlReplayElementKeyframe(DirectorImGuiForm form)
    {
        if (SelectedReplayElementKeyframe() is not { } frame) return;
        SyncReplayElementFields(frame);
        void Text(string field, string label) => form.Text(label, ReplayElementField(field), v => replayElementFieldValues[field] = v);
        void Number(string field, string label)
        {
            var range = ReplayPropertyRangeFor("replayElement" + UpperFirst(field));
            void Set(double v) => replayElementFieldValues[field] = DirectorNumberFormatting.Format(v);
            if (range?.TimeInput == true)
                form.Time(label, TimeSpan.FromMinutes(Math.Round(ReplayElementNumber(field) * 1440 / 24000) % 1440),
                    v => Set(Math.Round(v.TotalMinutes * 24000 / 1440)));
            else if (range is not null) form.Slider(label, ReplayElementNumber(field), range.MinimumValue, range.MaximumValue, range.Step / range.Scale, Set);
            else form.Number(label, ReplayElementNumber(field), Set);
        }
        void N(string field, string label) => Number(field, RmlLabel(label));
        void Select(string field, string label, params (string, string)[] choices)
            => form.Select(RmlLabel(label), ReplayElementField(field), choices.Select(v => (v.Item1, RmlLabel(v.Item2))), v =>
            {
                replayElementFieldValues[field] = v;
                // These choices show or hide other fields.
                if (field is "trackMode" or "timeEffect" && keyframeRmlWindow is not null) ComposeKeyframeRml(true);
            });
        void Check(string field, string label, bool clearView = false)
            => form.Check(RmlLabel(label), ReplayElementField(field) == "true", v => SetReplayElementSwitch(field, v), !clearView || owner.HasClearView);
        if (frame.TrackType != DirectorReplayElementTrackType.Timelapse) N("time", "replay-element-time-seconds");
        if (frame.TrackType is not (DirectorReplayElementTrackType.Freeze or DirectorReplayElementTrackType.BlockOverride
            or DirectorReplayElementTrackType.VoiceAudio or DirectorReplayElementTrackType.Timelapse or DirectorReplayElementTrackType.FollowEntity
            or DirectorReplayElementTrackType.Title))
            form.Select(RmlLabel("replay-element-interpolation"), ReplayElementField("interpolation"),
                new[] { "smooth", "linear", "ease-in", "ease-out", "ease-in-out", "hold", "hermite" }
                    .Select(v => (v, RmlLabel((v is "linear" or "hold" ? "replay-speed-interpolation-" : "replay-element-interpolation-") + v))),
                v => replayElementFieldValues["interpolation"] = v);
        switch (frame.TrackType)
        {
            case DirectorReplayElementTrackType.CameraOrbit:
                Number("x", "X"); Number("y", "Y"); Number("z", "Z"); N("dimension", "camera-dimension");
                N("distance", "replay-element-distance"); Number("yaw", "Yaw"); Number("pitch", "Pitch"); N("roll", "roll"); break;
            case DirectorReplayElementTrackType.TrackEntity:
            {
                Select("trackMode", "replay-track-mode", ("attached", "replay-track-mode-attached"), ("tripod", "replay-track-mode-tripod"),
                    ("shoulder", "replay-track-mode-shoulder"));
                string mode = ReplayElementField("trackMode", "attached");
                RmlReplayPlayerSelect(form);
                Text("entityActorKey", RmlLabel("replay-element-actor-key")); Text("entityId", RmlLabel("replay-element-entity-id"));
                Select("bodyPart", "replay-element-body-part", ("head", "replay-element-body-head"), ("body", "replay-element-body-center"), ("root", "replay-element-body-root"));
                if (mode == "attached")
                {
                    foreach (string axis in new[] { "X", "Y", "Z" })
                    {
                        N("offset" + axis, "replay-element-offset-" + axis.ToLowerInvariant());
                        N("viewOffset" + axis, "replay-element-view-" + axis.ToLowerInvariant());
                    }
                    N("yaw", "replay-element-yaw-offset"); N("pitch", "replay-element-pitch-offset");
                }
                else
                {
                    RmlReplaySecondaryPlayerSelect(form);
                    if (mode == "tripod")
                    {
                        Number("x", "X"); Number("y", "Y"); Number("z", "Z");
                        N("targetBlend", "replay-track-target-blend");
                    }
                    else
                    {
                        N("viewOffsetX", "replay-track-shoulder-side"); N("viewOffsetY", "replay-track-shoulder-up");
                        N("viewOffsetZ", "replay-track-shoulder-back");
                    }
                    Check("frameGroup", "replay-track-frame-group");
                    Check("autoFrame", "replay-track-auto-frame");
                    N("frameSize", "replay-track-frame-size");
                    N("trackSmooth", "replay-track-smooth");
                }
                N("roll", "roll");
                Check("clearView", "follow-clear-view", true); break;
            }
            case DirectorReplayElementTrackType.FollowEntity:
                Select("followRole", "replay-follow-role", ("start", "replay-follow-start"), ("end", "replay-follow-end"));
                // Keep start parameters editable when changing an end keyframe into a start.
                RmlReplayPlayerSelect(form);
                Text("entityActorKey", RmlLabel("replay-element-actor-key")); Text("entityId", RmlLabel("replay-element-entity-id"));
                Select("followView", "follow-view-mode", ("first", "follow-view-first"), ("third", "follow-view-third"));
                Select("followMotion", "follow-motion-mode", ("immediate", "follow-motion-immediate"), ("smooth", "follow-motion-smooth"), ("delayed", "follow-motion-delayed"));
                Select("followAnchor", "follow-anchor", ("head", "follow-anchor-head"), ("body", "follow-anchor-body"));
                Check("followClearView", "follow-clear-view", true);
                foreach (var field in new[] { ("followOffsetX", "offset-x"), ("followOffsetY", "offset-y"), ("followOffsetZ", "offset-z"),
                    ("followDistance", "distance"), ("followHeight", "height"), ("followOrbitAngle", "orbit-angle"), ("followOrbitSpeed", "orbit-speed"),
                    ("followSmooth", "smooth-time"), ("followDelay", "delay"), ("followFov", "fov") }) N(field.Item1, "follow-" + field.Item2);
                N("roll", "roll");
                Check("followWorld", "replay-follow-world-aligned");
                Select("followBlend", "replay-follow-blend", ("hold", "replay-follow-blend-none"), ("linear", "replay-speed-interpolation-linear"),
                    ("ease-in", "replay-element-interpolation-ease-in"), ("ease-out", "replay-element-interpolation-ease-out"),
                    ("ease-in-out", "replay-element-interpolation-ease-in-out"));
                break;
            case DirectorReplayElementTrackType.CameraShake:
                Check("split", "replay-element-split-axes");
                N("frequencyX", "replay-element-frequency-x"); N("amplitudeX", "replay-element-amplitude-x");
                N("frequencyY", "replay-element-frequency-y"); N("amplitudeY", "replay-element-amplitude-y");
                N("roll", "replay-element-shake-roll"); break;
            case DirectorReplayElementTrackType.Fov: N("fov", "fov"); break;
            case DirectorReplayElementTrackType.Speed: N("speed", "replay-element-speed"); break;
            case DirectorReplayElementTrackType.Timelapse: N("timelapseDuration", "replay-element-duration-seconds"); break;
            case DirectorReplayElementTrackType.TimeOfDay: N("timeOfDay", "replay-element-time-tick"); break;
            case DirectorReplayElementTrackType.Freeze:
                Check("frozen", "replay-element-frozen");
                Select("timeEffect", "replay-time-effect", ("freeze", "replay-time-effect-freeze"), ("rewind", "replay-time-effect-rewind"),
                    ("stutter", "replay-time-effect-stutter"), ("slow", "replay-time-effect-slow"));
                switch (ReplayElementField("timeEffect", "freeze"))
                {
                    case "rewind": N("effectSeconds", "replay-time-effect-seconds"); N("effectRate", "replay-time-effect-duration"); break;
                    case "stutter": N("effectRate", "replay-time-effect-fps"); break;
                    case "slow": N("effectRate", "replay-time-effect-rate"); break;
                    default: N("freezeDelay", "replay-element-freeze-delay"); Check("cameraContinues", "replay-time-effect-camera"); break;
                }
                break;
            case DirectorReplayElementTrackType.ColorGrade:
                N("gradeSepia", "replay-grade-sepia"); N("gradeContrast", "replay-grade-contrast");
                N("gradeVignette", "replay-grade-vignette"); N("gradeBloom", "replay-grade-bloom"); N("gradeGlitch", "replay-grade-glitch");
                N("gradeFade", "replay-grade-fade"); break;
            case DirectorReplayElementTrackType.Title:
                Text("titleText", RmlLabel("replay-title-text")); Text("titleSubText", RmlLabel("replay-title-subtext"));
                N("titleDuration", "replay-title-duration");
                Select("titlePosition", "replay-title-position", ("lower", "replay-title-position-lower"), ("center", "replay-title-position-center"),
                    ("top", "replay-title-position-top"));
                break;
            case DirectorReplayElementTrackType.BlockOverride:
                Number("blockX", "X"); Number("blockY", "Y"); Number("blockZ", "Z"); N("dimension", "camera-dimension");
                Text("blockCode", RmlLabel("replay-element-block-code")); break;
            case DirectorReplayElementTrackType.VoiceAudio: N("volume", "replay-element-volume"); break;
        }
        if (frame.TrackType is not (DirectorReplayElementTrackType.Freeze or DirectorReplayElementTrackType.Timelapse
            or DirectorReplayElementTrackType.FollowEntity or DirectorReplayElementTrackType.Title
            or DirectorReplayElementTrackType.VoiceAudio or DirectorReplayElementTrackType.BlockOverride))
            Check("endsElement", "replay-element-ends");
        KeyframeAction(form, "apply", ApplySelectedReplayElementKeyframe, "primary");
        KeyframeAction(form, "delete", DeleteSelectedReplayElementKeyframe, "danger");
    }
}
