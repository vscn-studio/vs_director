using Vintagestory.API.Client;
using VSDirector.Data;

namespace VSDirector.Storage;

internal sealed class DirectorActionStore
{
    private readonly ICoreClientAPI capi;
    private readonly string directory;

    public DirectorActionStore(ICoreClientAPI capi)
    {
        this.capi = capi;
        directory = Path.Combine(capi.GetOrCreateDataPath("ModData"), "VSDirector", "Actions");
        Directory.CreateDirectory(directory);
    }

    public string DirectoryPath => directory;

    public IReadOnlyList<DirectorActionAsset> LoadAll()
    {
        List<DirectorActionAsset> result = new();
        foreach (string path in Directory.EnumerateFiles(directory)
            .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".vsaction", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                result.Add(DirectorActionIO.Read(path));
            }
            catch (Exception exception)
            {
                capi.Logger.Warning("VS Director: action asset {0} could not be loaded: {1}", path, exception.Message);
            }
        }
        return result;
    }

    public string Save(DirectorActionAsset action, bool binary = false)
    {
        DirectorActionAsset normalized = DirectorActionNormalizer.Normalize(action);
        string extension = binary ? ".vsaction" : ".json";
        string path = Path.Combine(directory, FileStem(normalized) + extension);
        DirectorActionIO.Write(path, normalized);
        DeleteAlternate(normalized, extension);
        return path;
    }

    public bool Delete(Guid actionId)
    {
        bool deleted = false;
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            if (!Path.GetFileNameWithoutExtension(path).EndsWith(actionId.ToString("N"), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                File.Delete(path);
                deleted = true;
            }
            catch (IOException exception)
            {
                capi.Logger.Warning("VS Director: action asset {0} could not be deleted: {1}", path, exception.Message);
            }
        }
        return deleted;
    }

    public IReadOnlyList<DirectorActionAsset> Import(string path)
    {
        string extension = Path.GetExtension(path);
        IReadOnlyList<DirectorActionAsset> imported = extension.Equals(".vsactionpack", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? DirectorActionIO.ReadPackage(path)
                : new[] { DirectorActionIO.Read(path) };
        foreach (DirectorActionAsset action in imported)
        {
            Save(action);
        }
        return imported;
    }

    public string ExportPackage(IEnumerable<DirectorActionAsset> actions, string packageName)
    {
        string safeName = SafeName(packageName, "Action Pack");
        string path = Path.Combine(directory, safeName + ".vsactionpack");
        DirectorActionIO.WritePackage(path, actions, packageName);
        return path;
    }

    private void DeleteAlternate(DirectorActionAsset action, string retainedExtension)
    {
        string stem = FileStem(action);
        foreach (string extension in new[] { ".json", ".vsaction" })
        {
            if (extension.Equals(retainedExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try { File.Delete(Path.Combine(directory, stem + extension)); } catch (IOException) { }
        }
    }

    private static string FileStem(DirectorActionAsset action)
        => SafeName(action.Name, "Action") + "-" + action.Id.ToString("N");

    private static string SafeName(string? value, string fallback)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string safe = new((value ?? string.Empty)
            .Trim()
            .Where(character => !invalid.Contains(character))
            .Take(64)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }
}
