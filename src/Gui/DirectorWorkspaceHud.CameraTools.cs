using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using VSDirector.Rendering;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private enum CameraTool { Pivot, Rotate, Move }
    private CameraTool cameraTool = CameraTool.Rotate;
    private Guid cameraToolSelection;
    private bool cameraContextRequested, consumeCameraRelease;
    private EnumMouseButton cameraReleaseButton;
    private readonly List<CameraToolLine> cameraToolLines = new();
    private readonly List<CameraToolTriangle> cameraToolTriangles = new();
    private readonly List<(Guid Id, Vector2 Screen)> cameraToolHeads = new();
    private readonly record struct CameraToolLine(Vector2 A, Vector2 B, int Axis, Guid CameraId);
    private readonly record struct CameraToolTriangle(Vector2 A, Vector2 B, Vector2 C, int Axis);
    private static readonly (Vector3 A, Vector3 B)[] CameraOutline = DirectorEditorGeometry.CameraOutline;
    private const uint CameraOutlineColor = 0xffffffff;
    private const int ScreenRotationAxis = 6;
    private CameraPreset? cameraDragOriginal, cameraDragPose;
    private DirectorReplayViewportTimeline? cameraDragTimeline;
    private Vector3 cameraDragAxis, cameraDragPreviousDirection, cameraDragStartDirection, cameraDragPlanePoint;
    private float cameraDragParameter, cameraDragAngle;
    private int cameraDragAxisIndex = -1;
    private int cameraHoverAxis = -1;
    private Guid cameraDragReplayId, cameraDragSceneId;
    private readonly CameraPreviewSink cameraPreviewSink = new();
    private bool cameraPreviewOpen;
    private bool cameraPreviewFocus;
    private string cameraPreviewError = "";
    private bool cameraPreviewFailed;
    private Guid cameraPreviewSource;

    private sealed class CameraPreviewSink : IDirectorOffscreenFrameSink
    {
        internal Guid ViewId;
        internal int Texture;
        public void OnFrame(in DirectorOffscreenFrame frame)
        {
            if (frame.ViewId == ViewId) Texture = frame.ColorTextureId;
        }
    }

    private bool CameraToolsAvailable => IsOpened() && section == DirectorWorkspaceSection.Replay
        && owner.IsReplayPlaying && owner.ReplayActiveCameraMode == DirectorReplayCameraMode.Free;

    private void DrawReplayCameraTools()
    {
        if (!IsOpened()) return;
        if (transformPose is not null)
        {
            bool invalid = section != DirectorWorkspaceSection.Replay || transformReplayId != owner.ActiveReplayId
                || transformSceneId != owner.ReplayViewportTimeline.ActiveSceneId;
            if (invalid || transformClipId != selectedReplayViewportClipId || !ImGui.IsAnyItemActive())
                FinishCameraTransform(cancel: invalid);
        }
        if (cameraDragOriginal is not null && (!CameraToolsAvailable || layout.NavigationMode
            || cameraDragReplayId != owner.ActiveReplayId || cameraDragSceneId != owner.ReplayViewportTimeline.ActiveSceneId))
            FinishCameraToolDrag(cancel: true);
        if (cameraDragOriginal is not null && !capi.Input.MouseButton.Left)
            FinishCameraToolDrag(cancel: false);
        if (cameraDragOriginal is null) cameraToolSelection = selectedReplayViewportClipId;
        Vector2 pointer = ImGui.GetMousePos();
        cameraHoverAxis = !layout.NavigationMode && PointInsideGameViewport((int)pointer.X, (int)pointer.Y)
            ? CameraHandleAxisAt(pointer, 7 * DirectorImGuiTheme.Scale) : -1;
        cameraToolLines.Clear();
        cameraToolTriangles.Clear();
        cameraToolHeads.Clear();
        if (CameraToolsAvailable)
        {
            DrawCameraModels();
            if (cameraContextRequested)
            {
                cameraContextRequested = false;
                ImGui.OpenPopup("vsdirector-camera-context");
            }
            if (ImGui.BeginPopup("vsdirector-camera-context"))
            {
                foreach (CameraTool mode in Enum.GetValues<CameraTool>())
                    if (ImGui.MenuItem(RmlLabel("camera-tool-" + mode.ToString().ToLowerInvariant()), "", cameraTool == mode))
                        cameraTool = mode;
                ImGui.Separator();
                if (ImGui.MenuItem(RmlLabel("camera-transform-title")))
                {
                    transformTabDismissed = false;
                    ComposeCameraTransform();
                    rmlTransformWindow?.FocusNextFrame();
                }
                if (ImGui.MenuItem(RmlLabel("camera-tool-preview"))) OpenReplayCameraPreview();
                if (ImGui.MenuItem(RmlLabel("camera-observation-title"))) OpenReplayCameraObservation();
                if (ImGui.MenuItem(RmlLabel("camera-tool-reset-pivot"))) ResetSelectedCameraPivot();
                ImGui.EndPopup();
            }
        }
        else if (cameraDragOriginal is not null) FinishCameraToolDrag(cancel: true);
        DrawReplayCameraPreview();
        DrawReplayCameraObservation();
    }

    private void DrawCameraModels()
    {
        var timeline = transformTimeline ?? cameraDragTimeline ?? owner.ReplayViewportTimeline;
        var enabled = timeline.Tracks.Where(t => t.Enabled && t.TrackType == DirectorReplayElementTrackType.Camera)
            .Select(t => t.Id).ToHashSet();
        // This callback runs inside the workspace host. Its draw list overlays
        // the world without depth testing, while normal windows/popups stay above it.
        var draw = ImGui.GetWindowDrawList();
        var panel = layout.Panel("preview");
        float scale = DirectorImGuiTheme.Scale;
        Vector2 min = new((float)panel.X * scale, (float)panel.Y * scale);
        draw.PushClipRect(min, min + new Vector2((float)panel.Width, (float)panel.Height) * scale, true);
        try
        {
            CameraPreset? selectedPose = null;
            foreach (var clip in timeline.Clips.Where(c => c.Enabled && enabled.Contains(c.TrackId)
                && c.SourceType == DirectorReplayViewportSourceType.StaticCamera))
            {
                CameraPreset? pose = clip.Id == transformClipId && transformPose is not null ? transformPose
                    : clip.Id == cameraToolSelection && cameraDragPose is not null ? cameraDragPose : ReplayCameraElementPose(clip);
                if (pose is null || pose.Dimension != capi.World.Player.Entity.Pos.Dimension) continue;
                bool selected = clip.Id == cameraToolSelection;
                DrawCameraHead(draw, clip.Id, pose, selected ? CameraOutlineColor : 0xff000000);
                if (selected) selectedPose = pose;
            }
            if (selectedPose is not null) DrawCameraHandles(draw, selectedPose);
        }
        finally { draw.PopClipRect(); }
    }

    private void DrawCameraHead(ImDrawListPtr draw, Guid id, CameraPreset pose, uint color)
    {
        var basis = CameraHeadBasis(pose);
        var origin = new Vec3d(pose.X, pose.Y + pose.Dimension * 32768d, pose.Z);
        // The outline is measured in world blocks, just like nearby geometry.
        Vec3d At(Vector3 point) => Add(origin, basis.Right * point.X + basis.Up * point.Y + basis.Back * point.Z);
        foreach (var edge in CameraOutline)
            CameraWorldLine(draw, At(edge.A), At(edge.B), color, id);
        if (ProjectCameraPoint(origin, out var screen))
        {
            cameraToolHeads.Add((id, screen));
            if (id == cameraToolSelection)
            {
                DrawCameraOriginAxes(draw, origin);
                DrawCameraCenter(draw, screen);
            }
        }
    }

    private void DrawCameraOriginAxes(ImDrawListPtr draw, Vec3d origin)
    {
        // These plain world axes mark the camera itself, even with an offset pivot.
        // They are visual guides only and must never intercept handle picking.
        float length = Math.Max(.35f, CameraWorldUnitsPerPixel(origin) * 24 * DirectorImGuiTheme.Scale);
        uint[] colors = [0xff6666ee, 0xff77cf75, 0xffedaa62];
        for (int axis = 0; axis < 3; axis++)
            CameraHandleLine(draw, origin, origin, Add(origin, CameraAxis(axis) * length), colors[axis], -1, 1.5f);
    }

    private static (Vector3 Right, Vector3 Up, Vector3 Back) CameraHeadBasis(CameraPreset pose)
        => DirectorCameraManipulation.CameraHeadBasis(pose);

    private DirectorEditorHandles? replayHandles;
    private void DrawCameraHandles(ImDrawListPtr draw, CameraPreset pose)
    {
        var pivot=CameraPivot(pose); if(!ProjectCameraPoint(pivot,out var center)) return;
        float radius=CameraGizmoRadius(pose);
        replayHandles ??= new();
        replayHandles.Draw(draw,center,CameraWorldUnitsPerPixel(pivot),radius,capi.Render.PerspectiveViewMat,
            [Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ],cameraTool==CameraTool.Rotate,cameraTool==CameraTool.Pivot,cameraDragAxisIndex,false,cameraHoverAxis);
        foreach(var line in replayHandles.Lines) cameraToolLines.Add(new(line.A,line.B,line.Axis,line.CameraId));
        foreach(var face in replayHandles.Triangles) cameraToolTriangles.Add(new(face.A,face.B,face.C,face.Axis));
        cameraToolHeads.Add((cameraToolSelection,center));
        if(cameraTool==CameraTool.Rotate)
        {
            if(cameraDragAxisIndex>=0) DrawCameraRotationArc(draw,pivot,cameraDragAxisIndex==ScreenRotationAxis?radius+CameraWorldUnitsPerPixel(pivot)*22*DirectorImGuiTheme.Scale:radius);
            DrawCameraAngleReadout(draw,pose);
        }
    }

    private static void DrawCameraCenter(ImDrawListPtr draw, Vector2 center, uint color = 0xff55d9ff)
    {
        float scale = DirectorImGuiTheme.Scale;
        draw.AddCircleFilled(center, 4 * scale, color);
    }

    private void DrawCameraMoveCone(ImDrawListPtr draw, Vec3d pivot, float length, int axis, uint color)
    {
        const int sides = 64;
        Vector3 direction = CameraAxis(axis), u = CameraAxis((axis + 1) % 3), v = CameraAxis((axis + 2) % 3);
        Vec3d tip = Add(pivot, direction * length), bottom = Add(pivot, direction * (length - CameraMoveConeLength(pivot, length)));
        float radius = Math.Min(length * .075f, CameraWorldUnitsPerPixel(pivot) * 4.5f * DirectorImGuiTheme.Scale);
        var faces = new List<(Vec3d A, Vec3d B, Vec3d C)>(sides * 2);
        for (int i = 0; i < sides; i++)
        {
            float a = i * MathF.Tau / sides, b = (i + 1) * MathF.Tau / sides;
            Vec3d start = Add(bottom, (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius);
            Vec3d end = Add(bottom, (u * MathF.Cos(b) + v * MathF.Sin(b)) * radius);
            faces.Add((start, end, tip));
            faces.Add((bottom, end, start));
        }
        // Internal triangle edges must not be feathered: separate AA fringes
        // leave transparent spokes through an otherwise closed cone.
        var flags = draw.Flags;
        draw.Flags &= ~ImDrawListFlags.AntiAliasedFill;
        try
        {
            // Opaque faces and the bottom cap share the same unlit axis colour.
            foreach (var face in faces)
            {
                if (!ProjectCameraHandlePoint(pivot, face.A, out var a) || !ProjectCameraHandlePoint(pivot, face.B, out var b)
                    || !ProjectCameraHandlePoint(pivot, face.C, out var c)) continue;
                if ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) < 0) (b, c) = (c, b);
                draw.AddTriangleFilled(a, b, c, color);
                cameraToolTriangles.Add(new(a, b, c, axis));
                cameraToolLines.Add(new(a, b, axis, Guid.Empty));
                cameraToolLines.Add(new(b, c, axis, Guid.Empty));
                cameraToolLines.Add(new(c, a, axis, Guid.Empty));
            }
        }
        finally { draw.Flags = flags; }
    }

    private float CameraMoveConeLength(Vec3d pivot, float length)
        => Math.Min(length * .18f, CameraWorldUnitsPerPixel(pivot) * 10.8f * DirectorImGuiTheme.Scale);

    private int CameraHandleAxisAt(Vector2 point, float threshold)
    {
        if (cameraToolHeads.Any(head => Vector2.DistanceSquared(head.Screen, point) <= 64 * DirectorImGuiTheme.Scale * DirectorImGuiTheme.Scale))
            return -1;
        foreach (var face in cameraToolTriangles.AsEnumerable().Reverse())
        {
            static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
            float area = Cross(face.B - face.A, face.C - face.A);
            if (Math.Abs(area) < .001f) continue;
            float a = Cross(face.B - point, face.C - point) / area;
            float b = Cross(face.C - point, face.A - point) / area;
            if (a >= 0 && b >= 0 && a + b <= 1) return face.Axis;
        }
        return cameraToolLines.Where(line => line.Axis >= 0)
            .Select(line => (Line: line, Distance: CameraLineDistance(point, line.A, line.B)))
            .Where(item => item.Distance < threshold).OrderBy(item => item.Distance)
            .Select(item => item.Line.Axis).DefaultIfEmpty(-1).First();
    }

    private void CameraWorldLine(ImDrawListPtr draw, Vec3d a, Vec3d b, uint color, Guid id, int axis = -1, float thickness = 1.5f)
    {
        if (!ProjectCameraPoint(a, out var start) || !ProjectCameraPoint(b, out var end)) return;
        draw.AddLine(start, end, color, thickness * DirectorImGuiTheme.Scale);
        if (id != Guid.Empty || axis >= 0) cameraToolLines.Add(new(start, end, axis, id));
    }

    private bool ProjectCameraPoint(Vec3d point, out Vector2 screen)
    {
        screen = default;
        // Ignore the world far plane for editor overlays, while still rejecting
        // points behind the eye. Use the render matrices rather than player eye
        // offsets so free cameras and large world coordinates project identically.
        var view = capi.Render.PerspectiveViewMat;
        var projection = capi.Render.PerspectiveProjectionMat;
        double vx = view[0] * point.X + view[4] * point.Y + view[8] * point.Z + view[12];
        double vy = view[1] * point.X + view[5] * point.Y + view[9] * point.Z + view[13];
        double vz = view[2] * point.X + view[6] * point.Y + view[10] * point.Z + view[14];
        double vw = view[3] * point.X + view[7] * point.Y + view[11] * point.Z + view[15];
        double clipW = projection[3] * vx + projection[7] * vy + projection[11] * vz + projection[15] * vw;
        if (!double.IsFinite(clipW) || clipW <= 0) return false;
        var projected = MatrixToolsd.Project(point, projection, view, capi.Render.FrameWidth, capi.Render.FrameHeight);
        if (!double.IsFinite(projected.X) || !double.IsFinite(projected.Y)) return false;
        if (!DirectorWorkspaceViewportRuntime.TryMapGamePointToPhysical(projected.X,
            capi.Render.FrameHeight - projected.Y, out double x, out double y)) return false;
        screen = new((float)x, (float)y);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    private float CameraWorldUnitsPerPixel(Vec3d point)
    {
        var view = capi.Render.PerspectiveViewMat;
        var projection = capi.Render.PerspectiveProjectionMat;
        double depth = view[2] * point.X + view[6] * point.Y + view[10] * point.Z + view[14];
        if (!DirectorWorkspaceViewportRuntime.TryMapGamePointToPhysical(0, 0, out _, out double top)
            || !DirectorWorkspaceViewportRuntime.TryMapGamePointToPhysical(0, capi.Render.FrameHeight, out _, out double bottom))
            return .01f;
        // Measure in the view plane. Probing world X/Y/Z also measures depth
        // changes, which shrinks off-centre controls as the editor approaches.
        double pixelsPerUnitAtUnitDepth = Math.Abs(projection[5] * (bottom - top)) / 2;
        double units = Math.Abs(depth) / pixelsPerUnitAtUnitDepth;
        return double.IsFinite(units) && units > 0 ? (float)units : .01f;
    }

    // Editor controls stay at a comfortable screen size independently of the model.
    private float CameraHandleRadius(Vec3d point)
        => CameraWorldUnitsPerPixel(point) * 60 * DirectorImGuiTheme.Scale;

    private void DrawCameraRotationArc(ImDrawListPtr draw, Vec3d pivot, float radius)
    {
        float angle = IsControlDown() ? MathF.Round(cameraDragAngle / (MathF.PI / 12)) * (MathF.PI / 12) : cameraDragAngle;
        angle = DirectorCameraManipulation.NormalizeEditorAngle(angle * 180 / MathF.PI) * MathF.PI / 180;
        int segments = Math.Clamp((int)(Math.Abs(angle) * 32), 1, 256);
        Vector3 Direction(float amount) => Vector3.Transform(cameraDragStartDirection,
            Quaternion.CreateFromAxisAngle(cameraDragAxis, amount));
        CameraHandleLine(draw, pivot, pivot, Add(pivot, cameraDragStartDirection * radius), 0x99ffffff, -1, 1);
        CameraHandleLine(draw, pivot, pivot, Add(pivot, Direction(angle) * radius), 0xffffffff, -1, 1);
        for (int i = 0; i < segments; i++)
            CameraHandleLine(draw, pivot, Add(pivot, Direction(angle * i / segments) * radius * .88f),
                Add(pivot, Direction(angle * (i + 1) / segments) * radius * .88f), 0xeeffffff, -1, 2);
    }

    private void DrawCameraAngleReadout(ImDrawListPtr draw, CameraPreset pose)
    {
        int axis = cameraDragAxisIndex >= 0 ? cameraDragAxisIndex : cameraHoverAxis;
        if ((axis is < 0 or > 2) && axis != ScreenRotationAxis) return;
        var panel = layout.Panel("preview");
        float scale = DirectorImGuiTheme.Scale;
        float value = DirectorCameraManipulation.NormalizeEditorAngle(axis == 0 ? pose.Pitch : axis == 1 ? pose.Yaw : pose.Roll);
        string text = axis == ScreenRotationAxis ? Lang.Get("vsdirector:camera-rotation-screen")
            : FormattableString.Invariant($"{"XYZ"[axis]}  {value:0.0}°");
        if (cameraDragAxisIndex >= 0)
        {
            float degrees = cameraDragAngle * 180 / MathF.PI;
            if (IsControlDown()) degrees = MathF.Round(degrees / 15) * 15;
            degrees = DirectorCameraManipulation.NormalizeEditorAngle(degrees);
            text = axis == ScreenRotationAxis
                ? Lang.Get("vsdirector:camera-rotation-screen-feedback", FormattableString.Invariant($"{degrees:+0.0;-0.0;0.0}"))
                : Lang.Get("vsdirector:camera-rotation-feedback", "XYZ"[axis],
                    FormattableString.Invariant($"{degrees:+0.0;-0.0;0.0}"), FormattableString.Invariant($"{value:0.0}"));
        }
        float fontScale = Math.Min(scale, Math.Max(1, (float)panel.Width * scale - 28 * scale)
            / Math.Max(1, ImGui.CalcTextSize(text).X));
        Vector2 size = ImGui.CalcTextSize(text) * fontScale;
        Vector2 at = CameraReadoutPosition(ImGui.GetMousePos(), size,
            new((float)panel.X * scale, (float)panel.Y * scale),
            new((float)(panel.X + panel.Width) * scale, (float)(panel.Y + panel.Height) * scale), scale);
        draw.AddRectFilled(at - new Vector2(10, 6) * scale, at + size + new Vector2(10, 6) * scale, 0xdd242424, 4 * scale);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * fontScale, at, 0xffeeeeee, text);
    }

    private bool TryCameraToolMouseDown(MouseEvent args)
    {
        if (!CameraToolsAvailable || layout.NavigationMode || !PointInsideGameViewport(args.X, args.Y)
            || args.Button is not (EnumMouseButton.Left or EnumMouseButton.Right)) return false;
        Vector2 point = new(args.X, args.Y);
        float threshold = 9 * DirectorImGuiTheme.Scale;
        int handleAxis = CameraHandleAxisAt(point, threshold);
        Guid hit = cameraToolHeads.Where(head => Vector2.Distance(head.Screen, point) < 18 * DirectorImGuiTheme.Scale)
            .OrderBy(head => Vector2.DistanceSquared(head.Screen, point)).Select(head => head.Id).FirstOrDefault();
        if (hit == Guid.Empty)
            hit = cameraToolLines.Where(line => line.CameraId != Guid.Empty)
                .Select(line => (Line: line, Distance: CameraLineDistance(point, line.A, line.B)))
                .Where(item => item.Distance < threshold).OrderBy(item => item.Distance)
                .Select(item => item.Line.CameraId).FirstOrDefault();
        bool centerHit = cameraToolHeads.Any(head => head.Id == hit
            && Vector2.Distance(head.Screen, point) <= 8 * DirectorImGuiTheme.Scale);
        if (hit != Guid.Empty && (args.Button == EnumMouseButton.Right || centerHit))
        {
            SelectCameraToolClip(hit);
            if (args.Button == EnumMouseButton.Right) cameraContextRequested = true;
        }
        else if (handleAxis >= 0 && cameraToolSelection != Guid.Empty)
        {
            if (args.Button == EnumMouseButton.Left) BeginCameraToolDrag(handleAxis, args.X, args.Y);
            else cameraContextRequested = true;
        }
        else if (hit != Guid.Empty)
        {
            SelectCameraToolClip(hit);
            if (args.Button == EnumMouseButton.Right) cameraContextRequested = true;
        }
        else return false;
        consumeCameraRelease = true;
        cameraReleaseButton = args.Button;
        args.Handled = true;
        return true;
    }

    private void SelectCameraToolClip(Guid id)
    {
        var timeline = owner.ReplayViewportTimeline;
        var clip = timeline.Clips.FirstOrDefault(c => c.Id == id);
        if (clip is null) return;
        cameraToolSelection = id;
        selectedReplayViewportClipId = id;
        selectedReplayElementTrackId = clip.TrackId;
        selectedReplayTimelineElementTrack = ReplayTimelineElementKind.Camera;
        selectedReplayElementKeyframeId = selectedReplayMarkerId = selectedReplaySpeedKeyframeId = Guid.Empty;
        replayViewportFieldsId = Guid.Empty;
        EnsureReplayTimelineElementTrackVisible(clip.TrackId);
        SyncReplayTimelineSelection();
        var panel = layout.Panel("timeline");
        double available = Math.Max(1, panel.Width - ReplayTimelineLabelColumnWidth(panel.Width));
        double tick = clip.TimelineStartSeconds * ReplayTimelineTicksPerSecond;
        if (tick < replayTimelineScrollTicks || tick > replayTimelineScrollTicks + available / ReplayTimelinePixelsPerTick)
            replayTimelineScrollTicks = Math.Max(0, tick - available / ReplayTimelinePixelsPerTick * .25);
        CloseReplayFloatingEditor();
        ComposeInspector();
        ComposeTimeline();
    }

    private void BeginCameraToolDrag(int axis, int x, int y)
    {
        FinishCameraTransform(cancel: false);
        var timeline = owner.ReplayViewportTimeline;
        var clip = timeline.Clips.FirstOrDefault(c => c.Id == cameraToolSelection);
        var pose = clip is null ? null : ReplayCameraElementPose(clip);
        if (pose is null) return;
        var axisVector = cameraTool == CameraTool.Rotate && axis == ScreenRotationAxis
            ? CameraScreenRotationAxis() : CameraAxis(axis >= 3 ? axis - 3 : axis);
        if (cameraTool == CameraTool.Rotate)
        {
            if (!CameraRotationPointerDirection(x, y, CameraPivot(pose), axisVector, out cameraDragPreviousDirection)) return;
            cameraDragStartDirection = cameraDragPreviousDirection;
        }
        else
        {
            if (!CameraPointerRay(x, y, CameraPivot(pose), out var origin, out var direction)) return;
            if (axis >= 3)
            {
                if (!CameraPointerPlanePoint(origin, direction, axisVector, out cameraDragPlanePoint)) return;
            }
            else if (!DirectorCameraManipulation.TryAxisParameter(origin, direction, axisVector, out cameraDragParameter)) return;
        }
        EndReplayViewportFreeCameraPointerInput();
        owner.PauseReplayForTimelineScrub();
        cameraDragOriginal = pose;
        cameraDragPose = pose.DeepClone();
        cameraDragTimeline = timeline;
        cameraDragAxisIndex = axis;
        cameraDragAxis = axisVector;
        cameraDragAngle = 0;
        cameraDragReplayId = owner.ActiveReplayId;
        cameraDragSceneId = timeline.ActiveSceneId;
    }

    private bool MoveCameraToolDrag(int x, int y)
    {
        if (cameraDragOriginal is not { } original || cameraDragTimeline is null) return false;
        Vector3 origin = default, direction = default;
        if (cameraTool != CameraTool.Rotate && !CameraPointerRay(x, y, CameraPivot(original), out origin, out direction)) return true;
        bool snap = IsControlDown();
        if (cameraTool == CameraTool.Rotate)
        {
            if (!CameraRotationPointerDirection(x, y, CameraPivot(original), cameraDragAxis, out var current)) return true;
            cameraDragAngle += MathF.Atan2(Vector3.Dot(cameraDragAxis, Vector3.Cross(cameraDragPreviousDirection, current)),
                Vector3.Dot(cameraDragPreviousDirection, current));
            cameraDragPreviousDirection = current;
            float angle = snap ? MathF.Round(cameraDragAngle / (MathF.PI / 12)) * (MathF.PI / 12) : cameraDragAngle;
            cameraDragPose = DirectorCameraManipulation.Rotate(original, cameraDragAxis, angle, cameraDragPose);
        }
        else if (cameraDragAxisIndex >= 3)
        {
            if (!CameraPointerPlanePoint(origin, direction, cameraDragAxis, out var current)) return true;
            Vector3 offset = current - cameraDragPlanePoint;
            offset -= cameraDragAxis * Vector3.Dot(offset, cameraDragAxis);
            if (snap) offset = new(MathF.Round(offset.X * 4) / 4, MathF.Round(offset.Y * 4) / 4, MathF.Round(offset.Z * 4) / 4);
            cameraDragPose = DirectorCameraManipulation.Translate(original, offset, cameraTool == CameraTool.Pivot);
        }
        else
        {
            if (!DirectorCameraManipulation.TryAxisParameter(origin, direction, cameraDragAxis, out float parameter)) return true;
            float distance = parameter - cameraDragParameter;
            if (snap) distance = MathF.Round(distance * 4) / 4;
            cameraDragPose = DirectorCameraManipulation.Translate(original, cameraDragAxis * distance, cameraTool == CameraTool.Pivot);
        }
        var clip = cameraDragTimeline.Clips.FirstOrDefault(c => c.Id == cameraToolSelection);
        if (clip is not null) clip.CameraPose = cameraDragPose;
        return true;
    }

    private void FinishCameraToolDrag(bool cancel)
    {
        if (cameraDragOriginal is null) return;
        var timeline = cameraDragTimeline;
        bool changed = cameraDragOriginal is not null && cameraDragPose is not null
            && (cameraDragOriginal.X != cameraDragPose.X || cameraDragOriginal.Y != cameraDragPose.Y
                || cameraDragOriginal.Z != cameraDragPose.Z || cameraDragOriginal.Yaw != cameraDragPose.Yaw
                || cameraDragOriginal.Pitch != cameraDragPose.Pitch || cameraDragOriginal.Roll != cameraDragPose.Roll
                || cameraDragOriginal.EditorPivotOffsetX != cameraDragPose.EditorPivotOffsetX
                || cameraDragOriginal.EditorPivotOffsetY != cameraDragPose.EditorPivotOffsetY
                || cameraDragOriginal.EditorPivotOffsetZ != cameraDragPose.EditorPivotOffsetZ);
        bool valid = cameraDragReplayId == owner.ActiveReplayId && timeline?.ActiveSceneId == owner.ReplayViewportTimeline.ActiveSceneId;
        cameraDragOriginal = cameraDragPose = null;
        cameraDragTimeline = null;
        cameraDragAxisIndex = -1;
        if (!cancel && valid && changed && timeline is not null)
        {
            CommitReplayViewportTimeline(timeline);
            ComposeInspector();
            ComposeTimeline();
        }
    }

    private bool CameraPointerRay(int x, int y, Vec3d pivot, out Vector3 origin, out Vector3 direction)
    {
        origin = direction = default;
        if (!ProjectCameraPoint(pivot, out var center)) return false;
        var m = capi.Render.PerspectiveViewMat;
        Vector3 right = new((float)m[0], (float)m[4], (float)m[8]);
        Vector3 up = new((float)m[1], (float)m[5], (float)m[9]);
        // Invert the same view-plane projection used to draw the controls.
        origin = (right * (x - center.X) - up * (y - center.Y)) * CameraWorldUnitsPerPixel(pivot);
        direction = -CameraScreenRotationAxis();
        return float.IsFinite(direction.X) && float.IsFinite(direction.Y) && float.IsFinite(direction.Z);
    }

    private static bool CameraPointerPlanePoint(Vector3 origin, Vector3 direction, Vector3 normal, out Vector3 point)
    {
        point = default;
        float denominator = Vector3.Dot(direction, normal);
        if (Math.Abs(denominator) < 1e-6f) return false;
        // The orthographic pointer starts in the pivot's view plane. A valid
        // handle plane can lie on either side, so intersect the whole line.
        point = origin - direction * (Vector3.Dot(origin, normal) / denominator);
        return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    }

    private static Vec3d CameraPivot(CameraPreset pose) => new(pose.X + pose.EditorPivotOffsetX,
        pose.Y + pose.Dimension * 32768d + pose.EditorPivotOffsetY, pose.Z + pose.EditorPivotOffsetZ);
    private static Vec3d Add(Vec3d point, Vector3 offset) => new(point.X + offset.X, point.Y + offset.Y, point.Z + offset.Z);
    private static Vector3 CameraAxis(int axis) => axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
    private static float CameraLineDistance(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 line = b - a;
        float amount = line.LengthSquared() < 1e-5f ? 0 : Math.Clamp(Vector2.Dot(point - a, line) / line.LengthSquared(), 0, 1);
        return Vector2.Distance(point, a + line * amount);
    }

    private void ResetSelectedCameraPivot()
    {
        var timeline = owner.ReplayViewportTimeline;
        var clip = timeline.Clips.FirstOrDefault(c => c.Id == cameraToolSelection);
        if (clip is null || ReplayCameraElementPose(clip) is not { } pose) return;
        pose.EditorPivotOffsetX = pose.EditorPivotOffsetY = pose.EditorPivotOffsetZ = 0;
        clip.CameraPose = pose;
        CommitReplayViewportTimeline(timeline);
    }

    private void OpenReplayCameraPreview()
    {
        cameraPreviewSource = cameraToolSelection;
        cameraPreviewOpen = true;
        cameraPreviewFocus = true;
        cameraPreviewFailed = false;
        cameraPreviewError = "";
        owner.ClearReplayCameraPreviewError();
        cameraPreviewSink.Texture = 0;
        cameraPreviewSink.ViewId = owner.ReplayCameraPreviewViewId;
        imGuiWorkspace.CameraPreviewVisible = true;
    }

    private void DrawReplayCameraPreview()
    {
        if (!cameraPreviewOpen) return;
        if (section != DirectorWorkspaceSection.Replay || !owner.IsReplayPlaying)
        {
            CloseReplayCameraPreview();
            return;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4));
        if (cameraPreviewFocus) { ImGui.SetNextWindowFocus(); cameraPreviewFocus = false; }
        bool visible = ImGui.Begin(RmlLabel("camera-preview-title") + "###vsdirector-camera-preview", ref cameraPreviewOpen,
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        try
        {
            if (visible)
            {
                Vector2 available = Vector2.Max(new(1), ImGui.GetContentRegionAvail());
                float aspect = capi.Render.FrameWidth / (float)Math.Max(1, capi.Render.FrameHeight);
                Vector2 size = new(Math.Min(available.X, available.Y * aspect), Math.Min(available.Y, available.X / aspect));
                ImGui.SetCursorPos(ImGui.GetCursorPos() + (available - size) / 2);
                CameraPreset? pose = CameraPreviewPose();
                if (!cameraPreviewFailed && !string.IsNullOrEmpty(owner.ReplayCameraPreviewError))
                {
                    cameraPreviewError = owner.ReplayCameraPreviewError;
                    cameraPreviewFailed = true;
                }
                bool ready = !cameraPreviewFailed && owner.ConfigureReplayCameraPreview(pose,
                    (int)Math.Round(size.X), (int)Math.Round(size.Y), cameraPreviewSink, out cameraPreviewError);
                if (!string.IsNullOrEmpty(cameraPreviewError)) cameraPreviewFailed = true;
                if (ready && cameraPreviewSink.Texture != 0)
                    DrawCameraPreviewImage(cameraPreviewSink.Texture, size);
                else
                {
                    cameraPreviewSink.Texture = 0;
                    ImGui.TextWrapped(string.IsNullOrEmpty(cameraPreviewError) ? RmlLabel("camera-preview-waiting") : cameraPreviewError);
                }
            }
            else
            {
                owner.StopReplayCameraPreview();
                cameraPreviewSink.Texture = 0;
            }
        }
        finally { ImGui.End(); ImGui.PopStyleVar(); }
        if (!cameraPreviewOpen) CloseReplayCameraPreview();
    }

    private static void DrawCameraPreviewImage(int texture, Vector2 size)
        // The final composition and blit preserve OpenGL's bottom-left origin.
        => ImGui.Image((nint)texture, size, new Vector2(0, 1), new Vector2(1, 0));

    private CameraPreset? CameraPreviewPose()
        => ReplayCameraViewSubject(cameraPreviewSource);

    private CameraPreset? ReplayCameraViewSubject(Guid source)
    {
        // Follow the evaluated shooting camera when this element owns the current shot.
        var timeline = owner.ReplayViewportTimeline;
        var clip = timeline.Clips.FirstOrDefault(c => c.Id == source && c.Enabled
            && c.SourceType == DirectorReplayViewportSourceType.StaticCamera);
        if (clip is null) return null;
        if (!timeline.Tracks.Any(t => t.Id == clip.TrackId && t.Enabled)) return null;
        if (cameraDragPose is null && transformPose is null && owner.ReplayShootingSourceId == source) return owner.ReplayShootingPose;
        var pose = transformPose is not null && transformClipId == source ? transformPose.DeepClone()
            : cameraDragPose is not null && cameraToolSelection == source ? cameraDragPose.DeepClone() : ReplayCameraElementPose(clip);
        if (pose is null) return null;
        return owner.ApplyReplayShootingCameraVisuals(pose, timeline, owner.ReplayCameraSampleTime);
    }

    private void CloseReplayCameraPreview()
    {
        cameraPreviewOpen = false;
        imGuiWorkspace.CameraPreviewVisible = false;
        owner.StopReplayCameraPreview();
        cameraPreviewSink.Texture = 0;
    }
}
