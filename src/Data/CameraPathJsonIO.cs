using System.Text.Json;

namespace VSDirector.Data;

public static class CameraPathJsonIO
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public static string Export(CameraPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return JsonSerializer.Serialize(path.DeepClone(), Options);
    }

    public static CameraPath Import(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Path JSON is empty.");
        }

        CameraPath? path = JsonSerializer.Deserialize<CameraPath>(json, Options);
        if (path is null)
        {
            throw new InvalidDataException("Path JSON does not contain a path.");
        }
        path.Tags ??= new List<string>();
        path.Description ??= string.Empty;
        path.Keyframes ??= new List<CameraKeyframe>();
        CameraPathTiming.NormalizeTimeline(path.Keyframes);
        DirectorPathElementTimeline.Normalize(path);
        return path;
    }
}
