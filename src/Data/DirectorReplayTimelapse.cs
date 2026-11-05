namespace VSDirector.Data;

internal readonly record struct DirectorReplayTimelapsePair(
    DirectorReplayElementKeyframe Start,
    DirectorReplayElementKeyframe End);

internal static class DirectorReplayTimelapse
{
    internal static List<DirectorReplayTimelapsePair> FindPairs(
        IReadOnlyList<DirectorReplayElementKeyframe>? keyframes,
        Guid trackId)
    {
        List<DirectorReplayElementKeyframe> frames = (keyframes ?? Array.Empty<DirectorReplayElementKeyframe>())
            .Where(keyframe => keyframe is not null
                && keyframe.TrackId == trackId
                && keyframe.TrackType == DirectorReplayElementTrackType.Timelapse)
            .ToList();
        List<DirectorReplayTimelapsePair> pairs = new();
        HashSet<Guid> claimed = new();

        foreach (IGrouping<Guid, DirectorReplayElementKeyframe> group in frames
            .Where(keyframe => keyframe.TimelapsePairId != Guid.Empty)
            .GroupBy(keyframe => keyframe.TimelapsePairId))
        {
            DirectorReplayElementKeyframe? start = group.FirstOrDefault(keyframe =>
                keyframe.TimelapseRole == DirectorReplayTimelapseKeyframeRole.Start);
            DirectorReplayElementKeyframe? end = group.FirstOrDefault(keyframe =>
                keyframe.TimelapseRole == DirectorReplayTimelapseKeyframeRole.End);
            if (start is null || end is null || start.Id == end.Id)
            {
                List<DirectorReplayElementKeyframe> ordered = group
                    .OrderBy(keyframe => keyframe.TimeSeconds)
                    .ThenBy(keyframe => keyframe.Id)
                    .ToList();
                if (ordered.Count >= 2)
                {
                    start = ordered[0];
                    end = ordered[1];
                }
            }

            if (start is not null && end is not null && start.Id != end.Id)
            {
                pairs.Add(new DirectorReplayTimelapsePair(start, end));
                claimed.Add(start.Id);
                claimed.Add(end.Id);
            }
        }

        List<DirectorReplayElementKeyframe> legacy = frames
            .Where(keyframe => !claimed.Contains(keyframe.Id)
                && keyframe.TimelapsePairId == Guid.Empty)
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .ThenBy(keyframe => keyframe.Id)
            .ToList();
        for (int index = 0; index + 1 < legacy.Count; index += 2)
        {
            DirectorReplayElementKeyframe first = legacy[index];
            DirectorReplayElementKeyframe second = legacy[index + 1];
            DirectorReplayElementKeyframe start = first.TimelapseRole
                == DirectorReplayTimelapseKeyframeRole.End
                ? second
                : first;
            DirectorReplayElementKeyframe end = ReferenceEquals(start, first)
                ? second
                : first;
            pairs.Add(new DirectorReplayTimelapsePair(start, end));
        }

        return pairs
            .OrderBy(pair => pair.Start.TimeSeconds)
            .ThenBy(pair => pair.End.TimeSeconds)
            .ThenBy(pair => pair.Start.Id)
            .ToList();
    }

    internal static void NormalizePairs(
        List<DirectorReplayElementKeyframe> keyframes,
        Guid trackId)
    {
        List<DirectorReplayElementKeyframe> frames = keyframes
            .Where(keyframe => keyframe.TrackId == trackId
                && keyframe.TrackType == DirectorReplayElementTrackType.Timelapse)
            .ToList();
        HashSet<Guid> assigned = new();

        foreach (IGrouping<Guid, DirectorReplayElementKeyframe> group in frames
            .Where(keyframe => keyframe.TimelapsePairId != Guid.Empty)
            .GroupBy(keyframe => keyframe.TimelapsePairId))
        {
            List<DirectorReplayElementKeyframe> ordered = group
                .OrderBy(keyframe => keyframe.TimeSeconds)
                .ThenBy(keyframe => keyframe.Id)
                .ToList();
            if (ordered.Count < 2)
            {
                continue;
            }

            DirectorReplayElementKeyframe start = group.FirstOrDefault(keyframe =>
                    keyframe.TimelapseRole == DirectorReplayTimelapseKeyframeRole.Start)
                ?? ordered[0];
            DirectorReplayElementKeyframe end = group.FirstOrDefault(keyframe =>
                    keyframe.TimelapseRole == DirectorReplayTimelapseKeyframeRole.End
                    && keyframe.Id != start.Id)
                ?? ordered.First(keyframe => keyframe.Id != start.Id);
            Assign(start, end, group.Key);
            assigned.Add(start.Id);
            assigned.Add(end.Id);
        }

        List<DirectorReplayElementKeyframe> legacy = frames
            .Where(keyframe => !assigned.Contains(keyframe.Id)
                && keyframe.TimelapsePairId == Guid.Empty)
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .ThenBy(keyframe => keyframe.Id)
            .ToList();
        for (int index = 0; index + 1 < legacy.Count; index += 2)
        {
            DirectorReplayElementKeyframe first = legacy[index];
            DirectorReplayElementKeyframe second = legacy[index + 1];
            DirectorReplayElementKeyframe start = first.TimelapseRole
                == DirectorReplayTimelapseKeyframeRole.End
                ? second
                : first;
            DirectorReplayElementKeyframe end = ReferenceEquals(start, first)
                ? second
                : first;
            Assign(start, end, start.Id);
            assigned.Add(start.Id);
            assigned.Add(end.Id);
        }

        foreach (DirectorReplayElementKeyframe orphan in frames
            .Where(keyframe => !assigned.Contains(keyframe.Id)))
        {
            orphan.TimelapseRole = DirectorReplayTimelapseKeyframeRole.Start;
            orphan.TimelapsePairId = orphan.TimelapsePairId == Guid.Empty
                ? orphan.Id
                : orphan.TimelapsePairId;
        }

        static void Assign(
            DirectorReplayElementKeyframe start,
            DirectorReplayElementKeyframe end,
            Guid pairId)
        {
            if (start.Id == end.Id)
            {
                return;
            }
            start.TimelapseRole = DirectorReplayTimelapseKeyframeRole.Start;
            end.TimelapseRole = DirectorReplayTimelapseKeyframeRole.End;
            start.TimelapsePairId = pairId == Guid.Empty ? start.Id : pairId;
            end.TimelapsePairId = start.TimelapsePairId;
        }
    }

    internal static bool IsStart(DirectorReplayElementKeyframe keyframe)
        => keyframe.TrackType == DirectorReplayElementTrackType.Timelapse
            && keyframe.TimelapseRole == DirectorReplayTimelapseKeyframeRole.Start;

    internal static bool IsEnd(DirectorReplayElementKeyframe keyframe)
        => keyframe.TrackType == DirectorReplayElementTrackType.Timelapse
            && keyframe.TimelapseRole == DirectorReplayTimelapseKeyframeRole.End;

    internal static bool TryFindPair(
        IReadOnlyList<DirectorReplayElementKeyframe>? keyframes,
        Guid trackId,
        Guid keyframeId,
        out DirectorReplayTimelapsePair pair)
    {
        foreach (DirectorReplayTimelapsePair candidate in FindPairs(keyframes, trackId))
        {
            if (candidate.Start.Id == keyframeId || candidate.End.Id == keyframeId)
            {
                pair = candidate;
                return true;
            }
        }
        pair = default;
        return false;
    }
}
