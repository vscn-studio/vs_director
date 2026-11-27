using VSDirector.Compatibility;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Data;

namespace VSDirector.Replay;

internal sealed class DirectorReplayWorldPlayback : IDisposable
{
    private readonly Queue<ReplayChunkKey> historyRefreshQueue = new();
    private readonly HashSet<ReplayChunkKey> historyRefreshSet = [];
    private ReplayChunkKey? historyRefreshChunk;
    private int historyRefreshIndex;
    private int[]? historySolid, historyFluid;
    private IReadOnlySet<DirectorReplayPreloadColumn>? observation;
    private readonly HashSet<ReplayChunkKey> deferredBaseline = new();
    private ReplayChunkKey? backgroundRefresh;
    private bool firstBaseline;
    private readonly List<int> backgroundIndices = new();
    private int backgroundComparison;
    private bool backgroundCompared;
    private readonly Dictionary<ReplayChunkKey, BlockKey[]> recordedChangesByChunk;

    internal void SetObservationColumns(IReadOnlySet<DirectorReplayPreloadColumn> columns) => observation = columns;

    private bool InObservation(ReplayChunkKey key) => observation is null
        || observation.Contains(new(key.X, key.Z, document.WorldSnapshot.Dimension));

    internal bool HasPendingColumn(DirectorReplayPreloadColumn column)
        => column.Dimension == document.WorldSnapshot.Dimension
            && (deferredBaseline.Any(c => c.X == column.X && c.Z == column.Z)
                || historyRefreshSet.Any(c => c.X == column.X && c.Z == column.Z)
                || pendingChunkRedrawSet.Any(c => c.ChunkX == column.X && c.ChunkZ == column.Z));

    private bool Loaded(ReplayChunkKey key) => game.WorldMap.GetChunk(key.X,
        key.Y + document.WorldSnapshot.Dimension * GlobalConstants.DimensionSizeInChunks, key.Z) is ClientChunk
        { LoadedFromServer: true, MapChunk: not null };

    private void ScheduleDeferredBaseline()
    {
        if (deferredBaseline.Count == 0 || phase != ApplyPhase.None || backgroundRefresh.HasValue) return;
        ReplayChunkKey? next = null;
        foreach (var c in deferredBaseline)
        {
            if (InObservation(c)) { next = c; break; }
            next ??= c;
        }
        if (next is not { } key || (HasPendingChunkRefresh && !InObservation(key))) return;
        deferredBaseline.Remove(key);
        if (!Loaded(key)) return; // The normal chunk arrival hook restores it on delivery.
        backgroundRefresh = key;
        backgroundCompared = false;
        backgroundComparison = 0;
        backgroundIndices.Clear();
        if (historyRefreshSet.Add(key)) historyRefreshQueue.Enqueue(key);
    }
    public void ObserveServerRecordingChunkLoad(int x, int internalY, int z)
    {
        if (!started) return;
        int y = internalY - document.WorldSnapshot.Dimension * GlobalConstants.DimensionSizeInChunks;
        var key = new ReplayChunkKey(x, y, z);
        if (baselineChunks.TryGetValue(key, out var chunk)
            ? !document.HasHistoricalBaselines || chunk.CapturedAtSeconds <= requestedTime
            : sparsePositions.ContainsKey(key))
        {
            deferredBaseline.Remove(key);
            if (backgroundRefresh == key) backgroundRefresh = null;
            if (historyRefreshChunk == key) historyRefreshIndex = 0;
            else if (historyRefreshSet.Add(key)) historyRefreshQueue.Enqueue(key);
        }
    }
    private const int ChunkSize = GlobalConstants.ChunkSize;
    private const int ChunkBlockCount = ChunkSize * ChunkSize * ChunkSize;
    private const int CommitBatchSize = 4096;
    private const int MinimumOperationBudget = 4096;
    private const int BlockEntityOperationCost = 512;
    private const int BlockEntityScanCost = 8;
    private const int MaxChunkRedrawsPerAdvance = 2;
    private const double BaselineWorkBudgetMilliseconds = 4d;
    private const string LiveRealityObservationTaskCode =
        "vsdirector-replay-live-reality-observations";

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly DirectorReplayDocument document;
    private readonly Action<bool> applyingState;
    private readonly Func<bool> isApplyingState;
    private readonly int mainThreadId;

    private readonly Dictionary<BlockKey, BlockPair> originalBlocks = new();
    private readonly Dictionary<BlockKey, DirectorReplayBlockEntitySnapshot> originalBlockEntities = new();
    private readonly HashSet<BlockKey> touchedBlockEntities = new();
    // Animation callbacks from these entities describe replay state, not a
    // live server transition. The marker lets capture hooks distinguish that
    // case even when a replay reuses the world's existing entity instance.
    private readonly ConditionalWeakTable<BlockEntity, ReplayBlockEntityMarker>
        replayBlockEntities = new();
    private readonly Dictionary<ReplayChunkKey, DirectorReplayChunkSnapshot> baselineChunks;
    private readonly Dictionary<BlockKey, DirectorReplayBlockEntitySnapshot> baselineBlockEntitySnapshots;
    private readonly Dictionary<BlockKey, IReadOnlyList<DirectorReplayBlockEvent>> blockEventsByPosition;
    private readonly HashSet<BlockKey> legacyEmptyStarts = new();
    private readonly List<DirectorReplayBlockEvent> legacyCompletions = new();
    private readonly Dictionary<ReplayChunkKey, BlockKey[]> sparsePositions;
    private readonly Dictionary<ReplayChunkKey, DirectorReplayBlockEntitySnapshot[]> entitiesByChunk;
    private int historicalSnapshotIndex;
    private readonly Queue<BlockKey> restoreUnobservedEntities = new();
    private readonly HashSet<BlockKey> baselineBlockEntities;
    private readonly HashSet<string> unavailableBlockEntityClasses = new(StringComparer.Ordinal);
    private readonly Dictionary<BlockKey, DirectorReplayBlockEvent> pendingEventBlockEntities = new();
    private readonly Queue<BlockKey> pendingEventBlockEntityKeys = new();
    private readonly Queue<ChunkRedraw> pendingChunkRedraws = new();
    private readonly HashSet<ChunkRedraw> pendingChunkRedrawSet = new();
    // Network callbacks may arrive off-thread. Keep immutable coordinates and
    // merge them until the client thread can snapshot the live world safely.
    private readonly object liveRealityObservationSync = new();
    private readonly Dictionary<BlockKey, LiveRealityObservationKind>
        pendingLiveRealityObservations = new();
    private bool liveRealityObservationTaskQueued;
    private int nextBlockEvent;
    private IEnumerator<DirectorReplayBlockEvent>? indexedSeek;
    private DirectorReplayBlockEvent? nextIndexedChange;
    private bool UseIndexedSeek => document.Pages is not null && (document.HasHistoricalBaselines || document.WorldSnapshot.Chunks.All(c => c.CapturedAtSeconds == 0));
    private IEnumerator<DirectorReplayBlockEvent>? deferredChunks;
    private DirectorReplayBlockEvent? nextDeferredBlock;
    private DirectorReplayChunkSnapshot[] historicalChunks = [];
    private int historicalChunkIndex, historicalComparisonIndex, historicalChangeIndex;
    private int[]? historicalSolid, historicalFluid;
    private bool historicalCompared;
    private BlockPos[]? historicalBlockEntities;
    private int historicalBlockEntityIndex;
    private readonly List<int> historicalChanges = new();
    private Dictionary<ReplayChunkKey, BlockKey[]> baselineOriginalKeys = new();
    private double appliedTime;
    private double requestedTime;
    private double baselineTime;
    private ApplyPhase phase;
    private int baselineChunkIndex;
    private int baselineBlockIndex;
    private int[]? baselineSolid;
    private int[]? baselineFluid;
    private bool baselineChunkPrepared;
    private int baselineComparisonIndex;
    private bool baselineComparisonComplete;
    private bool baselineUsesChangedIndices;
    private readonly List<int> baselineChangedIndices = new();
    private int baselineChangedIndex;
    private List<BlockPos>? baselineExtraBlockEntities;
    private int baselineExtraChunkIndex;
    private BlockPos[]? baselineExtraChunkPositions;
    private int baselineExtraChunkPositionIndex;
    private bool baselineExtraScanComplete;
    private int baselineExtraBlockEntityIndex;
    private int baselineBlockEntityIndex;
    private long baselineProcessedBlocks;
    private volatile bool started;
    private bool synchronizing;
    private volatile bool disposed;

    public DirectorReplayWorldPlayback(
        ICoreClientAPI capi,
        ClientMain game,
        DirectorReplayDocument document,
        Action<bool> applyingState,
        Func<bool> isApplyingState)
    {
        this.capi = capi;
        this.game = game;
        this.document = document;
        this.applyingState = applyingState;
        this.isApplyingState = isApplyingState;
        mainThreadId = Environment.CurrentManagedThreadId;
        baselineChunks = document.WorldSnapshot.Chunks
            .GroupBy(chunk => new ReplayChunkKey(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ))
            .ToDictionary(group => group.Key, group => group.Last());
        baselineBlockEntitySnapshots = document.WorldSnapshot.BlockEntities
            .GroupBy(snapshot => new BlockKey(snapshot.X, snapshot.Y, snapshot.Z, snapshot.Dimension))
            .ToDictionary(group => group.Key, group => group.Last());
        blockEventsByPosition = document.Pages is { } pages
            ? pages.BlockPositions.ToDictionary(
                item => new BlockKey(item.X, item.Y, item.Z, item.Dimension),
                item => (IReadOnlyList<DirectorReplayBlockEvent>)new DirectorReplayList<DirectorReplayBlockEvent>(item.Indices.Length, index => document.BlockEvents[item.Indices[index]]))
            : document.BlockEvents
            .GroupBy(change => new BlockKey(change.X, change.Y, change.Z, change.Dimension))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<DirectorReplayBlockEvent>)group.OrderBy(change => change.TimeSeconds).ToArray());
        baselineBlockEntities = document.WorldSnapshot.BlockEntities
            .Select(snapshot => new BlockKey(snapshot.X, snapshot.Y, snapshot.Z, snapshot.Dimension))
            .ToHashSet();
        entitiesByChunk = baselineBlockEntitySnapshots.Values
            .GroupBy(s => new ReplayChunkKey(s.X >> 5, s.Y >> 5, s.Z >> 5))
            .ToDictionary(g => g.Key, g => g.ToArray());
        sparsePositions = blockEventsByPosition.Keys.Where(k => k.Dimension == document.WorldSnapshot.Dimension
            && !baselineChunks.ContainsKey(new(k.X >> 5, k.Y >> 5, k.Z >> 5)))
            .GroupBy(k => new ReplayChunkKey(k.X >> 5, k.Y >> 5, k.Z >> 5))
            .ToDictionary(g => g.Key, g => g.ToArray());
        if (!document.HasHistoricalBaselines) RepairLegacyPlacements();
        recordedChangesByChunk = blockEventsByPosition.Keys.Where(k => k.Dimension == document.WorldSnapshot.Dimension)
            .GroupBy(k => new ReplayChunkKey(k.X >> 5, k.Y >> 5, k.Z >> 5)).ToDictionary(g => g.Key, g => g.ToArray());
    }

    private void RepairLegacyPlacements()
    {
        // Older clients seeded newly initialized entities outside the snapshot
        // with their *placed* block ID at time zero, before a BE existed.
        foreach (var group in sparsePositions.Values)
        foreach (var key in group)
        {
            var history = blockEventsByPosition[key];
            if (history.Count < 2) continue;
            var first = history[0];
            var placed = history[1];
            if (first.TimeSeconds != 0 || !first.BlockEntityCaptured || !string.IsNullOrEmpty(first.BlockEntityClass)
                || first.SolidBlockId == 0 || placed.TimeSeconds <= 0 || placed.SolidBlockId != first.SolidBlockId
                || !placed.BlockEntityCaptured || placed.BlockEntityClass is not ("Sapling" or "KnappingSurface")) continue;
            var empty = first.DeepClone();
            empty.SolidBlockId = 0;
            legacyEmptyStarts.Add(key);
            var last = history[^1];
            DirectorReplayBlockEvent? removed = null;
            if (last.BlockEntityClass == "KnappingSurface" && KnappingCompletion(last) is double end)
            {
                removed = new DirectorReplayBlockEvent { TimeSeconds = end, X = key.X, Y = key.Y, Z = key.Z,
                    Dimension = key.Dimension, FluidBlockId = last.FluidBlockId, BlockEntityCaptured = true };
                legacyCompletions.Add(removed);
            }
            // Leave all intermediate voxel states backed by the original replay pages.
            blockEventsByPosition[key] = new DirectorReplayList<DirectorReplayBlockEvent>(
                history.Count + (removed is null ? 0 : 1),
                i => i == 0 ? empty : i == history.Count ? removed! : history[i]);
        }
        legacyCompletions.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
    }

    private double? KnappingCompletion(DirectorReplayBlockEvent last)
    {
        var sounds = document.SoundEvents;
        int start = DirectorReplayTimeSearch.LowerBound(sounds, last.TimeSeconds, s => s.TimeSeconds);
        double hit = double.NegativeInfinity;
        for (int i = start; i < sounds.Count; i++)
        {
            var s = sounds[i];
            if (s.TimeSeconds > last.TimeSeconds + 1) break;
            if (s.RelativePosition || s.Action != DirectorReplaySoundAction.Play
                || Math.Floor(s.X) != last.X || Math.Floor(s.Y) != last.Y || Math.Floor(s.Z) != last.Z) continue;
            if (s.Location is "game:sounds/player/knap1.ogg" or "game:sounds/player/knap2.ogg") hit = s.TimeSeconds;
            if (s.TimeSeconds - hit <= .15 && s.Location is ("game:sounds/player/collect1.ogg"
                or "game:sounds/player/collect2.ogg" or "game:sounds/player/collect3.ogg"))
                return s.TimeSeconds;
        }
        return null;
    }

    public int ModifiedBlockCount => originalBlocks.Count;

    private bool HasPendingChunkRefresh => historyRefreshChunk.HasValue || historyRefreshQueue.Count > 0;

    // Incoming live chunks replace replay terrain even while paused. Their
    // restoration must keep the manager advancing until the queue is drained.
    public bool IsBusy
        => phase != ApplyPhase.None || HasPendingChunkRefresh || deferredBaseline.Count > 0 || pendingChunkRedraws.Count > 0;

    public bool IsSynchronizing => synchronizing
        || (HasPendingChunkRefresh && (observation is null || historyRefreshSet.Any(InObservation)))
        || (observation is not null && deferredBaseline.Any(InObservation));

    internal bool IsReplayBlockEntity(BlockEntity blockEntity)
        => started
            && !disposed
            && replayBlockEntities.TryGetValue(blockEntity, out _);

    public bool IsReady
        => started && !IsBusy;

    public string Phase => HasPendingChunkRefresh ? "refresh" : phase switch
    {
        ApplyPhase.Baseline => "baseline",
        ApplyPhase.Events => "events",
        _ => "ready"
    };

    public double Progress
    {
        get
        {
            long totalBlocks = (long)document.WorldSnapshot.Chunks.Count * ChunkBlockCount;
            if (phase == ApplyPhase.Baseline)
            {
                return totalBlocks <= 0L
                    ? 0.9d
                    : Math.Clamp(baselineProcessedBlocks / (double)totalBlocks * 0.9d, 0d, 0.9d);
            }

            if (phase == ApplyPhase.Events)
            {
                int target = LastEventAtOrBefore(requestedTime);
                return target <= 0
                    ? 1d
                    : 0.9d + Math.Clamp(nextBlockEvent / (double)target, 0d, 1d) * 0.1d;
            }

            return started ? 1d : 0d;
        }
    }

    public void Start()
    {
        if (started || document.WorldSnapshot.Chunks.Count == 0)
        {
            return;
        }

        started = true;
        bool wasApplyingState = BeginApplyingState();
        try
        {
            BeginBaseline(0d);
            firstBaseline = true;
        }
        finally
        {
            applyingState(wasApplyingState);
        }
    }

    public void ApplyAt(double timeSeconds, bool synchronize = false)
    {
        if (!started)
        {
            return;
        }

        double time = double.IsFinite(timeSeconds)
            ? Math.Clamp(timeSeconds, 0d, document.DurationSeconds)
            : 0d;
        requestedTime = time;
        if (synchronize)
        {
            synchronizing = true;
        }
        if (phase == ApplyPhase.Baseline)
        {
            if (time + 0.000001d < baselineTime) BeginBaseline(time);
            return;
        }

        if ((UseIndexedSeek && synchronize && time - appliedTime > 5d && phase == ApplyPhase.None)
            || (nextIndexedChange is not null && time + 0.000001d < baselineTime)
            || time + 0.000001d < appliedTime
            || (historicalSolid is not null && historicalChunkIndex < historicalChunks.Length
                && time + 0.000001d < historicalChunks[historicalChunkIndex].CapturedAtSeconds))
        {
            BeginBaseline(time);
            return;
        }

        if (HasPendingEvent(time))
        {
            phase = ApplyPhase.Events;
            return;
        }

        appliedTime = time;
        if (phase == ApplyPhase.None)
        {
            synchronizing = false;
        }
    }

    public void Advance(int operationBudget)
    {
        if (!started || !IsBusy)
        {
            return;
        }

        int remaining = Math.Clamp(operationBudget, MinimumOperationBudget, 1000000);
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool wasApplyingState = BeginApplyingState();
        try
        {
            // Read incoming live blocks before baseline/event work can overwrite
            // them; those values must survive in originalBlocks for replay exit.
            ScheduleDeferredBaseline();
            if (HasPendingChunkRefresh && (IsSynchronizing || phase == ApplyPhase.None))
            {
                AdvanceHistoryRefresh(remaining, stopwatch);
                return;
            }

            if (phase == ApplyPhase.Baseline)
            {
                remaining = AdvanceBaseline(remaining, stopwatch);
                if (phase == ApplyPhase.Baseline)
                {
                    return;
                }
            }

            if (phase == ApplyPhase.Events && remaining > 0)
            {
                AdvanceEvents(remaining, stopwatch);
            }
        }
        finally
        {
            AdvancePendingChunkRedraws(
                MaxChunkRedrawsPerAdvance,
                stopwatch);
            applyingState(wasApplyingState);
        }
    }

    private void BeginBaseline(double targetTime)
    {
        indexedSeek?.Dispose(); indexedSeek = null; nextIndexedChange = null;
        deferredBaseline.Clear();
        firstBaseline = false;
        backgroundRefresh = null;
        historyRefreshQueue.Clear(); historyRefreshSet.Clear(); historyRefreshChunk = null; historySolid = historyFluid = null;
        requestedTime = Math.Clamp(targetTime, 0d, document.DurationSeconds);
        baselineTime = requestedTime;
        phase = ApplyPhase.Baseline;
        synchronizing = true;
        baselineChunkIndex = 0;
        baselineBlockIndex = 0;
        ReleaseBaselineArrays();
        baselineChunkPrepared = false;
        baselineComparisonIndex = 0;
        baselineComparisonComplete = false;
        baselineUsesChangedIndices = false;
        baselineChangedIndices.Clear();
        baselineChangedIndex = 0;
        baselineExtraBlockEntities = null;
        baselineExtraChunkIndex = 0;
        baselineExtraChunkPositions = null;
        baselineExtraChunkPositionIndex = 0;
        baselineExtraScanComplete = false;
        baselineExtraBlockEntityIndex = 0;
        baselineBlockEntityIndex = 0;
        baselineProcessedBlocks = 0L;
        pendingEventBlockEntities.Clear();
        pendingEventBlockEntityKeys.Clear();
        baselineOriginalKeys = originalBlocks.Keys.GroupBy(k => new ReplayChunkKey(k.X >> 5, k.Y >> 5, k.Z >> 5))
            .ToDictionary(g => g.Key, g => g.ToArray());
        historicalChunks = !document.HasHistoricalBaselines ? [] : document.WorldSnapshot.Chunks
            .Where(c => c.CapturedAtSeconds > targetTime).OrderBy(c => c.CapturedAtSeconds).ToArray();
        historicalChunkIndex = 0;
        restoreUnobservedEntities.Clear();
        foreach (var key in touchedBlockEntities)
        {
            if (IsFutureChunk(key, targetTime)) restoreUnobservedEntities.Enqueue(key);
        }
        ResetHistoricalChunk();
        deferredChunks?.Dispose();
        deferredChunks = DeferredEvents(targetTime).GetEnumerator();
        nextDeferredBlock = deferredChunks.MoveNext() ? deferredChunks.Current : null;
    }

    private IEnumerable<DirectorReplayBlockEvent> DeferredEvents(double targetTime)
    {
        int i = 0;
        // Indexed reconstruction already includes inferred completions at or before the seek.
        if (UseIndexedSeek)
            while (i < legacyCompletions.Count && legacyCompletions[i].TimeSeconds <= targetTime + .000001) i++;
        if (!document.HasHistoricalBaselines)
            foreach (var change in DirectorReplayDeferredChunks.Events(document.WorldSnapshot))
            {
                while (i < legacyCompletions.Count && legacyCompletions[i].TimeSeconds < change.TimeSeconds)
                    yield return legacyCompletions[i++];
                yield return change;
            }
        while (i < legacyCompletions.Count) yield return legacyCompletions[i++];
    }

    private int AdvanceBaseline(int budget, Stopwatch stopwatch)
    {
        IBulkBlockAccessor bulk = capi.World.GetBlockAccessorBulkUpdate(
            synchronize: false,
            relight: true);
        bulk.ReadFromStagedByDefault = true;
        int staged = 0;
        while (budget > 0
            && HasBaselineTime(stopwatch)
            && baselineChunkIndex < document.WorldSnapshot.Chunks.Count)
        {
            DirectorReplayChunkSnapshot chunk = document.WorldSnapshot.Chunks[baselineChunkIndex];
            var chunkKey = new ReplayChunkKey(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ);
            // Before the first frame these columns still contain live terrain. Restore them
            // from the recorded baseline and position history as the camera approaches.
            if (firstBaseline && observation is not null && !InObservation(chunkKey)
                && !baselineChunkPrepared && chunk.CapturedAtSeconds == 0)
            {
                deferredBaseline.Add(chunkKey);
                baselineProcessedBlocks += ChunkBlockCount;
                CompleteBaselineChunk();
                continue;
            }
            if (document.HasHistoricalBaselines && chunk.CapturedAtSeconds > baselineTime)
            {
                // Preserve the live background where this clip has no historical baseline yet.
                var originals = baselineOriginalKeys.GetValueOrDefault(new(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ)) ?? [];
                while (budget > 0 && HasBaselineTime(stopwatch) && baselineBlockIndex < originals.Length)
                {
                    var key = originals[baselineBlockIndex++];
                    if (originalBlocks.TryGetValue(key, out var original))
                    {
                        bulk.SetBlock(original.SolidBlockId, key.ToBlockPos(), BlockLayersAccess.Solid);
                        bulk.SetBlock(original.FluidBlockId, key.ToBlockPos(), BlockLayersAccess.Fluid);
                        staged++;
                        if (staged >= CommitBatchSize) { bulk.Commit(); bulk.ReadFromStagedByDefault = true; staged = 0; }
                    }
                    budget--;
                }
                if (baselineBlockIndex >= originals.Length) { baselineProcessedBlocks += ChunkBlockCount; CompleteBaselineChunk(); }
                continue;
            }
            if (!baselineChunkPrepared)
            {
                baselineSolid = ArrayPool<int>.Shared.Rent(ChunkBlockCount);
                baselineFluid = ArrayPool<int>.Shared.Rent(ChunkBlockCount);
                DirectorReplayChunkCodec.DecodeInto(
                    !document.HasHistoricalBaselines && chunk.CapturedAtSeconds > 0 ? null : chunk.SolidRuns,
                    baselineSolid.AsSpan(0, ChunkBlockCount));
                DirectorReplayChunkCodec.DecodeInto(
                    !document.HasHistoricalBaselines && chunk.CapturedAtSeconds > 0 ? null : chunk.FluidRuns,
                    baselineFluid.AsSpan(0, ChunkBlockCount));
                baselineChangedIndices.Clear();
                baselineChangedIndex = 0;
                baselineBlockIndex = 0;
                baselineComparisonIndex = 0;
                baselineComparisonComplete = false;
                baselineUsesChangedIndices = false;
                baselineChunkPrepared = true;
            }

            if (!baselineComparisonComplete)
            {
                ChunkComparisonResult result = AdvanceChangedBlockComparison(
                    chunk,
                    baselineSolid!,
                    baselineFluid!,
                    baselineChangedIndices,
                    ref baselineComparisonIndex,
                    ref budget,
                    stopwatch);
                if (result == ChunkComparisonResult.Pending)
                {
                    break;
                }

                baselineComparisonComplete = true;
                baselineUsesChangedIndices = result == ChunkComparisonResult.Complete;
                if (!baselineUsesChangedIndices)
                {
                    baselineChangedIndices.Clear();
                }
            }

            if (baselineUsesChangedIndices)
            {
                while (budget > 0
                    && HasBaselineTime(stopwatch)
                    && baselineChangedIndex < baselineChangedIndices.Count)
                {
                    int index = baselineChangedIndices[baselineChangedIndex++];
                    staged += StageBaselineBlock(bulk, chunk, index, baselineSolid!, baselineFluid!);
                    budget--;
                    if (staged >= CommitBatchSize)
                    {
                        bulk.Commit();
                        bulk.ReadFromStagedByDefault = true;
                        staged = 0;
                    }
                }

                if (baselineChangedIndex < baselineChangedIndices.Count)
                {
                    break;
                }

                baselineProcessedBlocks += ChunkBlockCount;
                CompleteBaselineChunk();
                continue;
            }

            while (budget > 0
                && HasBaselineTime(stopwatch)
                && baselineBlockIndex < ChunkBlockCount)
            {
                int index = baselineBlockIndex++;
                staged += StageBaselineBlock(bulk, chunk, index, baselineSolid!, baselineFluid!);
                baselineProcessedBlocks++;
                budget--;
                if (staged >= CommitBatchSize)
                {
                    bulk.Commit();
                    bulk.ReadFromStagedByDefault = true;
                    staged = 0;
                }
            }

            if (baselineBlockIndex >= ChunkBlockCount)
            {
                CompleteBaselineChunk();
            }
        }

        if (staged > 0)
        {
            bulk.Commit();
        }

        if (baselineChunkIndex >= document.WorldSnapshot.Chunks.Count
            && AdvanceBaselineBlockEntities(ref budget, stopwatch)
            && pendingChunkRedraws.Count == 0)
        {
            nextBlockEvent = UseIndexedSeek ? LastEventAtOrBefore(baselineTime) : 0;
            if (UseIndexedSeek)
            {
                indexedSeek = IndexedStateAt(baselineTime).GetEnumerator();
                nextIndexedChange = indexedSeek.MoveNext() ? indexedSeek.Current : null;
            }
            appliedTime = 0d;
            phase = HasPendingEvent(requestedTime) ? ApplyPhase.Events : ApplyPhase.None;
            if (phase == ApplyPhase.None)
            {
                appliedTime = requestedTime;
                synchronizing = false;
            }
        }

        return budget;
    }

    private int StageBaselineBlock(
        IBulkBlockAccessor bulk,
        DirectorReplayChunkSnapshot chunk,
        int index,
        int[] solid,
        int[] fluid)
    {
        int localX = index % ChunkSize;
        int localZ = index / ChunkSize % ChunkSize;
        int localY = index / (ChunkSize * ChunkSize);
        BlockPos position = new(
            chunk.ChunkX * ChunkSize + localX,
            chunk.ChunkY * ChunkSize + localY,
            chunk.ChunkZ * ChunkSize + localZ,
            document.WorldSnapshot.Dimension);
        return StageBlockPair(bulk, position, solid[index], fluid[index]);
    }

    private bool AdvanceBaselineBlockEntities(ref int budget, Stopwatch stopwatch)
    {
        while (budget >= BlockEntityOperationCost && HasBaselineTime(stopwatch)
            && restoreUnobservedEntities.TryDequeue(out var key))
        {
            var position = key.ToBlockPos();
            if (originalBlockEntities.Remove(key, out var original))
            {
                RecreateBlockEntity(original);
                if (capi.World.BlockAccessor.GetBlockEntity(position) is { } restored)
                    UnmarkReplayBlockEntity(restored);
            }
            else RemoveBlockEntity(position);
            touchedBlockEntities.Remove(key);
            originalBlocks.Remove(key);
            budget -= BlockEntityOperationCost;
        }
        if (restoreUnobservedEntities.Count > 0) return false;
        baselineExtraBlockEntities ??= new List<BlockPos>();
        if (!baselineExtraScanComplete)
        {
            baselineExtraScanComplete = AdvanceExtraBlockEntityScan(ref budget, stopwatch);
            if (!baselineExtraScanComplete)
            {
                return false;
            }
        }

        while (budget >= BlockEntityOperationCost
            && HasBaselineTime(stopwatch)
            && baselineExtraBlockEntityIndex < baselineExtraBlockEntities.Count)
        {
            BlockPos position = baselineExtraBlockEntities[baselineExtraBlockEntityIndex++];
            PreserveOriginalBlockEntity(position);
            RemoveBlockEntity(position);
            budget -= BlockEntityOperationCost;
        }

        if (baselineExtraBlockEntityIndex < baselineExtraBlockEntities.Count)
        {
            return false;
        }

        while (budget >= BlockEntityScanCost
            && HasBaselineTime(stopwatch)
            && baselineBlockEntityIndex < document.WorldSnapshot.BlockEntities.Count)
        {
            DirectorReplayBlockEntitySnapshot snapshot =
                document.WorldSnapshot.BlockEntities[baselineBlockEntityIndex++];
            if (deferredBaseline.Contains(new(snapshot.X >> 5, snapshot.Y >> 5, snapshot.Z >> 5))) continue;
            if (IsFutureChunk(new(snapshot.X, snapshot.Y, snapshot.Z, snapshot.Dimension), baselineTime))
            {
                budget -= BlockEntityScanCost;
                continue;
            }
            if (budget < BlockEntityOperationCost)
            {
                baselineBlockEntityIndex--;
                break;
            }
            RecreateBlockEntity(snapshot);
            budget -= BlockEntityOperationCost;
        }

        return baselineBlockEntityIndex >= document.WorldSnapshot.BlockEntities.Count;
    }

    private bool AdvanceExtraBlockEntityScan(ref int budget, Stopwatch stopwatch)
    {
        while (budget >= BlockEntityScanCost
            && HasBaselineTime(stopwatch)
            && baselineExtraChunkIndex < document.WorldSnapshot.Chunks.Count)
        {
            DirectorReplayChunkSnapshot chunkSnapshot =
                document.WorldSnapshot.Chunks[baselineExtraChunkIndex];
            if (deferredBaseline.Contains(new(chunkSnapshot.ChunkX, chunkSnapshot.ChunkY, chunkSnapshot.ChunkZ)))
            { baselineExtraChunkIndex++; continue; }
            if (document.HasHistoricalBaselines && chunkSnapshot.CapturedAtSeconds > baselineTime)
            { baselineExtraChunkIndex++; continue; }
            int internalChunkY = chunkSnapshot.ChunkY
                + document.WorldSnapshot.Dimension * GlobalConstants.DimensionSizeInChunks;
            IWorldChunk? chunk = game.WorldMap.GetChunk(
                chunkSnapshot.ChunkX,
                internalChunkY,
                chunkSnapshot.ChunkZ);
            if (baselineExtraChunkPositions is null)
            {
                baselineExtraChunkPositions = DirectorGameCompatibility.BlockEntities(chunk)
                    .Select(entity => new BlockPos(
                        entity.Pos.X,
                        entity.Pos.Y,
                        entity.Pos.Z,
                        document.WorldSnapshot.Dimension))
                    .ToArray();
                baselineExtraChunkPositionIndex = 0;
            }

            while (budget >= BlockEntityScanCost
                && HasBaselineTime(stopwatch)
                && baselineExtraChunkPositionIndex < baselineExtraChunkPositions.Length)
            {
                BlockPos position = baselineExtraChunkPositions[baselineExtraChunkPositionIndex++];
                if (!baselineBlockEntities.Contains(BlockKey.From(position)))
                {
                    baselineExtraBlockEntities!.Add(position);
                }
                budget -= BlockEntityScanCost;
            }

            if (baselineExtraChunkPositionIndex < baselineExtraChunkPositions.Length)
            {
                return false;
            }

            baselineExtraChunkPositions = null;
            baselineExtraChunkPositionIndex = 0;
            baselineExtraChunkIndex++;
        }

        return baselineExtraChunkIndex >= document.WorldSnapshot.Chunks.Count;
    }

    private void CompleteBaselineChunk()
    {
        baselineChunkIndex++;
        baselineBlockIndex = 0;
        ReleaseBaselineArrays();
        baselineChunkPrepared = false;
        baselineComparisonIndex = 0;
        baselineComparisonComplete = false;
        baselineUsesChangedIndices = false;
        baselineChangedIndices.Clear();
        baselineChangedIndex = 0;
    }

    private void ReleaseBaselineArrays()
    {
        if (baselineSolid is not null)
        {
            ArrayPool<int>.Shared.Return(baselineSolid);
            baselineSolid = null;
        }
        if (baselineFluid is not null)
        {
            ArrayPool<int>.Shared.Return(baselineFluid);
            baselineFluid = null;
        }
    }

    private static bool HasBaselineTime(Stopwatch stopwatch)
        => stopwatch.Elapsed.TotalMilliseconds < BaselineWorkBudgetMilliseconds;

    private ChunkComparisonResult AdvanceChangedBlockComparison(
        DirectorReplayChunkSnapshot snapshot,
        int[] solid,
        int[] fluid,
        List<int> changed,
        ref int comparisonIndex,
        ref int budget,
        Stopwatch stopwatch)
    {
        if (comparisonIndex >= ChunkBlockCount)
        {
            return ChunkComparisonResult.Complete;
        }
        if (budget <= 0 || !HasBaselineTime(stopwatch))
        {
            return ChunkComparisonResult.Pending;
        }

        int internalChunkY = snapshot.ChunkY
            + document.WorldSnapshot.Dimension * GlobalConstants.DimensionSizeInChunks;
        IWorldChunk? chunk = game.WorldMap.GetChunk(snapshot.ChunkX, internalChunkY, snapshot.ChunkZ);
        if (chunk is null || chunk.Disposed || (!chunk.Unpack_ReadOnly() && chunk.Data is null))
        {
            return ChunkComparisonResult.Unavailable;
        }

        IChunkBlocks? data = chunk.Data;
        if (data is null)
        {
            return ChunkComparisonResult.Unavailable;
        }

        data.TakeBulkReadLock();
        try
        {
            while (budget > 0
                && HasBaselineTime(stopwatch)
                && comparisonIndex < ChunkBlockCount)
            {
                int index = comparisonIndex++;
                if (data.GetBlockIdUnsafe(index) != solid[index]
                    || data.GetFluid(index) != fluid[index])
                {
                    changed.Add(index);
                }
            }
        }
        finally
        {
            data.ReleaseBulkReadLock();
        }

        return comparisonIndex >= ChunkBlockCount
            ? ChunkComparisonResult.Complete
            : ChunkComparisonResult.Pending;
    }

    private void AdvanceEvents(int budget, Stopwatch stopwatch)
    {
        IBulkBlockAccessor? bulk = null;
        int staged = 0;
        while (HasPendingEvent(requestedTime)
            && HasBaselineTime(stopwatch)
            && budget > 0)
        {
            if (nextIndexedChange is null && historicalChunkIndex < historicalChunks.Length
                && historicalChunks[historicalChunkIndex].CapturedAtSeconds <= requestedTime + 0.000001d
                && (nextBlockEvent >= document.BlockEvents.Count
                    || historicalChunks[historicalChunkIndex].CapturedAtSeconds <= document.BlockEvents[nextBlockEvent].TimeSeconds))
            {
                if (staged > 0) { bulk!.Commit(); staged = 0; }
                AdvancePendingEventBlockEntities(ref budget, stopwatch);
                if (pendingEventBlockEntities.Count > 0) break;
                if (!AdvanceHistoricalChunk(ref budget, stopwatch)) break;
                continue;
            }
            bool indexed = nextIndexedChange is not null;
            bool deferred = !indexed && nextDeferredBlock is not null && (nextBlockEvent >= document.BlockEvents.Count
                || nextDeferredBlock.TimeSeconds <= document.BlockEvents[nextBlockEvent].TimeSeconds);
            DirectorReplayBlockEvent change = indexed ? nextIndexedChange! : deferred ? nextDeferredBlock! : document.BlockEvents[nextBlockEvent];
            BlockPos position = new(change.X, change.Y, change.Z, change.Dimension);
            BlockKey key = BlockKey.From(position);
            if (change.TimeSeconds == 0 && legacyEmptyStarts.Contains(key)) change = blockEventsByPosition[key][0];
            // An event without captured BE state is an opaque transition. Apply
            // the previous authoritative BE state before changing its block.
            if (!change.BlockEntityCaptured && pendingEventBlockEntities.ContainsKey(key))
            {
                break;
            }

            if (indexed) nextIndexedChange = indexedSeek!.MoveNext() ? indexedSeek.Current : null;
            else if (deferred) nextDeferredBlock = deferredChunks!.MoveNext() ? deferredChunks.Current : null;
            else nextBlockEvent++;
            budget--;
            appliedTime = Math.Max(appliedTime, change.TimeSeconds);
            if (change.Dimension == document.WorldSnapshot.Dimension &&
                deferredBaseline.Contains(new(change.X >> 5, change.Y >> 5, change.Z >> 5))) continue;
            if (bulk is null)
            {
                bulk = capi.World.GetBlockAccessorBulkUpdate(synchronize: false, relight: true);
                bulk.ReadFromStagedByDefault = true;
            }
            staged += StageBlockPair(bulk, position, change.SolidBlockId, change.FluidBlockId);
            if (staged >= CommitBatchSize)
            {
                bulk.Commit();
                bulk.ReadFromStagedByDefault = true;
                staged = 0;
            }

            if (change.BlockEntityCaptured)
            {
                if (!pendingEventBlockEntities.ContainsKey(key))
                {
                    pendingEventBlockEntityKeys.Enqueue(key);
                }
                pendingEventBlockEntities[key] = change;
            }
        }

        if (staged > 0)
        {
            bulk!.Commit();
        }
        AdvancePendingEventBlockEntities(ref budget, stopwatch);

        if (!HasPendingEvent(requestedTime)
            && pendingEventBlockEntities.Count == 0
            && pendingEventBlockEntityKeys.Count == 0)
        {
            appliedTime = requestedTime;
            phase = ApplyPhase.None;
            synchronizing = false;
        }
    }

    private void AdvancePendingEventBlockEntities(ref int budget, Stopwatch stopwatch)
    {
        DiscardStalePendingEventBlockEntityKeys();
        while (budget >= BlockEntityOperationCost
            && HasBaselineTime(stopwatch)
            && pendingEventBlockEntityKeys.Count > 0)
        {
            BlockKey key = pendingEventBlockEntityKeys.Peek();
            DirectorReplayBlockEvent change = pendingEventBlockEntities[key];

            BlockPos position = new(change.X, change.Y, change.Z, change.Dimension);
            if (string.IsNullOrWhiteSpace(change.BlockEntityClass))
            {
                RemoveBlockEntity(position);
            }
            else
            {
                ApplyBlockEntity(
                    new DirectorReplayBlockEntitySnapshot
                    {
                        X = change.X,
                        Y = change.Y,
                        Z = change.Z,
                        Dimension = change.Dimension,
                        ClassName = change.BlockEntityClass,
                        Data = change.BlockEntityData
                    },
                    // A state transition can finish an animation between
                    // the entity update and the next render frame. Always
                    // rebuild this event's chunk so the static mesh receives
                    // the closed/open door state deterministically.
                    forceRedraw: true);
            }

            pendingEventBlockEntityKeys.Dequeue();
            pendingEventBlockEntities.Remove(key);
            budget -= BlockEntityOperationCost;
            DiscardStalePendingEventBlockEntityKeys();
        }

    }

    private void DiscardStalePendingEventBlockEntityKeys()
    {
        while (pendingEventBlockEntityKeys.Count > 0
            && !pendingEventBlockEntities.ContainsKey(pendingEventBlockEntityKeys.Peek()))
        {
            pendingEventBlockEntityKeys.Dequeue();
        }
    }

    private bool HasPendingEvent(double timeSeconds)
        => nextIndexedChange is not null || (nextBlockEvent < document.BlockEvents.Count
            && document.BlockEvents[nextBlockEvent].TimeSeconds <= timeSeconds + 0.000001d)
            || (nextDeferredBlock is not null && nextDeferredBlock.TimeSeconds <= timeSeconds + 0.000001d)
            || (historicalChunkIndex < historicalChunks.Length && historicalChunks[historicalChunkIndex].CapturedAtSeconds <= timeSeconds + 0.000001d);

    private void ResetHistoricalChunk()
    {
        historicalSolid = historicalFluid = null;
        historicalComparisonIndex = historicalChangeIndex = 0;
        historicalCompared = false;
        historicalBlockEntities = null;
        historicalBlockEntityIndex = 0;
        historicalSnapshotIndex = 0;
        historicalChanges.Clear();
    }

    private bool AdvanceHistoricalChunk(ref int budget, Stopwatch stopwatch)
    {
        var chunk = historicalChunks[historicalChunkIndex];
        historicalSolid ??= DirectorReplayChunkCodec.Decode(chunk.SolidRuns, ChunkBlockCount);
        historicalFluid ??= DirectorReplayChunkCodec.Decode(chunk.FluidRuns, ChunkBlockCount);
        if (!historicalCompared)
        {
            var result = AdvanceChangedBlockComparison(chunk, historicalSolid, historicalFluid, historicalChanges,
                ref historicalComparisonIndex, ref budget, stopwatch);
            if (result == ChunkComparisonResult.Pending) return false;
            if (result == ChunkComparisonResult.Unavailable)
            {
                historicalChanges.Clear();
                for (int i = 0; i < ChunkBlockCount; i++) historicalChanges.Add(i);
            }
            historicalCompared = true;
        }
        var bulk = capi.World.GetBlockAccessorBulkUpdate(synchronize: false, relight: true);
        bulk.ReadFromStagedByDefault = true;
        int staged = 0;
        while (budget > 0 && HasBaselineTime(stopwatch) && historicalChangeIndex < historicalChanges.Count)
        {
            staged += StageBaselineBlock(bulk, chunk, historicalChanges[historicalChangeIndex++], historicalSolid, historicalFluid);
            budget--;
            if (staged >= CommitBatchSize) { bulk.Commit(); bulk.ReadFromStagedByDefault = true; staged = 0; }
        }
        if (staged > 0) bulk.Commit();
        if (historicalChangeIndex < historicalChanges.Count) return false;
        // The baseline is authoritative even where the block IDs did not change.
        // Captured block-entity events at this timestamp are applied after the chunk.
        historicalBlockEntities ??= DirectorGameCompatibility.BlockEntities(game.WorldMap.GetChunk(chunk.ChunkX,
            chunk.ChunkY + document.WorldSnapshot.Dimension * GlobalConstants.DimensionSizeInChunks,
            chunk.ChunkZ)).Select(entity => new BlockPos(entity.Pos.X, entity.Pos.Y, entity.Pos.Z,
                document.WorldSnapshot.Dimension)).ToArray();
        while (historicalBlockEntityIndex < historicalBlockEntities.Length
            && budget >= BlockEntityOperationCost && HasBaselineTime(stopwatch))
        {
            BlockPos position = historicalBlockEntities[historicalBlockEntityIndex++];
            if (!baselineBlockEntities.Contains(BlockKey.From(position))) RemoveBlockEntity(position);
            budget -= BlockEntityOperationCost;
        }
        if (historicalBlockEntityIndex < historicalBlockEntities.Length) return false;
        if (entitiesByChunk.TryGetValue(new(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ), out var snapshots))
        {
            while (historicalSnapshotIndex < snapshots.Length && budget >= BlockEntityOperationCost && HasBaselineTime(stopwatch))
            {
                RecreateBlockEntity(snapshots[historicalSnapshotIndex++]);
                budget -= BlockEntityOperationCost;
            }
            if (historicalSnapshotIndex < snapshots.Length) return false;
        }
        appliedTime = Math.Max(appliedTime, chunk.CapturedAtSeconds);
        historicalChunkIndex++;
        ResetHistoricalChunk();
        return true;
    }

    private int LastEventAtOrBefore(double timeSeconds)
    {
        int low = 0;
        int high = document.BlockEvents.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (document.BlockEvents[middle].TimeSeconds <= timeSeconds + 0.000001d)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int LastPositionEvent(IReadOnlyList<DirectorReplayBlockEvent> changes, double time)
    {
        int lo = 0, hi = changes.Count;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (changes[mid].TimeSeconds <= time + 0.000001d) lo = mid + 1; else hi = mid; }
        return lo - 1;
    }

    private IEnumerable<DirectorReplayBlockEvent> IndexedStateAt(double time)
    {
        foreach (var changes in blockEventsByPosition.Values)
        {
            int index = LastPositionEvent(changes, time);
            if (index < 0) continue;
            var change = changes[index].DeepClone();
            if (!change.BlockEntityCaptured)
            {
                for (int i = index - 1; i >= 0; i--)
                {
                    var previous = changes[i];
                    if (!previous.BlockEntityCaptured) continue;
                    change.BlockEntityCaptured = true; change.BlockEntityClass = previous.BlockEntityClass;
                    change.BlockEntityData = previous.BlockEntityData; break;
                }
            }
            yield return change;
        }
    }

    private int StageBlockPair(
        IBulkBlockAccessor bulk,
        BlockPos position,
        int requestedSolid,
        int requestedFluid)
    {
        int currentSolid = bulk.GetBlock(position, BlockLayersAccess.Solid).BlockId;
        int currentFluid = bulk.GetBlock(position, BlockLayersAccess.Fluid).BlockId;
        int solid = FilterSolidBlock(requestedSolid, position);
        int fluid = FilterFluidBlock(requestedFluid);
        if (currentSolid == solid && currentFluid == fluid)
        {
            return 0;
        }

        BlockKey key = BlockKey.From(position);
        originalBlocks.TryAdd(key, new BlockPair(currentSolid, currentFluid));
        PreserveOriginalBlockEntity(position);
        int count = 0;
        if (currentSolid != solid)
        {
            bulk.SetBlock(solid, position, BlockLayersAccess.Solid);
            count++;
        }

        if (currentFluid != fluid)
        {
            bulk.SetBlock(fluid, position, BlockLayersAccess.Fluid);
            count++;
        }

        QueueChunkRedraw(position);

        return count;
    }

    private void AdvanceHistoryRefresh(int budget, Stopwatch timer)
    {
        bool wasApplying = BeginApplyingState();
        var bulk = capi.World.GetBlockAccessorBulkUpdate(synchronize: false, relight: true);
        bulk.ReadFromStagedByDefault = true; int staged = 0;
        try
        {
            while (budget > 0 && HasBaselineTime(timer))
            {
                if (historyRefreshChunk is null)
                {
                    int queued = historyRefreshQueue.Count;
                    while (queued-- > 0 && !InObservation(historyRefreshQueue.Peek()))
                        historyRefreshQueue.Enqueue(historyRefreshQueue.Dequeue());
                    if (!historyRefreshQueue.TryDequeue(out var next)) break;
                    historyRefreshChunk = next; historyRefreshIndex = 0;
                    if (baselineChunks.TryGetValue(next, out var baseline))
                    {
                        bool captured = baseline.CapturedAtSeconds <= requestedTime;
                        historySolid = DirectorReplayChunkCodec.Decode(captured ? baseline.SolidRuns : null, ChunkBlockCount);
                        historyFluid = DirectorReplayChunkCodec.Decode(captured ? baseline.FluidRuns : null, ChunkBlockCount);
                    }
                }
                var chunk = historyRefreshChunk.Value;
                bool synthetic = backgroundRefresh == chunk;
                if (synthetic && !backgroundCompared)
                {
                    var result = AdvanceChangedBlockComparison(baselineChunks[chunk], historySolid!, historyFluid!,
                        backgroundIndices, ref backgroundComparison, ref budget, timer);
                    if (result == ChunkComparisonResult.Pending) break;
                    if (result == ChunkComparisonResult.Unavailable)
                    {
                        // A later packet enqueues a fresh restoration if this column was unloaded.
                        if (Loaded(chunk)) backgroundIndices.AddRange(Enumerable.Range(0, ChunkBlockCount));
                    }
                    if (Loaded(chunk))
                    {
                        static int Local(int x, int y, int z) => (y & 31) * 1024 + (z & 31) * 32 + (x & 31);
                        if (recordedChangesByChunk.TryGetValue(chunk, out var changedPositions))
                            backgroundIndices.AddRange(changedPositions.Select(k => Local(k.X, k.Y, k.Z)));
                        if (entitiesByChunk.TryGetValue(chunk, out var entities))
                            backgroundIndices.AddRange(entities.Select(e => Local(e.X, e.Y, e.Z)));
                        var live = game.WorldMap.GetChunk(chunk.X, chunk.Y + document.WorldSnapshot.Dimension * GlobalConstants.DimensionSizeInChunks, chunk.Z);
                        backgroundIndices.AddRange(DirectorGameCompatibility.BlockEntities(live).Select(e => Local(e.Pos.X, e.Pos.Y, e.Pos.Z)));
                    }
                    backgroundCompared = true;
                    if (backgroundIndices.Count == 0 || !Loaded(chunk))
                    {
                        historyRefreshSet.Remove(chunk);
                        historyRefreshChunk = backgroundRefresh = null;
                        historySolid = historyFluid = null;
                        continue;
                    }
                }
                int index = synthetic ? backgroundIndices[historyRefreshIndex++] : historyRefreshIndex++;
                var sparse = historySolid is null ? sparsePositions[chunk] : null;
                var key = sparse is null
                    ? new BlockKey(chunk.X * 32 + index % 32, chunk.Y * 32 + index / 1024, chunk.Z * 32 + index / 32 % 32, document.WorldSnapshot.Dimension)
                    : sparse[index];
                var position = key.ToBlockPos();
                int solid = historySolid?[index] ?? 0, fluid = historyFluid?[index] ?? 0;
                bool known = sparse is null;
                if (historyRefreshIndex >= (synthetic ? backgroundIndices.Count : sparse?.Length ?? ChunkBlockCount))
                {
                    historyRefreshSet.Remove(chunk); historyRefreshChunk = null; historySolid = historyFluid = null;
                    if (synthetic) backgroundRefresh = null;
                }
                budget--;
                if (blockEventsByPosition.TryGetValue(key, out var changes))
                {
                    int at = LastPositionEvent(changes, requestedTime);
                    if (at >= 0)
                    {
                        var change = changes[at];
                        solid = change.SolidBlockId; fluid = change.FluidBlockId;
                        known = true;
                    }
                }
                if (!known) continue;
                int liveSolid = capi.World.BlockAccessor.GetBlock(position, BlockLayersAccess.Solid).Id;
                int liveFluid = capi.World.BlockAccessor.GetBlock(position, BlockLayersAccess.Fluid).Id;
                if (!synthetic && (liveSolid != solid || liveFluid != fluid || originalBlocks.ContainsKey(key)))
                    originalBlocks[key] = new(liveSolid, liveFluid);
                TryGetReplayBlockEntity(position, requestedTime, out var entity);
                bool restoreEntity = entity is not null || capi.World.BlockAccessor.GetBlockEntity(position) is not null;
                if (!synthetic && (restoreEntity || touchedBlockEntities.Contains(key))) PreserveLatestRealityBlockEntity(position, key);
                staged += StageBlockPair(bulk, position, solid, fluid);
                if (restoreEntity)
                {
                    if (staged > 0) { bulk.Commit(); bulk.ReadFromStagedByDefault = true; staged = 0; }
                    if (entity is not null) RecreateBlockEntity(entity); else RemoveBlockEntity(position);
                    budget -= BlockEntityOperationCost;
                }
                if (staged >= CommitBatchSize) { bulk.Commit(); bulk.ReadFromStagedByDefault = true; staged = 0; }
            }
            if (staged > 0) bulk.Commit();
        }
        finally { applyingState(wasApplying); }
    }

    private int FilterSolidBlock(int requestedBlockId, BlockPos position)
    {
        if (!ValidBlockId(requestedBlockId))
        {
            return 0;
        }
        DirectorReplayWorldVisuals world = document.ViewportTimeline.Visuals.World;
        if (!world.ShowTerrain || requestedBlockId == 0)
        {
            return 0;
        }
        return requestedBlockId;
    }

    private int FilterFluidBlock(int requestedBlockId)
    {
        if (!ValidBlockId(requestedBlockId))
        {
            return 0;
        }
        DirectorReplayVisualSettings visuals = document.ViewportTimeline.Visuals;
        if (!visuals.World.ShowTerrain || requestedBlockId == 0)
        {
            return 0;
        }
        return requestedBlockId;
    }

    private void PreserveOriginalBlockEntity(BlockPos position)
    {
        BlockKey key = BlockKey.From(position);
        if (!touchedBlockEntities.Add(key))
        {
            return;
        }

        BlockEntity? blockEntity = capi.World.BlockAccessor.GetBlockEntity(position);
        if (blockEntity is null)
        {
            return;
        }

        try
        {
            originalBlockEntities[key] =
                DirectorReplaySerialization.CaptureBlockEntity(capi, position, blockEntity);
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "VS Director: could not preserve block entity at {0}: {1}",
                position,
                exception.Message);
        }
    }

    private void ApplyBlockEntity(
        DirectorReplayBlockEntitySnapshot snapshot,
        bool forceRedraw = false,
        bool forceRecreate = false)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ClassName))
        {
            return;
        }

        // A replay can outlive a mod that registered one of its block entity
        // classes. Never pass an unknown class name into the game registry:
        // Vintage Story throws from SpawnBlockEntity on the render thread.
        Type? registeredClass;
        try { registeredClass = capi.ClassRegistry.GetBlockEntity(snapshot.ClassName); }
        catch (Exception exception)
        {
            registeredClass = null;
            LogUnavailableBlockEntity(snapshot.ClassName, exception.Message);
        }
        if (registeredClass is null)
        {
            LogUnavailableBlockEntity(snapshot.ClassName, "class is not registered by the current mod set");
            return;
        }

        BlockPos position = new(snapshot.X, snapshot.Y, snapshot.Z, snapshot.Dimension);
        PreserveOriginalBlockEntity(position);
        BlockKey key = BlockKey.From(position);
        touchedBlockEntities.Add(key);
        BlockEntity? current = capi.World.BlockAccessor.GetBlockEntity(position);
        string? currentClass = current is null
            ? null
            : capi.ClassRegistry.GetBlockEntityClass(current.GetType());
        bool replaced = forceRecreate
            || current is null
            || !string.Equals(currentClass, snapshot.ClassName, StringComparison.Ordinal);
        if (replaced)
        {
            if (current is not null)
            {
                UnmarkReplayBlockEntity(current);
                capi.World.BlockAccessor.RemoveBlockEntity(position);
            }

            try
            {
                capi.World.BlockAccessor.SpawnBlockEntity(snapshot.ClassName, position);
            }
            catch (Exception exception)
            {
                LogUnavailableBlockEntity(snapshot.ClassName, exception.Message);
                return;
            }
            current = capi.World.BlockAccessor.GetBlockEntity(position);
        }

        if (current is not null)
        {
            MarkReplayBlockEntity(current);
            DirectorReplaySerialization.ApplyBlockEntityData(
                current,
                snapshot.Data,
                capi.World,
                redrawOnClient: false);
            Action? animationRetessellated =
                DirectorReplaySerialization.CreateActiveAnimationRetessellationCallback(current);
            if (animationRetessellated is not null)
            {
                int internalChunkY = (position.Y >> 5)
                    + position.dimension * GlobalConstants.DimensionSizeInChunks;
                ReplayRemoteChunkPriorityPatch.RetessellateExistingChunkInOrder(
                    game.WorldMap,
                    position.X >> 5,
                    internalChunkY,
                    position.Z >> 5,
                    animationRetessellated);
            }
            // Existing block entities are responsible for requesting a redraw
            // when their visual state changes (for example, a beehive only
            // changes its mesh when Harvestable flips). Rebuilding the whole
            // chunk for every serialized timer/counter update is prohibitively
            // expensive during a replay event burst. New/replaced entities do
            // need one explicit mesh invalidation. Active animations are
            // promoted separately so detached-camera transitions do not wait
            // behind terrain near the real player.
            if (replaced || forceRedraw)
            {
                QueueChunkRedraw(position);
            }
        }
    }

    private void LogUnavailableBlockEntity(string className, string reason)
    {
        if (!unavailableBlockEntityClasses.Add(className)) return;
        capi.Logger.Warning(
            "VS Director: replay skipped unavailable block entity '{0}': {1}",
            className,
            reason);
    }

    private void RemoveBlockEntity(BlockPos position)
    {
        PreserveOriginalBlockEntity(position);
        touchedBlockEntities.Add(BlockKey.From(position));
        BlockEntity? current = capi.World.BlockAccessor.GetBlockEntity(position);
        if (current is not null)
        {
            UnmarkReplayBlockEntity(current);
            capi.World.BlockAccessor.RemoveBlockEntity(position);
            QueueChunkRedraw(position);
        }
    }

    private void MarkReplayBlockEntity(BlockEntity blockEntity)
        => replayBlockEntities.GetValue(
            blockEntity,
            static _ => new ReplayBlockEntityMarker());

    private void UnmarkReplayBlockEntity(BlockEntity blockEntity)
    {
        DirectorReplaySerialization.InvalidateAnimationOwners(blockEntity);
        replayBlockEntities.Remove(blockEntity);
    }

    private void QueueChunkRedraw(BlockPos position)
    {
        int internalChunkY = (position.Y >> 5)
            + position.dimension * GlobalConstants.DimensionSizeInChunks;
        ChunkRedraw redraw = new(
            position.X >> 5,
            internalChunkY,
            position.Z >> 5);
        if (pendingChunkRedrawSet.Add(redraw))
        {
            pendingChunkRedraws.Enqueue(redraw);
        }
    }

    private void RecreateBlockEntity(DirectorReplayBlockEntitySnapshot snapshot)
        => ApplyBlockEntity(snapshot, forceRedraw: true, forceRecreate: true);

    public void ObserveLiveBlockChange(BlockPos position)
        => QueueLiveRealityObservation(position, LiveRealityObservationKind.Block);

    public void ObserveLiveBlockEntityState(BlockPos position)
        => QueueLiveRealityObservation(position, LiveRealityObservationKind.BlockEntityState);

    public void ObserveLiveBlockEntityPacket(BlockPos position)
        => QueueLiveRealityObservation(position, LiveRealityObservationKind.BlockEntityPacket);

    private void QueueLiveRealityObservation(
        BlockPos position,
        LiveRealityObservationKind kind)
    {
        if (position is null)
        {
            return;
        }

        BlockKey key = BlockKey.From(position);
        bool onMainThread = Environment.CurrentManagedThreadId == mainThreadId;
        if (onMainThread && isApplyingState())
        {
            return;
        }

        bool processImmediately = onMainThread && !ReplayChunkEntityInitializationPatch.IsInitializing;
        bool enqueueMainThreadTask = false;
        lock (liveRealityObservationSync)
        {
            if (disposed)
            {
                return;
            }

            pendingLiveRealityObservations.TryGetValue(
                key,
                out LiveRealityObservationKind pending);
            pendingLiveRealityObservations[key] = pending | kind;
            if (!processImmediately && !liveRealityObservationTaskQueued)
            {
                liveRealityObservationTaskQueued = true;
                enqueueMainThreadTask = true;
            }
        }

        if (processImmediately)
        {
            ProcessPendingLiveRealityObservations();
            return;
        }

        if (!enqueueMainThreadTask)
        {
            return;
        }

        try
        {
            capi.Event.EnqueueMainThreadTask(
                ProcessPendingLiveRealityObservations,
                LiveRealityObservationTaskCode);
        }
        catch (Exception exception)
        {
            lock (liveRealityObservationSync)
            {
                liveRealityObservationTaskQueued = false;
                pendingLiveRealityObservations.Clear();
            }
            capi.Logger.Warning(
                "VS Director: live replay-world isolation could not be queued from thread {0}: {1}",
                Environment.CurrentManagedThreadId,
                exception.GetBaseException().Message);
        }
    }

    private void ProcessPendingLiveRealityObservations()
    {
        KeyValuePair<BlockKey, LiveRealityObservationKind>[] observations;
        lock (liveRealityObservationSync)
        {
            liveRealityObservationTaskQueued = false;
            if (disposed || pendingLiveRealityObservations.Count == 0)
            {
                pendingLiveRealityObservations.Clear();
                return;
            }

            observations = pendingLiveRealityObservations.ToArray();
            pendingLiveRealityObservations.Clear();
        }

        if (!started || disposed)
        {
            return;
        }

        // Playback applies a replay snapshot under the same boundary used by
        // this queue. A network notification can therefore land between the
        // dequeue and the end of that boundary. Keep it queued until the
        // replay write is complete; dropping it leaves the live-world backup
        // stale and lets a later callback overwrite the replay state.
        if (isApplyingState())
        {
            RequeueLiveRealityObservations(observations);
            return;
        }

        foreach ((BlockKey key, LiveRealityObservationKind kind) in observations)
        {
            ProcessLiveRealityObservation(key, kind);
        }
    }

    private void RequeueLiveRealityObservations(
        IReadOnlyList<KeyValuePair<BlockKey, LiveRealityObservationKind>> observations)
    {
        bool enqueueMainThreadTask = false;
        lock (liveRealityObservationSync)
        {
            if (disposed)
            {
                return;
            }

            foreach ((BlockKey key, LiveRealityObservationKind kind) in observations)
            {
                pendingLiveRealityObservations.TryGetValue(
                    key,
                    out LiveRealityObservationKind pending);
                pendingLiveRealityObservations[key] = pending | kind;
            }

            if (pendingLiveRealityObservations.Count > 0
                && !liveRealityObservationTaskQueued)
            {
                liveRealityObservationTaskQueued = true;
                enqueueMainThreadTask = true;
            }
        }

        if (!enqueueMainThreadTask)
        {
            return;
        }

        try
        {
            capi.Event.EnqueueMainThreadTask(
                ProcessPendingLiveRealityObservations,
                LiveRealityObservationTaskCode);
        }
        catch (Exception exception)
        {
            lock (liveRealityObservationSync)
            {
                liveRealityObservationTaskQueued = false;
            }
            capi.Logger.Warning(
                "VS Director: deferred live replay-world isolation could not be rescheduled: {0}",
                exception.GetBaseException().Message);
        }
    }

    private void ProcessLiveRealityObservation(
        BlockKey key,
        LiveRealityObservationKind kind)
    {
        BlockPos position = key.ToBlockPos();
        if (!CanObserveReality(position, out BlockPair replayBlocks))
        {
            return;
        }

        bool wasApplyingState = BeginApplyingState();
        try
        {
            if ((kind & LiveRealityObservationKind.Block) != 0)
            {
                originalBlocks[key] = new BlockPair(
                    capi.World.BlockAccessor.GetBlock(
                        position,
                        BlockLayersAccess.Solid).BlockId,
                    capi.World.BlockAccessor.GetBlock(
                        position,
                        BlockLayersAccess.Fluid).BlockId);
            }

            if ((kind & (LiveRealityObservationKind.BlockEntityState
                | LiveRealityObservationKind.BlockEntityPacket)) != 0)
            {
                PreserveLatestRealityBlockEntity(position, key);
            }

            ReapplyReplayPosition(position, replayBlocks);
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "VS Director: queued live replay-world isolation failed at {0}, {1}, {2} " +
                "(dimension {3}, observation {4}, expected main thread {5}, current thread {6}): {7}",
                key.X,
                key.Y,
                key.Z,
                key.Dimension,
                kind,
                mainThreadId,
                Environment.CurrentManagedThreadId,
                exception.GetBaseException().Message);
        }
        finally
        {
            applyingState(wasApplyingState);
        }
    }

    public bool OwnsReplayBlockEntitySound(Vec3f position)
    {
        BlockPos blockPosition = new(
            (int)Math.Floor(position.X),
            (int)Math.Floor(position.Y),
            (int)Math.Floor(position.Z),
            document.WorldSnapshot.Dimension);
        return TryGetReplayBlockEntity(
                blockPosition,
                requestedTime,
                out DirectorReplayBlockEntitySnapshot? snapshot)
            && snapshot is not null;
    }

    private bool CanObserveReality(BlockPos position, out BlockPair replayBlocks)
    {
        replayBlocks = default;
        return started
            && !disposed
            && TryGetReplayBlockPair(position, requestedTime, out replayBlocks);
    }

    private void PreserveLatestRealityBlockEntity(BlockPos position, BlockKey key)
    {
        touchedBlockEntities.Add(key);
        BlockEntity? blockEntity = capi.World.BlockAccessor.GetBlockEntity(position);
        if (blockEntity is null)
        {
            originalBlockEntities.Remove(key);
            return;
        }

        originalBlockEntities[key] = CreateBlockEntitySnapshot(blockEntity, position);
    }

    private DirectorReplayBlockEntitySnapshot CreateBlockEntitySnapshot(
        BlockEntity blockEntity,
        BlockPos position)
        => new()
        {
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            Dimension = position.dimension,
            ClassName = capi.ClassRegistry.GetBlockEntityClass(blockEntity.GetType()) ?? string.Empty,
            Data = DirectorReplaySerialization.SerializeBlockEntity(blockEntity)
        };

    private void ReapplyReplayPosition(BlockPos position, BlockPair replayBlocks)
    {
        bool wasApplyingState = BeginApplyingState();
        try
        {
            IBulkBlockAccessor bulk = capi.World.GetBlockAccessorBulkUpdate(
                synchronize: false,
                relight: true);
            bulk.ReadFromStagedByDefault = true;
            if (StageBlockPair(
                    bulk,
                    position,
                    replayBlocks.SolidBlockId,
                    replayBlocks.FluidBlockId) > 0)
            {
                bulk.Commit();
            }

            if (TryGetReplayBlockEntity(position, requestedTime, out DirectorReplayBlockEntitySnapshot? snapshot)
                && snapshot is not null)
            {
                RecreateBlockEntity(snapshot);
            }
            else
            {
                RemoveBlockEntity(position);
            }
        }
        finally
        {
            applyingState(wasApplyingState);
        }
    }

    private bool TryGetReplayBlockPair(
        BlockPos position,
        double timeSeconds,
        out BlockPair blocks)
    {
        BlockKey key = BlockKey.From(position);
        bool found = false;
        blocks = default;
        if (position.dimension == document.WorldSnapshot.Dimension
            && baselineChunks.TryGetValue(
                new ReplayChunkKey(position.X >> 5, position.Y >> 5, position.Z >> 5),
                out DirectorReplayChunkSnapshot? chunk))
        {
            if (document.HasHistoricalBaselines && chunk.CapturedAtSeconds > timeSeconds) return false;
            int index = (position.Y & (ChunkSize - 1)) * ChunkSize * ChunkSize
                + (position.Z & (ChunkSize - 1)) * ChunkSize
                + (position.X & (ChunkSize - 1));
            blocks = new BlockPair(
                chunk.CapturedAtSeconds <= timeSeconds ? ReadBlockRun(chunk.SolidRuns, index) : 0,
                chunk.CapturedAtSeconds <= timeSeconds ? ReadBlockRun(chunk.FluidRuns, index) : 0);
            found = true;
        }

        if (blockEventsByPosition.TryGetValue(key, out IReadOnlyList<DirectorReplayBlockEvent>? changes))
        {
            int index = LastPositionEvent(changes, timeSeconds);
            if (index >= 0)
            {
                var change = changes[index];
                blocks = new BlockPair(change.SolidBlockId, change.FluidBlockId);
                found = true;
            }
        }

        return found;
    }

    private bool TryGetReplayBlockEntity(
        BlockPos position,
        double timeSeconds,
        out DirectorReplayBlockEntitySnapshot? snapshot)
    {
        BlockKey key = BlockKey.From(position);
        snapshot = null;
        if (IsFutureChunk(key, timeSeconds)) return false;
        bool found = position.dimension == document.WorldSnapshot.Dimension
            && baselineChunks.ContainsKey(
                new ReplayChunkKey(position.X >> 5, position.Y >> 5, position.Z >> 5));
        baselineBlockEntitySnapshots.TryGetValue(key, out snapshot);

        if (blockEventsByPosition.TryGetValue(key, out IReadOnlyList<DirectorReplayBlockEvent>? changes))
        {
            for (int index = LastPositionEvent(changes, timeSeconds); index >= 0; index--)
            {
                var change = changes[index];
                found = true;
                if (!change.BlockEntityCaptured)
                {
                    continue;
                }

                snapshot = string.IsNullOrWhiteSpace(change.BlockEntityClass)
                    ? null
                    : new DirectorReplayBlockEntitySnapshot
                    {
                        X = change.X,
                        Y = change.Y,
                        Z = change.Z,
                        Dimension = change.Dimension,
                        ClassName = change.BlockEntityClass,
                        Data = change.BlockEntityData
                    };
                break;
            }
        }

        return found;
    }

    private bool IsFutureChunk(BlockKey key, double time)
        => document.HasHistoricalBaselines && key.Dimension == document.WorldSnapshot.Dimension
            && baselineChunks.TryGetValue(new(key.X >> 5, key.Y >> 5, key.Z >> 5), out var chunk)
            && chunk.CapturedAtSeconds > time;

    private static int ReadBlockRun(IReadOnlyList<DirectorReplayBlockRun> runs, int index)
    {
        int offset = 0;
        foreach (DirectorReplayBlockRun run in runs)
        {
            int end = offset + Math.Max(0, run.Length);
            if (index < end)
            {
                return run.BlockId;
            }
            offset = end;
        }

        return 0;
    }

    private void AdvancePendingChunkRedraws(int maximum, Stopwatch stopwatch)
    {
        int remaining = Math.Max(0, maximum);
        while (remaining-- > 0
            && pendingChunkRedraws.Count > 0
            && HasBaselineTime(stopwatch))
        {
            ChunkRedraw redraw = pendingChunkRedraws.Dequeue();
            pendingChunkRedrawSet.Remove(redraw);
            try
            {
                ReplayRemoteChunkPriorityPatch.RetessellateExistingChunkInOrder(
                    game.WorldMap,
                    redraw.ChunkX,
                    redraw.ChunkY,
                    redraw.ChunkZ,
                    onRetessellated: null);
            }
            catch
            {
                // A deferred update can race a chunk unload. The preload path
                // will rebuild that chunk if it becomes visible again.
            }
        }
    }

    private void FlushPendingChunkRedraws()
    {
        while (pendingChunkRedraws.Count > 0)
        {
            ChunkRedraw redraw = pendingChunkRedraws.Dequeue();
            pendingChunkRedrawSet.Remove(redraw);
            try
            {
                ReplayRemoteChunkPriorityPatch.RetessellateExistingChunkInOrder(
                    game.WorldMap,
                    redraw.ChunkX,
                    redraw.ChunkY,
                    redraw.ChunkZ,
                    onRetessellated: null);
            }
            catch
            {
                // A deferred update can race a chunk unload. The preload path
                // will rebuild that chunk if it becomes visible again.
            }
        }
        pendingChunkRedrawSet.Clear();
    }

    private bool ValidBlockId(int blockId)
        => blockId >= 0 && blockId < game.Blocks.Count;

    public void Dispose()
    {
        indexedSeek?.Dispose(); indexedSeek = null; nextIndexedChange = null;
        ResetHistoricalChunk();
        historicalChunks = [];
        baselineOriginalKeys.Clear();
        deferredChunks?.Dispose();
        deferredChunks = null;
        nextDeferredBlock = null;
        lock (liveRealityObservationSync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            liveRealityObservationTaskQueued = false;
            pendingLiveRealityObservations.Clear();
        }

        ReleaseBaselineArrays();
        if (!started)
        {
            return;
        }

        bool wasApplyingState = BeginApplyingState();
        try
        {
            IBulkBlockAccessor bulk = capi.World.GetBlockAccessorBulkUpdate(
                synchronize: false,
                relight: true);
            bulk.ReadFromStagedByDefault = true;
            int staged = 0;
            foreach ((BlockKey key, BlockPair original) in originalBlocks)
            {
                BlockPos position = key.ToBlockPos();
                bulk.SetBlock(original.SolidBlockId, position, BlockLayersAccess.Solid);
                bulk.SetBlock(original.FluidBlockId, position, BlockLayersAccess.Fluid);
                QueueChunkRedraw(position);
                staged += 2;
                if (staged >= CommitBatchSize)
                {
                    bulk.Commit();
                    bulk.ReadFromStagedByDefault = true;
                    staged = 0;
                }
            }

            if (staged > 0)
            {
                bulk.Commit();
            }

            foreach (BlockKey key in touchedBlockEntities)
            {
                BlockPos position = key.ToBlockPos();
                if (originalBlockEntities.TryGetValue(key, out DirectorReplayBlockEntitySnapshot? original))
                {
                    RecreateBlockEntity(original);
                }
                else if (capi.World.BlockAccessor.GetBlockEntity(position) is not null)
                {
                    capi.World.BlockAccessor.RemoveBlockEntity(position);
                    QueueChunkRedraw(position);
                }
            }
            FlushPendingChunkRedraws();
        }
        catch (Exception exception)
        {
            capi.Logger.Error("VS Director: failed to restore replay world state: {0}", exception);
        }
        finally
        {
            applyingState(wasApplyingState);
        }
    }

    private bool BeginApplyingState()
    {
        bool wasApplying = isApplyingState();
        applyingState(true);
        return wasApplying;
    }

    private readonly record struct BlockPair(int SolidBlockId, int FluidBlockId);

    private sealed class ReplayBlockEntityMarker
    {
    }

    [Flags]
    private enum LiveRealityObservationKind
    {
        None = 0,
        Block = 1,
        BlockEntityState = 2,
        BlockEntityPacket = 4
    }

    private enum ApplyPhase
    {
        None,
        Baseline,
        Events
    }

    private enum ChunkComparisonResult
    {
        Pending,
        Complete,
        Unavailable
    }

    private readonly record struct ChunkRedraw(int ChunkX, int ChunkY, int ChunkZ);

    private readonly record struct ReplayChunkKey(int X, int Y, int Z);

    private readonly record struct BlockKey(int X, int Y, int Z, int Dimension)
    {
        public static BlockKey From(BlockPos position)
            => new(position.X, position.Y, position.Z, position.dimension);

        public BlockPos ToBlockPos() => new(X, Y, Z, Dimension);
    }
}
