using System.Numerics;
using System.Text.Json;
using VSDirector.Data;

internal static class CameraManipulationChecks
{
    internal static void Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < .0001f;
        var camera = new CameraPreset { X = 30000000.125, Y = 120, Z = -30000000.25,
            Yaw = 37, Pitch = -24, Roll = 19, Fov = 70 };
        var zero = DirectorCameraManipulation.Basis(new());
        Check(Near(zero.Right, Vector3.UnitX) && Near(zero.Up, Vector3.UnitY) && Near(zero.Back, Vector3.UnitZ),
            "Camera basis disagrees with native camera axes.");
        var movedPivot = DirectorCameraManipulation.Translate(camera, new(2, -1, 3), true);
        Check(movedPivot.X == camera.X && movedPivot.Y == camera.Y && movedPivot.Z == camera.Z
            && movedPivot.Yaw == camera.Yaw && movedPivot.Pitch == camera.Pitch && movedPivot.Roll == camera.Roll,
            "Moving an editor pivot changed the shot.");
        Check(camera.EditorPivotOffsetX == 0, "Pivot edit mutated its input.");
        var moved = DirectorCameraManipulation.Translate(movedPivot, new(.25f, -.5f, .75f), false);
        Check(moved.X == camera.X + .25 && moved.Y == camera.Y - .5 && moved.Z == camera.Z + .75
            && moved.EditorPivotOffsetZ == 3, "Moving the camera lost world precision or its pivot offset.");
        foreach (float pitch in new[] { -90f, -89.99f, -24f, 0f, 89.99f, 90f, 120f })
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(new(1, 2, 3)) })
        foreach (float angle in new[] { 0f, .73f, -2.1f, MathF.PI })
        {
            var original = movedPivot.DeepClone(); original.Pitch = pitch;
            var before = DirectorCameraManipulation.Basis(original);
            var edited = DirectorCameraManipulation.Rotate(original, axis, angle);
            var after = DirectorCameraManipulation.Basis(edited);
            var rotation = Quaternion.CreateFromAxisAngle(axis, angle);
            Check(Near(after.Right, Vector3.Transform(before.Right, rotation))
                && Near(after.Up, Vector3.Transform(before.Up, rotation))
                && Near(after.Back, Vector3.Transform(before.Back, rotation)), $"World rotation changed camera orientation incorrectly: pitch={pitch}, axis={axis}, angle={angle}.");
            Check(Math.Abs(edited.X + edited.EditorPivotOffsetX - original.X - original.EditorPivotOffsetX) < 1e-6
                && Math.Abs(edited.Y + edited.EditorPivotOffsetY - original.Y - original.EditorPivotOffsetY) < 1e-6
                && Math.Abs(edited.Z + edited.EditorPivotOffsetZ - original.Z - original.EditorPivotOffsetZ) < 1e-6,
                "Rotation moved the world pivot.");
        }
        var euler = DirectorCameraManipulation.WithEulerRotation(movedPivot, 10, 90, -30);
        Check(euler.Pitch == 10 && euler.Yaw == 90 && euler.Roll == -30,
            "Numeric Euler editing changed the requested angles.");
        Check(Math.Abs(euler.X + euler.EditorPivotOffsetX - movedPivot.X - movedPivot.EditorPivotOffsetX) < 1e-6
            && Math.Abs(euler.Y + euler.EditorPivotOffsetY - movedPivot.Y - movedPivot.EditorPivotOffsetY) < 1e-6
            && Math.Abs(euler.Z + euler.EditorPivotOffsetZ - movedPivot.Z - movedPivot.EditorPivotOffsetZ) < 1e-6,
            "Numeric rotation moved the world pivot.");
        var roundTrip = DirectorCameraManipulation.WithEulerRotation(euler, movedPivot.Pitch, movedPivot.Yaw, movedPivot.Roll);
        Check(Math.Abs(roundTrip.X - movedPivot.X) < 1e-6 && Math.Abs(roundTrip.Y - movedPivot.Y) < 1e-6
            && Math.Abs(roundTrip.Z - movedPivot.Z) < 1e-6,
            "Numeric rotation round trip lost the original camera position.");
        var invalid = DirectorCameraManipulation.WithEulerRotation(movedPivot, float.NaN, 0, 0);
        Check(invalid.X == movedPivot.X && invalid.Pitch == movedPivot.Pitch && invalid.Yaw == movedPivot.Yaw,
            "Invalid numeric angles corrupted the camera.");
        var quarterTurn = DirectorCameraManipulation.WithEulerRotation(new() { EditorPivotOffsetX = 2 }, 0, 90, 0);
        Check(Math.Abs(quarterTurn.X - 2) < 1e-6 && Math.Abs(quarterTurn.Z - 2) < 1e-6,
            "Numeric yaw rotates around the wrong axis or pivot.");
        var restored = JsonSerializer.Deserialize<CameraPreset>(JsonSerializer.Serialize(movedPivot))!;
        Check(restored.DeepClone().EditorPivotOffsetX == 2 && restored.EditorPivotOffsetY == -1
            && restored.EditorPivotOffsetZ == 3, "Camera pivot was lost during save or clone.");
        var timeline = DirectorReplayViewportTimeline.Normalize(new()
        {
            Clips = [new() { SourceType = DirectorReplayViewportSourceType.StaticCamera,
                CameraPose = movedPivot, TimelineStartSeconds = 0, DurationSeconds = 1 }]
        }, 10);
        var savedTimeline = JsonSerializer.Deserialize<DirectorReplayViewportTimeline>(JsonSerializer.Serialize(timeline.DeepClone()))!;
        var savedPose = DirectorReplayViewportTimeline.Normalize(savedTimeline, 10).Clips.Single().CameraPose!;
        Check(savedPose.EditorPivotOffsetX == 2 && savedPose.EditorPivotOffsetZ == 3,
            "Replay scene normalization or save lost the camera pivot.");
        var savedCamera = CameraLibrary.Normalize(new() { Cameras = [movedPivot] }).Cameras.Single();
        Check(savedCamera.EditorPivotOffsetX == 2 && savedCamera.EditorPivotOffsetZ == 3,
            "Library normalization lost the camera pivot.");
        Check(DirectorCameraManipulation.TryAxisParameter(new(3, 4, 8), -Vector3.UnitZ, Vector3.UnitX, out float distance)
            && Math.Abs(distance - 3) < 1e-5, "Axis drag does not follow the pointer ray.");
        Check(!DirectorCameraManipulation.TryAxisParameter(Vector3.One, Vector3.UnitX, Vector3.UnitX, out _),
            "End-on axis drag is unstable.");
        Check(DirectorCameraManipulation.TryPlaneDirection(new(1, 0, 8), -Vector3.UnitZ, Vector3.UnitZ, out var direction)
            && Near(direction, Vector3.UnitX), "Rotation ring intersection is incorrect.");
        Check(!DirectorCameraManipulation.TryPlaneDirection(new(1, 0, 8), Vector3.UnitZ, Vector3.UnitZ, out _)
            && !DirectorCameraManipulation.TryPlaneDirection(new(1, 0, 8), Vector3.UnitX, Vector3.UnitZ, out _),
            "Rotation ring accepts an intersection behind the camera or a parallel ray.");
        Check(DirectorCameraManipulation.TryPlanePoint(new(0, 0, 8), -Vector3.UnitZ, Vector3.UnitZ, out var point)
            && Near(point, Vector3.Zero), "Moving through the plane pivot incorrectly rejects a zero offset.");
        Check(!DirectorCameraManipulation.TryPlanePoint(Vector3.One, Vector3.UnitX, Vector3.UnitY, out _)
            && !DirectorCameraManipulation.TryPlanePoint(Vector3.One, Vector3.UnitY, Vector3.UnitY, out _)
            && !DirectorCameraManipulation.TryPlanePoint(Vector3.One, -Vector3.UnitY, new(float.NaN, 1, 0), out _),
            "Plane movement accepts a parallel, backward or non-finite pointer intersection.");
    }
}
