using System.Text.Encodings.Web;
using System.Text.Json;

namespace VSDirector.Data;

public sealed class DirectorReplayTimelineEditDocument
{
    public const int CurrentSchemaVersion = 7;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid ReplayId { get; set; }

    public List<DirectorReplayMarker> Markers { get; set; } = new();

    public List<DirectorReplaySpeedKeyframe> SpeedKeyframes { get; set; } = new();

    public DirectorReplayViewportTimeline ViewportTimeline { get; set; } = new();

    public List<DirectorReplayPreloadColumn> IncludedPreloadColumns { get; set; } = new();

    public List<DirectorReplayPreloadColumn> ExcludedPreloadColumns { get; set; } = new();
}

public static class DirectorReplayTimelineEditIO
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public static DirectorReplayTimelineEditDocument Create(DirectorReplayDocument replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        DirectorReplayTimelineEditDocument document = new()
        {
            ReplayId = replay.Id,
            Markers = DirectorReplayTimeline.NormalizeMarkers(replay.Markers, replay.DurationSeconds),
            SpeedKeyframes = DirectorReplayTimeline.NormalizeSpeedKeyframes(
                replay.SpeedKeyframes,
                replay.DurationSeconds),
            ViewportTimeline = DirectorReplayViewportTimeline.Normalize(
                replay.ViewportTimeline,
                replay.DurationSeconds),
            IncludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(
                replay.IncludedPreloadColumns),
            ExcludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(
                replay.ExcludedPreloadColumns)
        };
        EnsureElementTracks(document.ViewportTimeline, document.SpeedKeyframes);
        return document;
    }

    public static DirectorReplayTimelineEditDocument? Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(fullPath);
        DirectorReplayTimelineEditDocument? document = JsonSerializer.Deserialize<DirectorReplayTimelineEditDocument>(
            stream,
            SerializerOptions);
        if (document is null)
        {
            throw new InvalidDataException($"Replay timeline sidecar '{fullPath}' was empty.");
        }
        if (document.SchemaVersion > DirectorReplayTimelineEditDocument.CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Replay timeline schema {document.SchemaVersion} is newer than supported schema "
                + $"{DirectorReplayTimelineEditDocument.CurrentSchemaVersion}.");
        }
        if (document.SchemaVersion < 6)
        {
            DirectorReplayViewportTimeline.MigrateLegacyTwentyTickTimebase(
                document.ViewportTimeline);
        }
        document.SchemaVersion = DirectorReplayTimelineEditDocument.CurrentSchemaVersion;
        return document;
    }

    public static void Save(string path, DirectorReplayTimelineEditDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, SerializerOptions);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void Apply(DirectorReplayDocument replay, DirectorReplayTimelineEditDocument edits)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.ReplayId != replay.Id)
        {
            throw new InvalidDataException("Replay timeline sidecar belongs to a different replay.");
        }

        replay.Markers = DirectorReplayTimeline.NormalizeMarkers(edits.Markers, replay.DurationSeconds);
        replay.SpeedKeyframes = DirectorReplayTimeline.NormalizeSpeedKeyframes(
            edits.SpeedKeyframes,
            replay.DurationSeconds);
        DirectorReplayViewportTimeline viewportTimeline = edits.ViewportTimeline?.DeepClone()
            ?? new DirectorReplayViewportTimeline();
        if (edits.SchemaVersion < 6)
        {
            DirectorReplayViewportTimeline.MigrateLegacyTwentyTickTimebase(viewportTimeline);
        }
        replay.ViewportTimeline = DirectorReplayViewportTimeline.Normalize(
            viewportTimeline,
            replay.DurationSeconds);
        EnsureElementTracks(replay.ViewportTimeline, replay.SpeedKeyframes);
        replay.IncludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(
            edits.IncludedPreloadColumns);
        replay.ExcludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(
            edits.ExcludedPreloadColumns);
    }

    private static void EnsureElementTracks(
        DirectorReplayViewportTimeline timeline,
        IReadOnlyCollection<DirectorReplaySpeedKeyframe> speedKeyframes)
    {
        DirectorReplayViewportTimeline.MigrateLegacySpeedKeyframes(timeline, speedKeyframes);
    }
}
