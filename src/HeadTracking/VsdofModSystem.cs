// Integration of VS-6DOF by DF (bjfishman / TGU-HansJack), CC0-1.0.
// The compatibility bridge intentionally retains the upstream helper names.
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using VSDirector.Data;
using VSDirector.Runtime;

namespace VSDirector.HeadTracking;

internal static class VsdofModSystem
{
    internal static ICoreClientAPI? Capi;
    internal static HeadTrackingConfig Config = new();
    internal static TrackingState State = new();
    internal static bool ExternalMod, IsZoomButtonAvailable;
    internal static SquintOverlayRenderer? ZoomOverlay;
    internal static Func<bool> AllowLive = () => false;
    internal static bool DirectorActive => DirectorCameraRuntime.Controller?.IsActive == true;
    private static FreeTrackReader? reader;
    private static Harmony? harmony;
    private static FTData center, filtered;
    private static bool hasCenter, hasFiltered;
    private static long filteredAt;
    private static FTData lastRaw;
    private static ZoomButtonConfig? optionalZoomConfig;
    private static long zoomConfigReadAt;
    internal const string ConfigFile = "vsdirector-headtracking.json";

    internal static void Start(ICoreClientAPI api, Func<bool> allowed)
    {
        Capi = api; AllowLive = allowed;
        try { Config = api.LoadModConfig<HeadTrackingConfig>(ConfigFile) ?? new(); }
        catch (Exception e) { api.Logger.Warning("VS Director: head tracking config: {0}", e.Message); Config = new(); }
        NormalizeConfig();
        reader = new();
        ExternalMod = api.ModLoader.IsModEnabled("vsdof");
        IsZoomButtonAvailable = api.ModLoader.IsModEnabled("zoombuttonreborn") || api.ModLoader.IsModEnabled("zoombutton");
        harmony = new Harmony("vsdirector.headtracking");
        harmony.CreateClassProcessor(typeof(PlayerCameraTrackingPatch)).Patch();
        harmony.CreateClassProcessor(typeof(PlayerModelVisibilityPatch)).Patch();
        if (ExternalMod && AccessTools.Method("VSDOF.PlayerCameraTrackingPatch:Postfix") is { } external)
            harmony.Patch(external, prefix: new HarmonyMethod(typeof(VsdofModSystem), nameof(ExternalCameraPrefix)));
        ZoomOverlay = new(api);
    }
    private static bool ExternalCameraPrefix(object[] __args)
    {
        if (VSDirector.Rendering.DirectorOffscreenRenderRuntime.IsRendering) return false;
        if (!DirectorActive && AllowLive()) return true;
        // Release the standalone mod's previous offset when Director takes over.
        var type = AccessTools.TypeByName("VSDOF.VsdofModSystem");
        var config = type is null ? null : AccessTools.Field(type, "Config")?.GetValue(null);
        if (config is not null && Capi?.World?.Player?.Entity is { } player)
            AccessTools.Method("VSDOF.PlayerCameraTrackingPatch:ResetOffsets")?.Invoke(null, [__args[0], player, config]);
        return false;
    }
    internal static void Save()
    {
        PlayerCameraTrackingPatch.ReleaseState();
        State.DirectorZoom = State.BuiltinZoom = false;
        optionalZoomConfig = null;
        NormalizeConfig(); Capi?.StoreModConfig(Config, ConfigFile);
        PlayerModelVisibilityPatch.RequestRetessellate();
        hasFiltered = false;
    }
    private static void NormalizeConfig()
    {
        foreach (var field in typeof(HeadTrackingConfig).GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (field.FieldType == typeof(float) && field.GetValue(Config) is float n)
                field.SetValue(Config, float.IsFinite(n) ? Math.Clamp(n, -1000, 1000) : 0f);
        Config.MaxTranslationX = Math.Abs(Config.MaxTranslationX); Config.MaxTranslationY = Math.Abs(Config.MaxTranslationY);
        Config.MaxTranslationZ = Math.Abs(Config.MaxTranslationZ);
        Config.CrouchHysteresis = Math.Abs(Config.CrouchHysteresis);
        Config.SmoothingSeconds = Math.Clamp(Config.SmoothingSeconds, 0, 1);
    }
    internal static void Recenter() { center = lastRaw; hasCenter = State.HasTracking; hasFiltered = false; }
    internal static bool TryGetTracking(out FTData data)
    {
        data = default;
        if (Capi is null || reader is null || !reader.TryRead(out var raw))
        { State.HasTracking = false; hasFiltered = false; return false; }
        lastRaw = raw;
        data = raw;
        if (hasCenter)
        {
            data.X -= center.X; data.Y -= center.Y; data.Z -= center.Z;
            data.Yaw = (float)Math.IEEERemainder(data.Yaw - center.Yaw, Math.PI * 2);
            data.Pitch = (float)Math.IEEERemainder(data.Pitch - center.Pitch, Math.PI * 2);
            data.Roll = (float)Math.IEEERemainder(data.Roll - center.Roll, Math.PI * 2);
        }
        long now = Stopwatch.GetTimestamp();
        if (hasFiltered && Config.SmoothingSeconds > 0)
        {
            float t = (float)(1 - Math.Exp(-Stopwatch.GetElapsedTime(filteredAt, now).TotalSeconds / Config.SmoothingSeconds));
            float L(float a, float b) => a + (b - a) * t;
            float A(float a, float b) => a + (float)Math.IEEERemainder(b - a, Math.PI * 2) * t;
            data.X = L(filtered.X, data.X); data.Y = L(filtered.Y, data.Y); data.Z = L(filtered.Z, data.Z);
            data.Yaw = A(filtered.Yaw, data.Yaw); data.Pitch = A(filtered.Pitch, data.Pitch); data.Roll = A(filtered.Roll, data.Roll);
        }
        filtered = data; filteredAt = now; hasFiltered = true;
        State.HasTracking = true; State.LastData = data;
        return true;
    }
    internal static bool TrySample(out DirectorHeadMotionSample sample)
        => TrySample(70, out sample);

    internal static bool TrySample(float baseFov, out DirectorHeadMotionSample sample)
    {
        sample = default;
        if (ZoomOverlay is not null) ZoomOverlay.PercentZoomed = 0;
        if (!Config.EnableTracking || Capi is null || !AllowLive() || PlayerCameraTrackingPatch.IsTrackingSuspended(Capi)
            || !TryGetTracking(out var data)) { State.DirectorZoom = false; return false; }
        float zoom = 1, vignette = 0;
        if (Config.EnableLeanToZoom)
        {
            float axis = -data.Z / 100f * (Config.LeanToZoomThreshold >= 0 ? 1 : -1);
            float amount;
            if (Config.EnableLeanToZoomAxis)
                amount = Math.Clamp((axis - Math.Abs(Config.LeanToZoomThreshold)) / Math.Max(.001f, Math.Abs(Config.LeanToZoomAxisRange)), 0, 1)
                    * Math.Clamp(Config.LeanToZoomAxisMax, 0, 1);
            else
            {
                if (!State.DirectorZoom && axis > Math.Abs(Config.LeanToZoomThreshold)) State.DirectorZoom = true;
                else if (State.DirectorZoom && axis < Math.Abs(Config.LeanToZoomThreshold) - Math.Abs(Config.LeanToZoomHysteresis)) State.DirectorZoom = false;
                amount = State.DirectorZoom ? 1 : 0;
            }
            TryGetZoomButtonConfig(out var z);
            zoom = 1 + (Math.Clamp(z.fieldOfView, 1, 150) / Math.Clamp(baseFov, 1, 150) - 1) * amount;
            vignette = z.vignetteShaderEnabled ? amount : 0;
        }
        sample = new(0,
            Config.EnableTranslation ? Math.Clamp(-data.X / 100f * Config.TranslationGainX, -Config.MaxTranslationX, Config.MaxTranslationX) + Config.BaselineOffsetX : 0,
            Config.EnableTranslation ? Math.Clamp(data.Y / 100f * Config.TranslationGainY, -Config.MaxTranslationY, Config.MaxTranslationY) + Config.BaselineOffsetY : 0,
            Config.EnableTranslation ? Math.Clamp(data.Z / 100f * Config.TranslationGainZ, -Config.MaxTranslationZ, Config.MaxTranslationZ) + Config.BaselineOffsetZ : 0,
            Config.EnableRotation ? -data.Yaw * Config.YawGain * 180 / MathF.PI : 0,
            Config.EnableRotation ? data.Pitch * Config.PitchGain * 180 / MathF.PI : 0,
            Config.EnableRotation && Config.EnableRoll ? -data.Roll * Config.RollGain : 0, zoom, vignette);
        if (ZoomOverlay is not null) ZoomOverlay.PercentZoomed = vignette;
        return true;
    }
    internal static bool TrySetZoomHotkeyState(bool pressed)
    {
        var key = Capi?.Input.GetHotKeyByCode("zoombutton")?.CurrentMapping?.KeyCode;
        if (!IsZoomButtonAvailable || key is null || key < 0 || key >= Capi!.Input.KeyboardKeyState.Length) return false;
        Capi.Input.KeyboardKeyState[key.Value] = pressed; return true;
    }
    internal static bool TryGetZoomButtonConfig(out ZoomButtonConfig config)
    {
        config = new() { fieldOfView = (int)Math.Clamp(Config.ZoomFov, 1, 150), mouseSensitivityFactor = Math.Clamp(Config.ZoomSensitivity, .01f, 2),
            changeMouseSmoothing = Config.ChangeZoomSmoothing, mouseSmoothing = Config.ZoomSmoothing, vignetteShaderEnabled = Config.EnableZoomVignette };
        if (IsZoomButtonAvailable)
            try
            {
                long now = Stopwatch.GetTimestamp();
                if (optionalZoomConfig is null || Stopwatch.GetElapsedTime(zoomConfigReadAt, now).TotalSeconds >= 2)
                {
                    optionalZoomConfig = Capi?.LoadModConfig<ZoomButtonConfig>(ZoomButtonConfig.ConfigFileName) ?? config;
                    zoomConfigReadAt = now;
                }
                config = optionalZoomConfig;
            }
            catch { /* Keep the integrated defaults if the optional mod config cannot be read. */ }
        return true;
    }
    internal static void Dispose()
    {
        PlayerCameraTrackingPatch.ReleaseState();
        Config.DisablePlayerModel = false; PlayerModelVisibilityPatch.RequestRetessellate();
        harmony?.UnpatchAll(harmony.Id); harmony = null;
        ZoomOverlay?.Dispose(); ZoomOverlay = null;
        reader?.Dispose(); reader = null;
        State = new(); hasFiltered = hasCenter = false; Capi = null; AllowLive = () => false;
        optionalZoomConfig = null;
    }
}

internal sealed class TrackingState
{
    public bool HasTracking, CrouchToggled, AppliedRotation, LeanZoomPressed, LeanZoomAxisActive, DirectorZoom, BuiltinZoom;
    public bool CrouchReady = true;
    public FTData LastData;
    public int LeanZoomAxisFieldOfView, LeanZoomAxisMouseSensitivity, LeanZoomAxisMouseSmoothing;
}
internal sealed class ZoomButtonConfig
{
    public const string ConfigFileName = "zoombutton119.json", FieldOfViewSettingName = "fieldOfView",
        MouseSensitivitySettingName = "mouseSensivity", MouseSmoothingSettingName = "mouseSmoothing";
    public int fieldOfView = 20;
    public float mouseSensitivityFactor = .5f, mouseSmoothing = 0;
    public bool changeMouseSmoothing = false, vignetteShaderEnabled = true;
}
