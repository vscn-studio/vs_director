using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VSDirector.Data;

public sealed class DirectorWorkflowMultiViewManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string CaptureMode { get; set; } = "live-world-offscreen-multiview";

    public string Status { get; set; } = "prepared";

    public Guid WorkflowId { get; set; }

    public string WorkflowName { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartedUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CompletedUtc { get; set; }

    public double TimelineStartSeconds { get; set; }

    public double TimelineEndSeconds { get; set; }

    public double LastTimelineSeconds { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int FramesPerSecond { get; set; }

    public bool ReplayStartedByCapture { get; set; }

    public bool AdvancesWorldTicks { get; set; }

    public bool RenderLocalPlayer { get; set; }

    public string LocalPlayerRenderMode { get; set; } = string.Empty;

    public List<string> Capabilities { get; set; } = new();

    public List<string> Limitations { get; set; } = new();

    public List<DirectorWorkflowMultiViewManifestPass> Passes { get; set; } = new();

    public List<DirectorWorkflowMultiViewManifestError> Errors { get; set; } = new();
}

public sealed class DirectorWorkflowMultiViewManifestPass
{
    public Guid PassId { get; set; }

    public Guid ViewId { get; set; }

    public string Name { get; set; } = string.Empty;

    public double StartSeconds { get; set; }

    public double EndSeconds { get; set; }

    public bool SplitForOverlap { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string Encoder { get; set; } = string.Empty;

    public string Codec { get; set; } = string.Empty;

    public long EncodedFrames { get; set; }

    public long DroppedFrames { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Success { get; set; }

    public string Error { get; set; } = string.Empty;

    public List<DirectorWorkflowMultiViewManifestSegment> Segments { get; set; } = new();
}

public sealed class DirectorWorkflowMultiViewManifestSegment
{
    public Guid CardId { get; set; }

    public string CardName { get; set; } = string.Empty;

    public DirectorRecordingSourceType SourceType { get; set; }

    public Guid SourceId { get; set; }

    public double StartSeconds { get; set; }

    public double EndSeconds { get; set; }
}

public sealed class DirectorWorkflowMultiViewManifestError
{
    public string Code { get; set; } = string.Empty;

    public Guid PassId { get; set; }

    public string PassName { get; set; } = string.Empty;

    public Guid CardId { get; set; }

    public DirectorRecordingSourceType SourceType { get; set; }

    public Guid SourceId { get; set; }

    public string Detail { get; set; } = string.Empty;
}

public static class DirectorWorkflowMultiViewManifestIO
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static DirectorWorkflowMultiViewManifest Load(string path)
    {
        using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        DirectorWorkflowMultiViewManifest? manifest = JsonSerializer.Deserialize<DirectorWorkflowMultiViewManifest>(
            stream,
            SerializerOptions);
        if (manifest is null || manifest.SchemaVersion != DirectorWorkflowMultiViewManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException("Unsupported or empty workflow multi-view manifest.");
        }
        return manifest;
    }

    public static void SaveAtomic(string path, DirectorWorkflowMultiViewManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("A workflow multi-view manifest requires a parent directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65536,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, manifest, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
