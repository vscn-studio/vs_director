using System.Buffers.Binary;
using VSDirector.Data;

internal static class TimelineMediaChecks
{
    internal static void Run()
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var rows = new DirectorTimelineRows([40, 100, 60]);
        Check(rows.At(-1) == -1 && rows.At(39) == 0 && rows.At(40) == 1 && rows.At(139) == 1
            && rows.At(140) == 2 && rows.At(200) == 3, "Row boundaries disagree with hit testing.");
        Check(rows.Top(2) - rows.Top(1) == 100 && rows.MaxFirst(160) == 1, "Scrolled variable row geometry differs.");
        Guid id = Guid.NewGuid();
        var layout = DirectorWorkspaceDocument.Normalize(new() { TimelineRowHeights = new() { [id] = 100 } });
        var copy = layout.DeepClone(); copy.TimelineRowHeights[id] = 80;
        Check(layout.TimelineRowHeights[id] == 100 && new DirectorTimelineRows([double.NaN, 999]).Top(2) == 200, "Row heights do not normalize or clone.");
        byte[] pcm = new byte[48000 * 2];
        for (int i = 0; i < 48000; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), i < 24000 ? (short)-16384 : (short)8192);
        var frames = new DirectorReplayList<DirectorReplayVoiceFrame>([
            new() { SpeakerId = "a", TimeSeconds = 10, Pcm16 = pcm },
            new() { SpeakerId = "b", TimeSeconds = 10, Pcm16 = pcm, Volume = .5f }
        ]);
        var wave = DirectorVoiceWaveform.Read(frames, new HashSet<string> { "a", "b" }, 10, 12, 4);
        Check(wave["a"].SequenceEqual(new float[] { .5f, .25f, 0, 0 })
            && wave["b"].SequenceEqual(new float[] { .25f, .125f, 0, 0 }), "Waveform lost speaker separation, amplitude or silence.");
        var clip = DirectorVoiceWaveform.Read(frames, new HashSet<string> { "a" }, 10.5, 11, 1);
        Check(clip["a"][0] == .25f, "Visible-window clipping includes samples before the window.");
        int reads = 0;
        var paged = new DirectorReplayList<DirectorReplayVoiceFrame>(100000, i =>
        {
            reads++;
            if (i < 49999 || i >= 50001) throw new Exception("Waveform read unrelated PCM.");
            return new() { SpeakerId = "a", TimeSeconds = i, Pcm16 = pcm };
        }, (t, inclusive) => (int)Math.Clamp(inclusive ? Math.Floor(t) + 1 : Math.Ceiling(t), 0, 100000));
        DirectorVoiceWaveform.Read(paged, new HashSet<string> { "a" }, 50000, 50001, 32);
        Check(reads == 2, "Waveform did not use the timestamp index.");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try { DirectorVoiceWaveform.Read(frames, new HashSet<string> { "a" }, 10, 11, 2, cts.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
    }
}
