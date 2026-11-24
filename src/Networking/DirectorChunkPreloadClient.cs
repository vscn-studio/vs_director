using Vintagestory.API.Client;
using VSDirector.Data;

namespace VSDirector.Networking;

internal sealed class DirectorChunkPreloadClient : IDisposable
{
    private const long HeartbeatIntervalMs = 1000;
    // Route and manually selected columns are streamed in bounded batches;
    // a new batch starts only after the server acknowledges the previous one.
    private const int MaxRequestColumns = 16;

    private readonly ICoreClientAPI capi;
    private readonly IClientNetworkChannel channel;
    private string activeReplayId = string.Empty;
    private int activePlanHash;
    private int batchId;
    private readonly HashSet<DirectorReplayPreloadColumn> completed = new();
    private IReadOnlyList<DirectorReplayPreloadColumn> batch = Array.Empty<DirectorReplayPreloadColumn>();
    private long lastSentMs;
    private int lastReportedSent = -1;

    public DirectorChunkPreloadClient(ICoreClientAPI capi)
    {
        this.capi = capi;
        channel = capi.Network.GetChannel(DirectorNetwork.ChannelName)
            .SetMessageHandler<DirectorChunkPreloadStatus>(OnStatus);
    }

    public void Update(Guid replayId, DirectorReplayPreloadPlan plan)
    {
        if (!channel.Connected || replayId == Guid.Empty || plan.ExpandedColumns.Count == 0)
        {
            Stop();
            return;
        }

        string replayCode = replayId.ToString("D");
        if (replayCode != activeReplayId) { completed.Clear(); batch = Array.Empty<DirectorReplayPreloadColumn>(); }
        completed.IntersectWith(plan.ExpandedColumnSet);
        IReadOnlyList<DirectorReplayPreloadColumn> columns = RequestColumns(plan);
        int planHash = PlanHash(columns);
        long now = capi.ElapsedMilliseconds;
        bool changed = !string.Equals(replayCode, activeReplayId, StringComparison.Ordinal)
            || planHash != activePlanHash;
        if (!changed && now - lastSentMs < HeartbeatIntervalMs)
        {
            return;
        }

        DirectorChunkPreloadRequest request = new()
        {
            Active = true,
            ReplayId = replayCode,
            RadiusChunks = 0
        };
        if (changed)
        {
            batch = columns;
            request.BatchId = ++batchId;
            request.Columns = columns.Select(column => new DirectorChunkPreloadColumn
            {
                X = column.X,
                Z = column.Z,
                Dimension = column.Dimension
            }).ToList();
            capi.Logger.Debug(
                "VS Director: preloading target {0}: {1} observation columns selected from {2}.",
                replayCode,
                columns.Count,
                plan.ExpandedColumns.Count);
            lastReportedSent = -1;
            DirectorChunkPreloadRuntime.Set(columns);
        }

        request.BatchId = batchId;
        activeReplayId = replayCode;
        activePlanHash = planHash;
        lastSentMs = now;
        channel.SendPacket(request);
    }

    public void Stop()
    {
        if (activeReplayId.Length == 0)
        {
            return;
        }

        if (channel.Connected)
            channel.SendPacket(new DirectorChunkPreloadRequest { Active = false });
        activeReplayId = string.Empty;
        activePlanHash = 0;
        completed.Clear(); batch = Array.Empty<DirectorReplayPreloadColumn>();
        lastSentMs = capi.ElapsedMilliseconds;
        lastReportedSent = -1;
        DirectorChunkPreloadRuntime.Clear();
    }

    public void ResetSession()
    {
        Stop();
        activeReplayId = string.Empty;
        activePlanHash = 0;
        lastSentMs = 0;
        lastReportedSent = -1;
    }

    public void Dispose() => Stop();

    private void OnStatus(DirectorChunkPreloadStatus status)
    {
        if (!status.Allowed)
        {
            capi.Logger.Warning(
                "VS Director: replay chunk preload was rejected: {0}",
                status.MessageCode);
            return;
        }
        if (status.ReplayId != activeReplayId || status.BatchId != batchId) return;
        if (status.TotalColumns == batch.Count && status.SentColumns >= batch.Count)
            completed.UnionWith(batch);
        if (status.SentColumns == lastReportedSent)
        {
            return;
        }

        lastReportedSent = status.SentColumns;
        capi.Logger.Debug(
            "VS Director: replay preload server progress: {0}/{1} columns sent.",
            status.SentColumns,
            status.TotalColumns);
    }

    private IReadOnlyList<DirectorReplayPreloadColumn> RequestColumns(DirectorReplayPreloadPlan plan)
    {
        // Keep a batch until its acknowledgement; then advance beyond the first 16 columns.
        if (batch.Count > 0 && batch.All(plan.ExpandedColumnSet.Contains) && batch.Any(c => !completed.Contains(c))) return batch;
        var pending = plan.ExpandedColumns.Where(c => !completed.Contains(c)).Take(MaxRequestColumns).ToArray();
        return pending.Length > 0 ? pending : plan.ExpandedColumns.Take(MaxRequestColumns).ToArray();
    }

    private static int PlanHash(IReadOnlyList<DirectorReplayPreloadColumn> columns)
    {
        HashCode hash = new();
        hash.Add(columns.Count);
        foreach (DirectorReplayPreloadColumn column in columns)
        {
            hash.Add(column);
        }
        return hash.ToHashCode();
    }
}

internal static class DirectorChunkPreloadRuntime
{
    private static IReadOnlySet<DirectorReplayPreloadColumn> activeColumns =
        new HashSet<DirectorReplayPreloadColumn>();

    internal static void Set(IEnumerable<DirectorReplayPreloadColumn> columns)
        => activeColumns = columns.ToHashSet();

    internal static void Clear()
        => activeColumns = new HashSet<DirectorReplayPreloadColumn>();

    internal static bool Contains(int chunkX, int chunkY, int chunkZ)
    {
        int dimension = Math.Max(0, chunkY / (DirectorChunkWindow.DimensionHeight / 32));
        return activeColumns.Contains(new DirectorReplayPreloadColumn(chunkX, chunkZ, dimension));
    }
}
