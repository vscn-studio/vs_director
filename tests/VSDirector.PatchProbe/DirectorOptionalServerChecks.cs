using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VSDirector;
using VSDirector.Data;
using VSDirector.Networking;

internal static class DirectorOptionalServerChecks
{
    internal static void Run()
    {
        var state = EnumChannelState.Registered;
        long clock = 0;
        var sent = new List<object>();
        var changes = new List<DirectorPermissionSnapshot>();
        Delegate? snapshotHandler = null;
        IClientNetworkChannel? channel = null;
        channel = ProbeDispatchProxy.Create<IClientNetworkChannel>((method, args) =>
        {
            if (method.Name == "get_Connected") return state == EnumChannelState.Connected;
            if (method.Name == "SetMessageHandler")
            {
                if (method.GetGenericArguments()[0] == typeof(DirectorPermissionSnapshot))
                    snapshotHandler = (Delegate)args![0]!;
                return channel;
            }
            if (method.Name == "SendPacket")
            {
                Check(state == EnumChannelState.Connected, "Packet sent without a server channel");
                sent.Add(args![0]!);
                return channel;
            }
            return ProbeDispatchProxy.DefaultValue(method.ReturnType);
        });
        var network = ProbeDispatchProxy.Create<IClientNetworkAPI>((method, _) => method.Name switch
        {
            "GetChannel" => channel,
            "GetChannelState" => state,
            _ => ProbeDispatchProxy.DefaultValue(method.ReturnType)
        });
        var logger = ProbeDispatchProxy.Create<ILogger>((method, _) => ProbeDispatchProxy.DefaultValue(method.ReturnType));
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_Network" => network,
            "get_ElapsedMilliseconds" => clock,
            "get_Logger" => logger,
            _ => ProbeDispatchProxy.DefaultValue(method.ReturnType)
        });
        Type TypeOf(string name) => typeof(VSDirectorModSystem).Assembly.GetType("VSDirector.Networking." + name, true)!;
        object permission = Activator.CreateInstance(TypeOf("DirectorPermissionClient"), api, (Action<DirectorPermissionSnapshot>)changes.Add)!;
        object? Call(object target, string method, params object?[] args) => AccessTools.Method(target.GetType(), method).Invoke(target, args);
        bool Flag(string name) => (bool)AccessTools.Property(permission.GetType(), name).GetValue(permission)!;
        bool Has(DirectorPermission value) => (bool)Call(permission, "Has", value)!;

        Call(permission, "RequestSnapshot");
        clock = 60000;
        Call(permission, "Tick");
        Check(!Flag("IsKnown") && !Has(DirectorPermission.ReplayCapture) && sent.Count == 0,
            "A slow channel handshake enabled local permissions");
        state = EnumChannelState.NotFound;
        Call(permission, "RequestSnapshot");
        Check(!Flag("IsKnown"), "Missing local channel registration was treated as an unmodded server");
        state = EnumChannelState.Connected;
        Call(permission, "RequestSnapshot");
        clock += 60000;
        Call(permission, "Tick");
        Check(!Flag("IsKnown") && !Has(DirectorPermission.ReplayCapture) && sent.Count == 2,
            "A delayed server permission reply enabled local permissions");
        snapshotHandler!.DynamicInvoke(new DirectorPermissionSnapshot { PermissionMask = (long)DirectorPermission.ReplayCapture });
        Check(Flag("IsKnown") && !Flag("IsClientOnly") && Has(DirectorPermission.ReplayCapture)
            && !Has(DirectorPermission.FreeCamera), "Server permissions were not applied");
        snapshotHandler.DynamicInvoke(new DirectorPermissionSnapshot());
        Check(!Has(DirectorPermission.ReplayCapture), "Revoked server permission remained granted");
        state = EnumChannelState.NotConnected;
        Call(permission, "RequestSnapshot");
        Check(!Flag("IsClientOnly") && !Has(DirectorPermission.ReplayCapture), "Lost channel bypassed existing server restrictions");

        Call(permission, "Reset");
        int before = sent.Count;
        Call(permission, "RequestSnapshot");
        Check(Flag("IsKnown") && Flag("IsClientOnly") && !Flag("IsAdmin"), "Client-only session was not resolved");
        foreach (var feature in new[] { DirectorPermission.FreeCamera, DirectorPermission.CameraLibrary,
            DirectorPermission.PathLibrary, DirectorPermission.FollowCameras, DirectorPermission.ReplayCapture,
            DirectorPermission.ReplayPlayback, DirectorPermission.OffscreenSavedSources })
            Check(Has(feature), "Client-only feature is unavailable: " + feature);
        Check(!Has(DirectorPermission.RemoteChunkAccess) && !Has(DirectorPermission.ActorRecording)
            && !Has(DirectorPermission.None) && !Has((DirectorPermission)(1L << 60)), "Server or invalid permissions granted locally");
        Call(permission, "Update", "other-player", DirectorPermission.All);
        Check(sent.Count == before && changes[^1].PermissionMask == (long)DirectorPermissions.ClientOnlyPermissions,
            "Local initialization sent a packet or omitted its change notification");
        var owner = (VSDirectorModSystem)RuntimeHelpers.GetUninitializedObject(typeof(VSDirectorModSystem));
        AccessTools.Field(typeof(VSDirectorModSystem), "permissionClient").SetValue(owner, permission);
        Check((bool)Call(owner, "EnsureDirectorAccess", new object?[] { null })!, "Local cameras still wait for remote chunk authorization");

        Call(permission, "Reset");
        state = EnumChannelState.Connected;
        Call(permission, "RequestSnapshot");
        Check(!Flag("IsClientOnly") && !Flag("IsKnown") && !Has(DirectorPermission.ReplayCapture),
            "Changing servers retained local permissions before synchronization");
        snapshotHandler.DynamicInvoke(new DirectorPermissionSnapshot { IsAdmin = true });
        Check(Flag("IsAdmin") && Has(DirectorPermission.RemoteChunkAccess), "Server administrator permissions were lost");

        using var anchor = (IDisposable)Activator.CreateInstance(TypeOf("DirectorAnchorClient"), api, (Action<DirectorAnchorStatus>)(_ => { }))!;
        using var preload = (IDisposable)Activator.CreateInstance(TypeOf("DirectorChunkPreloadClient"), api)!;
        var plan = new DirectorReplayPreloadPlan([], [], [new(1, 2, 0)], 0, false);
        var pose = new CameraPreset { X = 32, Y = 100, Z = 64 };
        Call(anchor, "OnStatus", new DirectorAnchorStatus { Allowed = true });
        Call(anchor, "Update", pose, true, null, null, null, 1, false);
        Call(preload, "Update", Guid.NewGuid(), plan);
        Check(sent[^1] is DirectorChunkPreloadRequest && sent[^2] is DirectorAnchorRequest, "Connected server features did not send requests");
        var selectedColumns = Enumerable.Range(0, 40).Select(i => new DirectorReplayPreloadColumn(i, 10, 0)).ToArray();
        var largePlan = new DirectorReplayPreloadPlan([], [], selectedColumns, 0, false);
        Guid preloadId = Guid.NewGuid();
        var received = new HashSet<int>();
        int staleBatch = 0;
        for (int batchIndex = 0; batchIndex < 3; batchIndex++)
        {
            clock += 1100;
            Call(preload, "Update", preloadId, largePlan);
            var request = (DirectorChunkPreloadRequest)sent[^1];
            if (request.Columns.Count is < 1 or > 16) throw new Exception("Preload batch exceeds limit or failed to advance");
            foreach (var column in request.Columns) received.Add(column.X);
            if (batchIndex > 0)
            {
                Call(preload, "OnStatus", new DirectorChunkPreloadStatus { Allowed = true, ReplayId = preloadId.ToString("D"), BatchId = staleBatch, TotalColumns = request.Columns.Count, SentColumns = request.Columns.Count });
                clock += 1100; Call(preload, "Update", preloadId, largePlan);
                if (((DirectorChunkPreloadRequest)sent[^1]).Columns.Count != 0) throw new Exception("Stale preload acknowledgement advanced the queue");
            }
            Call(preload, "OnStatus", new DirectorChunkPreloadStatus { Allowed = true, ReplayId = preloadId.ToString("D"), BatchId = request.BatchId, TotalColumns = request.Columns.Count, SentColumns = request.Columns.Count });
            staleBatch = request.BatchId;
        }
        Check(received.Count == 40, "Preload permanently ignores selections beyond the first 16 columns");
        Console.WriteLine("PASS preload processes all 40 selected columns in bounded batches and ignores stale acknowledgements");
        state = EnumChannelState.NotConnected;
        before = sent.Count;
        Call(anchor, "StopAnchor");
        Call(preload, "Stop");
        Call(anchor, "RequestAccess");
        Call(anchor, "Update", pose, true, null, null, null, 1, false);
        Call(preload, "Update", Guid.NewGuid(), plan);
        Check(sent.Count == before, "Server requests were sent in client-only mode or during disconnect");
        Console.WriteLine("PASS optional server supports local tools, preserves server permissions and suppresses disconnected requests");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
