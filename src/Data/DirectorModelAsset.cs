namespace VSDirector.Data;

public sealed class DirectorModelAsset
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ModelCode { get; set; } = string.Empty;

    public string EntityCode { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string SourceModId { get; set; } = string.Empty;

    public float Size { get; set; } = 1f;

    public bool Favorite { get; set; }

    public string ShapeAssetLocation { get; set; } = string.Empty;

    public string ShapeSourcePath { get; set; } = string.Empty;

    public string ShapeSourceSha256 { get; set; } = string.Empty;

    public DateTimeOffset? ShapeSourceModifiedUtc { get; set; }

    public string ShapeFormat { get; set; } = string.Empty;

    public List<string> ShapeAnimationCodes { get; set; } = new();

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }

    public DirectorModelAsset DeepClone()
    {
        DirectorModelAsset clone = (DirectorModelAsset)MemberwiseClone();
        clone.ShapeAnimationCodes = ShapeAnimationCodes?.ToList() ?? new List<string>();
        return clone;
    }
}

public static class DirectorModelNormalizer
{
    public static DirectorModelAsset Normalize(DirectorModelAsset? source)
    {
        source ??= new DirectorModelAsset();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset created = source.CreatedUtc == default ? now : source.CreatedUtc.ToUniversalTime();
        DateTimeOffset modified = source.ModifiedUtc == default ? created : source.ModifiedUtc.ToUniversalTime();
        string modelCode = Limited(source.ModelCode, string.Empty, 192);
        string entityCode = Limited(source.EntityCode, string.Empty, 192);
        if (entityCode.Length == 0 && modelCode.StartsWith("entity/", StringComparison.OrdinalIgnoreCase))
        {
            entityCode = modelCode["entity/".Length..];
        }
        if (entityCode.Length == 0 && modelCode.Length > 0)
        {
            entityCode = "game:player";
        }
        string shapeAssetLocation = Limited(source.ShapeAssetLocation, string.Empty, 256);
        string shapeSourcePath = Limited(source.ShapeSourcePath, string.Empty, 2048);
        string shapeSourceSha256 = NormalizeSha256(source.ShapeSourceSha256);
        bool hasShapeLink = shapeAssetLocation.Length > 0 || shapeSourcePath.Length > 0;
        return new DirectorModelAsset
        {
            Id = source.Id == Guid.Empty ? Guid.NewGuid() : source.Id,
            Name = Limited(source.Name, "Model", 96),
            ModelCode = modelCode,
            EntityCode = entityCode,
            Group = Limited(source.Group, string.Empty, 96),
            Description = Limited(source.Description, string.Empty, 2048),
            SourceModId = Limited(source.SourceModId, string.Empty, 96),
            Size = float.IsFinite(source.Size) ? Math.Clamp(source.Size, 0.1f, 10f) : 1f,
            Favorite = source.Favorite,
            ShapeAssetLocation = shapeAssetLocation,
            ShapeSourcePath = shapeSourcePath,
            ShapeSourceSha256 = shapeSourceSha256,
            ShapeSourceModifiedUtc = source.ShapeSourceModifiedUtc?.ToUniversalTime(),
            ShapeFormat = hasShapeLink ? "vintagestory-shape" : string.Empty,
            ShapeAnimationCodes = (source.ShapeAnimationCodes ?? new List<string>())
                .Select(code => Limited(code, string.Empty, 128))
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .Take(256)
                .ToList(),
            CreatedUtc = created,
            ModifiedUtc = modified < created ? created : modified
        };
    }

    private static string NormalizeSha256(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : string.Empty;
    }

    private static string Limited(string? value, string fallback, int maximumLength)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}
