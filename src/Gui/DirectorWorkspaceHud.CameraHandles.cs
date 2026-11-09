using System.Numerics;
using ImGuiNET;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private Vector3 CameraScreenRotationAxis()
    {
        var m = capi.Render.PerspectiveViewMat;
        return Vector3.Normalize(new((float)m[2], (float)m[6], (float)m[10]));
    }

    private float CameraGizmoRadius(CameraPreset pose)
    {
        Vec3d pivot = CameraPivot(pose);
        float units = CameraWorldUnitsPerPixel(pivot), scale = DirectorImGuiTheme.Scale;
        float pixels = CameraHandleRadius(pivot) / units;
        if (!ProjectCameraPoint(pivot, out var center)) return units * pixels;
        var basis = CameraHeadBasis(pose);
        Vec3d origin = new(pose.X, pose.Y + pose.Dimension * 32768d, pose.Z);
        foreach (var edge in CameraOutline)
        foreach (var point in new[] { edge.A, edge.B })
            if (ProjectCameraPoint(Add(origin, basis.Right * point.X + basis.Up * point.Y + basis.Back * point.Z), out var screen))
                pixels = Math.Max(pixels, Vector2.Distance(center, screen) + 18 * scale);
        return units * pixels;
    }

    private bool ProjectCameraHandlePoint(Vec3d pivot, Vec3d point, out Vector2 screen)
    {
        // Editor gizmos use an orthographic frame at the pivot. Their shape
        // and picking agree even when the outline is very close to the editor eye.
        if (!ProjectCameraPoint(pivot, out screen)) return false;
        var m = capi.Render.PerspectiveViewMat;
        double x = point.X - pivot.X, y = point.Y - pivot.Y, z = point.Z - pivot.Z;
        float units = CameraWorldUnitsPerPixel(pivot);
        screen += new Vector2((float)(m[0] * x + m[4] * y + m[8] * z),
            (float)-(m[1] * x + m[5] * y + m[9] * z)) / units;
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    private bool CameraRotationPointerDirection(int x, int y, Vec3d pivot, Vector3 axis, out Vector3 direction)
    {
        direction = default;
        if (!ProjectCameraPoint(pivot, out var center)) return false;
        var m = capi.Render.PerspectiveViewMat;
        Vector3 right = new((float)m[0], (float)m[4], (float)m[8]);
        Vector3 up = new((float)m[1], (float)m[5], (float)m[9]);
        Vector3 view = CameraScreenRotationAxis();
        float denominator = Vector3.Dot(view, axis);
        if (Math.Abs(denominator) < .0001f) return false;
        Vector3 point = right * (x - center.X) - up * (y - center.Y);
        point -= view * (Vector3.Dot(point, axis) / denominator);
        if (point.LengthSquared() < .0001f) return false;
        direction = Vector3.Normalize(point);
        return true;
    }

    private void CameraHandleLine(ImDrawListPtr draw, Vec3d pivot, Vec3d a, Vec3d b, uint color, int axis, float thickness)
    {
        if (!ProjectCameraHandlePoint(pivot, a, out var start) || !ProjectCameraHandlePoint(pivot, b, out var end)) return;
        draw.AddLine(start, end, color, thickness * DirectorImGuiTheme.Scale);
        if (axis >= 0) cameraToolLines.Add(new(start, end, axis, Guid.Empty));
    }

    private void DrawCameraScreenRotationRing(ImDrawListPtr draw, Vec3d pivot, float radius)
    {
        if (!ProjectCameraPoint(pivot, out var center)) return;
        float pixels = radius / CameraWorldUnitsPerPixel(pivot);
        uint color = cameraDragAxisIndex == ScreenRotationAxis || cameraHoverAxis == ScreenRotationAxis
            ? 0xffffc982u : 0xffff9a42u;
        const int segments = 128;
        for (int i = 0; i < segments; i++)
        {
            Vector2 Point(int index) => center + new Vector2(MathF.Cos(index * MathF.Tau / segments),
                MathF.Sin(index * MathF.Tau / segments)) * pixels;
            Vector2 a = Point(i), b = Point(i + 1);
            draw.AddLine(a, b, color, 2.5f * DirectorImGuiTheme.Scale);
            cameraToolLines.Add(new(a, b, ScreenRotationAxis, Guid.Empty));
        }
    }

    private Vector3 CameraViewDirection(Vec3d pivot)
    {
        // Derive the editor eye from the current render view, independently of the player.
        var m = capi.Render.PerspectiveViewMat;
        double x = m[0] * pivot.X + m[4] * pivot.Y + m[8] * pivot.Z + m[12];
        double y = m[1] * pivot.X + m[5] * pivot.Y + m[9] * pivot.Z + m[13];
        double z = m[2] * pivot.X + m[6] * pivot.Y + m[10] * pivot.Z + m[14];
        Vector3 direction = new((float)(-m[0] * x - m[1] * y - m[2] * z),
            (float)(-m[4] * x - m[5] * y - m[6] * z), (float)(-m[8] * x - m[9] * y - m[10] * z));
        return direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Vector3.UnitZ;
    }

    private void DrawCameraRotationRing(ImDrawListPtr draw, Vec3d pivot, float radius, int axis, uint color, Vector3 view)
    {
        DrawCameraRotationRingLayer(draw, pivot, radius, axis, color, view, false);
        DrawCameraRotationRingLayer(draw, pivot, radius, axis, color, view, true);
    }

    private void DrawCameraRotationRingLayer(ImDrawListPtr draw, Vec3d pivot, float radius, int axis, uint color, Vector3 view, bool frontLayer)
    {
        Vector3 normal = CameraAxis(axis), u = CameraAxis((axis + 1) % 3), v = CameraAxis((axis + 2) % 3);
        Vector3 front = view - normal * Vector3.Dot(view, normal);
        bool faceOn = front.LengthSquared() < .001f;
        if (faceOn)
        {
            var m = capi.Render.PerspectiveViewMat;
            Vector3 up = new((float)m[1], (float)m[5], (float)m[9]);
            front = up - normal * Vector3.Dot(up, normal);
            if (front.LengthSquared() < 1e-8f) front = u;
        }
        float facingAngle = MathF.Atan2(Vector3.Dot(front, v), Vector3.Dot(front, u));
        Vector3 Radial(float a) => u * MathF.Cos(a) + v * MathF.Sin(a);
        float markerAngle = facingAngle;
        const int steps = 96;
        float sweep = faceOn ? MathF.Tau : MathF.PI;
        if (faceOn && !frontLayer) return;
        float start = facingAngle - MathF.PI / 2 + (frontLayer ? 0 : MathF.PI);
        uint arcColor = frontLayer ? color : DarkCameraRingColor(color);
        for (int step = 0; step < steps; step++)
        {
            float a = start + step * sweep / steps, b = start + (step + 1) * sweep / steps;
            CameraHandleLine(draw, pivot, Add(pivot, Radial(a) * radius), Add(pivot, Radial(b) * radius), arcColor,
                axis, frontLayer ? 2.5f : 1);
        }
        if (!frontLayer) return;
        if (ProjectCameraHandlePoint(pivot, Add(pivot, Radial(markerAngle - .01f) * radius), out var before)
            && ProjectCameraHandlePoint(pivot, Add(pivot, Radial(markerAngle + .01f) * radius), out var after))
        {
            // Both tips stay on the arc while the solid double cone stays straight.
            float scale = DirectorImGuiTheme.Scale;
            float speed = Vector2.Distance(before, after) / .02f;
            if (speed < .001f) return;
            float halfAngle = Math.Clamp(15 * scale / speed, .001f, MathF.PI / 4);
            // Correct for perspective and edge-on foreshortening, keeping the
            // same screen size without pulling either tip away from the arc.
            for (int iteration = 0; iteration < 4; iteration++)
            {
                if (!ProjectCameraHandlePoint(pivot, Add(pivot, Radial(markerAngle - halfAngle) * radius), out var left)
                    || !ProjectCameraHandlePoint(pivot, Add(pivot, Radial(markerAngle + halfAngle) * radius), out var right)) return;
                float span = Vector2.Distance(left, right);
                if (span < .001f) return;
                halfAngle = Math.Clamp(halfAngle * (30 * scale / span), .001f, MathF.PI / 4);
            }
            float thickness = CameraWorldUnitsPerPixel(pivot) * 5 * scale;
            DrawCameraRotationHandle(draw, pivot, radius, axis, markerAngle, halfAngle, thickness, color);
        }
    }

    private static uint DarkCameraRingColor(uint color)
        => (uint)((color & 0xff) * .45f) | (uint)(((color >> 8) & 0xff) * .45f) << 8
            | (uint)(((color >> 16) & 0xff) * .45f) << 16 | 0x70000000;

    private static Vec3d CameraRotationHandlePoint(Vec3d pivot, float radius, int axis,
        float angle, float halfAngle, float thickness, float along, float around)
    {
        Vector3 u = CameraAxis((axis + 1) % 3), v = CameraAxis((axis + 2) % 3);
        Vector3 Radial(float theta) => u * MathF.Cos(theta) + v * MathF.Sin(theta);
        Vector3 start = Radial(angle - halfAngle) * radius, end = Radial(angle + halfAngle) * radius;
        Vector3 centre = Vector3.Lerp(start, end, (along + 1) / 2);
        Vector3 radial = Radial(angle);
        float width = thickness * (1 - Math.Abs(along));
        return Add(pivot, centre + radial * (width * MathF.Cos(around))
            + CameraAxis(axis) * (width * MathF.Sin(around)));
    }

    private void DrawCameraRotationHandle(ImDrawListPtr draw, Vec3d pivot, float radius, int axis,
        float angle, float halfAngle, float thickness, uint color)
    {
        // Two straight circular cones share a base perpendicular to the chord.
        // The tips move along the ring; every face uses the same unlit axis colour.
        const int segments = 2, sides = 32;
        var points = new Vector2[segments + 1, sides];
        for (int i = 0; i <= segments; i++)
        for (int j = 0; j < sides; j++)
            if (!ProjectCameraHandlePoint(pivot, CameraRotationHandlePoint(pivot, radius, axis, angle, halfAngle,
                thickness, 2f * i / segments - 1, j * MathF.Tau / sides), out points[i, j])) return;
        var flags = draw.Flags;
        draw.Flags &= ~ImDrawListFlags.AntiAliasedFill;
        try
        {
            for (int i = 0; i < segments; i++)
            for (int j = 0; j < sides; j++)
            {
                int next = (j + 1) % sides;
                if (i > 0) Face(points[i, j], points[i, next], points[i + 1, j]);
                if (i < segments - 1) Face(points[i, next], points[i + 1, next], points[i + 1, j]);
            }
        }
        finally { draw.Flags = flags; }

        void Face(Vector2 a, Vector2 b, Vector2 c)
        {
            if ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) < 0) (b, c) = (c, b);
            draw.AddTriangleFilled(a, b, c, color);
            cameraToolTriangles.Add(new(a, b, c, axis));
        }
    }

    private void DrawCameraMovePlane(ImDrawListPtr draw, Vec3d pivot, float length, int normalAxis, uint color, Vector3 view)
    {
        Vector3 normal = CameraAxis(normalAxis);
        if (Math.Abs(Vector3.Dot(normal, view)) < .08f) return;
        Vector3 u = CameraAxis((normalAxis + 1) % 3), v = CameraAxis((normalAxis + 2) % 3);
        if (Vector3.Dot(view, u) < 0) u = -u;
        if (Vector3.Dot(view, v) < 0) v = -v;
        Vec3d At(float x, float y) => Add(pivot, (u * x + v * y) * length);
        int handle = normalAxis + 3;
        bool highlighted = cameraDragAxisIndex == handle || cameraDragAxisIndex < 0 && cameraHoverAxis == handle;
        DrawCameraHandleQuad(draw, pivot, [At(.30f, .30f), At(.58f, .30f), At(.58f, .58f), At(.30f, .58f)],
            color, handle, highlighted ? 0xb0000000u : 0x55000000u);
    }

    private void DrawCameraPivotBox(ImDrawListPtr draw, Vec3d pivot, float length, int axis, uint color)
    {
        Vector3 tip = CameraAxis(axis) * length;
        float half = Math.Min(length * .10f, CameraWorldUnitsPerPixel(pivot) * 6 * DirectorImGuiTheme.Scale);
        Vec3d[] corners = Enumerable.Range(0, 8).Select(i => Add(pivot,
            tip + new Vector3((i & 1) == 0 ? -half : half, (i & 2) == 0 ? -half : half, (i & 4) == 0 ? -half : half))).ToArray();
        int[][] faces = [[0, 1, 3, 2], [4, 5, 7, 6], [0, 1, 5, 4], [2, 3, 7, 6], [0, 2, 6, 4], [1, 3, 7, 5]];
        var m = capi.Render.PerspectiveViewMat;
        double Depth(int[] face) => face.Average(i => m[2] * corners[i].X + m[6] * corners[i].Y + m[10] * corners[i].Z + m[14]);
        foreach (var face in faces.OrderBy(Depth))
            DrawCameraHandleQuad(draw, pivot, face.Select(i => corners[i]).ToArray(), color, axis, 0xe0000000);
    }

    private void DrawCameraHandleQuad(ImDrawListPtr draw, Vec3d pivot, Vec3d[] corners, uint color, int handle, uint alpha)
    {
        var screen = new Vector2[4];
        for (int i = 0; i < 4; i++) if (!ProjectCameraHandlePoint(pivot, corners[i], out screen[i])) return;
        float area = (screen[1].X - screen[0].X) * (screen[2].Y - screen[0].Y)
            - (screen[1].Y - screen[0].Y) * (screen[2].X - screen[0].X);
        if (Math.Abs(area) < .01f) return;
        if (area < 0) Array.Reverse(screen);
        draw.AddQuadFilled(screen[0], screen[1], screen[2], screen[3], (color & 0x00ffffff) | alpha);
        draw.AddQuad(screen[0], screen[1], screen[2], screen[3], color, 1.5f * DirectorImGuiTheme.Scale);
        cameraToolTriangles.Add(new(screen[0], screen[1], screen[2], handle));
        cameraToolTriangles.Add(new(screen[0], screen[2], screen[3], handle));
        for (int i = 0; i < 4; i++) cameraToolLines.Add(new(screen[i], screen[(i + 1) % 4], handle, Guid.Empty));
    }

    private static Vector2 CameraReadoutPosition(Vector2 pointer, Vector2 size, Vector2 min, Vector2 max, float scale)
    {
        Vector2 padding = new Vector2(10, 8) * scale;
        return Vector2.Clamp(pointer + new Vector2(18, 24) * scale, min + padding,
            Vector2.Max(min + padding, max - size - padding));
    }
}
