using System.Numerics;

namespace VSDirector.Data;

/// <summary>Camera editing in world axes. World positions stay in double precision.</summary>
public static class DirectorCameraManipulation
{
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

    public static CameraPreset Rotate(CameraPreset original, Vector3 axis, float radians)
    {
        var result = original.DeepClone();
        if (!Finite(axis) || axis.LengthSquared() < 1e-8f || !float.IsFinite(radians)) return result;
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians);
        var basis = Basis(original);
        Vector3 back = Vector3.Normalize(Vector3.Transform(basis.Back, rotation));
        Vector3 right = Vector3.Normalize(Vector3.Transform(basis.Right, rotation));
        double yaw = Math.Atan2(back.X, back.Z);
        double pitch = Math.Atan2(-back.Y, Math.Sqrt((double)back.X * back.X + (double)back.Z * back.Z));
        // At a vertical view the right vector still defines a stable heading.
        if (Math.Abs(Math.Cos(pitch)) < 1e-6) yaw = Math.Atan2(-right.Z, right.X);
        Vector3 flatRight = new((float)Math.Cos(yaw), 0, (float)-Math.Sin(yaw));
        Vector3 flatUp = Vector3.Cross(back, flatRight);
        result.Yaw = (float)(yaw * 180 / Math.PI);
        result.Pitch = (float)(pitch * 180 / Math.PI);
        result.Roll = (float)(-Math.Atan2(Vector3.Dot(right, flatUp), Vector3.Dot(right, flatRight)) * 180 / Math.PI);
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
    }

    public static CameraPreset WithEulerRotation(CameraPreset original, float pitch, float yaw, float roll)
    {
        var result = original.DeepClone();
        if (!float.IsFinite(pitch) || !float.IsFinite(yaw) || !float.IsFinite(roll)) return result;
        result.Pitch = pitch; result.Yaw = yaw; result.Roll = roll;
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
