namespace VSDirector.Data;

public readonly record struct DirectorReplayPreloadColumn(int X, int Z, int Dimension);

public sealed class DirectorReplayPreloadPlan
{
    public DirectorReplayPreloadPlan(
        IReadOnlyList<DirectorReplayPreloadColumn> sourceColumns,
        IReadOnlyList<DirectorReplayPreloadColumn> automaticColumns,
        IReadOnlyList<DirectorReplayPreloadColumn> expandedColumns,
        int radiusChunks,
        bool truncated)
    {
        SourceColumns = sourceColumns;
        AutomaticColumns = automaticColumns;
        AutomaticColumnSet = automaticColumns.ToHashSet();
        ExpandedColumns = expandedColumns;
        ExpandedColumnSet = expandedColumns.ToHashSet();
        RadiusChunks = radiusChunks;
        Truncated = truncated;
    }

    public IReadOnlyList<DirectorReplayPreloadColumn> SourceColumns { get; }

    public IReadOnlyList<DirectorReplayPreloadColumn> AutomaticColumns { get; }

    public IReadOnlySet<DirectorReplayPreloadColumn> AutomaticColumnSet { get; }

    public IReadOnlyList<DirectorReplayPreloadColumn> ExpandedColumns { get; }

    public IReadOnlySet<DirectorReplayPreloadColumn> ExpandedColumnSet { get; }

    public int RadiusChunks { get; }

    public bool Truncated { get; }
}

public static class DirectorReplayPreloadPlanner
{
    public const int DefaultRadiusChunks = 1;
    public const int DefaultMaxSourceColumns = 512;
    public const int MaxOverrideColumns = 4096;
    public const int MaxExpandedColumns = 4096;
    private const int ChunkSize = 32;

    public static DirectorReplayPreloadPlan Build(
        DirectorReplayDocument document,
        string? preferredPlayerUid,
        bool includeWorldSnapshotColumns,
        int radiusChunks = DefaultRadiusChunks,
        int maxSourceColumns = DefaultMaxSourceColumns)
    {
        ArgumentNullException.ThrowIfNull(document);
        int radius = Math.Clamp(radiusChunks, 0, 2);
        int maxColumns = Math.Clamp(maxSourceColumns, 1, 2048);
        DirectorReplayActorTrack? actor = SelectActor(document, preferredPlayerUid);
        List<DirectorReplayPreloadColumn> source = new();
        HashSet<DirectorReplayPreloadColumn> unique = new();
        bool truncated = false;

        if (actor is not null)
        {
            DirectorReplayPreloadColumn? previous = null;
            foreach (DirectorReplayActorSample sample in actor.Samples)
            {
                if (!double.IsFinite(sample.X) || !double.IsFinite(sample.Z))
                {
                    continue;
                }

                DirectorReplayPreloadColumn current = new(
                    (int)Math.Floor(sample.X / ChunkSize),
                    (int)Math.Floor(sample.Z / ChunkSize),
                    sample.Dimension);
                if (previous is { } from && from.Dimension == current.Dimension)
                {
                    foreach (DirectorReplayPreloadColumn column in Rasterize(from, current))
                    {
                        if (!Add(column))
                        {
                            truncated = true;
                            break;
                        }
                    }
                }
                else if (!Add(current))
                {
                    truncated = true;
                }

                previous = current;
                if (truncated)
                {
                    break;
                }
            }
        }

        List<DirectorReplayPreloadColumn> automatic = Expand(source, radius);
        if (includeWorldSnapshotColumns
            && document.WorldSnapshot?.Chunks is { Count: > 0 } chunks)
        {
            HashSet<DirectorReplayPreloadColumn> automaticSet = automatic.ToHashSet();
            foreach (DirectorReplayChunkSnapshot chunk in chunks)
            {
                DirectorReplayPreloadColumn column = new(
                    chunk.ChunkX,
                    chunk.ChunkZ,
                    document.WorldSnapshot.Dimension);
                if (!automaticSet.Add(column))
                {
                    continue;
                }
                if (automatic.Count >= MaxExpandedColumns)
                {
                    truncated = true;
                    break;
                }
                automatic.Add(column);
            }
        }

        List<DirectorReplayPreloadColumn> expanded = ApplyOverrides(
            automatic,
            document.IncludedPreloadColumns,
            document.ExcludedPreloadColumns,
            out bool overridesTruncated);
        return new DirectorReplayPreloadPlan(
            source,
            automatic,
            expanded,
            radius,
            truncated || overridesTruncated);

        bool Add(DirectorReplayPreloadColumn column)
        {
            if (unique.Contains(column))
            {
                return true;
            }
            if (source.Count >= maxColumns)
            {
                return false;
            }

            unique.Add(column);
            source.Add(column);
            return true;
        }
    }

    public static DirectorReplayPreloadPlan Build(
        CameraPath path,
        int radiusChunks = DefaultRadiusChunks,
        int maxSourceColumns = DefaultMaxSourceColumns)
    {
        ArgumentNullException.ThrowIfNull(path);
        int radius = Math.Clamp(radiusChunks, 0, 2);
        int maxColumns = Math.Clamp(maxSourceColumns, 1, 2048);
        List<DirectorReplayPreloadColumn> source = new();
        HashSet<DirectorReplayPreloadColumn> unique = new();
        bool truncated = false;

        if (path.Keyframes is { Count: > 0 })
        {
            DirectorReplayPreloadColumn? previous = null;
            for (int segment = 0; segment < Math.Max(1, path.Keyframes.Count - 1); segment++)
            {
                CameraKeyframe from = path.Keyframes[Math.Min(segment, path.Keyframes.Count - 1)];
                CameraKeyframe to = path.Keyframes[Math.Min(segment + 1, path.Keyframes.Count - 1)];
                int chunkDistance = Math.Max(
                    Math.Abs((int)Math.Floor(to.X / ChunkSize) - (int)Math.Floor(from.X / ChunkSize)),
                    Math.Abs((int)Math.Floor(to.Z / ChunkSize) - (int)Math.Floor(from.Z / ChunkSize)));
                int samples = path.Keyframes.Count == 1 ? 1 : Math.Clamp(chunkDistance * 4, 4, 128);
                for (int sampleIndex = 0; sampleIndex <= samples; sampleIndex++)
                {
                    double amount = sampleIndex / (double)samples;
                    double time = from.TimeSeconds + (to.TimeSeconds - from.TimeSeconds) * amount;
                    CameraKeyframe sample = CameraPathSampler.TryEvaluate(path, time, out CameraKeyframe evaluated)
                        ? evaluated
                        : amount < 0.5d ? from : to;
                    if (!double.IsFinite(sample.X) || !double.IsFinite(sample.Z))
                    {
                        continue;
                    }

                    DirectorReplayPreloadColumn current = new(
                        (int)Math.Floor(sample.X / ChunkSize),
                        (int)Math.Floor(sample.Z / ChunkSize),
                        sample.Dimension);
                    if (previous is { } prior && prior.Dimension == current.Dimension)
                    {
                        foreach (DirectorReplayPreloadColumn column in Rasterize(prior, current))
                        {
                            if (!Add(column))
                            {
                                truncated = true;
                                break;
                            }
                        }
                    }
                    else if (!Add(current))
                    {
                        truncated = true;
                    }

                    previous = current;
                    if (truncated)
                    {
                        break;
                    }
                }
                if (truncated)
                {
                    break;
                }
            }
        }

        List<DirectorReplayPreloadColumn> automatic = Expand(source, radius);
        List<DirectorReplayPreloadColumn> expanded = ApplyOverrides(
            automatic,
            path.IncludedPreloadColumns,
            path.ExcludedPreloadColumns,
            out bool overridesTruncated);
        return new DirectorReplayPreloadPlan(
            source,
            automatic,
            expanded,
            radius,
            truncated || overridesTruncated);

        bool Add(DirectorReplayPreloadColumn column)
        {
            if (unique.Contains(column))
            {
                return true;
            }
            if (source.Count >= maxColumns)
            {
                return false;
            }

            unique.Add(column);
            source.Add(column);
            return true;
        }
    }

    public static List<DirectorReplayPreloadColumn> NormalizeOverrideColumns(
        IEnumerable<DirectorReplayPreloadColumn>? columns)
    {
        if (columns is null)
        {
            return new List<DirectorReplayPreloadColumn>();
        }

        HashSet<DirectorReplayPreloadColumn> unique = new();
        List<DirectorReplayPreloadColumn> normalized = new();
        foreach (DirectorReplayPreloadColumn column in columns)
        {
            if (column.X < 0 || column.Z < 0 || column.Dimension < 0 || !unique.Add(column))
            {
                continue;
            }
            normalized.Add(column);
            if (normalized.Count >= MaxOverrideColumns)
            {
                break;
            }
        }
        return normalized;
    }

    public static List<DirectorReplayPreloadColumn> ApplyOverrides(
        IEnumerable<DirectorReplayPreloadColumn> automaticColumns,
        IEnumerable<DirectorReplayPreloadColumn>? includedColumns,
        IEnumerable<DirectorReplayPreloadColumn>? excludedColumns,
        out bool truncated)
    {
        HashSet<DirectorReplayPreloadColumn> excluded = NormalizeOverrideColumns(excludedColumns).ToHashSet();
        HashSet<DirectorReplayPreloadColumn> unique = new();
        List<DirectorReplayPreloadColumn> result = new();
        truncated = false;
        foreach (DirectorReplayPreloadColumn column in automaticColumns.Concat(
                     NormalizeOverrideColumns(includedColumns)))
        {
            if (excluded.Contains(column) || !unique.Add(column))
            {
                continue;
            }
            if (result.Count >= MaxExpandedColumns)
            {
                truncated = true;
                break;
            }
            result.Add(column);
        }
        return result;
    }

    public static List<DirectorReplayPreloadColumn> Expand(
        IEnumerable<DirectorReplayPreloadColumn> sourceColumns,
        int radiusChunks)
    {
        int radius = Math.Clamp(radiusChunks, 0, 2);
        int radiusSquared = radius * radius;
        HashSet<DirectorReplayPreloadColumn> unique = new();
        List<DirectorReplayPreloadColumn> expanded = new();
        foreach (DirectorReplayPreloadColumn source in sourceColumns)
        {
            for (int offsetX = -radius; offsetX <= radius; offsetX++)
            {
                for (int offsetZ = -radius; offsetZ <= radius; offsetZ++)
                {
                    if (offsetX * offsetX + offsetZ * offsetZ > radiusSquared)
                    {
                        continue;
                    }

                    DirectorReplayPreloadColumn column = new(
                        source.X + offsetX,
                        source.Z + offsetZ,
                        source.Dimension);
                    if (unique.Add(column))
                    {
                        expanded.Add(column);
                    }
                }
            }
        }

        return expanded;
    }

    private static DirectorReplayActorTrack? SelectActor(
        DirectorReplayDocument document,
        string? preferredPlayerUid)
    {
        string preferredUid = preferredPlayerUid?.Trim() ?? string.Empty;
        return document.Actors
            .Where(actor => actor?.Samples is { Count: > 0 })
            .OrderByDescending(actor => preferredUid.Length > 0
                && string.Equals(actor.PlayerUid, preferredUid, StringComparison.Ordinal))
            .ThenByDescending(actor => !string.IsNullOrWhiteSpace(actor.PlayerUid))
            .ThenByDescending(actor => actor.Samples.Count)
            .FirstOrDefault();
    }

    private static IEnumerable<DirectorReplayPreloadColumn> Rasterize(
        DirectorReplayPreloadColumn from,
        DirectorReplayPreloadColumn to)
    {
        int deltaX = to.X - from.X;
        int deltaZ = to.Z - from.Z;
        int steps = Math.Max(Math.Abs(deltaX), Math.Abs(deltaZ));
        if (steps == 0)
        {
            yield return to;
            yield break;
        }

        for (int step = 0; step <= steps; step++)
        {
            double amount = step / (double)steps;
            yield return new DirectorReplayPreloadColumn(
                (int)Math.Round(from.X + deltaX * amount, MidpointRounding.AwayFromZero),
                (int)Math.Round(from.Z + deltaZ * amount, MidpointRounding.AwayFromZero),
                from.Dimension);
        }
    }
}
