using Vintagestory.API.Client;

namespace VSDirector.Networking;

internal sealed class DirectorPermissionClient
{
    private readonly IClientNetworkChannel channel;
    private readonly Action<DirectorPermissionSnapshot> changed;

    public DirectorPermissionClient(
        ICoreClientAPI capi,
        Action<DirectorPermissionSnapshot> changed)
    {
        this.changed = changed;
        channel = capi.Network.GetChannel(DirectorNetwork.ChannelName)
            .SetMessageHandler<DirectorPermissionSnapshot>(OnSnapshot);
    }

    public bool IsKnown { get; private set; }

    public bool IsAdmin { get; private set; }

    public DirectorPermission Permissions { get; private set; }
        = DirectorPermissions.DefaultPlayerPermissions;

    public IReadOnlyList<DirectorPermissionMemberDto> Members { get; private set; }
        = Array.Empty<DirectorPermissionMemberDto>();

    public bool Has(DirectorPermission permission)
        => permission != DirectorPermission.None && DirectorPermissions.Sanitize((long)permission) == permission
            && (IsAdmin || (Permissions & permission) == permission);

    public void RequestSnapshot()
        => channel.SendPacket(new DirectorPermissionRequest
        {
            Action = (int)DirectorPermissionRequestAction.Refresh
        });

    public void Update(string playerUid, DirectorPermission permissions)
        => channel.SendPacket(new DirectorPermissionRequest
        {
            Action = (int)DirectorPermissionRequestAction.Update,
            PlayerUid = playerUid,
            PermissionMask = (long)(permissions & DirectorPermission.All)
        });

    public void Reset()
    {
        IsKnown = false;
        IsAdmin = false;
        Permissions = DirectorPermissions.DefaultPlayerPermissions;
        Members = Array.Empty<DirectorPermissionMemberDto>();
    }

    private void OnSnapshot(DirectorPermissionSnapshot snapshot)
    {
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
