using System.Numerics;

namespace VSDirector.Data;

/// <summary>Camera editing in world axes. World positions stay in double precision.</summary>
public static class DirectorCameraManipulation
{
    /// <summary>One signed turn for camera editing; path/animation winding is left intact.</summary>
    public static float NormalizeEditorAngle(double degrees)
    {
        if (!double.IsFinite(degrees)) return 0;
        double angle = degrees % 360;
        if (angle >= 180) angle -= 360;
        if (angle < -180) angle += 360;
        float rounded = (float)angle;
        // Rounding to the camera's float storage can reach the upper boundary.
        return rounded >= 180 ? -180 : rounded == 0 ? 0 : rounded;
    }

    public static (Vector3 Right, Vector3 Up, Vector3 Back) Basis(CameraPreset camera)
    {
        double yaw = camera.Yaw * Math.PI / 180, pitch = camera.Pitch * Math.PI / 180;
        Vector3 right = new((float)Math.Cos(yaw), 0, (float)-Math.Sin(yaw));
        Vector3 up = new((float)(Math.Sin(yaw) * Math.Sin(pitch)), (float)Math.Cos(pitch),
            (float)(Math.Cos(yaw) * Math.Sin(pitch)));
        Vector3 back = Vector3.Cross(right, up);
        float roll = camera.Roll * MathF.PI / 180;
        return (right * MathF.Cos(roll) - up * MathF.Sin(roll),
            right * MathF.Sin(roll) + up * MathF.Cos(roll), back);
    }

    public static (Vector3 Right, Vector3 Up, Vector3 Back) CameraHeadBasis(CameraPreset camera)
    {
        var basis = Basis(camera);
        // Native shooting views use world-up before applying roll. The other
        // Euler pitch hemisphere describes the same sightline with opposite axes.
        return Math.Cos(camera.Pitch * Math.PI / 180d) < 0
            ? (-basis.Right, -basis.Up, basis.Back) : basis;
    }

    public static CameraPreset Translate(CameraPreset original, Vector3 offset, bool pivotOnly)
    {
        var result = original.DeepClone();
        if (!Finite(offset)) return result;
        if (pivotOnly)
        {
            result.EditorPivotOffsetX += offset.X;
            result.EditorPivotOffsetY += offset.Y;
            result.EditorPivotOffsetZ += offset.Z;
        }
        else { result.X += offset.X; result.Y += offset.Y; result.Z += offset.Z; }
        return result;
    }

    public static CameraPreset Rotate(CameraPreset original, Vector3 axis, float radians, CameraPreset? angleReference = null)
    {
        var result = original.DeepClone();
        if (!Finite(axis) || axis.LengthSquared() < 1e-8f || !float.IsFinite(radians)) return result;
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians);
        var basis = Basis(original);
        Vector3 back = Vector3.Normalize(Vector3.Transform(basis.Back, rotation));
        Vector3 right = Vector3.Normalize(Vector3.Transform(basis.Right, rotation));
        double yaw = Math.Atan2(back.X, back.Z);
        double pitch = Math.Atan2(-back.Y, Math.Sqrt((double)back.X * back.X + (double)back.Z * back.Z));
        // At a vertical view yaw and roll are coupled. Retain the heading and
        // recover the remaining rotation from the right vector.
        var reference = angleReference ?? original;
        if (Math.Abs(Math.Cos(pitch)) < 1e-6) yaw = reference.Yaw * Math.PI / 180d;
        Vector3 flatRight = new((float)Math.Cos(yaw), 0, (float)-Math.Sin(yaw));
        Vector3 flatUp = Vector3.Cross(back, flatRight);
        double roll = -Math.Atan2(Vector3.Dot(right, flatUp), Vector3.Dot(right, flatRight)) * 180 / Math.PI;
        double pitchDegrees = pitch * 180 / Math.PI, yawDegrees = yaw * 180 / Math.PI;
        // Both Euler branches describe the same full camera frame. Keep the
        // closest branch before wrapping each component by whole turns. A tiny
        // drag must not select another branch with a compensating 180-degree roll.
        var first = NearReference(pitchDegrees, yawDegrees, roll);
        var second = NearReference(180 - pitchDegrees, yawDegrees + 180, roll + 180);
        var angles = Distance(first) <= Distance(second) ? first : second;
        result.Pitch = NormalizeEditorAngle(angles.Pitch);
        result.Yaw = NormalizeEditorAngle(angles.Yaw);
        result.Roll = NormalizeEditorAngle(angles.Roll);
        Vector3 oldOffset = new((float)original.EditorPivotOffsetX, (float)original.EditorPivotOffsetY,
            (float)original.EditorPivotOffsetZ);
        Vector3 newOffset = Vector3.Transform(oldOffset, rotation);
        result.X += oldOffset.X - newOffset.X;
        result.Y += oldOffset.Y - newOffset.Y;
        result.Z += oldOffset.Z - newOffset.Z;
        result.EditorPivotOffsetX = newOffset.X;
        result.EditorPivotOffsetY = newOffset.Y;
        result.EditorPivotOffsetZ = newOffset.Z;
        return result;

        (double Pitch, double Yaw, double Roll) NearReference(double p, double y, double r)
            => (Unwrap(p, reference.Pitch), Unwrap(y, reference.Yaw), Unwrap(r, reference.Roll));
        static double Unwrap(double value, double target) => value + 360 * Math.Round((target - value) / 360);
        double Distance((double Pitch, double Yaw, double Roll) value)
            => Math.Pow(value.Pitch - reference.Pitch, 2) + Math.Pow(value.Yaw - reference.Yaw, 2)
                + Math.Pow(value.Roll - reference.Roll, 2);
    }

    public static CameraPreset WithEulerRotation(CameraPreset original, float pitch, float yaw, float roll)
    {
        var result = original.DeepClone();
        if (!float.IsFinite(pitch) || !float.IsFinite(yaw) || !float.IsFinite(roll)) return result;
        result.Pitch = NormalizeEditorAngle(pitch);
        result.Yaw = NormalizeEditorAngle(yaw);
        result.Roll = NormalizeEditorAngle(roll);
        var before = Basis(original);
        var after = Basis(result);
        // Rotate the offset with the same change of basis as the camera head.
        double x = original.EditorPivotOffsetX, y = original.EditorPivotOffsetY, z = original.EditorPivotOffsetZ;
        double rx = x * before.Right.X + y * before.Right.Y + z * before.Right.Z;
        double uy = x * before.Up.X + y * before.Up.Y + z * before.Up.Z;
        double bz = x * before.Back.X + y * before.Back.Y + z * before.Back.Z;
        result.EditorPivotOffsetX = rx * after.Right.X + uy * after.Up.X + bz * after.Back.X;
        result.EditorPivotOffsetY = rx * after.Right.Y + uy * after.Up.Y + bz * after.Back.Y;
        result.EditorPivotOffsetZ = rx * after.Right.Z + uy * after.Up.Z + bz * after.Back.Z;
        result.X += x - result.EditorPivotOffsetX;
        result.Y += y - result.EditorPivotOffsetY;
        result.Z += z - result.EditorPivotOffsetZ;
        return result;
    }

    /// <summary>Signed distance along an axis at the closest point to a pointer ray.</summary>
    public static bool TryAxisParameter(Vector3 rayOrigin, Vector3 rayDirection, Vector3 axis, out float parameter)
    {
        parameter = 0;
        if (!Finite(rayOrigin) || !Finite(rayDirection) || !Finite(axis)
            || rayDirection.LengthSquared() < 1e-8f || axis.LengthSquared() < 1e-8f) return false;
        rayDirection = Vector3.Normalize(rayDirection);
        axis = Vector3.Normalize(axis);
        float dot = Vector3.Dot(rayDirection, axis), denominator = 1 - dot * dot;
        if (denominator < 1e-5f) return false;
        parameter = (Vector3.Dot(rayOrigin, axis) - dot * Vector3.Dot(rayOrigin, rayDirection)) / denominator;
        return float.IsFinite(parameter);
    }

    public static bool TryPlaneDirection(Vector3 rayOrigin, Vector3 rayDirection, Vector3 normal, out Vector3 direction)
    {
        direction = default;
        if (!TryPlanePoint(rayOrigin, rayDirection, normal, out var point) || point.LengthSquared() < 1e-8f) return false;
        direction = Vector3.Normalize(point);
        return Finite(direction);
    }

    /// <summary>Pointer intersection with the plane through the editing pivot, in pivot-relative coordinates.</summary>
    public static bool TryPlanePoint(Vector3 rayOrigin, Vector3 rayDirection, Vector3 normal, out Vector3 point)
    {
        point = default;
        float denominator = Vector3.Dot(rayDirection, normal);
        if (!Finite(rayOrigin) || !Finite(rayDirection) || !Finite(normal) || Math.Abs(denominator) < 1e-6f) return false;
        float distance = -Vector3.Dot(rayOrigin, normal) / denominator;
        if (!float.IsFinite(distance) || distance < 0) return false;
        point = rayOrigin + rayDirection * distance;
        return Finite(point);
    }

    private static bool Finite(Vector3 vector)
        => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
