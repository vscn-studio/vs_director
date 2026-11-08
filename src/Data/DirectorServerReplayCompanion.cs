using System.IO.Compression;
using System.Text.Json;

namespace VSDirector.Data;

/// <summary>
/// Reads the server companion stream and overlays authoritative observations
/// onto a client replay. The client document remains the source of camera,
/// animation, audio and particle presentation data.
/// </summary>
public static class DirectorServerReplayCompanion
{
    private const double TimeEpsilon = 0.0005d;

    public static bool TryMergeAdjacent(DirectorReplayDocument document, string clientReplayPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.ServerReplay is not { Available: true, Materialized: false, ManifestFile: { Length: > 0 } link }) return false;
        string replayDirectory = Path.GetDirectoryName(Path.GetFullPath(clientReplayPath))!;
        string? manifest = FindManifest(replayDirectory, link);
        if (manifest is null) return false;
        if (document.ServerRecording is { ClockAnchors.Count: > 0 } session)
        {
            var timeMap = new DirectorSessionTimeMap(session);
            Merge(document, manifest, cancellationToken, timeMap.ServerSeconds(0), timeMap.ServerSeconds(document.DurationSeconds), timeMap.ReplaySeconds);
        }
        else Merge(document, manifest, cancellationToken, 0, document.DurationSeconds > 0 ? document.DurationSeconds : double.PositiveInfinity);
        return true;
    }

    public static string? FindManifest(string replayDirectory, DirectorServerReplayLink link)
        => FindManifest(replayDirectory, link.ManifestFile);

    public static string? FindManifest(string replayDirectory, string manifestFile)
    {
        if (string.IsNullOrWhiteSpace(manifestFile)) return null;
        string fullDirectory = Path.GetFullPath(replayDirectory);
        string direct = Path.Combine(fullDirectory, manifestFile);
        if (File.Exists(direct)) return direct;
        string? nested = null;
        try
        {
            nested = Directory.EnumerateFiles(fullDirectory, Path.GetFileName(manifestFile), SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (nested is not null) return nested;
        string parent = Directory.GetParent(fullDirectory)?.FullName ?? string.Empty;
        string adjacent = Path.Combine(parent, manifestFile);
        return File.Exists(adjacent) ? adjacent : null;
    }

    public static void Merge(DirectorReplayDocument document, string manifestPath, CancellationToken cancellationToken = default,
        double fromSeconds = 0d, double toSeconds = double.PositiveInfinity, Func<double, double>? serverToReplay = null)
    {
        DirectorServerReplayManifest manifest = DirectorServerJson.Deserialize<DirectorServerReplayManifest>(File.ReadAllBytes(manifestPath));
        if (document.ServerReplay is { SessionId: var expected } && expected != Guid.Empty && expected != manifest.SessionId)
            throw new InvalidDataException("Server replay companion belongs to a different session.");

        List<DirectorServerReplayRecord> records = ReadRecords(manifest, manifestPath, fromSeconds, toSeconds, cancellationToken);
        if (serverToReplay is not null)
            foreach (var record in records) record.TimeSeconds = Math.Max(0, serverToReplay(record.TimeSeconds + fromSeconds));
        Apply(document, records);
        if (document.ServerReplay is not null)
        {
            document.ServerReplay.Available = true;
            document.ServerReplay.Materialized = true;
            document.ServerReplay.RequiredForCompletePlayback = false;
        }
    }

    private static List<DirectorServerReplayRecord> ReadRecords(DirectorServerReplayManifest manifest, string manifestPath, double fromSeconds, double toSeconds, CancellationToken cancellationToken)
    {
        var reader = new DirectorServerReplayLazyReader(manifestPath);
        var records = new List<DirectorServerReplayRecord>();
        if (fromSeconds > 0)
        {
            foreach (var key in manifest.Chunks.Select(c => (c.Dimension, c.ChunkX, c.ChunkY, c.ChunkZ)).Distinct())
            {
                var baseline = reader.ReadChunk(key.Dimension, key.ChunkX, key.ChunkY, key.ChunkZ, fromSeconds, cancellationToken).SingleOrDefault();
                if (baseline is null || baseline.TimeSeconds >= fromSeconds) continue;
                double captured = baseline.TimeSeconds;
                // Restore intervening events so a clip beginning between two
                // baselines starts with the state at its own boundary.
                int[] solid = DirectorReplayChunkCodec.Decode(baseline.SolidRuns, 32768), fluid = DirectorReplayChunkCodec.Decode(baseline.FluidRuns, 32768);
                foreach (var change in reader.Read(captured, fromSeconds, cancellationToken).Where(r => r.Kind is DirectorServerReplayRecordKind.BlockPlace or DirectorServerReplayRecordKind.BlockBreak))
                {
                    if (change.Dimension != key.Dimension || (int)change.X >> 5 != key.ChunkX || (int)change.Y >> 5 != key.ChunkY || (int)change.Z >> 5 != key.ChunkZ) continue;
                    int i = (((int)change.Y & 31) * 32 + ((int)change.Z & 31)) * 32 + ((int)change.X & 31);
                    solid[i] = change.SolidBlockId; fluid[i] = change.FluidBlockId;
                    baseline.BlockEntities.RemoveAll(b => b.X == (int)change.X && b.Y == (int)change.Y && b.Z == (int)change.Z);
                    if (change.BlockEntityClass.Length > 0) baseline.BlockEntities.Add(new() { X = (int)change.X, Y = (int)change.Y, Z = (int)change.Z, Dimension = key.Dimension, ClassName = change.BlockEntityClass, Data = change.BlockEntityData });
                }
                baseline.SolidRuns = DirectorReplayChunkCodec.Encode(solid); baseline.FluidRuns = DirectorReplayChunkCodec.Encode(fluid);
                baseline.TimeSeconds = 0; records.Add(baseline);
            }
        }
        records.AddRange(reader.Read(fromSeconds, toSeconds, cancellationToken));
        return records;
    }

    private static void Apply(DirectorReplayDocument document, IReadOnlyList<DirectorServerReplayRecord> records)
    {
        records = records.OrderBy(record => record.TimeSeconds).ToArray();
        Dictionary<string, DirectorReplayActorTrack> actors = document.Actors
            .Where(actor => !string.IsNullOrWhiteSpace(actor.PlayerUid))
            .GroupBy(actor => actor.PlayerUid, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        long nextReplayEntityId = document.Actors.Select(actor => actor.ReplayEntityId).DefaultIfEmpty(long.MinValue + 1).Min() - 1;

        foreach (var group in records.Where(record => record.IsPlayer).GroupBy(record => record.PlayerUid, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(group.Key)) continue;
            DirectorServerReplayRecord[] ordered = group.OrderBy(record => record.TimeSeconds).ToArray();
            if (!actors.TryGetValue(group.Key, out DirectorReplayActorTrack? actor))
            {
                DirectorServerReplayRecord first = ordered.First();
                actor = new DirectorReplayActorTrack
                {
                    ActorKey = "server:" + group.Key,
                    PlayerUid = group.Key,
                    EntityId = first.EntityId,
                    ReplayEntityId = nextReplayEntityId--,
                    EntityCode = string.IsNullOrWhiteSpace(first.EntityCode) ? "game:player" : first.EntityCode,
                    DisplayName = first.PlayerName,
                    ForceGhost = true
                };
                document.Actors.Add(actor);
                actors[group.Key] = actor;
            }

            double start = ordered[0].TimeSeconds;
            double end = ordered[^1].TimeSeconds;
            if (actor.Lifetimes.Count == 0) actor.Lifetimes.Add(new() { StartSeconds = start, EndSeconds = end });
            else actor.Lifetimes[0].EndSeconds = Math.Max(actor.Lifetimes[0].EndSeconds, end);
            foreach (DirectorServerReplayRecord record in ordered.Where(record => record.Kind == DirectorServerReplayRecordKind.PlayerSample))
            {
                DirectorReplayActorSample? existing = actor.Samples.FirstOrDefault(sample => Math.Abs(sample.TimeSeconds - record.TimeSeconds) <= TimeEpsilon);
                if (existing is null)
                {
                    actor.Samples.Add(new DirectorReplayActorSample { TimeSeconds = record.TimeSeconds });
                    existing = actor.Samples[^1];
                }
                existing.X = record.X; existing.Y = record.Y; existing.Z = record.Z;
                existing.Dimension = record.Dimension; existing.Yaw = record.Yaw;
                existing.HasAgentState = true; existing.BodyYawServer = record.Yaw;
            }
            actor.Samples = actor.Samples.OrderBy(sample => sample.TimeSeconds).ToList();
        }

        // NPCs and animals use the same authoritative track format as players,
        // but are keyed by entity id so respawns with the same code stay distinct.
        foreach (var group in records.Where(record => record.Kind == DirectorServerReplayRecordKind.EntitySample).GroupBy(record => record.EntityId))
        {
            DirectorServerReplayRecord first = group.OrderBy(record => record.TimeSeconds).First();
            DirectorReplayActorTrack actor = document.Actors.FirstOrDefault(item => item.EntityId == first.EntityId && item.ForceGhost)
                ?? new DirectorReplayActorTrack { ActorKey = "server:entity:" + first.EntityId, EntityId = first.EntityId, ReplayEntityId = nextReplayEntityId--, EntityCode = first.EntityCode, ForceGhost = true };
            if (!document.Actors.Contains(actor)) document.Actors.Add(actor);
            foreach (DirectorServerReplayRecord record in group.OrderBy(record => record.TimeSeconds))
                actor.Samples.Add(new DirectorReplayActorSample { TimeSeconds = record.TimeSeconds, X = record.X, Y = record.Y, Z = record.Z, Dimension = record.Dimension, Yaw = record.Yaw, HasAgentState = true, BodyYawServer = record.Yaw });
            actor.Samples = actor.Samples.OrderBy(item => item.TimeSeconds).GroupBy(item => item.TimeSeconds).Select(item => item.Last()).ToList();
            actor.Lifetimes.Clear(); actor.Lifetimes.Add(new() { StartSeconds = actor.Samples[0].TimeSeconds, EndSeconds = actor.Samples[^1].TimeSeconds });
        }

        var chunkStates = new Dictionary<(int, int, int, int), (int[] Solid, int[] Fluid, Dictionary<(int, int, int), DirectorReplayBlockEntitySnapshot> Entities)>();
        foreach (var record in records)
        {
            if (record.Kind is DirectorServerReplayRecordKind.BlockPlace or DirectorServerReplayRecordKind.BlockBreak)
            {
                int x = (int)record.X, y = (int)record.Y, z = (int)record.Z;
                document.BlockEvents.Add(new()
                {
                    TimeSeconds = record.TimeSeconds, X = x, Y = y, Z = z, Dimension = record.Dimension,
                    SolidBlockId = record.SolidBlockId, FluidBlockId = record.FluidBlockId,
                    BlockEntityCaptured = true, BlockEntityClass = record.BlockEntityClass, BlockEntityData = record.BlockEntityData
                });
                if (chunkStates.TryGetValue((record.Dimension, x >> 5, y >> 5, z >> 5), out var state))
                {
                    int i = ((y & 31) * 32 + (z & 31)) * 32 + (x & 31);
                    state.Solid[i] = record.SolidBlockId; state.Fluid[i] = record.FluidBlockId;
                    state.Entities.Remove((x, y, z));
                    if (record.BlockEntityClass.Length > 0) state.Entities[(x, y, z)] = new()
                    { X = x, Y = y, Z = z, Dimension = record.Dimension, ClassName = record.BlockEntityClass, Data = record.BlockEntityData };
                }
                continue;
            }
            if (record.Kind != DirectorServerReplayRecordKind.ChunkBaseline) continue;
            if (document.WorldSnapshot.Chunks.Count > 0 && document.WorldSnapshot.Dimension != record.Dimension)
                continue; // The native world snapshot represents one dimension.
            var key = (record.Dimension, record.ChunkX, record.ChunkY, record.ChunkZ);
            int[] solid = DirectorReplayChunkCodec.Decode(record.SolidRuns, 32768);
            int[] fluid = DirectorReplayChunkCodec.Decode(record.FluidRuns, 32768);
            var entities = record.BlockEntities.GroupBy(e => (e.X, e.Y, e.Z)).ToDictionary(g => g.Key, g => g.Last());
            if (!chunkStates.TryGetValue(key, out var previous))
            {
                document.WorldSnapshot.Dimension = record.Dimension;
                document.WorldSnapshot.MapSizeY = Math.Max(document.WorldSnapshot.MapSizeY, (record.ChunkY + 1) * 32);
                var snapshot = new DirectorReplayChunkSnapshot { CapturedAtSeconds = record.TimeSeconds, ChunkX = record.ChunkX, ChunkY = record.ChunkY,
                    ChunkZ = record.ChunkZ, SolidRuns = record.SolidRuns, FluidRuns = record.FluidRuns, ChunkHash = record.ChunkHash, ChunkVersion = record.ChunkVersion };
                int index = document.WorldSnapshot.Chunks.FindIndex(c => c.ChunkX == record.ChunkX && c.ChunkY == record.ChunkY && c.ChunkZ == record.ChunkZ);
                if (index >= 0) document.WorldSnapshot.Chunks[index] = snapshot; else document.WorldSnapshot.Chunks.Add(snapshot);
                document.WorldSnapshot.BlockEntities.RemoveAll(e => e.Dimension == record.Dimension && e.X >> 5 == record.ChunkX && e.Y >> 5 == record.ChunkY && e.Z >> 5 == record.ChunkZ);
                document.WorldSnapshot.BlockEntities.AddRange(entities.Values);
            }
            else
            {
                for (int i = 0; i < 32768; i++)
                {
                    int x = record.ChunkX * 32 + i % 32, y = record.ChunkY * 32 + i / 1024, z = record.ChunkZ * 32 + i / 32 % 32;
                    previous.Entities.TryGetValue((x, y, z), out var oldEntity);
                    entities.TryGetValue((x, y, z), out var newEntity);
                    bool entityChanged = oldEntity?.ClassName != newEntity?.ClassName
                        || !(oldEntity?.Data ?? []).AsSpan().SequenceEqual(newEntity?.Data ?? []);
                    if (previous.Solid[i] == solid[i] && previous.Fluid[i] == fluid[i] && !entityChanged) continue;
                    document.BlockEvents.Add(new() { TimeSeconds = record.TimeSeconds, X = x, Y = y, Z = z, Dimension = record.Dimension,
                        SolidBlockId = solid[i], FluidBlockId = fluid[i], BlockEntityCaptured = true,
                        BlockEntityClass = newEntity?.ClassName ?? "", BlockEntityData = newEntity?.Data ?? [] });
                }
            }
            chunkStates[key] = (solid, fluid, entities);
        }
        if (records.Any(record => record.Kind == DirectorServerReplayRecordKind.ChunkBaseline))
            document.IncludesWorldSnapshot = true;
        document.BlockEvents = document.BlockEvents.OrderBy(item => item.TimeSeconds).ToList();
        document.DurationSeconds = Math.Max(document.DurationSeconds, records.Count == 0 ? 0d : records.Max(record => record.TimeSeconds));
        document.CaptureScope = "server-authoritative-plus-client-witness";
    }
}
