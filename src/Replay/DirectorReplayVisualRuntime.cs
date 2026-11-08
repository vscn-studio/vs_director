using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Data;

namespace VSDirector.Replay;

internal static class DirectorReplayVisualRuntime
{
    private static DirectorReplayVisualSettings? current;
    private static double replaySpeed = 1d;

    internal static DirectorReplayVisualSettings? Current
    {
        get => Volatile.Read(ref current);
        set => Volatile.Write(ref current, value);
    }

    internal static double ReplaySpeed
    {
        get => Volatile.Read(ref replaySpeed);
        set => Volatile.Write(
            ref replaySpeed,
            double.IsFinite(value) ? Math.Clamp(Math.Abs(value), 0d, 64d) : 1d);
    }

    internal static Vec4f BackgroundColor
    {
        get
        {
            DirectorReplaySkyVisuals sky = Current?.Sky ?? new DirectorReplaySkyVisuals();
            string code = sky.Mode == DirectorReplaySkyMode.GreenScreen
                ? "#00FF00"
                : sky.BackgroundColor;
            Vec4f color = ParseColor(code, new Vec4f(0f, 0f, 0f, 1f));
            if (sky.Mode == DirectorReplaySkyMode.Transparent)
            {
                color.W = 0f;
            }
            return color;
        }
    }

    internal static bool ReplacesSky
        => Current?.Sky is { } sky
            && (!sky.ShowSky || sky.Mode != DirectorReplaySkyMode.Recorded);

    internal static bool SuppressesClouds
        => Current?.Sky is { } sky
            && (!sky.ShowClouds || ReplacesSky);

    internal static Vec4f ParseColor(string? code, Vec4f fallback)
    {
        string value = code?.Trim().TrimStart('#') ?? string.Empty;
        if (value.Length is not (6 or 8)
            || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgba))
        {
            return fallback.Clone();
        }
        if (value.Length == 6)
        {
            return new Vec4f(
                (rgba >> 16 & 0xff) / 255f,
                (rgba >> 8 & 0xff) / 255f,
                (rgba & 0xff) / 255f,
                1f);
        }
        return new Vec4f(
            (rgba >> 24 & 0xff) / 255f,
            (rgba >> 16 & 0xff) / 255f,
            (rgba >> 8 & 0xff) / 255f,
            (rgba & 0xff) / 255f);
    }

    internal static void ApplyAmbientOverrides(AmbientManager ambient)
    {
        DirectorReplayVisualSettings? visuals = Current;
        if (visuals is null)
        {
            return;
        }

        DirectorReplayEnvironmentVisuals environment = visuals.Environment;
        if (environment.OverrideFog)
        {
            Vec4f fog = ParseColor(environment.FogColor, ambient.BlendedFogColor);
            ambient.BlendedFogColor.Set(fog.X, fog.Y, fog.Z, fog.W);
            ambient.BlendedFogDensity = environment.FogDensity;
        }
        if (environment.FullBright)
        {
            ambient.BlendedSceneBrightness = Math.Max(2f, ambient.BlendedSceneBrightness);
        }
        if (visuals.Sky.OverrideClouds)
        {
            ambient.BlendedCloudDensity = visuals.Sky.CloudDensity;
        }
    }
}

internal sealed class DirectorReplayVisualEnvironmentController
{
    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private AmbientState? previousAmbient;
    private ShaderState? previousShader;

    internal DirectorReplayVisualEnvironmentController(ICoreClientAPI capi, ClientMain game)
    {
        this.capi = capi;
        this.game = game;
    }

    internal void Apply(DirectorReplayVisualSettings visuals)
    {
        DirectorReplayVisualSettings normalized = DirectorReplayVisualSettings.Normalize(visuals);
        DirectorReplayVisualSettings? previousVisuals = DirectorReplayVisualRuntime.Current;
        if (previousVisuals is null
            || previousVisuals.Environment.Weather != normalized.Environment.Weather)
        {
            DirectorReplayVisualRuntime.Current = null;
            DirectorReplayWeatherVisualPatch.ClearOverrides();
        }
        RestoreAmbient();
        RestoreShader();
        DirectorReplayVisualRuntime.Current = normalized;
        bool needsAmbientOverride = normalized.Environment.OverrideFog
            || normalized.Environment.FullBright
            || normalized.Sky.OverrideClouds;
        if (needsAmbientOverride)
        {
            AmbientManager ambient = game.AmbientManager;
            previousAmbient = AmbientState.Capture(ambient);
            DirectorReplayVisualRuntime.ApplyAmbientOverrides(ambient);
        }
        ApplyShader(normalized);
    }

    internal void Restore()
    {
        // Publish the inactive state before releasing the weather lock so an
        // async particle callback cannot register a new override during stop.
        DirectorReplayVisualRuntime.Current = null;
        DirectorReplayWeatherVisualPatch.ClearOverrides();
        RestoreAmbient();
        RestoreShader();
        DirectorReplayVisualRuntime.ReplaySpeed = 1d;
    }

    private void RestoreAmbient()
    {
        if (previousAmbient is not AmbientState state)
        {
            return;
        }
        state.Restore(game.AmbientManager);
        previousAmbient = null;
    }

    private void ApplyShader(DirectorReplayVisualSettings visuals)
    {
        DirectorReplayCameraVisuals camera = visuals.Camera;
        if (!camera.OverrideNearClip)
        {
            return;
        }

        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        previousShader = ShaderState.Capture(uniforms);

        if (camera.OverrideNearClip)
        {
            uniforms.ZNear = camera.NearClip;
        }
    }

    private void RestoreShader()
    {
        if (previousShader is not ShaderState state)
        {
            return;
        }
        state.Restore(capi.Render.ShaderUniforms);
        previousShader = null;
    }

    private readonly record struct ShaderState(
        float ZNear,
        float SunlightExtraBrightness,
        float SunSpecularIntensity,
        float WindSpeed,
        float WindWaveIntensity,
        float WindWaveCounter,
        float WindWaveCounterHigh,
        float WaterWaveIntensity,
        float WaterWaveCounter,
        float WaterFlowCounter,
        float WaterStillCounter,
        float CameraUnderwater,
        float TimeCounter,
        float ExtraBloom,
        float ExtraContrast,
        float DamageVignette,
        float ExtraSepia,
        float GlitchStrength,
        float GlitchWaviness,
        float GlobalWorldWarp,
        float PerceptionEffectIntensity,
        float SeasonRel,
        float SeasonTemperature,
        Vec4f WaterMurkColor,
        int PointLightsCount)
    {
        internal static ShaderState Capture(DefaultShaderUniforms value)
            => new(
                value.ZNear,
                value.SunlightExtraBrightness,
                value.SunSpecularIntensity,
                value.WindSpeed,
                value.WindWaveIntensity,
                value.WindWaveCounter,
                value.WindWaveCounterHighFreq,
                value.WaterWaveIntensity,
                value.WaterWaveCounter,
                value.WaterFlowCounter,
                value.WaterStillCounter,
                value.CameraUnderwater,
                value.TimeCounter,
                value.ExtraBloom,
                value.ExtraContrastLevel,
                value.DamageVignetting,
                value.ExtraSepia,
                value.GlitchStrength,
                value.GlitchWaviness,
                value.GlobalWorldWarp,
                value.PerceptionEffectIntensity,
                value.SeasonRel,
                value.SeasonTemperature,
                value.WaterMurkColor.Clone(),
                value.PointLightsCount);

        internal void Restore(DefaultShaderUniforms value)
        {
            value.ZNear = ZNear;
            value.SunlightExtraBrightness = SunlightExtraBrightness;
            value.SunSpecularIntensity = SunSpecularIntensity;
            value.WindSpeed = WindSpeed;
            value.WindWaveIntensity = WindWaveIntensity;
            value.WindWaveCounter = WindWaveCounter;
            value.WindWaveCounterHighFreq = WindWaveCounterHigh;
            value.WaterWaveIntensity = WaterWaveIntensity;
            value.WaterWaveCounter = WaterWaveCounter;
            value.WaterFlowCounter = WaterFlowCounter;
            value.WaterStillCounter = WaterStillCounter;
            value.CameraUnderwater = CameraUnderwater;
            value.TimeCounter = TimeCounter;
            value.ExtraBloom = ExtraBloom;
            value.ExtraContrastLevel = ExtraContrast;
            value.DamageVignetting = DamageVignette;
            value.ExtraSepia = ExtraSepia;
            value.GlitchStrength = GlitchStrength;
            value.GlitchWaviness = GlitchWaviness;
            value.GlobalWorldWarp = GlobalWorldWarp;
            value.PerceptionEffectIntensity = PerceptionEffectIntensity;
            value.SeasonRel = SeasonRel;
            value.SeasonTemperature = SeasonTemperature;
            value.WaterMurkColor = WaterMurkColor.Clone();
            value.PointLightsCount = PointLightsCount;
        }
    }

    private readonly record struct AmbientState(
        Vec4f FogColor,
        Vec3f AmbientColor,
        float FogDensity,
        float FogMinimum,
        float FlatFogDensity,
        float FlatFogOffset,
        float FlatFogShaderY,
        float CloudBrightness,
        float CloudDensity,
        float SceneBrightness,
        float FogBrightness)
    {
        internal static AmbientState Capture(AmbientManager manager)
            => new(
                manager.BlendedFogColor.Clone(),
                manager.BlendedAmbientColor.Clone(),
                manager.BlendedFogDensity,
                manager.BlendedFogMin,
                manager.BlendedFlatFogDensity,
                manager.BlendedFlatFogYOffset,
                manager.BlendedFlatFogYPosForShader,
                manager.BlendedCloudBrightness,
                manager.BlendedCloudDensity,
                manager.BlendedSceneBrightness,
                manager.BlendedFogBrightness);

        internal void Restore(AmbientManager manager)
        {
            manager.BlendedFogColor.Set(FogColor.X, FogColor.Y, FogColor.Z, FogColor.W);
            manager.BlendedAmbientColor.Set(AmbientColor.X, AmbientColor.Y, AmbientColor.Z);
            manager.BlendedFogDensity = FogDensity;
            manager.BlendedFogMin = FogMinimum;
            manager.BlendedFlatFogDensity = FlatFogDensity;
            manager.BlendedFlatFogYOffset = FlatFogOffset;
            manager.BlendedFlatFogYPosForShader = FlatFogShaderY;
            manager.BlendedCloudBrightness = CloudBrightness;
            manager.BlendedCloudDensity = CloudDensity;
            manager.BlendedSceneBrightness = SceneBrightness;
            manager.BlendedFogBrightness = FogBrightness;
        }
    }
}
