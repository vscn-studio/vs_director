using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VSDirector.Data;

namespace VSDirector.Storage;

internal sealed class WorldCameraStore
{
    private readonly ICoreClientAPI capi;
    private readonly string worldId;
    private readonly string filePath;

    public WorldCameraStore(ICoreClientAPI capi, string worldId)
    {
        this.capi = capi;
        this.worldId = worldId;

        string root = capi.GetOrCreateDataPath("ModData");
        string directory = Path.Combine(root, "VSDirector", "Worlds");
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, BuildFileName(worldId));
    }

    public CameraLibraryDocument Load()
    {
        if (!File.Exists(filePath))
        {
            return new CameraLibraryDocument();
        }

        return CameraDocumentIO.Load(filePath);
    }

    public bool Save(CameraLibrary library)
    {
        try
        {
            CameraDocumentIO.Save(filePath, library.Document);
            return true;
        }
        catch (Exception ex)
        {
            capi.Logger.Error("VS Director: could not save camera library for world {0}: {1}", worldId, ex.Message);
            return false;
        }
    }

    private static string BuildFileName(string value)
    {
        string readable = new(value.Where(char.IsLetterOrDigit).Take(24).ToArray());
        if (readable.Length == 0)
        {
            readable = "world";
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        string hash = Convert.ToHexString(digest)[..16].ToLowerInvariant();
        return $"{readable}-{hash}.json";
    }
}
