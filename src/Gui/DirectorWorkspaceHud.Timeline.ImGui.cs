using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private const double TimelineFooterHeight = 36;
    private DirectorTimelineWindow? imGuiTimeline;
    private bool imGuiTimelineDirty;
    private double timelineLabelsBottom;

    private void MoveImGuiTimelinePointer(MouseEvent args)
    {
        if (resizingTimelineTrack != Guid.Empty)
        {
            double scale = Math.Max(.001, GuiElement.scaled(1));
            double next = DirectorTimelineRows.NormalizeHeight(rowResizeHeight + args.Y / scale - rowResizeY);
            layout.TimelineRowHeights[resizingTimelineTrack] = next;
            imGuiTimelineDirty = true;
            return;
        }
        OnMouseMove(args);
        imGuiTimelineDirty = true;
    }

    private void UpdateImGuiTimelineFrame()
    {
        PollWaveforms();
        if (imGuiTimelineDirty && imGuiTimeline is not null && section is DirectorWorkspaceSection.Paths or DirectorWorkspaceSection.Replay)
            ComposeImGuiTimeline();
        else UpdateImGuiTimelinePlayhead();
    }

    private void ComposeImGuiTimeline()
    {
        imGuiTimelineDirty = false;
        var panel = layout.Panel("timeline");
        double height = Math.Max(1, panel.Height - 4);
        var bounds = ElementBounds.Fixed(0, 0, panel.Width, height);
        PanelRoot(panel).WithParent(ElementBounds.Empty).WithChildren(bounds).CalcWorldBounds();
        bool replay = section == DirectorWorkspaceSection.Replay;
        replayTimelineBounds = replay ? bounds : null;
        pathTimelineBounds = section == DirectorWorkspaceSection.Paths ? bounds : null;
        imGuiTimeline ??= new DirectorTimelineWindow(capi, ImGuiTimelineMouseDown, MoveImGuiTimelinePointer, FinishTimelinePointer,
            ImGuiTimelineWheel, OnKeyDown, ResetWorkspacePointerInteraction, () => OnEscapePressed(), imGuiWorkspace);
        imGuiTimeline.Begin(panel.X, panel.Y, panel.Width, panel.Height);
        var preview = layout.Panel("preview");
        imGuiTimeline.SetResizeCorner(preview.X + preview.Width - panel.X);
        double label = ReplayTimelineLabelColumnWidth(panel.Width);
        if (replay) BuildImGuiReplayTimeline(panel.Width, height - TimelineFooterHeight, label);
        else if (section == DirectorWorkspaceSection.Paths) BuildImGuiPathTimeline(panel.Width, height - TimelineFooterHeight, label);
        imGuiTimeline.End();
        imGuiTimeline.SetInputEnabled(!layout.NavigationMode);
        UpdateImGuiTimelinePlayhead();
        if (IsOpened()) imGuiTimeline.OpenTimeline();
    }

    private void ImGuiTimelineWheel(int x, int y, float delta, bool ctrl)
    {
        if (delta == 0 || layout.NavigationMode) return;
        int direction = Math.Sign(delta);
        if (section == DirectorWorkspaceSection.Paths)
        {
            if (ctrl && (PathTimelineTrackLabelsContain(x, y) || PathTimelineContentContains(x, y)))
                ZoomPathElementTimeline(direction, Math.Max(x, ReplayTimelineContentLeft(pathTimelineBounds!)));
            else if (PathTimelineTrackLabelsContain(x, y)) ScrollPathTimelineTracks(-direction);
            else if (PathTimelineContentContains(x, y))
            {
                if (y >= pathTimelineBounds!.renderY + GuiElement.scaled(PathTimelineHeaderHeight)) ScrollPathTimelineTracks(-direction);
                else ScrollPathTimeline(-direction * PathTimelineLabelTickStep * 2d);
            }
        }
        else if (section == DirectorWorkspaceSection.Replay)
        {
            if (ctrl && (ReplayTimelineTrackLabelsContain(x, y) || ReplayTimelineInteractionContains(x, y)))
                ZoomReplayTimeline(direction, Math.Max(x, ReplayTimelineContentLeft(replayTimelineBounds!)));
            else if (ReplayTimelineTrackLabelsContain(x, y)) ScrollReplayTimelineTracks(-direction);
            else if (ReplayTimelineInteractionContains(x, y))
            {
                if (y >= replayTimelineBounds!.renderY + GuiElement.scaled(ReplayTimelineHeaderHeight)) ScrollReplayTimelineTracks(-direction);
                else ScrollReplayTimeline(-direction * ReplayTimelineLabelTickStep * 2d);
            }
        }
        ComposeImGuiTimeline();
    }

    private void ImGuiTimelineMouseDown(MouseEvent args)
    {
        if (TryResizeTimelineRow(args)) return;
        if (args.Button == Vintagestory.API.Common.EnumMouseButton.Left && TryOpenImGuiTimelapseInterval(args.X, args.Y)) return;
        OnMouseDown(args);
    }

    private bool TryOpenImGuiTimelapseInterval(int x, int y)
    {
        if (section != DirectorWorkspaceSection.Replay || !ReplayTimelineInteractionContains(x, y)
            || ReplayTimelineElementTrackAt(y) is not { TrackType: DirectorReplayElementTrackType.Timelapse } track) return false;
        var timeline = CurrentReplayViewportTimeline();
        foreach (var pair in DirectorReplayTimelapse.FindPairs(timeline.ElementKeyframes, track.Id))
        {
            double left = ReplayTimelineXForSeconds(replayTimelineBounds!, pair.Start.TimeSeconds);
            double right = ReplayTimelineXForSeconds(replayTimelineBounds!, pair.End.TimeSeconds);
            if (right < left) (left, right) = (right, left);
            // Keep endpoint presses owned by the existing drag gesture.
            double edge = GuiElement.scaled(ReplayTimelineTrackHeight * .5);
            if (x <= left + edge || x >= right - edge) continue;
            selectedReplayElementTrackId = track.Id;
            selectedReplayElementKeyframeId = pair.Start.Id;
            replayElementFieldsId = Guid.Empty;
            OpenSelectedReplayElementKeyframeEditor();
            return true;
        }
        return false;
    }

    private void UpdateImGuiTimelinePlayhead()
    {
        if (imGuiTimeline is null || section is not (DirectorWorkspaceSection.Paths or DirectorWorkspaceSection.Replay)) return;
        var panel = layout.Panel("timeline");
        bool replay = section == DirectorWorkspaceSection.Replay;
        double time = replay ? (scrubbingReplay ? previewReplayTime : owner.ReplayTimeSeconds)
            : owner.IsPathPlaying ? owner.ActivePathTimeSeconds : pathEditorPlayhead;
        double label = ReplayTimelineLabelColumnWidth(panel.Width);
        double X(double seconds) => label + (seconds * ReplayTimelineTicksPerSecond - (replay ? replayTimelineScrollTicks : pathTimelineScrollTicks))
            * (replay ? ReplayTimelinePixelsPerTick : PathTimelinePixelsPerTick);
        imGuiTimeline.Playhead(X(time), label, panel.Width, panel.Height - 4);
        double target = replay ? replayKeyframeDragTime : pathTimelineDragTime;
        double original = double.NaN;
        if (replay && double.IsFinite(target))
        {
            original = replayAuxiliaryKeyframeDragKind switch
            {
                ReplayAuxiliaryKeyframeDragKind.Marker => owner.ReplayMarkers.FirstOrDefault(k => k.Id == replayAuxiliaryKeyframeDragId)?.TimeSeconds ?? double.NaN,
                ReplayAuxiliaryKeyframeDragKind.Speed => owner.ReplaySpeedKeyframes.FirstOrDefault(k => k.Id == replayAuxiliaryKeyframeDragId)?.TimeSeconds ?? double.NaN,
                ReplayAuxiliaryKeyframeDragKind.Element => CurrentReplayViewportTimeline().ElementKeyframes.FirstOrDefault(k => k.Id == replayAuxiliaryKeyframeDragId)?.TimeSeconds ?? double.NaN,
                _ => double.NaN
            };
            if (replayViewportDragMode != ReplayViewportDragMode.None)
            {
                original = replayViewportDragOriginalStart;
                if (replayViewportDragMode == ReplayViewportDragMode.ResizeEnd)
                {
                    original += replayViewportDragOriginalDuration;
                    var clip = replayViewportDraftTimeline?.Clips.FirstOrDefault(c => c.Id == replayViewportDragClipId);
                    if (clip is not null) target = clip.TimelineStartSeconds + clip.DurationSeconds;
                }
            }
        }
        else if (!replay && pathTimelineDragBefore is { } before)
        {
            if (pathTimelineDragKind == PathTimelineDragKind.Camera && pathTimelineCameraDragIndex >= 0 && pathTimelineCameraDragIndex < before.Keyframes.Count)
                original = before.Keyframes[pathTimelineCameraDragIndex].TimeSeconds;
            else if (pathTimelineDragKind == PathTimelineDragKind.Element)
                original = before.ElementKeyframes.FirstOrDefault(k => k.Id == pathTimelineElementDragId)?.TimeSeconds ?? double.NaN;
        }
        string text = DirectorTimelineCursor.TimeLabel(double.IsFinite(target) ? target : time, (int)ReplayTimelineTicksPerSecond);
        imGuiTimeline.CursorFeedback(text, double.IsFinite(target) ? X(target) : double.NaN,
            double.IsFinite(original) ? X(original) : double.NaN);
    }

    private void ImGuiTimelineRuler(double width, double label, int duration, double scroll,
        double pixels, int minor, int major)
    {
        var view = imGuiTimeline!;
        view.Shape("ruler", "timeline-ruler", label, 0, width - label, ReplayTimelineRulerHeight);
        view.Shape("labels", "timeline-label-bg", 0, 0, label, ReplayTimelineRulerHeight);
        view.Shape("ruler-baseline", "timeline-tick major", label, 35, width - label, 1);
        int first = Math.Max(0, (int)Math.Ceiling(scroll / minor) * minor);
        int last = Math.Min(duration, (int)Math.Ceiling(scroll + (width - label) / pixels));
        for (int tick = first; tick <= last; tick += minor)
        {
            double x = label + (tick - scroll) * pixels;
            bool large = tick % major == 0;
            view.Shape("tick-" + tick, "timeline-tick" + (large ? " major" : ""), x, large ? 22 : 29, 1, large ? 13 : 6);
            if (large && x + 80 <= width)
                view.Shape("tick-label-" + tick, "timeline-ruler-label", x + 3, 0, 77, 21, FormatReplayTimelineTick(tick));
        }
    }

    private void ImGuiTimelineTransport(bool replay)
    {
        var view = imGuiTimeline!;
        void Button(string id, int index, string caption, string icon, Func<bool> click, bool enabled, string tone = "")
            => view.Button(id, 10 + index * 30, 3, 28,
                Lang.Get("vsdirector:" + caption, FormatReplayTransportSpeed(owner.ReplayBasePlaybackSpeed)), icon, () => click(), enabled, tone);
        if (replay)
        {
            bool playing = owner.IsReplayPlaying;
            Button("replaySkipBack", 0, "replay-skip-backward", "track-prev", SkipReplayBackward, playing);
            Button("replaySlowDown", 1, "replay-slow-down", "skip-back", SlowDownReplay, playing,
                owner.ReplayBasePlaybackSpeed < 1 ? "active" : "");
            Button("pause", 2, owner.IsReplayPaused ? "replay-play" : "replay-pause", owner.IsReplayPaused ? "play" : "pause",
                ToggleReplayPause, playing || owner.IsReplayRecording, "primary");
            Button("replaySpeedUp", 3, "replay-speed-up", "skip-forward", SpeedUpReplay, playing,
                owner.ReplayBasePlaybackSpeed > 1 ? "active" : "");
            Button("replaySkipForward", 4, "replay-skip-forward", "track-next", SkipReplayForward, playing);
            Button("exit", 5, "replay-exit", "stop", StopReplay, playing);
            Button("replayShotCamera", 6, owner.IsReplayTimelineCameraActive ? "replay-free-camera-button" : "replay-timeline-camera-button",
                "video", ToggleReplayShotCamera, playing, owner.IsReplayTimelineCameraActive ? "active" : "");
            Button("replayMoveToPlayer", 7, "replay-move-to-player", "focus-2", OpenReplayPlayerPicker, playing);
            Button("replayTimelineFit", 8, "replay-timeline-fit", "arrows-maximize", FitReplayTimeline, playing);
        }
        else
        {
            int count = pathEdit?.Keyframes.Count ?? 0;
            Button("pathTimelinePrevious", 0, "previous-keyframe", "track-prev", SelectPreviousPathKeyframe, count > 0);
            Button("pathTimelineReverse", 1, "reverse", "play", () => PlayPathTimeline(true), count >= 2, "reverse");
            Button("pathTimelinePlay", 2, "play-path", "play", () => PlayPathTimeline(false), count >= 2, "primary");
            Button("pathTimelineNext", 3, "next-keyframe", "track-next", SelectNextPathKeyframe, count > 0);
            Button("pathTimelineStop", 4, "path-stop-playback", "stop", StopPathTimelinePlayback, owner.IsPathPlaying);
        }
    }

    private void ImGuiTimelineRow(string id, int index, double y, double width, double label, double rowHeight, string name,
        string icon, bool selected, bool visible, Func<bool> options, Func<bool> toggle, Func<bool> add, bool? expanded = null)
    {
        var view = imGuiTimeline!;
        view.Shape("row-" + id, "timeline-row" + (selected ? " selected" : index % 2 == 1 ? " alternate" : ""),
            0, y, width, rowHeight);
        view.PushClip(0, 0, label, timelineLabelsBottom);
        // The grip remains on the input surface so the existing reorder gesture owns pointer capture.
        double center = Math.Max(20, rowHeight * .5);
        view.Shape("grip-" + id, "timeline-track-icon", 12, y + center - 13, 24, 26, icon: "grip-vertical");
        if (expanded.HasValue)
            view.Button("expand-" + id, 36, y + center - 14, 24, Lang.Get("vsdirector:replay-preset-group-expand"),
                expanded.Value ? "chevron-down" : "chevron-right", () => add());
        else view.Shape("icon-" + id, "timeline-track-icon", 36, y + center - 13, 24, 26, icon: icon);
        double addX = Math.Max(190, label - ReplayTimelineLabelColumnExpansion - 30);
        view.Shape("name-" + id, "timeline-track-name", 60, y + center - 13, Math.Max(26, addX - 120), 26, name);
        view.Button("options-" + id, addX - 56, y + center - 14, 28, Lang.Get("vsdirector:replay-element-track-options"), "menu-2", () => options());
        view.Button("visible-" + id, addX - 28, y + center - 14, 28, Lang.Get(visible ? "vsdirector:replay-viewport-hide" : "vsdirector:replay-viewport-show"),
            visible ? "eye" : "eye-off", () => toggle());
        view.Button("add-" + id, addX, y + center - 14, 28, Lang.Get(expanded.HasValue ? "vsdirector:replay-preset-group-expand" : "vsdirector:replay-element-keyframe-add"),
            expanded.HasValue ? (expanded.Value ? "chevron-up" : "chevron-down") : "plus", () => add());
        view.PopClip();
    }

    private void ImGuiTimelineKey(string id, double time, double rowY, double label, double width,
        double scroll, double pixels, bool selected, string role = "", string icon = "square-rotated-filled", double rowHeight = 40)
    {
        double x = label + (time * ReplayTimelineTicksPerSecond - scroll) * pixels;
        if (x < label || x > width) return;
        imGuiTimeline!.Shape("key-" + id, "timeline-key " + role + (icon == "square-rotated-filled" ? " dot" : "")
            + (selected ? " selected" : ""), x - 12, rowY + rowHeight / 2 - 12, 24, 24, icon: icon);
    }

    private void ImGuiTimelineReorder(int index, int first, int visible, double width)
    {
        bool replay = section == DirectorWorkspaceSection.Replay;
        var rows = replay ? ReplayRows() : PathRows();
        if (resizingTimelineTrack != Guid.Empty)
        {
            index = replay
                ? ActiveReplayTimelineElementTracks().ToList().FindIndex(t => t.Id == resizingTimelineTrack)
                : pathEdit?.ElementTracks.FindIndex(t => t.Id == resizingTimelineTrack) ?? -1;
        }
        double y = (replay ? ReplayTimelineHeaderHeight : PathTimelineHeaderHeight) + rows.Top(index) - rows.Top(first);
        if (index >= first && y < layout.Panel("timeline").Height - 14)
            imGuiTimeline!.Shape("reorder", "timeline-reorder", 0, y - 1, width, 2);
        if (resizingTimelineTrack != Guid.Empty)
        {
            bool resizingVisibleRow = replay
                ? index >= 0 && index < ActiveReplayTimelineElementTracks().Count
                    && ActiveReplayTimelineElementTracks()[index].Id == resizingTimelineTrack
                : pathEdit is not null && index >= 0 && index < pathEdit.ElementTracks.Count
                    && pathEdit.ElementTracks[index].Id == resizingTimelineTrack;
            if (resizingVisibleRow)
                imGuiTimeline!.Shape("row-resize-line", "timeline-reorder", 0, y + rows.Height(index) - 1, width, 2,
                    title: Lang.Get("vsdirector:timeline-resize-row"));
        }
    }

    private void DrawTimelineFooter(double width, double y, double label, bool replay)
    {
        var view = imGuiTimeline!;
        view.Shape("footer", "timeline-label-bg", 0, y, label, TimelineFooterHeight);
        view.BlockInput(0, y, label, TimelineFooterHeight + 4);
        if (!replay)
        {
            view.Button("pathAddElement", 8, y + 4, 120, Lang.Get("vsdirector:replay-element-add"), "", () => OpenPathElementMenu());
            return;
        }
        view.Button("replayAddElement", 8, y + 4, 120, Lang.Get("vsdirector:replay-element-add"), "", () => OpenReplayElementMenu(), owner.IsReplayPlaying);
        var timeline = CurrentReplayViewportTimeline();
        var options = new List<(string, string)> {
            ("scene-command-new", Lang.Get("vsdirector:replay-scene-new")),
            ("scene-command-rename", Lang.Get("vsdirector:replay-scene-rename")),
            ("scene-command-delete", Lang.Get("vsdirector:replay-scene-delete")) };
        options.AddRange(timeline.Scenes.Select(s => (s.Id.ToString("D"), s.Name)));
        view.Select("replaySceneSelector", 136, y + 4, Math.Max(34, label - 144), timeline.ActiveSceneId.ToString("D"), options,
            value => OnReplaySceneMenuChanged(value, true));
    }

    private void BuildImGuiPathTimeline(double width, double height, double label)
    {
        timelineLabelsBottom = height;
        int duration = PathTimelineDurationTicks();
        double pixels = PathTimelinePixelsPerTick;
        pathTimelineScrollTicks = Math.Clamp(pathTimelineScrollTicks, 0, Math.Max(0, duration - (width - label) / pixels));
        ImGuiTimelineRuler(width, label, duration, pathTimelineScrollTicks, pixels, PathTimelineMinorTickStep, PathTimelineLabelTickStep);
        ImGuiTimelineTransport(false);
        if (pathEdit is null) return;
        DirectorPathElementTimeline.Normalize(pathEdit);
        var tracks = pathEdit.ElementTracks;
        var rows = new DirectorTimelineRows(tracks.Select(t => TrackHeight(t.Id)));
        int visible = rows.Count;
        pathTimelineFirstVisibleTrack = Math.Clamp(pathTimelineFirstVisibleTrack, 0, rows.MaxFirst(height - PathTimelineHeaderHeight));
        imGuiTimeline!.PushClip(0, PathTimelineHeaderHeight, width, Math.Max(0, height + TimelineFooterHeight - PathTimelineHeaderHeight));
        for (int row = 0; row < visible; row++)
        {
            int index = pathTimelineFirstVisibleTrack + row;
            double y = PathTimelineHeaderHeight + rows.Top(index) - rows.Top(pathTimelineFirstVisibleTrack);
            if (y >= height + TimelineFooterHeight) break;
            if (index >= tracks.Count) break;
            var track = tracks[index]; string id = track.Id.ToString("N");
            ImGuiTimelineRow(id, index, y, width, label, TrackHeight(track.Id), track.Name.Length == 0 ? PathElementTrackName(track.TrackType) : track.Name,
                TimelineTrackIcon(track.TrackType.ToString()), track.Id == selectedPathElementTrackId, PathTrackVisible(track),
                () => OpenPathTrackEditor(track.Id), () => TogglePathElementTrack(track.Id), () => AddPathKeyframeForTrack(track.Id));
            if (track.TrackType == DirectorPathElementTrackType.CameraPath)
            {
                for (int i = 0; i < pathEdit.Keyframes.Count; i++)
                    ImGuiTimelineKey(id + "-" + i, pathTimelineDragKind == PathTimelineDragKind.Camera && i == pathTimelineCameraDragIndex
                        ? pathTimelineDragTime : pathEdit.Keyframes[i].TimeSeconds, y, label, width, pathTimelineScrollTicks, pixels, selectedPathKeyframes.Contains(i), rowHeight: TrackHeight(track.Id));
            }
            else foreach (var frame in pathEdit.ElementKeyframes.Where(f => f.TrackId == track.Id))
                ImGuiTimelineKey(frame.Id.ToString("N"), pathTimelineDragKind == PathTimelineDragKind.Element && frame.Id == pathTimelineElementDragId
                    ? pathTimelineDragTime : frame.TimeSeconds, y, label, width, pathTimelineScrollTicks, pixels, frame.Id == selectedPathElementKeyframeId,
                    icon: TimelineKeyIcon(frame.TrackType.ToString()), rowHeight: TrackHeight(track.Id));
        }
        if (pathElementTrackReordering || resizingTimelineTrack != Guid.Empty)
            ImGuiTimelineReorder(pathElementTrackReordering ? pathElementTrackReorderIndex : pathTimelineFirstVisibleTrack,
                pathTimelineFirstVisibleTrack, visible, width);
        imGuiTimeline!.PopClip();
        DrawTimelineFooter(width, height, label, false);
    }

    /// <summary>
    /// When a replay is opened and does not fit at the current zoom, start with the whole
    /// recording in view. Runs once per opened replay, so later zooming is kept.
    /// </summary>
    private void AutoFitOpenedReplay(double contentWidth, int durationTicks)
    {
        Guid replayId = owner.ActiveReplayId;
        if (replayId == replayTimelineAutoFitReplayId
            || replayId == Guid.Empty
            || !owner.IsReplayPlaying
            || owner.ReplayDurationSeconds <= 0d
            || contentWidth <= 1d)
        {
            return;
        }
        replayTimelineAutoFitReplayId = replayId;
        if (durationTicks * ReplayTimelinePixelsPerTick > contentWidth)
        {
            replayTimelineFitPixelsPerTick = contentWidth / Math.Max(1, durationTicks);
            replayTimelineScrollTicks = 0d;
        }
    }

    private void BuildImGuiReplayTimeline(double width, double height, double label)
    {
        timelineLabelsBottom = height;
        var timeline = CurrentReplayViewportTimeline();
        var tracks = timeline.VisibleTracks(expandedReplayPresetGroups).ToList();
        int duration = ReplayTimelineDurationTicks();
        AutoFitOpenedReplay(width - label, duration);
        double pixels = ReplayTimelinePixelsPerTick;
        replayTimelineScrollTicks = Math.Clamp(replayTimelineScrollTicks, 0, Math.Max(0, duration - (width - label) / pixels));
        double X(double seconds) => label + (seconds * ReplayTimelineTicksPerSecond - replayTimelineScrollTicks) * pixels;
        double Time(DirectorReplayElementKeyframe frame) => replayAuxiliaryKeyframeDragKind == ReplayAuxiliaryKeyframeDragKind.Element
            && frame.Id == replayAuxiliaryKeyframeDragId ? replayKeyframeDragTime : frame.TimeSeconds;
        ImGuiTimelineRuler(width, label, duration, replayTimelineScrollTicks, pixels, ReplayTimelineMinorTickStep, ReplayTimelineLabelTickStep);
        ImGuiTimelineTransport(true);
        var (start, end) = CurrentReplayExportRange();
        double left = Math.Clamp(X(start), label, width), right = Math.Clamp(X(end), label, width);
        if (right > left) imGuiTimeline!.Shape("export-range", "timeline-export", left, 0, right - left, ReplayTimelineRulerHeight - 1);
        var rows = new DirectorTimelineRows(tracks.Select(t => TrackHeight(t.Id)));
        int visible = rows.Count;
        replayTimelineFirstVisibleTrack = Math.Clamp(replayTimelineFirstVisibleTrack, 0, rows.MaxFirst(height - ReplayTimelineHeaderHeight));
        RequestWaveforms(timeline, tracks.Skip(replayTimelineFirstVisibleTrack)
                .TakeWhile(t => rows.Top(tracks.IndexOf(t)) - rows.Top(replayTimelineFirstVisibleTrack) < height - ReplayTimelineHeaderHeight),
            replayTimelineScrollTicks / ReplayTimelineTicksPerSecond,
            (replayTimelineScrollTicks + (width - label) / pixels) / ReplayTimelineTicksPerSecond, width - label);
        imGuiTimeline!.PushClip(0, ReplayTimelineHeaderHeight, width, Math.Max(0, height + TimelineFooterHeight - ReplayTimelineHeaderHeight));
        for (int row = 0; row < visible; row++)
        {
            int index = replayTimelineFirstVisibleTrack + row;
            double y = ReplayTimelineHeaderHeight + rows.Top(index) - rows.Top(replayTimelineFirstVisibleTrack);
            double rowHeight = rows.Height(index);
            if (y >= height + TimelineFooterHeight) break;
            if (index >= tracks.Count) break;
            var track = tracks[index]; string id = track.Id.ToString("N");
            bool headGroup = track.TrackType == DirectorReplayElementTrackType.PresetGroup
                && timeline.Tracks.Any(t => t.ParentGroupId == track.Id && t.TrackType == DirectorReplayElementTrackType.HeadMotion);
            string trackName = track.Name.Length > 0 ? track.Name : ReplayTimelineElementTrackName(
                headGroup ? ReplayTimelineElementKind.HeadMotion : ReplayTimelineElementKindFor(track.TrackType));
            ImGuiTimelineRow(id, index, y, width, label, rowHeight, (track.ParentGroupId == Guid.Empty ? "" : "    ") + trackName,
                TimelineTrackIcon(track.TrackType.ToString()), IsReplayTimelineElementTrackSelected(track), timeline.IsTrackEnabled(track),
                () => OpenReplayTrackEditor(track.Id), () => ToggleReplayElementTrack(track.Id), () => AddReplayKeyframeForTrack(track.Id),
                track.TrackType == DirectorReplayElementTrackType.PresetGroup ? expandedReplayPresetGroups.Contains(track.Id) : null);
            if (track.TrackType == DirectorReplayElementTrackType.PresetGroup)
            {
                var children = timeline.TrackFamily(track.Id);
                var frames = timeline.ElementKeyframes.Where(f => children.Contains(f.TrackId)).ToList();
                var clips = timeline.Clips.Where(c => children.Contains(c.TrackId)).ToList();
                var starts = frames.Select(f => f.TimeSeconds).Concat(clips.Select(c => c.TimelineStartSeconds)).ToList();
                var ends = frames.Select(f => f.TimeSeconds + (f.TrackType == DirectorReplayElementTrackType.Title ? f.TitleDurationSeconds : f.HeadMotion?.Duration ?? 0))
                    .Concat(clips.Select(c => c.TimelineStartSeconds + (c.SourceType == DirectorReplayViewportSourceType.StaticCamera ? 0 : c.DurationSeconds))).ToList();
                if (starts.Count > 0)
                {
                    left = Math.Max(label, X(starts.Min()));
                    right = Math.Min(width, X(Math.Max(starts.Min() + 1 / ReplayTimelineTicksPerSecond, ends.Max())));
                    if (right > left)
                    {
                        imGuiTimeline!.Shape("group-span-" + id, "timeline-interval", left, y + rowHeight / 2 - 10, right - left, 20);
                        if (right - left > 80)
                            imGuiTimeline.Shape("group-count-" + id, "timeline-interval-label", left + 6, y + rowHeight / 2 - 10,
                                right - left - 12, 20, Lang.Get("vsdirector:replay-preset-group-count", children.Count - 1));
                    }
                }
                continue;
            }
            if (track.TrackType == DirectorReplayElementTrackType.Camera)
            {
                foreach (var clip in timeline.Clips.Where(c => c.TrackId == track.Id && c.SourceType == DirectorReplayViewportSourceType.StaticCamera))
                    ImGuiTimelineKey(clip.Id.ToString("N"), clip.TimelineStartSeconds, y, label, width, replayTimelineScrollTicks, pixels, clip.Id == selectedReplayViewportClipId, rowHeight: rowHeight);
                continue;
            }
            if (track.TrackType == DirectorReplayElementTrackType.Timelapse)
                foreach (var pair in DirectorReplayTimelapse.FindPairs(timeline.ElementKeyframes, track.Id))
                {
                    left = Math.Max(label, Math.Min(X(Time(pair.Start)), X(Time(pair.End))));
                    right = Math.Min(width, Math.Max(X(Time(pair.Start)), X(Time(pair.End))));
                    if (right <= left) continue;
                    string pairId = pair.Start.Id.ToString("N");
                    bool selected = pair.Start.Id == selectedReplayElementKeyframeId || pair.End.Id == selectedReplayElementKeyframeId;
                    imGuiTimeline!.Shape("interval-" + pairId, "timeline-interval" + (selected ? " selected" : ""), left, y + rowHeight / 2 - 12, right - left, 24);
                    double seconds = pair.Start.TimelapseDurationSeconds > 0 ? pair.Start.TimelapseDurationSeconds
                        : pair.End.TimelapseDurationSeconds > 0 ? pair.End.TimelapseDurationSeconds : track.TimelapseDurationSeconds;
                    if (seconds <= 0) seconds = pair.End.TimelapseTick > pair.Start.TimelapseTick
                        ? (pair.End.TimelapseTick - pair.Start.TimelapseTick) / ReplayTimelineTicksPerSecond : Math.Max(0, pair.End.TimeSeconds - pair.Start.TimeSeconds);
                    if (right - left >= 64)
                        imGuiTimeline.Shape("interval-label-" + pairId, "timeline-interval-label", left + 10, y + rowHeight / 2 - 10, right - left - 20, 20,
                            seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "s");
                }
            foreach (var frame in timeline.ElementKeyframes.Where(f => f.TrackId == track.Id))
            {
                if (track.TrackType == DirectorReplayElementTrackType.HeadMotion && frame.HeadMotion is { Duration: > 0 } motion)
                {
                    double motionLeft = Math.Max(label, X(frame.TimeSeconds));
                    double motionRight = Math.Min(width, X(frame.TimeSeconds + motion.Duration));
                    if (motionRight > motionLeft)
                        imGuiTimeline!.Shape("head-motion-" + frame.Id.ToString("N"), "timeline-interval", motionLeft,
                            y + rowHeight / 2 - 8, motionRight - motionLeft, 16);
                }
                if (track.TrackType == DirectorReplayElementTrackType.Timelapse)
                {
                    double edgeX = X(Time(frame));
                    if (edgeX >= label && edgeX <= width)
                        imGuiTimeline!.Shape("interval-edge-" + frame.Id.ToString("N"), "timeline-interval-edge", edgeX - 1.5, y + rowHeight / 2 - 12, 3, 24);
                    continue;
                }
                string role = track.TrackType == DirectorReplayElementTrackType.Timelapse ? frame.TimelapseRole.ToString().ToLowerInvariant()
                    : track.TrackType == DirectorReplayElementTrackType.FollowEntity ? frame.FollowEntityRole.ToString().ToLowerInvariant() : "";
                ImGuiTimelineKey(frame.Id.ToString("N"), Time(frame), y, label, width, replayTimelineScrollTicks, pixels, frame.Id == selectedReplayElementKeyframeId, role,
                    TimelineKeyIcon(frame.TrackType.ToString(), frame.Frozen, frame.TimelapseRole == DirectorReplayTimelapseKeyframeRole.End), rowHeight);
            }
            if (track.TrackType == DirectorReplayElementTrackType.VoiceAudio)
                DrawVoiceWaveform(timeline, track, y, rowHeight, label, width);
        }
        if (replayElementTrackReordering || resizingTimelineTrack != Guid.Empty)
            ImGuiTimelineReorder(replayElementTrackReordering ? replayElementTrackReorderIndex : replayTimelineFirstVisibleTrack,
                replayTimelineFirstVisibleTrack, visible, width);
        imGuiTimeline!.PopClip();
        DrawTimelineFooter(width, height, label, true);
    }

    private static string TimelineTrackIcon(string type) => type switch
    {
        "Camera" or "CameraPath" => "camera", "Marker" => "bookmark", "Speed" => "gauge", "CameraOrbit" => "rotate-360",
        "TrackEntity" or "LookAt" => "focus-2", "FollowEntity" => "walk", "CameraShake" => "camera-rotate", "HeadMotion" => "eye", "Fov" => "eye",
        "Timelapse" => "hourglass", "TimeOfDay" => "clock", "Freeze" => "snowflake", "BlockOverride" => "cube",
        "Weather" => "cloud", "Sky" => "cloud", "Composition" => "layout-grid", "PresetGroup" => "folder",
        "VoiceAudio" or "Audio" => "volume", "Roll" => "rotate", "ColorGrade" => "palette", "Title" => "typography", _ => "menu-2"
    };

    private static string TimelineKeyIcon(string type, bool frozen = false, bool end = false) => type switch
    {
        "Freeze" => frozen ? "snowflake" : "snowflake-off",
        "TimeOfDay" => "clock-filled",
        "Timelapse" => end ? "hourglass-low" : "hourglass-high",
        "Fov" => "eye-filled",
        "Marker" => "bookmark-filled",
        _ => "square-rotated-filled"
    };
}
