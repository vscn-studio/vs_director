using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VSDirector.Data;

internal static class ServerReplayCompanionChecks
{
    public static void Run()
    {
        BinaryDeltaRoundTrip();
        LazyReaderUsesChunkIndex();
        string root = Path.Combine(Path.GetTempPath(), "server-replay-companion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Guid session = Guid.NewGuid();
            string segment = "server-0000.jsonl.gz";
            string manifest = "server.manifest.json";
            var records = new[]
            {
                new DirectorServerReplayRecord { Kind = DirectorServerReplayRecordKind.PlayerSample, TimeSeconds = 0, PlayerUid = "p1", PlayerName = "One", EntityId = 7, EntityCode = "game:player", X = 10, Y = 20, Z = 30, Yaw = 1 },
                new DirectorServerReplayRecord { Kind = DirectorServerReplayRecordKind.PlayerSample, TimeSeconds = 1, PlayerUid = "p1", PlayerName = "One", EntityId = 7, EntityCode = "game:player", X = 11, Y = 20, Z = 30, Yaw = 2 },
                new DirectorServerReplayRecord { Kind = DirectorServerReplayRecordKind.BlockPlace, TimeSeconds = 1, X = 4, Y = 5, Z = 6, SolidBlockId = 12 },
                new DirectorServerReplayRecord { Kind = DirectorServerReplayRecordKind.ChunkBaseline, TimeSeconds = 0, Dimension = 0, ChunkX = 0, ChunkY = 0, ChunkZ = 0,
                    SolidRuns = [new() { BlockId = 1, Length = 32768 }], FluidRuns = [new() { BlockId = 0, Length = 32768 }] },
                new DirectorServerReplayRecord { Kind = DirectorServerReplayRecordKind.ChunkBaseline, TimeSeconds = 2, Dimension = 0, ChunkX = 0, ChunkY = 0, ChunkZ = 0,
                    SolidRuns = [new() { BlockId = 2, Length = 1 }, new() { BlockId = 1, Length = 32767 }], FluidRuns = [new() { Length = 32768 }] }
            };
            using (var file = File.Create(Path.Combine(root, segment)))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
                foreach (var record in records) writer.WriteLine(JsonSerializer.Serialize(record, DirectorServerJson.CompactOptions));
            DirectorServerJson.Save(Path.Combine(root, manifest), new DirectorServerReplayManifest
            {
                SessionId = session, WorldId = "world", Status = "completed", DurationSeconds = 2,
                Segments = [new() { Index = 0, File = segment, FromSeconds = 0, ToSeconds = 2, Records = records.Length }]
            });
            string replay = Path.Combine(root, "client.vsdreplay.gz");
            DirectorReplayDocument document = new()
            {
                WorldId = "world", DurationSeconds = 2,
                ServerReplay = new() { SessionId = session, ManifestFile = manifest, Available = true }
            };
            DirectorReplayIO.Save(replay, document);
            Check(DirectorServerReplayCompanion.TryMergeAdjacent(document, replay), "server companion was not found");
            Check(document.Actors.Count == 1 && document.Actors[0].Samples.Count == 2, "server player samples were not materialized");
            Check(document.Actors[0].Samples[1].X == 11 && document.BlockEvents.First().SolidBlockId == 12, "server observations were not merged");
            Check(document.WorldSnapshot.Chunks.Single().CapturedAtSeconds == 0 && document.WorldSnapshot.Chunks.Single().SolidRuns.Single().BlockId == 1,
                "Later chunk version replaced the recording-start baseline");
            Check(document.BlockEvents.Any(b => b.TimeSeconds == 2 && b.X == 0 && b.Y == 0 && b.Z == 0 && b.SolidBlockId == 2)
                && document.BlockEvents.Any(b => b.TimeSeconds == 2 && b.X == 4 && b.Y == 5 && b.Z == 6 && b.SolidBlockId == 1),
                "Later baseline did not restore the chronological block state");
            Check(document.IncludesWorldSnapshot && document.WorldSnapshot.Chunks.Count == 1, "server chunk baseline was not materialized");
            Check(document.CaptureScope == "server-authoritative-plus-client-witness", "hybrid capture scope was not marked");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void BinaryDeltaRoundTrip()
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            DirectorServerReplayBinaryCodec.WriteHeader(writer);
            var state = new DirectorServerReplayBinaryCodec.State();
            DirectorServerReplayBinaryCodec.Write(writer, new DirectorServerReplayRecord { Kind = DirectorServerReplayRecordKind.EntitySample, TimeSeconds = 1.25, EntityId = 4, X = 12.5, Y = 2, Z = -3.5, Dimension = 2, EntityCode = "game:chicken" }, state);
            DirectorServerReplayBinaryCodec.Write(writer, new DirectorServerReplayRecord { Kind = DirectorServerReplayRecordKind.EntitySample, TimeSeconds = 1.35, EntityId = 4, X = 12.6, Y = 2, Z = -3.5, Dimension = 2, EntityCode = "game:chicken" }, state);
        }
        stream.Position = 0;
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
        DirectorServerReplayBinaryCodec.ReadHeader(reader);
        var readState = new DirectorServerReplayBinaryCodec.State();
        var first = DirectorServerReplayBinaryCodec.Read(reader, readState);
        var second = DirectorServerReplayBinaryCodec.Read(reader, readState);
        Check(first.EntityCode == "game:chicken" && Math.Abs(second.X - 12.6) < 0.001 && second.TimeSeconds > first.TimeSeconds, "binary delta stream did not round trip");
    }

    private static void LazyReaderUsesChunkIndex()
    {
        string root = Path.Combine(Path.GetTempPath(), "server-replay-lazy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string binary = "segment.vsdseg";
            long offset;
            using (FileStream stream = File.Create(Path.Combine(root, binary)))
            using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            {
                DirectorServerReplayBinaryCodec.WriteHeader(writer);
                var state = new DirectorServerReplayBinaryCodec.State();
                offset = DirectorServerReplayBinaryCodec.Write(writer, new DirectorServerReplayRecord
                {
                    Kind = DirectorServerReplayRecordKind.ChunkBaseline, TimeSeconds = 4, ChunkX = 2, ChunkY = 0, ChunkZ = 3,
                    ChunkHash = "h", SolidRuns = [new() { BlockId = 1, Length = 32768 }], FluidRuns = [new() { Length = 32768 }]
                }, state);
            }
            DirectorServerJson.Save(Path.Combine(root, "manifest.json"), new DirectorServerReplayManifest
            {
                StorageFormat = "binary-delta-v1", Segments = [new() { Index = 0, BinaryFile = binary, FromSeconds = 0, ToSeconds = 4 }],
                Chunks = [new() { Dimension = 0, ChunkX = 2, ChunkY = 0, ChunkZ = 3, TimeSeconds = 4, Segment = 0, RecordOffset = offset, Hash = "h" }]
            });
            var reader = new DirectorServerReplayLazyReader(Path.Combine(root, "manifest.json"));
            Check(reader.ReadChunk(0, 2, 0, 3, 5).Single().ChunkHash == "h", "lazy chunk index did not seek to the indexed record");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
