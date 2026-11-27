using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector;
using VSDirector.Data;

internal static class DirectorReplayWorldCaptureChecks
{
    private sealed class WorkSurface : BlockEntity
    {
        public override void ToTreeAttributes(Vintagestory.API.Datastructures.ITreeAttribute tree)
            => tree.SetInt("state", 1);
    }
    private static readonly Dictionary<(int X, int Y, int Z), IWorldChunk> chunks = new();
    private static bool GetChunk(int chunkX, int chunkY, int chunkZ, ref IWorldChunk? __result)
    {
        __result = chunks.GetValueOrDefault((chunkX, chunkY, chunkZ)); return false;
    }

    internal static void Run()
    {
        var assembly = typeof(VSDirectorModSystem).Assembly;
        var type = assembly.GetType("VSDirector.Replay.DirectorReplayManager", true)!;
        var manager = RuntimeHelpers.GetUninitializedObject(type);
        void Set(string name, object? value) => AccessTools.Field(type, name).SetValue(manager, value);
        void Init(string name) { var f = AccessTools.Field(type, name); f.SetValue(manager, Activator.CreateInstance(f.FieldType)); }
        object? Call(string name, params object?[] args) => AccessTools.Method(type, name).Invoke(manager, args);
        var player = new EntityPlayer(); player.Pos.SetPos(511974.6, 113, 511778.8);
        var clientPlayer = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
        var playerDataField = AccessTools.Field(typeof(ClientPlayer), "worlddata");
        var playerData = RuntimeHelpers.GetUninitializedObject(playerDataField.FieldType);
        AccessTools.Property(playerDataField.FieldType, "EntityPlayer").SetValue(playerData, player);
        playerDataField.SetValue(clientPlayer, playerData);
        var live = new Dictionary<(int X, int Y, int Z), int[]>();
        var liveEntities = new Dictionary<BlockPos, BlockEntity>();
        Block[] blocks = [new() { BlockId = 0 }, new() { BlockId = 1 }, new() { BlockId = 2 }];
        var accessor = ProbeDispatchProxy.Create<IBlockAccessor>((m, a) =>
        {
            if (m.Name == "GetBlockEntity" && a![0] is BlockPos entityPos)
                return liveEntities.GetValueOrDefault(entityPos);
            if (m.Name == "GetBlock" && a![0] is BlockPos p)
            {
                if (a.Length > 1 && a[1] is int layer && layer == BlockLayersAccess.Fluid) return blocks[0];
                int id = live.GetValueOrDefault((p.X >> 5, p.Y >> 5, p.Z >> 5))?[(p.Y & 31) * 1024 + (p.Z & 31) * 32 + (p.X & 31)] ?? 0;
                return blocks[id];
            }
            return ProbeDispatchProxy.DefaultValue(m.ReturnType);
        });
        var world = ProbeDispatchProxy.Create<IClientWorldAccessor>((m, _) => m.Name switch
        {
            "get_Player" => clientPlayer, "get_MapSizeY" => 128, "get_BlockAccessor" => accessor,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var registry = ProbeDispatchProxy.Create<IClassRegistryAPI>((m, _) =>
            m.Name == "GetBlockEntityClass" ? "KnappingSurface" : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_ClassRegistry" => registry, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        var map = (ClientWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldMap));
        AccessTools.Field(typeof(ClientMain), "WorldMap").SetValue(game, map);
        var controller = RuntimeHelpers.GetUninitializedObject(assembly.GetType("VSDirector.Runtime.DirectorCameraController", true)!);
        Set("capi", api); Set("game", game); Set("controller", controller);
        Set("config", new DirectorConfig { ReplayCaptureWorld = true, ReplaySnapshotRadiusChunks = 0 });
        foreach (string field in new[] { "recordingSnapshotChunks", "recordingBlockEntities", "recordingBlockStates",
            "pendingBlockChanges", "pausedBlockChanges", "pendingWorldCapture" }) Init(field);
        var doc = new DirectorReplayDocument { DurationSeconds = 20, WorldSnapshot = new()
        {
            Dimension = 0, MapSizeY = 128, CenterChunkX = 15999, CenterChunkZ = 15998, RadiusChunks = 4,
            FollowCaptureArea = true
        } };
        Set("recording", doc); Set("recordingElapsedSeconds", 5d);
        int[] NewChunk(int x, int y, int z)
        {
            var cells = new int[32768]; live[(x,y,z)] = cells;
            var data = ProbeDispatchProxy.Create<IChunkBlocks>((m, a) => m.Name switch
            {
                "GetBlockIdUnsafe" => cells[(int)a![0]!], "GetFluid" => 0,
                _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
            });
            var entities = Activator.CreateInstance(typeof(IWorldChunk).GetProperty("BlockEntities")!.PropertyType);
            chunks[(x,y,z)] = ProbeDispatchProxy.Create<IWorldChunk>((m, _) => m.Name switch
            {
                "get_Data" => data, "Unpack_ReadOnly" => true, "get_BlockEntities" => entities,
                _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
            });
            return cells;
        }
        var harmony = new Harmony("vsdirector.probe.moving-capture");
        harmony.Patch(AccessTools.Method(typeof(ClientWorldMap), "GetChunk", [typeof(int),typeof(int),typeof(int)]),
            prefix: new HarmonyMethod(typeof(DirectorReplayWorldCaptureChecks), nameof(GetChunk)));
        try
        {
            var tree = NewChunk(15999,3,15993);
            int trunk = 17 * 1024 + 2 * 32 + 6, leaf = trunk + 1024;
            tree[trunk] = 1; tree[leaf] = 2;
            Call("CaptureAdditionalWorldChunks");
            var captured = doc.WorldSnapshot.Chunks.Single();
            if (captured.ChunkZ != 15993 || captured.CapturedAtSeconds != 5)
                throw new Exception("Moving capture missed the tree beyond the original radius");
            tree[leaf] = 0; // Partial felling, trunk remains standing.
            var leafPos = new BlockPos(15999*32+6, 3*32+18,15993*32+2,0);
            Set("recordingElapsedSeconds", 6d); Call("QueueBlockChanged", leafPos); Call("FlushPendingBlockChanges", int.MaxValue);
            if (doc.BlockEvents.Count != 1 || doc.BlockEvents[0].SolidBlockId != 0 || doc.BlockEvents[0].TimeSeconds != 6)
                throw new Exception("Partial felling outside the initial radius was not recorded");
            if (DirectorReplayChunkCodec.Decode(captured.SolidRuns,32768)[leaf] != 2)
                throw new Exception("Captured foliage retained mutable chunk storage");
            // Move again and mutate before the next capture tick.
            player.Pos.Z -= 32; var next = NewChunk(15999,3,15992); next[trunk]=1; next[leaf]=2;
            var trunkPos = new BlockPos(15999*32+6,3*32+17,15992*32+2,0);
            Set("recordingElapsedSeconds", 7d); Call("CaptureBlockBeforeChange", trunkPos);
            next[trunk] = 0; Call("QueueBlockChanged", trunkPos); Call("FlushPendingBlockChanges", int.MaxValue);
            var beforeWrite = doc.WorldSnapshot.Chunks.Single(c => c.ChunkZ == 15992);
            if (DirectorReplayChunkCodec.Decode(beforeWrite.SolidRuns,32768)[trunk] != 1 || doc.BlockEvents.Count != 2)
                throw new Exception("First write after moving was captured too late");
            Call("CaptureBlockBeforeChange", trunkPos);
            if (doc.WorldSnapshot.Chunks.Count != 2) throw new Exception("An observed chunk baseline was replaced");
            Set("recordingPaused", true); player.Pos.Z -= 32; NewChunk(15999,3,15991);
            Call("CaptureBlockBeforeChange", new BlockPos(trunkPos.X,trunkPos.Y,trunkPos.Z-32,0));
            if (doc.WorldSnapshot.Chunks.Count != 2) throw new Exception("Paused recording captured new terrain");
            string path = Path.Combine(Path.GetTempPath(), "director-moving-capture-"+Guid.NewGuid().ToString("N")+".vsdreplay");
            try
            {
                DirectorReplayIO.Save(path, doc);
                var loaded = DirectorReplayPages.TryOpen(path)!;
                if (!loaded.HasHistoricalBaselines || loaded.WorldSnapshot.Chunks.Count != 2 || loaded.BlockEvents.Count != 2)
                    throw new Exception("Moving baselines or partial felling were lost in paged replay persistence");
            }
            finally { File.Delete(path); File.Delete(path+".pages"); }
            Set("recordingPaused", false);
            Set("recordingElapsedSeconds", 10d);
            var workPos = new BlockPos(trunkPos.X, trunkPos.Y, trunkPos.Z-32,0);
            var work = new WorkSurface { Pos = workPos };
            Call("CaptureObservedBlockEntityState", work, true);
            var workChunk = doc.WorldSnapshot.Chunks.Single(c => c.ChunkZ == 15991);
            if (workChunk.CapturedAtSeconds != 10 || doc.BlockEvents.Any(e => e.TimeSeconds == 0))
                throw new Exception("A newly observed block entity was backdated to recording start");
            live[(15999,3,15991)][trunk] = 2;
            liveEntities[workPos] = work;
            Set("recordingElapsedSeconds", 11d); Call("CaptureObservedBlockEntityState", work, false);
            Call("QueueBlockEntityCapture");
            liveEntities.Clear();
            live[(15999,3,15991)][trunk] = 0;
            Set("recordingElapsedSeconds", 12d); Call("ProcessBlockEntityCapture", int.MaxValue);
            var history = doc.BlockEvents.Where(e => e.Z == workPos.Z).ToArray();
            if (history.Length != 2 || history[0].TimeSeconds != 11 || history[0].BlockEntityClass != "KnappingSurface"
                || history[1].TimeSeconds != 12 || history[1].SolidBlockId != 0 || !history[1].BlockEntityCaptured)
                throw new Exception("Missed work-surface removal or backdated its observation");
            Console.WriteLine("PASS real capture follows movement beyond initial radius, preserves partial tree baseline, captures before first write and persists paged history");
        }
        finally { harmony.UnpatchAll(harmony.Id); chunks.Clear(); }
    }
}
