using System.Numerics;
using ImGuiNET;
using VSDirector.Data;
using VSDirector.Rendering;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private readonly DirectorCameraObservation cameraObservation = new();
    private readonly CameraObservationSink cameraObservationSink = new();
    private bool cameraObservationOpen, cameraObservationFocus, cameraObservationFailed, cameraObservationGizmoPressed;
    private string cameraObservationError = "";
    private Guid cameraObservationSource, cameraObservationReplay;

    private sealed class CameraObservationSink : IDirectorOffscreenFrameSink
    {
        internal Guid ViewId;
        internal int Texture;
        internal float Aspect;
        internal CameraPreset? RequestedEye, RequestedSubject, Eye, Subject;

        public void OnFrame(in DirectorOffscreenFrame frame)
        {
            if (frame.ViewId != ViewId) return;
            Texture = frame.ColorTextureId;
            Aspect = frame.Width / (float)frame.Height;
            // Keep the overlay and orientation widget in the rendered frame's pose.
            Eye = RequestedEye;
            Subject = RequestedSubject;
        }
    }

    private void OpenReplayCameraObservation()
    {
        if (!cameraObservationOpen || cameraObservationSource != cameraToolSelection) cameraObservation.Reset();
        cameraObservationSource = cameraToolSelection;
        cameraObservationReplay = owner.ActiveReplayId;
        cameraObservationOpen = cameraObservationFocus = true;
        cameraObservationFailed = false;
        cameraObservationError = "";
        owner.ClearReplayCameraObservationError();
        cameraObservationSink.Texture = 0;
        cameraObservationSink.ViewId = owner.ReplayCameraObservationViewId;
        imGuiWorkspace.CameraObservationVisible = true;
    }

    private void DrawReplayCameraObservation()
    {
        if (!cameraObservationOpen) return;
        if (section != DirectorWorkspaceSection.Replay || !owner.IsReplayPlaying || cameraObservationReplay != owner.ActiveReplayId)
        {
            CloseReplayCameraObservation();
            return;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4));
        if (cameraObservationFocus) { ImGui.SetNextWindowFocus(); cameraObservationFocus = false; }
        bool visible = ImGui.Begin(RmlLabel("camera-observation-title") + "###vsdirector-camera-observation", ref cameraObservationOpen,
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        try
        {
            if (visible)
            {
                Vector2 available = Vector2.Max(new(1), ImGui.GetContentRegionAvail());
                float aspect = capi.Render.FrameWidth / (float)Math.Max(1, capi.Render.FrameHeight);
                Vector2 size = new(Math.Min(available.X, available.Y * aspect), Math.Min(available.Y, available.X / aspect));
                ImGui.SetCursorPos(ImGui.GetCursorPos() + (available - size) / 2);
                Vector2 min = ImGui.GetCursorScreenPos();
                CameraPreset? subject = ReplayCameraViewSubject(cameraObservationSource);
                if (!cameraObservationFailed && !string.IsNullOrEmpty(owner.ReplayCameraObservationError))
                {
                    cameraObservationError = owner.ReplayCameraObservationError;
                    cameraObservationFailed = true;
                }
                cameraObservationSink.RequestedSubject = subject;
                cameraObservationSink.RequestedEye = subject is null ? null : cameraObservation.Eye(subject);
                bool ready = !cameraObservationFailed && owner.ConfigureReplayCameraObservation(cameraObservationSink.RequestedEye,
                    (int)Math.Round(size.X), (int)Math.Round(size.Y), cameraObservationSink, out cameraObservationError);
                if (!string.IsNullOrEmpty(cameraObservationError)) cameraObservationFailed = true;
                if (ready && cameraObservationSink.Texture != 0 && cameraObservationSink.Eye is { } eye
                    && cameraObservationSink.Subject is { } renderedSubject)
                    DrawCameraObservationViewport(min, size, eye, renderedSubject, cameraObservationSink.Texture, cameraObservationSink.Aspect);
                else
                {
                    cameraObservationSink.Texture = 0;
                    ImGui.TextWrapped(subject is null ? RmlLabel("camera-observation-missing")
                        : string.IsNullOrEmpty(cameraObservationError) ? RmlLabel("camera-preview-waiting") : cameraObservationError);
                }
            }
            else
            {
                owner.StopReplayCameraObservation();
                cameraObservationSink.Texture = 0;
            }
        }
        finally { ImGui.End(); ImGui.PopStyleVar(); }
        if (!cameraObservationOpen) CloseReplayCameraObservation();
    }

    private static string ObservationSideKey(int side) => "camera-observation-" + (side switch
    {
        0 => "east", 1 => "west", 2 => "top", 3 => "bottom", 4 => "south", 5 => "north", _ => "orbit"
    });

    private static string ObservationSideLabel(int side) => RmlLabel(ObservationSideKey(side));

    private void DrawCameraObservationViewport(Vector2 min, Vector2 size, CameraPreset eye, CameraPreset subject, int texture, float aspect)
    {
        float scale = DirectorImGuiTheme.Scale;
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(min, min + size, true);
        draw.AddImage((nint)texture, min, min + size, new(0, 1), new(1, 0));
        DrawObservedCamera(draw, eye, subject, min, size, aspect);
        draw.PopClipRect();

        // Submit the controls first so the orbit surface cannot capture their clicks.
        if (size.X >= 130 * scale && size.Y >= ImGui.GetFrameHeight() * 2)
        {
            ImGui.SetCursorScreenPos(min + new Vector2(6 * scale));
            ImGui.SetNextItemWidth(Math.Min(146 * scale, size.X - 12 * scale));
            if (ImGui.BeginCombo("##observation-direction", ObservationSideLabel(cameraObservation.Side)))
            {
                for (int side = -1; side < 6; side++)
                    if (ImGui.Selectable(ObservationSideLabel(side), cameraObservation.Side == side)) cameraObservation.Snap(side);
                ImGui.Separator();
                if (ImGui.Selectable(RmlLabel("camera-observation-reset"))) cameraObservation.Reset();
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(RmlLabel("camera-observation-controls"));
        }
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("camera-observation-viewport", size, ImGuiButtonFlags.MouseButtonLeft);
        bool hovered = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
        draw.PushClipRect(min, min + size, true);
        try
        {
            bool onGizmo = DrawObservationOrientationGizmo(draw, eye, min, size, hovered);
            var io = ImGui.GetIO();
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) cameraObservationGizmoPressed = false;
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) cameraObservationGizmoPressed = onGizmo;
            if (active && !cameraObservationGizmoPressed && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                cameraObservation.Orbit(io.MouseDelta / scale);
            if (hovered && io.MouseWheel != 0) cameraObservation.Zoom(io.MouseWheel);
        }
        finally { draw.PopClipRect(); }

        ImGui.SetCursorScreenPos(min + size);
    }

    private static void DrawObservedCamera(ImDrawListPtr draw, CameraPreset eye, CameraPreset subject, Vector2 min, Vector2 size, float aspect)
    {
        var basis = CameraHeadBasis(subject);
        bool Project(Vector3 local, out Vector2 screen)
        {
            Vector3 offset = basis.Right * local.X + basis.Up * local.Y + basis.Back * local.Z;
            return DirectorCameraObservation.Project(eye, subject.X + offset.X, subject.Y + offset.Y, subject.Z + offset.Z,
                aspect, min, size, out screen);
        }
        float scale = DirectorImGuiTheme.Scale;
        // An editor overlay stays visible even if the world occludes the camera.
        foreach (var edge in CameraOutline)
            if (Project(edge.A, out var a) && Project(edge.B, out var b)) draw.AddLine(a, b, CameraOutlineColor, 1.5f * scale);
        if (!DirectorCameraObservation.Project(eye, subject.X, subject.Y, subject.Z, aspect, min, size, out var center)) return;
        uint[] colors = [0xff6666ee, 0xff77cf75, 0xffedaa62];
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 offset = CameraAxis(axis) * .65f;
            if (DirectorCameraObservation.Project(eye, subject.X + offset.X, subject.Y + offset.Y, subject.Z + offset.Z,
                aspect, min, size, out var tip)) draw.AddLine(center, tip, colors[axis], 1.5f * scale);
        }
        DrawCameraCenter(draw, center);
    }

    private bool DrawObservationOrientationGizmo(ImDrawListPtr draw, CameraPreset eye, Vector2 min, Vector2 size, bool hovered)
    {
        float scale = DirectorImGuiTheme.Scale;
        float extent = Math.Min(55 * scale, Math.Min(size.X, size.Y) * .33f);
        if (extent < 20) return false;
        Vector2 center = min + size - new Vector2(extent + 8 * scale);
        float radius = extent * .70f, dot = extent * .19f;
        var sides = Enumerable.Range(0, 6).Select(side =>
        {
            Vector3 direction = CameraAxis(side / 2) * (side % 2 == 0 ? 1 : -1);
            Vector3 projected = DirectorCameraObservation.ViewDirection(eye, direction);
            return (Side: side, Point: center + new Vector2(projected.X, projected.Y) * radius, Depth: projected.Z);
        }).OrderBy(side => side.Depth).ToArray();
        Vector2 mouse = ImGui.GetMousePos();
        int hit = hovered ? sides.Where(side => Vector2.DistanceSquared(mouse, side.Point) <= (dot + 3 * scale) * (dot + 3 * scale))
            .Select(side => side.Side).DefaultIfEmpty(-1).Last() : -1;
        draw.AddCircleFilled(center, extent, 0xa0232220, 48);
        uint[] colors = [0xff6969ed, 0xff77ce83, 0xffefad6e];
        uint[] dark = [0xff424282, 0xff426c48, 0xff82603d];
        foreach (var side in sides)
        {
            int axis = side.Side / 2;
            bool positive = side.Side % 2 == 0;
            uint color = hit == side.Side ? 0xffa1eaff : positive ? colors[axis] : dark[axis];
            draw.AddLine(center, side.Point, color, (positive ? 2 : 1) * scale);
            draw.AddCircleFilled(side.Point, dot, color, 24);
            if (positive)
            {
                string label = "XYZ"[axis].ToString();
                draw.AddText(side.Point - ImGui.CalcTextSize(label) / 2, 0xff181818, label);
            }
        }
        if (hit >= 0)
        {
            ImGui.SetTooltip(RmlLabel(ObservationSideKey(hit) + "-tooltip"));
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) cameraObservation.Snap(hit);
        }
        return hovered && Vector2.DistanceSquared(mouse, center) <= extent * extent;
    }

    private void CloseReplayCameraObservation()
    {
        cameraObservationOpen = false;
        imGuiWorkspace.CameraObservationVisible = false;
        owner.StopReplayCameraObservation();
        cameraObservationSink.Texture = 0;
        cameraObservationGizmoPressed = false;
    }
}
