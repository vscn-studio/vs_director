using VSDirector.Data;

namespace VSDirector;

internal readonly record struct DirectorFollowTarget(
    CameraFollowTargetType TargetType,
    string PlayerUid,
    long EntityId,
    string DisplayName)
{
    public string Selector => TargetType == CameraFollowTargetType.Player
        ? "player:" + PlayerUid
        : "entity:" + EntityId;
}
