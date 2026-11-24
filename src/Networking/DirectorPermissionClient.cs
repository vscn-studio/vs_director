using Vintagestory.API.Client;

namespace VSDirector.Networking;

internal sealed class DirectorPermissionClient
{
    private readonly ICoreClientAPI capi;
    private readonly IClientNetworkChannel channel;
    private readonly Action<DirectorPermissionSnapshot> changed;
    private long nextRefreshMs;
    private bool serverChannelSeen;

    public DirectorPermissionClient(
        ICoreClientAPI capi,
        Action<DirectorPermissionSnapshot> changed)
    {
        this.capi = capi;
        this.changed = changed;
        channel = capi.Network.GetChannel(DirectorNetwork.ChannelName)
            .SetMessageHandler<DirectorPermissionSnapshot>(OnSnapshot);
    }

    public bool IsKnown { get; private set; }

    public bool IsClientOnly { get; private set; }

    public bool IsAdmin { get; private set; }

    public DirectorPermission Permissions { get; private set; }
        = DirectorPermissions.DefaultPlayerPermissions;

    public IReadOnlyList<DirectorPermissionMemberDto> Members { get; private set; }
        = Array.Empty<DirectorPermissionMemberDto>();

    public bool Has(DirectorPermission permission)
        => permission != DirectorPermission.None && DirectorPermissions.Sanitize((long)permission) == permission
            && (IsAdmin || (Permissions & permission) == permission);

    public void Tick()
    {
        if (!IsKnown && capi.ElapsedMilliseconds >= nextRefreshMs)
            RequestSnapshot();
    }

    public void RequestSnapshot()
    {
        nextRefreshMs = capi.ElapsedMilliseconds + 1000;
        var state = capi.Network.GetChannelState(DirectorNetwork.ChannelName);
        if (state is not (EnumChannelState.Connected or EnumChannelState.NotConnected)) return;
        // In 1.22.7 GetChannelState reports Connected once the list arrives even
        // for a local channel absent from it. The channel itself records the match.
        if (!channel.Connected)
        {
            if (IsKnown || serverChannelSeen) return;
            IsClientOnly = true;
            IsKnown = true;
            IsAdmin = false;
            Permissions = DirectorPermissions.ClientOnlyPermissions;
            Members = Array.Empty<DirectorPermissionMemberDto>();
            changed(new DirectorPermissionSnapshot { PermissionMask = (long)Permissions });
            return;
        }
        if (state != EnumChannelState.Connected) return;
        serverChannelSeen = true;
        channel.SendPacket(new DirectorPermissionRequest
        {
            Action = (int)DirectorPermissionRequestAction.Refresh
        });
    }

    public void Update(string playerUid, DirectorPermission permissions)
    {
        if (!IsAdmin || !channel.Connected) return;
        channel.SendPacket(new DirectorPermissionRequest
        {
            Action = (int)DirectorPermissionRequestAction.Update,
            PlayerUid = playerUid,
            PermissionMask = (long)(permissions & DirectorPermission.All)
        });
    }

    public void Reset()
    {
        IsKnown = false;
        IsClientOnly = false;
        IsAdmin = false;
        serverChannelSeen = false;
        nextRefreshMs = 0;
        Permissions = DirectorPermissions.DefaultPlayerPermissions;
        Members = Array.Empty<DirectorPermissionMemberDto>();
    }

    private void OnSnapshot(DirectorPermissionSnapshot snapshot)
    {
        IsClientOnly = false;
        IsKnown = true;
        IsAdmin = snapshot.IsAdmin;
        Permissions = snapshot.IsAdmin
            ? DirectorPermission.All
            : DirectorPermissions.Sanitize(snapshot.PermissionMask)
                | DirectorPermissions.DefaultPlayerPermissions;
        Members = snapshot.IsAdmin
            ? snapshot.Members.ToArray()
            : Array.Empty<DirectorPermissionMemberDto>();
        changed(snapshot);
    }
}
