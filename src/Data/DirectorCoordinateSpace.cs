namespace VSDirector.Data;

public static class DirectorCoordinateSpace
{
    public static double ToDisplayHorizontal(double absoluteCoordinate, int mapSize)
    {
        if (!double.IsFinite(absoluteCoordinate) || mapSize <= 0)
        {
            return double.IsFinite(absoluteCoordinate) ? absoluteCoordinate : 0d;
        }

        return absoluteCoordinate - mapSize / 2d;
    }

    public static double ToAbsoluteHorizontal(double displayCoordinate, int mapSize)
    {
        if (!double.IsFinite(displayCoordinate) || mapSize <= 0)
        {
            return double.IsFinite(displayCoordinate) ? displayCoordinate : 0d;
        }

        return displayCoordinate + mapSize / 2d;
    }

    public static (double X, double Y, double Z) ToDisplay(
        double absoluteX,
        double y,
        double absoluteZ,
        int mapSizeX,
        int mapSizeZ)
        => (
            ToDisplayHorizontal(absoluteX, mapSizeX),
            double.IsFinite(y) ? y : 0d,
            ToDisplayHorizontal(absoluteZ, mapSizeZ));

    public static (double X, double Y, double Z) ToAbsolute(
        double displayX,
        double y,
        double displayZ,
        int mapSizeX,
        int mapSizeZ)
        => (
            ToAbsoluteHorizontal(displayX, mapSizeX),
            double.IsFinite(y) ? y : 0d,
            ToAbsoluteHorizontal(displayZ, mapSizeZ));
}
