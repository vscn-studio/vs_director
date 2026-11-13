using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using VSDirector;
using VSDirector.Networking;
using VSDirector.Data;

internal static class DirectorPerformanceChecks
{
    private sealed class Player : Vintagestory.Server.ServerPlayer
    {
        public bool Allowed = true;
        public Player(Vintagestory.Server.ServerWorldPlayerData data) : base(null!, data) { }
        protected override void Init() { }
        public override string PlayerUID => "actor-probe";
        public override IPlayerRole Role { get => null!; set { } }
        public override bool HasPrivilege(string code) => Allowed;
    }
    private sealed class Carrier : EntityPlayer
    {
        public bool Deferred;
        public readonly Queue<Action> Teleports = new();
        public override void TeleportTo(EntityPos position, Action? onTeleported = null)
        {
            var copy = position.Copy();
            void Apply() { Pos.SetFrom(copy); onTeleported?.Invoke(); }
            if (Deferred) Teleports.Enqueue(Apply); else Apply();
        }
    }
    internal static void Run()
    {
        var assembly = typeof(VSDirectorModSystem).Assembly;
        Type type = assembly.GetType("VSDirector.Networking.DirectorPerformanceServer", true)!;
        var carrier = new Carrier(); carrier.Pos.SetPos(1, 100, 3);
        carrier.Pos.HeadYaw = MathF.PI; carrier.Pos.HeadPitch = 1.2f;
        carrier.SetCollisionBox(.6f, 1.8f); carrier.SetSelectionBox(.6f, 1.8f); carrier.LocalEyePos.Y = 1.7;
        long clock = 100;
        var worldData = (Vintagestory.Server.ServerWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Server.ServerWorldPlayerData));
        worldData.ModData = new();
        AccessTools.Field(worldData.GetType(), "Entityplayer").SetValue(worldData, carrier);
        var data = worldData.ModData; var packets = new List<DirectorPerformanceStatus>();
        var player = new Player(worldData);
        IServerNetworkChannel? channel = null;
        channel = ProbeDispatchProxy.Create<IServerNetworkChannel>((m, a) =>
        {
            if (m.Name == "SetMessageHandler") return channel;
            if (m.Name == "SendPacket" && a![0] is DirectorPerformanceStatus status) packets.Add(status);
            return ProbeDispatchProxy.DefaultValue(m.ReturnType);
        });
        var network = ProbeDispatchProxy.Create<IServerNetworkAPI>((m, _) => m.Name == "GetChannel" ? channel : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var blocks = ProbeDispatchProxy.Create<IBlockAccessor>((m, _) => m.Name == "get_MapSizeY" ? 256 : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var world = ProbeDispatchProxy.Create<IServerWorldAccessor>((m, _) => m.Name switch { "get_ElapsedMilliseconds" => clock, "get_BlockAccessor" => blocks, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType) });
        var events = ProbeDispatchProxy.Create<IServerEventAPI>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var api = ProbeDispatchProxy.Create<ICoreServerAPI>((m, _) => m.Name switch { "get_Network" => network, "get_World" => world, "get_Event" => events, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType) });
        Type permissionType = assembly.GetType("VSDirector.Networking.DirectorPermissionServer", true)!;
        object permissions = RuntimeHelpers.GetUninitializedObject(permissionType);
        AccessTools.Field(permissionType, "store").SetValue(permissions, new DirectorPermissionStoreDocument());
        using var server = (IDisposable)Activator.CreateInstance(type, api, permissions)!;
        void Call(string method, params object[] args) => AccessTools.Method(type, method).Invoke(server, args);
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        string Start() { string id = Guid.NewGuid().ToString(); Call("Request", player, new DirectorPerformanceRequest { Session = id, X = 10, Y = 100, Z = 20, CustomBody = true, BodyWidth = .2, BodyHeight = .6, SneakHeight = .3, EyeHeight = .5 }); return id; }
        void Request(string id, int action) => Call("Request", player, new DirectorPerformanceRequest { Session = id, Action = action });
        string first = Start(); Check(carrier.Pos.X == 10 && data.Count == 1, "Start failed to persist return position");
        Check(Math.Abs(carrier.Pos.Pitch - Math.PI) < .00001 && carrier.Pos.Roll == 0,
            "Performance initialization does not use the native horizontal player pitch");
        Check(carrier.Pos.HeadYaw == 0 && carrier.Pos.HeadPitch == 0,
            "Performance initialization retained the editor's relative head angles");
        Check(Math.Abs(carrier.CollisionBox.XSize - .2) < .001 && Math.Abs(carrier.CollisionBox.Y2 - .6) < .001 && carrier.LocalEyePos.Y == .5, "Server did not apply independent body dimensions");
        carrier.Pos.Yaw = 1.25f; carrier.BodyYaw = 0;
        Request(first, 3); clock += 33; Call("Tick", .033f); Request(first, 1);
        Check(packets.Where(p => p.Phase == "sample").All(p => Math.Abs(p.Yaw - 1.25f) < .00001),
            "Authoritative samples use an unsimulated visual BodyYaw");
        Check(carrier.Pos.X == 1 && data.Count == 0, "Stop did not restore carrier");
        Check(Math.Abs(carrier.CollisionBox.Y2 - 1.8) < .001 && carrier.LocalEyePos.Y == 1.7, "Stop did not restore body and eye height");
        Check(packets.Count(p => p.Phase == "sample") == 3 && packets.Where(p => p.Phase == "sample").Select(p => p.Sequence).SequenceEqual(new long[] { 1, 2, 3 }), "Final authoritative sample or ordering lost");
        carrier.Deferred = true; string cancelled = Start(); Request(cancelled, 1);
        Check(data.Count == 1, "Cancelled pending teleport discarded recovery");
        carrier.Deferred = false; carrier.Teleports.Dequeue()();
        Check(carrier.Pos.X == 1 && data.Count == 0, "Late teleport after cancellation stranded carrier");
        string disconnected = Start(); Call("Disconnect", player); Check(data.Count == 1, "Disconnect discarded persisted return position");
        Call("Rejoin", player); Check(carrier.Pos.X == 1 && data.Count == 0, "Rejoin did not restore carrier");
        Check(carrier.WatchedAttributes.GetTreeAttribute("vsdirector-player-body") is null && Math.Abs(carrier.CollisionBox.Y2 - 1.8) < .001, "Reconnect retained temporary dimensions");
        string timeout = Start(); clock += 10001; Call("Tick", .033f); Check(carrier.Pos.X == 1, "Heartbeat timeout did not restore carrier");
        string longTake = Start(); Request(longTake, 3);
        for (int second = 0; second < 65; second++) { clock += 1000; Request(longTake, 2); Call("Tick", 1f); }
        Check(packets[^1].Phase == "sample" && packets[^1].Seconds >= 65, "Recording ended despite live heartbeats after ten seconds");
        Request(longTake, 1); Check(carrier.Pos.X == 1 && data.Count == 0, "Long take failed to restore player");
        string revoked = Start(); player.Allowed = false; Call("Tick", .033f); Check(carrier.Pos.X == 1, "Permission revocation did not stop recording");
        int before = packets.Count; Start(); Check(packets.Count == before + 1 && packets[^1].Error.Length > 0 && carrier.Pos.X == 1, "Unauthorized relocation accepted");
        player.Allowed = true;
        Call("Request", player, new DirectorPerformanceRequest { Session = Guid.NewGuid().ToString(), X = 10, Y = 100, Z = 20, CustomBody = true, BodyWidth = double.NaN });
        Check(packets[^1].Error.Length > 0 && carrier.WatchedAttributes.GetTreeAttribute("vsdirector-player-body") is null, "Invalid collision dimensions accepted");
        void Body(int action) => Call("Request", player, new DirectorPerformanceRequest { Session = Guid.NewGuid().ToString(), Action = action, CustomBody = true, BodyWidth = .25, BodyHeight = .7, SneakHeight = .4, EyeHeight = .6 });
        Body(4); Check(Math.Abs(carrier.CollisionBox.Y2 - .7) < .001, "Explicit player body apply failed");
        string withOverride = Start(); Request(withOverride, 1);
        Check(Math.Abs(carrier.CollisionBox.Y2 - .7) < .001, "Recording did not restore previous explicit dimensions");
        Body(5); Check(Math.Abs(carrier.CollisionBox.Y2 - 1.8) < .001, "Explicit restore failed");
        Body(4); Call("Disconnect", player);
        Check(carrier.WatchedAttributes.GetTreeAttribute("vsdirector-player-body") is null, "Disconnect retained explicit body override");
        Body(4); player.Allowed = false; Call("Tick", .033f);
        Check(Math.Abs(carrier.CollisionBox.Y2 - 1.8) < .001, "Revoked permission retained explicit body override");
        CheckBody(assembly);
        Console.WriteLine("PASS performance server start/stop, final samples, teleport cancellation, persistent disconnect/rejoin, timeout and permission revocation");
    }
    private static void CheckBody(Assembly assembly)
    {
        Type type = assembly.GetType("VSDirector.Runtime.DirectorPlayerBodyRuntime", true)!;
        void Call(string method, params object[] args) => AccessTools.Method(type, method).Invoke(null, args);
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var player = new EntityPlayer(); player.Pos.SetPos(10.5, 100, 10.5);
        player.SetCollisionBox(.6f, 1.8f); player.SetSelectionBox(.6f, 1.8f); player.LocalEyePos.Y = 1.7;
        bool ceiling = false;
        var air = new Block { CollisionBoxes = [] };
        var roof = new Block { CollisionBoxes = [new Cuboidf(0, .4f, 0, 1, 1, 1)] };
        var blocks = ProbeDispatchProxy.Create<IBlockAccessor>((m, a) => m.Name == "GetBlock"
            ? ceiling && a![0] is BlockPos p && p.InternalY == 100 ? roof : air : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var tester = new CollisionTester();
        player.World = ProbeDispatchProxy.Create<IWorldAccessor>((m, _) => m.Name switch { "get_BlockAccessor" => blocks, "get_CollisionTester" => tester, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType) });
        Call("Set", player, new CreationPlayerBody { Enabled = true, Width = .2, Height = .6, SneakHeight = .3, EyeHeight = .5 });
        player.ServerControls.Sneak = true; Call("Apply", player);
        Check(Math.Abs(player.CollisionBox.Y2 - .3) < .001 && player.LocalEyePos.Y == .25, "Custom crouch dimensions/eye ignored");
        ceiling = true; player.ServerControls.Sneak = false; Call("Apply", player);
        Check(!player.PrevFrameCanStandUp && Math.Abs(player.CollisionBox.Y2 - .3) < .001, "Player stood up into low ceiling");
        Check(!tester.IsColliding(blocks, player.CollisionBox, player.Pos.XYZ, false), "Small crouched body cannot fit below low ceiling");
        ceiling = false; Call("Apply", player);
        Check(player.PrevFrameCanStandUp && Math.Abs(player.CollisionBox.Y2 - .6) < .001, "Player failed to stand after leaving ceiling");
        // Simulate a client receiving the synchronized body attribute, then its removal.
        var client = new EntityPlayer(); client.SetCollisionBox(.6f, 1.8f); client.SetSelectionBox(.6f, 1.8f); client.LocalEyePos.Y = 1.7;
        client.WatchedAttributes["vsdirector-player-body"] = player.WatchedAttributes["vsdirector-player-body"].Clone();
        Call("Prepare", client); Call("Apply", client);
        Check(client.CollisionBox.Y2 == player.CollisionBox.Y2 && client.LocalEyePos.Y == .5, "Client/server physical dimensions differ");
        client.WatchedAttributes.RemoveAttribute("vsdirector-player-body"); Call("Prepare", client);
        Check(Math.Abs(client.CollisionBox.Y2 - 1.8) < .001 && client.LocalEyePos.Y == 1.7, "Client did not restore native dimensions");
        Call("Clear", player);
        Console.WriteLine("PASS independent collision width/height, crouch clearance, synchronized eye height and restoration");
    }
}
