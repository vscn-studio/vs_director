namespace VSDirector.Data;

public readonly record struct DirectorReplayParticlePlaybackWindow(int StartIndex, int EndIndex);

public readonly record struct DirectorReplaySoundPlaybackWindow(int StartIndex, int EndIndex);

public static class DirectorReplayPlaybackPolicy
{
    public static bool ShouldRestartFromEnd(
        bool playbackPaused,
        double playbackTimeSeconds,
        double durationSeconds)
        => playbackPaused
            && double.IsFinite(playbackTimeSeconds)
            && double.IsFinite(durationSeconds)
            && durationSeconds >= 0d
            && playbackTimeSeconds >= durationSeconds - 0.000001d;

    public static bool IsValidSoundPosition(double x, double y, double z)
        => double.IsFinite(x)
            && double.IsFinite(y)
            && double.IsFinite(z);

    public static DirectorReplaySoundPlaybackWindow SelectSoundWindow(
        IReadOnlyList<DirectorReplaySoundEvent> events,
        int nextEvent,
        double playbackTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(events);
        double time = double.IsFinite(playbackTimeSeconds) ? Math.Max(0d, playbackTimeSeconds) : 0d;
        int end = FirstSoundEventAfter(events, time);
        return new DirectorReplaySoundPlaybackWindow(Math.Clamp(nextEvent, 0, end), end);
    }

    public static IReadOnlyList<DirectorReplaySoundEvent> SelectActiveSounds(
        IReadOnlyList<DirectorReplaySoundEvent> events,
        double playbackTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(events);
        double time = double.IsFinite(playbackTimeSeconds) ? Math.Max(0d, playbackTimeSeconds) : 0d;
        int end = FirstSoundEventAfter(events, time);
        Dictionary<long, DirectorReplaySoundEvent> active = new();
        for (int index = 0; index < end; index++)
        {
            DirectorReplaySoundEvent sound = events[index];
            if (sound.InstanceId <= 0L)
            {
                continue;
            }
            if (sound.Action is DirectorReplaySoundAction.Stop or DirectorReplaySoundAction.Pause)
            {
                active.Remove(sound.InstanceId);
            }
            else if (sound.Action == DirectorReplaySoundAction.Play
                || sound.Action == DirectorReplaySoundAction.Update
                    && active.ContainsKey(sound.InstanceId))
            {
                active[sound.InstanceId] = sound;
            }
        }
        return active.Values
            .Where(sound => IsSoundActiveAt(sound, time))
            .OrderBy(sound => sound.InstanceId)
            .ToArray();
    }

    private static bool IsSoundActiveAt(DirectorReplaySoundEvent sound, double timeSeconds)
    {
        if (sound.ShouldLoop)
        {
            return true;
        }
        if (sound.SoundLengthSeconds <= 0f)
        {
            return false;
        }

        double elapsed = Math.Max(0d, timeSeconds - sound.TimeSeconds);
        double effectivePitch = Math.Max(0.01d, sound.Pitch + sound.PitchOffset);
        return sound.PlaybackPosition + elapsed * effectivePitch < sound.SoundLengthSeconds;
    }

    public static double ClampPlaybackDelta(double deltaSeconds, double maximumSeconds)
    {
        double delta = double.IsFinite(deltaSeconds) ? Math.Max(0d, deltaSeconds) : 0d;
        double maximum = double.IsFinite(maximumSeconds) ? Math.Max(0d, maximumSeconds) : 0d;
        return Math.Min(delta, maximum);
    }

    public static float ResolvePendingParticleDelta(
        float replayFrameDeltaSeconds,
        float pendingDeltaSeconds,
        bool resetTimeline)
    {
        float frameDelta = float.IsFinite(replayFrameDeltaSeconds)
            ? Math.Max(0f, replayFrameDeltaSeconds)
            : 0f;
        if (resetTimeline || frameDelta > 0f)
        {
            return frameDelta;
        }

        return float.IsFinite(pendingDeltaSeconds)
            ? Math.Max(0f, pendingDeltaSeconds)
            : 0f;
    }

    public static bool ShouldAdvanceParticleClock(bool playbackPaused, bool exportActive, bool creationAdvancing = false)
        => !playbackPaused || exportActive || creationAdvancing;

    public static bool ShouldPauseReplaySounds(
        bool playbackPaused,
        bool gamePaused,
        bool obsExportRecording)
        => gamePaused || playbackPaused && !obsExportRecording;

    public static bool ShouldRebuildWorld(
        DirectorReplayDocument? document,
        bool quickMode,
        bool applyWorldSnapshot)
        => document is not null
            && !quickMode
            && applyWorldSnapshot
            && document.WorldSnapshot.Chunks.Count > 0;

    public static bool HasNonActorContent(DirectorReplayDocument? document, bool quickMode)
    {
        if (document is null)
        {
            return false;
        }

        return (!quickMode
                && (document.WorldSnapshot.Chunks.Count > 0 || document.BlockEvents.Count > 0))
            || document.ParticleEvents.Count > 0
            || document.SoundEvents.Count > 0
            || document.ClockSamples.Count > 0;
    }

    public static bool ShouldForceActorAnimations(
        bool backwards,
        bool becameVisible,
        bool animatorReplaced,
        bool inCurrentView,
        bool wasInCurrentView)
        => backwards
            || becameVisible
            || animatorReplaced
            || (inCurrentView && !wasInCurrentView);

    public static bool ShouldResetActorAnimationManager(
        bool forceAnimations,
        bool hasExactRecordedStates)
        => forceAnimations && !hasExactRecordedStates;

    public static float ResolveAnimatorPoseDelta(
        bool hasExactRecordedFrames,
        float replayDeltaSeconds)
        => hasExactRecordedFrames
            ? 0f
            : float.IsFinite(replayDeltaSeconds)
                ? Math.Max(0f, replayDeltaSeconds)
                : 0f;

    public static DirectorReplayParticlePlaybackWindow SelectParticleWindow(
        IReadOnlyList<DirectorReplayParticleEvent> events,
        int nextEvent,
        double playbackTimeSeconds,
        double maxEventAgeSeconds,
        int maxEvents)
    {
        ArgumentNullException.ThrowIfNull(events);
        double time = double.IsFinite(playbackTimeSeconds) ? Math.Max(0d, playbackTimeSeconds) : 0d;
        double age = double.IsFinite(maxEventAgeSeconds) ? Math.Max(0d, maxEventAgeSeconds) : 0d;
        int limit = Math.Max(1, maxEvents);
        int end = FirstEventAfter(events, time);
        int freshStart = FirstEventAtOrAfter(events, Math.Max(0d, time - age));
        int start = Math.Clamp(Math.Max(nextEvent, Math.Max(freshStart, end - limit)), 0, end);
        return new DirectorReplayParticlePlaybackWindow(start, end);
    }

    public static int AvailableParticleSlots(
        int aliveCount,
        int maximumAlive,
        int reservedPrioritySlots,
        bool priority)
    {
        int maximum = Math.Max(0, maximumAlive);
        int reserve = Math.Clamp(reservedPrioritySlots, 0, maximum);
        int limit = priority ? maximum : maximum - reserve;
        return Math.Max(0, limit - Math.Max(0, aliveCount));
    }

    private static int FirstEventAfter(
        IReadOnlyList<DirectorReplayParticleEvent> events,
        double timeSeconds)
        => DirectorReplayTimeSearch.UpperBound(events, timeSeconds + 0.000001d, static e => e.TimeSeconds);

    private static int FirstSoundEventAfter(
        IReadOnlyList<DirectorReplaySoundEvent> events,
        double timeSeconds)
        => DirectorReplayTimeSearch.UpperBound(events, timeSeconds + 0.000001d, static e => e.TimeSeconds);

    private static int FirstEventAtOrAfter(
        IReadOnlyList<DirectorReplayParticleEvent> events,
        double timeSeconds)
        => DirectorReplayTimeSearch.LowerBound(events, timeSeconds - 0.000001d, static e => e.TimeSeconds);
}
