using Vintagestory.API.Common;

namespace VSDirector.Runtime;

internal static class DirectorSoundSpatialPolicy
{
    internal static bool KeepListenerRelative(EnumSoundType type)
        => type is EnumSoundType.Music
            or EnumSoundType.MusicGlitchunaffected
            or EnumSoundType.Ambient
            or EnumSoundType.AmbientGlitchunaffected
            or EnumSoundType.Weather;

    internal static bool AnchorRelativeSoundToWorld(bool relativePosition, EnumSoundType type)
        => relativePosition && !KeepListenerRelative(type);

    internal static bool SuppressObserverRelativeSound(
        bool relativePosition,
        EnumSoundType type,
        bool followingNonLocalTarget)
        => followingNonLocalTarget
            && relativePosition
            && !KeepListenerRelative(type);
}
