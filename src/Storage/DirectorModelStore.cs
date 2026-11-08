using Vintagestory.API.Client;
using VSDirector.Data;

namespace VSDirector.Storage;

internal sealed class DirectorModelStore
{
    private readonly ICoreClientAPI capi;
    private readonly string directory;

    public DirectorModelStore(ICoreClientAPI capi)
    {
        this.capi = capi;
        directory = Path.Combine(capi.GetOrCreateDataPath("ModData"), "VSDirector", "Models");
        Directory.CreateDirectory(directory);
    }

    public string DirectoryPath => directory;

    public IReadOnlyList<DirectorModelAsset> LoadAll()
    {
        List<DirectorModelAsset> result = new();
        foreach (string path in Directory.EnumerateFiles(directory)
            .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".vsmodel", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                result.AddRange(DirectorModelAssetIO.Read(path));
            }
            catch (Exception exception)
            {
                capi.Logger.Warning("VS Director: model asset {0} could not be loaded: {1}", path, exception.Message);
            }
        }
        return result;
    }

    public IReadOnlyList<DirectorModelAsset> Import(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string json = File.ReadAllText(fullPath);
        DirectorModelFileKind kind = DirectorVsmc2ShapeDocument.DetectFileKind(json);
        IReadOnlyList<DirectorModelAsset> models = kind switch
        {
            DirectorModelFileKind.VintagestoryShape => new[] { CreateShapeLink(fullPath) },
            DirectorModelFileKind.DirectorModel or DirectorModelFileKind.DirectorPackage
                => DirectorModelAssetIO.Read(fullPath),
            _ => throw new InvalidDataException("Unknown model file format.")
        };
        foreach (DirectorModelAsset model in models)
        {
            Save(model);
        }
        return models;
    }

    public DirectorVsmc2ShapeDocument InspectShape(string path)
        => DirectorVsmc2ShapeDocument.Read(path);

    public string Save(DirectorModelAsset model)
    {
        DirectorModelAsset normalized = DirectorModelNormalizer.Normalize(model);
        string path = Path.Combine(directory, FileStem(normalized) + ".vsmodel");
        DirectorModelAssetIO.Write(path, normalized);
        return path;
    }

    public bool Delete(Guid modelId)
    {
        bool deleted = false;
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            if (!Path.GetFileNameWithoutExtension(path).EndsWith(modelId.ToString("N"), StringComparison.OrdinalIgnoreCase))
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
                capi.Logger.Warning("VS Director: model asset {0} could not be deleted: {1}", path, exception.Message);
            }
        }
        return deleted;
    }

    private static string FileStem(DirectorModelAsset model)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string safe = new((model.Name ?? string.Empty).Trim()
            .Where(character => !invalid.Contains(character))
            .Take(64)
            .ToArray());
        return (safe.Length == 0 ? "Model" : safe) + "-" + model.Id.ToString("N");
    }

    private static DirectorModelAsset CreateShapeLink(string path)
    {
        DirectorVsmc2ShapeDocument shape = DirectorVsmc2ShapeDocument.Read(path);
        string name = Path.GetFileNameWithoutExtension(path).Trim();
        string code = new string(name
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .ToArray()).Trim('-');
        if (code.Length == 0)
        {
            code = shape.Sha256[..12];
        }
        return DirectorModelNormalizer.Normalize(new DirectorModelAsset
        {
            Name = name.Length == 0 ? "Shape" : name,
            ModelCode = "shape:" + code,
            ShapeSourcePath = path,
            ShapeSourceSha256 = shape.Sha256,
            ShapeSourceModifiedUtc = shape.ModifiedUtc,
            ShapeFormat = DirectorVsmc2ShapeDocument.Format,
            ShapeAnimationCodes = shape.AnimationCodes.ToList()
        });
    }
}
