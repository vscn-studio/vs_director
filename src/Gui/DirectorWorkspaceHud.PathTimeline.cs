using System.Globalization;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private const double PathTimelineHeaderHeight = 36d;
    private const double PathTimelineTrackHeight = ReplayTimelineTrackHeight;
    private const double PathTimelineTicksPerSecond = DirectorPathElementTimeline.TicksPerSecond;
    private const double PathTimelineBasePixelsPerTick = ReplayTimelineBasePixelsPerTick;
    private static readonly int[] PathTimelineTickScaleLevels = { 30, 15, 10, 5, 0 };

    private double pathTimelineScrollTicks;
    private int pathTimelineTickScaleIndex;
    private int pathTimelineFirstVisibleTrack;
    private double pathTimelinePanStartMouseX;
    private double pathTimelinePanStartTicks;
    private bool pathTimelinePanning;
    private bool pathTimelineScrubbing;
    private Guid selectedPathElementTrackId;
    private Guid selectedPathElementKeyframeId;
    private PathTimelineDragKind pathTimelineDragKind;
    private int pathTimelineCameraDragIndex = -1;
    private Guid pathTimelineElementDragId;
    private double pathTimelineDragTime = double.NaN;
    private bool pathTimelineDragMoved;
    private CameraPath? pathTimelineDragBefore;
    private bool pathElementTrackReordering;
    private Guid pathElementTrackReorderId;
    private int pathElementTrackReorderIndex = -1;
    private CameraPath? pathElementTrackReorderBefore;
    private PathFloatingEditorKind pathFloatingEditorKind;
    private Guid pathFloatingTrackId;
    private double pathFloatingX = 180d;
    private double pathFloatingY = 120d;
    private string pathFloatingTrackName = string.Empty;
    private string pathFloatingInterpolation = "linear";
    private string pathFloatingBodyPart = "head";

    private int PathTimelineTickScale
        => PathTimelineTickScaleLevels[Math.Clamp(
            pathTimelineTickScaleIndex,
            0,
            PathTimelineTickScaleLevels.Length - 1)];

    private int PathTimelineMinorTickStep
        => PathTimelineTickScale switch
        {
            30 => 1,
            15 => 2,
            10 => 3,
            5 => 6,
            _ => 30
        };

    private int PathTimelineLabelTickStep
        => PathTimelineTickScale switch
        {
            30 => 5,
            15 => 10,
            10 => 15,
            5 => 30,
            _ => 150
        };

    private double PathTimelinePixelsPerTick
        => PathTimelineBasePixelsPerTick / PathTimelineMinorTickStep;

    private void ComposePathElementTimeline(
        GuiComposer composer,
        DirectorWorkspacePanelLayout panel,
        CairoFont detail)
    {
        if (pathEdit is null)
        {
            return;
        }
        DirectorPathElementTimeline.Normalize(pathEdit);
        double timelineY = 6d;
        // Leave the same visible bottom breathing room as the replay timeline
        // block instead of letting the final track touch the panel edge.
        double timelineHeight = Math.Max(96d, panel.Height - 20d);
        pathTimelineBounds = ElementBounds.Fixed(0d, timelineY, panel.Width, timelineHeight);
        composer.AddCustomRender(pathTimelineBounds, RenderPathElementTimeline);

        double labelColumnWidth = ReplayTimelineLabelColumnWidth(panel.Width);
        double controlY = timelineY + 3d;
        const double buttonWidth = 26d;
        const double buttonStep = 30d;
        composer
            .AddDirectorTransportButton(DirectorIcon.SkipBack,
                Lang.Get("vsdirector:previous-keyframe"),
                SelectPreviousPathKeyframe,
                ElementBounds.Fixed(14d, controlY, buttonWidth, 26d),
                "pathTimelinePrevious")
            .AddDirectorTransportButton(DirectorIcon.Reverse,
                Lang.Get("vsdirector:reverse"),
                () => PlayPathTimeline(true),
                ElementBounds.Fixed(14d + buttonStep, controlY, buttonWidth, 26d),
                "pathTimelineReverse")
            .AddDirectorTransportButton(DirectorIcon.Play,
                Lang.Get("vsdirector:play-path"),
                () => PlayPathTimeline(false),
                ElementBounds.Fixed(14d + buttonStep * 2d, controlY, buttonWidth, 26d),
                "pathTimelinePlay")
            .AddDirectorTransportButton(DirectorIcon.SkipForward,
                Lang.Get("vsdirector:next-keyframe"),
                SelectNextPathKeyframe,
                ElementBounds.Fixed(14d + buttonStep * 3d, controlY, buttonWidth, 26d),
                "pathTimelineNext")
            .AddDirectorIconButton(DirectorIcon.Stop,
                Lang.Get("vsdirector:path-stop-playback"),
                StopPathTimelinePlayback,
                ElementBounds.Fixed(14d + buttonStep * 4d, controlY, buttonWidth, 26d),
                EnumButtonStyle.Small,
                "pathTimelineStop");

        double contentWidth = Math.Max(1d, panel.Width - labelColumnWidth);
        double visibleTicks = contentWidth / PathTimelinePixelsPerTick;
        int durationTicks = PathTimelineDurationTicks();
        pathTimelineScrollTicks = Math.Clamp(
            pathTimelineScrollTicks,
            0d,
            Math.Max(0d, durationTicks - visibleTicks));
        int firstVisibleTick = Math.Max(0, (int)Math.Floor(pathTimelineScrollTicks));
        int lastVisibleTick = Math.Min(
            durationTicks,
            (int)Math.Ceiling(pathTimelineScrollTicks + visibleTicks));
        int firstLabelTick = ((firstVisibleTick + PathTimelineLabelTickStep - 1)
            / PathTimelineLabelTickStep) * PathTimelineLabelTickStep;
        int? dragTick = double.IsFinite(pathTimelineDragTime)
            ? DirectorPathElementTimeline.TickAt(pathTimelineDragTime)
            : null;
        CairoFont rulerFont = DirectorFont.WhiteDetailText().WithLineHeightMultiplier(1d);
        const double labelWidth = 78d;
        int labelIndex = 0;
        for (int tick = firstLabelTick; tick <= lastVisibleTick;
             tick += PathTimelineLabelTickStep)
        {
            double x = labelColumnWidth
                + (tick - pathTimelineScrollTicks) * PathTimelinePixelsPerTick;
            double textX = x + 3d;
            if (textX + labelWidth > panel.Width
                || dragTick is int active
                    && Math.Abs(active - tick) * PathTimelinePixelsPerTick < labelWidth)
            {
                continue;
            }
            composer.AddDirectorSingleLineText(
                FormatReplayTimelineTick(tick),
                rulerFont,
                ElementBounds.Fixed(textX, timelineY + 1d, labelWidth, 17d),
                "pathRulerLabel" + labelIndex.ToString(CultureInfo.InvariantCulture));
            labelIndex++;
        }
        if (dragTick is int preciseTick)
        {
            double x = labelColumnWidth
                + (preciseTick - pathTimelineScrollTicks) * PathTimelinePixelsPerTick;
            double textX = Math.Clamp(
                x + 3d,
                labelColumnWidth + 2d,
                Math.Max(labelColumnWidth + 2d, panel.Width - labelWidth - 2d));
            composer.AddDirectorSingleLineText(
                FormatReplayTimelineTick(preciseTick),
                rulerFont,
                ElementBounds.Fixed(textX, timelineY + 1d, labelWidth, 17d),
                "pathDraggedKeyframeTime");
        }

        IReadOnlyList<DirectorPathElementTrack> tracks = pathEdit.ElementTracks;
        int visibleCount = Math.Max(1, (int)Math.Floor(
            (timelineHeight - PathTimelineHeaderHeight) / PathTimelineTrackHeight));
        pathTimelineFirstVisibleTrack = Math.Clamp(
            pathTimelineFirstVisibleTrack,
            0,
            Math.Max(0, tracks.Count + 1 - visibleCount));
        for (int visibleIndex = 0; visibleIndex < visibleCount; visibleIndex++)
        {
            int trackIndex = pathTimelineFirstVisibleTrack + visibleIndex;
            if (trackIndex > tracks.Count)
            {
                break;
            }
            double rowY = timelineY + PathTimelineHeaderHeight
                + visibleIndex * PathTimelineTrackHeight;
            if (rowY + PathTimelineTrackHeight > timelineY + timelineHeight)
            {
                break;
            }
            if (trackIndex == tracks.Count)
            {
                composer.AddDirectorButton(
                    Lang.Get("vsdirector:replay-element-add"),
                    OpenPathElementMenu,
                    ElementBounds.Fixed(8d, rowY + 2d,
                        Math.Clamp(labelColumnWidth * 0.38d, 72d, 104d), 26d),
                    EnumButtonStyle.Small,
                    "pathAddElement");
                continue;
            }

            DirectorPathElementTrack track = tracks[trackIndex];
            string key = track.Id.ToString("N");
            double addX = Math.Max(
                190d,
                labelColumnWidth - ReplayTimelineLabelColumnExpansion - 30d);
            double visibilityX = addX - 28d;
            double menuX = visibilityX - 28d;
            double nameX = 60d;
            double nameWidth = Math.Max(26d, menuX - nameX - 4d);
            composer
                .AddDirectorChromelessIconButton(DirectorIcon.Drag,
                    Lang.Get("vsdirector:replay-element-track-reorder"),
                    () => SelectPathElementTrack(track.Id),
                    ElementBounds.Fixed(8d, rowY + 2d, 28d, 26d),
                    "pathTrackDrag" + key)
                .AddDirectorChromelessIconButton(DirectorIcon.Bars,
                    Lang.Get("vsdirector:replay-element-track-options"),
                    () => OpenPathTrackEditor(track.Id),
                    ElementBounds.Fixed(menuX, rowY + 2d, 28d, 26d),
                    "pathTrackMenu" + key)
                .AddDirectorChromelessIconButton(
                    PathTrackVisible(track) ? DirectorIcon.Eye : DirectorIcon.EyeOff,
                    Lang.Get(PathTrackVisible(track)
                        ? "vsdirector:replay-viewport-hide"
                        : "vsdirector:replay-viewport-show"),
                    () => TogglePathElementTrack(track.Id),
                    ElementBounds.Fixed(visibilityX, rowY + 2d, 28d, 26d),
                    "pathTrackVisible" + key)
                .AddDirectorChromelessIconButton(DirectorIcon.CirclePlus,
                    Lang.Get("vsdirector:replay-element-keyframe-add"),
                    () => AddPathKeyframeForTrack(track.Id),
                    ElementBounds.Fixed(addX, rowY + 2d, 28d, 26d),
                    "pathTrackAdd" + key)
                .AddDirectorSingleLineText(
                    Ellipsize(track.Name.Length == 0
                            ? PathElementTrackName(track.TrackType)
                            : track.Name,
                        Math.Max(6, (int)(nameWidth / 7d))),
                    detail,
                    ElementBounds.Fixed(nameX, rowY + 4d, nameWidth, 22d));
        }
    }

    private void RenderPathElementTimeline(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        if (pathEdit is null)
        {
            return;
        }
        double time = owner.IsPathPlaying ? owner.ActivePathTimeSeconds : pathEditorPlayhead;
        double labelWidth = ReplayTimelineLabelColumnWidth(bounds);
        double headerHeight = GuiElement.scaled(PathTimelineHeaderHeight);
        double rulerHeight = GuiElement.scaled(ReplayTimelineRulerHeight);
        double rowHeight = GuiElement.scaled(PathTimelineTrackHeight);
        double contentLeft = labelWidth;
        double contentWidth = Math.Max(GuiElement.scaled(1d), bounds.OuterWidth - contentLeft);
        double lineWidth = GuiElement.scaled(1d);
        double rulerBaseline = rulerHeight - lineWidth;
        int white = ColorUtil.ToRgba(255, 255, 255, 255);
        const float backgroundZ = 20f;
        double pixelsPerTick = GuiElement.scaled(PathTimelinePixelsPerTick);
        int durationTicks = PathTimelineDurationTicks();
        double visibleTicks = contentWidth
            / DirectorTimelineCoordinateMath.PositivePixelsPerTick(pixelsPerTick);
        pathTimelineScrollTicks = Math.Clamp(
            pathTimelineScrollTicks,
            0d,
            Math.Max(0d, durationTicks - visibleTicks));
        double TimelineX(double seconds)
            => contentLeft + (seconds * PathTimelineTicksPerSecond
                - pathTimelineScrollTicks) * pixelsPerTick;

        RenderSolidWorkflowRect(bounds, 0d, 0d, bounds.OuterWidth, bounds.OuterHeight,
            backgroundZ, ColorUtil.ToRgba(232, 13, 18, 22));
        RenderSolidWorkflowRect(bounds, 0d, 0d, labelWidth, headerHeight,
            backgroundZ, ColorUtil.ToRgba(238, 24, 31, 38));
        RenderSolidWorkflowRect(bounds, contentLeft, 0d, contentWidth, headerHeight,
            backgroundZ, ColorUtil.ToRgba(226, 16, 23, 30));
        RenderSolidWorkflowRect(bounds, contentLeft - lineWidth * 0.5d, 0d,
            lineWidth, bounds.OuterHeight, 100.25f, white);
        RenderSolidWorkflowRect(bounds, 0d, rulerBaseline, bounds.OuterWidth,
            lineWidth, 100.3f, white);

        int firstTick = Math.Max(0,
            (int)Math.Floor(pathTimelineScrollTicks / PathTimelineMinorTickStep)
                * PathTimelineMinorTickStep);
        int lastTick = Math.Min(durationTicks,
            (int)Math.Ceiling(pathTimelineScrollTicks + visibleTicks));
        for (int tick = firstTick; tick <= lastTick; tick += PathTimelineMinorTickStep)
        {
            double x = contentLeft + (tick - pathTimelineScrollTicks) * pixelsPerTick;
            bool major = tick % PathTimelineLabelTickStep == 0;
            double height = GuiElement.scaled(major ? 13d : 6d);
            RenderSolidWorkflowRect(bounds, x - lineWidth * 0.5d,
                rulerBaseline - height, lineWidth, height,
                major ? 100.4f : 100.35f, white);
        }

        IReadOnlyList<DirectorPathElementTrack> tracks = pathEdit.ElementTracks;
        int visibleCount = Math.Max(1, (int)Math.Floor(
            (bounds.OuterHeight - headerHeight) / Math.Max(1d, rowHeight)));
        pathTimelineFirstVisibleTrack = Math.Clamp(
            pathTimelineFirstVisibleTrack,
            0,
            Math.Max(0, tracks.Count + 1 - visibleCount));
        for (int visibleIndex = 0; visibleIndex < visibleCount; visibleIndex++)
        {
            int trackIndex = pathTimelineFirstVisibleTrack + visibleIndex;
            if (trackIndex > tracks.Count)
            {
                break;
            }
            double rowY = headerHeight + visibleIndex * rowHeight;
            if (rowY + rowHeight > bounds.OuterHeight)
            {
                break;
            }
            bool addRow = trackIndex == tracks.Count;
            DirectorPathElementTrack? track = addRow ? null : tracks[trackIndex];
            bool selected = track?.Id == selectedPathElementTrackId;
            int rowColor = addRow
                ? ColorUtil.ToRgba(238, 16, 23, 30)
                : selected
                    ? ColorUtil.ToRgba(64, 60, 53, 58)
                    : trackIndex % 2 == 0
                        ? ColorUtil.ToRgba(46, 22, 29, 35)
                        : ColorUtil.ToRgba(34, 20, 26, 32);
            RenderSolidWorkflowRect(bounds, 0d, rowY, labelWidth,
                rowHeight - lineWidth, backgroundZ, rowColor);
            if (!addRow)
            {
                RenderSolidWorkflowRect(bounds, contentLeft, rowY, contentWidth,
                    rowHeight - lineWidth, backgroundZ, rowColor);
            }
            RenderSolidWorkflowRect(bounds, 0d, rowY + rowHeight - lineWidth,
                addRow ? labelWidth : bounds.OuterWidth,
                lineWidth, 100.34f, white);
            if (track is null)
            {
                continue;
            }

            double centerY = rowY + rowHeight * 0.5d;
            timelineMarkerRenderer.RenderTrackIcon(
                track.TrackType,
                bounds.renderX + GuiElement.scaled(46d),
                bounds.renderY + centerY,
                rowHeight * 0.8d,
                100.52f,
                white);
            void RenderKeyframe(double keyframeTime, bool isSelected)
            {
                double x = TimelineX(keyframeTime);
                if (x < contentLeft || x > contentLeft + contentWidth)
                {
                    return;
                }
                timelineMarkerRenderer.Render(
                    bounds.renderX + x,
                    bounds.renderY + centerY,
                    rowHeight * 0.9d,
                    100.55f,
                    isSelected
                        ? ColorUtil.ToRgba(255, 255, 213, 76)
                        : white);
            }
            if (track.TrackType == DirectorPathElementTrackType.CameraPath)
            {
                for (int index = 0; index < pathEdit.Keyframes.Count; index++)
                {
                    double keyTime = pathTimelineDragKind == PathTimelineDragKind.Camera
                            && index == pathTimelineCameraDragIndex
                        ? pathTimelineDragTime
                        : pathEdit.Keyframes[index].TimeSeconds;
                    RenderKeyframe(keyTime, selectedPathKeyframes.Contains(index));
                }
            }
            else
            {
                foreach (DirectorPathElementKeyframe frame in pathEdit.ElementKeyframes.Where(
                    frame => frame.TrackId == track.Id))
                {
                    double keyTime = pathTimelineDragKind == PathTimelineDragKind.Element
                            && frame.Id == pathTimelineElementDragId
                        ? pathTimelineDragTime
                        : frame.TimeSeconds;
                    RenderKeyframe(keyTime, frame.Id == selectedPathElementKeyframeId);
                }
            }
        }

        if (pathElementTrackReordering && pathElementTrackReorderIndex >= 0)
        {
            int visibleIndex = pathElementTrackReorderIndex - pathTimelineFirstVisibleTrack;
            if (visibleIndex >= 0 && visibleIndex <= visibleCount)
            {
                double y = headerHeight + visibleIndex * rowHeight;
                RenderSolidWorkflowRect(bounds, 0d, y - lineWidth,
                    bounds.OuterWidth, lineWidth * 2d, 100.92f,
                    ColorUtil.ToRgba(255, 255, 213, 76));
            }
        }

        double playheadX = TimelineX(time);
        if (playheadX >= contentLeft && playheadX <= contentLeft + contentWidth)
        {
            RenderSolidWorkflowRect(bounds, playheadX - lineWidth * 0.5d,
                rulerBaseline, lineWidth,
                Math.Max(GuiElement.scaled(12d), bounds.OuterHeight - rulerBaseline),
                100.8f, white);
            timelineMarkerRenderer.RenderPlayhead(
                bounds.renderX + playheadX,
                bounds.renderY + rulerBaseline - GuiElement.scaled(5d),
                GuiElement.scaled(22d),
                100.86f,
                white);
        }
    }

    private int PathTimelineDurationTicks()
        => Math.Max(1, (int)Math.Ceiling(
            Math.Max(0d, pathEdit?.DurationSeconds ?? 0d) * PathTimelineTicksPerSecond));

    private double PathTimelineTimeAt(double mouseX)
    {
        if (pathTimelineBounds is null)
        {
            return pathEditorPlayhead;
        }
        double left = ReplayTimelineContentLeft(pathTimelineBounds);
        double tick = pathTimelineScrollTicks
            + (mouseX - left)
                / DirectorTimelineCoordinateMath.PositivePixelsPerTick(
                    GuiElement.scaled(PathTimelinePixelsPerTick));
        int snapped = Math.Clamp(
            (int)Math.Round(tick, MidpointRounding.AwayFromZero),
            0,
            PathTimelineDurationTicks());
        return snapped / PathTimelineTicksPerSecond;
    }

    private double PathTimelineXForSeconds(double seconds)
    {
        if (pathTimelineBounds is null)
        {
            return 0d;
        }
        return ReplayTimelineContentLeft(pathTimelineBounds)
            + (seconds * PathTimelineTicksPerSecond - pathTimelineScrollTicks)
            * GuiElement.scaled(PathTimelinePixelsPerTick);
    }

    private bool PathTimelineContentContains(double mouseX, double mouseY)
    {
        if (pathTimelineBounds is null)
        {
            return false;
        }
        pathTimelineBounds.CalcWorldBounds();
        double left = ReplayTimelineContentLeft(pathTimelineBounds);
        return mouseX >= left
            && mouseX <= pathTimelineBounds.renderX + pathTimelineBounds.OuterWidth
            && mouseY >= pathTimelineBounds.renderY
            && mouseY <= pathTimelineBounds.renderY + pathTimelineBounds.OuterHeight;
    }

    private bool PathTimelineTrackLabelsContain(double mouseX, double mouseY)
    {
        if (pathTimelineBounds is null)
        {
            return false;
        }
        pathTimelineBounds.CalcWorldBounds();
        double left = pathTimelineBounds.renderX;
        double right = ReplayTimelineContentLeft(pathTimelineBounds);
        double top = pathTimelineBounds.renderY + GuiElement.scaled(PathTimelineHeaderHeight);
        double bottom = pathTimelineBounds.renderY + pathTimelineBounds.OuterHeight;
        return mouseX >= left && mouseX < right && mouseY >= top && mouseY <= bottom;
    }

    private void ScrollPathTimelineTracks(int delta)
    {
        if (pathEdit is null || pathTimelineBounds is null)
        {
            return;
        }
        var rows = new DirectorTimelineRows(pathEdit.ElementTracks.Select(t => TrackHeight(t.Id)).Append(40));
        pathTimelineFirstVisibleTrack = Math.Clamp(
            pathTimelineFirstVisibleTrack + delta,
            0,
            rows.MaxFirst(pathTimelineBounds.OuterHeight / GuiElement.scaled(1) - PathTimelineHeaderHeight));
    }

    private DirectorPathElementTrack? PathTimelineTrackAt(double mouseY)
    {
        if (pathEdit is null || pathTimelineBounds is null)
        {
            return null;
        }
        pathTimelineBounds.CalcWorldBounds();
        int index = TimelineRowAt(mouseY, false);
        if (mouseY >= pathTimelineBounds.renderY + pathTimelineBounds.OuterHeight)
        {
            return null;
        }
        return index >= 0 && index < pathEdit.ElementTracks.Count
            ? pathEdit.ElementTracks[index]
            : null;
    }

    private bool TryBeginPathTimelineMouseDown(MouseEvent args)
    {
        if (pathEdit is null || pathTimelineBounds is null)
        {
            return false;
        }
        pathTimelineBounds.CalcWorldBounds();
        if (args.Button == EnumMouseButton.Middle && PathTimelineContentContains(args.X, args.Y))
        {
            pathTimelinePanning = true;
            pathTimelinePanStartMouseX = args.X;
            pathTimelinePanStartTicks = pathTimelineScrollTicks;
            return true;
        }
        if (args.Button != EnumMouseButton.Left)
        {
            return false;
        }
        DirectorPathElementTrack? track = PathTimelineTrackAt(args.Y);
        double localX = args.X - pathTimelineBounds.renderX;
        if (track is not null && localX >= GuiElement.scaled(8d)
            && localX <= GuiElement.scaled(36d))
        {
            return BeginPathTrackReorder(track);
        }
        if (!PathTimelineContentContains(args.X, args.Y))
        {
            return false;
        }

        if (track is null)
        {
            pathTimelineScrubbing = true;
            SetPathAuthoringPlayhead(PathTimelineTimeAt(args.X), refreshPathMarker: true);
            ComposeTimeline();
            return true;
        }
        selectedPathElementTrackId = track.Id;
        double hitRadius = GuiElement.scaled(PathTimelineTrackHeight * 0.5d);
        if (track.TrackType == DirectorPathElementTrackType.CameraPath
            && pathEdit.Keyframes.Count > 0)
        {
            int nearest = Enumerable.Range(0, pathEdit.Keyframes.Count)
                .OrderBy(index => Math.Abs(
                    PathTimelineXForSeconds(pathEdit.Keyframes[index].TimeSeconds) - args.X))
                .First();
            if (Math.Abs(PathTimelineXForSeconds(pathEdit.Keyframes[nearest].TimeSeconds) - args.X)
                <= hitRadius)
            {
                SelectOnlyPathKeyframe(nearest);
                selectedPathElementKeyframeId = Guid.Empty;
                BeginPathTimelineKeyframeDrag(PathTimelineDragKind.Camera, nearest, Guid.Empty,
                    pathEdit.Keyframes[nearest].TimeSeconds);
                return true;
            }
        }
        else
        {
            DirectorPathElementKeyframe? nearest = pathEdit.ElementKeyframes
                .Where(frame => frame.TrackId == track.Id)
                .OrderBy(frame => Math.Abs(PathTimelineXForSeconds(frame.TimeSeconds) - args.X))
                .FirstOrDefault();
            if (nearest is not null
                && Math.Abs(PathTimelineXForSeconds(nearest.TimeSeconds) - args.X) <= hitRadius)
            {
                selectedPathElementKeyframeId = nearest.Id;
                selectedPathKeyframes.Clear();
                selectedPathKeyframeIndex = -1;
                BeginPathTimelineKeyframeDrag(PathTimelineDragKind.Element, -1, nearest.Id,
                    nearest.TimeSeconds);
                return true;
            }
        }

        SetPathAuthoringPlayhead(PathTimelineTimeAt(args.X), refreshPathMarker: true);
        ComposeTimeline();
        return true;
    }

    private void BeginPathTimelineKeyframeDrag(
        PathTimelineDragKind kind,
        int cameraIndex,
        Guid elementId,
        double time)
    {
        pathTimelineDragKind = kind;
        pathTimelineCameraDragIndex = cameraIndex;
        pathTimelineElementDragId = elementId;
        pathTimelineDragTime = time;
        pathTimelineDragMoved = false;
        pathTimelineDragBefore = pathEdit?.DeepClone();
        SetPathAuthoringPlayhead(time, refreshPathMarker: true);
        ComposeInspector();
        ComposeTimeline();
    }

    private void UpdatePathElementTimelineMouseMove(MouseEvent args)
    {
        if (pathTimelinePanning)
        {
            double delta = (args.X - pathTimelinePanStartMouseX)
                / DirectorTimelineCoordinateMath.PositivePixelsPerTick(
                    GuiElement.scaled(PathTimelinePixelsPerTick));
            pathTimelineScrollTicks = pathTimelinePanStartTicks - delta;
            ScrollPathTimeline(0d);
            ComposeTimeline();
            args.Handled = true;
            return;
        }
        if (pathElementTrackReordering)
        {
            UpdatePathTrackReorder(args.Y);
            args.Handled = true;
            return;
        }
        if (pathTimelineDragKind == PathTimelineDragKind.None)
        {
            if (pathTimelineScrubbing)
            {
                SetPathAuthoringPlayhead(
                    PathTimelineTimeAt(args.X),
                    refreshPathMarker: false);
                ComposeTimeline();
                args.Handled = true;
            }
            return;
        }
        double next = PathTimelineTimeAt(args.X);
        pathTimelineDragMoved |= DirectorPathElementTimeline.TickAt(next)
            != DirectorPathElementTimeline.TickAt(pathTimelineDragTime);
        pathTimelineDragTime = next;
        pathEditorPlayhead = next;
        ComposeTimeline();
        args.Handled = true;
    }

    private bool FinishPathElementTimelineMouseUp(MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Middle && pathTimelinePanning)
        {
            pathTimelinePanning = false;
            return true;
        }
        if (args.Button != EnumMouseButton.Left)
        {
            return false;
        }
        if (pathTimelineScrubbing)
        {
            pathTimelineScrubbing = false;
            SetPathAuthoringPlayhead(pathEditorPlayhead, refreshPathMarker: true);
            ComposeTimeline();
            return true;
        }
        if (pathElementTrackReordering)
        {
            pathElementTrackReordering = false;
            pathElementTrackReorderId = Guid.Empty;
            pathElementTrackReorderIndex = -1;
            CameraPath? reorderBefore = pathElementTrackReorderBefore;
            pathElementTrackReorderBefore = null;
            PersistPathTimelineEdit(reorderBefore);
            return true;
        }
        if (pathTimelineDragKind == PathTimelineDragKind.None || pathEdit is null)
        {
            return false;
        }

        PathTimelineDragKind kind = pathTimelineDragKind;
        int cameraIndex = pathTimelineCameraDragIndex;
        Guid elementId = pathTimelineElementDragId;
        bool moved = pathTimelineDragMoved;
        CameraPath? before = pathTimelineDragBefore;
        double time = pathTimelineDragTime;
        pathTimelineDragKind = PathTimelineDragKind.None;
        pathTimelineCameraDragIndex = -1;
        pathTimelineElementDragId = Guid.Empty;
        pathTimelineDragTime = double.NaN;
        pathTimelineDragMoved = false;
        pathTimelineDragBefore = null;

        if (moved && kind == PathTimelineDragKind.Camera)
        {
            if (CameraPathTiming.TrySetKeyframeTime(pathEdit, cameraIndex, time,
                    out double applied))
            {
                int nextIndex = pathEdit.Keyframes.FindIndex(frame =>
                    Math.Abs(frame.TimeSeconds - applied) <= CameraPathTiming.TimeMatchEpsilon);
                SelectOnlyPathKeyframe(nextIndex);
            }
        }
        else if (moved && kind == PathTimelineDragKind.Element)
        {
            DirectorPathElementKeyframe? frame = pathEdit.ElementKeyframes.FirstOrDefault(
                item => item.Id == elementId);
            if (frame is not null)
            {
                int tick = DirectorPathElementTimeline.TickAt(time);
                pathEdit.ElementKeyframes.RemoveAll(item => item.Id != frame.Id
                    && item.TrackId == frame.TrackId
                    && DirectorPathElementTimeline.TickAt(item.TimeSeconds) == tick);
                frame.TimeSeconds = tick
                    / PathTimelineTicksPerSecond;
                DirectorPathElementTimeline.Normalize(pathEdit);
            }
        }
        pathEditorPlayhead = time;
        PersistPathTimelineEdit(moved ? before : null);
        if (!moved)
        {
            OpenPathKeyframeEditor(kind, cameraIndex, elementId);
        }
        return true;
    }

    private void ZoomPathElementTimeline(double direction, double mouseX)
    {
        if (pathTimelineBounds is null || Math.Abs(direction) <= double.Epsilon)
        {
            return;
        }
        double left = ReplayTimelineContentLeft(pathTimelineBounds);
        double oldPixels = GuiElement.scaled(PathTimelinePixelsPerTick);
        double anchor = pathTimelineScrollTicks + (mouseX - left)
            / DirectorTimelineCoordinateMath.PositivePixelsPerTick(oldPixels);
        pathTimelineTickScaleIndex = Math.Clamp(
            pathTimelineTickScaleIndex + (direction > 0d ? -1 : 1),
            0,
            PathTimelineTickScaleLevels.Length - 1);
        double newPixels = GuiElement.scaled(PathTimelinePixelsPerTick);
        pathTimelineScrollTicks = anchor - (mouseX - left)
            / DirectorTimelineCoordinateMath.PositivePixelsPerTick(newPixels);
        ScrollPathTimeline(0d);
    }

    private void ScrollPathTimeline(double deltaTicks)
    {
        if (pathTimelineBounds is null)
        {
            return;
        }
        double visible = Math.Max(1d,
            pathTimelineBounds.OuterWidth - ReplayTimelineLabelColumnWidth(pathTimelineBounds))
            / DirectorTimelineCoordinateMath.PositivePixelsPerTick(
                GuiElement.scaled(PathTimelinePixelsPerTick));
        pathTimelineScrollTicks = Math.Clamp(
            pathTimelineScrollTicks + deltaTicks,
            0d,
            Math.Max(0d, PathTimelineDurationTicks() - visible));
    }

    private void StepPathTimelineTick(int direction)
    {
        int tick = Math.Clamp(
            DirectorPathElementTimeline.TickAt(pathEditorPlayhead) + Math.Sign(direction),
            0,
            PathTimelineDurationTicks());
        SetPathAuthoringPlayhead(
            tick / PathTimelineTicksPerSecond,
            refreshPathMarker: true);
        if (pathTimelineBounds is not null)
        {
            double visible = Math.Max(1d,
                pathTimelineBounds.OuterWidth - ReplayTimelineLabelColumnWidth(pathTimelineBounds))
                / DirectorTimelineCoordinateMath.PositivePixelsPerTick(
                    GuiElement.scaled(PathTimelinePixelsPerTick));
            if (tick < pathTimelineScrollTicks) pathTimelineScrollTicks = tick;
            if (tick > pathTimelineScrollTicks + visible)
            {
                pathTimelineScrollTicks = Math.Max(0d, tick - visible);
            }
        }
        ComposeTimeline();
    }

    private bool BeginPathTrackReorder(DirectorPathElementTrack track)
    {
        if (pathEdit is null)
        {
            return false;
        }
        pathElementTrackReordering = true;
        pathElementTrackReorderId = track.Id;
        pathElementTrackReorderIndex = pathEdit.ElementTracks.FindIndex(item => item.Id == track.Id);
        pathElementTrackReorderBefore = pathEdit.DeepClone();
        selectedPathElementTrackId = track.Id;
        return true;
    }

    private void UpdatePathTrackReorder(double mouseY)
    {
        if (pathEdit is null || pathTimelineBounds is null)
        {
            return;
        }
        pathTimelineBounds.CalcWorldBounds();
        int target = TimelineRowAt(mouseY, false);
        target = Math.Clamp(target, 0, pathEdit.ElementTracks.Count - 1);
        int source = pathEdit.ElementTracks.FindIndex(track => track.Id == pathElementTrackReorderId);
        if (source < 0 || source == target)
        {
            return;
        }
        DirectorPathElementTrack moved = pathEdit.ElementTracks[source];
        pathEdit.ElementTracks.RemoveAt(source);
        pathEdit.ElementTracks.Insert(target, moved);
        pathElementTrackReorderIndex = target;
        ComposeTimeline();
    }

    private bool SelectPathElementTrack(Guid trackId)
    {
        selectedPathElementTrackId = trackId;
        selectedPathElementKeyframeId = Guid.Empty;
        ComposeTimeline();
        return true;
    }

    private bool PathTrackVisible(DirectorPathElementTrack track)
        => track.TrackType == DirectorPathElementTrackType.CameraPath
            ? pathEdit?.PreviewVisible == true
            : track.Enabled;

    private bool TogglePathElementTrack(Guid trackId)
    {
        if (pathEdit?.ElementTracks.FirstOrDefault(track => track.Id == trackId)
            is not DirectorPathElementTrack track)
        {
            return true;
        }
        CameraPath before = pathEdit.DeepClone();
        if (track.TrackType == DirectorPathElementTrackType.CameraPath)
        {
            pathEdit.PreviewVisible = !pathEdit.PreviewVisible;
        }
        else
        {
            track.Enabled = !track.Enabled;
        }
        PersistPathTimelineEdit(before);
        return true;
    }

    private bool StopPathTimelinePlayback()
    {
        owner.ExitCamera();
        ComposeTimeline();
        return true;
    }

    private bool PlayPathTimeline(bool reverse)
    {
        bool result = PlayEditedPath(reverse);
        ComposeTimeline();
        return result;
    }

    private static string PathElementTrackName(DirectorPathElementTrackType type)
        => Lang.Get(type switch
        {
            DirectorPathElementTrackType.CameraPath => "vsdirector:path-element-camera",
            DirectorPathElementTrackType.Fov => "vsdirector:path-element-fov",
            DirectorPathElementTrackType.Roll => "vsdirector:path-element-roll",
            DirectorPathElementTrackType.CameraShake => "vsdirector:path-element-shake",
            DirectorPathElementTrackType.LookAt => "vsdirector:path-element-look-at",
            DirectorPathElementTrackType.Speed => "vsdirector:path-element-speed",
            DirectorPathElementTrackType.Marker => "vsdirector:path-element-marker",
            _ => "vsdirector:path-element-camera"
        });

    private bool PersistPathTimelineEdit(CameraPath? before)
    {
        if (pathEdit is null)
        {
            return false;
        }
        DirectorPathElementTimeline.Normalize(pathEdit);
        RecordPathEdit(before);
        Guid id = pathEdit.Id;
        if (!owner.TrySavePath(pathEdit, out _, showNotification: false))
        {
            return false;
        }
        pathEdit = owner.FindPath(id) ?? pathEdit;
        owner.SetPathEditPreview(
            pathEdit,
            SelectedPathKeyframeIndex(),
            pathEditorPlayhead,
            selectedPathKeyframes);
        ComposeResources();
        ComposeInspector();
        ComposeTimeline();
        return true;
    }

    private enum PathTimelineDragKind
    {
        None,
        Camera,
        Element
    }

    private enum PathFloatingEditorKind
    {
        None,
        AddElement,
        Track,
        CameraKeyframe,
        ElementKeyframe
    }
}
