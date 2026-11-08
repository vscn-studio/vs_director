using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vintagestory.API.Client;
using VSDirector.Data;

namespace VSDirector.Storage;

internal sealed class DirectorWorkspaceStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private readonly ICoreClientAPI capi;
    private readonly string worldId;
    private readonly string filePath;

    public DirectorWorkspaceStore(ICoreClientAPI capi, string worldId)
    {
        this.capi = capi;
        this.worldId = worldId;
        string directory = Path.Combine(capi.GetOrCreateDataPath("ModData"), "VSDirector", "Workspace");
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, BuildFileName(worldId));
    }

    public DirectorWorkspaceDocument Load()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new DirectorWorkspaceDocument();
            }

            using FileStream stream = File.OpenRead(filePath);
            DirectorWorkspaceDocument? document = JsonSerializer.Deserialize<DirectorWorkspaceDocument>(stream, JsonOptions);
            return DirectorWorkspaceDocument.Normalize(document);
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "VS Director: could not load workspace layout for world {0}: {1}",
                worldId,
                exception.Message);
            return new DirectorWorkspaceDocument();
        }
    }

    public bool Save(DirectorWorkspaceDocument document)
    {
        try
        {
            DirectorWorkspaceDocument snapshot = DirectorWorkspaceDocument.Normalize(document);
            string temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                }

                File.Move(temporary, filePath, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "VS Director: could not save workspace layout for world {0}: {1}",
                worldId,
                exception.Message);
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

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16]
            .ToLowerInvariant();
        return $"{readable}-{hash}.workspace.json";
    }

    public void Dispose()
    {
    }
}
