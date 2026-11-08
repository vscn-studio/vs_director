using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private int pathFloatingCameraIndex = -1;

    private bool OpenPathElementMenu()
    {
        pathFloatingEditorKind = PathFloatingEditorKind.AddElement;
        ShowPathFloatingEditor();
        return true;
    }

    private bool OpenPathTrackEditor(Guid trackId)
    {
        if (pathEdit?.ElementTracks.FirstOrDefault(track => track.Id == trackId)
            is not DirectorPathElementTrack track)
        {
            return true;
        }
        selectedPathElementTrackId = trackId;
        pathFloatingTrackId = trackId;
        pathFloatingTrackName = track.Name.Length == 0
            ? PathElementTrackName(track.TrackType)
            : track.Name;
        pathFloatingEditorKind = PathFloatingEditorKind.Track;
        ShowPathFloatingEditor();
        return true;
    }

    private bool AddPathKeyframeForTrack(Guid trackId)
    {
        if (pathEdit?.ElementTracks.FirstOrDefault(track => track.Id == trackId)
            is not DirectorPathElementTrack track)
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        double time = track.TrackType == DirectorPathElementTrackType.CameraPath
            ? CameraPathAuthoring.ResolveCaptureTime(
                pathEdit,
                pathEditorPlayhead,
                pathEditorPlayheadWasPositioned)
            : DirectorPathElementTimeline.TickAt(pathEditorPlayhead)
                / PathTimelineTicksPerSecond;
        if (track.TrackType == DirectorPathElementTrackType.CameraPath)
        {
            if (!owner.TryCapturePathKeyframePose(pathEdit, time,
                    out CameraKeyframe frame, out _, preferBasePathPreview: true))
            {
                return true;
            }
            int index = CameraPathTiming.UpsertKeyframe(pathEdit, frame);
            SelectOnlyPathKeyframe(index);
            SetPathAuthoringPlayhead(
                CameraPathAuthoring.NextAppendTime(pathEdit),
                refreshPathMarker: false,
                explicitlyPositioned: false);
            selectedPathElementKeyframeId = Guid.Empty;
            selectedPathElementTrackId = track.Id;
            PersistPathTimelineEdit(before);
            return true;
        }

        string defaultMarkerLabel = Lang.Get("vsdirector:path-element-marker-default");
        if (!DirectorPathElementTimeline.TryUpsertKeyframeAtTime(
                pathEdit,
                track.Id,
                time,
                fallbackPose: null,
                defaultMarkerLabel,
                out DirectorPathElementKeyframe keyframe))
        {
            if (!owner.TryCapturePathKeyframePose(pathEdit, time,
                    out CameraKeyframe pose, out _)
                || !DirectorPathElementTimeline.TryUpsertKeyframeAtTime(
                    pathEdit,
                    track.Id,
                    time,
                    pose,
                    defaultMarkerLabel,
                    out keyframe))
            {
                return true;
            }
        }
        selectedPathElementTrackId = track.Id;
        selectedPathElementKeyframeId = keyframe.Id;
        selectedPathKeyframes.Clear();
        selectedPathKeyframeIndex = -1;
        PersistPathTimelineEdit(before);
        return true;
    }

    private void OpenPathKeyframeEditor(
        PathTimelineDragKind kind,
        int cameraIndex,
        Guid elementId)
    {
        if (kind == PathTimelineDragKind.Camera)
        {
            pathFloatingCameraIndex = cameraIndex;
            pathFloatingEditorKind = PathFloatingEditorKind.CameraKeyframe;
        }
        else if (kind == PathTimelineDragKind.Element)
        {
            selectedPathElementKeyframeId = elementId;
            pathFloatingEditorKind = PathFloatingEditorKind.ElementKeyframe;
        }
        else
        {
            return;
        }
        ShowPathFloatingEditor();
    }

    private void ShowPathFloatingEditor()
    {
        CaptureKeyframeAnchor();
        ComposeWorkflowCanvas();
        floatingEditorDialog.OpenForWorkspace();
    }

    private void ClosePathFloatingEditor()
    {
        pathFloatingEditorKind = PathFloatingEditorKind.None;
        pathFloatingTrackId = Guid.Empty;
        pathFloatingCameraIndex = -1;
        RemoveFloatingEditorComposer();
    }

    private void ComposePathFloatingEditor(string key)
    {
        if (pathFloatingEditorKind != PathFloatingEditorKind.None)
        {
            ComposeKeyframeRml(replay: false);
            return;
        }
        DisposeKeyframeRml();
        double width = ReplayPropertyPopupWidth;
        double height = PathFloatingEditorHeight();
        ClampPathFloatingEditorPosition(width, height);
        ElementBounds root = ElementBounds.Fixed(pathFloatingX, pathFloatingY, width, height);
        ElementBounds content = ElementBounds.Fixed(
            0d,
            FloatingEditorHeaderHeight,
            width,
            Math.Max(1d, height - FloatingEditorHeaderHeight));
        ElementBounds background = ElementBounds.Fill;
        background.WithChildren(content);
        CairoFont small = DirectorFont.WhiteSmallText();
        GuiComposer composer = capi.Gui.CreateCompo(key, root)
            .AddDirectorOpaqueWindowSurface(background)
            .AddDirectorDialogHeader(
                PathFloatingEditorTitle(),
                ClosePathFloatingEditor,
                width,
                "pathFloatingHeader",
                UpdateFloatingEditorPosition)
            .BeginChildElements(content);

        int y = 10;
        switch (pathFloatingEditorKind)
        {
            case PathFloatingEditorKind.AddElement:
                ComposePathElementMenu(composer);
                break;
            case PathFloatingEditorKind.Track:
                ComposePathTrackEditor(composer, small, ref y);
                break;
            case PathFloatingEditorKind.CameraKeyframe:
                ComposePathCameraKeyframeEditor(composer, small, ref y);
                break;
            case PathFloatingEditorKind.ElementKeyframe:
                ComposePathElementKeyframeEditor(composer, small, ref y);
                break;
        }

        composer.EndChildElements();
        composer.zDepth = 35f;
        GuiComposer composed = composer.Compose(focusFirstElement: false);
        floatingEditorDialog.SetComposer(composed);
        RestorePathFloatingInputs(composed);
        floatingEditorDialog.OpenForWorkspace();
    }

    private double PathFloatingEditorHeight()
    {
        int rows = pathFloatingEditorKind switch
        {
            PathFloatingEditorKind.AddElement => 3,
            PathFloatingEditorKind.Track => 1,
            PathFloatingEditorKind.CameraKeyframe => 13,
            PathFloatingEditorKind.ElementKeyframe => PathElementPropertyRows(
                SelectedPathElementKeyframe()),
            _ => 1
        };
        return pathFloatingEditorKind switch
        {
            PathFloatingEditorKind.AddElement => 54d + rows * 38d,
            PathFloatingEditorKind.Track => 118d,
            _ => 114d + rows * 34d
        };
    }

    private static int PathElementPropertyRows(DirectorPathElementKeyframe? keyframe)
    {
        if (keyframe is null)
        {
            return 1;
        }
        int common = keyframe.TrackType == DirectorPathElementTrackType.Marker ? 1 : 2;
        return common + keyframe.TrackType switch
        {
            DirectorPathElementTrackType.Fov => 1,
            DirectorPathElementTrackType.Roll => 1,
            DirectorPathElementTrackType.CameraShake => 4,
            DirectorPathElementTrackType.LookAt => 12,
            DirectorPathElementTrackType.Speed => 1,
            DirectorPathElementTrackType.Marker => 1,
            _ => 0
        };
    }

    private string PathFloatingEditorTitle()
        => pathFloatingEditorKind switch
        {
            PathFloatingEditorKind.AddElement => Lang.Get("vsdirector:replay-element-add"),
            PathFloatingEditorKind.Track => Lang.Get("vsdirector:replay-element-track-options"),
            PathFloatingEditorKind.CameraKeyframe or PathFloatingEditorKind.ElementKeyframe
                => Lang.Get("vsdirector:replay-element-properties"),
            _ => string.Empty
        };

    private void ComposePathElementMenu(GuiComposer composer)
    {
        DirectorPathElementTrackType[] types =
        {
            DirectorPathElementTrackType.Fov,
            DirectorPathElementTrackType.Roll,
            DirectorPathElementTrackType.CameraShake,
            DirectorPathElementTrackType.LookAt,
            DirectorPathElementTrackType.Speed,
            DirectorPathElementTrackType.Marker
        };
        const double gap = 8d;
        double buttonWidth = (ReplayPropertyPopupWidth - 28d) / 2d;
        for (int index = 0; index < types.Length; index++)
        {
            DirectorPathElementTrackType type = types[index];
            int column = index % 2;
            int row = index / 2;
            composer.AddDirectorButton(
                PathElementTrackName(type),
                () => AddPathElementTrack(type),
                ElementBounds.Fixed(10d + column * (buttonWidth + gap),
                    10d + row * 38d, buttonWidth, 30d),
                EnumButtonStyle.Small,
                "pathElementMenu" + index);
        }
    }

    private bool AddPathElementTrack(DirectorPathElementTrackType type)
    {
        if (pathEdit is null || type == DirectorPathElementTrackType.CameraPath)
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        DirectorPathElementTrack track = new() { TrackType = type };
        pathEdit.ElementTracks.Add(track);
        selectedPathElementTrackId = track.Id;
        selectedPathElementKeyframeId = Guid.Empty;
        ClosePathFloatingEditor();
        PersistPathTimelineEdit(before);
        return true;
    }

    private void ComposePathTrackEditor(GuiComposer composer, CairoFont small, ref int y)
    {
        DirectorPathElementTrack? track = pathEdit?.ElementTracks.FirstOrDefault(item =>
            item.Id == pathFloatingTrackId);
        if (track is null)
        {
            return;
        }
        AddReplayPropertyInput(composer, small, Lang.Get("vsdirector:name"),
            "pathFloatingTrackName", pathFloatingTrackName, ref y);
        composer
            .AddDirectorButton(Lang.Get("vsdirector:apply"), ApplyPathTrackEditor,
                ElementBounds.Fixed(10d, y, 108d, 30d), EnumButtonStyle.Normal,
                "pathTrackApply")
            .AddDirectorButton(Lang.Get("vsdirector:replay-element-track-clear"),
                ClearPathTrackKeyframes,
                ElementBounds.Fixed(126d, y, 120d, 30d), EnumButtonStyle.Small,
                "pathTrackClear");
        if (track.TrackType != DirectorPathElementTrackType.CameraPath)
        {
            composer.AddDirectorDangerButton(Lang.Get("vsdirector:delete"),
                DeletePathTrackFromEditor,
                ElementBounds.Fixed(254d, y, 112d, 30d),
                "pathTrackDelete");
        }
    }

    private void ComposePathCameraKeyframeEditor(
        GuiComposer composer,
        CairoFont small,
        ref int y)
    {
        CameraKeyframe? frame = SelectedPathFloatingCameraKeyframe();
        if (frame is null)
        {
            return;
        }
        AddReplayPropertyHeader(composer, small,
            PathElementTrackName(DirectorPathElementTrackType.CameraPath), ref y);
        AddReplayPropertyInput(composer, small, Lang.Get("vsdirector:name"),
            "pathFloatingCameraName", frame.Name, ref y);
        AddPathFloatingNumber(composer, small,
            Lang.Get("vsdirector:replay-element-time-seconds"), "cameraTime", frame.TimeSeconds, ref y);
        AddPathFloatingNumber(composer, small,
            Lang.Get("vsdirector:keyframe-duration"), "cameraDuration", frame.DurationSeconds, ref y);
        AddPathFloatingNumber(composer, small,
            Lang.Get("vsdirector:keyframe-hold"), "cameraHold", frame.HoldSeconds, ref y);
        AddPathFloatingInterpolation(composer, small,
            frame.InterpolationToNext ?? pathEdit?.Interpolation ?? CameraPathInterpolation.Linear,
            ref y);
        AddPathFloatingNumber(composer, small,
            Lang.Get("vsdirector:segment-start-speed"), "cameraStartSpeed",
            frame.SegmentStartSpeed, ref y);
        AddPathFloatingNumber(composer, small,
            Lang.Get("vsdirector:segment-end-speed"), "cameraEndSpeed",
            frame.SegmentEndSpeed, ref y);
        (double displayX, double displayY, double displayZ) = owner.DisplayPosition(
            frame.X, frame.Y, frame.Z);
        AddPathFloatingNumber(composer, small, "X", "cameraX", displayX, ref y);
        AddPathFloatingNumber(composer, small, "Y", "cameraY", displayY, ref y);
        AddPathFloatingNumber(composer, small, "Z", "cameraZ", displayZ, ref y);
        AddPathFloatingNumber(composer, small, Lang.Get("vsdirector:camera-dimension"),
            "cameraDimension", frame.Dimension, ref y);
        AddPathFloatingNumber(composer, small, "Yaw", "cameraYaw", frame.Yaw, ref y);
        AddPathFloatingNumber(composer, small, "Pitch", "cameraPitch", frame.Pitch, ref y);
        composer
            .AddDirectorIconButton(DirectorIcon.Save, Lang.Get("vsdirector:apply"),
                ApplyPathCameraKeyframe,
                ElementBounds.Fixed(10d, y, 30d, 28d), EnumButtonStyle.Normal,
                "pathCameraApply")
            .AddDirectorIconButton(DirectorIcon.Capture,
                Lang.Get("vsdirector:replay-camera-recapture"),
                RecapturePathCameraKeyframe,
                ElementBounds.Fixed(46d, y, 30d, 28d), EnumButtonStyle.Small,
                "pathCameraRecapture")
            .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:delete"),
                DeletePathCameraKeyframe,
                ElementBounds.Fixed(82d, y, 30d, 28d), EnumButtonStyle.Small,
                "pathCameraDelete");
    }

    private void AddPathFloatingInterpolation(
        GuiComposer composer,
        CairoFont small,
        CameraPathInterpolation interpolation,
        ref int y)
    {
        string[] codes = { "hold", "linear", "ease", "bezier" };
        string[] names =
        {
            Lang.Get("vsdirector:replay-speed-interpolation-hold"),
            Lang.Get("vsdirector:replay-speed-interpolation-linear"),
            Lang.Get("vsdirector:replay-speed-interpolation-ease"),
            Lang.Get("vsdirector:interpolation-bezier")
        };
        pathFloatingInterpolation = interpolation switch
        {
            CameraPathInterpolation.Step => "hold",
            CameraPathInterpolation.EaseInOut => "ease",
            CameraPathInterpolation.Bezier => "bezier",
            _ => "linear"
        };
        composer
            .AddDirectorSingleLineText(Lang.Get("vsdirector:replay-element-interpolation"), small,
                ElementBounds.Fixed(10d, y + 6d, ReplayPropertyLabelWidth, 22d))
            .AddDropDown(codes, names, Math.Max(0, Array.IndexOf(codes, pathFloatingInterpolation)),
                OnPathFloatingInterpolationChanged,
                ElementBounds.Fixed(ReplayPropertyControlX, y,
                    ReplayPropertyDropDownWidth, 28d),
                "pathFloatingInterpolation");
        y += 34;
    }

    private void ComposePathElementKeyframeEditor(
        GuiComposer composer,
        CairoFont small,
        ref int y)
    {
        DirectorPathElementKeyframe? frame = SelectedPathElementKeyframe();
        if (frame is null)
        {
            return;
        }
        AddReplayPropertyHeader(composer, small, PathElementTrackName(frame.TrackType), ref y);
        AddPathFloatingNumber(composer, small,
            Lang.Get("vsdirector:replay-element-time-seconds"),
            "elementTime", frame.TimeSeconds, ref y);
        if (frame.TrackType != DirectorPathElementTrackType.Marker)
        {
            AddPathElementInterpolation(composer, small, frame.InterpolationToNext, ref y);
        }
        switch (frame.TrackType)
        {
            case DirectorPathElementTrackType.Fov:
                AddPathFloatingNumber(composer, small, Lang.Get("vsdirector:fov"),
                    "elementFov", frame.Fov, ref y);
                break;
            case DirectorPathElementTrackType.Roll:
                AddPathFloatingNumber(composer, small, Lang.Get("vsdirector:roll"),
                    "elementRoll", frame.Roll, ref y);
                break;
            case DirectorPathElementTrackType.CameraShake:
                AddPathFloatingNumber(composer, small,
                    Lang.Get("vsdirector:replay-element-frequency-x"),
                    "elementFrequencyX", frame.FrequencyX, ref y);
                AddPathFloatingNumber(composer, small,
                    Lang.Get("vsdirector:replay-element-amplitude-x"),
                    "elementAmplitudeX", frame.AmplitudeX, ref y);
                AddPathFloatingNumber(composer, small,
                    Lang.Get("vsdirector:replay-element-frequency-y"),
                    "elementFrequencyY", frame.FrequencyY, ref y);
                AddPathFloatingNumber(composer, small,
                    Lang.Get("vsdirector:replay-element-amplitude-y"),
                    "elementAmplitudeY", frame.AmplitudeY, ref y);
                break;
            case DirectorPathElementTrackType.LookAt:
                AddReplayPropertyInput(composer, small,
                    Lang.Get("vsdirector:replay-element-entity-id"),
                    "pathFloatingTargetEntity", frame.TargetEntityId.ToString(), ref y);
                AddPathBodyPart(composer, small, frame.BodyPart, ref y);
                (double displayX, double displayY, double displayZ) = owner.DisplayPosition(
                    frame.X, frame.Y, frame.Z);
                AddPathFloatingNumber(composer, small, "X", "elementX", displayX, ref y);
                AddPathFloatingNumber(composer, small, "Y", "elementY", displayY, ref y);
                AddPathFloatingNumber(composer, small, "Z", "elementZ", displayZ, ref y);
                AddPathFloatingNumber(composer, small, Lang.Get("vsdirector:camera-dimension"),
                    "elementDimension", frame.Dimension, ref y);
                AddPathFloatingNumber(composer, small,
                    Lang.Get("vsdirector:replay-element-offset-x"),
                    "elementOffsetX", frame.OffsetX, ref y);
                AddPathFloatingNumber(composer, small,
                    Lang.Get("vsdirector:replay-element-offset-y"),
                    "elementOffsetY", frame.OffsetY, ref y);
                AddPathFloatingNumber(composer, small,
                    Lang.Get("vsdirector:replay-element-offset-z"),
                    "elementOffsetZ", frame.OffsetZ, ref y);
                AddPathFloatingNumber(composer, small,
                    Lang.Get("vsdirector:replay-element-yaw-offset"),
                    "elementYawOffset", frame.YawOffset, ref y);
                AddPathFloatingNumber(composer, small,
                    Lang.Get("vsdirector:replay-element-pitch-offset"),
                    "elementPitchOffset", frame.PitchOffset, ref y);
                break;
            case DirectorPathElementTrackType.Speed:
                AddPathFloatingNumber(composer, small,
                    Lang.Get("vsdirector:path-element-playback-rate"),
                    "elementRate", frame.PlaybackRate, ref y);
                break;
            case DirectorPathElementTrackType.Marker:
                AddReplayPropertyInput(composer, small, Lang.Get("vsdirector:name"),
                    "pathFloatingMarkerLabel", frame.Label, ref y);
                break;
        }
        composer
            .AddDirectorIconButton(DirectorIcon.Save, Lang.Get("vsdirector:apply"),
                ApplyPathElementKeyframe,
                ElementBounds.Fixed(10d, y, 30d, 28d), EnumButtonStyle.Normal,
                "pathElementApply")
            .AddDirectorIconButton(DirectorIcon.Delete, Lang.Get("vsdirector:delete"),
                DeletePathElementKeyframe,
                ElementBounds.Fixed(46d, y, 30d, 28d), EnumButtonStyle.Small,
                "pathElementDelete");
    }

    private void AddPathElementInterpolation(
        GuiComposer composer,
        CairoFont small,
        DirectorPathElementInterpolation interpolation,
        ref int y)
    {
        string[] codes = { "hold", "linear", "ease" };
        string[] names =
        {
            Lang.Get("vsdirector:replay-speed-interpolation-hold"),
            Lang.Get("vsdirector:replay-speed-interpolation-linear"),
            Lang.Get("vsdirector:replay-speed-interpolation-ease")
        };
        pathFloatingInterpolation = interpolation switch
        {
            DirectorPathElementInterpolation.Hold => "hold",
            DirectorPathElementInterpolation.EaseInOut => "ease",
            _ => "linear"
        };
        composer
            .AddDirectorSingleLineText(Lang.Get("vsdirector:replay-element-interpolation"), small,
                ElementBounds.Fixed(10d, y + 6d, ReplayPropertyLabelWidth, 22d))
            .AddDropDown(codes, names, Math.Max(0, Array.IndexOf(codes, pathFloatingInterpolation)),
                OnPathFloatingInterpolationChanged,
                ElementBounds.Fixed(ReplayPropertyControlX, y,
                    ReplayPropertyDropDownWidth, 28d),
                "pathFloatingInterpolation");
        y += 34;
    }

    private void AddPathBodyPart(
        GuiComposer composer,
        CairoFont small,
        string bodyPart,
        ref int y)
    {
        string[] codes = { "head", "body", "root" };
        string[] names =
        {
            Lang.Get("vsdirector:replay-element-body-head"),
            Lang.Get("vsdirector:replay-element-body-center"),
            Lang.Get("vsdirector:replay-element-body-root")
        };
        pathFloatingBodyPart = bodyPart is "body" or "root" ? bodyPart : "head";
        composer
            .AddDirectorSingleLineText(Lang.Get("vsdirector:replay-element-body-part"), small,
                ElementBounds.Fixed(10d, y + 6d, ReplayPropertyLabelWidth, 22d))
            .AddDropDown(codes, names, Math.Max(0, Array.IndexOf(codes, pathFloatingBodyPart)),
                OnPathFloatingBodyPartChanged,
                ElementBounds.Fixed(ReplayPropertyControlX, y,
                    ReplayPropertyDropDownWidth, 28d),
                "pathFloatingBodyPart");
        y += 34;
    }

    private void AddPathFloatingNumber(
        GuiComposer composer,
        CairoFont small,
        string label,
        string key,
        double value,
        ref int y)
        => AddReplayPropertyNumber(composer, small, label, "pathFloating" + UpperFirst(key),
            value, ref y);

    private void RestorePathFloatingInputs(GuiComposer composer)
    {
        SetInput(composer, "pathFloatingTrackName", pathFloatingTrackName);
        if (SelectedPathFloatingCameraKeyframe() is CameraKeyframe camera)
        {
            SetInput(composer, "pathFloatingCameraName", camera.Name);
            SetNumber(composer, "pathFloatingCameraTime", camera.TimeSeconds);
            SetNumber(composer, "pathFloatingCameraDuration", camera.DurationSeconds);
            SetNumber(composer, "pathFloatingCameraHold", camera.HoldSeconds);
            SetNumber(composer, "pathFloatingCameraStartSpeed", camera.SegmentStartSpeed);
            SetNumber(composer, "pathFloatingCameraEndSpeed", camera.SegmentEndSpeed);
            (double displayX, double displayY, double displayZ) = owner.DisplayPosition(
                camera.X, camera.Y, camera.Z);
            SetNumber(composer, "pathFloatingCameraX", displayX);
            SetNumber(composer, "pathFloatingCameraY", displayY);
            SetNumber(composer, "pathFloatingCameraZ", displayZ);
            SetNumber(composer, "pathFloatingCameraDimension", camera.Dimension);
            SetNumber(composer, "pathFloatingCameraYaw", camera.Yaw);
            SetNumber(composer, "pathFloatingCameraPitch", camera.Pitch);
        }
        if (SelectedPathElementKeyframe() is not DirectorPathElementKeyframe frame)
        {
            return;
        }
        SetNumber(composer, "pathFloatingElementTime", frame.TimeSeconds);
        SetNumber(composer, "pathFloatingElementFov", frame.Fov);
        SetNumber(composer, "pathFloatingElementRoll", frame.Roll);
        SetNumber(composer, "pathFloatingElementFrequencyX", frame.FrequencyX);
        SetNumber(composer, "pathFloatingElementAmplitudeX", frame.AmplitudeX);
        SetNumber(composer, "pathFloatingElementFrequencyY", frame.FrequencyY);
        SetNumber(composer, "pathFloatingElementAmplitudeY", frame.AmplitudeY);
        SetInput(composer, "pathFloatingTargetEntity", frame.TargetEntityId.ToString());
        (double elementDisplayX, double elementDisplayY, double elementDisplayZ) = owner.DisplayPosition(
            frame.X, frame.Y, frame.Z);
        SetNumber(composer, "pathFloatingElementX", elementDisplayX);
        SetNumber(composer, "pathFloatingElementY", elementDisplayY);
        SetNumber(composer, "pathFloatingElementZ", elementDisplayZ);
        SetNumber(composer, "pathFloatingElementDimension", frame.Dimension);
        SetNumber(composer, "pathFloatingElementOffsetX", frame.OffsetX);
        SetNumber(composer, "pathFloatingElementOffsetY", frame.OffsetY);
        SetNumber(composer, "pathFloatingElementOffsetZ", frame.OffsetZ);
        SetNumber(composer, "pathFloatingElementYawOffset", frame.YawOffset);
        SetNumber(composer, "pathFloatingElementPitchOffset", frame.PitchOffset);
        SetNumber(composer, "pathFloatingElementRate", frame.PlaybackRate);
        SetInput(composer, "pathFloatingMarkerLabel", frame.Label);
    }

    private void ClampPathFloatingEditorPosition(double width, double height)
    {
        double screenWidth = capi.Render.FrameWidth / Math.Max(0.001d, GuiElement.scaled(1d));
        double screenHeight = capi.Render.FrameHeight / Math.Max(0.001d, GuiElement.scaled(1d));
        pathFloatingX = Math.Clamp(pathFloatingX, 0d, Math.Max(0d, screenWidth - width));
        pathFloatingY = Math.Clamp(pathFloatingY,
            DirectorWorkspacePanelLayout.ToolbarHeight,
            Math.Max(DirectorWorkspacePanelLayout.ToolbarHeight, screenHeight - height));
    }

    private void OnPathFloatingInterpolationChanged(string value, bool selected)
    {
        if (selected && value is "hold" or "linear" or "ease" or "bezier")
        {
            pathFloatingInterpolation = value;
        }
    }

    private void OnPathFloatingBodyPartChanged(string value, bool selected)
    {
        if (selected && value is "head" or "body" or "root")
        {
            pathFloatingBodyPart = value;
        }
    }

    private CameraKeyframe? SelectedPathFloatingCameraKeyframe()
        => pathEdit is not null
            && pathFloatingCameraIndex >= 0
            && pathFloatingCameraIndex < pathEdit.Keyframes.Count
                ? pathEdit.Keyframes[pathFloatingCameraIndex]
                : null;

    private DirectorPathElementKeyframe? SelectedPathElementKeyframe()
        => pathEdit?.ElementKeyframes.FirstOrDefault(frame =>
            frame.Id == selectedPathElementKeyframeId);

    private bool ApplyPathTrackEditor()
    {
        if (pathEdit?.ElementTracks.FirstOrDefault(track => track.Id == pathFloatingTrackId)
            is not DirectorPathElementTrack track)
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        track.Name = floatingEditorDialog.Composer?.GetTextInput("pathFloatingTrackName")?
            .GetText().Trim() ?? pathFloatingTrackName;
        pathFloatingTrackName = track.Name;
        PersistPathTimelineEdit(before);
        return true;
    }

    private bool ClearPathTrackKeyframes()
    {
        if (pathEdit?.ElementTracks.FirstOrDefault(track => track.Id == pathFloatingTrackId)
            is not DirectorPathElementTrack track)
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        if (track.TrackType == DirectorPathElementTrackType.CameraPath)
        {
            pathEdit.Keyframes.Clear();
            selectedPathKeyframes.Clear();
            selectedPathKeyframeIndex = -1;
        }
        else
        {
            pathEdit.ElementKeyframes.RemoveAll(frame => frame.TrackId == track.Id);
            selectedPathElementKeyframeId = Guid.Empty;
        }
        PersistPathTimelineEdit(before);
        return true;
    }

    private bool DeletePathTrackFromEditor()
    {
        if (pathEdit?.ElementTracks.FirstOrDefault(track => track.Id == pathFloatingTrackId)
            is not DirectorPathElementTrack track
            || track.TrackType == DirectorPathElementTrackType.CameraPath)
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        pathEdit.ElementTracks.Remove(track);
        pathEdit.ElementKeyframes.RemoveAll(frame => frame.TrackId == track.Id);
        selectedPathElementTrackId = Guid.Empty;
        selectedPathElementKeyframeId = Guid.Empty;
        ClosePathFloatingEditor();
        PersistPathTimelineEdit(before);
        return true;
    }

    private bool ApplyPathCameraKeyframe()
    {
        CameraKeyframe? frame = SelectedPathFloatingCameraKeyframe();
        GuiComposer? composer = floatingEditorDialog.Composer;
        if (pathEdit is null || frame is null || (composer is null && keyframeRmlWindow is null)
            || !TryPathFloatingNumber(composer, "cameraTime", out double time)
            || !TryPathFloatingNumber(composer, "cameraDuration", out double duration)
            || !TryPathFloatingNumber(composer, "cameraHold", out double hold)
            || !TryPathFloatingNumber(composer, "cameraStartSpeed", out double startSpeed)
            || !TryPathFloatingNumber(composer, "cameraEndSpeed", out double endSpeed)
            || !TryPathFloatingNumber(composer, "cameraX", out double x)
            || !TryPathFloatingNumber(composer, "cameraY", out double y)
            || !TryPathFloatingNumber(composer, "cameraZ", out double z)
            || !TryPathFloatingNumber(composer, "cameraDimension", out double dimension)
            || !TryPathFloatingNumber(composer, "cameraYaw", out double yaw)
            || !TryPathFloatingNumber(composer, "cameraPitch", out double pitch)
            || time < 0d || time > CameraPathTiming.MaxTimelineSeconds
            || duration < 0d || hold < 0d
            || startSpeed < CameraPathTiming.MinSegmentSpeed
            || startSpeed > CameraPathTiming.MaxSegmentSpeed
            || endSpeed < CameraPathTiming.MinSegmentSpeed
            || endSpeed > CameraPathTiming.MaxSegmentSpeed)
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:auto-save-invalid-input"));
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        int oldIndex = pathFloatingCameraIndex;
        frame.Name = PathFloatingText(composer, "pathFloatingCameraName", frame.Name).Trim();
        frame.DurationSeconds = duration;
        frame.HoldSeconds = Math.Min(hold, duration);
        frame.SegmentStartSpeed = startSpeed;
        frame.SegmentEndSpeed = endSpeed;
        frame.InterpolationToNext = pathFloatingInterpolation switch
        {
            "hold" => CameraPathInterpolation.Step,
            "ease" => CameraPathInterpolation.EaseInOut,
            "bezier" => CameraPathInterpolation.Bezier,
            _ => CameraPathInterpolation.Linear
        };
        (frame.X, frame.Y, frame.Z) = owner.AbsolutePosition(x, y, z);
        frame.Dimension = (int)Math.Round(dimension);
        frame.Yaw = CameraPathOrientation.PreserveDegrees(yaw, frame.Yaw);
        frame.Pitch = CameraPathOrientation.PreserveDegrees(pitch, frame.Pitch);
        CameraPathTiming.TrySetKeyframeTime(pathEdit, oldIndex,
            DirectorPathElementTimeline.TickAt(time) / PathTimelineTicksPerSecond,
            out double applied);
        pathFloatingCameraIndex = pathEdit.Keyframes.FindIndex(item =>
            Math.Abs(item.TimeSeconds - applied) <= CameraPathTiming.TimeMatchEpsilon);
        SelectOnlyPathKeyframe(pathFloatingCameraIndex);
        pathEditorPlayhead = applied;
        if (PersistPathTimelineEdit(before)) ClosePathFloatingEditor();
        return true;
    }

    private bool RecapturePathCameraKeyframe()
    {
        CameraKeyframe? frame = SelectedPathFloatingCameraKeyframe();
        if (pathEdit is null || frame is null
            || !owner.TryCapturePathKeyframePose(pathEdit, frame.TimeSeconds,
                out CameraKeyframe captured, out _, preferBasePathPreview: true))
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        frame.X = captured.X;
        frame.Y = captured.Y;
        frame.Z = captured.Z;
        frame.Dimension = captured.Dimension;
        frame.Yaw = captured.Yaw;
        frame.Pitch = captured.Pitch;
        PersistPathTimelineEdit(before);
        ComposeWorkflowCanvas();
        return true;
    }

    private bool DeletePathCameraKeyframe()
    {
        if (pathEdit is null || pathFloatingCameraIndex < 0
            || pathFloatingCameraIndex >= pathEdit.Keyframes.Count)
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        pathEdit.Keyframes.RemoveAt(pathFloatingCameraIndex);
        pathFloatingCameraIndex = -1;
        selectedPathKeyframes.Clear();
        selectedPathKeyframeIndex = -1;
        ClosePathFloatingEditor();
        PersistPathTimelineEdit(before);
        return true;
    }

    private bool ApplyPathElementKeyframe()
    {
        DirectorPathElementKeyframe? frame = SelectedPathElementKeyframe();
        GuiComposer? composer = floatingEditorDialog.Composer;
        if (pathEdit is null || frame is null || (composer is null && keyframeRmlWindow is null)
            || !TryPathFloatingNumber(composer, "elementTime", out double time)
            || time < 0d || time > Math.Max(pathEdit.DurationSeconds, 0d))
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-element-invalid"));
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        // Validate the whole draft before replacing the stored keyframe.
        frame = frame.DeepClone();
        frame.TimeSeconds = DirectorPathElementTimeline.TickAt(time) / PathTimelineTicksPerSecond;
        frame.InterpolationToNext = pathFloatingInterpolation switch
        {
            "hold" => DirectorPathElementInterpolation.Hold,
            "ease" => DirectorPathElementInterpolation.EaseInOut,
            _ => DirectorPathElementInterpolation.Linear
        };
        bool valid = ApplyPathElementValues(composer, frame);
        if (!valid)
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-element-invalid"));
            return true;
        }
        int tick = DirectorPathElementTimeline.TickAt(frame.TimeSeconds);
        pathEdit.ElementKeyframes[pathEdit.ElementKeyframes.FindIndex(item => item.Id == frame.Id)] = frame;
        pathEdit.ElementKeyframes.RemoveAll(item => item.Id != frame.Id
            && item.TrackId == frame.TrackId
            && DirectorPathElementTimeline.TickAt(item.TimeSeconds) == tick);
        pathEditorPlayhead = frame.TimeSeconds;
        if (PersistPathTimelineEdit(before)) ClosePathFloatingEditor();
        return true;
    }

    private bool ApplyPathElementValues(GuiComposer? composer, DirectorPathElementKeyframe frame)
    {
        switch (frame.TrackType)
        {
            case DirectorPathElementTrackType.Fov:
                if (!TryPathFloatingNumber(composer, "elementFov", out double fov)
                    || fov < DirectorPathElementTimeline.MinFovDegrees
                    || fov > DirectorPathElementTimeline.MaxFovDegrees) return false;
                frame.Fov = (float)fov;
                return true;
            case DirectorPathElementTrackType.Roll:
                if (!TryPathFloatingNumber(composer, "elementRoll", out double roll)) return false;
                frame.Roll = CameraPathOrientation.PreserveDegrees(roll, frame.Roll);
                return true;
            case DirectorPathElementTrackType.CameraShake:
                if (!TryPathFloatingNumber(composer, "elementFrequencyX", out double frequencyX)
                    || !TryPathFloatingNumber(composer, "elementAmplitudeX", out double amplitudeX)
                    || !TryPathFloatingNumber(composer, "elementFrequencyY", out double frequencyY)
                    || !TryPathFloatingNumber(composer, "elementAmplitudeY", out double amplitudeY)
                    || frequencyX < 0d || frequencyX > 120d
                    || frequencyY < 0d || frequencyY > 120d
                    || Math.Abs(amplitudeX) > 180d || Math.Abs(amplitudeY) > 180d) return false;
                frame.FrequencyX = (float)frequencyX;
                frame.AmplitudeX = (float)amplitudeX;
                frame.FrequencyY = (float)frequencyY;
                frame.AmplitudeY = (float)amplitudeY;
                return true;
            case DirectorPathElementTrackType.LookAt:
                string entityText = PathFloatingText(composer, "pathFloatingTargetEntity", "0");
                if (!long.TryParse(entityText, out long entityId)
                    || !TryPathFloatingNumber(composer, "elementX", out double x)
                    || !TryPathFloatingNumber(composer, "elementY", out double y)
                    || !TryPathFloatingNumber(composer, "elementZ", out double z)
                    || !TryPathFloatingNumber(composer, "elementDimension", out double dimension)
                    || !TryPathFloatingNumber(composer, "elementOffsetX", out double offsetX)
                    || !TryPathFloatingNumber(composer, "elementOffsetY", out double offsetY)
                    || !TryPathFloatingNumber(composer, "elementOffsetZ", out double offsetZ)
                    || !TryPathFloatingNumber(composer, "elementYawOffset", out double yawOffset)
                    || !TryPathFloatingNumber(composer, "elementPitchOffset", out double pitchOffset))
                    return false;
                frame.TargetEntityId = Math.Max(0L, entityId);
                frame.BodyPart = pathFloatingBodyPart;
                (frame.X, frame.Y, frame.Z) = owner.AbsolutePosition(x, y, z);
                frame.Dimension = (int)Math.Round(dimension);
                frame.OffsetX = offsetX;
                frame.OffsetY = offsetY;
                frame.OffsetZ = offsetZ;
                frame.YawOffset = (float)yawOffset;
                frame.PitchOffset = (float)pitchOffset;
                return true;
            case DirectorPathElementTrackType.Speed:
                if (!TryPathFloatingNumber(composer, "elementRate", out double rate)
                    || rate < DirectorPathElementTimeline.MinPlaybackRate
                    || rate > DirectorPathElementTimeline.MaxPlaybackRate) return false;
                frame.PlaybackRate = rate;
                return true;
            case DirectorPathElementTrackType.Marker:
                frame.Label = PathFloatingText(composer, "pathFloatingMarkerLabel", "").Trim();
                frame.InterpolationToNext = DirectorPathElementInterpolation.Hold;
                return true;
            default:
                return false;
        }
    }

    private bool DeletePathElementKeyframe()
    {
        if (pathEdit is null || selectedPathElementKeyframeId == Guid.Empty)
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        pathEdit.ElementKeyframes.RemoveAll(frame => frame.Id == selectedPathElementKeyframeId);
        selectedPathElementKeyframeId = Guid.Empty;
        ClosePathFloatingEditor();
        PersistPathTimelineEdit(before);
        return true;
    }

    private bool TryPathFloatingNumber(
        GuiComposer? composer,
        string field,
        out double value)
    {
        value = 0d;
        string key = "pathFloating" + UpperFirst(field);
        string? text = keyframeRmlWindow is not null ? keyframeRmlValues.GetValueOrDefault(key)
            : composer?.GetNumberInput(key)?.GetText();
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }
}
