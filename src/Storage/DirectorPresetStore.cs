using Vintagestory.API.Client;
using VSDirector.Data;

namespace VSDirector.Storage;

internal sealed class DirectorPresetStore
{
    private readonly ICoreClientAPI capi;
    private readonly string directory;

    public DirectorPresetStore(ICoreClientAPI capi)
    {
        this.capi = capi;
        string root = capi.GetOrCreateDataPath("ModData");
        directory = Path.Combine(root, "VSDirector", "Presets");
        Directory.CreateDirectory(directory);
    }

    public string DirectoryPath => directory;

    public string Export(string requestedName, CameraLibraryDocument document)
    {
        string fileName = NormalizeFileName(requestedName, ".json");
        string path = Path.Combine(directory, fileName);
        DirectorPresetPackageIO.Save(path, document, Path.GetFileNameWithoutExtension(fileName));
        return path;
    }

    public (DirectorPresetPackage Package, string Path) Import(string selector)
    {
        string path = ResolveExisting(selector);
        return (DirectorPresetPackageIO.Load(path), path);
    }

    public IReadOnlyList<string> List()
    {
        try
        {
            return Directory.EnumerateFiles(directory)
                .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToArray();
        }
        catch (Exception exception)
        {
            capi.Logger.Warning("VS Director: could not enumerate preset packages: {0}", exception.Message);
            return Array.Empty<string>();
        }
    }

    private string ResolveExisting(string selector)
    {
        string requested = selector?.Trim() ?? string.Empty;
        if (requested.Length == 0)
        {
            throw new ArgumentException("A preset file name is required.", nameof(selector));
        }

        string normalized = NormalizeFileName(requested, string.Empty);
        string[] candidates = Path.HasExtension(normalized)
            ? new[] { normalized }
            : new[] { normalized + ".json", normalized + ".txt" };
        foreach (string candidate in candidates)
        {
            string path = Path.Combine(directory, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("The preset package was not found in the VS Director preset folder.", requested);
    }

    private static string NormalizeFileName(string requestedName, string defaultExtension)
    {
        string raw = Path.GetFileName(requestedName?.Trim() ?? string.Empty);
        string extension = Path.GetExtension(raw);
        string stem = Path.GetFileNameWithoutExtension(raw);
        if (stem.Length == 0)
        {
            stem = "DirectorPreset";
        }

        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string safeStem = new(stem
            .Where(character => !invalid.Contains(character) && !char.IsControl(character))
            .Take(96)
            .ToArray());
        safeStem = safeStem.Trim().TrimEnd('.');
        if (safeStem.Length == 0)
        {
            safeStem = "DirectorPreset";
        }

        string safeExtension = string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase)
            ? ".txt"
            : string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
                ? ".json"
                : defaultExtension;
        return safeStem + safeExtension;
    }
}
