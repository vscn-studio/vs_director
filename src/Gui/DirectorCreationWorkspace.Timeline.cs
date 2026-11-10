using static VSDirector.Gui.DirectorCreationText;
using System.Numerics;
using ImGuiNET;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private double timelineZoom = 1, timelineStart;
    private bool timelineEditor;
    private Guid contextKeyId;
    private readonly Dictionary<Guid, bool> timelineObjectExpanded = new();
    private void ZoomTimeline(double factor, double anchor)
    {
        double duration = Math.Max(.001, Session.EditDuration), before = duration / timelineZoom;
        timelineZoom = Math.Clamp(timelineZoom * factor, .05, 256);
        timelineStart = Math.Clamp(timelineStart + (before - duration / timelineZoom) * Math.Clamp(anchor, 0, 1), 0, Math.Max(0, duration - duration / timelineZoom));
    }
    private Guid draggedKey, draggedClip;
    private int clipEdge, curveHandle;
    private double dragTime, dragStart, dragDuration, dragSource;
    private static string ChannelName(CreationChannel channel) => channel switch
    { CreationChannel.Position => L("creation-position"), CreationChannel.Rotation => L("creation-rotation"), CreationChannel.Scale => L("creation-scale"), CreationChannel.Pivot => L("creation-pivot"), CreationChannel.Color => L("creation-color"), CreationChannel.Visible => L("creation-visibility"), CreationChannel.IkWeight => L("creation-ik-weight"), _ => L("creation-field-of-view") };
    private double Snap(double t) => ImGui.GetIO().KeyAlt ? Math.Max(0, t) : Math.Max(0, Math.Round(t * Project!.Output.Fps) / Project.Output.Fps);
    private void DrawTimeline()
    {
        if (Project is not { } p) return;
        if (Runtime.PerformanceActive)
        {
            ImGui.TextWrapped(Runtime.Status);
            ImGui.TextUnformatted(Session.Time.ToString("0.00") + " s");
            if (DirectorImGuiIcons.Button(DirectorImGuiIcons.Get("stop") + "##performance-stop", new(28 * DirectorImGuiTheme.Scale))) Run(() => Runtime.StopPerformance());
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(L("creation-stop-recording"));
            return;
        }
        if (Runtime.Exporting)
        {
            ImGui.ProgressBar((float)Runtime.ExportProgress);
            if (Button(L("creation-cancel-export"))) Runtime.CancelExport();
            ImGui.TextWrapped(Runtime.Status);
            return;
        }
        float scale = DirectorImGuiTheme.Scale;
        Vector2 header = ImGui.GetCursorScreenPos();
        float totalWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
        int transportCount = 9 + (Runtime.PerformanceActive ? 1 : 0) + (SelectedAnimation is not null ? 1 : 0);
        float transportWidth = (transportCount * 30 - 2) * scale;
        bool compact = totalWidth < transportWidth + 100 * scale;
        float labelWidth = compact ? totalWidth * .44f : Math.Max(transportWidth + 8 * scale, Math.Min(300 * scale, totalWidth * .35f));
        float width = Math.Max(1, totalWidth - labelWidth);
        int transportColumns = Math.Max(1, (int)((totalWidth + 2 * scale) / (30 * scale)));
        float transportHeight = compact ? (float)Math.Ceiling(transportCount / (double)transportColumns) * 32 * scale : 36 * scale;
        float rowHeight = 40 * scale, headerHeight = (compact ? transportHeight : 0) + 36 * scale;
        float rulerY = header.Y + (compact ? transportHeight : 0);
        double duration = Math.Max(.001, Session.EditDuration);
        var io = ImGui.GetIO();
        bool ctrl = io.KeyCtrl || ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl);
        bool shift = io.KeyShift || ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
        bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows) && ImGui.IsMouseHoveringRect(header, header + ImGui.GetContentRegionAvail());
        if (hovered && ctrl && io.MouseWheel != 0) ZoomTimeline(Math.Pow(1.2, io.MouseWheel), (io.MousePos.X - header.X - labelWidth) / width);
        double span = duration / timelineZoom;
        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Middle)) timelineStart -= io.MouseDelta.X / width * span;
        if (hovered && !ctrl && (io.MouseWheelH != 0 || shift || io.MousePos.Y < header.Y + headerHeight))
            timelineStart -= (io.MouseWheelH != 0 ? io.MouseWheelH : io.MouseWheel) * span * .08;
        timelineStart = Math.Clamp(timelineStart, 0, Math.Max(0, duration - span));
        float x0 = header.X + labelWidth;
        float X(double time) => x0 + (float)((time - timelineStart) / span) * width;
        double T(float x) => Math.Clamp(Snap(timelineStart + (x - x0) / width * span), 0, duration);
        if (ImGui.BeginChild("creation-transport", new(compact ? totalWidth : labelWidth, transportHeight), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) DrawTimelineTransport();
        ImGui.EndChild();
        ImGui.SetCursorScreenPos(new(x0, rulerY));
        ImGui.InvisibleButton("ruler", new(width, 36 * scale));
        if (ImGui.IsItemActive()) SeekEditTime(T(io.MousePos.X));
        var headerDraw = ImGui.GetWindowDrawList();
        headerDraw.AddRectFilled(new(x0, rulerY), new(x0 + width, header.Y + headerHeight), DirectorImGuiTheme.Panel);
        headerDraw.PushClipRect(new(x0, rulerY), new(x0 + width, header.Y + headerHeight), true);
        double desiredStep = span * (80 * scale) / width;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1d / p.Output.Fps, desiredStep))));
        double step = new[] { 1d, 2, 5, 10 }.Select(v => v * magnitude).First(v => v >= desiredStep);
        step = Math.Max(1d / p.Output.Fps, step);
        for (double t = Math.Floor(timelineStart / step) * step; t <= timelineStart + span; t += step / 5)
        {
            float x = X(t); bool major = Math.Abs(t / step - Math.Round(t / step)) < .001;
            headerDraw.AddLine(new(x, header.Y + headerHeight - (major ? 12 : 5)), new(x, header.Y + headerHeight), DirectorImGuiTheme.Muted);
            if (major) headerDraw.AddText(new(x + 3, rulerY + 1), DirectorImGuiTheme.Muted, t.ToString(step < .1 ? "0.###" : "0.##"));
        }
        float head = X(EditTime);
        headerDraw.AddTriangleFilled(new(head - 5 * scale, rulerY), new(head + 5 * scale, rulerY), new(head, rulerY + 8 * scale), DirectorImGuiTheme.Accent);
        headerDraw.AddLine(new(head, rulerY), new(head, header.Y + headerHeight), DirectorImGuiTheme.Accent, 2 * scale);
        headerDraw.PopClipRect();
        ImGui.SetCursorScreenPos(new(header.X, header.Y + headerHeight));
        float rowsHeight = Math.Max(1, ImGui.GetContentRegionAvail().Y);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0));
        if (ImGui.BeginChild("timeline-rows", new(0, rowsHeight), false, ctrl || shift ? ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None))
        {
            Vector2 origin = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            float gridBottom = ImGui.GetWindowPos().Y + ImGui.GetWindowHeight();
            for (double t = Math.Ceiling(timelineStart / step) * step; t <= timelineStart + span; t += step)
                draw.AddLine(new(X(t), ImGui.GetWindowPos().Y), new(X(t), gridBottom), 0x224f4f4f);
            draw.AddLine(new(x0, ImGui.GetWindowPos().Y), new(x0, gridBottom), DirectorImGuiTheme.Border);
            var tracks = EditTracks.OrderBy(t => t.PartId).ThenBy(t => t.Channel).ToArray();
            void DrawTrack(CreationTrack track)
            {
                ImGui.PushID(track.Id.ToString());
                Vector2 row = ImGui.GetCursorScreenPos();
                ImGui.SetNextItemAllowOverlap();
                ImGui.InvisibleButton("track", new(Math.Max(1, ImGui.GetContentRegionAvail().X), rowHeight));
                string label = p.Objects.Find(o => o.Id == track.ObjectId)?.Name ?? SelectedObject?.Name ?? L("creation-objects");
                if (track.PartId != Guid.Empty) label = p.Models.SelectMany(m => m.Parts).FirstOrDefault(b => b.Id == track.PartId)?.Name ?? L("creation-bone");
                draw.AddRectFilled(row, row + new Vector2(labelWidth + width, rowHeight), track.Id == trackId ? DirectorImGuiTheme.Selected : 0x22444444u);
                draw.AddLine(row + new Vector2(0, rowHeight), row + new Vector2(labelWidth + width, rowHeight), DirectorImGuiTheme.Border);
                float actionsLeft = Math.Max(row.X, x0 - 60 * scale);
                draw.PushClipRect(row, new(actionsLeft, row.Y + rowHeight), true);
                draw.AddText(row + new Vector2(5 * scale, (rowHeight - ImGui.GetFontSize()) / 2), 0xffdddddd, label + " · " + ChannelName(track.Channel)); draw.PopClipRect();
                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && (io.MousePos.X < actionsLeft || io.MousePos.X >= x0))
                {
                    float mouseX = ImGui.GetIO().MousePos.X; double time = T(mouseX);
                    var key = track.Keys.MinBy(k => Math.Abs(X(k.Time) - mouseX));
                    bool hit = mouseX >= x0 && key is not null && Math.Abs(X(key.Time) - mouseX) <= 10 * DirectorImGuiTheme.Scale;
                    bool canDrag = !Session.HasTransformDraft;
                    Continue(() =>
                    {
                        trackId = track.Id; keyId = clipId = Guid.Empty;
                        if (track.ObjectId != Guid.Empty) Session.SelectedObject = track.ObjectId;
                        Session.SelectedPart = track.PartId;
                        if (hit) { keyId = key!.Id; if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) timelineEditor = true; else if (canDrag) { draggedKey = key.Id; Session.BeginGesture(); } }
                        else if (mouseX >= x0) Runtime.Seek(time);
                    });
                }
                draw.PushClipRect(new(x0, row.Y), new(x0 + width, row.Y + rowHeight), true);
                foreach (var key in track.Keys.ToArray())
                {
                    Vector2 c = new(X(key.Time), row.Y + rowHeight / 2);
                    string glyph = DirectorImGuiIcons.Get("square-rotated-filled");
                    float glyphSize = 24 * DirectorImGuiTheme.Scale;
                    Vector2 glyphBounds = ImGui.CalcTextSize(glyph) * (glyphSize / ImGui.GetFontSize());
                    draw.AddText(ImGui.GetFont(), glyphSize, c - glyphBounds * .5f, key.Id == keyId ? DirectorImGuiTheme.Accent : 0xffe6e0de, glyph);
                    if (draggedKey == key.Id && ImGui.IsMouseDown(ImGuiMouseButton.Left)) { key.Time = T(ImGui.GetIO().MousePos.X); Session.Changed(); }
                }
                draw.PopClipRect();
                if (ImGui.BeginPopupContextItem("track-menu"))
                {
                    if (ImGui.IsWindowAppearing())
                    {
                        var nearest = track.Keys.MinBy(k => Math.Abs(X(k.Time) - ImGui.GetIO().MousePos.X));
                        contextKeyId = nearest is not null && ImGui.GetIO().MousePos.X >= x0 && Math.Abs(X(nearest.Time) - ImGui.GetIO().MousePos.X) < 14 * scale ? nearest.Id : Guid.Empty;
                    }
                    var contextKey = track.Keys.Find(k => k.Id == contextKeyId);
                    if (contextKey is not null)
                    {
                        Item(L("creation-edit-keyframe"), () => { trackId = track.Id; keyId = contextKey.Id; clipId = Guid.Empty; timelineEditor = true; });
                        Item(L("creation-delete-keyframe"), () => Session.Edit(L("creation-delete-keyframe"), _ => track.Keys.Remove(contextKey)));
                        ImGui.Separator();
                    }
                    Item(L("creation-enable-disable-track"), () => Session.Edit(L("creation-enable-disable-track"), _ => track.Enabled = !track.Enabled));
                    Item(L("creation-delete-track"), () => Session.Edit(L("creation-delete-track"), _ => EditTracks.Remove(track))); ImGui.EndPopup();
                }
                Vector2 afterRow = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(new(actionsLeft, row.Y + (rowHeight - 28 * scale) / 2));
                using (DirectorImGuiTheme.Button(quiet: true))
                {
                    if (DirectorImGuiIcons.Button(DirectorImGuiIcons.Get(track.Enabled ? "eye" : "eye-off") + "##enabled", new(28 * scale)))
                        Continue(() => Session.Edit(L("creation-enable-disable-track"), _ => track.Enabled = !track.Enabled));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(L("creation-enable-disable-track"));
                    ImGui.SameLine(0, 2 * scale);
                    if (DirectorImGuiIcons.Button(DirectorImGuiIcons.Get("plus") + "##add-key", new(28 * scale)))
                        Continue(() => Session.Edit(L("creation-add-transform-key"), _ =>
                        {
                            var value = DirectorCreationEvaluator.Sample(track, EditTime, default);
                            var key = DirectorCreationEdits.SetKey(track, EditTime, value);
                            trackId = track.Id; keyId = key.Id; clipId = Guid.Empty; timelineEditor = true;
                        }));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(L("creation-add-transform-key"));
                }
                ImGui.SetCursorScreenPos(afterRow);
                ImGui.PopID();
            }
            void DrawLane(Guid actor, int lane)
            {
                    Vector2 row = ImGui.GetCursorScreenPos();
                    ImGui.InvisibleButton("lane" + actor + lane, new(Math.Max(1, ImGui.GetContentRegionAvail().X), rowHeight), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
                    bool laneHovered = ImGui.IsItemHovered();
                    draw.AddText(row + new Vector2(5 * scale, (rowHeight - ImGui.GetFontSize()) / 2), 0xffbbbbbb, L("creation-clip") + (lane + 1));
                    draw.PushClipRect(new(x0, row.Y), new(x0 + width, row.Y + rowHeight), true);
                    foreach (var clip in p.Clips.Where(c => c.Lane == lane && c.ObjectId == actor).ToArray())
                    {
                        Vector2 min = new(X(clip.Start), row.Y + 2), max = new(X(clip.Start + clip.Duration), row.Y + rowHeight - 2);
                        uint color = clip.AudioAssetId == Guid.Empty ? 0xff91644a : 0xff5b8b4d;
                        draw.AddRectFilled(min, max, clip.Enabled ? color : 0xff555555, 3);
                        if (clip.Id == clipId) draw.AddRect(min, max, 0xffffffff, 3);
                        draw.PushClipRect(min, max, true); draw.AddText(min + new Vector2(4, 3), 0xffffffff, clip.Name); draw.PopClipRect();
                        if (laneHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.GetIO().MousePos.X >= x0 && ImGui.IsMouseHoveringRect(min, max))
                        {
                            bool canDrag = !Session.HasTransformDraft;
                            float mouseX = ImGui.GetIO().MousePos.X; double time = T(mouseX);
                            Continue(() =>
                            {
                                clipId = clip.Id; keyId = trackId = Guid.Empty;
                                if (clip.ObjectId != Guid.Empty) Session.SelectedObject = clip.ObjectId;
                                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { timelineEditor = true; return; }
                                if (!canDrag) return;
                                draggedClip = clip.Id;
                                dragTime = time; dragStart = clip.Start; dragDuration = clip.Duration; dragSource = clip.SourceStart;
                                clipEdge = Math.Abs(mouseX - min.X) < 10 * scale ? -1 : Math.Abs(mouseX - max.X) < 10 * scale ? 1 : 0;
                                Session.BeginClipGesture(clip);
                            });
                        }
                        if (laneHovered && ImGui.IsMouseReleased(ImGuiMouseButton.Right) && ImGui.GetIO().MousePos.X >= x0 && ImGui.IsMouseHoveringRect(min, max))
                            Continue(() => { clipId = clip.Id; keyId = trackId = Guid.Empty; timelineEditor = true; });
                        if (draggedClip == clip.Id && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                        {
                            var before = (clip.Start, clip.Duration, clip.SourceStart);
                            double offset = Snap(timelineStart + (io.MousePos.X - x0) / width * span) - dragTime, frame = 1d / p.Output.Fps;
                            if (clipEdge == 0) clip.Start = Math.Clamp(Snap(dragStart + offset), 0, Math.Max(0, 86400 - clip.Duration));
                            else if (clipEdge > 0) clip.Duration = Math.Clamp(Snap(dragDuration + offset), frame, Math.Max(frame, 86400 - clip.Start));
                            else
                            {
                                double movement = Math.Clamp(offset, Math.Max(-dragStart, -dragSource / clip.Speed), dragDuration - frame);
                                clip.Start = dragStart + movement; clip.Duration = dragDuration - movement; clip.SourceStart = dragSource + movement * clip.Speed;
                            }
                            if (before != (clip.Start, clip.Duration, clip.SourceStart)) Session.Changed();
                        }
                    }
                draw.PopClipRect();
            }
            if (SelectedAnimation is null)
            {
                foreach (var obj in p.Objects.ToArray())
                {
                    ImGui.PushID(obj.Id.ToString());
                    Vector2 row = ImGui.GetCursorScreenPos();
                    ImGui.InvisibleButton("object-row", new(Math.Max(1, ImGui.GetContentRegionAvail().X), rowHeight));
                    bool expanded = timelineObjectExpanded.GetValueOrDefault(obj.Id, obj.Id == Session.SelectedObject);
                    if (ImGui.IsItemClicked())
                    {
                        if (io.MousePos.X < row.X + 28 * scale) timelineObjectExpanded[obj.Id] = expanded = !expanded;
                        else Select(obj);
                    }
                    draw.AddRectFilled(row, row + new Vector2(totalWidth, rowHeight), obj.Id == Session.SelectedObject ? DirectorImGuiTheme.Selected : DirectorImGuiTheme.Panel);
                    draw.AddLine(row + new Vector2(0, rowHeight), row + new Vector2(totalWidth, rowHeight), DirectorImGuiTheme.Border);
                    draw.PushClipRect(row, new(x0, row.Y + rowHeight), true);
                    float textY = row.Y + (rowHeight - ImGui.GetFontSize()) / 2;
                    draw.AddText(new(row.X + 4 * scale, textY), DirectorImGuiTheme.Text, DirectorImGuiIcons.Get(expanded ? "chevron-down" : "chevron-right"));
                    string rowName = obj.Name;
                    while (rowName.Length > 1 && ImGui.CalcTextSize(rowName).X > labelWidth - 38 * scale) rowName = rowName[..^1];
                    if (rowName != obj.Name) rowName += "…";
                    draw.AddText(new(row.X + 30 * scale, textY), DirectorImGuiTheme.Text, rowName);
                    draw.PopClipRect();
                    if (ImGui.BeginPopupContextItem("timeline-object")) { DrawObjectContext(obj); ImGui.EndPopup(); }
                    if (expanded)
                    {
                        foreach (var track in tracks.Where(t => t.ObjectId == obj.Id)) DrawTrack(track);
                        foreach (int lane in p.Clips.Where(c => c.ObjectId == obj.Id).Select(c => c.Lane).Distinct().OrderBy(v => v)) DrawLane(obj.Id, lane);
                    }
                    ImGui.PopID();
                }
                if (p.Clips.Any(c => c.AudioAssetId != Guid.Empty))
                {
                    ImGui.TextUnformatted(L("creation-audio-tracks"));
                    foreach (int lane in p.Clips.Where(c => c.ObjectId == Guid.Empty).Select(c => c.Lane).Distinct().OrderBy(v => v)) DrawLane(Guid.Empty, lane);
                }
            }
            else foreach (var track in tracks) DrawTrack(track);
            float playhead = X(EditTime);
            draw.PushClipRect(new(x0, ImGui.GetWindowPos().Y), new(x0 + width, gridBottom), true);
            draw.AddLine(new(playhead, ImGui.GetWindowPos().Y), new(playhead, gridBottom), DirectorImGuiTheme.Accent, 2 * scale);
            draw.PopClipRect();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && (draggedKey != Guid.Empty || draggedClip != Guid.Empty))
        { Session.EndGesture(L("creation-move-keyframe-clip")); draggedKey = draggedClip = Guid.Empty; }

        DrawTimelineEditor();
    }
    private void DrawTimelineEditor()
    {
        if (!timelineEditor) return;
        if (!EditTracks.Any(t => t.Keys.Any(k => k.Id == keyId)) && !Project!.Clips.Any(c => c.Id == clipId)) { timelineEditor = false; return; }
        Vector2 display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowSize(new(Math.Min(480 * DirectorImGuiTheme.Scale, display.X - 20), Math.Min(420 * DirectorImGuiTheme.Scale, display.Y - 40)), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(display * .5f, ImGuiCond.Appearing, new(.5f));
        ImGui.SetNextWindowSizeConstraints(new(Math.Min(260, display.X - 20), Math.Min(160, display.Y - 40)), display - new Vector2(20, 40));
        ImGui.SetNextWindowBgAlpha(1);
        bool visible = ImGui.Begin((keyId != Guid.Empty ? L("creation-edit-keyframe") : L("creation-edit-clip")) + "###creation-timeline-editor", ref timelineEditor, ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse);
        ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);
        try { if (visible) DrawTimelineSelection(); } finally { ImGui.End(); }
    }
    private void DrawTimelineTransport()
    {
        var p = Project!;
        float scale = DirectorImGuiTheme.Scale, side = 28 * scale, gap = 2 * scale;
        float available = ImGui.GetContentRegionAvail().X;
        var commands = new List<(string Id, string Glyph, string Tip, Action Run, bool Active)>();
        void Icon(string id, string glyph, string tooltip, Action action, bool active = false) => commands.Add((id, glyph, tooltip, action, active));
        Icon("start", "track-prev", L("creation-go-start"), () => SeekEditTime(SelectedAnimation is null && p.Loop ? p.LoopStart : 0));
        Icon("previous-frame", "skip-back", L("creation-previous-frame"), () => SeekEditTime(EditTime - 1d / p.Output.Fps));
        Icon("play", Session.Playing ? "pause" : "play", Session.Playing ? L("creation-pause") : L("creation-play"), () => Continue(() => Session.Play(!Session.Playing)), true);
        Icon("next-frame", "skip-forward", L("creation-next-frame"), () => SeekEditTime(EditTime + 1d / p.Output.Fps));
        Icon("end", "track-next", L("creation-go-end"), () => SeekEditTime(Session.EditDuration));
        Icon("stop", "stop", L("creation-stop"), () => Continue(() => { Session.Play(false); Runtime.Seek(SelectedAnimation is null && p.Loop ? p.LoopStart : 0); }));
        bool loop = SelectedAnimation?.Loop ?? p.Loop;
        Icon("loop", "refresh", L("creation-loop"), () => Session.Edit(L("creation-loop-playback"), _ => { if (SelectedAnimation is { } action) action.Loop = !loop; else p.Loop = !loop; }), loop);
        Icon("auto-key", "record", L("creation-auto-key"), () => Continue(() => Session.AutoKey = !Session.AutoKey), Session.AutoKey);
        Icon("preview", "camera", L("creation-preview"), () => Runtime.Preview = !Runtime.Preview, Runtime.Preview);
        if (Runtime.PerformanceActive) Icon("record-stop", "stop", L("creation-stop-recording"), () => Runtime.StopPerformance(), true);
        if (SelectedAnimation is not null) Icon("back-project", "arrow-back-up", L("creation-back-project"), ReturnToProject);
        float right = ImGui.GetCursorScreenPos().X + available;
        for (int i = 0; i < commands.Count; i++)
        {
            if (i > 0 && ImGui.GetItemRectMax().X + gap + side <= right) ImGui.SameLine(0, gap);
            var command = commands[i];
            using var tone = DirectorImGuiTheme.Button(command.Active ? "active" : "normal", quiet: true);
            if (DirectorImGuiIcons.Button(DirectorImGuiIcons.Get(command.Glyph) + "##" + command.Id, new(side))) command.Run();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(command.Tip);
        }
    }
    private void SeekEditTime(double time)
    {
        Continue(() => Runtime.Seek(time));
    }
    private void DrawTimelineSelection()
    {
        var p = Project!;
        var clip = p.Clips.Find(c => c.Id == clipId);
        var track = EditTracks.Find(t => t.Id == trackId);
        var key = track?.Keys.Find(k => k.Id == keyId);
        if (ImGui.BeginChild("timeline-selection", new(0, 0), false))
        {
            if (clip is not null)
            {
                Toggle(L("creation-visible"), clip.Enabled, v => clip.Enabled = v);
                if (clip.PerformanceAssetId != Guid.Empty)
                {
                    if (Button(L("creation-performance-bake"))) Run(() => Runtime.BakePerformance(clip));
                    if (Button(L("creation-performance-background"))) Run(() => Runtime.UsePerformanceBackground(clip));
                }
                if (Button(L("creation-duplicate-clip"))) Session.Edit(L("creation-duplicate-clip"), _ => { var c = CreationJson.Clone(clip); c.Id = Guid.NewGuid(); c.Start = Math.Min(p.Duration - 1d / p.Output.Fps, clip.Start + clip.Duration); c.Duration = Math.Min(c.Duration, p.Duration - c.Start); p.Clips.Add(c); clipId = c.Id; });
                SameLineIfFits(L("creation-split-at-playhead")); if (Button(L("creation-split-at-playhead")) && Session.Time > clip.Start && Session.Time < clip.Start + clip.Duration) Session.Edit(L("creation-split-clip"), _ => clipId = DirectorCreationEdits.Split(p, clip, Session.Time).Id);
                SameLineIfFits(L("creation-delete-clip")); if (Button(L("creation-delete-clip"))) Session.Edit(L("creation-delete-clip"), _ => p.Clips.Remove(clip));
                if (ImGui.BeginTable("clip-fields", Math.Clamp((int)(ImGui.GetContentRegionAvail().X / 210), 1, 3), ImGuiTableFlags.SizingStretchSame))
                {
                    void Field(string title, double value, Action<double> set, double min, double max, float step = .1f) { ImGui.TableNextColumn(); Number(title, value, set, min, max, step); }
                    Field(L("creation-start"), clip.Start, v => clip.Start = v, 0, p.Duration);
                    Field(L("creation-duration"), clip.Duration, v => clip.Duration = v, 1d / p.Output.Fps, p.Duration);
                    Field(L("creation-source-offset"), clip.SourceStart, v => clip.SourceStart = v, 0, 86400);
                    Field(L("creation-speed"), clip.Speed, v => clip.Speed = v, .01, 16);
                    Field(L("creation-lane"), clip.Lane, v => clip.Lane = (int)v, 0, 99, 1);
                    Field(L("creation-fade-in"), clip.FadeIn, v => clip.FadeIn = v, 0, clip.Duration);
                    Field(L("creation-fade-out"), clip.FadeOut, v => clip.FadeOut = v, 0, clip.Duration);
                    if (clip.AudioAssetId != Guid.Empty) Field(L("creation-volume-db"), clip.Gain, v => clip.Gain = v, -60, 12, 1);
                    ImGui.TableNextColumn(); Toggle(L("creation-loop-clip"), clip.Loop, v => clip.Loop = v);
                    ImGui.EndTable();
                }
            }
            else if (key is not null && track is not null)
            {
                if (Button(L("creation-delete-keyframe"))) Session.Edit(L("creation-delete-keyframe"), _ => { track.Keys.Remove(key); keyId = Guid.Empty; });
                SameLineIfFits(L("creation-copy-to-playhead")); if (Button(L("creation-copy-to-playhead"))) Session.Edit(L("creation-duplicate-keyframe"), _ => { var k = CreationJson.Clone(key); k.Id = Guid.NewGuid(); k.Time = EditTime; track.Keys.RemoveAll(old => Math.Abs(old.Time - k.Time) < 1e-6); track.Keys.Add(k); keyId = k.Id; });
                if (ImGui.BeginTable("key-fields", ImGui.GetContentRegionAvail().X < 460 ? 1 : 2, ImGuiTableFlags.SizingStretchSame))
                {
                    ImGui.TableNextColumn(); Number(L("creation-time"), key.Time, v => key.Time = Snap(v), 0, SelectedAnimation?.Duration ?? p.Duration);
                    VectorField(L("creation-keyframe-value"), key.Value, v => key.Value = v, track.Channel == CreationChannel.Rotation ? 1 : .1f);
                    Choice(L("creation-interpolation"), key.Interpolation, v => key.Interpolation = v, [L("creation-hold"), L("creation-linear"), L("creation-ease-in"), L("creation-ease-out"), L("creation-smooth"), L("creation-bezier")]);
                    ImGui.TableNextColumn(); if (Header(L("creation-curve-editor"))) DrawCurve(key); ImGui.EndTable();
                }
            }
            else if (SelectedAnimation is { } a)
            {
                TextField(L("creation-animation-name"), a.Name, v => a.Name = v); Number(L("creation-animation-duration"), a.Duration, v => a.Duration = v, .034, 86400); Toggle(L("creation-loop-animation"), a.Loop, v => a.Loop = v);
            }
            else
            {
                if (ImGui.BeginTable("loop-fields", ImGui.GetContentRegionAvail().X < 460 ? 1 : 2, ImGuiTableFlags.SizingStretchSame))
                { ImGui.TableNextColumn(); Number(L("creation-loop-start"), p.LoopStart, v => p.LoopStart = v, 0, Math.Max(0, Math.Min(p.LoopEnd - .001, p.Duration - .001))); ImGui.TableNextColumn(); Number(L("creation-loop-end"), p.LoopEnd, v => p.LoopEnd = v, p.LoopStart + .001, p.Duration); ImGui.EndTable(); }
            }
        }
        ImGui.EndChild();
    }
    private void DrawCurve(CreationKeyframe key)
    {
        Vector2 min = ImGui.GetCursorScreenPos(), size = new(Math.Max(80, ImGui.GetContentRegionAvail().X), 85 * DirectorImGuiTheme.Scale);
        ImGui.InvisibleButton("curve", size); var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, 0xff242424);
        Vector2 P(double x, double y) => min + new Vector2((float)x * size.X, (1 - (float)y) * size.Y);
        Vector2 previous = P(0, 0);
        for (int i = 1; i <= 64; i++)
        {
            double x = i / 64d;
            double y = key.Interpolation switch { CreationInterpolation.Hold => x == 1 ? 1 : 0, CreationInterpolation.Smooth => x*x*(3-2*x), CreationInterpolation.EaseIn => x*x, CreationInterpolation.EaseOut => 1-(1-x)*(1-x), CreationInterpolation.Bezier => DirectorCreationEvaluator.BezierTime(key, x), _ => x };
            Vector2 point = P(x, y); draw.AddLine(previous, point, 0xff70c2ec, 2); previous = point;
        }
        if (key.Interpolation != CreationInterpolation.Bezier) return;
        Vector2 a = P(key.HandleOutTime, key.HandleOutValue), b = P(key.HandleInTime, key.HandleInValue);
        draw.AddLine(P(0,0), a, 0xff888888); draw.AddLine(P(1,1), b, 0xff888888);
        draw.AddCircleFilled(a, 5, 0xffeeeeee); draw.AddCircleFilled(b, 5, 0xffeeeeee);
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) { curveHandle = Vector2.Distance(ImGui.GetIO().MousePos, a) < Vector2.Distance(ImGui.GetIO().MousePos, b) ? 1 : 2; Session.BeginGesture(); }
        if (curveHandle != 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            double x = Math.Clamp((ImGui.GetIO().MousePos.X-min.X)/size.X,0,1), y = Math.Clamp(1-(ImGui.GetIO().MousePos.Y-min.Y)/size.Y,-2,3);
            if (curveHandle == 1) { key.HandleOutTime = Math.Min(x, key.HandleInTime); key.HandleOutValue = y; } else { key.HandleInTime = Math.Max(x, key.HandleOutTime); key.HandleInValue = y; } Session.Changed();
        }
        if (curveHandle != 0 && ImGui.IsMouseReleased(ImGuiMouseButton.Left)) { curveHandle = 0; Session.EndGesture(L("creation-edit-animation-curve")); }
    }
}
