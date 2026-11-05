using Vintagestory.API.Client;
using VSDirector.Data;

namespace VSDirector.Networking;

internal sealed class DirectorChunkPreloadClient : IDisposable
{
    private const long HeartbeatIntervalMs = 1000;
    // This request is advisory.  Sending an entire replay corridor can mean
    // thousands of columns and turns a singleplayer host into a terrain-load
    // queue.  The replay manager supplies a current observation window; this
    // final cap also protects path and editor callers.
    private const int MaxRequestColumns = 16;

    private readonly ICoreClientAPI capi;
    private readonly IClientNetworkChannel channel;
    private string activeReplayId = string.Empty;
    private int activePlanHash;
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
        if (replayId == Guid.Empty || plan.ExpandedColumns.Count == 0)
        {
            Stop();
            return;
        }

        string replayCode = replayId.ToString("D");
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

        channel.SendPacket(request);
        activeReplayId = replayCode;
        activePlanHash = planHash;
        lastSentMs = now;
    }

    public void Stop()
    {
        if (activeReplayId.Length == 0)
        {
            return;
        }

        channel.SendPacket(new DirectorChunkPreloadRequest { Active = false });
        activeReplayId = string.Empty;
        activePlanHash = 0;
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

    private static IReadOnlyList<DirectorReplayPreloadColumn> RequestColumns(
        DirectorReplayPreloadPlan plan)
    {
        if (plan.ExpandedColumns.Count <= MaxRequestColumns)
        {
            return plan.ExpandedColumns;
        }

        return plan.ExpandedColumns.Take(MaxRequestColumns).ToArray();
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
