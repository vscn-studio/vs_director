using System.Numerics;
using System.Text.Json;
using VSDirector.Data;

internal static class CameraObservationChecks
{
    internal static void Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var orbit = new DirectorCameraObservation();
        var subject = new CameraPreset { X = 30000000.125, Y = 120.25, Z = -30000000.875,
            Dimension = 2, Pitch = 200, Yaw = 100, Roll = 25, Fov = 70, EditorPivotOffsetX = 10 };
        string original = JsonSerializer.Serialize(subject);
        Vector3[] directions = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        Vector2 min = new(37, 55), size = new(480, 270);
        for (int side = -1; side < 6; side++)
        {
            orbit.Snap(side);
            var eye = orbit.Eye(subject);
            Check(eye.Dimension == 2 && eye.Roll == 0 && eye.Fov == 50, "Observer inherited shooting rotation or lost dimension.");
            if (side >= 0)
            {
                var offset = new Vector3((float)(eye.X - subject.X), (float)(eye.Y - subject.Y), (float)(eye.Z - subject.Z));
                Check(Vector3.Distance(offset, directions[side] * 3) < .00001, "Cardinal observer is on the wrong side.");
            }
            bool Project(double x, double y, double z, out Vector2 screen)
                => DirectorCameraObservation.Project(eye, x, y, z, 16f / 9, min, size, out screen);
            Check(Project(subject.X, subject.Y, subject.Z, out var center)
                && Vector2.Distance(center, min + size / 2) < .001, "Observer did not center its subject at a pole/cardinal/large position.");
            var basis = DirectorCameraManipulation.CameraHeadBasis(eye);
            Check(Project(subject.X + basis.Right.X, subject.Y + basis.Right.Y, subject.Z + basis.Right.Z, out var right)
                && right.X > center.X && Math.Abs(right.Y - center.Y) < .001, "Observer image mirrored horizontally.");
            Check(Project(subject.X + basis.Up.X, subject.Y + basis.Up.Y, subject.Z + basis.Up.Z, out var up)
                && up.Y < center.Y && Math.Abs(up.X - center.X) < .001, "Observer image inverted vertically.");
            Check(!Project(eye.X + basis.Back.X, eye.Y + basis.Back.Y, eye.Z + basis.Back.Z, out _), "Observer projected behind-eye points.");
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 projected = DirectorCameraObservation.ViewDirection(eye, directions[axis * 2]);
                Project(subject.X + directions[axis * 2].X * .2, subject.Y + directions[axis * 2].Y * .2,
                    subject.Z + directions[axis * 2].Z * .2, out var axisScreen);
                Vector2 delta = axisScreen - center;
                Check(Math.Abs(delta.X * projected.Y - delta.Y * projected.X) < .001,
                    "Orientation widget does not agree with the rendered world axes.");
            }
        }
        orbit.Orbit(new(10000, 10000));
        Check(orbit.Side == -1 && orbit.Yaw >= -180 && orbit.Yaw < 180 && orbit.Pitch == 90, "Orbit accumulated angles or flipped over a pole.");
        orbit.Orbit(new(0, -10000));
        Check(orbit.Pitch == -90, "Orbit lower pole was not clamped.");
        orbit.Zoom(10000); Check(orbit.Distance == 1, "Observer could enter its subject.");
        orbit.Zoom(-10000); Check(orbit.Distance == 96, "Observer zoom exceeded its range.");
        orbit.Orbit(new(float.NaN)); orbit.Zoom(float.NaN);
        Check(float.IsFinite(orbit.Yaw) && float.IsFinite(orbit.Distance), "Invalid input corrupted observer camera.");
        orbit.Reset(); Check(orbit.Side == -1 && orbit.Distance == 3, "Reset did not frame the camera again.");
        Check(JsonSerializer.Serialize(subject) == original, "Observation changed the shooting camera or pivot.");
    }
}
