using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VSDirector.Networking;

internal static class DirectorBlockSoundRelay
{
    private const double MaxSoundOffsetFromBlock = 2d;

    private static IClientNetworkChannel? channel;
    private static ILogger? logger;
    private static int sendFailureLogged;

    [ThreadStatic]
    private static int captureDepth;

    [ThreadStatic]
    private static BlockPos? activeBlock;

    internal static void Attach(ICoreClientAPI capi)
    {
        channel = capi.Network.GetChannel(DirectorNetwork.ChannelName);
        logger = capi.Logger;
        Interlocked.Exchange(ref sendFailureLogged, 0);
    }

    internal static void Detach()
    {
        channel = null;
        logger = null;
        captureDepth = 0;
        activeBlock = null;
    }

    internal static void Begin(BlockSelection? selection)
    {
        captureDepth++;
        if (captureDepth == 1)
        {
            activeBlock = selection?.Position?.Copy();
        }
    }

    internal static void End()
    {
        if (captureDepth <= 0)
        {
            captureDepth = 0;
            activeBlock = null;
            return;
        }

        captureDepth--;
        if (captureDepth == 0)
        {
            activeBlock = null;
        }
    }

    internal static void Capture(
        AssetLocation? location,
        double x,
        double y,
        double z,
        float volume,
        float pitch,
        float range,
        EnumSoundType soundType)
    {
        IClientNetworkChannel? currentChannel = channel;
        BlockPos? block = activeBlock;
        if (captureDepth <= 0
            || currentChannel is null
            || block is null
            || location is null
            || !double.IsFinite(x)
            || !double.IsFinite(y)
            || !double.IsFinite(z)
            || !float.IsFinite(volume)
            || !float.IsFinite(pitch)
            || !float.IsFinite(range))
        {
            return;
        }

        double centerX = block.X + 0.5d;
        double centerY = block.InternalY + 0.5d;
        double centerZ = block.Z + 0.5d;
        bool listenerRelative = x == 0d && y == 0d && z == 0d;
        if (listenerRelative)
        {
            x = centerX;
            y = centerY;
            z = centerZ;
        }
        else if (Math.Abs(x - centerX) > MaxSoundOffsetFromBlock
            || Math.Abs(y - centerY) > MaxSoundOffsetFromBlock
            || Math.Abs(z - centerZ) > MaxSoundOffsetFromBlock)
        {
            return;
        }

        try
        {
            currentChannel.SendPacket(new DirectorBlockSoundEvent
            {
                Location = location.ToString(),
                X = x,
                Y = y,
                Z = z,
                Dimension = block.dimension,
                Pitch = pitch,
                Range = range,
                Volume = volume,
                SoundType = (int)soundType,
                BlockX = block.X,
                BlockY = block.InternalY,
                BlockZ = block.Z,
                HasBlockPosition = true
            });
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref sendFailureLogged, 1) == 0)
            {
                logger?.Warning(
                    "VS Director: a local block sound could not be relayed: {0}",
                    exception.GetBaseException().Message);
            }
        }
    }
}

[HarmonyPatch(
    typeof(SystemMouseInWorldInteractions),
    "ContinueBreakSurvival",
    new[] { typeof(BlockSelection), typeof(Block), typeof(float) })]
internal static class DirectorBlockBreakingSoundScopePatch
{
    [HarmonyPrefix]
    private static void Prefix(BlockSelection __0)
        => DirectorBlockSoundRelay.Begin(__0);

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception)
    {
        DirectorBlockSoundRelay.End();
        return __exception;
    }
}
