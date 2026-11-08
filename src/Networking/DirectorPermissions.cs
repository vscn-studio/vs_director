namespace VSDirector.Networking;

[Flags]
public enum DirectorPermission : long
{
    None = 0,
    OffscreenCurrentView = 1L << 0,
    OffscreenSavedSources = 1L << 1,
    FreeCamera = 1L << 2,
    CameraLibrary = 1L << 3,
    PathLibrary = 1L << 4,
    CameraSequences = 1L << 5,
    FollowCameras = 1L << 6,
    ReplayCapture = 1L << 7,
    ReplayPlayback = 1L << 8,
    StandardRecording = 1L << 9,
    Workflows = 1L << 11,
    PresetPackages = 1L << 13,
    SharedCameras = 1L << 14,
    RemoteChunkAccess = 1L << 15,
    All = OffscreenCurrentView
        | OffscreenSavedSources
        | FreeCamera
        | CameraLibrary
        | PathLibrary
        | FollowCameras
        | ReplayCapture
        | ReplayPlayback
        | SharedCameras
        | RemoteChunkAccess
}

public sealed record DirectorPermissionDefinition(
    DirectorPermission Permission,
    string Code,
    string NameLanguageKey,
    string DescriptionLanguageKey);

public static class DirectorPermissions
{
    public const string AdminRoleCode = "admin";

    public static readonly DirectorPermission DefaultPlayerPermissions
        = DirectorPermission.OffscreenCurrentView;

    public static readonly IReadOnlyList<DirectorPermissionDefinition> Definitions =
        new[]
        {
            Define(DirectorPermission.OffscreenCurrentView, "offscreen-current"),
            Define(DirectorPermission.OffscreenSavedSources, "offscreen-sources"),
            Define(DirectorPermission.FreeCamera, "free-camera"),
            Define(DirectorPermission.CameraLibrary, "cameras"),
            Define(DirectorPermission.PathLibrary, "paths"),
            Define(DirectorPermission.FollowCameras, "follows"),
            Define(DirectorPermission.ReplayCapture, "replay-capture"),
            Define(DirectorPermission.ReplayPlayback, "replay-playback"),
            Define(DirectorPermission.SharedCameras, "shared-cameras"),
            Define(DirectorPermission.RemoteChunkAccess, "remote-chunks"),
        };

    public static bool IsAdminRole(string? roleCode)
        => string.Equals(roleCode?.Trim(), AdminRoleCode, StringComparison.OrdinalIgnoreCase);

    public static DirectorPermission Sanitize(long mask)
        => (DirectorPermission)mask & DirectorPermission.All;

    public static string PrivilegeCode(DirectorPermission permission)
        => Definitions.First(definition => definition.Permission == permission).Code;

    private static DirectorPermissionDefinition Define(
        DirectorPermission permission,
        string suffix)
        => new(
            permission,
            "vsdirector." + suffix,
            "vsdirector:permission-" + suffix,
            "vsdirector:permission-" + suffix + "-description");
}
