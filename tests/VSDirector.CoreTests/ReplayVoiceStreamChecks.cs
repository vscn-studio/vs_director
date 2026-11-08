using VSDirector.Data;

internal static class ReplayVoiceStreamChecks
{
    public static void Run()
    {
        var packets = Enumerable.Range(0, 100).Select(i => new DirectorReplayVoiceFrame
        { SourceId = "speaker", SpeakerId = "p", TimeSeconds = i * .02, Pcm16 = Enumerable.Repeat((byte)(i + 1), 1920).ToArray() }).ToArray();
        var outputs = new List<FakeOutput>();
        using var stream = new DirectorReplayVoiceStream(packets, () => { var output = new FakeOutput(); outputs.Add(output); return output; });
        // 10 fps must play every 20 ms packet, not only the newest packet.
        for (int frame = 0; frame < 20; frame++) stream.Update(frame * .1, 1, false, _ => 1);
        Check(outputs.Count == 1, "Voice allocated a source for every packet.");
        Check(outputs[0].Bytes.SequenceEqual(packets.SelectMany(f => f.Pcm16)), "Low-FPS playback discarded or duplicated voice samples.");
        int queued = outputs[0].Bytes.Count;
        stream.SetPaused(true);
        stream.Update(1.9, 1, true, _ => 1);
        Check(outputs[0].Paused && outputs[0].Bytes.Count == queued, "Paused voice advanced its queue.");
        stream.Update(.25, 1, false, _ => 1);
        Check(outputs.Count == 2 && outputs[0].Disposed, "Backward seek did not dispose old audio.");
        Check(outputs[1].Bytes.Take(960).All(b => b == 13), "Seek did not trim the partially elapsed packet sample-accurately.");

        var gapPackets = new[] {
            new DirectorReplayVoiceFrame { SourceId = "gap", TimeSeconds = 0, Pcm16 = Enumerable.Repeat((byte)1, 1920).ToArray() },
            new DirectorReplayVoiceFrame { SourceId = "gap", TimeSeconds = .04, Pcm16 = Enumerable.Repeat((byte)2, 1920).ToArray() }
        };
        var gapOutput = new FakeOutput();
        using var gap = new DirectorReplayVoiceStream(gapPackets, () => gapOutput);
        gap.Update(0, 1, false, _ => 1);
        Check(gapOutput.Bytes.Count == 5760 && gapOutput.Bytes.Skip(1920).Take(1920).All(b => b == 0), "Recorded silence was collapsed.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class FakeOutput : IDirectorReplayVoiceOutput
    {
        public List<byte> Bytes = new();
        public bool Paused, Disposed;
        public bool IsEmpty => Bytes.Count == 0;
        public void Enqueue(byte[] pcm) => Bytes.AddRange(pcm);
        public void Update(DirectorReplayVoiceFrame frame, float gain, float pitch) { }
        public void SetPaused(bool paused) => Paused = paused;
        public void Dispose() => Disposed = true;
    }
}
