namespace VSDirector.Data;

/// <summary>
/// Converts Vintage Story's model-facing yaw to the native camera yaw used by
/// EntityPos.GetViewVector. The optional shape offset accounts for entity
/// shapes whose authored forward axis is rotated relative to normal entities.
/// </summary>
public static class DirectorEntityViewMath
{
    public static double ToCameraYaw(
        double modelYaw,
        double headYaw = 0d,
        double shapeYawOffset = 0d)
    {
        if (!double.IsFinite(modelYaw)
            || !double.IsFinite(headYaw)
            || !double.IsFinite(shapeYawOffset))
        {
            return double.NaN;
        }

        return modelYaw + headYaw + Math.PI + shapeYawOffset;
    }

    public static double EntityShapeYawOffset(string? entityCode)
    {
        if (string.IsNullOrWhiteSpace(entityCode))
        {
            return 0d;
        }

        string normalized = entityCode.Trim();
        int separator = normalized.IndexOf(':');
        if (separator >= 0 && separator + 1 < normalized.Length)
        {
            normalized = normalized[(separator + 1)..];
        }

        return (normalized.Equals("armorstand", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("armorstand-aged", StringComparison.OrdinalIgnoreCase))
            || normalized.Equals("strawdummy", StringComparison.OrdinalIgnoreCase)
            ? Math.PI / 2d
            : 0d;
    }

    public static double ToPlayerRelativePitch(double nativePitch)
    {
        if (!double.IsFinite(nativePitch))
        {
            return double.NaN;
        }

        // Player pitch is PI-centered (PI is horizontal), while camera
        // follow poses use a zero-centered angle where positive is upward.
        return Math.PI - nativePitch;
    }
}
