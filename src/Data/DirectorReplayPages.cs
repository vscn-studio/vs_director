using System.IO.Compression;
using System.Text.Json;

namespace VSDirector.Data;

/// <summary>Indexed compressed payload pages with a shared bounded cache.
/// Only metadata is loaded eagerly; reads hold no persistent file handles.</summary>
public sealed class DirectorReplayPages
{
    public const string Suffix = ".pages";
    private const int PageSize = 256;
    private const uint Magic = 0x50445356;
    private const long CacheLimit = 16 * 1024 * 1024;
    private readonly string path;
    private readonly Header header;
    private readonly Dictionary<string, (object Value, long Bytes, LinkedListNode<string> Node)> cache = new();
    private readonly LinkedList<string> lru = new();
    private readonly object gate = new();
    public long ResidentBytes { get; private set; }
    public long PagesRead { get; private set; }
    public long CacheHits { get; private set; }
    public bool PrefetchEnabled { get; set; } = true;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public sealed class Header
    {
        public int Version { get; set; } = 3;
        public long SourceBytes { get; set; }
        public long SourceTicks { get; set; }
        public string CompanionStamp { get; set; } = "";
        public DirectorReplayDocument Document { get; set; } = new();
        public Dictionary<string, int> Counts { get; set; } = new();
        public Dictionary<string, int> PageSizes { get; set; } = new();
        public Dictionary<string, Page> Entries { get; set; } = new();
        public List<BlockIndex> Blocks { get; set; } = new();
    }
    public sealed class Page
    {
        public double[]? Times { get; set; }
        public long Offset { get; set; }
        public int Bytes { get; set; }
        public long DecodedBytes { get; set; }
    }
    public sealed class BlockIndex
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public int Dimension { get; set; }
        public int[] Indices { get; set; } = [];
    }
    private DirectorReplayPages(string path, Header header) { this.path = path; this.header = header; }
    public IReadOnlyList<BlockIndex> BlockPositions => header.Blocks;
    // Export uses its own cache so sequential PCM reads cannot evict the
    // current playback's actor working set.
    public DirectorReplayList<DirectorReplayVoiceFrame> OpenVoiceView()
        => new DirectorReplayPages(path, header).List<DirectorReplayVoiceFrame>("voice");

    public static DirectorReplayDocument? TryOpen(string sourcePath)
    {
        string path = sourcePath + Suffix;
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        using var binary = new BinaryReader(file);
        if (binary.ReadUInt32() != Magic) return null;
        long offset = binary.ReadInt64();
        if (offset < 12 || offset >= file.Length) throw new InvalidDataException("Invalid replay page header offset.");
        file.Position = offset;
        using var stream = new GZipStream(file, CompressionMode.Decompress, true);
        Header h = JsonSerializer.Deserialize<Header>(stream, Options) ?? throw new InvalidDataException("Empty replay page header.");
        var source = new FileInfo(sourcePath);
        if (h.Version != 3 || h.SourceBytes != source.Length || h.SourceTicks != source.LastWriteTimeUtc.Ticks) return null;
        if (h.CompanionStamp.Length > 0 && h.Document.ServerReplay is { } link)
        {
            string? manifest = DirectorServerReplayCompanion.FindManifest(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, link);
            if (manifest is not null && Stamp(manifest) != h.CompanionStamp) return null;
        }
        var pages = new DirectorReplayPages(path, h);
        var document = h.Document;
        document.Pages = pages;
        document.BlockEvents = pages.List<DirectorReplayBlockEvent>("blocks");
        document.ParticleEvents = pages.List<DirectorReplayParticleEvent>("particles");
        document.SoundEvents = pages.List<DirectorReplaySoundEvent>("sounds");
        document.VoiceFrames = pages.List<DirectorReplayVoiceFrame>("voice");
        document.ClockSamples = pages.List<DirectorReplayClockSample>("clock");
        for (int i = 0; i < document.Actors.Count; i++)
        {
            int actorIndex = i;
            document.Actors[i].InitialStateReader = () => pages.Read<byte[]>($"actor/{actorIndex}/initial", 0);
            document.Actors[i].Samples = pages.List<DirectorReplayActorSample>($"actor/{i}/samples");
            document.Actors[i].States = pages.List<DirectorReplayActorState>($"actor/{i}/states");
        }
        for (int i = 0; i < document.WorldSnapshot.BlockEntities.Count; i++)
        {
            int entityIndex = i;
            document.WorldSnapshot.BlockEntities[i].DataReader = () => pages.Read<byte[]>($"blockentity/{entityIndex}", 0);
        }
        for (int i = 0; i < document.WorldSnapshot.Chunks.Count; i++)
        {
            document.WorldSnapshot.Chunks[i].SolidRuns = pages.List<DirectorReplayBlockRun>($"chunk/{i}/solid");
            document.WorldSnapshot.Chunks[i].FluidRuns = pages.List<DirectorReplayBlockRun>($"chunk/{i}/fluid");
        }
        return document;
    }

    private DirectorReplayList<T> List<T>(string key)
    {
        int size = header.PageSizes.GetValueOrDefault(key, PageSize);
        int count = header.Counts.GetValueOrDefault(key);
        if (count == 0 || header.Entries[key + "/0"].Times is null) return new(count, index => Read<T>(key, index));
        // Timestamp arrays are small compared with poses/PCM/state payloads.
        // Searching and visibility queries must never decompress distant pages.
        double[][] times = Enumerable.Range(0, (count + size - 1) / size)
            .Select(page => header.Entries[key + "/" + page].Times!).ToArray();
        int Search(double time, bool inclusive)
        {
            int low = 0, high = count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                double at = times[mid / size][mid % size];
                if (inclusive ? at <= time : at < time) low = mid + 1; else high = mid;
            }
            return low;
        }
        return new(count, index => Read<T>(key, index), Search);
    }

    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Length + ":" + file.LastWriteTimeUtc.Ticks;
    }

    private readonly HashSet<string> prefetching = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<object>> loading = new();

    private T Read<T>(string key, int index)
    {
        int size = header.PageSizes.GetValueOrDefault(key, PageSize);
        int page = index / size;
        T[] values = GetPage<T>(key + "/" + page);
        if (PrefetchEnabled && index % size >= size / 2) Prefetch<T>(key + "/" + (page + 1));
        return values[index % size];
    }

    private void Prefetch<T>(string entryName)
    {
        lock (gate)
        {
            if (prefetching.Count >= 4 || cache.ContainsKey(entryName) || prefetching.Contains(entryName)
                || !header.Entries.TryGetValue(entryName, out var entry) || entry.DecodedBytes * 3 > CacheLimit) return;
            prefetching.Add(entryName);
        }
        _ = Task.Run(() =>
        {
            try { _ = GetPage<T>(entryName); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { /* A foreground read reports the failure if this page is needed. */ }
            finally { lock (gate) prefetching.Remove(entryName); }
        });
    }

    private T[] GetPage<T>(string entryName)
    {
        lock (gate)
        {
            if (cache.TryGetValue(entryName, out var found))
            {
                CacheHits++; lru.Remove(found.Node); lru.AddFirst(found.Node);
                return (T[])found.Value;
            }
        }
        // Never hold the shared LRU lock during I/O/decompression. Background
        // PCM export and prefetch cannot block cached actor reads.
        var pending = loading.GetOrAdd(entryName, _ => new Lazy<object>(() => DecodePage<T>(entryName),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try { return (T[])pending.Value; }
        finally { loading.TryRemove(new KeyValuePair<string, Lazy<object>>(entryName, pending)); }
    }

    private object DecodePage<T>(string entryName)
    {
        if (!header.Entries.TryGetValue(entryName, out var entry)) throw new InvalidDataException("Replay page missing: " + entryName);
        using var file = File.OpenRead(path);
        if (entry.Offset < 12 || entry.Bytes < 0 || entry.Offset > file.Length - entry.Bytes) throw new InvalidDataException("Invalid replay page offset.");
        file.Position = entry.Offset;
        byte[] compressed = new byte[entry.Bytes]; file.ReadExactly(compressed);
        using var memory = new MemoryStream(compressed, false);
        using var stream = new GZipStream(memory, CompressionMode.Decompress);
        T[] values = JsonSerializer.Deserialize<T[]>(stream, Options) ?? throw new InvalidDataException("Empty replay page.");
        lock (gate)
        {
            PagesRead++;
            if (cache.TryGetValue(entryName, out var existing)) return existing.Value;
            long bytes = Math.Max(1, entry.DecodedBytes * 3);
            while (ResidentBytes + bytes > CacheLimit && lru.Last is { } last)
            {
                ResidentBytes -= cache[last.Value].Bytes;
                cache.Remove(last.Value); lru.RemoveLast();
            }
            if (bytes <= CacheLimit)
            {
                var node = lru.AddFirst(entryName);
                cache.Add(entryName, (values, bytes, node)); ResidentBytes += bytes;
            }
        }
        return values;
    }

    public static void Write(string sourcePath, DirectorReplayDocument document, string companionStamp = "", CancellationToken token = default)
    {
        string path = sourcePath + Suffix, temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var source = new FileInfo(sourcePath);
        Header h = new() { SourceBytes = source.Length, SourceTicks = source.LastWriteTimeUtc.Ticks, CompanionStamp = companionStamp };
        if (document.ServerReplay is { Materialized: true } link)
        {
            string? manifest = DirectorServerReplayCompanion.FindManifest(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, link);
            if (manifest is not null) h.CompanionStamp = Stamp(manifest);
        }
        // Build metadata without cloning any payload lists.
        h.Document = new DirectorReplayDocument
        {
            SchemaVersion = document.SchemaVersion, Id = document.Id, Name = document.Name, WorldId = document.WorldId,
            CreatedUtc = document.CreatedUtc, SampleRate = document.SampleRate, DurationSeconds = document.DurationSeconds,
            CaptureScope = document.CaptureScope, ServerRecording = document.ServerRecording, ServerReplay = document.ServerReplay,
            SessionMerge = document.SessionMerge, IncludesWorldSnapshot = document.IncludesWorldSnapshot,
            ParticleCaptureVersion = document.ParticleCaptureVersion, Markers = document.Markers, SpeedKeyframes = document.SpeedKeyframes,
            ViewportTimeline = document.ViewportTimeline, IncludedPreloadColumns = document.IncludedPreloadColumns, ExcludedPreloadColumns = document.ExcludedPreloadColumns,
            WorldSnapshot = new() { Dimension = document.WorldSnapshot.Dimension, MapSizeY = document.WorldSnapshot.MapSizeY,
                CenterChunkX = document.WorldSnapshot.CenterChunkX, CenterChunkZ = document.WorldSnapshot.CenterChunkZ,
                RadiusChunks = document.WorldSnapshot.RadiusChunks }
        };
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using var binary = new BinaryWriter(file, System.Text.Encoding.UTF8, true);
                binary.Write(Magic); binary.Write(0L);
                void WriteList<T>(string key, IReadOnlyList<T> items)
                {
                    h.Counts[key] = items.Count;
                    int size = typeof(T) == typeof(DirectorReplayActorSample) ? 16
                        : typeof(T) == typeof(DirectorReplayActorState) ? 8
                        : typeof(T) == typeof(DirectorReplayVoiceFrame) ? 16 : PageSize;
                    h.PageSizes[key] = size;
                    for (int i = 0; i < items.Count; i += size)
                    {
                        token.ThrowIfCancellationRequested();
                        var page = new T[Math.Min(size, items.Count - i)];
                        for (int j = 0; j < page.Length; j++) page[j] = items[i + j];
                        byte[] data = JsonSerializer.SerializeToUtf8Bytes(page, Options);
                        long offset = file.Position;
                        using (var output = new GZipStream(file, CompressionLevel.Fastest, true)) output.Write(data);
                        h.Entries[key + "/" + i / size] = new Page { Offset = offset, Bytes = checked((int)(file.Position - offset)), DecodedBytes = data.Length,
                            Times = Timestamp(page[0]) is null ? null : page.Select(item => Timestamp(item)!.Value).ToArray() };
                    }
                }
                WriteList("blocks", document.BlockEvents); WriteList("particles", document.ParticleEvents);
                WriteList("sounds", document.SoundEvents); WriteList("voice", document.VoiceFrames); WriteList("clock", document.ClockSamples);
                h.Blocks = document.BlockEvents.Select((b, i) => (b, i)).GroupBy(x => (x.b.X, x.b.Y, x.b.Z, x.b.Dimension))
                    .Select(g => new BlockIndex { X = g.Key.X, Y = g.Key.Y, Z = g.Key.Z, Dimension = g.Key.Dimension, Indices = g.Select(x => x.i).ToArray() }).ToList();
                for (int i = 0; i < document.Actors.Count; i++)
                {
                    var actor = document.Actors[i];
                    WriteList<byte[]>($"actor/{i}/initial", new[] { actor.InitialState });
                    WriteList($"actor/{i}/samples", actor.Samples); WriteList($"actor/{i}/states", actor.States);
                    h.Document.Actors.Add(new() { ActorKey = actor.ActorKey, EntityId = actor.EntityId, PlayerUid = actor.PlayerUid,
                        ForceGhost = actor.ForceGhost, EntityCode = actor.EntityCode, DisplayName = actor.DisplayName,
                        EquipmentSlotCapacity = actor.Samples.Select(s => s.EquipmentItems.Count).DefaultIfEmpty().Max(),
                        BackpackSlotCapacity = actor.Samples.Select(s => s.BackpackItems.Count).DefaultIfEmpty().Max(),
                        ReplayEntityId = actor.ReplayEntityId, SimulationRange = actor.SimulationRange, Lifetimes = actor.Lifetimes });
                }
                for (int i = 0; i < document.WorldSnapshot.BlockEntities.Count; i++)
                {
                    var entity = document.WorldSnapshot.BlockEntities[i];
                    WriteList<byte[]>($"blockentity/{i}", new[] { entity.Data });
                    h.Document.WorldSnapshot.BlockEntities.Add(new() { X = entity.X, Y = entity.Y, Z = entity.Z, Dimension = entity.Dimension, ClassName = entity.ClassName });
                }
                for (int i = 0; i < document.WorldSnapshot.Chunks.Count; i++)
                {
                    var chunk = document.WorldSnapshot.Chunks[i];
                    WriteList($"chunk/{i}/solid", chunk.SolidRuns); WriteList($"chunk/{i}/fluid", chunk.FluidRuns);
                    h.Document.WorldSnapshot.Chunks.Add(new() { ChunkX = chunk.ChunkX, ChunkY = chunk.ChunkY, ChunkZ = chunk.ChunkZ,
                        ChunkHash = chunk.ChunkHash, ChunkVersion = chunk.ChunkVersion, CapturedAtSeconds = chunk.CapturedAtSeconds });
                }
                long headerOffset = file.Position;
                using (var output = new GZipStream(file, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(output, h, Options);
                file.Position = 4; binary.Write(headerOffset); binary.Flush(); file.Flush(true);
            }
            token.ThrowIfCancellationRequested(); File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }

    private static double? Timestamp<T>(T item) => item switch
    {
        DirectorReplayActorSample sample => sample.TimeSeconds, DirectorReplayActorState state => state.TimeSeconds,
        DirectorReplayClockSample clock => clock.TimeSeconds, DirectorReplayBlockEvent block => block.TimeSeconds,
        DirectorReplayParticleEvent particle => particle.TimeSeconds, DirectorReplaySoundEvent sound => sound.TimeSeconds,
        DirectorReplayVoiceFrame voice => voice.TimeSeconds, _ => null
    };
}
