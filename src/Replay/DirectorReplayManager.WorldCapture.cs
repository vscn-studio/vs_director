using System.Diagnostics;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Replay;

internal sealed partial class DirectorReplayManager
{
    private readonly Queue<ChunkKey> pendingWorldCapture = new();
    private double nextWorldCaptureScan;

    private void CaptureAdditionalWorldChunks()
    {
        if (recording is not { } active || recordingPaused || !CaptureWorld || applyingReplayState) return;
        if (recordingElapsedSeconds >= nextWorldCaptureScan)
        {
            nextWorldCaptureScan = recordingElapsedSeconds + .5;
            pendingWorldCapture.Clear();
            var centers = new List<(double X, double Y, double Z, int Dimension)>();
            if (capi.World.Player?.Entity is { } player)
                centers.Add((player.Pos.X, player.Pos.Y, player.Pos.Z, player.Pos.Dimension));
            if (controller.CurrentPose is { } camera)
                centers.Add((camera.X, camera.Y, camera.Z, camera.Dimension));
            foreach (var chunk in DirectorReplayCaptureArea.MissingChunks(active.WorldSnapshot, centers,
                Math.Clamp(config.ReplaySnapshotRadiusChunks, 0, 16)))
                pendingWorldCapture.Enqueue(new(chunk.X, chunk.Y, chunk.Z, active.WorldSnapshot.Dimension));
        }
        var timer = Stopwatch.StartNew();
        int attempts = 0;
        while (attempts++ < 4 && timer.Elapsed.TotalMilliseconds < 4 && pendingWorldCapture.TryDequeue(out var key))
            CaptureRecordingChunk(key);
    }

    // Capture before the first write, including all blocks in a tree's chunk.
    // Tick capture alone can lose a fast block change immediately after moving.
    internal void CaptureBlockBeforeChange(BlockPos position)
    {
        if (recording is not { } active || recordingPaused || !CaptureWorld || !ShouldTrackDirectBlockChanges
            || !WithinObservedCaptureWorld(active, position)) return;
        CaptureRecordingChunk(ChunkKey.FromBlock(BlockKey.From(position)));
    }

    private bool CaptureRecordingChunk(ChunkKey key)
    {
        if (recording is not { } active || recordingPaused || !CaptureWorld || applyingReplayState
            || key.Dimension != active.WorldSnapshot.Dimension || key.Y < 0
            || key.Y * 32 >= active.WorldSnapshot.MapSizeY) return false;
        if (recordingSnapshotChunks.ContainsKey(key)) return true;
        var addition = new DirectorReplayWorldSnapshot { Dimension = key.Dimension };
        if (!DirectorReplayWorldCapture.CaptureChunk(capi, game, addition, key.X, key.Y, key.Z, recordingElapsedSeconds))
            return false;
        var captured = addition.Chunks.Single();
        active.WorldSnapshot.FollowCaptureArea = true;
        active.WorldSnapshot.Chunks.Add(captured);
        active.WorldSnapshot.BlockEntities.AddRange(addition.BlockEntities);
        recordingSnapshotChunks.Add(key, captured);
        foreach (var entity in addition.BlockEntities)
            recordingBlockEntities[BlockKey.From(entity)] = new CachedBlockEntity(entity.ClassName, CloneBytes(entity.Data));
        active.IncludesWorldSnapshot = true;
        active.CaptureScope = DirectorReplayDocument.ClientWorldCaptureScope;
        return true;
    }
}
