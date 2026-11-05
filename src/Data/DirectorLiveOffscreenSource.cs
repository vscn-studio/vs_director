using System.Globalization;

namespace VSDirector.Data;

public readonly record struct DirectorLiveOffscreenSource(
    DirectorRecordingSourceType Type,
    Guid Id)
{
    public bool IsSupported => Type == DirectorRecordingSourceType.Program && Id == Guid.Empty
        || Id != Guid.Empty && Type is
            DirectorRecordingSourceType.StaticCamera
            or DirectorRecordingSourceType.Path
            or DirectorRecordingSourceType.Sequence
            or DirectorRecordingSourceType.Follow
            or DirectorRecordingSourceType.Workflow;

    public string Serialize()
        => ((int)Type).ToString(CultureInfo.InvariantCulture) + "|" + Id.ToString("D");

    public static bool TryParse(string? value, out DirectorLiveOffscreenSource source)
    {
        source = default;
        string[] parts = (value ?? string.Empty).Split('|', 2);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int typeValue)
            || !Enum.IsDefined(typeof(DirectorRecordingSourceType), typeValue)
            || !Guid.TryParse(parts[1], out Guid id))
        {
            return false;
        }

        source = new DirectorLiveOffscreenSource((DirectorRecordingSourceType)typeValue, id);
        return source.IsSupported;
    }
}
