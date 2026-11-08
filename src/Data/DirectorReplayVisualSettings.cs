namespace VSDirector.Data;

public enum DirectorReplaySkyMode
{
    Recorded,
    SolidColor,
    GreenScreen,
    Transparent
}

public enum DirectorReplayWeatherMode
{
    Recorded,
    Clear,
    Rain,
    Snow,
    Storm
}

/// <summary>
/// Scene-local visual overrides exposed by the replay inspector.
/// </summary>
public sealed class DirectorReplayVisualSettings
{
    public DirectorReplayWorldVisuals World { get; set; } = new();

    public DirectorReplaySkyVisuals Sky { get; set; } = new();

    public DirectorReplayEnvironmentVisuals Environment { get; set; } = new();

    public DirectorReplayCameraVisuals Camera { get; set; } = new();

    public DirectorReplayParticleVisuals Particles { get; set; } = new();

    public DirectorReplayAudioVisuals Audio { get; set; } = new();

    public DirectorReplayCompositionVisuals Composition { get; set; } = new();

    public DirectorReplayVisualSettings DeepClone()
        => new()
        {
            World = (World ?? new DirectorReplayWorldVisuals()).DeepClone(),
            Sky = (Sky ?? new DirectorReplaySkyVisuals()).DeepClone(),
            Environment = (Environment ?? new DirectorReplayEnvironmentVisuals()).DeepClone(),
            Camera = (Camera ?? new DirectorReplayCameraVisuals()).DeepClone(),
            Particles = (Particles ?? new DirectorReplayParticleVisuals()).DeepClone(),
            Audio = (Audio ?? new DirectorReplayAudioVisuals()).DeepClone(),
            Composition = (Composition ?? new DirectorReplayCompositionVisuals()).DeepClone()
        };

    public static DirectorReplayVisualSettings Normalize(DirectorReplayVisualSettings? source)
    {
        DirectorReplayVisualSettings result = source?.DeepClone() ?? new DirectorReplayVisualSettings();
        result.World.Normalize();
        result.Sky.Normalize();
        result.Environment.Normalize();
        result.Camera.Normalize();
        result.Particles.Normalize();
        result.Audio.Normalize();
        result.Composition.Normalize();
        return result;
    }
}

public sealed class DirectorReplayWorldVisuals
{
    public bool ShowTerrain { get; set; } = true;
    public bool ShowEntities { get; set; } = true;
    public bool ShowPlayers { get; set; } = true;
    public bool ShowNameTags { get; set; } = true;
    public bool HideTrackedEntity { get; set; }
    public bool ShowEntityBoxes { get; set; }
    public bool ShowEntityLookDirections { get; set; }

    public DirectorReplayWorldVisuals DeepClone() => (DirectorReplayWorldVisuals)MemberwiseClone();
    internal void Normalize() { }
}

public sealed class DirectorReplaySkyVisuals
{
    public DirectorReplaySkyMode Mode { get; set; }
    public string BackgroundColor { get; set; } = "#000000";
    public bool ShowSky { get; set; } = true;
    public bool ShowClouds { get; set; } = true;
    public bool OverrideClouds { get; set; }
    public float CloudDensity { get; set; } = 1f;

    public DirectorReplaySkyVisuals DeepClone() => (DirectorReplaySkyVisuals)MemberwiseClone();

    internal void Normalize()
    {
        if (!Enum.IsDefined(Mode)) Mode = DirectorReplaySkyMode.Recorded;
        BackgroundColor = NormalizeColor(BackgroundColor, "#000000");
        CloudDensity = Finite(CloudDensity, 1f, 0f, 4f);
    }

    internal static string NormalizeColor(string? value, string fallback)
    {
        string color = value?.Trim() ?? string.Empty;
        if (color.Length == 7 && color[0] == '#' && color[1..].All(Uri.IsHexDigit)) return color.ToUpperInvariant();
        if (color.Length == 9 && color[0] == '#' && color[1..].All(Uri.IsHexDigit)) return color.ToUpperInvariant();
        return fallback;
    }

    internal static float Finite(float value, float fallback, float minimum, float maximum)
        => Math.Clamp(float.IsFinite(value) ? value : fallback, minimum, maximum);
}

public sealed class DirectorReplayEnvironmentVisuals
{
    public bool OverrideTime { get; set; }
    public int TimeOfDayTick { get; set; } = 6000;
    public bool OverrideSeason { get; set; }
    public float Season { get; set; } = 0.33f;
    public DirectorReplayWeatherMode Weather { get; set; }
    public float Precipitation { get; set; } = 1f;
    public bool OverrideFog { get; set; }
    public string FogColor { get; set; } = "#C8D8E8";
    public float FogDensity { get; set; }
    public bool FullBright { get; set; }

    public DirectorReplayEnvironmentVisuals DeepClone() => (DirectorReplayEnvironmentVisuals)MemberwiseClone();

    internal void Normalize()
    {
        TimeOfDayTick = Math.Clamp(TimeOfDayTick, 0, 24000);
        Season = DirectorReplaySkyVisuals.Finite(Season, 0.33f, 0f, 1f);
        if (!Enum.IsDefined(Weather)) Weather = DirectorReplayWeatherMode.Recorded;
        Precipitation = DirectorReplaySkyVisuals.Finite(Precipitation, 1f, 0f, 1f);
        FogColor = DirectorReplaySkyVisuals.NormalizeColor(FogColor, "#C8D8E8");
        // Vanilla weather converts its fog density from config units with
        // `density / 1000f`; values around 0.001-0.01 already cover the
        // normal weather range. Keep the replay control in that same scale.
        FogDensity = DirectorReplaySkyVisuals.Finite(FogDensity, 0f, 0f, 0.02f);
    }
}

public sealed class DirectorReplayCameraVisuals
{
    public bool OverrideFov { get; set; }
    public float Fov { get; set; } = 70f;
    public bool OverrideRoll { get; set; }
    public float Roll { get; set; }
    public bool CameraCollision { get; set; }
    public bool OverrideNearClip { get; set; }
    public float NearClip { get; set; } = 0.05f;
    public bool CameraShake { get; set; }
    public bool SplitShake { get; set; }
    public float ShakeFrequencyX { get; set; } = 1f;
    public float ShakeAmplitudeX { get; set; }
    public float ShakeFrequencyY { get; set; } = 1f;
    public float ShakeAmplitudeY { get; set; }

    public DirectorReplayCameraVisuals DeepClone() => (DirectorReplayCameraVisuals)MemberwiseClone();

    internal void Normalize()
    {
        Fov = DirectorReplaySkyVisuals.Finite(Fov, 70f, 10f, 170f);
        Roll = DirectorReplaySkyVisuals.Finite(Roll, 0f, -180f, 180f);
        NearClip = DirectorReplaySkyVisuals.Finite(NearClip, 0.05f, 0.001f, 16f);
        ShakeFrequencyX = DirectorReplaySkyVisuals.Finite(ShakeFrequencyX, 1f, 0.1f, 10f);
        ShakeAmplitudeX = DirectorReplaySkyVisuals.Finite(ShakeAmplitudeX, 0f, 0f, 10f);
        ShakeFrequencyY = DirectorReplaySkyVisuals.Finite(ShakeFrequencyY, 1f, 0.1f, 10f);
        ShakeAmplitudeY = DirectorReplaySkyVisuals.Finite(ShakeAmplitudeY, 0f, 0f, 10f);
    }
}

public sealed class DirectorReplayParticleVisuals
{
    public bool ShowParticles { get; set; } = true;

    public DirectorReplayParticleVisuals DeepClone() => (DirectorReplayParticleVisuals)MemberwiseClone();
    internal void Normalize() { }
}

public sealed class DirectorReplayAudioVisuals
{
    public bool Enabled { get; set; } = true;
    public float MasterVolume { get; set; } = 1f;
    public bool PreservePitchWithReplaySpeed { get; set; } = true;
    public bool SpatialAudio { get; set; } = true;

    public DirectorReplayAudioVisuals DeepClone() => (DirectorReplayAudioVisuals)MemberwiseClone();
    internal void Normalize()
    {
        MasterVolume = Gain(MasterVolume);
    }

    private static float Gain(float value) => DirectorReplaySkyVisuals.Finite(value, 1f, 0f, 2f);
}

public sealed class DirectorReplayCompositionVisuals
{
    public bool RuleOfThirds { get; set; }
    public bool CenterGuide { get; set; }
    public bool CameraPath { get; set; }

    public DirectorReplayCompositionVisuals DeepClone() => (DirectorReplayCompositionVisuals)MemberwiseClone();
    internal void Normalize() { }
}
