using Vintagestory.API.Client;
using VSDirector.Data;

namespace VSDirector.Networking;

internal sealed class DirectorSharedCameraClient
{
    private readonly IClientNetworkChannel channel;
    private readonly Action<string> errorHandler;
    private List<CameraPreset> cameras = new();

    public DirectorSharedCameraClient(ICoreClientAPI capi, Action<string> errorHandler)
    {
        this.errorHandler = errorHandler;
        channel = capi.Network.GetChannel(DirectorNetwork.ChannelName)
            .SetMessageHandler<DirectorSharedCameraCatalog>(OnCatalog);
    }

    public IReadOnlyList<CameraPreset> Cameras
        => cameras.Select(camera => camera.DeepClone()).ToArray();

    public event Action? CatalogChanged;

    public void RequestList()
    {
        channel.SendPacket(new DirectorSharedCameraRequest
        {
            Action = (int)DirectorSharedCameraAction.List
        });
    }

    public void Publish(CameraPreset camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        channel.SendPacket(new DirectorSharedCameraRequest
        {
            Action = (int)DirectorSharedCameraAction.Upsert,
            Camera = ToDto(camera)
        });
    }

    public void Delete(Guid cameraId)
    {
        channel.SendPacket(new DirectorSharedCameraRequest
        {
            Action = (int)DirectorSharedCameraAction.Delete,
            CameraId = cameraId.ToString("D")
        });
    }

    public void Reset()
    {
        cameras.Clear();
        CatalogChanged?.Invoke();
    }

    private void OnCatalog(DirectorSharedCameraCatalog catalog)
    {
        cameras = (catalog.Cameras ?? new List<DirectorSharedCameraDto>())
            .Select(FromDto)
            .Where(camera => camera is not null)
            .Cast<CameraPreset>()
            .OrderBy(camera => camera.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(camera => camera.Id)
            .ToList();
        if (!string.IsNullOrWhiteSpace(catalog.Error))
        {
            errorHandler(catalog.Error);
        }
        CatalogChanged?.Invoke();
    }

    private static CameraPreset? FromDto(DirectorSharedCameraDto source)
    {
        if (source is null
            || !Guid.TryParse(source.Id, out Guid id)
            || id == Guid.Empty
            || string.IsNullOrWhiteSpace(source.Name)
            || !double.IsFinite(source.X)
            || !double.IsFinite(source.Y)
            || !double.IsFinite(source.Z)
            || !float.IsFinite(source.Yaw)
            || !float.IsFinite(source.Pitch)
            || !float.IsFinite(source.Roll)
            || !float.IsFinite(source.Fov))
        {
            return null;
        }

        DateTimeOffset modified;
        try
        {
            modified = source.ModifiedUtcTicks > 0
                ? new DateTimeOffset(source.ModifiedUtcTicks, TimeSpan.Zero)
                : DateTimeOffset.UtcNow;
        }
        catch (ArgumentOutOfRangeException)
        {
            modified = DateTimeOffset.UtcNow;
        }
        return new CameraPreset
        {
            Id = id,
            Name = source.Name.Trim(),
            X = source.X,
            Y = source.Y,
            Z = source.Z,
            Yaw = source.Yaw,
            Pitch = source.Pitch,
            Roll = source.Roll,
            Fov = Math.Clamp(source.Fov, 10f, 170f),
            Dimension = source.Dimension,
            CreatedUtc = modified,
            ModifiedUtc = modified
        };
    }

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
}
