namespace VSDirector.Data;

public static class QuickSlots
{
    public const int First = 1;
    public const int Last = 10;
    public const int Count = Last - First + 1;

    public static bool IsValid(int slot) => slot is >= First and <= Last;

    public static int KeyboardDigit(int slot)
    {
        if (!IsValid(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return slot == Last ? 0 : slot;
    }
}
