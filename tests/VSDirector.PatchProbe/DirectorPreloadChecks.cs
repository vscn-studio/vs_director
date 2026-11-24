using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VSDirector;
using VSDirector.Data;
using VSDirector.Networking;

internal static class DirectorPreloadChecks
{
    internal static void Run()
    {
        var assembly = typeof(VSDirectorModSystem).Assembly;
        Type T(string name) => assembly.GetType("VSDirector." + name, true)!;
        void Set(object target, string name, object? value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
        object? Call(object target, string name, params object?[] args) => AccessTools.Method(target.GetType(), name).Invoke(target, args);
        var logger = ProbeDispatchProxy.Create<ILogger>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var world = ProbeDispatchProxy.Create<IClientWorldAccessor>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name == "get_World" ? world : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var manager = RuntimeHelpers.GetUninitializedObject(T("Replay.DirectorReplayManager"));
        Set(manager, "capi", api); Set(manager, "controller", RuntimeHelpers.GetUninitializedObject(T("Runtime.DirectorCameraController")));
        var document = new DirectorReplayDocument { DurationSeconds = 20,
            IncludedPreloadColumns = [new(900, 901, 0)], ExcludedPreloadColumns = [new(100, 100, 0)] };
        var fallback = new DirectorReplayPreloadPlan([new(100, 100, 0)], [], [], 0, false);
        var columns = (IReadOnlyList<DirectorReplayPreloadColumn>)Call(manager, "BuildPlaybackPreloadWindow", document, 0d, fallback)!;
        if (columns[0] != new DirectorReplayPreloadColumn(900, 901, 0) || columns.Contains(new(100, 100, 0)) || !columns.Contains(new(101, 100, 0)))
            throw new Exception("Playback preload ignores manual inclusion/exclusion or automatic neighbors");

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
        var worldManager = ProbeDispatchProxy.Create<IWorldManagerAPI>((m, _) =>
        {
            if (m.Name == "LoadChunkColumnPriority") loadCalls++;
            if (m.Name == "ForceSendChunkColumn") dispatchCalls++;
            return m.Name switch { "get_ChunkSize" => 32, "get_MapSizeY" => 256,
                "HasChunk" => delivered, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType) };
        });
        var statuses = new List<DirectorChunkPreloadStatus>();
        IServerNetworkChannel? channel = null;
        channel = ProbeDispatchProxy.Create<IServerNetworkChannel>((m, args) =>
        {
            if (m.Name == "SendPacket") statuses.Add((DirectorChunkPreloadStatus)args![0]!);
            return m.ReturnType == typeof(IServerNetworkChannel) ? channel : ProbeDispatchProxy.DefaultValue(m.ReturnType);
        });
        var sapi = ProbeDispatchProxy.Create<ICoreServerAPI>((m, _) => m.Name switch
        {
            "get_WorldManager" => worldManager, "get_Logger" => logger, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
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
        var player = ProbeDispatchProxy.Create<IServerPlayer>((m, _) => m.Name == "get_PlayerName" ? "Probe" : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        Call(server, "EnsurePreloadChunks", player, state, 1000L);
        if (loadCalls != 1 || dispatchCalls != 1 || statuses[^1].SentColumns != 0)
            throw new Exception("Preload failed to schedule one async load or declared an unreceived column complete");
        Call(server, "EnsurePreloadChunks", player, state, 2000L);
        if (loadCalls != 2) throw new Exception("Preload did not schedule the next column");
        delivered = true;
        Call(server, "EnsurePreloadChunks", player, state, 4000L);
        Call(server, "EnsurePreloadChunks", player, state, 5000L);
        if (statuses[^1].SentColumns != 2 || loadCalls != 2) throw new Exception("Preload completion does not follow delivery");
        Call(server, "ReleasePreload", state);
        Console.WriteLine("PASS playback applies manual preload overrides; server schedules one async column per tick and waits for receipt before completing");
    }
}
