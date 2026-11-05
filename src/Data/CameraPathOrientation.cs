namespace VSDirector.Data;

internal static class CameraPathOrientation
{
    public static float PreserveDegrees(double value, float fallback)
    {
        // PlayerCamera pitch is game-native and may be centered near 180 degrees.
        return double.IsFinite(value) && value >= -float.MaxValue && value <= float.MaxValue
            ? (float)value
            : fallback;
    }
}
