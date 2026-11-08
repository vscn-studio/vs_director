using VSDirector.Data;

internal static class ServerIndexChecks
{
    internal static void Run()
    {
        using (var memory = new MemoryStream())
        {
            using (var w = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
            {
                // Frozen v1 record layout, independent of the current encoder.
                w.Write(0x31534456u); w.Write((byte)1); w.Write((byte)DirectorServerReplayRecordKind.EntitySample);
                w.Write7BitEncodedInt(1250000); w.Write7BitEncodedInt(5000); w.Write((byte)0); w.Write((byte)0);
                w.Write((byte)0); w.Write((byte)0); w.Write(true); w.Write(true); w.Write(false); w.Write("game:chicken");
                w.Write(0f); w.Write(0f); w.Write(0f);
                for (int i = 0; i < 4; i++) w.Write(0);
                w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write(0L); w.Write(true);
                w.Write(0); w.Write(0); w.Write(""); w.Write(0); w.Write(0);
            }
            memory.Position = 0; using var r = new BinaryReader(memory);
            var legacy = DirectorServerReplayBinaryCodec.Read(r, new() { Version = DirectorServerReplayBinaryCodec.ReadHeader(r) });
            Check(legacy.TimeSeconds == 1.25 && legacy.X == 2.5 && legacy.EntityCode == "game:chicken", "v1 binary compatibility regressed.");
        }
        string folder = Path.Combine(Path.GetTempPath(), "director-server-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var manifest = new DirectorServerReplayManifest();
            for (int segment = 0; segment < 2; segment++)
            {
                string filename = segment + ".vsdseg";
                using var file = File.Create(Path.Combine(folder, filename));
                using var writer = new BinaryWriter(file);
                DirectorServerReplayBinaryCodec.WriteHeader(writer);
                var state = new DirectorServerReplayBinaryCodec.State();
                for (int i = 0; i < 600; i++)
                {
                    bool baseline = i == 599;
                    double time = segment * 1000 + i;
                    long offset = file.Position;
                    if (i % 256 == 0 || baseline) manifest.Checkpoints.Add(new()
                    {
                        Segment = segment, RecordOffset = offset, TimeSeconds = time, TimeUs = state.CurrentTimeUs,
                        Xmm = state.CurrentXmm, Ymm = state.CurrentYmm, Zmm = state.CurrentZmm,
                        EntityId = state.CurrentEntityId, Dimension = state.CurrentDimension, PlayerUid = state.CurrentPlayerUid
                    });
                    var record = new DirectorServerReplayRecord { Kind = baseline ? DirectorServerReplayRecordKind.ChunkBaseline : DirectorServerReplayRecordKind.EntitySample,
                        TimeSeconds = time, X = -100.123456789123, Y = -0d, Z = double.Epsilon,
                        EntityId = long.MaxValue, Dimension = segment, ChunkX = 3, ChunkY = 2, ChunkZ = 1,
                        ChunkHash = baseline ? "same" : "", ChunkVersion = segment + 1 };
                    if (baseline && segment == 0) { record.SolidRuns = [new() { BlockId = 7, Length = 32768 }]; record.FluidRuns = [new() { Length = 32768 }]; }
                    DirectorServerReplayBinaryCodec.Write(writer, record, state);
                    if (baseline) manifest.Chunks.Add(new() { Segment = segment, RecordOffset = offset, Dimension = segment, ChunkX = 3, ChunkY = 2, ChunkZ = 1, Hash = "same", TimeSeconds = time, Version = segment + 1, HasPayload = segment == 0 });
                }
                manifest.Segments.Add(new() { Index = segment, BinaryFile = filename, FromSeconds = segment * 1000, ToSeconds = segment * 1000 + 599 });
            }
            string path = Path.Combine(folder, "manifest.json"); DirectorServerJson.Save(path, manifest);
            var reader = new DirectorServerReplayLazyReader(path);
            var chunk = reader.ReadChunk(1, 3, 2, 1, 2000).Single();
            Check(chunk.SolidRuns.Single().BlockId == 7 && chunk.Dimension == 1, "Cross-segment payload reference failed.");
            Check(reader.DecodedRecords == 2, "Chunk seek scanned history instead of direct checkpoints.");
            Check(!reader.ReadChunk(1, 3, 2, 1, 1598).Any(), "Future baseline leaked before capture.");
            var samples = reader.Read(1512, 1513).ToArray();
            Check(samples.Length == 2 && samples[0].TimeSeconds == 0 && samples[1].TimeSeconds == 1, "Boundary records lost at checkpoint.");
            Check(BitConverter.DoubleToInt64Bits(samples[0].X) == BitConverter.DoubleToInt64Bits(-100.123456789123)
                && BitConverter.DoubleToInt64Bits(samples[0].Y) == BitConverter.DoubleToInt64Bits(-0d)
                && samples[0].Z == double.Epsilon, "Binary delta lost floating-point bits.");
            Check(reader.DecodedRecords < 350, "Time seek scanned unrelated segments.");
        }
        finally { Directory.Delete(folder, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
