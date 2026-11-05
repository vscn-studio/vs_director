namespace VSDirector.Data;

/// <summary>
/// Selects the amount of actor animation work that a replay frame may claim.
/// The dedicated renderer can spend a larger budget because its actors are
/// removed from the engine's normal entity-renderer traversal.
/// </summary>
public enum DirectorReplayActorSchedulingMode
{
    Preview,
    Dedicated
}

/// <summary>
/// Runtime-independent description of one actor's animation scheduling state.
/// The replay manager is responsible for calculating visibility and distance;
/// this policy only decides whether the actor is eligible for this frame.
/// </summary>
public readonly record struct DirectorReplayActorSchedulingRequest(
    bool Eligible,
    bool Force,
    long LastAnimationUpdateFrame,
    double DistanceSquared);

/// <summary>
/// Result of a deterministic round-robin scheduling pass.
/// Indices refer to the request list supplied to <see cref="SelectAnimationUpdates"/>.
/// </summary>
public readonly record struct DirectorReplayActorSchedulingResult(
    IReadOnlyList<int> SelectedIndices,
    int NextCursor);

public static class DirectorReplayActorSchedulingPolicy
{
    public const int PreviewAnimationBudget = 16;
    public const int DedicatedAnimationBudget = 32;
    public const int DedicatedMinimumAnimationBudget = 16;
    public const int DedicatedMaximumAnimationBudget = 64;
    public const int MaximumReservedTransientActors = 64;
    // Animator creation can invoke model loading and shader preparation. Keep
    // that one-time work as a small share of the normal animation budget so a
    // camera cut cannot make all newly visible actors initialize at once.
    public const int DedicatedMaximumWarmupsPerFrame = 8;
    public const int DedicatedWarmupAttemptsBeforeFallback = 6;
    public const long DedicatedMaximumWarmupRetryDelayFrames = 30L;
    public const double DedicatedWarmupWorkBudgetMilliseconds = 2d;

    private const double NearDistanceSquared = 16d * 16d;
    private const double MediumDistanceSquared = 32d * 32d;

    /// <summary>
    /// Keeps part of the recording actor budget available for short-lived
    /// items and falling blocks that may not exist when recording starts.
    /// </summary>
    public static int ResolveTransientActorReserve(int maximumActors)
    {
        int maximum = Math.Clamp(maximumActors, 1, 2048);
        return maximum <= 1
            ? 0
            : Math.Min(MaximumReservedTransientActors, Math.Max(1, maximum / 4));
    }

    /// <summary>
    /// Historical tracks retain their samples but do not occupy capture slots.
    /// Only currently captured actors, including active transient tracks, count
    /// towards the concurrent recording budget.
    /// </summary>
    public static bool CanAllocateRecordingActor(
        IReadOnlySet<string> activeActorKeys,
        IReadOnlyDictionary<string, string> transientActorTypes,
        int maximumActors,
        bool canUseTransientReserve)
        => CanAllocateRecordingActor(
            activeActorKeys.Count,
            activeActorKeys.Count(transientActorTypes.ContainsKey),
            maximumActors,
            canUseTransientReserve);

    /// <summary>
    /// Stable actors stop before the reserved transient portion. Players,
    /// workflow ghosts, and transient actors may claim the complete budget.
    /// </summary>
    public static bool CanAllocateRecordingActor(
        int existingActors,
        int existingTransientActors,
        int maximumActors,
        bool canUseTransientReserve)
    {
        int maximum = Math.Clamp(maximumActors, 1, 2048);
        int total = Math.Max(0, existingActors);
        if (total >= maximum)
        {
            return false;
        }

        int stableActors = Math.Max(0, total - Math.Max(0, existingTransientActors));
        int stableLimit = maximum - ResolveTransientActorReserve(maximum);
        return canUseTransientReserve || stableActors < Math.Max(1, stableLimit);
    }

    /// <summary>
    /// Resolves the number of lazy animator creations allowed in one render
    /// frame. Warm-ups use at most one quarter of the animation budget and
    /// always leave one slot available while the dedicated path is active.
    /// </summary>
    public static int ResolveWarmupBudget(int animationBudget)
    {
        if (animationBudget <= 0)
        {
            return 0;
        }

        return Math.Clamp(
            (animationBudget + 3) / 4,
            1,
            DedicatedMaximumWarmupsPerFrame);
    }

    /// <summary>
    /// Returns the number of replay frames to wait after a failed lazy
    /// animator initialization. The bounded backoff prevents an unsupported
    /// entity from consuming work on every render stage.
    /// </summary>
    public static long WarmupRetryDelayFrames(int failedAttempts)
    {
        if (failedAttempts <= 0)
        {
            return 0L;
        }

        int shift = Math.Min(failedAttempts - 1, 3);
        return Math.Min(
            DedicatedMaximumWarmupRetryDelayFrames,
            1L << shift);
    }

    public static bool ShouldFallbackAfterWarmupFailure(int failedAttempts)
        => failedAttempts >= DedicatedWarmupAttemptsBeforeFallback;

    /// <summary>
    /// Returns the minimum number of replay frames between animation-pose
    /// updates for an actor at the supplied distance. Invalid distances are
    /// treated as far away instead of making an actor update every frame.
    /// </summary>
    public static int DistanceUpdateInterval(double distanceSquared)
    {
        if (!double.IsFinite(distanceSquared))
        {
            return 4;
        }

        double distance = Math.Max(0d, distanceSquared);
        return distance <= NearDistanceSquared
            ? 1
            : distance <= MediumDistanceSquared
                ? 2
                : 4;
    }

    /// <summary>
    /// Keeps the existing replay transition semantics in one testable place.
    /// A newly visible actor must receive a complete pose before it is drawn.
    /// </summary>
    public static bool ShouldForceAnimation(
        bool backwards,
        bool becameVisible,
        bool animatorReplaced,
        bool inCurrentView,
        bool wasInCurrentView)
        => DirectorReplayPlaybackPolicy.ShouldForceActorAnimations(
            backwards,
            becameVisible,
            animatorReplaced,
            inCurrentView,
            wasInCurrentView);

    /// <summary>
    /// Determines whether an actor's animation state is due for an update.
    /// A backwards seek, a first update, or an explicit force bypasses the
    /// distance-based interval.
    /// </summary>
    public static bool IsAnimationDue(
        long frame,
        long lastAnimationUpdateFrame,
        double distanceSquared,
        bool force = false)
    {
        if (force || lastAnimationUpdateFrame < 0L || frame < lastAnimationUpdateFrame)
        {
            return true;
        }

        return frame - lastAnimationUpdateFrame
            >= DistanceUpdateInterval(distanceSquared);
    }

    /// <summary>
    /// Resolves a mode-specific budget and clamps it to the number of actors.
    /// Callers may override the defaults while tuning a client profile.
    /// </summary>
    public static int ResolveAnimationBudget(
        DirectorReplayActorSchedulingMode mode,
        int actorCount,
        int previewBudget = PreviewAnimationBudget,
        int dedicatedBudget = DedicatedAnimationBudget)
    {
        int count = Math.Max(0, actorCount);
        int requested = mode == DirectorReplayActorSchedulingMode.Dedicated
            ? dedicatedBudget
            : previewBudget;
        return Math.Clamp(requested, 0, count);
    }

    /// <summary>
    /// Nudges the dedicated animation budget toward a render-thread time
    /// target. The hysteresis avoids changing the budget for normal jitter and
    /// keeps the first-view gate bounded on both fast and slow machines.
    /// </summary>
    public static int AdaptAnimationBudget(
        int previousBudget,
        double elapsedMilliseconds,
        int updatedCount,
        double targetMilliseconds = 4d,
        int minimumBudget = DedicatedMinimumAnimationBudget,
        int maximumBudget = DedicatedMaximumAnimationBudget)
    {
        int minimum = Math.Max(1, Math.Min(minimumBudget, maximumBudget));
        int maximum = Math.Max(minimum, maximumBudget);
        int current = Math.Clamp(previousBudget, minimum, maximum);
        if (updatedCount <= 0
            || !double.IsFinite(elapsedMilliseconds)
            || !double.IsFinite(targetMilliseconds)
            || targetMilliseconds <= 0d)
        {
            return current;
        }

        double elapsed = Math.Max(0d, elapsedMilliseconds);
        int step = Math.Max(1, current / 8);
        if (elapsed > targetMilliseconds * 1.15d)
        {
            return Math.Max(minimum, current - step);
        }
        if (elapsed < targetMilliseconds * 0.65d && updatedCount >= current)
        {
            return Math.Min(maximum, current + step);
        }
        return current;
    }

    /// <summary>
    /// Selects due actors in a circular, deterministic order. Forced actors are
    /// considered before ordinary due actors, but remain bounded by the frame
    /// budget. One quarter is reserved for ordinary due work when both classes
    /// are present, so an actor whose animator cannot initialize cannot starve
    /// every already-visible actor. The caller can keep an unselected forced
    /// actor hidden until its pose is ready; this prevents a camera cut that
    /// reveals hundreds of actors from turning one render frame into a stall.
    /// The cursor advances by the budget rather than by the number selected;
    /// this preserves fairness when some actors are not currently eligible.
    /// </summary>
    public static DirectorReplayActorSchedulingResult SelectAnimationUpdates(
        IReadOnlyList<DirectorReplayActorSchedulingRequest> requests,
        long frame,
        int cursor,
        DirectorReplayActorSchedulingMode mode,
        int? budgetOverride = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        int count = requests.Count;
        if (count == 0)
        {
            return new DirectorReplayActorSchedulingResult(Array.Empty<int>(), 0);
        }

        int start = cursor % count;
        if (start < 0)
        {
            start += count;
        }

        int budget = budgetOverride.HasValue
            ? Math.Clamp(budgetOverride.Value, 0, count)
            : ResolveAnimationBudget(mode, count);
        if (budget == 0)
        {
            return new DirectorReplayActorSchedulingResult(Array.Empty<int>(), start);
        }

        List<int> selected = new(Math.Min(budget, count));
        bool[] selectedFlags = new bool[count];
        int ordinaryReserve = budget >= 4 ? Math.Max(1, budget / 4) : 0;
        int forcedLimit = budget - ordinaryReserve;
        for (int offset = 0; offset < count && selected.Count < forcedLimit; offset++)
        {
            int index = (start + offset) % count;
            DirectorReplayActorSchedulingRequest request = requests[index];
            if (request.Eligible && request.Force)
            {
                selected.Add(index);
                selectedFlags[index] = true;
            }
        }

        for (int offset = 0; offset < count && selected.Count < budget; offset++)
        {
            int index = (start + offset) % count;
            DirectorReplayActorSchedulingRequest request = requests[index];
            if (!request.Eligible
                || request.Force
                || !IsAnimationDue(
                    frame,
                    request.LastAnimationUpdateFrame,
                    request.DistanceSquared))
            {
                continue;
            }

            selected.Add(index);
            selectedFlags[index] = true;
        }

        // If no ordinary work needed its reserved share, give the unused slots
        // back to first-view actors instead of reducing total throughput.
        for (int offset = 0; offset < count && selected.Count < budget; offset++)
        {
            int index = (start + offset) % count;
            DirectorReplayActorSchedulingRequest request = requests[index];
            if (!selectedFlags[index] && request.Eligible && request.Force)
            {
                selected.Add(index);
                selectedFlags[index] = true;
            }
        }

        int nextCursor = (int)((start + (long)budget) % count);
        return new DirectorReplayActorSchedulingResult(selected, nextCursor);
    }
}
