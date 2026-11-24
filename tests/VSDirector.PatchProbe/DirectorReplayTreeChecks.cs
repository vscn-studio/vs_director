using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using VSDirector;
using VSDirector.Data;

internal static class DirectorReplayTreeChecks
{
    // Replace only the game storage/mesh dependencies. Replay baseline, events,
    // seek, chunk refresh, busy/ready gates and cleanup execute production code.
    private static IEnumerable<CodeInstruction> NoChunkComparison(IEnumerable<CodeInstruction> instructions)
        => [new(OpCodes.Ldc_I4_2), new(OpCodes.Ret)];
    private static bool NoBlockEntities(ref bool __result) { __result = true; return false; }
    private static void NoHistoricalBlockEntities(ref BlockPos[]? ___historicalBlockEntities)
        => ___historicalBlockEntities ??= [];
    private static bool KnownBlock(int blockId, ref bool __result) { __result = blockId is >= 0 and <= 2; return false; }
    private static Action? receivedChunk;
    private static bool CaptureChunkNotification() { receivedChunk?.Invoke(); return false; }
    private static bool SkipGameChunkLoad() => false;

    internal static void Run(string? inspectPath)
    {
        if (inspectPath is not null) Inspect(inspectPath);
        var type = typeof(VSDirectorModSystem).Assembly.GetType("VSDirector.Replay.DirectorReplayWorldPlayback", true)!;
        var harmony = new Harmony("vsdirector.probe.replay-tree");
        var chunkPatch = typeof(VSDirectorModSystem).Assembly.GetType("VSDirector.Replay.ReplayRemoteChunkPriorityPatch", true)!;
        var logger = ProbeDispatchProxy.Create<ILogger>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        if (AccessTools.Method(chunkPatch, "TryPatch").Invoke(null, [harmony, logger]) is not true)
            throw new Exception("Chunk replacement hook did not install");
        harmony.Patch(AccessTools.Method(chunkPatch, "Postfix"), prefix: new HarmonyMethod(typeof(DirectorReplayTreeChecks), nameof(CaptureChunkNotification)));
        foreach (string method in new[] { "loadChunkMT", "OverloadChunkMT" })
            harmony.Patch(AccessTools.Method(typeof(ClientWorldMap), method), prefix: new HarmonyMethod(typeof(DirectorReplayTreeChecks), nameof(SkipGameChunkLoad)));
        harmony.Patch(AccessTools.Method(type, "AdvanceChangedBlockComparison"), transpiler: new HarmonyMethod(typeof(DirectorReplayTreeChecks), nameof(NoChunkComparison)));
        harmony.Patch(AccessTools.Method(type, "AdvanceExtraBlockEntityScan"), prefix: new HarmonyMethod(typeof(DirectorReplayTreeChecks), nameof(NoBlockEntities)));
        harmony.Patch(AccessTools.Method(type, "AdvanceHistoricalChunk"), prefix: new HarmonyMethod(typeof(DirectorReplayTreeChecks), nameof(NoHistoricalBlockEntities)));
        harmony.Patch(AccessTools.Method(type, "ValidBlockId"), prefix: new HarmonyMethod(typeof(DirectorReplayTreeChecks), nameof(KnownBlock)));
        try
        {
            foreach (bool paged in new[] { false, true })
            foreach (int baselineKind in new[] { 0, 1, 2 })
            foreach (double capturedAt in new[] { 0d, 3d })
                CheckTree(type, paged, capturedAt, baselineKind);
        }
        finally { receivedChunk = null; harmony.UnpatchAll(harmony.Id); }
        Console.WriteLine("PASS 12 tree replay variants: standing/partially cut/fully cut, rewind, chunk replacement, delayed/moving/historical capture, paged seek and live-world restoration");
    }

    private static void CheckTree(Type type, bool paged, double capturedAt, int baselineKind)
    {
        bool historical = baselineKind != 0;
        int[] baseline = new int[32768];
        baseline[1 + 32 + 1024] = 1; baseline[1 + 32 + 2048] = 1; baseline[1 + 32 + 3072] = 2;
        var document = new DirectorReplayDocument { DurationSeconds = 12 };
        if (baselineKind == 1) document.SessionMerge = new DirectorSessionMergeOrigin();
        if (baselineKind == 2) document.WorldSnapshot.FollowCaptureArea = true;
        document.WorldSnapshot.Chunks.Add(new DirectorReplayChunkSnapshot { ChunkX = 10, ChunkY = 0, ChunkZ = 10,
            CapturedAtSeconds = capturedAt, SolidRuns = DirectorReplayChunkCodec.Encode(baseline), FluidRuns = [] });
        for (int y = 1; y <= 3; y++) document.BlockEvents.Add(new DirectorReplayBlockEvent
            { TimeSeconds = y == 3 ? 5 : 7, X = 321, Y = y, Z = 321, SolidBlockId = 0, FluidBlockId = 0 });
        if (!historical)
        {
            document.BlockEvents.Add(new() { X=353, Y=1, Z=321, SolidBlockId=1, BlockEntityCaptured=true });
            document.BlockEvents.Add(new() { X=353, Y=1, Z=321, SolidBlockId=1, BlockEntityCaptured=true,
                BlockEntityClass="KnappingSurface", TimeSeconds=4.5 });
            document.SoundEvents.Add(new() { X=353.5, Y=1, Z=321.5, TimeSeconds=5.1, Location="game:sounds/player/knap2.ogg" });
            document.SoundEvents.Add(new() { X=353.5, Y=1, Z=321.5, TimeSeconds=5.2, Location="game:sounds/player/collect3.ogg" });
        }
        document.BlockEvents = document.BlockEvents.OrderBy(e => e.TimeSeconds).ToList();
        string? temp = null;
        if (paged)
        {
            temp = Path.Combine(Path.GetTempPath(), "director-tree-" + Guid.NewGuid().ToString("N") + ".vsdreplay.gz");
            DirectorReplayIO.Save(temp, document);
            DirectorReplayPages.Write(temp, document);
            document = DirectorReplayPages.TryOpen(temp)!;
            document.Pages!.PrefetchEnabled = false;
        }
        var values = new Dictionary<(int X, int Y, int Z, int Layer), int>();
        Block[] blocks = [new() { BlockId = 0 }, new() { BlockId = 1 }, new() { BlockId = 2 }];
        object? Access(MethodInfo m, object?[]? args)
        {
            if (m.Name == "GetBlock" && args![0] is BlockPos read)
                return blocks[values.GetValueOrDefault((read.X, read.Y, read.Z, (int)args[1]!))];
            if (m.Name == "SetBlock" && args![1] is BlockPos write)
                values[(write.X, write.Y, write.Z, (int)args[2]!)] = (int)args[0]!;
            return ProbeDispatchProxy.DefaultValue(m.ReturnType);
        }
        var bulk = ProbeDispatchProxy.Create<IBulkBlockAccessor>(Access);
        var world = ProbeDispatchProxy.Create<IClientWorldAccessor>((m, _) => m.Name switch
        {
            "get_BlockAccessor" => bulk, "GetBlockAccessorBulkUpdate" => bulk, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var logger = ProbeDispatchProxy.Create<ILogger>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Logger" => logger, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        bool applying = false;
        var player = Activator.CreateInstance(type, api, null, document, (Action<bool>)(v => applying = v), (Func<bool>)(() => applying))!;
        object? Call(string method, params object[] args) => AccessTools.Method(type, method).Invoke(player, args);
        bool Flag(string property) => (bool)AccessTools.Property(type, property).GetValue(player)!;
        void Settle(double time, bool seek = false)
        {
            Call("ApplyAt", time, seek);
            // Exactly the same IsBusy gate as DirectorReplayManager.ApplyPlaybackFrame.
            for (int frame = 0; frame < 3000 && Flag("IsBusy"); frame++)
            { Call("ApplyAt", time, false); Call("Advance", 32768); }
            if (Flag("IsBusy") || Flag("IsSynchronizing") || !Flag("IsReady"))
                throw new Exception($"Replay never settled after a tree chunk reload: paged={paged}, historical={historical}, captured={capturedAt}, time={time}, busy={Flag("IsBusy")}, sync={Flag("IsSynchronizing")}");
            if (applying) throw new Exception("Replay state guard leaked");
        }
        void Tree(bool standing)
        {
            for (int y = 1; y <= 3; y++)
                if (values.GetValueOrDefault((321, y, 321, BlockLayersAccess.Solid)) != (standing ? y == 3 ? 2 : 1 : 0))
                    throw new Exception($"Wrong tree state: standing={standing}, y={y}, paged={paged}, historical={historical}, capture={capturedAt}");
        }
        try
        {
            Call("Start"); Settle(4); Tree(true);
            // Drive the installed engine hooks, not just the world-player queue.
            var map = (ClientWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldMap));
            AccessTools.Field(typeof(ClientWorldMap), "chunksLock").SetValue(map, new object());
            AccessTools.Field(typeof(WorldMap), "index3dMulX").SetValue(map, 1024);
            AccessTools.Field(typeof(WorldMap), "index3dMulZ").SetValue(map, 1024);
            var received = (ClientChunk)RuntimeHelpers.GetUninitializedObject(typeof(ClientChunk));
            var old = (ClientChunk)RuntimeHelpers.GetUninitializedObject(typeof(ClientChunk));
            long chunkKey = MapUtil.Index3dL(10, 0, 10, 1024, 1024);
            var loaded = new Dictionary<long, ClientChunk> { [chunkKey] = old };
            AccessTools.Field(typeof(ClientWorldMap), "chunks").SetValue(map, loaded);
            int notifications = 0;
            receivedChunk = () => { notifications++; Call("ObserveServerRecordingChunkLoad", 10, 0, 10); };
            var packet = new Packet_ServerChunk { X = 10, Y = 0, Z = 10 };
            var overload = AccessTools.Method(typeof(ClientWorldMap), "OverloadChunkMT");
            overload.Invoke(map, [packet, received]);
            if (notifications != 0) throw new Exception("Deferred replacement was treated as installed");
            loaded[chunkKey] = received; values.Clear();
            overload.Invoke(map, [packet, received]);
            if (notifications != 1 || !Flag("IsBusy")) throw new Exception("Existing chunk replacement bypassed historical reconstruction");
            Settle(4); Tree(true);
            values.Clear(); AccessTools.Method(typeof(ClientWorldMap), "loadChunkMT").Invoke(map, [packet, received]);
            if (notifications != 2) throw new Exception("Initial chunk load bypassed historical reconstruction");
            Settle(4); Tree(true);
            Settle(6);
            if (values.GetValueOrDefault((321,3,321,BlockLayersAccess.Solid)) != 0
                || values.GetValueOrDefault((321,1,321,BlockLayersAccess.Solid)) != 1)
                throw new Exception("Partial felling did not preserve the uncut trunk");
            Settle(4, true); Tree(true);
            Settle(8); Tree(false);
            Settle(4, true); Tree(true);
            values.Clear(); // A real server/preload column contains the already felled tree.
            Call("ObserveServerRecordingChunkLoad", 10, 0, 10);
            Call("ObserveServerRecordingChunkLoad", 10, 0, 10);
            if (!Flag("IsBusy") || !Flag("IsSynchronizing") || Flag("IsReady"))
                throw new Exception("Incoming live chunks were incorrectly reported as ready");
            Call("Advance", 1); // Even the minimum budget cannot finish a whole chunk.
            if (!Flag("IsBusy") || Flag("IsReady")) throw new Exception("Partial chunk refresh was reported as ready");
            values.Clear(); // A second packet replaces even the already restored prefix.
            Call("ObserveServerRecordingChunkLoad", 10, 0, 10);
            Settle(4); Tree(true);
            for (int frame = 0; frame < 8; frame++) { Settle(4); Tree(true); }
            Settle(8); Tree(false);
            Call("ObserveServerRecordingChunkLoad", 10, 0, 10); Settle(8); Tree(false);
            Settle(0, true); Tree(capturedAt == 0);
            values[(321, 1, 321, BlockLayersAccess.Solid)] = 1;
            Call("ObserveServerRecordingChunkLoad", 10, 0, 10); Settle(0);
            if (historical && capturedAt > 0)
            {
                if (values.GetValueOrDefault((321, 1, 321, BlockLayersAccess.Solid)) != 1)
                    throw new Exception("Historical replay erased live terrain before its first captured baseline");
            }
            else Tree(capturedAt == 0);
            values.Clear(); Call("ObserveServerRecordingChunkLoad", 10, 0, 10); Settle(4); Tree(true);
            if (!historical)
            {
                var spot = (353, 1, 321, BlockLayersAccess.Solid);
                var neighbor = (354, 1, 321, BlockLayersAccess.Solid);
                values[spot] = 2; values[neighbor] = 1;
                Call("ObserveServerRecordingChunkLoad", 11, 0, 10); Settle(4);
                if (values[spot] != 0 || values[neighbor] != 1) throw new Exception("Sparse refresh leaked live terrain or overwrote an unrecorded neighbor");
                Settle(5);
                if (values[spot] != 1) throw new Exception("Missing working surface during knapping");
                Settle(5.21);
                if (values[spot] != 0) throw new Exception("Sequential playback retained a finished knapping surface");
                Settle(4, true); Settle(12, true);
                if (values[spot] != 0) throw new Exception("Seeking past completion retained a finished knapping surface");
            }
        }
        finally
        {
            ((IDisposable)player).Dispose();
            if (temp is not null) { File.Delete(temp); File.Delete(temp + ".pages"); }
        }
        Tree(false); // Leaving replay must preserve the real-world felling.
        if (!historical && values[(353,1,321,BlockLayersAccess.Solid)] != 2)
            throw new Exception("Exiting sparse playback did not restore latest live block");
    }

    private static void Inspect(string path)
    {
        var document = DirectorReplayPages.TryOpen(path) ?? throw new Exception("Replay page cache unavailable; original recording left untouched");
        var chunks = document.WorldSnapshot.Chunks.ToDictionary(c => (c.ChunkX, c.ChunkY, c.ChunkZ));
        int removals = 0, baselinePresent = 0;
        foreach (var position in document.Pages!.BlockPositions)
        {
            var removed = position.Indices.Select(i => document.BlockEvents[i]).FirstOrDefault(e => e.SolidBlockId == 0);
            if (removed is null) continue;
            removals++;
            if (!chunks.TryGetValue((position.X >> 5, position.Y >> 5, position.Z >> 5), out var chunk) || chunk.CapturedAtSeconds > removed.TimeSeconds) continue;
            int index = (position.Y & 31) * 1024 + (position.Z & 31) * 32 + (position.X & 31), offset = 0;
            foreach (var run in chunk.SolidRuns)
            {
                offset += run.Length;
                if (offset <= index) continue;
                if (run.BlockId != 0) baselinePresent++;
                Console.WriteLine($"Removed ({position.X},{position.Y},{position.Z}) baseline={run.BlockId} captured={chunk.CapturedAtSeconds:0.000} first-removal={removed.TimeSeconds:0.000}; events="
                    + string.Join(",", position.Indices.Select(i => document.BlockEvents[i]).Select(e => $"{e.TimeSeconds:0.000}:{e.SolidBlockId}")));
                break;
            }
        }
        Console.WriteLine($"READ-ONLY recording inspection: {document.BlockEvents.Count} block events; {removals} removed positions; {baselinePresent} retain a non-air pre-removal baseline");
        Console.WriteLine($"World dimension={document.WorldSnapshot.Dimension}, chunks={chunks.Count}, duration={document.DurationSeconds}, historical={document.HasHistoricalBaselines}");
        Console.WriteLine($"Snapshot center=({document.WorldSnapshot.CenterChunkX},{document.WorldSnapshot.CenterChunkZ}), radius={document.WorldSnapshot.RadiusChunks}; chunk ranges X={chunks.Keys.Min(c => c.ChunkX)}..{chunks.Keys.Max(c => c.ChunkX)}, Z={chunks.Keys.Min(c => c.ChunkZ)}..{chunks.Keys.Max(c => c.ChunkZ)}");
        foreach (var actor in document.Actors.Where(a => !string.IsNullOrEmpty(a.PlayerUid)))
        {
            var samples = actor.Samples.ToArray();
            var chopping = samples.Where(s => s.AnimationStates.Any(a => a.Code == "axechop")).ToArray();
            if (chopping.Length > 0) Console.WriteLine($"Axe chopping: t={chopping[0].TimeSeconds:0.000}..{chopping[^1].TimeSeconds:0.000}, pos=({chopping[0].X:0.0},{chopping[0].Y:0.0},{chopping[0].Z:0.0})..({chopping[^1].X:0.0},{chopping[^1].Y:0.0},{chopping[^1].Z:0.0})");
            if (chopping.Length > 0)
            {
                var s = chopping[0]; int x = (int)Math.Floor(s.X) >> 5, y = (int)Math.Floor(s.Y) >> 5, z = (int)Math.Floor(s.Z) >> 5;
                Console.WriteLine($"Chopping chunk=({x},{y},{z}), captured={chunks.ContainsKey((x,y,z))}; nearby block events="
                    + document.BlockEvents.Count(e => Math.Abs(e.X-s.X) < 20 && Math.Abs(e.Z-s.Z) < 20));
            }
            Console.WriteLine($"Player actions: {string.Join(", ", samples.SelectMany(s => s.AnimationStates).Select(a => a.Code + "/" + a.StateCode).Distinct())}");
            var throws = samples.Where(s => s.AnimationStates.Any(a => a.Code.Contains("throw", StringComparison.OrdinalIgnoreCase) || a.StateCode.Contains("throw", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (throws.Length == 0) continue;
            Console.WriteLine($"Actor {actor.ActorKey}: {actor.Samples.Count} samples, throw samples={throws.Length}, first={throws[0].TimeSeconds}, last={throws[^1].TimeSeconds}");
            foreach (var s in throws.Take(3).Concat(throws.TakeLast(3)))
                foreach (var a in s.AnimationStates.Where(a => a.Code.Contains("throw", StringComparison.OrdinalIgnoreCase) || a.StateCode.Contains("throw", StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"  t={s.TimeSeconds} {a.Code}/{a.StateCode} p={a.Progress} active={a.IsActive} running={a.IsRunning} weight={a.BlendedWeight} ease={a.EasingFactor} hand={s.HandUse}");
        }
    }
}
