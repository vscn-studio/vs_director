using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private DirectorTimelineWindow? rmlTimeline;
    private bool rmlTimelineDirty;

    private void MoveRmlTimelinePointer(MouseEvent args)
    {
        if (resizingTimelineTrack != Guid.Empty)
        {
            double scale = Math.Max(.001, GuiElement.scaled(1));
            double next = DirectorTimelineRows.NormalizeHeight(rowResizeHeight + args.Y / scale - rowResizeY);
            layout.TimelineRowHeights[resizingTimelineTrack] = next;
            rmlTimelineDirty = true;
            return;
        }
        OnMouseMove(args);
        rmlTimelineDirty = true;
    }

    private void UpdateRmlTimelineFrame()
    {
        PollWaveforms();
        if (rmlTimelineDirty && rmlTimeline is not null && section is DirectorWorkspaceSection.Paths or DirectorWorkspaceSection.Replay)
            ComposeRmlTimeline();
        else UpdateRmlTimelinePlayhead();
    }

    private void ComposeRmlTimeline()
    {
        rmlTimelineDirty = false;
        var panel = layout.Panel("timeline");
        double height = Math.Max(1, panel.Height - 14);
        var bounds = ElementBounds.Fixed(0, 0, panel.Width, height);
        PanelRoot(panel).WithParent(ElementBounds.Empty).WithChildren(bounds).CalcWorldBounds();
        bool replay = section == DirectorWorkspaceSection.Replay;
        replayTimelineBounds = replay ? bounds : null;
        pathTimelineBounds = section == DirectorWorkspaceSection.Paths ? bounds : null;
        rmlTimeline ??= new DirectorTimelineWindow(capi, RmlTimelineMouseDown, MoveRmlTimelinePointer, FinishTimelinePointer,
            RmlTimelineWheel, OnKeyDown, ResetWorkspacePointerInteraction, () => OnEscapePressed(), imGuiWorkspace);
        rmlTimeline.Begin(panel.X, panel.Y, panel.Width, panel.Height);
        var preview = layout.Panel("preview");
        rmlTimeline.SetResizeCorner(preview.X + preview.Width - panel.X);
        double label = ReplayTimelineLabelColumnWidth(panel.Width);
        if (replay) BuildRmlReplayTimeline(panel.Width, height, label);
        else if (section == DirectorWorkspaceSection.Paths) BuildRmlPathTimeline(panel.Width, height, label);
        rmlTimeline.End();
        rmlTimeline.SetInputEnabled(!layout.NavigationMode);
        UpdateRmlTimelinePlayhead();
        if (IsOpened()) rmlTimeline.OpenTimeline();
    }

    private void RmlTimelineWheel(int x, int y, float delta, bool ctrl)
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
        ComposeRmlTimeline();
    }

    private void RmlTimelineMouseDown(MouseEvent args)
    {
        if (TryResizeTimelineRow(args)) return;
        if (args.Button == Vintagestory.API.Common.EnumMouseButton.Left && TryOpenRmlTimelapseInterval(args.X, args.Y)) return;
        OnMouseDown(args);
    }

    private bool TryOpenRmlTimelapseInterval(int x, int y)
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

    private void UpdateRmlTimelinePlayhead()
    {
        if (rmlTimeline is null || section is not (DirectorWorkspaceSection.Paths or DirectorWorkspaceSection.Replay)) return;
        var panel = layout.Panel("timeline");
        bool replay = section == DirectorWorkspaceSection.Replay;
        double time = replay ? (scrubbingReplay ? previewReplayTime : owner.ReplayTimeSeconds)
            : owner.IsPathPlaying ? owner.ActivePathTimeSeconds : pathEditorPlayhead;
        double label = ReplayTimelineLabelColumnWidth(panel.Width);
        double x = label + (time * ReplayTimelineTicksPerSecond - (replay ? replayTimelineScrollTicks : pathTimelineScrollTicks))
            * (replay ? ReplayTimelinePixelsPerTick : PathTimelinePixelsPerTick);
        rmlTimeline.Playhead(x, label, panel.Width, panel.Height - 14);
    }

    private void RmlTimelineRuler(double width, double label, int duration, double scroll,
        double pixels, int minor, int major, double drag)
    {
        var view = rmlTimeline!;
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
            if (large && x + 80 <= width && (!double.IsFinite(drag) || Math.Abs(drag * ReplayTimelineTicksPerSecond - tick) * pixels >= 80))
                view.Shape("tick-label-" + tick, "timeline-ruler-label", x + 3, 0, 77, 21, FormatReplayTimelineTick(tick));
        }
        if (double.IsFinite(drag))
        {
            int tick = ReplayTimelineTickAt(drag);
            double x = Math.Clamp(label + (tick - scroll) * pixels + 3, label + 2, Math.Max(label + 2, width - 80));
            view.Shape("drag-time", "timeline-ruler-label", x, 0, 78, 21, FormatReplayTimelineTick(tick));
        }
    }

    private void RmlTimelineTransport(bool replay)
    {
        var view = rmlTimeline!;
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

    private void RmlTimelineRow(string id, int index, double y, double width, double label, double rowHeight, string name,
        string icon, bool selected, bool visible, Func<bool> options, Func<bool> toggle, Func<bool> add)
    {
        var view = rmlTimeline!;
        view.Shape("row-" + id, "timeline-row" + (selected ? " selected" : index % 2 == 1 ? " alternate" : ""),
            0, y, width, rowHeight);
        // The grip remains on the input surface so the existing reorder gesture owns pointer capture.
        double center = Math.Max(20, rowHeight * .5);
        view.Shape("grip-" + id, "timeline-track-icon", 12, y + center - 13, 24, 26, icon: "grip-vertical");
        view.Shape("icon-" + id, "timeline-track-icon", 36, y + center - 13, 24, 26, icon: icon);
        double addX = Math.Max(190, label - ReplayTimelineLabelColumnExpansion - 30);
        view.Shape("name-" + id, "timeline-track-name", 60, y + center - 13, Math.Max(26, addX - 120), 26, name);
        view.Button("options-" + id, addX - 56, y + center - 14, 28, Lang.Get("vsdirector:replay-element-track-options"), "menu-2", () => options());
        view.Button("visible-" + id, addX - 28, y + center - 14, 28, Lang.Get(visible ? "vsdirector:replay-viewport-hide" : "vsdirector:replay-viewport-show"),
            visible ? "eye" : "eye-off", () => toggle());
        view.Button("add-" + id, addX, y + center - 14, 28, Lang.Get("vsdirector:replay-element-keyframe-add"), "plus", () => add());
    }

    private void RmlTimelineKey(string id, double time, double rowY, double label, double width,
        double scroll, double pixels, bool selected, string role = "", string icon = "square-rotated-filled", double rowHeight = 40)
    {
        double x = label + (time * ReplayTimelineTicksPerSecond - scroll) * pixels;
        if (x < label || x > width) return;
        rmlTimeline!.Shape("key-" + id, "timeline-key " + role + (icon == "square-rotated-filled" ? " dot" : "")
            + (selected ? " selected" : ""), x - 12, rowY + rowHeight / 2 - 12, 24, 24, icon: icon);
    }

    private void RmlTimelineReorder(int index, int first, int visible, double width)
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
            rmlTimeline!.Shape("reorder", "timeline-reorder", 0, y - 1, width, 2);
        if (resizingTimelineTrack != Guid.Empty)
        {
            bool resizingVisibleRow = replay
                ? index >= 0 && index < ActiveReplayTimelineElementTracks().Count
                    && ActiveReplayTimelineElementTracks()[index].Id == resizingTimelineTrack
                : pathEdit is not null && index >= 0 && index < pathEdit.ElementTracks.Count
                    && pathEdit.ElementTracks[index].Id == resizingTimelineTrack;
            if (resizingVisibleRow)
                rmlTimeline!.Shape("row-resize-line", "timeline-reorder", 0, y + rows.Height(index) - 1, width, 2,
                    title: Lang.Get("vsdirector:timeline-resize-row"));
        }
    }

    private void BuildRmlPathTimeline(double width, double height, double label)
    {
        int duration = PathTimelineDurationTicks();
        double pixels = PathTimelinePixelsPerTick;
        pathTimelineScrollTicks = Math.Clamp(pathTimelineScrollTicks, 0, Math.Max(0, duration - (width - label) / pixels));
        RmlTimelineRuler(width, label, duration, pathTimelineScrollTicks, pixels, PathTimelineMinorTickStep, PathTimelineLabelTickStep, pathTimelineDragTime);
        RmlTimelineTransport(false);
        if (pathEdit is null) return;
        DirectorPathElementTimeline.Normalize(pathEdit);
        var tracks = pathEdit.ElementTracks;
        var rows = new DirectorTimelineRows(tracks.Select(t => TrackHeight(t.Id)).Append(40));
        int visible = rows.Count;
        pathTimelineFirstVisibleTrack = Math.Clamp(pathTimelineFirstVisibleTrack, 0, rows.MaxFirst(height - PathTimelineHeaderHeight));
        for (int row = 0; row < visible; row++)
        {
            int index = pathTimelineFirstVisibleTrack + row;
            double y = PathTimelineHeaderHeight + rows.Top(index) - rows.Top(pathTimelineFirstVisibleTrack);
            if (y >= height) break;
            if (index > tracks.Count) break;
            if (index == tracks.Count)
            {
                rmlTimeline!.Button("pathAddElement", 8, y + 1, 88, Lang.Get("vsdirector:replay-element-add"), "", () => OpenPathElementMenu());
                break;
            }
            var track = tracks[index]; string id = track.Id.ToString("N");
            RmlTimelineRow(id, index, y, width, label, TrackHeight(track.Id), track.Name.Length == 0 ? PathElementTrackName(track.TrackType) : track.Name,
                TimelineTrackIcon(track.TrackType.ToString()), track.Id == selectedPathElementTrackId, PathTrackVisible(track),
                () => OpenPathTrackEditor(track.Id), () => TogglePathElementTrack(track.Id), () => AddPathKeyframeForTrack(track.Id));
            if (track.TrackType == DirectorPathElementTrackType.CameraPath)
            {
                for (int i = 0; i < pathEdit.Keyframes.Count; i++)
                    RmlTimelineKey(id + "-" + i, pathTimelineDragKind == PathTimelineDragKind.Camera && i == pathTimelineCameraDragIndex
                        ? pathTimelineDragTime : pathEdit.Keyframes[i].TimeSeconds, y, label, width, pathTimelineScrollTicks, pixels, selectedPathKeyframes.Contains(i), rowHeight: TrackHeight(track.Id));
            }
            else foreach (var frame in pathEdit.ElementKeyframes.Where(f => f.TrackId == track.Id))
                RmlTimelineKey(frame.Id.ToString("N"), pathTimelineDragKind == PathTimelineDragKind.Element && frame.Id == pathTimelineElementDragId
                    ? pathTimelineDragTime : frame.TimeSeconds, y, label, width, pathTimelineScrollTicks, pixels, frame.Id == selectedPathElementKeyframeId,
                    icon: TimelineKeyIcon(frame.TrackType.ToString()), rowHeight: TrackHeight(track.Id));
        }
        if (pathElementTrackReordering || resizingTimelineTrack != Guid.Empty)
            RmlTimelineReorder(pathElementTrackReordering ? pathElementTrackReorderIndex : pathTimelineFirstVisibleTrack,
                pathTimelineFirstVisibleTrack, visible, width);
    }

    private void BuildRmlReplayTimeline(double width, double height, double label)
    {
        var timeline = CurrentReplayViewportTimeline();
        var tracks = timeline.Tracks.Where(t => t.TrackType != DirectorReplayElementTrackType.Marker).ToList();
        double pixels = ReplayTimelinePixelsPerTick;
        int duration = ReplayTimelineDurationTicks();
        replayTimelineScrollTicks = Math.Clamp(replayTimelineScrollTicks, 0, Math.Max(0, duration - (width - label) / pixels));
        double X(double seconds) => label + (seconds * ReplayTimelineTicksPerSecond - replayTimelineScrollTicks) * pixels;
        double Time(DirectorReplayElementKeyframe frame) => replayAuxiliaryKeyframeDragKind == ReplayAuxiliaryKeyframeDragKind.Element
            && frame.Id == replayAuxiliaryKeyframeDragId ? replayKeyframeDragTime : frame.TimeSeconds;
        RmlTimelineRuler(width, label, duration, replayTimelineScrollTicks, pixels, ReplayTimelineMinorTickStep, ReplayTimelineLabelTickStep, replayKeyframeDragTime);
        RmlTimelineTransport(true);
        var (start, end) = CurrentReplayExportRange();
        double left = Math.Clamp(X(start), label, width), right = Math.Clamp(X(end), label, width);
        if (right > left) rmlTimeline!.Shape("export-range", "timeline-export", left, 0, right - left, ReplayTimelineRulerHeight - 1);
        var rows = new DirectorTimelineRows(tracks.Select(t => TrackHeight(t.Id)).Append(ReplayTimelineTrackHeight));
        int visible = rows.Count;
        replayTimelineFirstVisibleTrack = Math.Clamp(replayTimelineFirstVisibleTrack, 0, rows.MaxFirst(height - ReplayTimelineHeaderHeight));
        RequestWaveforms(timeline, tracks.Skip(replayTimelineFirstVisibleTrack)
                .TakeWhile(t => rows.Top(tracks.IndexOf(t)) - rows.Top(replayTimelineFirstVisibleTrack) < height - ReplayTimelineHeaderHeight),
            replayTimelineScrollTicks / ReplayTimelineTicksPerSecond,
            (replayTimelineScrollTicks + (width - label) / pixels) / ReplayTimelineTicksPerSecond, width - label);
        for (int row = 0; row < visible; row++)
        {
            int index = replayTimelineFirstVisibleTrack + row;
            double y = ReplayTimelineHeaderHeight + rows.Top(index) - rows.Top(replayTimelineFirstVisibleTrack);
            double rowHeight = rows.Height(index);
            if (y >= height) break;
            if (index > tracks.Count) break;
            if (index == tracks.Count)
            {
                rmlTimeline!.Button("replayAddElement", 8, y + 1, 88, Lang.Get("vsdirector:replay-element-add"), "", () => OpenReplayElementMenu(), owner.IsReplayPlaying);
                var options = new List<(string, string)> {
                    ("scene-command-new", Lang.Get("vsdirector:replay-scene-new")),
                    ("scene-command-rename", Lang.Get("vsdirector:replay-scene-rename")),
                    ("scene-command-delete", Lang.Get("vsdirector:replay-scene-delete")) };
                options.AddRange(timeline.Scenes.Select(s => (s.Id.ToString("D"), s.Name)));
                rmlTimeline.Select("replaySceneSelector", 102, y + 1, Math.Max(34, label - 110), timeline.ActiveSceneId.ToString("D"), options,
                    value => OnReplaySceneMenuChanged(value, true));
                break;
            }
            var track = tracks[index]; string id = track.Id.ToString("N");
            RmlTimelineRow(id, index, y, width, label, rowHeight, track.Name.Length == 0 ? ReplayTimelineElementTrackName(ReplayTimelineElementKindFor(track.TrackType)) : track.Name,
                TimelineTrackIcon(track.TrackType.ToString()), IsReplayTimelineElementTrackSelected(track), track.Enabled,
                () => OpenReplayTrackEditor(track.Id), () => ToggleReplayElementTrack(track.Id), () => AddReplayKeyframeForTrack(track.Id));
            if (track.TrackType == DirectorReplayElementTrackType.Camera)
            {
                foreach (var clip in timeline.Clips.Where(c => c.TrackId == track.Id && c.SourceType == DirectorReplayViewportSourceType.StaticCamera))
                    RmlTimelineKey(clip.Id.ToString("N"), clip.TimelineStartSeconds, y, label, width, replayTimelineScrollTicks, pixels, clip.Id == selectedReplayViewportClipId, rowHeight: rowHeight);
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
                    rmlTimeline!.Shape("interval-" + pairId, "timeline-interval" + (selected ? " selected" : ""), left, y + rowHeight / 2 - 12, right - left, 24);
                    double seconds = pair.Start.TimelapseDurationSeconds > 0 ? pair.Start.TimelapseDurationSeconds
                        : pair.End.TimelapseDurationSeconds > 0 ? pair.End.TimelapseDurationSeconds : track.TimelapseDurationSeconds;
                    if (seconds <= 0) seconds = pair.End.TimelapseTick > pair.Start.TimelapseTick
                        ? (pair.End.TimelapseTick - pair.Start.TimelapseTick) / ReplayTimelineTicksPerSecond : Math.Max(0, pair.End.TimeSeconds - pair.Start.TimeSeconds);
                    if (right - left >= 64)
                        rmlTimeline.Shape("interval-label-" + pairId, "timeline-interval-label", left + 10, y + rowHeight / 2 - 10, right - left - 20, 20,
                            seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "s");
                }
            foreach (var frame in timeline.ElementKeyframes.Where(f => f.TrackId == track.Id))
            {
                if (track.TrackType == DirectorReplayElementTrackType.Timelapse)
                {
                    double edgeX = X(Time(frame));
                    if (edgeX >= label && edgeX <= width)
                        rmlTimeline!.Shape("interval-edge-" + frame.Id.ToString("N"), "timeline-interval-edge", edgeX - 1.5, y + rowHeight / 2 - 12, 3, 24);
                    continue;
                }
                string role = track.TrackType == DirectorReplayElementTrackType.Timelapse ? frame.TimelapseRole.ToString().ToLowerInvariant()
                    : track.TrackType == DirectorReplayElementTrackType.FollowEntity ? frame.FollowEntityRole.ToString().ToLowerInvariant() : "";
                RmlTimelineKey(frame.Id.ToString("N"), Time(frame), y, label, width, replayTimelineScrollTicks, pixels, frame.Id == selectedReplayElementKeyframeId, role,
                    TimelineKeyIcon(frame.TrackType.ToString(), frame.Frozen, frame.TimelapseRole == DirectorReplayTimelapseKeyframeRole.End), rowHeight);
            }
            if (track.TrackType == DirectorReplayElementTrackType.VoiceAudio)
                DrawVoiceWaveform(timeline, track, y, rowHeight, label, width);
        }
        if (replayElementTrackReordering || resizingTimelineTrack != Guid.Empty)
            RmlTimelineReorder(replayElementTrackReordering ? replayElementTrackReorderIndex : replayTimelineFirstVisibleTrack,
                replayTimelineFirstVisibleTrack, visible, width);
    }

    private static string TimelineTrackIcon(string type) => type switch
    {
        "Camera" or "CameraPath" => "camera", "Marker" => "bookmark", "Speed" => "gauge", "CameraOrbit" => "rotate-360",
        "TrackEntity" or "LookAt" => "focus-2", "FollowEntity" => "walk", "CameraShake" => "camera-rotate", "Fov" => "eye",
        "Timelapse" => "hourglass", "TimeOfDay" => "clock", "Freeze" => "snowflake", "BlockOverride" => "cube",
        "VoiceAudio" or "Audio" => "volume", "Roll" => "rotate", _ => "menu-2"
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
