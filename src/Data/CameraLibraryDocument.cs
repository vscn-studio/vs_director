using System.Text.Json.Serialization;

namespace VSDirector.Data;

/// <summary>
/// Versioned JSON root for a world's saved camera presets.
/// </summary>
public sealed class CameraLibraryDocument
{
    public const int CurrentSchemaVersion = 22;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<CameraPreset> Cameras { get; set; } = new();

    public List<CameraPath> Paths { get; set; } = new();

    public List<CameraSequence> Sequences { get; set; } = new();

    public List<DirectorWorkflow> Workflows { get; set; } = new();

    public List<CameraFollowRig> FollowRigs { get; set; } = new();

    // Kept only for in-process compatibility with legacy helpers. Removed from world JSON.
    [JsonIgnore]
    public List<DirectorRecordingTrack> RecordingTracks { get; set; } = new();

    public List<DirectorActorPerformance> ActorPerformances { get; set; } = new();

    public List<DirectorActionAsset> Actions { get; set; } = new();

    public List<DirectorModelAsset> Models { get; set; } = new();

    /// <summary>
    /// Compatibility alias for callers that describe the collection as presets.
    /// It is intentionally omitted from JSON; <see cref="Cameras"/> is canonical.
    /// </summary>
    [JsonIgnore]
    public List<CameraPreset> Presets
    {
        get => Cameras;
        set => Cameras = value ?? new List<CameraPreset>();
    }

    [JsonIgnore]
    public int Version
    {
        get => SchemaVersion;
        set => SchemaVersion = value;
    }

    public CameraLibraryDocument DeepClone()
    {
        return new CameraLibraryDocument
        {
            SchemaVersion = SchemaVersion,
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
                : FollowRigs.Where(rig => rig is not null).Select(rig => rig.DeepClone()).ToList(),
            RecordingTracks = RecordingTracks is null
                ? new List<DirectorRecordingTrack>()
                : RecordingTracks
                    .Where(track => track is not null)
                    .Select(track => track.DeepClone())
                    .ToList(),
            ActorPerformances = ActorPerformances is null
                ? new List<DirectorActorPerformance>()
                : ActorPerformances
                    .Where(performance => performance is not null)
                    .Select(performance => performance.DeepClone())
                    .ToList(),
            Actions = Actions is null
                ? new List<DirectorActionAsset>()
                : Actions
                    .Where(action => action is not null)
                    .Select(action => action.DeepClone())
                    .ToList(),
            Models = Models is null
                ? new List<DirectorModelAsset>()
                : Models
                    .Where(model => model is not null)
                    .Select(model => model.DeepClone())
                    .ToList()
        };
    }
}
