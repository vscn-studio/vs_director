using HarmonyLib;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VSDirector;
using VSDirector.Data;

internal static class DirectorReplayIsolationChecks
{
    private static readonly Type Playback = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayWorldPlayback", true)!;

    private static object Open(DirectorReplayDocument doc)
        => Activator.CreateInstance(Playback, null, null, doc, (Action<bool>)(_ => { }), (Func<bool>)(() => false))!;

    private static void Check(object playback, BlockPos position, double time, int? expected, string? entity)
    {
        object?[] blockArgs = [position, time, null], entityArgs = [position, time, null];
        bool blockKnown = (bool)AccessTools.Method(Playback, "TryGetReplayBlockPair").Invoke(playback, blockArgs)!;
        bool entityKnown = (bool)AccessTools.Method(Playback, "TryGetReplayBlockEntity").Invoke(playback, entityArgs)!;
        var pair = blockArgs[2]!;
        int? solid = blockKnown ? (int)AccessTools.Property(pair.GetType(), "SolidBlockId").GetValue(pair)! : null;
        var snapshot = entityArgs[2] as DirectorReplayBlockEntitySnapshot;
        if (solid != expected || entityKnown != expected.HasValue || snapshot?.ClassName != entity)
            throw new Exception($"Isolation at {position} t={time}: solid={solid}, entity={snapshot?.ClassName}, expected {expected}/{entity}");
    }

    internal static void Run(string? path)
    {
        CheckGhostChunkUnload();
        CheckChunkInitialization();
        var pos = new BlockPos(33, 2, 4, 0);
        var doc = new DirectorReplayDocument();
        doc.BlockEvents.Add(new() { X=33, Y=2, Z=4, SolidBlockId=2, BlockEntityCaptured=true });
        doc.BlockEvents.Add(new() { X=33, Y=2, Z=4, SolidBlockId=2, BlockEntityCaptured=true,
            BlockEntityClass="KnappingSurface", TimeSeconds=2 });
        doc.SoundEvents.Add(new() { TimeSeconds=2.1, X=33, Y=2, Z=4, Location="game:sounds/player/knap2.ogg" });
        doc.SoundEvents.Add(new() { TimeSeconds=2.12, X=32, Y=2, Z=4, Location="game:sounds/player/collect3.ogg" });
        var unfinished = Open(doc);
        Check(unfinished, pos, 0, 0, null);
        Check(unfinished, pos, 10, 2, "KnappingSurface");
        doc.SoundEvents[1].X = 33;
        var finished = Open(doc);
        Check(finished, pos, 2.11, 2, "KnappingSurface");
        Check(finished, pos, 2.13, 0, null);
        Check(finished, pos, 1, 0, null);
        if (doc.BlockEvents.Count != 2 || doc.BlockEvents[0].SolidBlockId != 2)
            throw new Exception("Compatibility lookup modified the source events");

        var timed = new DirectorReplayDocument();
        timed.WorldSnapshot.FollowCaptureArea = true;
        timed.WorldSnapshot.Chunks.Add(new() { ChunkX=1, CapturedAtSeconds=5,
            SolidRuns=[new() { Length=32768, BlockId=2 }] });
        timed.WorldSnapshot.BlockEntities.Add(new() { X=33, Y=2, Z=4, ClassName="Sapling" });
        var historical = Open(timed);
        Check(historical, pos, 0, null, null);
        Check(historical, pos, 5, 2, "Sapling");
        if (path is not null)
        {
            // The two work surfaces in Replay 20261123-203001 have different evidence.
            var real = DirectorReplayPages.TryOpen(path) ?? throw new Exception("Original replay cache unavailable");
            var player = Open(real);
            var sapling = new BlockPos(511977, 115, 511785, 0);
            var work = new BlockPos(511982, 128, 511801, 0);
            Check(player, sapling, 245.20, 0, null);
            Check(player, sapling, 245.21, 4791, "Sapling");
            Check(player, work, 130.02, 0, null);
            Check(player, work, 144.36, 10079, "KnappingSurface");
            Check(player, work, 144.38, 0, null);
            var firstWork = new BlockPos(511982, 111, 511958, 0);
            Check(player, firstWork, 29.37, 10079, "KnappingSurface");
            Check(player, firstWork, 29.38, 0, null);
            Console.WriteLine("PASS original paged recording: seed placement and both knapping completions");
        }
        Console.WriteLine("PASS sparse placement, unfinished knapping, completion, rewind lookup, timed block-entity visibility");
    }

    private sealed class ObservedEntity(Action<BlockPos> observe) : BlockEntity
    {
        public override void Initialize(ICoreAPI api) => observe(Pos);
    }

    private static void CheckGhostChunkUnload()
    {
        var managerType = Playback.Assembly.GetType("VSDirector.Replay.DirectorReplayManager", true)!;
        var manager = RuntimeHelpers.GetUninitializedObject(managerType);
        AccessTools.Field(managerType, "replayGhostIds").SetValue(manager, new HashSet<long> { -17 });
        var runtime = Playback.Assembly.GetType("VSDirector.Replay.DirectorReplayRuntime", true)!;
        var owner = AccessTools.Property(runtime, "Manager");
        var harmony = new Harmony("vsdirector.probe.ghost-chunk");
        var previous = owner.GetValue(null);
        try
        {
            owner.SetValue(null, manager);
            harmony.CreateClassProcessor(Playback.Assembly.GetType("VSDirector.Replay.ReplayGhostChunkMembershipPatch", true)!).Patch();
            var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            var map = (ClientWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldMap));
            game.WorldMap = map;
            var chunks = new Dictionary<long, ClientChunk>();
            AccessTools.Field(typeof(ClientWorldMap), "chunksLock").SetValue(map, new object());
            AccessTools.Field(typeof(ClientWorldMap), "chunks").SetValue(map, chunks);
            for (int i = 0; i < 2; i++)
            {
                var chunk = (ClientChunk)RuntimeHelpers.GetUninitializedObject(typeof(ClientChunk));
                AccessTools.Field(typeof(ClientChunk), "packUnpackLock").SetValue(chunk, new object());
                chunk.BlockEntities = new();
                chunks.Add(i, chunk);
            }
            var live = new EntityPlayer { EntityId = 17 };
            var ghost = new EntityPlayer { EntityId = -17 };
            live.WatchedAttributes.SetString("playerUID", "same-player");
            ghost.WatchedAttributes.SetString("playerUID", "same-player");
            var player = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
            var data = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
            AccessTools.Field(typeof(ClientWorldPlayerData), "entityplayer").SetValue(data, live);
            AccessTools.Field(typeof(ClientPlayer), "worlddata").SetValue(player, data);
            AccessTools.Field(typeof(ClientMain), "player").SetValue(game, player);
            chunks[0].AddEntity(live);
            chunks[0].AddEntity(ghost);
            game.UpdateEntityChunk(ghost, 1L);
            if (chunks[0].EntitiesCount != 1 || chunks[0].Entities[0] != live || chunks[1].EntitiesCount != 0)
                throw new Exception("Replay player entered a live terrain chunk during spawn or migration");
            var unloadType = typeof(ClientMain).Assembly.GetType("Vintagestory.Client.NoObf.SystemUnloadChunks", true)!;
            var unload = RuntimeHelpers.GetUninitializedObject(unloadType);
            AccessTools.Field(unloadType, "game").SetValue(unload, game);
            AccessTools.Method(unloadType, "UnloadChunk").Invoke(unload, [chunks[1]]);
            if (game.EntityPlayer != live) throw new Exception("Terrain unload lost the live player");
            Console.WriteLine("PASS engine chunk migration and unload keep same-UID replay players out of terrain ownership");
        }
        finally { owner.SetValue(null, previous); harmony.UnpatchAll(harmony.Id); }
    }

    private static void CheckChunkInitialization()
    {
        var tasks = new List<Action>();
        var events = ProbeDispatchProxy.Create<IClientEventAPI>((m, a) =>
        {
            if (m.Name == "EnqueueMainThreadTask") tasks.Add((Action)a![0]!);
            return ProbeDispatchProxy.DefaultValue(m.ReturnType);
        });
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) =>
            m.Name == "get_Event" ? events : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        var replay = Activator.CreateInstance(Playback, api, game, new DirectorReplayDocument(),
            (Action<bool>)(_ => { }), (Func<bool>)(() => false))!;
        var observe = AccessTools.Method(Playback, "ObserveLiveBlockEntityState");
        var visits = new List<BlockPos>();
        void OnInitialize(BlockPos pos)
        {
            visits.Add(pos);
            observe.Invoke(replay, [pos]);
        }
        var chunk = (ClientChunk)RuntimeHelpers.GetUninitializedObject(typeof(ClientChunk));
        chunk.BlockEntities = new();
        for (int x = 1; x <= 2; x++)
        {
            var pos = new BlockPos(x, 0, 0, 0);
            chunk.BlockEntities.Add(pos, new ObservedEntity(OnInitialize) { Pos = pos });
        }
        var patch = Playback.Assembly.GetType("VSDirector.Replay.ReplayChunkEntityInitializationPatch", true)!;
        var harmony = new Harmony("vsdirector.probe.chunk-entity-init");
        harmony.CreateClassProcessor(patch).Patch();
        try
        {
            ScreenManager.FrameProfiler ??= new FrameProfilerUtil("probe");
            AccessTools.Method(typeof(ClientChunk), "InitBlockEntitiesFromPacket").Invoke(chunk, [game]);
            var pending = (System.Collections.IDictionary)AccessTools.Field(Playback, "pendingLiveRealityObservations").GetValue(replay)!;
            if (visits.Count != 2 || tasks.Count != 1 || pending.Count != 2)
                throw new Exception("Chunk initialization must finish before replay restoration drains either observation");
            tasks[0]();
            if (pending.Count != 0 || (bool)AccessTools.Property(patch, "IsInitializing").GetValue(null)!)
                throw new Exception("Chunk initialization boundary leaked queued replay observations");
            Console.WriteLine("PASS engine chunk entity initialization defers replay writes until the enumeration ends");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}
