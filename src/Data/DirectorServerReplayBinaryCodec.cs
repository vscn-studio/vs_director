using System.Text;

namespace VSDirector.Data;

/// <summary>
/// Compact seekable server segment format. Numeric values are delta encoded
/// against the previous record; a segment can therefore be read incrementally
/// without deserializing the complete replay document.
/// </summary>
public static class DirectorServerReplayBinaryCodec
{
    private const uint Magic = 0x31534456; // VSD1

    public sealed class State
    {
        public int Version { get; set; } = 2;
        internal long TimeUs;
        internal long Xmm, Ymm, Zmm;
        internal int Dimension;
        internal long EntityId;
        internal string PlayerUid = string.Empty;
        public long CurrentTimeUs => TimeUs;
        public long CurrentXmm => Xmm;
        public long CurrentYmm => Ymm;
        public long CurrentZmm => Zmm;
        public int CurrentDimension => Dimension;
        public long CurrentEntityId => EntityId;
        public string CurrentPlayerUid => PlayerUid;
    }

    public static State CreateState(long timeUs, long xmm, long ymm, long zmm, int dimension, long entityId, string playerUid)
        => new() { TimeUs = timeUs, Xmm = xmm, Ymm = ymm, Zmm = zmm, Dimension = dimension, EntityId = entityId, PlayerUid = playerUid ?? string.Empty };

    public static void WriteHeader(BinaryWriter writer)
    {
        writer.Write(Magic);
        writer.Write((byte)2);
    }

    public static int ReadHeader(BinaryReader reader)
    {
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Invalid VS Director server binary segment.");
        int version = reader.ReadByte();
        if (version is not (1 or 2)) throw new InvalidDataException("Unsupported server segment version.");
        return version;
    }

    public static long Write(BinaryWriter writer, DirectorServerReplayRecord value, State state)
    {
        long offset = writer.BaseStream.Position;
        writer.Write((byte)value.Kind);
        WriteVarInt(writer, (long)Math.Round(value.TimeSeconds * 1_000_000d) - state.TimeUs);
        state.TimeUs = Math.Max(0, (long)Math.Round(value.TimeSeconds * 1_000_000d));
        long x = BitConverter.DoubleToInt64Bits(value.X), y = BitConverter.DoubleToInt64Bits(value.Y), z = BitConverter.DoubleToInt64Bits(value.Z);
        WriteVarUInt(writer, (ulong)(x ^ state.Xmm)); WriteVarUInt(writer, (ulong)(y ^ state.Ymm)); WriteVarUInt(writer, (ulong)(z ^ state.Zmm));
        state.Xmm = x; state.Ymm = y; state.Zmm = z;
        WriteVarInt(writer, value.Dimension - state.Dimension); state.Dimension = value.Dimension;
        WriteVarInt(writer, value.EntityId - state.EntityId); state.EntityId = value.EntityId;
        WriteString(writer, value.PlayerUid, state.PlayerUid); state.PlayerUid = value.PlayerUid ?? string.Empty;
        WriteString(writer, value.PlayerName, string.Empty);
        WriteString(writer, value.EntityCode, string.Empty);
        writer.Write(value.Yaw); writer.Write(value.Pitch); writer.Write(value.Roll);
        writer.Write(value.OldBlockId); writer.Write(value.NewBlockId); writer.Write(value.SolidBlockId); writer.Write(value.FluidBlockId);
        WriteVarInt(writer, value.ChunkX); WriteVarInt(writer, value.ChunkY); WriteVarInt(writer, value.ChunkZ);
        writer.Write(value.ChunkVersion); WriteString(writer, value.ChunkHash, string.Empty);
        WriteRuns(writer, value.SolidRuns); WriteRuns(writer, value.FluidRuns);
        writer.Write(value.BlockEntityClass ?? string.Empty); WriteBytes(writer, value.BlockEntityData);
        writer.Write(value.BlockEntities?.Count ?? 0);
        foreach (var entity in value.BlockEntities ?? []) { writer.Write(entity.X); writer.Write(entity.Y); writer.Write(entity.Z); writer.Write(entity.Dimension); writer.Write(entity.ClassName ?? string.Empty); WriteBytes(writer, entity.Data); }
        return offset;
    }

    public static DirectorServerReplayRecord Read(BinaryReader reader, State state)
    {
        var value = new DirectorServerReplayRecord { Kind = (DirectorServerReplayRecordKind)reader.ReadByte() };
        state.TimeUs += state.Version == 1 ? (long)ReadVarUInt(reader) : ReadVarInt(reader); value.TimeSeconds = state.TimeUs / 1_000_000d;
        if (state.Version == 1)
        {
            state.Xmm += ReadVarInt(reader); state.Ymm += ReadVarInt(reader); state.Zmm += ReadVarInt(reader);
            value.X = state.Xmm / 1000d; value.Y = state.Ymm / 1000d; value.Z = state.Zmm / 1000d;
        }
        else
        {
            state.Xmm ^= (long)ReadVarUInt(reader); state.Ymm ^= (long)ReadVarUInt(reader); state.Zmm ^= (long)ReadVarUInt(reader);
            value.X = BitConverter.Int64BitsToDouble(state.Xmm); value.Y = BitConverter.Int64BitsToDouble(state.Ymm); value.Z = BitConverter.Int64BitsToDouble(state.Zmm);
        }
        state.Dimension += checked((int)ReadVarInt(reader)); value.Dimension = state.Dimension;
        state.EntityId += ReadVarInt(reader); value.EntityId = state.EntityId;
        state.PlayerUid = ReadString(reader, state.PlayerUid); value.PlayerUid = state.PlayerUid;
        value.PlayerName = ReadString(reader, string.Empty); value.EntityCode = ReadString(reader, string.Empty);
        value.Yaw = reader.ReadSingle(); value.Pitch = reader.ReadSingle(); value.Roll = reader.ReadSingle();
        value.OldBlockId = reader.ReadInt32(); value.NewBlockId = reader.ReadInt32(); value.SolidBlockId = reader.ReadInt32(); value.FluidBlockId = reader.ReadInt32();
        value.ChunkX = (int)ReadVarInt(reader); value.ChunkY = (int)ReadVarInt(reader); value.ChunkZ = (int)ReadVarInt(reader);
        value.ChunkVersion = reader.ReadInt64(); value.ChunkHash = ReadString(reader, string.Empty);
        value.SolidRuns = ReadRuns(reader); value.FluidRuns = ReadRuns(reader);
        value.BlockEntityClass = reader.ReadString(); value.BlockEntityData = ReadBytes(reader);
        int count = ReadCount(reader, 32768); for (int i = 0; i < count; i++) value.BlockEntities.Add(new() { X = reader.ReadInt32(), Y = reader.ReadInt32(), Z = reader.ReadInt32(), Dimension = reader.ReadInt32(), ClassName = reader.ReadString(), Data = ReadBytes(reader) });
        return value;
    }

    private static void WriteRuns(BinaryWriter w, List<DirectorReplayBlockRun>? runs) { w.Write(runs?.Count ?? 0); foreach (var run in runs ?? []) { WriteVarInt(w, run.BlockId); WriteVarInt(w, run.Length); } }
    private static int ReadCount(BinaryReader r, int max) { int n = r.ReadInt32(); return n >= 0 && n <= max ? n : throw new InvalidDataException("Invalid record count."); }
    private static List<DirectorReplayBlockRun> ReadRuns(BinaryReader r) { int count = ReadCount(r, 32768); var runs = new List<DirectorReplayBlockRun>(count); for (int i = 0; i < count; i++) runs.Add(new() { BlockId = (int)ReadVarInt(r), Length = (int)ReadVarInt(r) }); return runs; }
    private static void WriteBytes(BinaryWriter w, byte[]? data) { data ??= []; w.Write(data.Length); w.Write(data); }
    private static byte[] ReadBytes(BinaryReader r) { int n = ReadCount(r, 64 * 1024 * 1024); byte[] bytes = r.ReadBytes(n); return bytes.Length == n ? bytes : throw new EndOfStreamException(); }
    private static void WriteString(BinaryWriter w, string? value, string previous) { value ??= string.Empty; w.Write(value == previous); if (value != previous) w.Write(value); }
    private static string ReadString(BinaryReader r, string previous) => r.ReadBoolean() ? previous : r.ReadString();
    private static void WriteVarUInt(BinaryWriter w, ulong value) { while (value >= 0x80) { w.Write((byte)(value | 0x80)); value >>= 7; } w.Write((byte)value); }
    private static ulong ReadVarUInt(BinaryReader r)
    {
        ulong result = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            byte b = r.ReadByte();
            if (shift == 63 && b > 1) throw new InvalidDataException("Invalid varint.");
            result |= (ulong)(b & 127) << shift;
            if ((b & 128) == 0) return result;
        }
        throw new InvalidDataException("Invalid varint.");
    }
    private static void WriteVarInt(BinaryWriter w, long value) => WriteVarUInt(w, (ulong)((value << 1) ^ (value >> 63)));
    private static long ReadVarInt(BinaryReader r) { ulong value = ReadVarUInt(r); return (long)(value >> 1) ^ -((long)value & 1); }
}
