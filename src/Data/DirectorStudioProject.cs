using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VSDirector.Data;

public enum DirectorStudioAssetKind
{
    Model,
    Animation,
    Texture,
    Audio,
    Other
}

public sealed class DirectorStudioAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Asset";
    public string SourceName { get; set; } = string.Empty;
    public DirectorStudioAssetKind Kind { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public byte[] Content { get; set; } = Array.Empty<byte>();

    public DirectorStudioAsset DeepClone() => new()
    {
        Id = Id, Name = Name, SourceName = SourceName, Kind = Kind, Sha256 = Sha256,
        Content = (byte[])Content.Clone()
    };
}

public sealed class DirectorStudioSceneObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public string ResourceLink { get; set; } = string.Empty;
    public string Name { get; set; } = "Object";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Roll { get; set; }
    public float Scale { get; set; } = 1;
    public bool Visible { get; set; } = true;
    public List<StudioTransformKey> Keys { get; set; } = new();
    public List<StudioActionClip> Actions { get; set; } = new();

    public DirectorStudioSceneObject DeepClone()
    {
        var copy = (DirectorStudioSceneObject)MemberwiseClone();
        copy.Keys = Keys.Select(k => k.Clone()).ToList();
        copy.Actions = Actions.Select(c => c.Clone()).ToList();
        return copy;
    }
}

/// <summary>A standalone Studio document. It intentionally references no Director libraries or replay files.</summary>
public sealed class DirectorStudioProject
{
    public const int CurrentSchemaVersion = 3;
    public const int MaximumAssets = 2048;
    public const int MaximumSceneObjects = 4096;
    public const int MaximumAssetBytes = 64 * 1024 * 1024;
    public const long MaximumProjectBytes = 256L * 1024 * 1024;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Untitled Studio";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<DirectorStudioAsset> Assets { get; set; } = new();
    public List<DirectorStudioSceneObject> SceneObjects { get; set; } = new();
    public double Duration { get; set; } = 10;
    public int FramesPerSecond { get; set; } = 30;
    public List<StudioCameraKey> CameraKeys { get; set; } = new();

    public DirectorStudioProject DeepClone() => new()
    {
        SchemaVersion = SchemaVersion,
        Id = Id,
        Name = Name,
        CreatedUtc = CreatedUtc,
        ModifiedUtc = ModifiedUtc,
        Assets = Assets.Select(asset => asset.DeepClone()).ToList(),
        SceneObjects = SceneObjects.Select(item => item.DeepClone()).ToList(),
        Duration = Duration, FramesPerSecond = FramesPerSecond,
        CameraKeys = CameraKeys.Select(k => k.Clone()).ToList()
    };

    public static DirectorStudioProject Normalize(DirectorStudioProject? source)
    {
        if (source is null) throw new InvalidDataException("Studio project is empty.");
        if (source.SchemaVersion is not (1 or 2 or CurrentSchemaVersion))
            throw new NotSupportedException($"Studio project schema {source.SchemaVersion} is not supported (expected {CurrentSchemaVersion}).");
        if (source.Id == Guid.Empty) throw new InvalidDataException("Studio project ID is missing.");
        if (source.Assets is null || source.SceneObjects is null) throw new InvalidDataException("Studio project lists are missing.");
        if (source.Assets.Count > MaximumAssets || source.SceneObjects.Count > MaximumSceneObjects)
            throw new InvalidDataException("Studio project exceeds the resource or scene object limit.");
        if (source.Assets.Any(asset => asset is null) || source.SceneObjects.Any(item => item is null))
            throw new InvalidDataException("Studio project contains null entries.");
        DirectorStudioTimeline.Validate(source);
        if (source.Assets.Any(asset => asset.Id == Guid.Empty) || source.SceneObjects.Any(item => item.Id == Guid.Empty))
            throw new InvalidDataException("Studio project contains an empty entry ID.");
        if (source.Assets.Select(asset => asset.Id).Distinct().Count() != source.Assets.Count
            || source.SceneObjects.Select(item => item.Id).Distinct().Count() != source.SceneObjects.Count)
            throw new InvalidDataException("Studio project contains duplicate entry IDs.");

        DateTimeOffset created = source.CreatedUtc == default ? DateTimeOffset.UtcNow : source.CreatedUtc.ToUniversalTime();
        List<DirectorStudioAsset> assets = source.Assets.Select(asset =>
            {
                byte[] content = asset.Content ?? Array.Empty<byte>();
                if (content.Length > MaximumAssetBytes) throw new InvalidDataException($"Studio asset '{asset.SourceName}' exceeds the 64 MiB limit.");
                if (content.Length == 0) throw new InvalidDataException($"Studio asset '{asset.SourceName}' is empty.");
                string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                if (!string.Equals(asset.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Studio asset '{asset.SourceName}' failed integrity validation.");
                return new DirectorStudioAsset
                {
                    Id = asset.Id,
                    Name = Limited(asset.Name, "Asset", 96),
                    SourceName = Limited(asset.SourceName, string.Empty, 260),
                    Kind = Enum.IsDefined(asset.Kind) ? asset.Kind : DirectorStudioAssetKind.Other,
                    Sha256 = hash,
                    Content = (byte[])content.Clone()
                };
            }).ToList();
        HashSet<Guid> assetIds = assets.Select(asset => asset.Id).ToHashSet();
        if (source.SceneObjects.Any(item => string.IsNullOrWhiteSpace(item.ResourceLink)
                ? !assetIds.Contains(item.AssetId)
                : item.AssetId != Guid.Empty))
            throw new InvalidDataException("Studio scene contains a missing resource reference.");
        List<DirectorStudioSceneObject> sceneObjects = source.SceneObjects
            .Select(item => new DirectorStudioSceneObject
            {
                Id = item.Id,
                AssetId = item.AssetId,
                ResourceLink = string.IsNullOrWhiteSpace(item.ResourceLink)
                    ? string.Empty : DirectorStudioResourceLink.Parse(item.ResourceLink).ToString(),
                Name = Limited(item.Name, "Object", 96),
                X = Finite(item.X), Y = Finite(item.Y), Z = Finite(item.Z),
                Yaw = Finite(item.Yaw), Pitch = Finite(item.Pitch), Roll = Finite(item.Roll),
                Scale = float.IsFinite(item.Scale) ? Math.Clamp(item.Scale, .01f, 100f) : 1f,
                Visible = item.Visible,
                Keys = item.Keys.Select(k => k.Clone()).ToList(),
                Actions = item.Actions.Select(c => c.Clone()).ToList()
            }).ToList();

        DirectorStudioProject normalized = new()
        {
            SchemaVersion = CurrentSchemaVersion,
            Id = source.Id,
            Name = Limited(source.Name, "Untitled Studio", 96),
            CreatedUtc = created,
            ModifiedUtc = source.ModifiedUtc == default || source.ModifiedUtc < created
                ? created : source.ModifiedUtc.ToUniversalTime(),
            Assets = assets,
            SceneObjects = sceneObjects,
            Duration = source.Duration, FramesPerSecond = source.FramesPerSecond,
            CameraKeys = source.CameraKeys.Select(k => k.Clone()).ToList()
        };
        if (JsonSerializer.SerializeToUtf8Bytes(normalized, Options).LongLength > MaximumProjectBytes)
            throw new InvalidDataException("Studio project exceeds the 256 MiB limit.");
        return normalized;
    }

    public static DirectorStudioAsset ImportFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Studio asset file was not found.", fullPath);
        if (info.Length <= 0 || info.Length > MaximumAssetBytes) throw new InvalidDataException("Studio assets must be between 1 byte and 64 MiB.");
        string extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension is not (".json" or ".png" or ".jpg" or ".jpeg" or ".ogg" or ".wav" or ".vsaction"))
            throw new NotSupportedException("Studio currently imports JSON, PNG/JPEG, OGG/WAV, and .vsaction files.");
        byte[] content = File.ReadAllBytes(fullPath);
        if (content.Length > MaximumAssetBytes) throw new InvalidDataException("Studio assets are limited to 64 MiB each.");
        DirectorStudioAssetKind kind;
        if (extension == ".json")
        {
            using JsonDocument json = JsonDocument.Parse(content);
            kind = Classify(json.RootElement);
        }
        else kind = extension switch
        {
            ".png" or ".jpg" or ".jpeg" => DirectorStudioAssetKind.Texture,
            ".ogg" or ".wav" => DirectorStudioAssetKind.Audio,
            ".vsaction" => DirectorStudioAssetKind.Animation,
            _ => DirectorStudioAssetKind.Other
        };
        string sourceName = Path.GetFileName(fullPath);
        string name = Path.GetFileNameWithoutExtension(fullPath);
        return new DirectorStudioAsset
        {
            Name = Limited(name, "Asset", 96),
            SourceName = Limited(sourceName, string.Empty, 260),
            Kind = kind,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            Content = content
        };
    }

    private static DirectorStudioAssetKind Classify(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return DirectorStudioAssetKind.Other;
        bool model = root.TryGetProperty("elements", out _)
            || root.TryGetProperty("textureWidth", out _)
            || root.TryGetProperty("textures", out _);
        bool animation = root.TryGetProperty("animations", out _)
            || root.TryGetProperty("keyframes", out _)
            || root.TryGetProperty("quantityframes", out _)
            || root.TryGetProperty("layers", out _);
        return model ? DirectorStudioAssetKind.Model : animation ? DirectorStudioAssetKind.Animation : DirectorStudioAssetKind.Other;
    }

    private static string Limited(string? value, string fallback, int max)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Length == 0) text = fallback;
        return text.Length <= max ? text : text[..max];
    }

    private static double Finite(double value) => double.IsFinite(value) ? Math.Clamp(value, -1_000_000d, 1_000_000d) : 0d;
    private static float Finite(float value) => float.IsFinite(value) ? Math.Clamp(value, -100_000f, 100_000f) : 0f;

    internal static readonly JsonSerializerOptions Options = new()
    {
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };
}

public static class DirectorStudioProjectIO
{
    public const string Extension = ".vstudio";

    public static DirectorStudioProject Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Studio projects must use the .vstudio extension.");
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Studio project was not found.", fullPath);
        if (info.Length == 0) throw new InvalidDataException("Studio project is empty.");
        if (info.Length > DirectorStudioProject.MaximumProjectBytes) throw new InvalidDataException("Studio project exceeds the 256 MiB limit.");
        using FileStream stream = File.OpenRead(fullPath);
        return DirectorStudioProject.Normalize(JsonSerializer.Deserialize<DirectorStudioProject>(stream, DirectorStudioProject.Options));
    }

    public static void Save(string path, DirectorStudioProject project, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) fullPath += Extension;
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        DirectorStudioProject snapshot = DirectorStudioProject.Normalize(project);
        snapshot.ModifiedUtc = DateTimeOffset.UtcNow;
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough))
                JsonSerializer.Serialize(stream, snapshot, DirectorStudioProject.Options);
            File.Move(temporary, fullPath, overwrite);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }
}
