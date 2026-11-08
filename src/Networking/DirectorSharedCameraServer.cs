using Vintagestory.API.Server;
using VSDirector.Data;

namespace VSDirector.Networking;

internal sealed class DirectorSharedCameraServer
{
    private const string StoreFileName = "VSDirectorSharedCameras.json";
    private readonly ICoreServerAPI sapi;
    private readonly IServerNetworkChannel channel;
    private readonly DirectorPermissionServer permissions;
    private readonly string worldId;
    private DirectorSharedCameraStoreDocument store;

    public DirectorSharedCameraServer(ICoreServerAPI sapi, DirectorPermissionServer permissions)
    {
        this.sapi = sapi;
        this.permissions = permissions;
        worldId = string.IsNullOrWhiteSpace(sapi.World.SavegameIdentifier)
            ? "default"
            : sapi.World.SavegameIdentifier;
        store = Load();
        channel = sapi.Network.GetChannel(DirectorNetwork.ChannelName)
            .SetMessageHandler<DirectorSharedCameraRequest>(OnRequest);
    }

    private void OnRequest(IServerPlayer player, DirectorSharedCameraRequest request)
    {
        if (!HasAccess(player))
        {
            SendCatalog(player, "permission");
            return;
        }

        DirectorSharedCameraAction action = Enum.IsDefined(typeof(DirectorSharedCameraAction), request.Action)
            ? (DirectorSharedCameraAction)request.Action
            : DirectorSharedCameraAction.List;
        switch (action)
        {
            case DirectorSharedCameraAction.Upsert:
                if (!TryNormalize(request.Camera, out CameraPreset? camera))
                {
                    SendCatalog(player, "invalid-camera");
                    return;
                }

                List<CameraPreset> cameras = WorldCameras();
                int existing = cameras.FindIndex(candidate => candidate.Id == camera.Id);
                CameraPreset? replaced = null;
                if (existing >= 0)
                {
                    replaced = cameras[existing];
                    camera.CreatedUtc = cameras[existing].CreatedUtc;
                    cameras[existing] = camera;
                }
                else
                {
                    cameras.Add(camera);
                }
                if (!Save())
                {
                    if (existing >= 0 && replaced is not null)
                    {
                        cameras[existing] = replaced;
                    }
                    else
                    {
                        cameras.RemoveAll(candidate => candidate.Id == camera.Id);
                    }
                    SendCatalog(player, "camera-save-failed");
                    return;
                }
                BroadcastCatalog();
                return;

            case DirectorSharedCameraAction.Delete:
                if (!Guid.TryParse(request.CameraId, out Guid cameraId))
                {
                    SendCatalog(player, "camera-not-found");
                    return;
                }
                List<CameraPreset> worldCameras = WorldCameras();
                int removedIndex = worldCameras.FindIndex(candidate => candidate.Id == cameraId);
                if (removedIndex < 0)
                {
                    SendCatalog(player, "camera-not-found");
                    return;
                }
                CameraPreset removedCamera = worldCameras[removedIndex];
                worldCameras.RemoveAt(removedIndex);
                if (!Save())
                {
                    worldCameras.Insert(removedIndex, removedCamera);
                    SendCatalog(player, "camera-save-failed");
                    return;
                }
                BroadcastCatalog();
                return;

            default:
                SendCatalog(player, string.Empty);
                return;
        }
    }

    private bool TryNormalize(DirectorSharedCameraDto source, out CameraPreset camera)
    {
        camera = new CameraPreset();
        if (source is null
            || !double.IsFinite(source.X)
            || !double.IsFinite(source.Y)
            || !double.IsFinite(source.Z)
            || !float.IsFinite(source.Yaw)
            || !float.IsFinite(source.Pitch)
            || !float.IsFinite(source.Roll)
            || !float.IsFinite(source.Fov)
            || source.Fov is < 10f or > 170f
            || Math.Abs((long)source.Dimension) > 1_000_000L)
        {
            return false;
        }

        string name = source.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return false;
        }
        if (name.Length > 80)
        {
            name = name[..80];
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        camera = new CameraPreset
        {
            Id = Guid.TryParse(source.Id, out Guid id) && id != Guid.Empty ? id : Guid.NewGuid(),
            Name = name,
            X = source.X,
            Y = source.Y,
            Z = source.Z,
            Yaw = NormalizeAngle(source.Yaw),
            Pitch = Math.Clamp(source.Pitch, -89.9f, 89.9f),
            Roll = NormalizeAngle(source.Roll),
            Fov = source.Fov,
            Dimension = source.Dimension,
            CreatedUtc = now,
            ModifiedUtc = now
        };
        return true;
    }

    private void BroadcastCatalog()
    {
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (HasAccess(player))
            {
                SendCatalog(player, string.Empty);
            }
        }
    }

    private void SendCatalog(IServerPlayer player, string error)
    {
        channel.SendPacket(new DirectorSharedCameraCatalog
        {
            Cameras = WorldCameras()
                .OrderBy(camera => camera.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(camera => camera.Id)
                .Select(ToDto)
                .ToList(),
            Error = error
        }, player);
    }

    private List<CameraPreset> WorldCameras()
    {
        if (!store.Worlds.TryGetValue(worldId, out List<CameraPreset>? cameras))
        {
            cameras = new List<CameraPreset>();
            store.Worlds[worldId] = cameras;
        }
        return cameras;
    }

    private DirectorSharedCameraStoreDocument Load()
    {
        try
        {
            DirectorSharedCameraStoreDocument loaded = sapi.LoadModConfig<DirectorSharedCameraStoreDocument>(StoreFileName)
                ?? new DirectorSharedCameraStoreDocument();
            loaded.Worlds ??= new Dictionary<string, List<CameraPreset>>(StringComparer.Ordinal);
            return loaded;
        }
        catch (Exception exception)
        {
            sapi.Logger.Warning("VS Director: failed to load shared cameras: {0}", exception.Message);
            return new DirectorSharedCameraStoreDocument();
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
            sapi.Logger.Warning("VS Director: failed to save shared cameras: {0}", exception.Message);
            return false;
        }
    }

    private bool HasAccess(IServerPlayer player)
        => permissions.Has(player, DirectorPermission.SharedCameras);

    private static DirectorSharedCameraDto ToDto(CameraPreset camera)
        => new()
        {
            Id = camera.Id.ToString("D"),
            Name = camera.Name,
            X = camera.X,
            Y = camera.Y,
            Z = camera.Z,
            Yaw = camera.Yaw,
            Pitch = camera.Pitch,
            Roll = camera.Roll,
            Fov = camera.Fov,
            Dimension = camera.Dimension,
            ModifiedUtcTicks = camera.ModifiedUtc.UtcTicks
        };

    private static float NormalizeAngle(float value)
    {
        float wrapped = value % 360f;
        return wrapped > 180f ? wrapped - 360f : wrapped < -180f ? wrapped + 360f : wrapped;
    }

    public sealed class DirectorSharedCameraStoreDocument
    {
        public Dictionary<string, List<CameraPreset>> Worlds { get; set; } = new(StringComparer.Ordinal);
    }
}
