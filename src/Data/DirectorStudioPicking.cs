using System.Numerics;

namespace VSDirector.Data;

public static class DirectorStudioPicking
{
    // VS matrices are column-major. System.Numerics uses row vectors; the same
    // flat values therefore represent the transposed matrix needed here.
    public static Matrix4x4 Matrix(float[] m) => new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
    public static bool Ray(double x, double y, float[] view, float[] projection, out Vector3 origin, out Vector3 direction)
    {
        origin = direction = default;
        if (!Matrix4x4.Invert(Matrix(view) * Matrix(projection), out var inverse)) return false;
        var near = Vector4.Transform(new Vector4((float)(x * 2 - 1), (float)(1 - y * 2), -1, 1), inverse);
        var far = Vector4.Transform(new Vector4((float)(x * 2 - 1), (float)(1 - y * 2), 1, 1), inverse);
        if (Math.Abs(near.W) < 1e-9 || Math.Abs(far.W) < 1e-9) return false;
        origin = new(near.X / near.W, near.Y / near.W, near.Z / near.W);
        direction = Vector3.Normalize(new Vector3(far.X / far.W, far.Y / far.W, far.Z / far.W) - origin);
        return float.IsFinite(direction.X) && float.IsFinite(direction.Y) && float.IsFinite(direction.Z);
    }
    public static double? Triangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a; var e2 = c - a; var cross = Vector3.Cross(direction, e2);
        double determinant = Vector3.Dot(e1, cross);
        if (Math.Abs(determinant) < 1e-9) return null;
        double u = Vector3.Dot(origin - a, cross) / determinant;
        if (u < 0 || u > 1) return null;
        var q = Vector3.Cross(origin - a, e1); double v = Vector3.Dot(direction, q) / determinant;
        if (v < 0 || u + v > 1) return null;
        double t = Vector3.Dot(e2, q) / determinant; return t >= 0 ? t : null;
    }
}
