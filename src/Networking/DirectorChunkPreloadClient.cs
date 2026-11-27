using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using VSDirector.Replay;

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
    private IReadOnlySet<DirectorReplayPreloadColumn>? activePlan;
    private int batchId;
    private readonly HashSet<DirectorReplayPreloadColumn> completed = new();
    private readonly HashSet<DirectorReplayPreloadColumn> requested = new();
    private readonly HashSet<DirectorReplayPreloadColumn> residentOnPlanChange = new();
    private IReadOnlyList<DirectorReplayPreloadColumn> batch = Array.Empty<DirectorReplayPreloadColumn>();
    private long lastSentMs;
    private long firstMissingAfterDispatchMs;
    private bool retryDelivery;
    private readonly HashSet<DirectorReplayPreloadColumn> missingAfterDelivery = new();
    private int verificationIndex;
    private long retryAfterMs;
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
        long now = capi.ElapsedMilliseconds;
        if (replayCode == activeReplayId && now < retryAfterMs) return;
        bool reset = replayCode != activeReplayId || activePlan is null;
        bool planChanged = reset || !activePlan!.SetEquals(plan.ExpandedColumnSet);
        var removed = planChanged && !reset
            ? requested.Where(c => !plan.ExpandedColumnSet.Contains(c)).ToArray()
            : Array.Empty<DirectorReplayPreloadColumn>();
        if (reset)
        {
            requested.Clear();
            completed.Clear();
            batch = Array.Empty<DirectorReplayPreloadColumn>();
            retryDelivery = false;
            missingAfterDelivery.Clear();
            firstMissingAfterDispatchMs = 0;
        }
        else if (removed.Length > 0)
        {
            requested.ExceptWith(removed);
            completed.ExceptWith(removed);
            missingAfterDelivery.ExceptWith(removed);
        }
        if (!reset && now - lastSentMs >= HeartbeatIntervalMs && capi.World is ClientMain game)
        {
            // Completed columns can disappear after an engine unload. Revisit
            // a bounded slice each heartbeat even after the queue has drained.
            for (int i = 0; i < Math.Min(MaxRequestColumns, plan.ExpandedColumns.Count); i++)
            {
                verificationIndex %= plan.ExpandedColumns.Count;
                var column = plan.ExpandedColumns[verificationIndex++];
                if (completed.Contains(column) && !DirectorReplayManager.IsTerrainColumnReceived(game.WorldMap, column))
                {
                    completed.Remove(column);
                    missingAfterDelivery.Add(column);
                }
            }
        }
        var map = (capi.World as ClientMain)?.WorldMap;
        if (planChanged)
        {
            residentOnPlanChange.Clear();
            if (map is not null)
                residentOnPlanChange.UnionWith(plan.ExpandedColumns.Where(c => DirectorReplayManager.IsTerrainColumnReceived(map, c)));
        }
        var urgent = plan.ObservationColumns.Where(c => map is not null
            && !DirectorReplayManager.IsTerrainColumnReceived(map, c)).ToArray();
        foreach (var c in urgent)
            if (completed.Remove(c)) missingAfterDelivery.Add(c);
        IReadOnlyList<DirectorReplayPreloadColumn> columns;
        if (urgent.Any(c => !batch.Contains(c)))
            columns = urgent.Take(MaxRequestColumns).ToArray();
        else if (missingAfterDelivery.Count > 0)
            columns = urgent.Concat(missingAfterDelivery).Distinct().Take(MaxRequestColumns).ToArray();
        else columns = RequestColumns(plan);
        retryDelivery = columns.Any(missingAfterDelivery.Contains);
        bool changed = reset || retryDelivery || !batch.SequenceEqual(columns);
        if (planChanged)
        {
            activePlan = plan.ExpandedColumnSet;
            DirectorChunkPreloadRuntime.Set(activePlan);
        }
        if (!changed && removed.Length == 0 && now - lastSentMs < HeartbeatIntervalMs)
        {
            return;
        }

        DirectorChunkPreloadRequest request = new()
        {
            Active = true,
            ReplayId = replayCode,
            RadiusChunks = 0
        };
        if (!reset) request.RemovedColumns = removed.Select(PacketColumn).ToList();
        if (changed)
        {
            batch = columns;
            request.KeepPreviousBatches = !reset;
            request.RetryDelivery = retryDelivery;
            if (retryDelivery) missingAfterDelivery.ExceptWith(columns);
            retryDelivery = false;
            firstMissingAfterDispatchMs = 0;
            ++batchId;
            request.Columns = columns.Select(PacketColumn).ToList();
            if (map is not null)
                request.ResidentColumns = columns.Where(c => DirectorReplayManager.IsTerrainColumnReceived(map, c))
                    .Select(PacketColumn).ToList();
            requested.UnionWith(columns);
            capi.Logger.Debug(
                "VS Director: preload {0}: {1} columns from {2}, {3} retention only.",
                replayCode,
                columns.Count,
                plan.ExpandedColumns.Count, request.ResidentColumns.Count);
            lastReportedSent = -1;
        }

        request.BatchId = batchId;
        activeReplayId = replayCode;
        lastSentMs = now;
        channel.SendPacket(request);
    }

    private static DirectorChunkPreloadColumn PacketColumn(DirectorReplayPreloadColumn column)
        => new() { X = column.X, Z = column.Z, Dimension = column.Dimension };

    internal bool NeedsRegistration(Guid id, DirectorReplayPreloadPlan plan)
        => channel.Connected && (activeReplayId != id.ToString("D") || activePlan is null
            || !activePlan.SetEquals(plan.ExpandedColumnSet) || completed.Count < plan.ExpandedColumns.Count);

    public void Stop()
    {
        if (activeReplayId.Length == 0)
        {
            return;
        }

        if (channel.Connected)
            channel.SendPacket(new DirectorChunkPreloadRequest { Active = false });
        activeReplayId = string.Empty;
        activePlan = null;
        requested.Clear();
        retryAfterMs = 0;
        completed.Clear(); batch = Array.Empty<DirectorReplayPreloadColumn>();
        lastSentMs = capi.ElapsedMilliseconds;
        lastReportedSent = -1;
        DirectorChunkPreloadRuntime.Clear();
    }

    public void ResetSession()
    {
        Stop();
        activeReplayId = string.Empty;
        activePlan = null;
        lastSentMs = 0;
        lastReportedSent = -1;
    }

    public void Dispose() => Stop();

    private void OnStatus(DirectorChunkPreloadStatus status)
    {
        if (status.ReplayId != activeReplayId || activeReplayId.Length == 0
            || (status.BatchId != batchId && !(status.BatchId == 0 && !status.Allowed))) return;
        if (!status.Allowed)
        {
            if (status.MessageCode is "preload-session-lost" or "invalid-preload")
            {
                activePlan = null;
                retryAfterMs = status.MessageCode == "invalid-preload" ? capi.ElapsedMilliseconds + 5000 : 0;
            }
            capi.Logger.Warning(
                "VS Director: replay chunk preload was rejected: {0}",
                status.MessageCode);
            return;
        }
        if (status.TotalColumns == batch.Count && status.SentColumns >= batch.Count)
        {
            var map = (capi.World as ClientMain)?.WorldMap;
            int before = completed.Count;
            foreach (var column in batch)
            {
                if (map is null || DirectorReplayManager.IsTerrainColumnReceived(map, column))
                    completed.Add(column);
            }
            if (map is not null && before != completed.Count)
                capi.Logger.Debug("VS Director: replay preload client received {0}/{1} planned columns; batch {2}.",
                    completed.Count, activePlan?.Count, batchId);
            if (batch.Any(c => !completed.Contains(c)))
            {
                long now = capi.ElapsedMilliseconds;
                if (firstMissingAfterDispatchMs == 0) firstMissingAfterDispatchMs = now;
                else if (now - firstMissingAfterDispatchMs >= 5000)
                    missingAfterDelivery.UnionWith(batch.Where(c => !completed.Contains(c)));
            }
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

    private IReadOnlyList<DirectorReplayPreloadColumn> RequestColumns(DirectorReplayPreloadPlan plan)
    {
        // Keep a batch until its acknowledgement; then advance beyond the first 16 columns.
        bool keepBatch = batch.Count > 0 && batch.All(plan.ExpandedColumnSet.Contains);
        if (!retryDelivery && keepBatch && batch.Any(c => !completed.Contains(c))) return batch;
        if (!retryDelivery && !keepBatch && batch.Count > 0)
        {
            var remaining = batch.Where(c => plan.ExpandedColumnSet.Contains(c) && !completed.Contains(c)).ToArray();
            if (remaining.Length > 0) return remaining;
        }
        if (completed.Count == plan.ExpandedColumnSet.Count)
            return keepBatch ? batch : plan.ExpandedColumns.Take(MaxRequestColumns).ToArray();
        var pending = plan.ExpandedColumns.Where(c => !completed.Contains(c) && !residentOnPlanChange.Contains(c))
            .Concat(plan.ExpandedColumns.Where(c => !completed.Contains(c) && residentOnPlanChange.Contains(c)))
            .Take(MaxRequestColumns).ToArray();
        return pending.Length > 0 ? pending : batch;
    }
}

internal static class DirectorChunkPreloadRuntime
{
    private static volatile ILookup<(int X, int Z), int> activeColumns =
        Array.Empty<DirectorReplayPreloadColumn>().ToLookup(c => (c.X, c.Z), c => c.Dimension);

    internal static void Set(IEnumerable<DirectorReplayPreloadColumn> columns)
        => activeColumns = columns.ToLookup(c => (c.X, c.Z), c => c.Dimension);

    internal static void Clear()
        => Set(Array.Empty<DirectorReplayPreloadColumn>());

    internal static IEnumerable<int> DimensionsAt(int chunkX, int chunkZ)
        => activeColumns[(chunkX, chunkZ)];

    internal static bool Contains(int chunkX, int chunkY, int chunkZ)
    {
        int dimension = Math.Max(0, chunkY / (DirectorChunkWindow.DimensionHeight / 32));
        return DimensionsAt(chunkX, chunkZ).Contains(dimension);
    }
}
