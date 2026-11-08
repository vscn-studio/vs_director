namespace VSDirector.Data;

public enum DirectorReplayCameraMode
{
    None,
    Free,
    Fixed,
    Path,
    SyncedPath,
    Sequence,
    Follow
}

public enum DirectorReplayCameraAuthority
{
    Timeline,
    ManualFree
}

public readonly record struct DirectorReplayCameraState(
    DirectorReplayCameraMode Mode,
    Guid SourceId)
{
    public static DirectorReplayCameraState Inactive => new(DirectorReplayCameraMode.None, Guid.Empty);

    public static DirectorReplayCameraState Free => new(DirectorReplayCameraMode.Free, Guid.Empty);

    public static DirectorReplayCameraState FromSource(DirectorReplayCameraMode mode, Guid sourceId)
    {
        if (mode is DirectorReplayCameraMode.None or DirectorReplayCameraMode.Free)
        {
            return mode == DirectorReplayCameraMode.Free ? Free : Inactive;
        }

        return new DirectorReplayCameraState(mode, sourceId);
    }

    public bool Is(DirectorReplayCameraMode mode, Guid sourceId)
        => Mode == mode && SourceId == sourceId;
}
