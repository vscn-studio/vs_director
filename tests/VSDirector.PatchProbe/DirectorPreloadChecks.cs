using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Server;
using VSDirector;
using VSDirector.Data;
using VSDirector.Networking;

internal static class DirectorPreloadChecks
{
    internal static void Run(string? replayPath = null)
    {
        var assembly = typeof(VSDirectorModSystem).Assembly;
        Type T(string name) => assembly.GetType("VSDirector." + name, true)!;
        void Set(object target, string name, object? value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
        object? Call(object target, string name, params object?[] args) => AccessTools.Method(target.GetType(), name).Invoke(target, args);
        var logger = ProbeDispatchProxy.Create<ILogger>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var world = ProbeDispatchProxy.Create<IClientWorldAccessor>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_Logger" => logger, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType) });
        var manager = RuntimeHelpers.GetUninitializedObject(T("Replay.DirectorReplayManager"));
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        var map = (ClientWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldMap));
        Set(map, "mapsize", new Vec3i(1024000, 64, 1024000));
        map.ClientChunkSize = 32;
        game.WorldMap = map;
        Set(manager, "game", game);
        Set(manager, "capi", api); Set(manager, "controller", RuntimeHelpers.GetUninitializedObject(T("Runtime.DirectorCameraController")));
        var document = new DirectorReplayDocument { DurationSeconds = 20,
            IncludedPreloadColumns = [new(900, 901, 0)], ExcludedPreloadColumns = [new(100, 100, 0)] };
        document.Actors.Add(new DirectorReplayActorTrack { PlayerUid = "Probe", Samples = [
            new() { X = 3200, Z = 3200 }, new() { X = 4480, Z = 3200, TimeSeconds = 20 }] });
        var route = DirectorReplayPreloadPlanner.Build(document, "Probe", false);
        var startupPlan = (DirectorReplayPreloadPlan)Call(manager, "BuildPlaybackPreloadWindow", document, 0d, route)!;
        var columns = startupPlan.ExpandedColumns;
        if (columns[0] != new DirectorReplayPreloadColumn(900, 901, 0) || columns.Contains(new(100, 100, 0))
            || !columns.Contains(new(101, 100, 0)) || !columns.Contains(new(140, 100, 0)))
            throw new Exception("Playback preload ignores manual inclusion/exclusion or automatic neighbors");
        if (replayPath is not null)
        {
            var recorded = DirectorReplayPages.TryOpen(replayPath) ?? DirectorReplayIO.Load(replayPath);
            if (DirectorReplayTimelineEditIO.Load(replayPath + ".timeline.json") is { } edits)
                DirectorReplayTimelineEditIO.Apply(recorded, edits);
            var recordedRoute = DirectorReplayPreloadPlanner.Build(recorded, null, true);
            var atStart = (DirectorReplayPreloadPlan)Call(manager, "BuildPlaybackPreloadWindow", recorded, 0d, recordedRoute)!;
            if (!recordedRoute.ExpandedColumnSet.IsSubsetOf(atStart.ExpandedColumnSet))
                throw new Exception("The recorded route lost planned terrain when opened at time zero");
            Console.WriteLine($"PASS {recorded.Name}: {recorded.IncludedPreloadColumns.Count} manual columns, {recordedRoute.SourceColumns.Count} route centers, {atStart.ExpandedColumns.Count} total columns requested at t=0");
        }

        var worldType = T("Replay.DirectorReplayWorldPlayback");
        var replayWorld = RuntimeHelpers.GetUninitializedObject(worldType);
        Set(replayWorld, "document", document); Set(replayWorld, "started", true); Set(replayWorld, "requestedTime", 10d);
        foreach (string fieldName in new[] { "historyRefreshQueue", "historyRefreshSet", "baselineChunks" })
        {
            var field = AccessTools.Field(worldType, fieldName);
            field.SetValue(replayWorld, Activator.CreateInstance(field.FieldType));
        }
        var baselines = (System.Collections.IDictionary)AccessTools.Field(worldType, "baselineChunks").GetValue(replayWorld)!;
        var keyType = baselines.GetType().GetGenericArguments()[0];
        baselines.Add(Activator.CreateInstance(keyType, 10, 2, 10)!, new DirectorReplayChunkSnapshot { ChunkX = 10, ChunkY = 2, ChunkZ = 10, CapturedAtSeconds = 0 });
        Call(replayWorld, "ObserveServerRecordingChunkLoad", 10, 2, 10);
        Call(replayWorld, "ObserveServerRecordingChunkLoad", 10, 2, 10);
        if ((int)AccessTools.Property(AccessTools.Field(worldType, "historyRefreshQueue").FieldType, "Count").GetValue(AccessTools.Field(worldType, "historyRefreshQueue").GetValue(replayWorld))! != 1)
            throw new Exception("Arriving preload chunks do not queue replay-state restoration for ordinary recordings");

        int loadCalls = 0, dispatchCalls = 0;
        bool delivered = false;
        var worldManager = ProbeDispatchProxy.Create<IWorldManagerAPI>((m, args) =>
        {
            if (m.Name == "LoadChunkColumnPriority") loadCalls++;
            if (m.Name == "ForceSendChunkColumn") throw new Exception("Unexpected full-column resend");
            if (m.Name == "SendChunk")
            {
                if ((int)args![1]! != 3 || (bool)args[4]!) throw new Exception("Preload resent an existing layer or used the player's view range");
                dispatchCalls++;
            }
            return m.Name switch { "get_ChunkSize" => 32, "get_MapSizeY" => 256,
                "get_MapSizeX" or "get_MapSizeZ" => 32768,
                "ChunkIndex3D" => ((long)(int)args![0]! << 32) + (int)args[1]!,
                "HasChunk" => delivered || (int)args![1]! != 3, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType) };
        });
        var statuses = new List<DirectorChunkPreloadStatus>();
        IServerNetworkChannel? channel = null;
        channel = ProbeDispatchProxy.Create<IServerNetworkChannel>((m, args) =>
        {
            if (m.Name == "SendPacket") statuses.Add((DirectorChunkPreloadStatus)args![0]!);
            return m.ReturnType == typeof(IServerNetworkChannel) ? channel : ProbeDispatchProxy.DefaultValue(m.ReturnType);
        });
        var serverWorld = ProbeDispatchProxy.Create<IServerWorldAccessor>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var sapi = ProbeDispatchProxy.Create<ICoreServerAPI>((m, _) => m.Name switch
        {
            "get_WorldManager" => worldManager, "get_Logger" => logger, "get_World" => serverWorld,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var serverType = T("Networking.DirectorAnchorServer");
        var server = RuntimeHelpers.GetUninitializedObject(serverType);
        Set(server, "sapi", sapi); Set(server, "channel", channel);
        var retention = Activator.CreateInstance(serverType.GetNestedType("AnchorChunkRetention", BindingFlags.NonPublic)!, sapi)!;
        Set(server, "retention", retention);
        var columnType = serverType.GetNestedType("PreloadColumn", BindingFlags.NonPublic)!;
        var set = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(columnType))!;
        Call(set, "Add", Activator.CreateInstance(columnType, 100, 100, 0));
        Call(set, "Add", Activator.CreateInstance(columnType, 101, 100, 0));
        var stateType = serverType.GetNestedType("PreloadState", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(stateType, Guid.NewGuid().ToString("D"), set, 0L)!;
        var player = (ProbeAdmin)RuntimeHelpers.GetUninitializedObject(typeof(ProbeAdmin));
        var playerData = ServerWorldPlayerData.CreateNew("Probe", "Probe");
        Set(player, "worlddata", playerData);
        var connection = new ConnectedClient(1) { WorldData = playerData, MapChunkSent = [] };
        Set(player, "client", connection);
        var engine = (ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain));
        engine.WorldMap = (ServerWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ServerWorldMap));
        Set(engine.WorldMap, "mapsize", new Vec3i(1024000, 256, 1024000));
        Set(engine.WorldMap, "chunkMapSizeY", 8);
        Set(engine.WorldMap, "index3dMulX", 2097152);
        Set(engine.WorldMap, "index3dMulZ", 2097152);
        engine.SendChunk(15985, 0, 16000, player, true);
        if (connection.forceSendChunks.Count != 0) throw new Exception("Probe column is inside player view distance");
        engine.SendChunk(15985, 0, 16000, player, false);
        engine.ForceSendChunkColumn(player, 15985, 16000, 0);
        if (!connection.forceSendMapChunks.Contains(engine.WorldMap.MapChunkIndex2D(15985, 16000))
            || !Enumerable.Range(0, 8).All(y => connection.forceSendChunks.Contains(engine.WorldMap.ChunkIndex3D(15985, y, 16000))))
            throw new Exception("Engine did not queue the remote map and all vertical chunks");
        Call(server, "EnsurePreloadChunks", player, state, 1000L);
        if (loadCalls != 1 || dispatchCalls != 1 || statuses[^1].SentColumns != 0)
            throw new Exception("Preload failed to schedule one async load or declared an unreceived column complete");
        Call(server, "EnsurePreloadChunks", player, state, 2000L);
        if (loadCalls != 2) throw new Exception("Preload did not schedule the next column");
        delivered = true;
        Call(server, "EnsurePreloadChunks", player, state, 4000L);
        Call(server, "EnsurePreloadChunks", player, state, 5000L);
        if (statuses[^1].SentColumns != 2 || loadCalls != 2 || dispatchCalls != 2)
            throw new Exception("Completed terrain was retransmitted");
        Call(server, "ReleasePreload", state);
        foreach (string name in new[] { "preloads", "anchors", "config" })
            Set(server, name, Activator.CreateInstance(AccessTools.Field(serverType, name).FieldType));
        Set(server, "permissions", RuntimeHelpers.GetUninitializedObject(T("Networking.DirectorPermissionServer")));
        string replayCode = Guid.NewGuid().ToString("D");
        bool Protected(int x) => (bool)AccessTools.Method(T("Networking.DirectorAnchorChunkProtection"),
            "IsProtected", [typeof(string), typeof(long)]).Invoke(null, ["Probe", (long)x << 32])!;
        for (int i = 0; i < 2; i++)
        {
            Call(server, "OnPreloadRequest", player, new DirectorChunkPreloadRequest { Active = true,
                ReplayId = replayCode, BatchId = i + 1, KeepPreviousBatches = i > 0,
                Columns = [new DirectorChunkPreloadColumn { X = 100 + i, Z = 100 }] });
        }
        if (!Protected(100) || !Protected(101)) throw new Exception("New batch released earlier remote columns");
        Call(server, "OnPreloadRequest", player, new DirectorChunkPreloadRequest { Active = true,
            ReplayId = replayCode, BatchId = 2, RemovedColumns = [new() { X = 100, Z = 100 }] });
        if (Protected(100) || !Protected(101)) throw new Exception("Selection edit released the wrong remote columns");
        Call(server, "OnPreloadRequest", player, new DirectorChunkPreloadRequest { Active = false });
        if (Protected(100) || Protected(101)) throw new Exception("Stopped preload leaked protected columns");
        var chunks = new Dictionary<long, ClientChunk>();
        Set(map, "chunks", chunks);
        Set(map, "chunksLock", new object());
        Set(map, "index3dMulX", 2097152);
        Set(map, "index3dMulZ", 2097152);
        var start = Activator.CreateInstance(manager.GetType().GetNestedType("PendingPlaybackStart", BindingFlags.NonPublic)!,
            document, "", false, route, true, false)!;
        AccessTools.Property(start.GetType(), "StartupPlan").SetValue(start, startupPlan);
        Set(manager, "pendingPlaybackStart", start);
        AccessTools.Property(manager.GetType(), "CanPreloadRemoteTerrain").SetValue(manager, (Func<bool>)(() => true));
        var loaded = (ClientChunk)RuntimeHelpers.GetUninitializedObject(typeof(ClientChunk));
        Set(loaded, "loadedFromServer", true);
        Set(loaded, "clientmapchunk", RuntimeHelpers.GetUninitializedObject(typeof(ClientMapChunk)));
        foreach (var c in columns)
            for (int y = 0; y < 2; y++) chunks[map.ChunkIndex3D(c.X, y, c.Z)] = loaded;
        for (int y = 0; y < 2; y++) chunks[map.ChunkIndex3D(100, y, 100)] = loaded;
        if (Call(manager, "IsPlaybackObservationAreaReady", document, 0d) is not true)
            throw new Exception("Fixture must start with the local observation area ready");
        long farChunk = map.ChunkIndex3D(140, 1, 100);
        chunks.Remove(farChunk);
        Call(manager, "ProcessPendingPlaybackStart");
        if ((bool)AccessTools.Property(start.GetType(), "ChunksReady").GetValue(start)!
            || (int)AccessTools.Property(start.GetType(), "PreloadReadyColumns").GetValue(start)! != columns.Count - 1)
            throw new Exception("Replay started with an incomplete distant route column");
        chunks[farChunk] = loaded;
        if (Call(manager, "UpdatePreloadReadiness", start) is not true)
            throw new Exception("Full route was still blocked after the remote column arrived");
        chunks.Clear();
        Set(manager, "pendingPlaybackStart", null);
        Set(map, "mapsize", new Vec3i(32, 32, 32));
        Console.WriteLine("PASS stationary t=0 replay requests a destination 40 columns ahead and waits for its last vertical chunk");
        game.BlocksReceivedAndLoaded = true;
        game.Platform = (ClientPlatformAbstract)RuntimeHelpers.GetUninitializedObject(
            typeof(ClientMain).Assembly.GetType("Vintagestory.Client.NoObf.ClientPlatformWindows", true)!);
        var local = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_IsSinglePlayer" => true, "get_Logger" => logger,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var suspension = Activator.CreateInstance(T("Replay.DirectorSingleplayerReplaySuspension"),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [local, game], null)!;
        Set(manager, "game", game);
        Set(manager, "singleplayerWorldSuspension", suspension);
        var plan = new DirectorReplayPreloadPlan([], [], [new(0, 0, 0)], 0, false);
        if (Call(suspension, "TryAcquire") is not true || !game.Platform.IsGamePaused)
            throw new Exception("Replay did not suspend the integrated server");
        bool CanStream()
        {
            Set(manager, "nextTerrainCheckMs", 0L);
            bool stream = (bool)Call(manager, "CanStreamReplayTerrain", plan, false)!;
            Call(suspension, "Maintain");
            return stream;
        }
        if (!CanStream() || game.Platform.IsGamePaused) throw new Exception("Missing terrain deadlocked the paused server");
        game.IsPaused = true;
        if (CanStream() || !game.Platform.IsGamePaused) throw new Exception("Preload bypassed the escape-menu pause");
        game.IsPaused = false;
        var chunk = (ClientChunk)RuntimeHelpers.GetUninitializedObject(typeof(ClientChunk));
        chunks.Add(0, chunk);
        Set(chunk, "loadedFromServer", true);
        if (!CanStream()) throw new Exception("3D chunk without its map ended loading early");
        Set(chunk, "clientmapchunk", RuntimeHelpers.GetUninitializedObject(typeof(ClientMapChunk)));
        if (CanStream() || !game.Platform.IsGamePaused) throw new Exception("Receiving terrain did not restore suspension");
        Call(suspension, "Release");
        if (game.Platform.IsGamePaused) throw new Exception("Stopping replay retained its pause");
        Console.WriteLine("PASS suspended singleplayer streams missing terrain, respects menu pause and re-suspends on map and chunk receipt");
        Console.WriteLine("PASS playback applies manual preload overrides; server schedules one async column per tick and waits for receipt before completing");
    }

    private sealed class ProbeAdmin : ServerPlayer
    {
        private ProbeAdmin(ServerMain server, ServerWorldPlayerData data) : base(server, data) { }
        public override IPlayerRole Role { get => null!; set { } }
        public override bool HasPrivilege(string privilegeCode) => true;
    }
}
