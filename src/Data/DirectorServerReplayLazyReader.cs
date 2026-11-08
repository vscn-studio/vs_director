using System.IO.Compression;
using System.Text.Json;

namespace VSDirector.Data;

public sealed class DirectorServerReplayLazyReader
{
    private readonly string root;
    public DirectorServerReplayManifest Manifest { get; }
    private readonly Dictionary<int, DirectorServerReplaySegmentInfo> segments;
    private readonly Dictionary<int, DirectorServerReplayCheckpoint[]> checkpoints;
    private readonly Dictionary<(int, int, int, int), DirectorServerReplayChunkIndex[]> chunks;
    private readonly Dictionary<string, DirectorServerReplayChunkIndex> payloads;
    public long DecodedRecords { get; private set; }

    public DirectorServerReplayLazyReader(string manifestPath)
    {
        root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        Manifest = DirectorServerJson.Deserialize<DirectorServerReplayManifest>(File.ReadAllBytes(manifestPath));
        if (Manifest.Version is < 1 or > DirectorServerReplayManifest.CurrentVersion)
            throw new InvalidDataException("Unsupported server manifest version.");
        segments = Manifest.Segments.ToDictionary(item => item.Index);
        checkpoints = Manifest.Checkpoints.GroupBy(item => item.Segment).ToDictionary(g => g.Key, g => g.OrderBy(x => x.RecordOffset).ToArray());
        chunks = Manifest.Chunks.GroupBy(item => (item.Dimension, item.ChunkX, item.ChunkY, item.ChunkZ))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.TimeSeconds).ThenBy(x => x.RecordOffset).ToArray());
        payloads = Manifest.Chunks.Where(item => item.HasPayload).GroupBy(item => item.Hash).ToDictionary(g => g.Key, g => g.First());
    }
    private string Resolve(string name)
    {
        string path = Path.GetFullPath(Path.Combine(root, name));
        if (string.IsNullOrWhiteSpace(name) || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid server segment path.");
        return path;
    }
    private static int Last<T>(T[] items, Func<T, bool> before)
    {
        int lo = 0, hi = items.Length;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (before(items[mid])) lo = mid + 1; else hi = mid; }
        return lo - 1;
    }
    private DirectorServerReplayBinaryCodec.State Position(BinaryReader reader, int segment, long offset, double from)
    {
        int version = DirectorServerReplayBinaryCodec.ReadHeader(reader);
        var state = new DirectorServerReplayBinaryCodec.State { Version = version };
        if (checkpoints.TryGetValue(segment, out var points))
        {
            int i = Last(points, p => offset >= 0 ? p.RecordOffset <= offset : p.TimeSeconds < from);
            if (i >= 0)
            {
                var p = points[i];
                if (p.RecordOffset < reader.BaseStream.Position || p.RecordOffset >= reader.BaseStream.Length)
                    throw new InvalidDataException("Checkpoint offset outside segment.");
                reader.BaseStream.Position = p.RecordOffset;
                state = DirectorServerReplayBinaryCodec.CreateState(p.TimeUs, p.Xmm, p.Ymm, p.Zmm, p.Dimension, p.EntityId, p.PlayerUid);
                state.Version = version;
            }
        }
        return state;
    }
    private DirectorServerReplayRecord At(DirectorServerReplayChunkIndex index, CancellationToken token)
    {
        if (!segments.TryGetValue(index.Segment, out var segment)) throw new InvalidDataException("Indexed segment missing.");
        using var stream = File.OpenRead(Resolve(segment.BinaryFile));
        using var reader = new BinaryReader(stream);
        var state = Position(reader, segment.Index, index.RecordOffset, 0);
        while (stream.Position <= index.RecordOffset && stream.Position < stream.Length)
        {
            token.ThrowIfCancellationRequested();
            long offset = stream.Position;
            var item = DirectorServerReplayBinaryCodec.Read(reader, state); DecodedRecords++;
            if (offset == index.RecordOffset)
            {
                if (item.Kind != DirectorServerReplayRecordKind.ChunkBaseline || item.ChunkHash != index.Hash)
                    throw new InvalidDataException("Chunk index does not match segment.");
                return item;
            }
        }
        throw new InvalidDataException("Chunk offset does not address a record.");
    }
    private void ResolvePayload(DirectorServerReplayRecord item, CancellationToken token)
    {
        if (item.Kind != DirectorServerReplayRecordKind.ChunkBaseline || item.SolidRuns.Count > 0 || item.ChunkHash.Length == 0) return;
        DirectorServerReplayRecord? original = null;
        if (payloads.TryGetValue(item.ChunkHash, out var source)) original = At(source, token);
        else
        {
            // v1 indexes did not identify the owning payload.
            foreach (var candidate in Raw(0, double.PositiveInfinity, token))
                if (candidate.ChunkHash == item.ChunkHash && candidate.SolidRuns.Count > 0) { original = candidate; break; }
        }
        if (original is null || original.SolidRuns.Count == 0) throw new InvalidDataException("Shared chunk payload missing.");
        item.SolidRuns = original.SolidRuns; item.FluidRuns = original.FluidRuns;
    }
    private IEnumerable<DirectorServerReplayRecord> Raw(double from, double to, CancellationToken token)
    {
        foreach (var segment in Manifest.Segments.OrderBy(s => s.Index))
        {
            token.ThrowIfCancellationRequested();
            if (segment.ToSeconds > 0 && segment.ToSeconds < from || segment.FromSeconds > to) continue;
            if (!string.IsNullOrWhiteSpace(segment.BinaryFile))
            {
                using var stream = File.OpenRead(Resolve(segment.BinaryFile));
                using var reader = new BinaryReader(stream);
                var state = Position(reader, segment.Index, -1, from);
                while (stream.Position < stream.Length)
                {
                    token.ThrowIfCancellationRequested();
                    var item = DirectorServerReplayBinaryCodec.Read(reader, state); DecodedRecords++;
                    if (item.TimeSeconds >= from && item.TimeSeconds <= to) yield return item;
                }
            }
            else
            {
                using var file = File.OpenRead(Resolve(segment.File));
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip);
                while (reader.ReadLine() is { } line)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var item = JsonSerializer.Deserialize<DirectorServerReplayRecord>(line, DirectorServerJson.CompactOptions);
                    DecodedRecords++;
                    if (item is not null && item.TimeSeconds >= from && item.TimeSeconds <= to) yield return item;
                }
            }
        }
    }
    public IEnumerable<DirectorServerReplayRecord> Read(double fromSeconds = 0, double toSeconds = double.PositiveInfinity, CancellationToken cancellationToken = default)
    {
        foreach (var item in Raw(fromSeconds, toSeconds, cancellationToken))
        {
            ResolvePayload(item, cancellationToken); item.TimeSeconds -= fromSeconds; yield return item;
        }
    }
    public IEnumerable<DirectorServerReplayRecord> ReadChunk(int dimension, int x, int y, int z, double timeSeconds, CancellationToken cancellationToken = default)
    {
        if (chunks.TryGetValue((dimension, x, y, z), out var versions))
        {
            int i = Last(versions, item => item.TimeSeconds <= timeSeconds);
            if (i < 0) yield break;
            var item = At(versions[i], cancellationToken);
            ResolvePayload(item, cancellationToken);
            yield return item;
        }
        else
        {
            var item = Raw(0, timeSeconds, cancellationToken)
                .LastOrDefault(item => item.Kind == DirectorServerReplayRecordKind.ChunkBaseline && item.Dimension == dimension && item.ChunkX == x && item.ChunkY == y && item.ChunkZ == z);
            if (item is not null) { ResolvePayload(item, cancellationToken); yield return item; }
        }
    }
}
