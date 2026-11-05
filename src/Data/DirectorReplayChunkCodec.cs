namespace VSDirector.Data;

public static class DirectorReplayChunkCodec
{
    public static List<DirectorReplayBlockRun> Encode(ReadOnlySpan<int> blockIds)
    {
        List<DirectorReplayBlockRun> runs = new();
        if (blockIds.Length == 0)
        {
            return runs;
        }

        int current = Math.Max(0, blockIds[0]);
        int length = 1;
        for (int index = 1; index < blockIds.Length; index++)
        {
            int blockId = Math.Max(0, blockIds[index]);
            if (blockId == current && length < int.MaxValue)
            {
                length++;
                continue;
            }

            runs.Add(new DirectorReplayBlockRun { BlockId = Math.Max(0, current), Length = length });
            current = blockId;
            length = 1;
        }

        runs.Add(new DirectorReplayBlockRun { BlockId = Math.Max(0, current), Length = length });
        return runs;
    }

    public static int[] Decode(IEnumerable<DirectorReplayBlockRun>? runs, int expectedLength)
    {
        int length = Math.Max(0, expectedLength);
        int[] result = new int[length];
        DecodeInto(runs, result);
        return result;
    }

    public static void DecodeInto(
        IEnumerable<DirectorReplayBlockRun>? runs,
        Span<int> destination)
    {
        destination.Clear();
        if (runs is null || destination.Length == 0)
        {
            return;
        }

        int offset = 0;
        foreach (DirectorReplayBlockRun? run in runs)
        {
            if (run is null || run.Length <= 0 || offset >= destination.Length)
            {
                continue;
            }

            int count = Math.Min(run.Length, destination.Length - offset);
            destination.Slice(offset, count).Fill(Math.Max(0, run.BlockId));
            offset += count;
        }
    }

    public static List<DirectorReplayBlockRun> Normalize(
        IEnumerable<DirectorReplayBlockRun>? runs,
        int expectedLength)
        => Encode(Decode(runs, expectedLength));
}
