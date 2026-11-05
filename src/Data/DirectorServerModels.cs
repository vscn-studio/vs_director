using System.Text.Json;

namespace VSDirector.Data;

/// <summary>Legacy capture metadata; coordinated recording is no longer supported.</summary>
public sealed class DirectorServerRecordingConfig
{
    public int RadiusChunks { get; set; } = 4;
    public int VolumeSeconds { get; set; } = 300;
    public int SampleRate { get; set; } = 30;
    public int EntityStateRate { get; set; } = 2;
    public int MaxActors { get; set; } = 256;
    public bool DiagnosticsEnabled { get; set; }
    public bool CaptureParticles { get; set; } = true;
    public bool CaptureSounds { get; set; } = true;
    public bool CaptureVoice { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool IncludeAdministrators { get; set; }
    public bool AllowPlayerDecline { get; set; } = true;
    public int ConfirmationAutoStartSeconds { get; set; } = 30;
    public int SaveTimeoutSeconds { get; set; } = 60;
    public DirectorServerRecordingConfig Copy() => (DirectorServerRecordingConfig)MemberwiseClone();
    public void Normalize()
    {
        RadiusChunks = Math.Clamp(RadiusChunks, 1, 16);
        VolumeSeconds = Math.Clamp(VolumeSeconds, 10, 900);
        SampleRate = Math.Clamp(SampleRate, 5, 60);
        EntityStateRate = Math.Clamp(EntityStateRate, 1, 10);
        MaxActors = Math.Clamp(MaxActors, 1, 2048);
        SaveTimeoutSeconds = Math.Clamp(SaveTimeoutSeconds, 5, 600);
        ConfirmationAutoStartSeconds = Math.Clamp(ConfirmationAutoStartSeconds, 0, 86400);
    }
}
public static class DirectorServerJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static readonly JsonSerializerOptions CompactOptions = new(Options) { WriteIndented = false };
    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static T Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("Empty server data.");
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        try { File.WriteAllBytes(temp, Serialize(value)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
