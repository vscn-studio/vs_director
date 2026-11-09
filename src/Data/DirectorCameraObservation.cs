using System.Numerics;

namespace VSDirector.Data;

/// <summary>An editor-only orbit camera. It never changes the observed camera.</summary>
internal sealed class DirectorCameraObservation
{
    internal float Yaw { get; private set; } = 35;
    internal float Pitch { get; private set; } = -25;
    internal float Distance { get; private set; } = 3;
    internal int Side { get; private set; } = -1;

    // Positive and negative X, Y and Z: east, west, top, bottom, south, north.
    internal void Snap(int side)
    {
        Side = side is >= 0 and < 6 ? side : -1;
        (Yaw, Pitch) = Side switch
        {
            0 => (90, 0), 1 => (-90, 0), 2 => (0, -90),
            3 => (0, 90), 4 => (0, 0), 5 => (180, 0), _ => (35, -25)
        };
    }

    internal void Reset() { Snap(-1); Distance = 3; }

    internal void Orbit(Vector2 delta)
    {
        if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y)) return;
        Yaw = DirectorCameraManipulation.NormalizeEditorAngle(Yaw - delta.X * .4f);
        Pitch = Math.Clamp(Pitch + delta.Y * .4f, -90, 90);
        Side = -1;
    }

    internal void Zoom(float wheel)
    {
        if (float.IsFinite(wheel)) Distance = Math.Clamp(Distance * MathF.Exp(-wheel * .15f), 1, 96);
    }

    internal CameraPreset Eye(CameraPreset subject)
    {
        var eye = new CameraPreset { Yaw = Yaw, Pitch = Pitch, Fov = 50, Dimension = subject.Dimension };
        Vector3 offset = DirectorCameraManipulation.Basis(eye).Back * Distance;
        eye.X = subject.X + offset.X;
        eye.Y = subject.Y + offset.Y;
        eye.Z = subject.Z + offset.Z;
        return eye;
    }

    internal static Vector3 ViewDirection(CameraPreset eye, Vector3 direction)
    {
        var basis = DirectorCameraManipulation.CameraHeadBasis(eye);
        return new(Vector3.Dot(direction, basis.Right), -Vector3.Dot(direction, basis.Up), Vector3.Dot(direction, basis.Back));
    }

    internal static bool Project(CameraPreset eye, double x, double y, double z,
        float aspect, Vector2 min, Vector2 size, out Vector2 screen)
    {
        screen = default;
        var basis = DirectorCameraManipulation.CameraHeadBasis(eye);
        // Subtract before converting to float: editing also works far from world zero.
        double dx = x - eye.X, dy = y - eye.Y, dz = z - eye.Z;
        double depth = -(dx * basis.Back.X + dy * basis.Back.Y + dz * basis.Back.Z);
        if (depth < .01 || !double.IsFinite(depth) || aspect <= 0) return false;
        double focal = 1 / Math.Tan(eye.Fov * Math.PI / 360);
        double px = (dx * basis.Right.X + dy * basis.Right.Y + dz * basis.Right.Z) * focal / (depth * aspect);
        double py = (dx * basis.Up.X + dy * basis.Up.Y + dz * basis.Up.Z) * focal / depth;
        screen = min + new Vector2((float)(px + 1) * size.X / 2, (float)(1 - py) * size.Y / 2);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }
}
