namespace VSDirector;

public sealed class DirectorServerConfig
{
    public int MaxAnchorRadiusChunks { get; set; } = 4;

    public int AnchorTimeoutSeconds { get; set; } = 5;

    public int ResendIntervalMs { get; set; } = 1000;

    public bool AllowCrossDimensionAnchors { get; set; } = true;
}
