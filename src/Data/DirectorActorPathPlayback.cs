namespace VSDirector.Data;

public static class DirectorActorPathPlayback
{
    private const double DefaultPlayerEyeHeight = 1.62d;
    private const double DefaultEntityEyeHeight = 0.9d;
    private const double DegreesToRadians = Math.PI / 180d;

    public static double ResolvePlaybackTime(
        CameraPath path,
        Guid activeReplayId,
        double replayTimeSeconds,
        double localTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(path);
        double local = double.IsFinite(localTimeSeconds) ? localTimeSeconds : 0d;
        if (path.SourceReplayId == Guid.Empty || path.SourceReplayId != activeReplayId)
        {
            return local;
        }

        double replayTime = double.IsFinite(replayTimeSeconds) ? replayTimeSeconds : 0d;
        double offset = double.IsFinite(path.ReplayTimeOffsetSeconds)
            ? Math.Max(0d, path.ReplayTimeOffsetSeconds)
            : 0d;
        return replayTime - offset;
    }

    public static DirectorReplayActorSample Apply(
        DirectorReplayActorSample source,
        CameraPath path,
        CameraKeyframe frame,
        CameraKeyframe? motionFrame,
        double motionDeltaSeconds,
        bool playerActor)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(frame);
        DirectorReplayActorSample result = source.DeepClone();
        double eyeHeight = ResolveEyeHeight(path, playerActor);
        result.X = frame.X;
        result.Y = frame.Y - eyeHeight;
        result.Z = frame.Z;
        result.Dimension = frame.Dimension;

        float yaw = NormalizeRadians((float)(frame.Yaw * DegreesToRadians));
        if (path.ActorYawConvention == ActorPathYawConvention.CameraFacing)
        {
            yaw = NormalizeRadians(yaw - MathF.PI);
        }
        float pitch = NormalizeRadians((float)(frame.Pitch * DegreesToRadians));
        float roll = NormalizeRadians((float)(frame.Roll * DegreesToRadians));
        result.Yaw = yaw;
        result.Roll = roll;
        result.BodyYaw = yaw;
        result.BodyYawServer = yaw;
        result.WalkYaw = yaw;
        result.WalkPitch = 0f;
        result.HeadYaw = 0f;
        if (playerActor)
        {
            result.Pitch = 0f;
            result.HeadPitch = pitch;
            result.HasThirdPersonPose = true;
            result.ThirdPersonBodyYaw = yaw;
            result.ThirdPersonHeadYaw = 0f;
            result.ThirdPersonHeadPitch = pitch;
        }
        else
        {
            result.Pitch = pitch;
            result.HeadPitch = 0f;
        }

        if (motionFrame is not null
            && double.IsFinite(motionDeltaSeconds)
            && Math.Abs(motionDeltaSeconds) > 0.000001d)
        {
            result.MotionX = (motionFrame.X - frame.X) / motionDeltaSeconds;
            result.MotionY = (motionFrame.Y - frame.Y) / motionDeltaSeconds;
            result.MotionZ = (motionFrame.Z - frame.Z) / motionDeltaSeconds;
            result.HasMotion = double.IsFinite(result.MotionX)
                && double.IsFinite(result.MotionY)
                && double.IsFinite(result.MotionZ);
        }
        else
        {
            result.MotionX = 0d;
            result.MotionY = 0d;
            result.MotionZ = 0d;
            result.HasMotion = false;
        }
        return result;
    }

    private static double ResolveEyeHeight(CameraPath path, bool playerActor)
    {
        if (path.ActorEyeHeightOffset is double stored && double.IsFinite(stored))
        {
            return stored;
        }
        return string.IsNullOrWhiteSpace(path.SourceActorKey)
            ? 0d
            : playerActor
                ? DefaultPlayerEyeHeight
                : DefaultEntityEyeHeight;
    }

    private static float NormalizeRadians(float radians)
    {
        float wrapped = radians % (MathF.PI * 2f);
        if (wrapped > MathF.PI)
        {
            wrapped -= MathF.PI * 2f;
        }
        else if (wrapped < -MathF.PI)
        {
            wrapped += MathF.PI * 2f;
        }
        return wrapped;
    }
}
