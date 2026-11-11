using System.Numerics;
namespace VSDirector.Gui;
internal static class DirectorEditorGeometry
{
    internal static readonly (Vector3 A, Vector3 B)[] CameraOutline = CreateCameraOutline();
    internal static (Vector3 A, Vector3 B)[] CreateCameraOutline()
    {
        // Simple camera body, flared lens and two film reels, facing local -Z.
        // Keep only the recognisable silhouette within a one-block diameter.
        var lines = new List<(Vector3 A, Vector3 B)>();
        Vector3[] Rectangle(float x, float y, float z) =>
            [new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z)];
        void JoinRings(Vector3[] a, Vector3[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                lines.Add((a[i], a[(i + 1) % a.Length]));
                lines.Add((b[i], b[(i + 1) % b.Length]));
                lines.Add((a[i], b[i]));
            }
        }
        void Box(Vector3 min, Vector3 max)
        {
            Vector3[] Face(float z) => [new(min.X, min.Y, z), new(max.X, min.Y, z),
                new(max.X, max.Y, z), new(min.X, max.Y, z)];
            JoinRings(Face(min.Z), Face(max.Z));
        }
        Box(new(-.18f, -.16f, -.12f), new(.18f, .16f, .28f));
        JoinRings(Rectangle(.09f, .07f, -.12f), Rectangle(.17f, .13f, -.42f));
        const int reelSegments = 12;
        foreach (float z in new[] { -.045f, .185f })
        for (int i = 0; i < reelSegments; i++)
        {
            Vector3 At(float angle) => new(0, .255f + .10f * MathF.Cos(angle), z + .10f * MathF.Sin(angle));
            lines.Add((At(i * MathF.Tau / reelSegments), At((i + 1) * MathF.Tau / reelSegments)));
        }
        return lines.ToArray();
    }

}
