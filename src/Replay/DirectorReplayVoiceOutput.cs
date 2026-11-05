using VSDirector.Data;
using VSDirector.Recording;

namespace VSDirector.Replay;

/// <summary>Streams into the game's current OpenAL context without replacing
/// its device/listener or creating temporary game sound assets.</summary>
internal sealed class DirectorReplayVoiceOutput : IDirectorReplayVoiceOutput
{
    private readonly DirectorOpenAlNative al;
    private readonly HashSet<uint> buffers = [];
    private uint source;
    public DirectorReplayVoiceOutput(DirectorOpenAlNative al)
    {
        this.al = al;
        if (!al.HasCurrentContext) throw new InvalidOperationException("Replay voice has no active game audio context.");
        source = al.CreateStreamingSource(64, 6, 1);
        if (source == 0) throw new InvalidOperationException("Replay voice source allocation failed.");
    }
    private void Reclaim()
    {
        int processed = Math.Min(buffers.Count, al.GetProcessedBufferCount(source));
        while (processed-- > 0)
        {
            uint buffer = al.UnqueueSourceBuffer(source);
            if (buffers.Remove(buffer)) al.DeleteBuffer(buffer);
        }
    }
    public bool IsEmpty { get { Reclaim(); return buffers.Count == 0; } }
    public void Enqueue(byte[] pcm)
    {
        Reclaim();
        uint buffer = al.CreateBuffer(pcm, 1, 16, DirectorReplayVoiceFrame.SampleRate);
        if (buffer == 0) throw new InvalidOperationException("Replay voice buffer allocation failed.");
        buffers.Add(buffer);
        al.QueueSourceBuffer(source, buffer);
    }
    public void Update(DirectorReplayVoiceFrame frame, float gain, float pitch)
    {
        al.SetSourceRelative(source, !frame.Spatialized);
        al.SetSourcePosition(source, frame.Spatialized ? (float)frame.X : 0,
            frame.Spatialized ? (float)DirectorChunkWindow.ToAbsoluteY(frame.Y, frame.Dimension) : 0, frame.Spatialized ? (float)frame.Z : 0);
        al.ConfigureStreamingSource(source, frame.MaxDistance, frame.ReferenceDistance, frame.Spatialized ? frame.RolloffFactor : 0);
        al.SetSourceGain(source, Math.Clamp(gain, 0, 4)); al.SetSourcePitch(source, pitch);
    }
    public void SetPaused(bool paused)
    {
        int state = al.GetSourceState(source);
        if (paused && state == 4114) al.PauseSource(source);
        else if (!paused && buffers.Count > 0 && state != 4114) al.PlaySource(source);
    }
    public void Dispose()
    {
        if (source == 0) return;
        al.StopSource(source); al.DeleteSource(source); source = 0;
        foreach (uint buffer in buffers) al.DeleteBuffer(buffer);
        buffers.Clear();
    }
}
