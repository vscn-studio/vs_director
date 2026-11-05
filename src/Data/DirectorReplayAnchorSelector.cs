namespace VSDirector.Data;

public readonly record struct DirectorReplayAnchorPose(
    double X,
    double Y,
    double Z,
    int Dimension);

public static class DirectorReplayAnchorSelector
{
    private const int ChunkSize = 32;

    public static bool TrySelect(
        DirectorReplayDocument? document,
        double timeSeconds,
        string? preferredPlayerUid,
        out DirectorReplayAnchorPose pose)
    {
        pose = default;
        if (document is null)
        {
            return false;
        }

        DirectorReplayActorTrack[] actors = document.Actors?
            .Where(actor => actor?.Samples is { Count: > 0 })
            .ToArray()
            ?? Array.Empty<DirectorReplayActorTrack>();
        string preferredUid = preferredPlayerUid?.Trim() ?? string.Empty;

        if (TrySelectActor(actors, timeSeconds, preferredUid, requireVisible: true, out pose)
            || TrySelectActor(actors, timeSeconds, preferredUid, requireVisible: false, out pose))
        {
            return true;
        }

        DirectorReplayWorldSnapshot? snapshot = document.WorldSnapshot;
        if (snapshot?.Chunks is not { Count: > 0 })
        {
            return false;
        }

        pose = new DirectorReplayAnchorPose(
            snapshot.CenterChunkX * ChunkSize + ChunkSize / 2d,
            0d,
            snapshot.CenterChunkZ * ChunkSize + ChunkSize / 2d,
            snapshot.Dimension);
        return true;
    }

    private static bool TrySelectActor(
        IReadOnlyList<DirectorReplayActorTrack> actors,
        double timeSeconds,
        string preferredPlayerUid,
        bool requireVisible,
        out DirectorReplayAnchorPose pose)
    {
        pose = default;
        IEnumerable<DirectorReplayActorTrack> candidates = actors;
        if (requireVisible)
        {
            candidates = candidates.Where(actor => DirectorReplayMath.IsActorVisible(actor, timeSeconds));
        }

        candidates = candidates
            .OrderByDescending(actor => preferredPlayerUid.Length > 0
                && string.Equals(actor.PlayerUid, preferredPlayerUid, StringComparison.Ordinal))
            .ThenByDescending(actor => !string.IsNullOrWhiteSpace(actor.PlayerUid));

        foreach (DirectorReplayActorTrack actor in candidates)
        {
            if (!DirectorReplayMath.TrySample(actor, timeSeconds, out DirectorReplayActorSample sample)
                || !double.IsFinite(sample.X)
                || !double.IsFinite(sample.Y)
                || !double.IsFinite(sample.Z))
            {
                continue;
            }

            pose = new DirectorReplayAnchorPose(sample.X, sample.Y, sample.Z, sample.Dimension);
            return true;
        }

        return false;
    }
}
