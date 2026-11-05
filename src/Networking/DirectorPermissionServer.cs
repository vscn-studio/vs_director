using Vintagestory.API.Server;
using Vintagestory.API.Config;

namespace VSDirector.Networking;

internal sealed class DirectorPermissionServer : IDisposable
{
    private const string StoreFileName = "VSDirectorPermissions.json";

    private readonly ICoreServerAPI sapi;
    private readonly IServerNetworkChannel channel;
    private DirectorPermissionStoreDocument store;

    public DirectorPermissionServer(ICoreServerAPI sapi)
    {
        this.sapi = sapi;
        store = Load();
        foreach (DirectorPermissionDefinition definition in DirectorPermissions.Definitions)
        {
            sapi.Permissions.RegisterPrivilege(
                definition.Code,
                "VS Director: " + definition.Code,
                adminAutoGrant: false);
        }

        channel = sapi.Network.GetChannel(DirectorNetwork.ChannelName)
            .SetMessageHandler<DirectorPermissionRequest>(OnRequest);
        sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
    }

    public bool Has(IServerPlayer player, DirectorPermission permission)
    {
        if (permission == DirectorPermission.None || DirectorPermissions.Sanitize((long)permission) != permission) return false;
        if (IsAdmin(player)
            || (DirectorPermissions.DefaultPlayerPermissions & permission) == permission)
        {
            return true;
        }

        DirectorPermission assigned = AssignedPermissions(player.PlayerUID);
        if ((assigned & permission) == permission)
        {
            return true;
        }

        return DirectorPermissions.Definitions
            .Where(definition => (permission & definition.Permission) != 0)
            .All(definition => ((assigned | DirectorPermissions.DefaultPlayerPermissions) & definition.Permission) != 0 || player.HasPrivilege(definition.Code));
    }

    public void Dispose()
        => sapi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        RememberPlayer(player.PlayerUID, player.PlayerName);
        SendSnapshot(player, includeRoster: IsAdmin(player));
    }

    private void OnRequest(IServerPlayer player, DirectorPermissionRequest request)
    {
        DirectorPermissionRequestAction action = Enum.IsDefined(
            typeof(DirectorPermissionRequestAction),
            request.Action)
                ? (DirectorPermissionRequestAction)request.Action
                : DirectorPermissionRequestAction.Refresh;
        RememberPlayer(player.PlayerUID, player.PlayerName);

        if (action == DirectorPermissionRequestAction.Update)
        {
            if (!IsAdmin(player))
            {
                SendSnapshot(player, includeRoster: false, error: "permission");
                return;
            }

            string targetUid = request.PlayerUid?.Trim() ?? string.Empty;
            if (targetUid.Length == 0)
            {
                SendSnapshot(player, includeRoster: true, error: "invalid-player");
                return;
            }

            IServerPlayerData? playerData = sapi.PlayerData.GetPlayerDataByUid(targetUid);
            string knownName = playerData?.LastKnownPlayername?.Trim() ?? string.Empty;
            if (playerData is null && !store.Players.ContainsKey(targetUid) && sapi.World.PlayerByUid(targetUid) is null)
            { SendSnapshot(player, includeRoster: true, error: "invalid-player"); return; }
            if (DirectorPermissions.IsAdminRole(playerData?.RoleCode) || HasStoredPrivilege(playerData, Privilege.controlserver)
                || sapi.World.PlayerByUid(targetUid) is IServerPlayer targetAdmin && IsAdmin(targetAdmin))
            {
                SendSnapshot(player, includeRoster: true, error: "admin-managed-by-role");
                return;
            }

            bool hadPrevious = store.Players.TryGetValue(
                targetUid,
                out DirectorPermissionStoreEntry? previous);
            DirectorPermission permissions = DirectorPermissions.Sanitize(request.PermissionMask)
                & ~DirectorPermissions.DefaultPlayerPermissions;
            if (permissions == DirectorPermission.None)
            {
                store.Players.Remove(targetUid);
            }
            else
            {
                store.Players[targetUid] = new DirectorPermissionStoreEntry
                {
                    PlayerName = knownName,
                    PermissionMask = (long)permissions
                };
            }

            if (!Save())
            {
                if (hadPrevious)
                {
                    store.Players[targetUid] = previous!;
                }
                else
                {
                    store.Players.Remove(targetUid);
                }
                SendSnapshot(player, includeRoster: true, error: "save-failed");
                return;
            }

            if (sapi.World.PlayerByUid(targetUid) is IServerPlayer target)
            {
                SendSnapshot(target, includeRoster: IsAdmin(target));
            }
        }

        SendSnapshot(player, includeRoster: IsAdmin(player));
    }

    private DirectorPermission AssignedPermissions(string playerUid)
        => store.Players.TryGetValue(playerUid, out DirectorPermissionStoreEntry? entry)
            ? DirectorPermissions.Sanitize(entry.PermissionMask)
            : DirectorPermission.None;

    private void RememberPlayer(string playerUid, string playerName)
    {
        if (store.Players.TryGetValue(playerUid, out DirectorPermissionStoreEntry? entry)
            && !string.Equals(entry.PlayerName, playerName, StringComparison.Ordinal))
        {
            entry.PlayerName = playerName;
            Save();
        }
    }

    private void SendSnapshot(IServerPlayer player, bool includeRoster, string error = "")
    {
        bool isAdmin = IsAdmin(player);
        DirectorPermission effective = isAdmin
            ? DirectorPermission.All
            : DirectorPermissions.DefaultPlayerPermissions | AssignedPermissions(player.PlayerUID);
        foreach (DirectorPermissionDefinition definition in DirectorPermissions.Definitions)
        {
            if (player.HasPrivilege(definition.Code))
            {
                effective |= definition.Permission;
            }
        }

        DirectorPermissionSnapshot snapshot = new()
        {
            IsAdmin = isAdmin,
            PermissionMask = (long)effective,
            Error = error
        };
        if (includeRoster)
        {
            snapshot.Members = BuildRoster();
        }
        channel.SendPacket(snapshot, player);
    }

    private List<DirectorPermissionMemberDto> BuildRoster()
    {
        Dictionary<string, IServerPlayerData> playerData = sapi.PlayerData.PlayerDataByUid;
        HashSet<string> playerUids = playerData.Keys
            .Concat(store.Players.Keys)
            .ToHashSet(StringComparer.Ordinal);
        return playerUids
            .Select(uid =>
            {
                playerData.TryGetValue(uid, out IServerPlayerData? data);
                store.Players.TryGetValue(uid, out DirectorPermissionStoreEntry? entry);
                var live = sapi.World.PlayerByUid(uid) as IServerPlayer;
                bool isAdmin = live is not null ? IsAdmin(live) : DirectorPermissions.IsAdminRole(data?.RoleCode) || HasStoredPrivilege(data, Privilege.controlserver);
                DirectorPermission inherited = DirectorPermissions.DefaultPlayerPermissions;
                foreach (var definition in DirectorPermissions.Definitions)
                    if (live is not null ? live.HasPrivilege(definition.Code) : HasStoredPrivilege(data, definition.Code)) inherited |= definition.Permission;
                DirectorPermission permissions = isAdmin
                    ? DirectorPermission.All
                    : inherited | AssignedPermissions(uid);
                return new DirectorPermissionMemberDto
                {
                    PlayerUid = uid,
                    PlayerName = data?.LastKnownPlayername?.Trim()
                        ?? entry?.PlayerName?.Trim()
                        ?? uid,
                    RoleCode = data?.RoleCode?.Trim() ?? string.Empty,
                    Online = sapi.World.PlayerByUid(uid) is IServerPlayer online
                        && online.ConnectionState == EnumClientState.Playing,
                    IsAdmin = isAdmin,
                    PermissionMask = (long)permissions,
                    AssignedMask = (long)AssignedPermissions(uid), InheritedMask = (long)inherited
                };
            })
            .OrderByDescending(member => member.Online)
            .ThenBy(member => member.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool HasStoredPrivilege(IServerPlayerData? data, string code)
        => data is not null && data.DeniedPrivileges?.Contains(code) != true
            && (data.PermaPrivileges?.Contains(code) == true || !string.IsNullOrWhiteSpace(data.RoleCode) && sapi.Permissions.GetRole(data.RoleCode)?.Privileges?.Contains(code) == true);

    private DirectorPermissionStoreDocument Load()
    {
        try
        {
            DirectorPermissionStoreDocument loaded
                = sapi.LoadModConfig<DirectorPermissionStoreDocument>(StoreFileName)
                    ?? new DirectorPermissionStoreDocument();
            loaded.Players ??= new Dictionary<string, DirectorPermissionStoreEntry>(StringComparer.Ordinal);
            return loaded;
        }
        catch (Exception exception)
        {
            sapi.Logger.Warning("VS Director: failed to load permissions: {0}", exception.Message);
            return new DirectorPermissionStoreDocument();
        }
    }

    private bool Save()
    {
        try
        {
            sapi.StoreModConfig(store, StoreFileName);
            return true;
        }
        catch (Exception exception)
        {
            sapi.Logger.Warning("VS Director: failed to save permissions: {0}", exception.Message);
            return false;
        }
    }

    internal static bool IsAdmin(IServerPlayer player)
        => DirectorPermissions.IsAdminRole(player.Role?.Code) || player.HasPrivilege(Privilege.controlserver);
}

public sealed class DirectorPermissionStoreDocument
{
    public Dictionary<string, DirectorPermissionStoreEntry> Players { get; set; }
        = new(StringComparer.Ordinal);
}

public sealed class DirectorPermissionStoreEntry
{
    public string PlayerName { get; set; } = string.Empty;

    public long PermissionMask { get; set; }
}
