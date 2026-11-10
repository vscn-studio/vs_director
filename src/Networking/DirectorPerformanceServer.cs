using ProtoBuf;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;

namespace VSDirector.Networking;

[ProtoContract]
public sealed class DirectorPerformanceRequest
{
    [ProtoMember(1)] public string Session = "";
    [ProtoMember(2)] public int Action;
    [ProtoMember(3)] public double X;
    [ProtoMember(4)] public double Y;
    [ProtoMember(5)] public double Z;
    [ProtoMember(6)] public float Yaw;
    [ProtoMember(7)] public int Dimension;
    [ProtoMember(8)] public bool CustomBody;
    [ProtoMember(9)] public double BodyWidth;
    [ProtoMember(10)] public double BodyHeight;
    [ProtoMember(11)] public double SneakHeight;
    [ProtoMember(12)] public double EyeHeight;
}
[ProtoContract]
public sealed class DirectorPerformanceStatus
{
    [ProtoMember(1)] public string Session = "";
    [ProtoMember(2)] public string Phase = "";
    [ProtoMember(3)] public string Error = "";
    [ProtoMember(4)] public double X;
    [ProtoMember(5)] public double Y;
    [ProtoMember(6)] public double Z;
    [ProtoMember(7)] public float Yaw;
    [ProtoMember(8)] public float Pitch;
    [ProtoMember(9)] public double Seconds;
    [ProtoMember(10)] public long Sequence;
}

/// <summary>Owns temporary player relocation. Gameplay continues through normal server rules.</summary>
internal sealed class DirectorPerformanceServer : IDisposable
{
    private sealed class Session
    {
        public required string Id;
        public required IServerPlayer Player;
        public required EntityPos Original;
        public long LastSeen, Start, Requested, Sequence;
        public bool Ready, Running, Closing;
        public VSDirector.Data.CreationPlayerBody? PreviousBody;
    }
    private readonly ICoreServerAPI api;
    private readonly DirectorPermissionServer permissions;
    private readonly IServerNetworkChannel channel;
    private readonly Dictionary<string, Session> sessions = new();
    private readonly Dictionary<string, IServerPlayer> bodyOverrides = new();
    private const string RestoreKey = "vsdirector-performance-return";
    [ProtoContract]
    public sealed class ReturnPosition
    {
        [ProtoMember(1)] public double X;
        [ProtoMember(2)] public double Y;
        [ProtoMember(3)] public double Z;
        [ProtoMember(4)] public int Dimension;
        [ProtoMember(5)] public float Yaw;
        [ProtoMember(6)] public float Pitch;
        [ProtoMember(7)] public float Roll;
        public EntityPos Position() => new EntityPos { Dimension = Dimension }.SetPos(X, Y, Z).SetAngles(Roll, Yaw, Pitch);
    }
    private readonly long tick;
    public DirectorPerformanceServer(ICoreServerAPI api, DirectorPermissionServer permissions)
    {
        this.api = api; this.permissions = permissions;
        channel = api.Network.GetChannel(DirectorNetwork.ChannelName).SetMessageHandler<DirectorPerformanceRequest>(Request);
        tick = api.Event.RegisterGameTickListener(Tick, 33);
        api.Event.PlayerDisconnect += Disconnect; api.Event.PlayerNowPlaying += Rejoin;
    }
    private void Request(IServerPlayer player, DirectorPerformanceRequest request)
    {
        if (request.Action is < 0 or > 5 || !Guid.TryParse(request.Session, out _)) return;
        var body = new VSDirector.Data.CreationPlayerBody { Enabled = request.CustomBody, Width = request.BodyWidth, Height = request.BodyHeight, SneakHeight = request.SneakHeight, EyeHeight = request.EyeHeight };
        if (request.Action is 4 or 5)
        {
            bool denied = sessions.ContainsKey(player.PlayerUID) || request.Action == 4
                && (!body.Enabled || !body.Valid() || !player.Entity.Alive || player.Entity.MountedOn is not null
                    || !permissions.Has(player, DirectorPermission.ActorRecording | DirectorPermission.FreeCamera));
            if (!denied)
            {
                if (request.Action == 4) { VSDirector.Runtime.DirectorPlayerBodyRuntime.Set(player.Entity, body); bodyOverrides[player.PlayerUID] = player; }
                else { VSDirector.Runtime.DirectorPlayerBodyRuntime.Clear(player.Entity); bodyOverrides.Remove(player.PlayerUID); }
            }
            channel.SendPacket(new DirectorPerformanceStatus { Session = request.Session, Phase = "body", Error = denied ? "creation-recording-denied" : "" }, player);
            return;
        }
        if (request.Action != 0)
        {
            if (!sessions.TryGetValue(player.PlayerUID, out var current) || current.Id != request.Session || current.Closing) return;
            current.LastSeen = api.World.ElapsedMilliseconds;
            if (request.Action == 1) End(current);
            else if (request.Action == 3 && current.Ready && !current.Running) { current.Running = true; current.Start = api.World.ElapsedMilliseconds; Sample(current); }
            return;
        }
        if (body.Enabled && !body.Valid() || !Guid.TryParse(request.Session, out _) || !permissions.Has(player, DirectorPermission.ActorRecording | DirectorPermission.FreeCamera)
            || sessions.ContainsKey(player.PlayerUID) || player.GetModData<ReturnPosition>(RestoreKey) is not null || !player.Entity.Alive || player.Entity.MountedOn is not null
            || !double.IsFinite(request.X) || !double.IsFinite(request.Y) || !double.IsFinite(request.Z) || !float.IsFinite(request.Yaw)
            || request.Dimension != player.Entity.Pos.Dimension || Math.Abs(request.X) > 30000000 || Math.Abs(request.Z) > 30000000
            || request.Y < 0 || request.Y > api.World.BlockAccessor.MapSizeY)
        { channel.SendPacket(new DirectorPerformanceStatus { Session = request.Session, Phase = "ended", Error = "creation-recording-denied" }, player); return; }
        var session = new Session { Id = request.Session, Player = player, Original = player.Entity.Pos.Copy(), LastSeen = api.World.ElapsedMilliseconds, Requested = api.World.ElapsedMilliseconds };
        session.PreviousBody = VSDirector.Runtime.DirectorPlayerBodyRuntime.Settings(player.Entity);
        var original = session.Original;
        player.SetModData(RestoreKey, new ReturnPosition { X = original.X, Y = original.Y, Z = original.Z, Dimension = original.Dimension, Yaw = original.Yaw, Pitch = original.Pitch, Roll = original.Roll });
        sessions[player.PlayerUID] = session;
        if (body.Enabled) VSDirector.Runtime.DirectorPlayerBodyRuntime.Set(player.Entity, body);
        var destination = session.Original.Copy(); destination.SetPos(request.X, request.Y, request.Z); destination.Yaw = request.Yaw; destination.Pitch = destination.Roll = 0;
        player.Entity.TeleportTo(destination, () =>
        {
            // Cancellation can race the asynchronous chunk-loading teleport. Restore again
            // after that callback, rather than allowing a late teleport to strand the player.
            if (session.Closing) { Restore(player, session.Original, session.Id); return; }
            if (!sessions.TryGetValue(player.PlayerUID, out var active) || !ReferenceEquals(active, session)) return;
            session.Ready = true; channel.SendPacket(new DirectorPerformanceStatus { Session = session.Id, Phase = "ready" }, player);
        });
    }
    private void Tick(float dt)
    {
        foreach (var player in bodyOverrides.Values.ToArray())
            if (!player.Entity.Alive || !permissions.Has(player, DirectorPermission.ActorRecording | DirectorPermission.FreeCamera))
            { VSDirector.Runtime.DirectorPlayerBodyRuntime.Clear(player.Entity); bodyOverrides.Remove(player.PlayerUID); }
        foreach (var session in sessions.Values.ToArray())
        {
            if (session.Closing) continue;
            if (!session.Player.Entity.Alive || api.World.ElapsedMilliseconds - session.LastSeen > 10000
                || !session.Running && api.World.ElapsedMilliseconds - session.Requested > 20000
                || !permissions.Has(session.Player, DirectorPermission.ActorRecording | DirectorPermission.FreeCamera)) { End(session); continue; }
            if (!session.Running) continue;
            Sample(session);
        }
    }
    private void Sample(Session session)
    {
        var pos = session.Player.Entity.Pos;
        channel.SendPacket(new DirectorPerformanceStatus { Session = session.Id, Phase = "sample", X = pos.X, Y = pos.Y, Z = pos.Z,
            Yaw = session.Player.Entity.BodyYaw, Pitch = pos.Pitch, Seconds = (api.World.ElapsedMilliseconds - session.Start) / 1000d, Sequence = ++session.Sequence }, session.Player);
    }
    private void End(Session session, bool disconnect = false)
    {
        if (session.Closing) return;
        session.Closing = true;
        if (!disconnect && session.Running) Sample(session);
        VSDirector.Runtime.DirectorPlayerBodyRuntime.Clear(session.Player.Entity);
        if (!disconnect && session.Player.Entity.Alive && session.PreviousBody is { } body && bodyOverrides.ContainsKey(session.Player.PlayerUID)
            && permissions.Has(session.Player, DirectorPermission.ActorRecording | DirectorPermission.FreeCamera))
            VSDirector.Runtime.DirectorPlayerBodyRuntime.Set(session.Player.Entity, body);
        if (session.Player.Entity.Alive)
        {
            if (disconnect) sessions.Remove(session.Player.PlayerUID);
            else if (session.Ready) Restore(session.Player, session.Original, session.Id);
        }
        else { sessions.Remove(session.Player.PlayerUID); session.Player.RemoveModdata(RestoreKey); }
        if (!disconnect) channel.SendPacket(new DirectorPerformanceStatus { Session = session.Id, Phase = "ended" }, session.Player);
    }
    private void Restore(IServerPlayer player, EntityPos position, string? sessionId = null)
    {
        if (!player.Entity.Alive) return;
        player.Entity.TeleportTo(position, () =>
        {
            if (sessions.TryGetValue(player.PlayerUID, out var current))
            {
                if (current.Id != sessionId) return;
                sessions.Remove(player.PlayerUID);
            }
            player.RemoveModdata(RestoreKey);
        });
    }
    private void Disconnect(IServerPlayer player)
    {
        bodyOverrides.Remove(player.PlayerUID); VSDirector.Runtime.DirectorPlayerBodyRuntime.Clear(player.Entity);
        if (!sessions.TryGetValue(player.PlayerUID, out var session)) return;
        if (session.Closing) sessions.Remove(player.PlayerUID);
        else End(session, true);
    }
    private void Rejoin(IServerPlayer player)
    {
        VSDirector.Runtime.DirectorPlayerBodyRuntime.Clear(player.Entity);
        if (player.GetModData<ReturnPosition>(RestoreKey) is { } position) Restore(player, position.Position());
    }
    public void Dispose()
    {
        foreach (var session in sessions.Values.ToArray()) End(session);
        foreach (var player in bodyOverrides.Values) VSDirector.Runtime.DirectorPlayerBodyRuntime.Clear(player.Entity);
        bodyOverrides.Clear();
        api.Event.UnregisterGameTickListener(tick); api.Event.PlayerDisconnect -= Disconnect; api.Event.PlayerNowPlaying -= Rejoin;
    }
}
