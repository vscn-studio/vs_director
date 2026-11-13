using System.Numerics;

namespace VSDirector.Data;

public static class DirectorCreationMath
{
    // Native held-item composition, adapted to row vectors from InGameDevTools
    // VanillaAnimationEditor.PreviewSupport.cs (MIT, see THIRD-PARTY.txt).
    public static Matrix4x4 HeldItemMatrix(CreationVector attachmentPosition, CreationVector attachmentRotation,
        CreationVector origin, CreationVector scale, CreationVector translation, CreationVector rotation)
        => Matrix4x4.CreateTranslation(-origin.Float())
            * CreationTransform.RotationMatrix(attachmentRotation + rotation)
            * Matrix4x4.CreateTranslation((attachmentPosition * (1d / 16) + translation).Float())
            * Matrix4x4.CreateScale(scale.Float()) * Matrix4x4.CreateTranslation(origin.Float());

    public static float[] ProjectView(double[] view, CreationVector origin, double dimensionOffset)
    {
        var result = view.Select(v => (float)v).ToArray();
        double y = origin.Y + dimensionOffset;
        result[12] = (float)(view[0] * origin.X + view[4] * y + view[8] * origin.Z + view[12]);
        result[13] = (float)(view[1] * origin.X + view[5] * y + view[9] * origin.Z + view[13]);
        result[14] = (float)(view[2] * origin.X + view[6] * y + view[10] * origin.Z + view[14]);
        return result;
    }
    // Creation objects use ordinary XYZ rotations. Native cameras use yaw/pitch
    // with a world-up hemisphere; never put those angles directly in an object matrix.
    public static CreationVector CameraRotationFromNative(CameraPreset camera)
    {
        var (right, up, back) = DirectorCameraManipulation.CameraHeadBasis(camera);
        return FromMatrix(new(right.X,right.Y,right.Z,0, up.X,up.Y,up.Z,0,
            back.X,back.Y,back.Z,0, 0,0,0,1)).Rotation;
    }

    public static CreationVector NativeCameraRotation(Matrix4x4 world)
    {
        var back = Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33));
        var right = Vector3.Normalize(new Vector3(world.M11, world.M12, world.M13));
        double yaw = Math.Atan2(back.X, back.Z);
        double pitch = Math.Atan2(-back.Y, Math.Sqrt(back.X * back.X + back.Z * back.Z));
        var flatRight = new Vector3((float)Math.Cos(yaw), 0, (float)-Math.Sin(yaw));
        var flatUp = Vector3.Cross(back, flatRight);
        double roll = -Math.Atan2(Vector3.Dot(right, flatUp), Vector3.Dot(right, flatRight));
        return new(pitch * 180 / Math.PI, yaw * 180 / Math.PI, roll * 180 / Math.PI);
    }

    public static Matrix4x4 BoneMatrix(CreationPart part, CreationTransform pose, int version)
    {
        var origin = part.Origin * (1d / 16) + pose.Pivot;
        var from = part.From * (1d / 16) - origin + pose.Position;
        if (version == 1)
            return CreationTransform.RotationMatrix(pose.Rotation) * Matrix4x4.CreateScale(pose.Scale.Float())
                * Matrix4x4.CreateTranslation(from.Float()) * CreationTransform.RotationMatrix(part.Rotation)
                * Matrix4x4.CreateScale(part.Scale.Float()) * Matrix4x4.CreateTranslation(origin.Float());
        return Matrix4x4.CreateTranslation(from.Float())
            * Matrix4x4.CreateScale(new Vector3((float)(part.Scale.X * pose.Scale.X), (float)(part.Scale.Y * pose.Scale.Y), (float)(part.Scale.Z * pose.Scale.Z)))
            * CreationTransform.RotationMatrix(part.Rotation + pose.Rotation) * Matrix4x4.CreateTranslation(origin.Float());
    }

    // Native v0 rotates around rotationOrigin, before the animated translation;
    // v1 applies the animated rotation at the element's local geometry origin.
    public static Vector3 BonePivot(CreationPart part, CreationTransform pose, int version)
        => version == 1 ? Vector3.Zero : ((part.Origin - part.From) * (1d / 16) + pose.Pivot - pose.Position).Float();

    public static Matrix4x4 BoneHandle(CreationPart part, CreationTransform pose, int version, Matrix4x4 world)
        => Matrix4x4.CreateTranslation(BonePivot(part, pose, version)) * world;

    public static Matrix4x4 BoneFromHandle(CreationPart part, CreationTransform pose, int version, Matrix4x4 handle)
        => Matrix4x4.CreateTranslation(-BonePivot(part, pose, version)) * handle;

    // Recover the authored pose from the manipulated bone-local matrix, including native version ordering.
    public static CreationTransform PoseFromMatrix(CreationPart part, Matrix4x4 local, CreationTransform near, int version)
    {
        var origin = part.Origin * (1d / 16) + near.Pivot;
        var result = near with { };
        if (version == 1)
        {
            var rest = CreationTransform.RotationMatrix(part.Rotation) * Matrix4x4.CreateScale(part.Scale.Float())
                * Matrix4x4.CreateTranslation(origin.Float());
            if (!Matrix4x4.Invert(rest, out var inverse)) throw new InvalidDataException("骨骼基础缩放为零，无法变换。");
            var posed = local * inverse;
            static float Sign(double value) => value < 0 ? -1 : 1;
            var scale = new Vector3(new Vector3(posed.M11, posed.M21, posed.M31).Length() * Sign(near.Scale.X),
                new Vector3(posed.M12, posed.M22, posed.M32).Length() * Sign(near.Scale.Y),
                new Vector3(posed.M13, posed.M23, posed.M33).Length() * Sign(near.Scale.Z));
            if (!Matrix4x4.Invert(Matrix4x4.CreateScale(scale), out var unscale)) throw new InvalidDataException("骨骼缩放为零，无法变换。");
            var rotation = posed; rotation.Translation = Vector3.Zero;
            result.Scale = CreationVector.From(scale);
            result.Rotation = FromMatrix(rotation * unscale, near.Rotation).Rotation;
            result.Position = CreationVector.From(posed.Translation) - (part.From * (1d / 16) - origin);
        }
        else
        {
            var posed = local * Matrix4x4.CreateTranslation(-origin.Float());
            var decomposed = FromMatrix(posed, part.Rotation + near.Rotation,
                new(part.Scale.X * near.Scale.X, part.Scale.Y * near.Scale.Y, part.Scale.Z * near.Scale.Z));
            var linear = posed; linear.Translation = Vector3.Zero;
            if (!Matrix4x4.Invert(linear, out var inverse) || part.Scale.X == 0 || part.Scale.Y == 0 || part.Scale.Z == 0)
                throw new InvalidDataException("骨骼缩放为零，无法变换。");
            result.Position = CreationVector.From(Vector3.TransformNormal(posed.Translation, inverse)) - (part.From * (1d / 16) - origin);
            result.Rotation = decomposed.Rotation - part.Rotation;
            result.Scale = new(decomposed.Scale.X / part.Scale.X, decomposed.Scale.Y / part.Scale.Y, decomposed.Scale.Z / part.Scale.Z);
        }
        return result;
    }

    public static CreationTransform FromMatrix(Matrix4x4 matrix, CreationVector nearRotation = default, CreationVector? nearScale = null)
    {
        if (!Matrix4x4.Decompose(matrix, out var scale, out _, out var position)) throw new InvalidDataException("变换矩阵不可分解。");
        // Decompose may assign a reflection to a different axis, introducing an
        // apparent 180-degree flip. Preserve the authored scale signs while editing.
        if (nearScale is { } signed)
        {
            static float Sign(double value) => value < 0 ? -1 : 1;
            var candidate = new Vector3(new Vector3(matrix.M11, matrix.M12, matrix.M13).Length() * Sign(signed.X),
                new Vector3(matrix.M21, matrix.M22, matrix.M23).Length() * Sign(signed.Y),
                new Vector3(matrix.M31, matrix.M32, matrix.M33).Length() * Sign(signed.Z));
            if (Math.Sign(candidate.X * candidate.Y * candidate.Z) == Math.Sign(matrix.GetDeterminant())) scale = candidate;
        }
        // Keep the original small matrix terms at the poles. A float quaternion round trip
        // destroys these terms and can produce several degrees of error near +/-90 degrees.
        var r = matrix;
        if (scale.X == 0 || scale.Y == 0 || scale.Z == 0) throw new InvalidDataException("变换缩放为零。");
        r.M11 /= scale.X; r.M12 /= scale.X; r.M13 /= scale.X;
        r.M21 /= scale.Y; r.M22 /= scale.Y; r.M23 /= scale.Y;
        r.M31 /= scale.Z; r.M32 /= scale.Z; r.M33 /= scale.Z;
        double cosine = Math.Sqrt(r.M11 * r.M11 + r.M21 * r.M21);
        double y = Math.Atan2(r.M31, cosine), x, z;
        if (cosine > 1e-6) { x = Math.Atan2(-r.M32, r.M33); z = Math.Atan2(-r.M21, r.M11); }
        else { x = nearRotation.X * Math.PI / 180; z = Math.Atan2(r.M12, r.M22) - Math.Sign(y) * x; }
        var primary = new CreationVector(x * 180 / Math.PI, y * 180 / Math.PI, z * 180 / Math.PI);
        static double Nearest(double value, double near) => value + Math.Round((near - value) / 360) * 360;
        CreationVector Near(CreationVector v) => new(Nearest(v.X, nearRotation.X), Nearest(v.Y, nearRotation.Y), Nearest(v.Z, nearRotation.Z));
        var a = Near(primary); var b = Near(new(primary.X + 180, 180 - primary.Y, primary.Z + 180));
        static double Distance(CreationVector v) => v.X*v.X + v.Y*v.Y + v.Z*v.Z;
        return new() { Position = CreationVector.From(position), Scale = CreationVector.From(scale), Rotation = Distance(a - nearRotation) <= Distance(b - nearRotation) ? a : b };
    }
}
