using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using VSDirector.Data;
using VSDirector.Replay;

namespace VSDirector.Runtime;

internal static class DirectorEntityViewResolver
{
    private const double NativePlayerPitchTolerance = 0.01d;

    internal static bool TryResolveHeadPosition(
        Entity entity,
        out double x,
        out double y,
        out double z)
    {
        if (entity.SelectionBox is { } box)
        {
            double height = Math.Max(0.1d, box.Y2 - box.Y1);
            double eyeY = entity.LocalEyePos.Y;
            x = entity.Pos.X + (box.X1 + box.X2) * 0.5d;
            z = entity.Pos.Z + (box.Z1 + box.Z2) * 0.5d;
            y = entity.Pos.Y + (double.IsFinite(eyeY)
                && eyeY >= box.Y1 - 0.05d
                && eyeY <= box.Y2 + 0.05d
                    ? eyeY
                    : box.Y1 + height * 0.86d);
        }
        else
        {
            x = entity.Pos.X + entity.LocalEyePos.X;
            y = entity.Pos.Y + entity.LocalEyePos.Y;
            z = entity.Pos.Z + entity.LocalEyePos.Z;
        }

        return double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z);
    }

    internal static double ResolveHeadYaw(Entity entity)
    {
        if (entity is EntityPlayer player)
        {
            if (TryResolveNativePlayerPitch(player.Pos.Pitch, out _))
            {
                // Native player positions already store the camera-facing yaw.
                return player.Pos.Yaw;
            }

            // Actor-path and action-preview players store model yaw one
            // half-turn away from the camera-facing convention.
            return player.Pos.Yaw + player.Pos.HeadYaw + Math.PI;
        }

        double modelYaw = entity.Pos.Yaw;
        double headYaw = entity is EntityAgent
            ? entity.Pos.HeadYaw
            : 0d;
        return DirectorEntityViewMath.ToCameraYaw(
            modelYaw,
            headYaw,
            ResolveEntityShapeYawOffset(entity));
    }

    internal static double ResolveCameraYaw(Entity entity)
    {
        if (entity is EntityPlayer player
            && TryResolveNativePlayerPitch(player.Pos.Pitch, out _))
        {
            // Native player yaw points in the opposite half-turn from the
            // zero-centred camera pose used by follow/track evaluation.
            return entity.Pos.Yaw + Math.PI;
        }

        return entity is EntityAgent
            ? DirectorEntityViewMath.ToCameraYaw(
                entity.Pos.Yaw,
                entity.Pos.HeadYaw,
                ResolveEntityShapeYawOffset(entity))
            : DirectorEntityViewMath.ToCameraYaw(
                entity.Pos.Yaw,
                shapeYawOffset: ResolveEntityShapeYawOffset(entity));
    }

    internal static double ResolveCameraPitch(Entity entity)
    {
        if (entity is EntityPlayer player)
        {
            if (TryResolveNativePlayerPitch(player.Pos.Pitch, out _))
            {
                return DirectorEntityViewMath.ToPlayerRelativePitch(player.Pos.Pitch);
            }

            // Synthetic actor-path samples store the camera-relative pitch in
            // HeadPitch and leave the native player pitch at zero.
            return player.Pos.HeadPitch;
        }

        return entity is EntityAgent
            ? entity.Pos.HeadPitch
            : entity.Pos.Pitch;
    }

    internal static double ResolveBodyYaw(Entity entity)
    {
        return entity is EntityAgent agent ? agent.BodyYaw : entity.Pos.Yaw;
    }

    internal static double ResolveHeadPitch(Entity entity)
    {
        if (entity is EntityPlayer player)
        {
            if (TryResolveNativePlayerPitch(player.Pos.Pitch, out _))
            {
                // The arrow renderer consumes the native PI-centred player
                // pitch directly; converting it to a zero-centred angle
                // would mirror the view.
                return player.Pos.Pitch;
            }

            // Synthetic actor-path samples already use the camera-relative
            // pitch field expected by the look-direction mesh.
            return player.Pos.HeadPitch;
        }

        return entity is EntityAgent ? entity.Pos.HeadPitch : entity.Pos.Pitch;
    }

    private static bool TryResolveNativePlayerPitch(double nativePitch, out double relativePitch)
    {
        relativePitch = double.NaN;
        if (!double.IsFinite(nativePitch))
        {
            return false;
        }

        relativePitch = DirectorEntityViewMath.ToPlayerRelativePitch(nativePitch);
        return double.IsFinite(relativePitch)
            && Math.Abs(relativePitch) <= Math.PI / 2d + NativePlayerPitchTolerance;
    }

    private static double ResolveEntityShapeYawOffset(Entity entity)
        => DirectorEntityViewMath.EntityShapeYawOffset(entity.Code?.Path);
}
