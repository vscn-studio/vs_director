using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace VSDirector.Networking;

internal sealed class DirectorAnchorServer : IDisposable
{
    private const int DimensionHeight = 32768;
    private const int MinRadiusChunks = 1;
    private const int HardMaxRadiusChunks = 12;
    // Replay route preload is intentionally a small, moving observation
    // window.  Keep the server-side limit tight as a second line of defence
    // against a stale client or an oversized custom packet.
    private const int HardMaxPreloadSourceColumns = 16;
    private const int HardMaxPreloadExpandedColumns = 16;
    // Chunk-column loading can synchronously generate terrain and serialize a
    // full vertical column.  In an integrated server, doing several of those
    // operations from one network handler starves the host tick long enough
    // for the client watchdog to report an unresponsive host.  Schedule one
    // column at a time from the regular server tick instead.
    private const int MaxColumnsPerEnsure = 1;
    private const long ColumnRetryIntervalMs = 2000;
    private const int MaxBlockSoundsPerSecond = 32;
    private const double MaxBlockSoundSourceDistance = 16d;
    private const int MaxSoundLocationLength = 256;

    private readonly ICoreServerAPI sapi;
    private readonly IServerNetworkChannel channel;
    private readonly DirectorServerConfig config;
    private readonly DirectorPermissionServer permissions;
    private readonly Dictionary<string, AnchorState> anchors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreloadState> preloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlockSoundRateState> blockSoundRates = new(StringComparer.Ordinal);
    private readonly AnchorChunkRetention retention;
    private readonly long tickListenerId;
    private bool preferPreloadWork;

    public DirectorAnchorServer(ICoreServerAPI sapi, DirectorPermissionServer permissions)
    {
        this.sapi = sapi;
        this.permissions = permissions;
        config = LoadConfig(sapi);
        retention = new AnchorChunkRetention(sapi);

        channel = sapi.Network.GetChannel(DirectorNetwork.ChannelName)
            .SetMessageHandler<DirectorAnchorRequest>(OnAnchorRequest)
            .SetMessageHandler<DirectorChunkPreloadRequest>(OnPreloadRequest)
            .SetMessageHandler<DirectorBlockSoundEvent>(OnBlockSound);
        sapi.Event.PlayerDisconnect += OnPlayerDisconnect;
        tickListenerId = sapi.Event.RegisterGameTickListener(OnServerTick, 250);
    }

    internal DirectorServerConfig Config => config;

    public void Dispose()
    {
        sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
        sapi.Event.UnregisterGameTickListener(tickListenerId);
        foreach (string playerUid in anchors.Keys.ToArray())
        {
            RemoveAnchor(playerUid);
        }
        foreach (string playerUid in preloads.Keys.ToArray())
        {
            RemovePreload(playerUid);
        }
        anchors.Clear();
        preloads.Clear();
        blockSoundRates.Clear();
    }

    private void OnAnchorRequest(IServerPlayer player, DirectorAnchorRequest request)
    {
        int maxRadius = EffectiveMaxRadius;
        if (!HasAccess(player))
        {
            RemoveAnchor(player.PlayerUID);
            SendStatus(player, allowed: false, "permission", maxRadius);
            return;
        }

        if (!request.Active)
        {
            RemoveAnchor(player.PlayerUID);
            SendStatus(player, allowed: true, string.Empty, maxRadius);
            return;
        }

        if (player.Entity is null)
        {
            RemoveAnchor(player.PlayerUID);
            SendStatus(player, allowed: false, "invalid-anchor", maxRadius);
            return;
        }

        double targetX = request.X;
        double targetZ = request.Z;
        int targetDimension = request.Dimension;
        bool targetRequested = !string.IsNullOrWhiteSpace(request.TargetPlayerUid)
            || request.TargetEntityId > 0;
        bool targetAvailable = false;
        bool hasTargetAnchor = false;
        long targetEntityId = 0;
        int targetViewDistance = 0;
        if (!string.IsNullOrWhiteSpace(request.TargetPlayerUid))
        {
            IServerPlayer? targetPlayer = sapi.World.PlayerByUid(request.TargetPlayerUid.Trim()) as IServerPlayer;
            if (targetPlayer?.Entity is not null && targetPlayer.ConnectionState == EnumClientState.Playing)
            {
                targetX = targetPlayer.Entity.Pos.X;
                targetZ = targetPlayer.Entity.Pos.Z;
                targetDimension = targetPlayer.Entity.Pos.Dimension;
                targetEntityId = targetPlayer.Entity.EntityId;
                targetViewDistance = targetPlayer.WorldData.LastApprovedViewDistance;
                targetAvailable = true;
                hasTargetAnchor = true;
            }
        }
        else if (request.TargetEntityId > 0)
        {
            Entity? targetEntity = sapi.World.GetEntityById(request.TargetEntityId);
            if (targetEntity is not null)
            {
                targetX = targetEntity.Pos.X;
                targetZ = targetEntity.Pos.Z;
                targetDimension = targetEntity.Pos.Dimension;
                targetEntityId = targetEntity.EntityId;
                targetAvailable = true;
                hasTargetAnchor = true;
            }
        }

        if (!hasTargetAnchor && request.HasSecondaryAnchor)
        {
            targetX = request.SecondaryX;
            targetZ = request.SecondaryZ;
            targetDimension = request.SecondaryDimension;
            hasTargetAnchor = true;
        }

        double cameraX = request.X;
        double cameraZ = request.Z;
        int anchorDimension = request.Dimension;
        if (hasTargetAnchor && targetDimension != request.Dimension && config.AllowCrossDimensionAnchors)
        {
            // A remote player can be resolved before the client knows which
            // dimension to request. Bootstrap both anchor centers at the
            // server-authoritative target until the client receives it.
            cameraX = targetX;
            cameraZ = targetZ;
            anchorDimension = targetDimension;
        }

        if ((!config.AllowCrossDimensionAnchors && request.Dimension != player.Entity.Pos.Dimension)
            || (!config.AllowCrossDimensionAnchors && targetDimension != request.Dimension)
            || !double.IsFinite(request.X)
            || !double.IsFinite(request.Z)
            || !double.IsFinite(targetX)
            || !double.IsFinite(targetZ))
        {
            RemoveAnchor(player.PlayerUID);
            SendStatus(
                player,
                allowed: false,
                "invalid-anchor",
                maxRadius,
                targetRequested,
                targetAvailable,
                targetEntityId);
            return;
        }

        int chunkSize = sapi.WorldManager.ChunkSize;
        int cameraChunkX = (int)Math.Floor(cameraX / chunkSize);
        int cameraChunkZ = (int)Math.Floor(cameraZ / chunkSize);
        int targetChunkX = (int)Math.Floor(targetX / chunkSize);
        int targetChunkZ = (int)Math.Floor(targetZ / chunkSize);
        if (!IsValidChunkColumn(cameraChunkX, cameraChunkZ)
            || (hasTargetAnchor && !IsValidChunkColumn(targetChunkX, targetChunkZ)))
        {
            RemoveAnchor(player.PlayerUID);
            SendStatus(player, allowed: false, "invalid-anchor", maxRadius);
            return;
        }

        int radius = request.CompactWindow
            ? ResolveCompactAnchorRadius(request.RadiusChunks, maxRadius)
            : ResolveAnchorRadius(
                request.RadiusChunks,
                targetViewDistance,
                chunkSize,
                maxRadius);
        long now = sapi.World.ElapsedMilliseconds;
        AnchorState state = new(
            cameraChunkX,
            cameraChunkZ,
            hasTargetAnchor ? targetChunkX : null,
            hasTargetAnchor ? targetChunkZ : null,
            anchorDimension,
            radius,
            now);
        if (anchors.TryGetValue(player.PlayerUID, out AnchorState? existing)
            && existing.HasSameArea(state))
        {
            existing.LastRequestMs = now;
            existing.EnsurePending = true;
            SendStatus(
                player,
                allowed: true,
                string.Empty,
                maxRadius,
                targetRequested,
                targetAvailable,
                targetEntityId);
            return;
        }

        RetainState(state);
        if (existing is not null)
        {
            ReleaseState(existing);
        }
        anchors[player.PlayerUID] = state;
        UpdateProtectedChunks(player.PlayerUID);
        sapi.Logger.Debug(
            "VS Director: remote chunk anchor for {0} moved to {1}, radius {2} ({3} columns).",
            player.PlayerName,
            string.Join(" and ", state.Centers.Select(center => $"({center.X},{center.Z},d{state.Dimension})")),
            state.Radius,
            state.TotalColumns);
        SendStatus(
            player,
            allowed: true,
            string.Empty,
            maxRadius,
            targetRequested,
            targetAvailable,
            targetEntityId);
    }

    private void OnPreloadRequest(IServerPlayer player, DirectorChunkPreloadRequest request)
    {
        if (!HasAccess(player))
        {
            RemovePreload(player.PlayerUID);
            SendPreloadStatus(player, request.ReplayId, allowed: false, "permission", 0, 0);
            return;
        }
        if (!request.Active)
        {
            RemovePreload(player.PlayerUID);
            SendPreloadStatus(player, string.Empty, allowed: true, string.Empty, 0, 0);
            return;
        }

        string replayId = request.ReplayId?.Trim() ?? string.Empty;
        long now = sapi.World.ElapsedMilliseconds;
        if (request.Columns is not { Count: > 0 })
        {
            if (preloads.TryGetValue(player.PlayerUID, out PreloadState? heartbeat)
                && string.Equals(heartbeat.ReplayId, replayId, StringComparison.Ordinal))
            {
                heartbeat.LastRequestMs = now;
                heartbeat.EnsurePending = true;
                SendPreloadStatus(
                    player,
                    replayId,
                    allowed: true,
                    string.Empty,
                    heartbeat.Columns.Count,
                    heartbeat.LastCompleteColumns);
                return;
            }

            SendPreloadStatus(player, replayId, allowed: false, "invalid-preload", 0, 0);
            return;
        }

        if (!Guid.TryParse(replayId, out _)
            || request.Columns.Count > HardMaxPreloadSourceColumns
            || player.Entity is null)
        {
            RemovePreload(player.PlayerUID);
            SendPreloadStatus(player, replayId, allowed: false, "invalid-preload", 0, 0);
            return;
        }

        int radius = Math.Clamp(request.RadiusChunks, 0, 2);
        int radiusSquared = radius * radius;
        HashSet<PreloadColumn> expanded = new();
        bool invalid = false;
        foreach (DirectorChunkPreloadColumn source in request.Columns)
        {
            if (source is null
                || source.Dimension < 0
                || (!config.AllowCrossDimensionAnchors && source.Dimension != player.Entity.Pos.Dimension))
            {
                invalid = true;
                break;
            }

            for (int offsetX = -radius; offsetX <= radius && !invalid; offsetX++)
            {
                for (int offsetZ = -radius; offsetZ <= radius; offsetZ++)
                {
                    if (offsetX * offsetX + offsetZ * offsetZ > radiusSquared)
                    {
                        continue;
                    }

                    PreloadColumn column = new(
                        source.X + offsetX,
                        source.Z + offsetZ,
                        source.Dimension);
                    if (!IsValidChunkColumn(column.X, column.Z)
                        || (!expanded.Contains(column)
                            && expanded.Count >= HardMaxPreloadExpandedColumns))
                    {
                        invalid = true;
                        break;
                    }
                    expanded.Add(column);
                }
            }
            if (invalid)
            {
                break;
            }
        }

        if (invalid || expanded.Count == 0)
        {
            RemovePreload(player.PlayerUID);
            SendPreloadStatus(player, replayId, allowed: false, "invalid-preload", 0, 0);
            return;
        }

        PreloadState next = new(replayId, expanded, now);
        RetainPreload(next);
        if (preloads.Remove(player.PlayerUID, out PreloadState? existing))
        {
            ReleasePreload(existing);
        }
        preloads[player.PlayerUID] = next;
        UpdateProtectedChunks(player.PlayerUID);
        sapi.Logger.Debug(
            "VS Director: preloading replay {0} for {1}: {2} scheduled columns.",
            replayId,
            player.PlayerName,
            next.Columns.Count);
        SendPreloadStatus(
            player,
            replayId,
            allowed: true,
            string.Empty,
            next.Columns.Count,
            next.LastCompleteColumns);
    }

    private void OnBlockSound(IServerPlayer player, DirectorBlockSoundEvent request)
    {
        if (player.ConnectionState != EnumClientState.Playing
            || (!permissions.Has(player, DirectorPermission.ReplayCapture)
                && !permissions.Has(player, DirectorPermission.StandardRecording))
            || player.Entity is null
            || request is null
            || sapi.World is not ServerMain server
            || !AcceptBlockSound(player, sapi.World.ElapsedMilliseconds))
        {
            return;
        }

        if (!double.IsFinite(request.X)
            || !double.IsFinite(request.Y)
            || !double.IsFinite(request.Z)
            || !float.IsFinite(request.Pitch)
            || !float.IsFinite(request.Range)
            || !float.IsFinite(request.Volume)
            || request.Dimension < 0
            || request.Dimension != player.Entity.Pos.Dimension)
        {
            return;
        }

        int soundDimension = (int)Math.Floor(request.Y / DimensionHeight);
        if (soundDimension != request.Dimension)
        {
            return;
        }

        double dx = request.X - player.Entity.Pos.X;
        double dy = request.Y - player.Entity.Pos.InternalY;
        double dz = request.Z - player.Entity.Pos.Z;
        if (dx * dx + dy * dy + dz * dz > MaxBlockSoundSourceDistance * MaxBlockSoundSourceDistance)
        {
            return;
        }

        int blockX = request.HasBlockPosition ? request.BlockX : (int)Math.Floor(request.X);
        int blockY = request.HasBlockPosition ? request.BlockY : (int)Math.Floor(request.Y);
        int blockZ = request.HasBlockPosition ? request.BlockZ : (int)Math.Floor(request.Z);
        if (request.HasBlockPosition
            && (Math.Abs(request.X - (blockX + 0.5d)) > 2.5d
                || Math.Abs(request.Y - (blockY + 0.5d)) > 2.5d
                || Math.Abs(request.Z - (blockZ + 0.5d)) > 2.5d))
        {
            return;
        }

        string rawLocation = request.Location?.Trim() ?? string.Empty;
        if (rawLocation.Length == 0
            || rawLocation.Length > MaxSoundLocationLength
            || rawLocation.IndexOfAny(new[] { '\0', '\r', '\n', '\\' }) >= 0
            || rawLocation.Contains("..", StringComparison.Ordinal))
        {
            return;
        }

        AssetLocation location;
        try
        {
            location = new AssetLocation(rawLocation);
        }
        catch
        {
            return;
        }

        if (!location.Valid
            || !location.Path.StartsWith("sounds/", StringComparison.OrdinalIgnoreCase)
            || !Enum.IsDefined(typeof(EnumSoundType), request.SoundType))
        {
            return;
        }

        float pitch = Math.Clamp(request.Pitch, 0.25f, 4f);
        float range = Math.Clamp(request.Range, 1f, 128f);
        float volume = Math.Clamp(request.Volume, 0f, 4f);
        if (volume <= 0f)
        {
            return;
        }

        DirectorAnchorSoundBroadcastPatch.ForwardClientSound(
            server,
            player.ClientId,
            location,
            request.X,
            request.Y,
            request.Z,
            blockX,
            blockY,
            blockZ,
            pitch,
            range,
            volume,
            (EnumSoundType)request.SoundType);
    }

    private bool AcceptBlockSound(IServerPlayer player, long now)
    {
        string playerUid = player.PlayerUID;
        if (!blockSoundRates.TryGetValue(playerUid, out BlockSoundRateState? state)
            || now - state.WindowStartMs >= 1000)
        {
            blockSoundRates[playerUid] = new BlockSoundRateState(now, 1);
            return true;
        }

        if (state.Count >= MaxBlockSoundsPerSecond)
        {
            return false;
        }

        state.Count++;
        return true;
    }

    private void OnServerTick(float deltaTime)
    {
        _ = deltaTime;
        long now = sapi.World.ElapsedMilliseconds;
        long timeoutMs = Math.Clamp(config.AnchorTimeoutSeconds, 2, 60) * 1000L;
        long resendMs = Math.Clamp(config.ResendIntervalMs, 250, 5000);
        foreach ((string playerUid, AnchorState state) in anchors.ToArray())
        {
            IServerPlayer? player = sapi.World.PlayerByUid(playerUid) as IServerPlayer;
            if (player is null
                || player.ConnectionState != EnumClientState.Playing
                || !HasAccess(player)
                || now - state.LastRequestMs > timeoutMs)
            {
                RemoveAnchor(playerUid);
                continue;
            }

        }

        foreach ((string playerUid, PreloadState state) in preloads.ToArray())
        {
            IServerPlayer? player = sapi.World.PlayerByUid(playerUid) as IServerPlayer;
            if (player is null
                || player.ConnectionState != EnumClientState.Playing
                || !HasAccess(player)
                || now - state.LastRequestMs > timeoutMs)
            {
                RemovePreload(playerUid);
                continue;
            }

        }

        // Do not let a packet handler or a single server tick synchronously
        // load multiple columns.  In singleplayer that work runs beside the
        // client and can trip the host watchdog.  Alternating queues keeps
        // the current observation anchor responsive while route preload
        // continues in the background.
        bool handled = preferPreloadWork
            ? TryEnsurePreloadChunks(now, resendMs)
            : TryEnsureAnchorChunks(now, resendMs);
        if (!handled)
        {
            handled = preferPreloadWork
                ? TryEnsureAnchorChunks(now, resendMs)
                : TryEnsurePreloadChunks(now, resendMs);
        }
        if (handled)
        {
            preferPreloadWork = !preferPreloadWork;
        }
    }

    private bool TryEnsureAnchorChunks(long now, long resendMs)
    {
        foreach ((string playerUid, AnchorState state) in anchors)
        {
            if (!state.EnsurePending && now - state.LastEnsureMs < resendMs)
            {
                continue;
            }

            if (sapi.World.PlayerByUid(playerUid) is IServerPlayer player
                && player.ConnectionState == EnumClientState.Playing
                && HasAccess(player))
            {
                EnsureAnchorChunks(player, state, now);
                return true;
            }
        }

        return false;
    }

    private bool TryEnsurePreloadChunks(long now, long resendMs)
    {
        foreach ((string playerUid, PreloadState state) in preloads)
        {
            if (!state.EnsurePending && now - state.LastEnsureMs < resendMs)
            {
                continue;
            }

            if (sapi.World.PlayerByUid(playerUid) is IServerPlayer player
                && player.ConnectionState == EnumClientState.Playing
                && HasAccess(player))
            {
                EnsurePreloadChunks(player, state, now);
                return true;
            }
        }

        return false;
    }

    private void EnsureAnchorChunks(IServerPlayer player, AnchorState state, long now)
    {
        int chunkSize = sapi.WorldManager.ChunkSize;
        int total = state.Columns.Count;
        int start = total == 0 ? 0 : state.NextColumnIndex % total;
        int inspected = Math.Min(MaxColumnsPerEnsure, total);
        for (int offset = 0; offset < inspected; offset++)
        {
            AnchorColumn column = state.Columns[(start + offset) % total];
            if (state.CompleteColumns.Contains(column))
            {
                continue;
            }

            if (HasCompleteColumn(player, column.X, column.Z, state.Dimension))
            {
                state.CompleteColumns.Add(column);
                RetainAnchorColumn(state, column);
                continue;
            }

            if (!TryMarkColumnDispatch(state.LastDispatchColumns, column, now))
            {
                continue;
            }

            RetainAnchorColumn(state, column);
            int dimensionOffset = state.Dimension * DimensionHeight / chunkSize;
            // ForceSendChunkColumn() only queues 3D chunks in 1.22.3.  The
            // client also needs the 2D map chunk before remote terrain can be
            // meshed correctly.
            sapi.WorldManager.SendChunk(
                column.X,
                dimensionOffset,
                column.Z,
                player,
                onlyIfInRange: false);
            if (state.Dimension != 0 && state.LoadRequestedColumns.Add(column))
            {
                sapi.WorldManager.LoadChunkColumnForDimension(column.X, column.Z, state.Dimension);
            }
            sapi.WorldManager.ForceSendChunkColumn(player, column.X, column.Z, state.Dimension);
        }

        state.NextColumnIndex = total == 0 ? 0 : (start + inspected) % total;
        state.EnsurePending = false;
        state.LastEnsureMs = now;
        ReportAnchorProgress(player, state);
    }

    private void EnsurePreloadChunks(IServerPlayer player, PreloadState state, long now)
    {
        int chunkSize = sapi.WorldManager.ChunkSize;
        PreloadColumn[] columns = state.Columns.ToArray();
        int start = columns.Length == 0 ? 0 : state.NextColumnIndex % columns.Length;
        int inspected = Math.Min(MaxColumnsPerEnsure, columns.Length);
        for (int offset = 0; offset < inspected; offset++)
        {
            PreloadColumn column = columns[(start + offset) % columns.Length];
            if (state.CompleteColumns.Contains(column))
            {
                continue;
            }

            if (HasCompleteColumn(player, column.X, column.Z, column.Dimension))
            {
                state.CompleteColumns.Add(column);
                continue;
            }

            if (!TryMarkColumnDispatch(state.LastDispatchColumns, column, now))
            {
                continue;
            }

            int dimensionOffset = column.Dimension * DimensionHeight / chunkSize;
            sapi.WorldManager.SendChunk(
                column.X,
                dimensionOffset,
                column.Z,
                player,
                onlyIfInRange: false);
            // Route preload must never synchronously generate terrain.  That
            // API can block an integrated server long enough for the game to
            // declare its host unresponsive.  The anchor owns loading the
            // small area currently being observed; this advisory path only
            // sends columns that are already available.
            sapi.WorldManager.ForceSendChunkColumn(player, column.X, column.Z, column.Dimension);
            state.CompleteColumns.Add(column);
        }

        state.NextColumnIndex = columns.Length == 0
            ? 0
            : (start + inspected) % columns.Length;
        int completeColumns = state.CompleteColumns.Count;
        state.EnsurePending = false;
        state.LastEnsureMs = now;
        if (completeColumns == state.LastCompleteColumns)
        {
            return;
        }

        state.LastCompleteColumns = completeColumns;
        sapi.Logger.Debug(
            "VS Director: replay preload for {0}: {1}/{2} columns sent.",
            player.PlayerName,
            completeColumns,
            state.Columns.Count);
        SendPreloadStatus(
            player,
            state.ReplayId,
            allowed: true,
            string.Empty,
            state.Columns.Count,
            completeColumns);
    }

    private void RetainAnchorColumn(AnchorState state, AnchorColumn column)
    {
        if (state.Dimension == 0 && state.RetainedColumns.Add(column))
        {
            retention.Acquire(column);
        }
    }

    private void ReportAnchorProgress(IServerPlayer player, AnchorState state)
    {
        int completeColumns = state.CompleteColumns.Count;
        if (completeColumns == state.LastCompleteColumns)
        {
            return;
        }

        state.LastCompleteColumns = completeColumns;
        sapi.Logger.Debug(
            "VS Director: remote chunks for {0}: {1}/{2} columns sent.",
            player.PlayerName,
            completeColumns,
            state.TotalColumns);
    }

    private static bool TryMarkColumnDispatch<TColumn>(
        Dictionary<TColumn, long> lastDispatch,
        TColumn column,
        long now)
        where TColumn : notnull
    {
        if (lastDispatch.TryGetValue(column, out long previous)
            && now - previous < ColumnRetryIntervalMs)
        {
            return false;
        }

        lastDispatch[column] = now;
        return true;
    }

    private bool HasCompleteColumn(IServerPlayer player, int chunkX, int chunkZ, int dimension)
    {
        int chunkSize = sapi.WorldManager.ChunkSize;
        int chunkCountY = (sapi.WorldManager.MapSizeY + chunkSize - 1) / chunkSize;
        int dimensionOffset = dimension * DimensionHeight / chunkSize;
        for (int chunkY = 0; chunkY < chunkCountY; chunkY++)
        {
            if (!sapi.WorldManager.HasChunk(chunkX, chunkY + dimensionOffset, chunkZ, player))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsValidChunkColumn(int chunkX, int chunkZ)
    {
        int chunkSize = sapi.WorldManager.ChunkSize;
        int maxX = (sapi.WorldManager.MapSizeX + chunkSize - 1) / chunkSize;
        int maxZ = (sapi.WorldManager.MapSizeZ + chunkSize - 1) / chunkSize;
        return chunkX >= 0 && chunkX < maxX && chunkZ >= 0 && chunkZ < maxZ;
    }

    private bool HasAccess(IServerPlayer player)
        => permissions.Has(player, DirectorPermission.RemoteChunkAccess);

    private int EffectiveMaxRadius
        => Math.Clamp(config.MaxAnchorRadiusChunks, MinRadiusChunks, HardMaxRadiusChunks);

    internal static int ResolveAnchorRadius(
        int requestedRadiusChunks,
        int targetViewDistance,
        int chunkSize,
        int maxRadiusChunks)
    {
        int targetRadius = targetViewDistance > 0 && chunkSize > 0
            ? (int)Math.Ceiling(targetViewDistance / (double)chunkSize)
            : 0;
        int desiredRadius = Math.Max(requestedRadiusChunks, targetRadius);
        return Math.Clamp(
            desiredRadius,
            MinRadiusChunks,
            Math.Max(MinRadiusChunks, maxRadiusChunks));
    }

    private static int ResolveCompactAnchorRadius(int requestedRadiusChunks, int maxRadiusChunks)
        => Math.Clamp(requestedRadiusChunks, 0, Math.Max(MinRadiusChunks, maxRadiusChunks));

    private void SendStatus(
        IServerPlayer player,
        bool allowed,
        string messageCode,
        int maxRadius,
        bool targetRequested = false,
        bool targetAvailable = false,
        long targetEntityId = 0)
    {
        channel.SendPacket(new DirectorAnchorStatus
        {
            Allowed = allowed,
            MessageCode = messageCode,
            MaxRadiusChunks = maxRadius,
            TargetRequested = targetRequested,
            TargetAvailable = targetAvailable,
            TargetEntityId = targetEntityId
        }, player);
    }

    private void SendPreloadStatus(
        IServerPlayer player,
        string replayId,
        bool allowed,
        string messageCode,
        int totalColumns,
        int sentColumns)
    {
        channel.SendPacket(new DirectorChunkPreloadStatus
        {
            Allowed = allowed,
            ReplayId = replayId,
            MessageCode = messageCode,
            TotalColumns = Math.Max(0, totalColumns),
            SentColumns = Math.Clamp(sentColumns, 0, Math.Max(0, totalColumns))
        }, player);
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        RemoveAnchor(player.PlayerUID);
        RemovePreload(player.PlayerUID);
        blockSoundRates.Remove(player.PlayerUID);
    }

    private void RetainState(AnchorState state)
    {
        int chunkSize = sapi.WorldManager.ChunkSize;
        int chunkCountY = (sapi.WorldManager.MapSizeY + chunkSize - 1) / chunkSize;
        int dimensionOffset = state.Dimension * DimensionHeight / chunkSize;
        state.Columns.Clear();
        state.Columns.AddRange(EnumerateColumns(state).OrderBy(column => AnchorDistanceSquared(state, column)));
        foreach (AnchorColumn column in state.Columns)
        {
            for (int chunkY = 0; chunkY < chunkCountY; chunkY++)
            {
                state.ProtectedChunks.Add(sapi.WorldManager.ChunkIndex3D(
                    column.X,
                    chunkY + dimensionOffset,
                    column.Z));
            }
        }
        state.TotalColumns = state.Columns.Count;
        state.EnsurePending = true;
    }

    private static long AnchorDistanceSquared(AnchorState state, AnchorColumn column)
    {
        long nearest = long.MaxValue;
        foreach ((int x, int z) in state.Centers)
        {
            long dx = column.X - (long)x;
            long dz = column.Z - (long)z;
            nearest = Math.Min(nearest, dx * dx + dz * dz);
        }
        return nearest;
    }

    private void RetainPreload(PreloadState state)
    {
        // Route preload is deliberately advisory. Keeping every expanded
        // column loaded (up to 4096 columns) pins a large portion of the
        // integrated server world and can make the host watchdog report that
        // the server is unresponsive. The bounded EnsurePreloadChunks loop
        // requests columns in small batches instead; the normal anchor keeps
        // the current observation area alive.
        _ = state;
    }

    private void ReleaseState(AnchorState state)
    {
        foreach (AnchorColumn column in state.RetainedColumns)
        {
            retention.Release(column);
        }
        state.RetainedColumns.Clear();
        state.Columns.Clear();
        state.CompleteColumns.Clear();
        state.LoadRequestedColumns.Clear();
        state.LastDispatchColumns.Clear();
        state.ProtectedChunks.Clear();
    }

    private void ReleasePreload(PreloadState state)
    {
        foreach (AnchorColumn column in state.RetainedColumns)
        {
            retention.Release(column);
        }
        state.RetainedColumns.Clear();
        state.LoadRequestedColumns.Clear();
        state.CompleteColumns.Clear();
        state.LastDispatchColumns.Clear();
        state.EnsurePending = false;
        state.ProtectedChunks.Clear();
    }

    private void RemoveAnchor(string playerUid)
    {
        if (anchors.Remove(playerUid, out AnchorState? state))
        {
            ReleaseState(state);
        }
        UpdateProtectedChunks(playerUid);
    }

    private void RemovePreload(string playerUid)
    {
        if (preloads.Remove(playerUid, out PreloadState? state))
        {
            ReleasePreload(state);
        }
        UpdateProtectedChunks(playerUid);
    }

    private void UpdateProtectedChunks(string playerUid)
    {
        HashSet<long> protectedChunks = new();
        HashSet<long> entityTrackingChunks = new();
        HashSet<DirectorAnchorSoundColumn> soundColumns = new();
        if (anchors.TryGetValue(playerUid, out AnchorState? anchor))
        {
            protectedChunks.UnionWith(anchor.ProtectedChunks);
            entityTrackingChunks.UnionWith(anchor.ProtectedChunks);
            soundColumns.UnionWith(EnumerateColumns(anchor).Select(column =>
                new DirectorAnchorSoundColumn(column.X, column.Z, anchor.Dimension)));
        }
        if (preloads.TryGetValue(playerUid, out PreloadState? preload))
        {
            protectedChunks.UnionWith(preload.ProtectedChunks);
        }

        if (protectedChunks.Count > 0)
        {
            DirectorAnchorChunkProtection.Set(
                playerUid,
                protectedChunks,
                entityTrackingChunks,
                soundColumns);
        }
        else
        {
            DirectorAnchorChunkProtection.Remove(playerUid);
        }
    }

    private IEnumerable<AnchorColumn> EnumerateColumns(AnchorState state)
    {
        HashSet<AnchorColumn> yielded = new();
        int radiusSquared = state.Radius * state.Radius;
        foreach ((int centerX, int centerZ) in state.Centers)
        {
            for (int offsetX = -state.Radius; offsetX <= state.Radius; offsetX++)
            {
                for (int offsetZ = -state.Radius; offsetZ <= state.Radius; offsetZ++)
                {
                    if (offsetX * offsetX + offsetZ * offsetZ > radiusSquared)
                    {
                        continue;
                    }

                    AnchorColumn column = new(centerX + offsetX, centerZ + offsetZ);
                    if (IsValidChunkColumn(column.X, column.Z) && yielded.Add(column))
                    {
                        yield return column;
                    }
                }
            }
        }
    }

    private static DirectorServerConfig LoadConfig(ICoreServerAPI api)
    {
        const string fileName = "VSDirectorServer.json";
        try
        {
            DirectorServerConfig config = api.LoadModConfig<DirectorServerConfig>(fileName) ?? new DirectorServerConfig();
            api.StoreModConfig(config, fileName);
            return config;
        }
        catch (Exception ex)
        {
            api.Logger.Warning("VS Director: failed to load server config: {0}", ex.Message);
            return new DirectorServerConfig();
        }
    }

    private sealed class AnchorState
    {
        public AnchorState(
            int cameraChunkX,
            int cameraChunkZ,
            int? targetChunkX,
            int? targetChunkZ,
            int dimension,
            int radius,
            long now)
        {
            CameraChunkX = cameraChunkX;
            CameraChunkZ = cameraChunkZ;
            TargetChunkX = targetChunkX;
            TargetChunkZ = targetChunkZ;
            Dimension = dimension;
            Radius = radius;
            LastRequestMs = now;
            LastEnsureMs = now;
        }

        public int CameraChunkX { get; }

        public int CameraChunkZ { get; }

        public int? TargetChunkX { get; }

        public int? TargetChunkZ { get; }

        public IEnumerable<(int X, int Z)> Centers
        {
            get
            {
                yield return (CameraChunkX, CameraChunkZ);
                if (TargetChunkX is int targetX
                    && TargetChunkZ is int targetZ
                    && (targetX != CameraChunkX || targetZ != CameraChunkZ))
                {
                    yield return (targetX, targetZ);
                }
            }
        }

        public int Dimension { get; }

        public int Radius { get; }

        public long LastRequestMs { get; set; }

        public long LastEnsureMs { get; set; }

        public List<AnchorColumn> Columns { get; } = new();

        public HashSet<AnchorColumn> RetainedColumns { get; } = new();

        public HashSet<AnchorColumn> LoadRequestedColumns { get; } = new();

        public HashSet<AnchorColumn> CompleteColumns { get; } = new();

        public Dictionary<AnchorColumn, long> LastDispatchColumns { get; } = new();

        public HashSet<long> ProtectedChunks { get; } = new();

        public int TotalColumns { get; set; }

        public int LastCompleteColumns { get; set; } = -1;

        public int NextColumnIndex { get; set; }

        public bool EnsurePending { get; set; }

        public bool HasSameArea(AnchorState other)
            => CameraChunkX == other.CameraChunkX
                && CameraChunkZ == other.CameraChunkZ
                && TargetChunkX == other.TargetChunkX
                && TargetChunkZ == other.TargetChunkZ
                && Dimension == other.Dimension
                && Radius == other.Radius;
    }

    private readonly record struct AnchorColumn(int X, int Z);

    private readonly record struct PreloadColumn(int X, int Z, int Dimension);

    private sealed class PreloadState
    {
        public PreloadState(string replayId, HashSet<PreloadColumn> columns, long now)
        {
            ReplayId = replayId;
            Columns = columns;
            LastRequestMs = now;
            LastEnsureMs = now;
            EnsurePending = true;
        }

        public string ReplayId { get; }

        public HashSet<PreloadColumn> Columns { get; }

        public HashSet<AnchorColumn> RetainedColumns { get; } = new();

        public HashSet<PreloadColumn> LoadRequestedColumns { get; } = new();

        public HashSet<PreloadColumn> CompleteColumns { get; } = new();

        public Dictionary<PreloadColumn, long> LastDispatchColumns { get; } = new();

        public HashSet<long> ProtectedChunks { get; } = new();

        public long LastRequestMs { get; set; }

        public long LastEnsureMs { get; set; }

        public int LastCompleteColumns { get; set; } = -1;

        public int NextColumnIndex { get; set; }

        public bool EnsurePending { get; set; }
    }

    private sealed class BlockSoundRateState
    {
        public BlockSoundRateState(long windowStartMs, int count)
        {
            WindowStartMs = windowStartMs;
            Count = count;
        }

        public long WindowStartMs { get; }

        public int Count { get; set; }
    }

    private sealed class AnchorChunkRetention
    {
        private readonly ICoreServerAPI sapi;
        private readonly object? serverMain;
        private readonly MethodInfo? removeForceLoaded;
        private readonly ISet<long>? forceLoadedColumns;
        private readonly Dictionary<AnchorColumn, RetainedColumn> retained = new();
        private readonly bool canRelease;
        private bool releaseFailureLogged;

        public AnchorChunkRetention(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
            FieldInfo? serverField = FindField(sapi.WorldManager.GetType(), "server");
            serverMain = serverField?.GetValue(sapi.WorldManager);
            removeForceLoaded = serverMain?.GetType().GetMethod(
                "RemoveChunkColumnFromForceLoadedList",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                new[] { typeof(long) },
                modifiers: null);
            forceLoadedColumns = FindField(serverMain?.GetType(), "forceLoadedChunkColumns")
                ?.GetValue(serverMain) as ISet<long>;
            canRelease = serverMain is not null && removeForceLoaded is not null && forceLoadedColumns is not null;
            if (!canRelease)
            {
                sapi.Logger.Warning(
                    "VS Director: server chunk retention internals were unavailable; remote chunks will use priority loading without permanent retention.");
            }
        }

        public void Acquire(AnchorColumn column)
        {
            if (retained.TryGetValue(column, out RetainedColumn? current))
            {
                current.ReferenceCount++;
                return;
            }

            long index = sapi.WorldManager.MapChunkIndex2D(column.X, column.Z);
            bool wasAlreadyForceLoaded = canRelease && forceLoadedColumns!.Contains(index);
            sapi.WorldManager.LoadChunkColumnPriority(
                column.X,
                column.Z,
                canRelease ? new ChunkLoadOptions { KeepLoaded = true } : null);
            retained[column] = new RetainedColumn(index, wasAlreadyForceLoaded);
        }

        public void Release(AnchorColumn column)
        {
            if (!retained.TryGetValue(column, out RetainedColumn? current))
            {
                return;
            }

            if (--current.ReferenceCount > 0)
            {
                return;
            }

            retained.Remove(column);
            if (!canRelease || current.WasAlreadyForceLoaded)
            {
                return;
            }

            try
            {
                removeForceLoaded!.Invoke(serverMain, new object[] { current.MapIndex });
            }
            catch (Exception exception) when (!releaseFailureLogged)
            {
                releaseFailureLogged = true;
                sapi.Logger.Warning(
                    "VS Director: retained server chunks could not be released: {0}",
                    exception.GetBaseException().Message);
            }
        }

        private static FieldInfo? FindField(Type? type, string name)
        {
            while (type is not null)
            {
                FieldInfo? field = type.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field is not null)
                {
                    return field;
                }
                type = type.BaseType;
            }

            return null;
        }

        private sealed class RetainedColumn
        {
            public RetainedColumn(long mapIndex, bool wasAlreadyForceLoaded)
            {
                MapIndex = mapIndex;
                WasAlreadyForceLoaded = wasAlreadyForceLoaded;
            }

            public long MapIndex { get; }

            public bool WasAlreadyForceLoaded { get; }

            public int ReferenceCount { get; set; } = 1;
        }
    }
}
