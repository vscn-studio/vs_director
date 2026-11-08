using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vintagestory.API.Client;
using VSDirector.Data;
using VSDirector.Recording;

namespace VSDirector.Replay;

internal sealed record DirectorReplayInfo(
    string Path,
    Guid Id,
    string Name,
    DateTimeOffset CreatedUtc,
    double DurationSeconds,
    int ActorCount)
{
    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

internal sealed class DirectorReplayStore : IDisposable
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ICoreClientAPI capi;
    private readonly string worldId;
    private readonly string directory;
    private readonly object indexGate = new();
    private readonly Dictionary<string, DirectorReplayInfo> indexedByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource indexCancellation = new();
    private Task? indexTask;
    private int indexedFiles;
    private int totalFiles;
    private string indexError = string.Empty;
    private bool disposed;

    public DirectorReplayStore(ICoreClientAPI capi, string worldId, string outputDirectory = "")
    {
        this.capi = capi;
        this.worldId = worldId;
        string root = ResolveRoot(capi, outputDirectory);
        directory = Path.Combine(root, BuildWorldDirectory(worldId));
        Directory.CreateDirectory(directory);
        BeginIndexRefresh();
    }
    internal static string ResolveRoot(ICoreClientAPI api, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return Path.Combine(api.GetOrCreateDataPath("ModData"), "VSDirector", "Replays");
        string path = configured.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Replay directory must be an absolute path.");
        return Path.GetFullPath(path);
    }

    public bool IsIndexing
    {
        get
        {
            lock (indexGate)
            {
                return indexTask is { IsCompleted: false };
            }
        }
    }

    public double IndexProgress
    {
        get
        {
            lock (indexGate)
            {
                return totalFiles <= 0 ? (IsIndexing ? 0d : 1d) : Math.Clamp(indexedFiles / (double)totalFiles, 0d, 1d);
            }
        }
    }

    public string IndexError
    {
        get
        {
            lock (indexGate)
            {
                return indexError;
            }
        }
    }

    public string Save(
        DirectorReplayDocument replay,
        DirectorReplayCompressionProfile compression = DirectorReplayCompressionProfile.Balanced)
        => SaveCore(replay, compression, CancellationToken.None, normalized: false);

    private string SaveCore(
        DirectorReplayDocument replay,
        DirectorReplayCompressionProfile compression,
        CancellationToken cancellationToken,
        bool normalized)
    {
        DirectorReplayDocument snapshot = normalized ? replay : DirectorReplayMath.Normalize(replay, cancellationToken);
        string name = DirectorRecordingFiles.SanitizeFilePart(snapshot.Name, "Replay");
        string targetDirectory = snapshot.ServerRecording is { } link
            ? Path.Combine(directory, "session-" + link.SessionId.ToString("N")) : directory;
        string path = Path.Combine(targetDirectory, $"{name}-{snapshot.Id:N}.vsdreplay.gz");
        DirectorReplayIO.SaveNormalized(path, snapshot, compression, cancellationToken);
        DirectorReplayInfo info = ToInfo(path, snapshot);
        try { File.Delete(TimelinePath(path)); WriteMetadata(path, info); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { capi.Logger.Warning("VS Director: replay saved, optional metadata cache failed: {0}", e.Message); }
        lock (indexGate)
        {
            indexedByPath[path] = info;
        }
        return path;
    }

    public Task<string> SaveAsync(
        DirectorReplayDocument replay,
        DirectorReplayCompressionProfile compression = DirectorReplayCompressionProfile.Balanced,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replay);
        return Task.Run(() => Save(replay, compression), cancellationToken);
    }

    public IReadOnlyList<DirectorReplayInfo> List()
    {
        BeginIndexRefresh();
        lock (indexGate)
        {
            return Ordered(indexedByPath.Values.GroupBy(info => info.Id)
                .Select(group => group.OrderBy(info => IsLocal(info.Path) ? 0 : 1).First())).ToArray();
        }
    }

    public DirectorReplayInfo? Resolve(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        IReadOnlyList<DirectorReplayInfo> available = List();
        string trimmed = selector.Trim();
        if (int.TryParse(trimmed, out int index) && index >= 1 && index <= available.Count)
        {
            return available[index - 1];
        }

        if (Guid.TryParse(trimmed, out Guid id))
        {
            return available.FirstOrDefault(replay => replay.Id == id);
        }

        return available.FirstOrDefault(
            replay => string.Equals(replay.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public DirectorReplayDocument? Find(string selector)
    {
        DirectorReplayInfo? match = Resolve(selector);
        if (match is null)
        {
            return null;
        }

        DirectorReplayDocument replay = DirectorReplayIO.Load(match.Path);
        replay.Name = match.Name;
        ApplyTimelineEdits(match.Path, replay);
        return replay;
    }

    public bool UpdateMetadata(
        Guid id,
        string name,
        IEnumerable<string>? tags,
        string? description,
        out DirectorReplayInfo? updated,
        out string error)
    {
        updated = null;
        error = string.Empty;
        string normalizedName = name?.Trim() ?? string.Empty;
        if (normalizedName.Length == 0)
        {
            error = "replay-name-required";
            return false;
        }

        DirectorReplayInfo? current;
        lock (indexGate)
        {
            current = indexedByPath.Values.OrderBy(info => IsLocal(info.Path) ? 0 : 1).FirstOrDefault(info => info.Id == id);
        }
        if (current is null)
        {
            error = "replay-not-found";
            return false;
        }

        try
        {
            string fullPath = ValidateReplayPath(current.Path);
            if (!File.Exists(fullPath))
            {
                error = "replay-not-found";
                return false;
            }

            updated = current with
            {
                Name = normalizedName,
                Tags = NormalizeTags(tags),
                Description = description?.Trim() ?? string.Empty
            };
            WriteMetadata(fullPath, updated);
            lock (indexGate)
            {
                indexedByPath[fullPath] = updated;
            }
            return true;
        }
        catch (Exception exception)
        {
            updated = null;
            error = exception.Message;
            return false;
        }
    }

    public void SaveTimelineEdits(string replayPath, DirectorReplayDocument replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        string fullPath = ValidateReplayPath(replayPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Replay source was not found.", fullPath);
        }

        DirectorReplayTimelineEditIO.Save(
            TimelinePath(fullPath),
            DirectorReplayTimelineEditIO.Create(replay));
    }

    public void ApplyTimelineEdits(string replayPath, DirectorReplayDocument replay)
    {
        try
        {
            DirectorReplayTimelineEditDocument? edits = DirectorReplayTimelineEditIO.Load(
                TimelinePath(ValidateReplayPath(replayPath)));
            if (edits is not null)
            {
                DirectorReplayTimelineEditIO.Apply(replay, edits);
            }
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "VS Director: ignored invalid replay timeline sidecar for {0}: {1}",
                replayPath,
                exception.Message);
        }
    }

    public bool Delete(Guid id, out string deletedName, out string error)
    {
        deletedName = string.Empty;
        error = string.Empty;
        DirectorReplayInfo? match;
        lock (indexGate)
        {
            match = indexedByPath.Values.OrderBy(info => IsLocal(info.Path) ? 0 : 1).FirstOrDefault(info => info.Id == id);
        }

        if (match is null)
        {
            error = "replay-not-found";
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(match.Path);
            string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                error = "replay-delete-path";
                return false;
            }

            File.Delete(fullPath);
            File.Delete(fullPath + ".session.json"); // Remove legacy optional session cache.
            File.Delete(fullPath + DirectorReplayPages.Suffix);
            File.Delete(MetadataPath(fullPath));
            File.Delete(TimelinePath(fullPath));
            lock (indexGate)
            {
                indexedByPath.Remove(fullPath);
            }
            BeginIndexRefresh(force: true);
            deletedName = match.Name;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public void BeginIndexRefresh(bool force = false)
    {
        lock (indexGate)
        {
            if (disposed || (!force && indexTask is not null))
            {
                return;
            }

            if (force)
            {
                indexCancellation.Cancel();
                indexCancellation.Dispose();
                indexCancellation = new CancellationTokenSource();
                indexedByPath.Clear();
            }

            indexedFiles = 0;
            totalFiles = 0;
            indexError = string.Empty;
            CancellationToken token = indexCancellation.Token;
            indexTask = Task.Run(() => BuildIndex(token), token);
        }
    }

    private void BuildIndex(CancellationToken token)
    {
        try
        {
            string[] files = Directory.EnumerateFiles(directory, "*.vsdreplay.gz", SearchOption.AllDirectories)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            lock (indexGate)
            {
                token.ThrowIfCancellationRequested();
                totalFiles = files.Length;
            }

            foreach (string path in files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    DirectorReplayInfo? info = TryReadMetadata(path);
                    if (info is null)
                    {
                        DirectorReplayDocument replay = DirectorReplayIO.Load(path);
                        if (!string.Equals(replay.WorldId, worldId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        info = ToInfo(path, replay);
                        WriteMetadata(path, info);
                    }

                    lock (indexGate)
                    {
                        token.ThrowIfCancellationRequested();
                        indexedByPath[path] = info;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lock (indexGate) { if (!token.IsCancellationRequested) indexError = exception.Message; }
                    capi.Logger.Warning(
                        "VS Director: ignored unreadable replay {0}: {1}",
                        path,
                        exception.Message);
                }
                finally
                {
                    lock (indexGate)
                    {
                        if (!token.IsCancellationRequested) indexedFiles++;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            lock (indexGate)
            {
                indexError = exception.Message;
            }
            capi.Logger.Warning("VS Director: replay index refresh failed: {0}", exception.Message);
        }
    }

    private DirectorReplayInfo? TryReadMetadata(string replayPath)
    {
        string metadataPath = MetadataPath(replayPath);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(metadataPath);
        ReplayMetadata? metadata = JsonSerializer.Deserialize<ReplayMetadata>(stream, MetadataJsonOptions);
        FileInfo replayFile = new(replayPath);
        if (metadata is null
            || metadata.SourceLength != replayFile.Length
            || metadata.SourceLastWriteUtcTicks != replayFile.LastWriteTimeUtc.Ticks
            || !string.Equals(metadata.WorldId, worldId, StringComparison.Ordinal))
        {
            return null;
        }

        return new DirectorReplayInfo(
            replayPath,
            metadata.Id,
            metadata.Name,
            metadata.CreatedUtc,
            metadata.DurationSeconds,
            metadata.ActorCount)
        {
            Description = metadata.Description?.Trim() ?? string.Empty,
            Tags = NormalizeTags(metadata.Tags)
        };
    }

    private void WriteMetadata(string replayPath, DirectorReplayInfo info)
    {
        FileInfo replayFile = new(replayPath);
        ReplayMetadata metadata = new()
        {
            SchemaVersion = 2,
            SourceLength = replayFile.Length,
            SourceLastWriteUtcTicks = replayFile.LastWriteTimeUtc.Ticks,
            WorldId = worldId,
            Id = info.Id,
            Name = info.Name,
            Description = info.Description,
            Tags = info.Tags.ToList(),
            CreatedUtc = info.CreatedUtc,
            DurationSeconds = info.DurationSeconds,
            ActorCount = info.ActorCount
        };
        string metadataPath = MetadataPath(replayPath);
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        string temporary = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, metadata, MetadataJsonOptions);
            }

            File.Move(temporary, metadataPath, overwrite: true);
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
        }
    }

    private static DirectorReplayInfo ToInfo(string path, DirectorReplayDocument replay)
        => new(path, replay.Id, replay.Name, replay.CreatedUtc, replay.DurationSeconds, replay.Actors.Count)
        { Tags = replay.SessionMerge is not null ? ["Director", "merged"] : [],
          Description = replay.SessionMerge is { } merge ? $"Session {merge.SessionId}; {merge.Replays.Count} sources; clock uncertainty {merge.MaxClockUncertaintyUs / 1000d:0.###} ms. "
              + string.Join(" ", merge.Warnings) : "" };

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags)
        => (tags ?? Array.Empty<string>())
            .Select(tag => tag?.Trim() ?? string.Empty)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IOrderedEnumerable<DirectorReplayInfo> Ordered(IEnumerable<DirectorReplayInfo> replays)
        => replays
            .OrderByDescending(replay => replay.CreatedUtc)
            .ThenBy(replay => replay.Name, StringComparer.OrdinalIgnoreCase);

    private string MetadataPath(string replayPath) => SidecarBase(replayPath) + ".meta.json";

    private string TimelinePath(string replayPath) => SidecarBase(replayPath) + ".timeline.json";

    private bool IsLocal(string path) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private string SidecarBase(string path) => IsLocal(path) ? path : Path.Combine(directory, "edits",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))));

    private string ValidateReplayPath(string replayPath)
    {
        string fullPath = Path.GetFullPath(replayPath);
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            lock (indexGate)
                if (!indexedByPath.ContainsKey(fullPath))
                    throw new InvalidOperationException("Replay timeline path is outside the current world store.");
        }
        return fullPath;
    }

    private static string BuildWorldDirectory(string value)
    {
        string readable = DirectorRecordingFiles.SanitizeFilePart(value, "world");
        if (readable.Length > 24)
        {
            readable = readable[..24];
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16]
            .ToLowerInvariant();
        return $"{readable}-{hash}";
    }

    public void Dispose()
    {
        lock (indexGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            indexCancellation.Cancel();
            indexCancellation.Dispose();
        }
    }

    private sealed class ReplayMetadata
    {
        public int SchemaVersion { get; set; }

        public long SourceLength { get; set; }

        public long SourceLastWriteUtcTicks { get; set; }

        public string WorldId { get; set; } = string.Empty;

        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public List<string> Tags { get; set; } = new();

        public DateTimeOffset CreatedUtc { get; set; }

        public double DurationSeconds { get; set; }

        public int ActorCount { get; set; }
    }
}
