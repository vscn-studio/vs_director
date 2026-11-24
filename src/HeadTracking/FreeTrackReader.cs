// FreeTrack layout/source: VS-6DOF, CC0-1.0 (licenses/VS-6DOF-CC0.txt).
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace VSDirector.HeadTracking;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FTData
{
    public uint DataID;
    public int CamWidth, CamHeight;
    public float Yaw, Pitch, Roll, X, Y, Z;
    public readonly bool IsFinite => float.IsFinite(Yaw) && float.IsFinite(Pitch) && float.IsFinite(Roll)
        && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
}

internal sealed class FreeTrackReader : IDisposable
{
    private MemoryMappedFile? memory;
    private MemoryMappedViewAccessor? view;
    private long lastOpen, lastPacket;
    private uint packet;
    private bool received;
    private readonly string mappingName;
    internal FreeTrackReader(string mappingName = "FT_SharedMem") => this.mappingName = mappingName;
    public bool TryRead(out FTData data)
    {
        data = default;
        if (!OperatingSystem.IsWindows()) return false;
        long now = Stopwatch.GetTimestamp();
        try
        {
            if (view is null)
            {
                if (lastOpen != 0 && Stopwatch.GetElapsedTime(lastOpen, now).TotalSeconds < 1) return false;
                lastOpen = now;
                memory = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.Read);
                view = memory.CreateViewAccessor(0, Marshal.SizeOf<FTData>(), MemoryMappedFileAccess.Read);
            }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint before = view.ReadUInt32(0);
                view.Read(0, out data);
                if (data.DataID != before || before != view.ReadUInt32(0)) continue;
                if (!data.IsFinite || (data.DataID == 0 && data.CamWidth == 0 && data.CamHeight == 0)) return false;
                if (!received || packet != data.DataID) { packet = data.DataID; lastPacket = now; received = true; }
                if (Stopwatch.GetElapsedTime(lastPacket, now).TotalSeconds <= 1) return true;
                Dispose(); // A restarted tracker may have created a new mapping.
                return false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        { Dispose(); }
        return false;
    }
    public void Dispose()
    {
        view?.Dispose(); view = null;
        memory?.Dispose(); memory = null;
    }
}
