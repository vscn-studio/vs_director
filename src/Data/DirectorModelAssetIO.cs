using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VSDirector.Data;

public sealed class DirectorModelPackage
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Name { get; set; } = string.Empty;

    public List<DirectorModelAsset> Models { get; set; } = new();
}

public static class DirectorModelAssetIO
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IReadOnlyList<DirectorModelAsset> Read(string path)
    {
        string json = File.ReadAllText(path, Encoding.UTF8);
        using JsonDocument document = JsonDocument.Parse(json);
        DirectorModelFileKind kind = DirectorVsmc2ShapeDocument.DetectFileKind(document.RootElement);
        if (kind == DirectorModelFileKind.VintagestoryShape)
        {
            throw new DirectorShapeFileException("The selected JSON is a Vintage Story Shape, not Director model metadata.");
        }
        if (kind == DirectorModelFileKind.DirectorPackage)
        {
            DirectorModelPackage package = JsonSerializer.Deserialize<DirectorModelPackage>(json, Options)
                ?? throw new JsonException("Model package is empty.");
            if (package.SchemaVersion > DirectorModelPackage.CurrentSchemaVersion)
            {
                throw new NotSupportedException(
                    $"Model package schema {package.SchemaVersion} is newer than supported schema {DirectorModelPackage.CurrentSchemaVersion}.");
            }
            return (package.Models ?? new List<DirectorModelAsset>())
                .Where(model => model is not null)
                .Select(DirectorModelNormalizer.Normalize)
                .ToArray();
        }
        if (kind != DirectorModelFileKind.DirectorModel)
        {
            throw new JsonException("Unknown model file format.");
        }
        DirectorModelAsset model = JsonSerializer.Deserialize<DirectorModelAsset>(json, Options)
            ?? throw new JsonException("Model asset is empty.");
        return new[] { DirectorModelNormalizer.Normalize(model) };
    }

    public static void Write(string path, DirectorModelAsset model)
    {
        ArgumentNullException.ThrowIfNull(model);
        WriteJson(path, DirectorModelNormalizer.Normalize(model));
    }

    public static void WritePackage(string path, IEnumerable<DirectorModelAsset> models, string name)
    {
        ArgumentNullException.ThrowIfNull(models);
        WriteJson(path, new DirectorModelPackage
        {
            Name = name?.Trim() ?? string.Empty,
            Models = models.Where(model => model is not null)
                .Select(PortableClone)
                .ToList()
        });
    }

    private static DirectorModelAsset PortableClone(DirectorModelAsset model)
    {
        DirectorModelAsset portable = DirectorModelNormalizer.Normalize(model);
        portable.ShapeSourcePath = string.Empty;
        portable.ShapeSourceSha256 = string.Empty;
        portable.ShapeSourceModifiedUtc = null;
        return portable;
    }

    private static void WriteJson<T>(string path, T value)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch (IOException) { }
        }
    }
}
