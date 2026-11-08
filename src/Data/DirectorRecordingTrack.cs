using System.Text.Json.Serialization;

namespace VSDirector.Data;

public enum DirectorRecordingSourceType
{
    Program,
    StaticCamera,
    Path,
    Sequence,
    Follow,
    Workflow,
    // Internal replay-export source; never offered as a recording assignment.
    ReplayScene
}

/// <summary>
/// One independently rendered recording assignment. A single game client can
/// own one assignment at a time; multiple clients provide independent tracks.
/// </summary>
public sealed class DirectorRecordingTrack
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DirectorRecordingSourceType SourceType { get; set; }

    public Guid SourceId { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }

    [JsonIgnore]
    public double? ExportStartSeconds { get; set; }

    [JsonIgnore]
    public double? ExportEndSeconds { get; set; }

    [JsonIgnore]
    public List<DirectorWorkflowExportSegment> WorkflowSegments { get; set; } = new();

    public DirectorRecordingTrack DeepClone()
    {
        return new DirectorRecordingTrack
        {
            Id = Id,
            Name = Name,
            SourceType = SourceType,
            SourceId = SourceId,
            Enabled = Enabled,
            CreatedUtc = CreatedUtc,
            ModifiedUtc = ModifiedUtc,
            ExportStartSeconds = ExportStartSeconds,
            ExportEndSeconds = ExportEndSeconds,
            WorkflowSegments = (WorkflowSegments ?? new List<DirectorWorkflowExportSegment>())
                .Select(segment => segment.DeepClone())
                .ToList()
        };
    }

    public static bool RequiresSource(DirectorRecordingSourceType sourceType)
        => sourceType != DirectorRecordingSourceType.Program;
}
