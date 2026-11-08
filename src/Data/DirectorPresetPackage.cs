using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VSDirector.Data;

/// <summary>
/// A portable, human-readable bundle of director assets. World recording
/// tracks are deliberately excluded because they refer to local output and
/// worker assignments rather than shareable camera content.
/// </summary>
public sealed class DirectorPresetPackage
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Name { get; set; } = "VS Director Preset";

    public DateTimeOffset ExportedUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<CameraPreset> Cameras { get; set; } = new();

    public List<CameraPath> Paths { get; set; } = new();

    public List<CameraSequence> Sequences { get; set; } = new();

    public List<DirectorWorkflow> Workflows { get; set; } = new();

    public List<CameraFollowRig> FollowRigs { get; set; } = new();

    public DirectorPresetPackage DeepClone()
    {
        return new DirectorPresetPackage
        {
            SchemaVersion = SchemaVersion,
            Name = Name,
            ExportedUtc = ExportedUtc,
            Cameras = Cameras is null
                ? new List<CameraPreset>()
                : Cameras.Where(camera => camera is not null).Select(camera => camera.DeepClone()).ToList(),
            Paths = Paths is null
                ? new List<CameraPath>()
                : Paths.Where(path => path is not null).Select(path => path.DeepClone()).ToList(),
            Sequences = Sequences is null
                ? new List<CameraSequence>()
                : Sequences.Where(sequence => sequence is not null).Select(sequence => sequence.DeepClone()).ToList(),
            Workflows = Workflows is null
                ? new List<DirectorWorkflow>()
                : Workflows.Where(workflow => workflow is not null).Select(workflow => workflow.DeepClone()).ToList(),
            FollowRigs = FollowRigs is null
                ? new List<CameraFollowRig>()
                : FollowRigs.Where(rig => rig is not null).Select(rig => rig.DeepClone()).ToList()
        };
    }

    public static DirectorPresetPackage FromDocument(CameraLibraryDocument document, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new DirectorPresetPackage
        {
            Name = string.IsNullOrWhiteSpace(name) ? "VS Director Preset" : name.Trim(),
            ExportedUtc = DateTimeOffset.UtcNow,
            Cameras = document.Cameras is null
                ? new List<CameraPreset>()
                : document.Cameras.Where(camera => camera is not null).Select(camera => camera.DeepClone()).ToList(),
            Paths = document.Paths is null
                ? new List<CameraPath>()
                : document.Paths.Where(path => path is not null).Select(path => path.DeepClone()).ToList(),
            Sequences = document.Sequences is null
                ? new List<CameraSequence>()
                : document.Sequences.Where(sequence => sequence is not null).Select(sequence => sequence.DeepClone()).ToList(),
            Workflows = document.Workflows is null
                ? new List<DirectorWorkflow>()
                : document.Workflows.Where(workflow => workflow is not null).Select(workflow => workflow.DeepClone()).ToList(),
            FollowRigs = document.FollowRigs is null
                ? new List<CameraFollowRig>()
                : document.FollowRigs.Where(rig => rig is not null).Select(rig => rig.DeepClone()).ToList()
        };
    }

    public CameraLibraryDocument ToDocument()
    {
        return new CameraLibraryDocument
        {
            SchemaVersion = CameraLibraryDocument.CurrentSchemaVersion,
            Cameras = Cameras?.Where(camera => camera is not null).Select(camera => camera.DeepClone()).ToList()
                ?? new List<CameraPreset>(),
            Paths = Paths?.Where(path => path is not null).Select(path => path.DeepClone()).ToList()
                ?? new List<CameraPath>(),
            Sequences = Sequences?.Where(sequence => sequence is not null).Select(sequence => sequence.DeepClone()).ToList()
                ?? new List<CameraSequence>(),
            Workflows = Workflows?.Where(workflow => workflow is not null).Select(workflow => workflow.DeepClone()).ToList()
                ?? new List<DirectorWorkflow>(),
            FollowRigs = FollowRigs?.Where(rig => rig is not null).Select(rig => rig.DeepClone()).ToList()
                ?? new List<CameraFollowRig>()
        };
    }
}

public sealed class DirectorPresetImportResult
{
    public DirectorPresetImportResult(
        CameraLibraryDocument document,
        int cameras,
        int paths,
        int sequences,
        int followRigs,
        int workflows = 0)
    {
        Document = document;
        Cameras = cameras;
        Paths = paths;
        Sequences = sequences;
        FollowRigs = followRigs;
        Workflows = workflows;
    }

    public CameraLibraryDocument Document { get; }

    public int Cameras { get; }

    public int Paths { get; }

    public int Sequences { get; }

    public int FollowRigs { get; }

    public int Workflows { get; }
}

/// <summary>
/// JSON and text import/export for portable director bundles. Text files use
/// the same UTF-8 JSON representation so they remain diffable and lossless.
/// </summary>
public static class DirectorPresetPackageIO
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string ExportText(CameraLibraryDocument document, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        DirectorPresetPackage package = DirectorPresetPackage.FromDocument(
            CameraLibrary.Normalize(document),
            name);
        return JsonSerializer.Serialize(package, SerializerOptions);
    }

    public static DirectorPresetPackage ParseText(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        DirectorPresetPackage? package = JsonSerializer.Deserialize<DirectorPresetPackage>(text, SerializerOptions);
        if (package is null)
        {
            throw new InvalidDataException("The preset text did not contain a JSON package.");
        }

        if (package.SchemaVersion > DirectorPresetPackage.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Preset schema {package.SchemaVersion} is newer than supported schema {DirectorPresetPackage.CurrentSchemaVersion}.");
        }

        package.SchemaVersion = DirectorPresetPackage.CurrentSchemaVersion;
        package.Name = string.IsNullOrWhiteSpace(package.Name) ? "VS Director Preset" : package.Name.Trim();
        package.ExportedUtc = package.ExportedUtc == default
            ? DateTimeOffset.UtcNow
            : package.ExportedUtc.ToUniversalTime();
        return package;
    }

    public static void Save(string path, CameraLibraryDocument document, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The preset path must include a file name.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, ExportText(document, name), new System.Text.UTF8Encoding(false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static DirectorPresetPackage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ParseText(File.ReadAllText(Path.GetFullPath(path), System.Text.Encoding.UTF8));
    }

    /// <summary>
    /// Merges a package into a world document. Every imported object receives a
    /// new id; sequence shot references are remapped to the imported cameras.
    /// Quick slots are intentionally cleared to avoid silently stealing a local
    /// operator's Alt+number bindings.
    /// </summary>
    public static DirectorPresetImportResult Merge(
        CameraLibraryDocument destination,
        DirectorPresetPackage package)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(package);
        if (package.SchemaVersion > DirectorPresetPackage.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Preset schema {package.SchemaVersion} is newer than supported schema {DirectorPresetPackage.CurrentSchemaVersion}.");
        }

        CameraLibrary library = new(destination);
        Dictionary<Guid, Guid> cameraIds = new();
        int importedCameras = 0;
        foreach (CameraPreset? source in package.Cameras ?? new List<CameraPreset>())
        {
            if (source is null)
            {
                continue;
            }

            CameraPreset clone = source.DeepClone();
            Guid oldId = clone.Id;
            clone.Id = Guid.NewGuid();
            clone.QuickSlot = null;
            CameraPreset added = library.Add(clone);
            if (oldId != Guid.Empty && !cameraIds.ContainsKey(oldId))
            {
                cameraIds[oldId] = added.Id;
            }

            importedCameras++;
        }

        Dictionary<Guid, Guid> pathIds = new();
        int importedPaths = 0;
        foreach (CameraPath? source in package.Paths ?? new List<CameraPath>())
        {
            if (source is null)
            {
                continue;
            }

            CameraPath clone = source.DeepClone();
            Guid oldId = clone.Id;
            clone.Id = Guid.NewGuid();
            CameraPath added = library.AddPath(clone);
            if (oldId != Guid.Empty && !pathIds.ContainsKey(oldId))
            {
                pathIds[oldId] = added.Id;
            }
            importedPaths++;
        }

        Dictionary<Guid, Guid> sequenceIds = new();
        int importedSequences = 0;
        foreach (CameraSequence? source in package.Sequences ?? new List<CameraSequence>())
        {
            if (source is null)
            {
                continue;
            }

            CameraSequence clone = source.DeepClone();
            Guid oldId = clone.Id;
            clone.Id = Guid.NewGuid();
            clone.Shots = (clone.Shots ?? new List<CameraSequenceShot>())
                .Where(shot => shot is not null && cameraIds.ContainsKey(shot.CameraId))
                .Select(shot => new CameraSequenceShot
                {
                    CameraId = cameraIds[shot.CameraId],
                    DurationSeconds = shot.DurationSeconds
                })
                .ToList();
            CameraSequence added = library.AddSequence(clone);
            if (oldId != Guid.Empty && !sequenceIds.ContainsKey(oldId))
            {
                sequenceIds[oldId] = added.Id;
            }
            importedSequences++;
        }

        Dictionary<Guid, Guid> followIds = new();
        int importedFollowRigs = 0;
        foreach (CameraFollowRig? source in package.FollowRigs ?? new List<CameraFollowRig>())
        {
            if (source is null)
            {
                continue;
            }

            CameraFollowRig clone = source.DeepClone();
            Guid oldId = clone.Id;
            clone.Id = Guid.NewGuid();
            clone.QuickSlot = null;
            CameraFollowRig added = library.AddFollowRig(clone);
            if (oldId != Guid.Empty && !followIds.ContainsKey(oldId))
            {
                followIds[oldId] = added.Id;
            }
            importedFollowRigs++;
        }

        int importedWorkflows = 0;
        foreach (DirectorWorkflow? source in package.Workflows ?? new List<DirectorWorkflow>())
        {
            if (source is null)
            {
                continue;
            }

            DirectorWorkflow clone = source.DeepClone();
            clone.Id = Guid.NewGuid();
            clone.Cards = (clone.Cards ?? new List<DirectorWorkflowCard>())
                .Where(card => card is not null
                    && (card.Kind == DirectorWorkflowCardKind.Countdown
                        || TryRemapWorkflowSource(
                            card.SourceType,
                            card.SourceId,
                            cameraIds,
                            pathIds,
                            sequenceIds,
                            followIds,
                            out _)))
                .Select(card =>
                {
                    Guid sourceId = Guid.Empty;
                    if (card.Kind != DirectorWorkflowCardKind.Countdown)
                    {
                        _ = TryRemapWorkflowSource(
                            card.SourceType,
                            card.SourceId,
                            cameraIds,
                            pathIds,
                            sequenceIds,
                            followIds,
                            out sourceId);
                    }
                    DirectorWorkflowCard mapped = card.DeepClone();
                    mapped.Id = Guid.NewGuid();
                    mapped.SourceId = sourceId;
                    return mapped;
                })
                .ToList();
            library.AddWorkflow(clone);
            importedWorkflows++;
        }

        return new DirectorPresetImportResult(
            library.Document,
            importedCameras,
            importedPaths,
            importedSequences,
            importedFollowRigs,
            importedWorkflows);
    }

    private static bool TryRemapWorkflowSource(
        DirectorRecordingSourceType type,
        Guid sourceId,
        IReadOnlyDictionary<Guid, Guid> cameraIds,
        IReadOnlyDictionary<Guid, Guid> pathIds,
        IReadOnlyDictionary<Guid, Guid> sequenceIds,
        IReadOnlyDictionary<Guid, Guid> followIds,
        out Guid mappedId)
    {
        return type switch
        {
            DirectorRecordingSourceType.StaticCamera => cameraIds.TryGetValue(sourceId, out mappedId),
            DirectorRecordingSourceType.Path => pathIds.TryGetValue(sourceId, out mappedId),
            DirectorRecordingSourceType.Sequence => sequenceIds.TryGetValue(sourceId, out mappedId),
            DirectorRecordingSourceType.Follow => followIds.TryGetValue(sourceId, out mappedId),
            _ => Fail(out mappedId)
        };
    }

    private static bool Fail(out Guid id)
    {
        id = Guid.Empty;
        return false;
    }
}
