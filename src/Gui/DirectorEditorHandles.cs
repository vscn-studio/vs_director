using System.Numerics;
using ImGuiNET;
using Vintagestory.API.MathTools;
namespace VSDirector.Gui;
// The creation workspace uses the replay handle geometry and hit surfaces.
// Projection and axis basis are supplied by its own fitted viewport.
internal sealed class DirectorEditorHandles
{
    private const int ScreenRotationAxis=6;
    private readonly List<(Vector2 A,Vector2 B,int Axis,Guid CameraId)> cameraToolLines=new();
    private readonly List<(Vector2 A,Vector2 B,Vector2 C,int Axis)> cameraToolTriangles=new();
    private readonly List<(Guid Id,Vector2 Screen)> cameraToolHeads=new();
    internal IEnumerable<(Vector2 A,Vector2 B,int Axis,Guid CameraId)> Lines => cameraToolLines;
    internal IEnumerable<(Vector2 A,Vector2 B,Vector2 C,int Axis)> Triangles => cameraToolTriangles;
    private int cameraDragAxisIndex=-1, cameraHoverAxis=-1;
    private double[] viewMatrix=[];
    private Vector3[] axes=[];
    private Vector2 center;
    private float units;
    private Vector3 CameraAxis(int axis)=>axes[axis];
    private static Vec3d Add(Vec3d p,Vector3 v)=>new(p.X+v.X,p.Y+v.Y,p.Z+v.Z);
    private float CameraWorldUnitsPerPixel(Vec3d p)=>units;
    private bool ProjectCameraPoint(Vec3d p,out Vector2 screen) { screen=center; return true; }
    private bool ProjectCameraHandlePoint(Vec3d pivot,Vec3d point,out Vector2 screen)
    {
        var m=viewMatrix; double x=point.X-pivot.X,y=point.Y-pivot.Y,z=point.Z-pivot.Z;
        screen=center+new Vector2((float)(m[0]*x+m[4]*y+m[8]*z),(float)-(m[1]*x+m[5]*y+m[9]*z))/units;
        return float.IsFinite(screen.X)&&float.IsFinite(screen.Y);
    }
    private void CameraHandleLine(ImDrawListPtr draw,Vec3d pivot,Vec3d a,Vec3d b,uint color,int axis,float thickness)
    {
        ProjectCameraHandlePoint(pivot,a,out var start); ProjectCameraHandlePoint(pivot,b,out var end);
        draw.AddLine(start,end,color,thickness*DirectorImGuiTheme.Scale);
        if(axis>=0) cameraToolLines.Add((start,end,axis,Guid.Empty));
    }
    private static float CameraLineDistance(Vector2 p,Vector2 a,Vector2 b)
    { var d=b-a; return Vector2.Distance(p,a+d*(d.LengthSquared()<1e-8?0:Math.Clamp(Vector2.Dot(p-a,d)/d.LengthSquared(),0,1))); }
    internal int Draw(ImDrawListPtr draw,Vector2 origin,float worldUnits,float radius,double[] view,Vector3[] basis,bool rotate,bool scale,int active,bool interactive,int hover=-1,int rotationAxis=-1)
    {
        center=origin;units=worldUnits;viewMatrix=view;axes=basis;cameraDragAxisIndex=active;cameraHoverAxis=hover;
        cameraToolLines.Clear();cameraToolTriangles.Clear();cameraToolHeads.Clear();
        Vec3d pivot=new(); Vector3 eye=Vector3.Normalize(new((float)view[2],(float)view[6],(float)view[10]));
        uint[] colors=[0xff6666ee,0xff77cf75,0xffedaa62];
        uint Color(int i)=>active==i||cameraHoverAxis==i?0xff55d9ff:colors[i];
        if(rotate)
        {
            for(int i=0;i<3;i++) if(rotationAxis<0||rotationAxis==i) DrawCameraRotationRingLayer(draw,pivot,radius,i,Color(i),eye,false);
            for(int i=0;i<3;i++) if(rotationAxis<0||rotationAxis==i) DrawCameraRotationRingLayer(draw,pivot,radius,i,Color(i),eye,true);
            if(rotationAxis<0||rotationAxis==6) DrawCameraScreenRotationRing(draw,pivot,radius+units*22*DirectorImGuiTheme.Scale);
        }
        else
        {
            for(int i=0;i<3;i++)
            {
                var d=axes[i]; float projected=new Vector2((float)(view[0]*d.X+view[4]*d.Y+view[8]*d.Z),(float)(view[1]*d.X+view[5]*d.Y+view[9]*d.Z)).Length();
                float length=radius/Math.Max(.15f,projected);
                CameraHandleLine(draw,pivot,pivot,Add(pivot,d*(length-(scale?0:CameraMoveConeLength(pivot,length)))),Color(i),i,1.5f);
                if(scale) DrawCameraPivotBox(draw,pivot,length,i,Color(i)); else DrawCameraMoveCone(draw,pivot,length,i,Color(i));
                ProjectCameraHandlePoint(pivot,Add(pivot,d*length),out var tip); draw.AddText(tip+new Vector2(6,-4)*DirectorImGuiTheme.Scale,Color(i),"XYZ"[i].ToString());
            }
            if(!scale) for(int i=0;i<3;i++) DrawCameraMovePlane(draw,pivot,radius,i,Color(i),eye);
        }
        draw.AddCircleFilled(origin,4*DirectorImGuiTheme.Scale,0xff55d9ff);
        cameraHoverAxis=interactive?CameraHandleAxisAt(ImGui.GetMousePos(),9*DirectorImGuiTheme.Scale):-1;
        return cameraHoverAxis;
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
            var m = viewMatrix;
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

    private Vec3d CameraRotationHandlePoint(Vec3d pivot, float radius, int axis,
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
        var m = viewMatrix;
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

}
