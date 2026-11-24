using System.Numerics;

namespace VSDirector.Data;

/// <summary>A frozen world-space constraint for one pointer gesture.</summary>
public sealed class DirectorCreationDrag
{
    private readonly Matrix4x4 inverse;
    private readonly Vector2 viewport, size;
    private readonly Vector3 pivot, normal, axis, start;
    private readonly bool plane;
    private DirectorCreationDrag(Matrix4x4 inverse, Vector2 viewport, Vector2 size,
        Vector3 pivot, Vector3 normal, Vector3 axis, bool plane, Vector3 start)
    { this.inverse = inverse; this.viewport = viewport; this.size = size; this.pivot = pivot;
        this.normal = normal; this.axis = axis; this.plane = plane; this.start = start; }

    public static DirectorCreationDrag? Begin(Matrix4x4 viewProjection, Vector2 viewport, Vector2 size,
        Vector2 pointer, Vector3 pivot, Vector3 axis, bool plane)
    {
        if (size.X <= 0 || size.Y <= 0 || !Matrix4x4.Invert(viewProjection, out var inverse)) return null;
        axis = Vector3.Normalize(axis);
        if (!Ray(inverse, viewport, size, pointer, out var from, out var direction)) return null;
        // The axis constraint lies in the plane facing the pointer ray. Never
        // divide by a nearly edge-on projection: an end-on axis is not draggable.
        var normal = plane ? axis : direction - axis * Vector3.Dot(direction, axis);
        if (normal.LengthSquared() < .0025f) return null;
        normal = Vector3.Normalize(normal);
        if (!Intersect(from, direction, pivot, normal, out var start)) return null;
        return new(inverse, viewport, size, pivot, normal, axis, plane, start);
    }

    public bool TryOffset(Vector2 pointer, out Vector3 offset)
    {
        offset = default;
        if (!Ray(inverse, viewport, size, pointer, out var from, out var direction)
            || !Intersect(from, direction, pivot, normal, out var hit)) return false;
        var delta = hit - start;
        offset = plane ? delta - normal * Vector3.Dot(delta, normal) : axis * Vector3.Dot(delta, axis);
        return float.IsFinite(offset.X) && float.IsFinite(offset.Y) && float.IsFinite(offset.Z);
    }

    private static bool Ray(Matrix4x4 inverse, Vector2 viewport, Vector2 size, Vector2 pointer,
        out Vector3 from, out Vector3 direction)
    {
        var point = (pointer - viewport) / size;
        var a = Vector4.Transform(new Vector4(point.X * 2 - 1, 1 - point.Y * 2, -1, 1), inverse);
        var b = Vector4.Transform(new Vector4(point.X * 2 - 1, 1 - point.Y * 2, .5f, 1), inverse);
        from = default; direction = default;
        if (Math.Abs(a.W) < 1e-8f || Math.Abs(b.W) < 1e-8f) return false;
        from = new Vector3(a.X, a.Y, a.Z) / a.W;
        direction = new Vector3(b.X, b.Y, b.Z) / b.W - from;
        if (direction.LengthSquared() < 1e-12f) return false;
        direction = Vector3.Normalize(direction);
        // Overlays remain visible between the eye and the scene's near plane.
        // Start perspective picking at the eye so those handles can be dragged
        // too; an orthographic ray still starts on its parallel near plane.
        var eye = Vector4.Transform(new Vector4(0, 0, 1, 0), inverse);
        if (Math.Abs(eye.W) > 1e-8f)
            from = new Vector3(eye.X, eye.Y, eye.Z) / eye.W;
        return true;
    }
    private static bool Intersect(Vector3 from, Vector3 ray, Vector3 pivot, Vector3 normal, out Vector3 hit)
    {
        hit = default;
        float denominator = Vector3.Dot(ray, normal);
        if (Math.Abs(denominator) < .025f) return false;
        float distance = Vector3.Dot(pivot - from, normal) / denominator;
        if (!float.IsFinite(distance) || distance < 0) return false;
        hit = from + ray * distance;
        return true;
    }
}
