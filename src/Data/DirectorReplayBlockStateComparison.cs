namespace VSDirector.Data;

internal static class DirectorReplayBlockStateComparison
{
    public static bool IsEquivalent(
        int expectedSolidBlockId,
        int expectedFluidBlockId,
        bool expectedBlockEntityKnown,
        string? expectedBlockEntityClass,
        byte[]? expectedBlockEntityData,
        int currentSolidBlockId,
        int currentFluidBlockId,
        string? currentBlockEntityClass,
        byte[]? currentBlockEntityData)
    {
        if (expectedSolidBlockId != currentSolidBlockId
            || expectedFluidBlockId != currentFluidBlockId
            || !expectedBlockEntityKnown)
        {
            return false;
        }

        return string.Equals(
                expectedBlockEntityClass ?? string.Empty,
                currentBlockEntityClass ?? string.Empty,
                StringComparison.Ordinal)
            && (expectedBlockEntityData ?? Array.Empty<byte>()).AsSpan().SequenceEqual(
                currentBlockEntityData ?? Array.Empty<byte>());
    }
}
