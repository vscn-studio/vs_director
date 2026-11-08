using System.Collections;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client;
using VSDirector.Data;

namespace VSDirector.Runtime;

internal static class DirectorFollowSoundIsolation
{
    private static readonly FieldInfo? LoadedSoundsField = typeof(LoadedSoundNative)
        .GetField("loadedSounds", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo? LoadedSoundsLockField = typeof(LoadedSoundNative)
        .GetField("loadedSoundsLock", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly HashSet<LoadedSoundNative> pausedObserverSounds =
        new(ReferenceEqualityComparer.Instance);
    [ThreadStatic]
    private static bool restoringObserverSounds;

    internal static bool ShouldSuppressObserverRelativeSound(
        double x,
        double y,
        double z,
        EnumSoundType soundType)
        => !restoringObserverSounds
            && DirectorSoundSpatialPolicy.SuppressObserverRelativeSound(
                x == 0d && y == 0d && z == 0d,
                soundType,
                DirectorCameraRuntime.Controller?.IsFollowingNonLocalTarget == true);

    internal static bool ShouldSuppressObserverRelativeSound(SoundParams? parameters)
        => !restoringObserverSounds
            && parameters is not null
            && DirectorSoundSpatialPolicy.SuppressObserverRelativeSound(
                parameters.RelativePosition,
                parameters.SoundType,
                DirectorCameraRuntime.Controller?.IsFollowingNonLocalTarget == true);

    internal static float DistanceSquaredFromActiveListener(
        EntityPos observer,
        double x,
        double y,
        double z)
    {
        if (DirectorCameraRuntime.Controller is not
            {
                IsFollowingNonLocalTarget: true,
                CurrentPose: { } camera
            })
        {
            return observer.SquareDistanceTo(x, y, z);
        }

        double dx = camera.X - x;
        double dy = DirectorChunkWindow.ToAbsoluteY(camera.Y, camera.Dimension) - y;
        double dz = camera.Z - z;
        return (float)Math.Min(float.MaxValue, dx * dx + dy * dy + dz * dz);
    }

    internal static void UpdatePlayingObserverRelativeSounds()
    {
        if (DirectorCameraRuntime.Controller?.IsFollowingNonLocalTarget != true)
        {
            RestoreObserverRelativeSounds();
            return;
        }

        if (LoadedSoundsField?.GetValue(null) is not IEnumerable loaded
            || LoadedSoundsLockField?.GetValue(null) is not object gate)
        {
            return;
        }

        LoadedSoundNative[] sounds;
        lock (gate)
        {
            sounds = loaded.Cast<object>().OfType<LoadedSoundNative>().ToArray();
        }

        foreach (LoadedSoundNative sound in sounds)
        {
            try
            {
                if (!sound.IsDisposed
                    && sound.IsPlaying
                    && ShouldSuppressObserverRelativeSound(sound.Params))
                {
                    if (sound.Params.ShouldLoop)
                    {
                        sound.Pause();
                        pausedObserverSounds.Add(sound);
                    }
                    else
                    {
                        sound.Stop();
                    }
                }
            }
            catch
            {
            }
        }
    }

    internal static void RestoreObserverRelativeSounds()
    {
        restoringObserverSounds = true;
        try
        {
            foreach (LoadedSoundNative sound in pausedObserverSounds.ToArray())
            {
                try
                {
                    if (!sound.IsDisposed && sound.IsPaused)
                    {
                        sound.Start();
                    }
                }
                catch
                {
                }
            }
        }
        finally
        {
            restoringObserverSounds = false;
            pausedObserverSounds.Clear();
        }
    }
}
