using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VSDirector.Data;

public enum DirectorModelFileKind
{
    Unknown,
    DirectorModel,
    DirectorPackage,
    VintagestoryShape
}

public sealed class DirectorShapeFileException : Exception
{
    public DirectorShapeFileException(string message) : base(message)
    {
    }
}

public sealed class DirectorVsmc2ShapeDocument
{
    public const string Format = "vintagestory-shape";

    public string FilePath { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public DateTimeOffset ModifiedUtc { get; init; }

    public int ElementCount { get; init; }

    public int TextureCount { get; init; }

    public IReadOnlyList<string> AnimationCodes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<DirectorShapeDiagnostic> Diagnostics { get; init; }
        = Array.Empty<DirectorShapeDiagnostic>();

    public bool IsValid => Diagnostics.All(diagnostic => diagnostic.Severity != DirectorShapeDiagnosticSeverity.Error);

    public static DirectorModelFileKind DetectFileKind(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return DetectFileKind(document.RootElement);
    }

    public static DirectorModelFileKind DetectFileKind(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return DirectorModelFileKind.Unknown;
        }
        if (HasProperty(root, "elements") || HasProperty(root, "textures") || HasProperty(root, "animations"))
        {
            return DirectorModelFileKind.VintagestoryShape;
        }
        if (TryGetProperty(root, "models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
        {
            return DirectorModelFileKind.DirectorPackage;
        }
        if (HasProperty(root, "modelCode") || HasProperty(root, "entityCode")
            || (HasProperty(root, "id") && HasProperty(root, "name")))
        {
            return DirectorModelFileKind.DirectorModel;
        }
        return DirectorModelFileKind.Unknown;
    }

    public static DirectorVsmc2ShapeDocument Parse(string json, string filePath = "")
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        if (DetectFileKind(document.RootElement) != DirectorModelFileKind.VintagestoryShape)
        {
            throw new DirectorShapeFileException("The selected file is not a Vintage Story Shape JSON document.");
        }
        DirectorVsmc2ShapeValidationResult validation = DirectorVsmc2ShapeValidator.Validate(document.RootElement, filePath);
        return new DirectorVsmc2ShapeDocument
        {
            FilePath = filePath,
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),
            ModifiedUtc = DateTimeOffset.UtcNow,
            ElementCount = validation.ElementCount,
            TextureCount = validation.TextureCount,
            AnimationCodes = validation.AnimationCodes,
            Diagnostics = validation.Diagnostics
        };
    }

    public static DirectorVsmc2ShapeDocument Read(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(fullPath);
        int offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        string json = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        DirectorVsmc2ShapeDocument parsed = Parse(json, fullPath);
        return new DirectorVsmc2ShapeDocument
        {
            FilePath = parsed.FilePath,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ModifiedUtc = File.GetLastWriteTimeUtc(fullPath),
            ElementCount = parsed.ElementCount,
            TextureCount = parsed.TextureCount,
            AnimationCodes = parsed.AnimationCodes,
            Diagnostics = parsed.Diagnostics
        };
    }

    internal static bool HasProperty(JsonElement element, string name)
        => TryGetProperty(element, name, out _);

    internal static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
