using Vintagestory.API.Client;
using VSDirector.Data;

namespace VSDirector.Networking;

internal sealed class DirectorAnchorClient : IDisposable
{
    private const int ChunkSize = 32;
    private const long HeartbeatIntervalMs = 1000;

    private readonly ICoreClientAPI capi;
    private readonly IClientNetworkChannel channel;
    private readonly Action<DirectorAnchorStatus> statusHandler;

    private bool anchorActive;
    private int lastChunkX = int.MinValue;
    private int lastChunkZ = int.MinValue;
    private int lastDimension = int.MinValue;
    private string lastTargetPlayerUid = string.Empty;
    private long lastTargetEntityId;
    private int lastSecondaryChunkX = int.MinValue;
    private int lastSecondaryChunkZ = int.MinValue;
    private int lastSecondaryDimension = int.MinValue;
    private int lastRequestedRadius = int.MinValue;
    private bool lastCompactWindow;
    private long lastSentMs;

    public DirectorAnchorClient(ICoreClientAPI capi, Action<DirectorAnchorStatus> statusHandler)
    {
        this.capi = capi;
        this.statusHandler = statusHandler;
        channel = capi.Network.GetChannel(DirectorNetwork.ChannelName)
            .SetMessageHandler<DirectorAnchorStatus>(OnStatus);
    }

    public bool AccessKnown { get; private set; }

    public bool AccessAllowed { get; private set; }

    public int ServerMaxRadiusChunks { get; private set; } = 1;

    public void RequestAccess()
    {
        AccessKnown = false;
        AccessAllowed = false;
        channel.SendPacket(new DirectorAnchorRequest { Active = false });
        lastSentMs = capi.ElapsedMilliseconds;
    }

    public void ResetSession()
    {
        StopAnchor();
        AccessKnown = false;
        AccessAllowed = false;
        ServerMaxRadiusChunks = 1;
        lastChunkX = int.MinValue;
        lastChunkZ = int.MinValue;
        lastDimension = int.MinValue;
        lastTargetPlayerUid = string.Empty;
        lastTargetEntityId = 0;
        lastSecondaryChunkX = int.MinValue;
        lastSecondaryChunkZ = int.MinValue;
        lastSecondaryDimension = int.MinValue;
        lastRequestedRadius = int.MinValue;
        lastCompactWindow = false;
        lastSentMs = 0;
    }

    public void Update(
        CameraPreset? pose,
        bool active,
        string? targetPlayerUid = null,
        long? targetEntityId = null,
        CameraPreset? secondaryPose = null,
        int? radiusOverrideChunks = null,
        bool compactWindow = false)
    {
        if (!active || pose is null)
        {
            StopAnchor();
            return;
        }

        if (!AccessKnown)
        {
            if (capi.ElapsedMilliseconds - lastSentMs >= HeartbeatIntervalMs)
            {
                RequestAccess();
                lastSentMs = capi.ElapsedMilliseconds;
            }

            return;
        }

        if (!AccessAllowed)
        {
            return;
        }

        int chunkX = FloorDiv(pose.X, ChunkSize);
        int chunkZ = FloorDiv(pose.Z, ChunkSize);
        string playerUid = targetPlayerUid?.Trim() ?? string.Empty;
        long entityId = targetEntityId.GetValueOrDefault();
        int secondaryChunkX = secondaryPose is null ? int.MinValue : FloorDiv(secondaryPose.X, ChunkSize);
        int secondaryChunkZ = secondaryPose is null ? int.MinValue : FloorDiv(secondaryPose.Z, ChunkSize);
        int secondaryDimension = secondaryPose?.Dimension ?? int.MinValue;
        int requestedRadius;
        if (radiusOverrideChunks is int overrideRadius)
        {
            requestedRadius = Math.Clamp(
                overrideRadius,
                compactWindow ? 0 : 1,
                Math.Max(1, ServerMaxRadiusChunks));
        }
        else
        {
            int viewDistance = capi.World.Player?.WorldData.LastApprovedViewDistance ?? ChunkSize;
            requestedRadius = Math.Clamp(
                (viewDistance + ChunkSize - 1) / ChunkSize,
                1,
                Math.Max(1, ServerMaxRadiusChunks));
        }
        long now = capi.ElapsedMilliseconds;
        bool moved = chunkX != lastChunkX
            || chunkZ != lastChunkZ
            || pose.Dimension != lastDimension
            || !string.Equals(playerUid, lastTargetPlayerUid, StringComparison.Ordinal)
            || entityId != lastTargetEntityId
            || secondaryChunkX != lastSecondaryChunkX
            || secondaryChunkZ != lastSecondaryChunkZ
            || secondaryDimension != lastSecondaryDimension
            || requestedRadius != lastRequestedRadius
            || compactWindow != lastCompactWindow;
        if (!moved && now - lastSentMs < HeartbeatIntervalMs)
        {
            return;
        }
        channel.SendPacket(new DirectorAnchorRequest
        {
            Active = true,
            X = pose.X,
            Z = pose.Z,
            Dimension = pose.Dimension,
            RadiusChunks = requestedRadius,
            TargetPlayerUid = playerUid,
            TargetEntityId = entityId,
            HasSecondaryAnchor = secondaryPose is not null,
            SecondaryX = secondaryPose?.X ?? 0d,
            SecondaryZ = secondaryPose?.Z ?? 0d,
            SecondaryDimension = secondaryPose?.Dimension ?? 0,
            CompactWindow = compactWindow
        });
        if (moved)
        {
            capi.Logger.Debug(
                "VS Director: requesting remote chunks at ({0},{1},d{2}), radius {3}{4}.",
                chunkX,
                chunkZ,
                pose.Dimension,
                requestedRadius,
                secondaryPose is null
                    ? string.Empty
                    : $" and ({secondaryChunkX},{secondaryChunkZ},d{secondaryDimension})");
        }
        anchorActive = true;
        lastChunkX = chunkX;
        lastChunkZ = chunkZ;
        lastDimension = pose.Dimension;
        lastTargetPlayerUid = playerUid;
        lastTargetEntityId = entityId;
        lastSecondaryChunkX = secondaryChunkX;
        lastSecondaryChunkZ = secondaryChunkZ;
        lastSecondaryDimension = secondaryDimension;
        lastRequestedRadius = requestedRadius;
        lastCompactWindow = compactWindow;
        lastSentMs = now;
    }

    public void StopAnchor()
    {
        if (!anchorActive)
        {
            return;
        }

        channel.SendPacket(new DirectorAnchorRequest { Active = false });
        anchorActive = false;
        lastChunkX = int.MinValue;
        lastChunkZ = int.MinValue;
        lastDimension = int.MinValue;
        lastTargetPlayerUid = string.Empty;
        lastTargetEntityId = 0;
        lastSecondaryChunkX = int.MinValue;
        lastSecondaryChunkZ = int.MinValue;
        lastSecondaryDimension = int.MinValue;
        lastRequestedRadius = int.MinValue;
        lastCompactWindow = false;
        lastSentMs = capi.ElapsedMilliseconds;
    }

    public void Dispose()
    {
        StopAnchor();
    }

    private void OnStatus(DirectorAnchorStatus status)
    {
        AccessKnown = true;
        AccessAllowed = status.Allowed || status.MessageCode == "invalid-anchor";
        ServerMaxRadiusChunks = Math.Max(1, status.MaxRadiusChunks);
        if (!AccessAllowed)
        {
            anchorActive = false;
        }

        statusHandler(status);
    }

    private static int FloorDiv(double value, int divisor)
        => (int)Math.Floor(value / divisor);
}
