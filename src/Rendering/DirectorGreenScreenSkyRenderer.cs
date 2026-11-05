using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Replay;

namespace VSDirector.Rendering;

internal static class DirectorGreenScreenRuntime
{
    private static DirectorConfig? config;
    private static DirectorGreenScreenSkyRenderer? skyRenderer;

    internal static bool SkyEnabled
        => DirectorReplayVisualRuntime.ReplacesSky
            || config is { EnableGreenScreenSupport: true, EnableGreenScreenSky: true };

    internal static bool CloudsSuppressed
        => DirectorReplayVisualRuntime.SuppressesClouds
            || SkyEnabled && config?.GreenScreenSkyHideClouds == true;

    internal static bool StarsSuppressed
        => SkyEnabled;

    internal static bool SunMoonSuppressed
        => SkyEnabled;

    internal static bool AuroraSuppressed
        => SkyEnabled;

    internal static Vec4f SkyColor
        => DirectorReplayVisualRuntime.ReplacesSky
            ? DirectorReplayVisualRuntime.BackgroundColor
            : new Vec4f(0f, 1f, 0f, 1f);

    internal static void Attach(
        DirectorConfig current,
        DirectorGreenScreenSkyRenderer renderer)
    {
        config = current;
        skyRenderer = renderer;
    }

    internal static void Detach(
        DirectorConfig current,
        DirectorGreenScreenSkyRenderer? renderer)
    {
        if (ReferenceEquals(config, current))
        {
            config = null;
        }
        if (ReferenceEquals(skyRenderer, renderer))
        {
            skyRenderer = null;
        }
    }

    internal static bool TryRenderSky(object skySystem, float deltaTime)
        => SkyEnabled && skyRenderer?.Render(skySystem, deltaTime) == true;
}

internal sealed class DirectorGreenScreenSkyRenderer : IDisposable
{
    private const float SkyStateRefreshIntervalSeconds = 0.25f;

    private static readonly FieldInfo? SkyDaylightField = AccessTools.Field(
        typeof(DefaultShaderUniforms),
        "SkyDaylight");

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly MeshRef screenTriangle;
    private readonly object?[] sunColorArguments = new object?[2];
    private IShaderProgram? shader;
    private Type? skySystemType;
    private MethodInfo? calcSunColor;
    private FieldInfo? skyTextureField;
    private float skyStateElapsed = float.PositiveInfinity;
    private bool skyStateUpdateErrorLogged;
    private bool sunColorUpdateFailed;
    private bool disposed;

    internal DirectorGreenScreenSkyRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        game = capi.World as ClientMain
            ?? throw new InvalidOperationException("VS Director green-screen sky requires the desktop client.");
        screenTriangle = capi.Render.UploadMesh(BuildScreenTriangle());
        capi.Event.ReloadShader += LoadShader;
        LoadShader();
    }

    internal bool Render(object skySystem, float deltaTime)
    {
        if (shader is null || shader.Disposed)
        {
            return false;
        }

        float elapsed = float.IsFinite(deltaTime) && deltaTime > 0f
            ? Math.Min(deltaTime, 1f)
            : 1f / 60f;
        skyStateElapsed += elapsed;
        if (skyStateElapsed >= SkyStateRefreshIntervalSeconds)
        {
            try
            {
                UpdateSkyState(skySystem);
            }
            catch (Exception exception)
            {
                if (!skyStateUpdateErrorLogged)
                {
                    skyStateUpdateErrorLogged = true;
                    capi.Logger.Debug(
                        "VS Director: optimized green-screen sky state refresh failed: {0}",
                        exception.Message);
                }
            }
            skyStateElapsed = 0f;
        }

        game.Platform.GlDisableDepthTest();
        shader.Use();
        try
        {
            Vec4f color = DirectorGreenScreenRuntime.SkyColor;
            shader.Uniform("greenScreenColor", color.X, color.Y, color.Z, color.W);
            game.Platform.RenderMesh(screenTriangle);
        }
        finally
        {
            shader.Stop();
            game.Platform.GlEnableDepthTest();
        }
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        capi.Event.ReloadShader -= LoadShader;
        shader?.Dispose();
        shader = null;
        screenTriangle.Dispose();
    }

    private void UpdateSkyState(object skySystem)
    {
        IClientGameCalendar calendar = game.Calendar;
        double playerY = game.EntityPlayer?.Pos.Y ?? game.SeaLevel;
        float altitudeFactor = (float)GameMath.Clamp(
            (playerY - game.SeaLevel - 1000d) / 30000d,
            0d,
            1d);
        float daylight = 1.25f * GameMath.Max(
            calendar.DayLightStrength - calendar.MoonLightStrength / 2f,
            0.05f);
        SkyDaylightField?.SetValue(
            game.shUniforms,
            Math.Max(0f, daylight * (1f - altitudeFactor)));
        game.shUniforms.SunsetMod = calendar.SunsetMod;

        if (sunColorUpdateFailed)
        {
            return;
        }
        Type currentType = skySystem.GetType();
        if (skySystemType != currentType)
        {
            skySystemType = currentType;
            calcSunColor = AccessTools.Method(
                currentType,
                "calcSunColor",
                new[] { typeof(Vec3f), typeof(Vec3f) });
            skyTextureField = AccessTools.Field(currentType, "skyTexture");
        }
        if (calcSunColor is null
            || skyTextureField?.GetValue(skySystem) is null
            || capi.World.Player is not { } player)
        {
            return;
        }
        try
        {
            Vec3f view = EntityPos.GetViewVector(
                player.CameraPitch,
                player.CameraYaw);
            sunColorArguments[0] = calendar.SunPositionNormalized;
            sunColorArguments[1] = view;
            calcSunColor.Invoke(skySystem, sunColorArguments);
        }
        catch (Exception exception)
        {
            sunColorUpdateFailed = true;
            capi.Logger.Debug(
                "VS Director: optimized green-screen sky could not refresh sun color: {0}",
                exception.Message);
        }
    }

    private static MeshData BuildScreenTriangle()
    {
        int color = ColorUtil.ToRgba(255, 255, 255, 255);
        MeshData data = new(3, 3, withNormals: false, withUv: false);
        data.SetMode(EnumDrawMode.Triangles);
        data.AddVertexSkipTex(-1f, -1f, 0f, color);
        data.AddVertexSkipTex(3f, -1f, 0f, color);
        data.AddVertexSkipTex(-1f, 3f, 0f, color);
        data.AddIndex(0);
        data.AddIndex(1);
        data.AddIndex(2);
        return data;
    }

    private bool LoadShader()
    {
        IShaderProgram next = capi.Shader.NewShaderProgram();
        next.AssetDomain = "vsdirector";
        next.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        next.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("greenscreensky", next);
        bool compiled = next.Compile();
        if (!compiled)
        {
            next.Dispose();
            return false;
        }
        next.Use();
        try
        {
            next.Uniform("greenScreenColor", 0f, 1f, 0f, 1f);
        }
        finally
        {
            next.Stop();
        }
        IShaderProgram? previous = shader;
        shader = next;
        previous?.Dispose();
        return true;
    }
}

internal static class DirectorGreenScreenRenderPatch
{
    private static readonly MethodInfo SkipSkyPrefix = AccessTools.Method(
        typeof(DirectorGreenScreenRenderPatch),
        nameof(PrefixSkipSky))!;
    private static readonly MethodInfo SkipStarsPrefix = AccessTools.Method(
        typeof(DirectorGreenScreenRenderPatch),
        nameof(PrefixSkipStars))!;
    private static readonly MethodInfo SkipSunMoonPrefix = AccessTools.Method(
        typeof(DirectorGreenScreenRenderPatch),
        nameof(PrefixSkipSunMoon))!;
    private static readonly MethodInfo SkipAuroraPrefix = AccessTools.Method(
        typeof(DirectorGreenScreenRenderPatch),
        nameof(PrefixSkipAurora))!;
    private static readonly MethodInfo RenderSkyPrefix = AccessTools.Method(
        typeof(DirectorGreenScreenRenderPatch),
        nameof(PrefixRenderSky))!;
    private static readonly MethodInfo SkipCloudsPrefix = AccessTools.Method(
        typeof(DirectorGreenScreenRenderPatch),
        nameof(PrefixSkipClouds))!;

    internal static IReadOnlyList<MethodBase> TryPatch(Harmony harmony, ILogger logger)
    {
        List<MethodBase> patched = new();
        AddPatch(
            harmony,
            logger,
            patched,
            "Vintagestory.Client.NoObf.SystemRenderSkyColor",
            "OnRenderFrame3D",
            new[] { typeof(float) },
            RenderSkyPrefix,
            required: true);
        AddPatch(
            harmony,
            logger,
            patched,
            "Vintagestory.Client.NoObf.SystemRenderNightSky",
            "OnRenderFrame3D",
            new[] { typeof(float) },
            SkipStarsPrefix);
        AddPatch(
            harmony,
            logger,
            patched,
            "Vintagestory.Client.NoObf.SystemRenderSunMoon",
            "OnRenderFrame3D",
            new[] { typeof(float) },
            SkipSunMoonPrefix);
        AddPatch(
            harmony,
            logger,
            patched,
            "Vintagestory.Client.NoObf.SystemRenderSunMoon",
            "OnRenderFrame3DPost",
            new[] { typeof(float) },
            SkipSunMoonPrefix);
        AddPatch(
            harmony,
            logger,
            patched,
            "Vintagestory.GameContent.AuroraRenderer",
            "OnRenderFrame",
            new[] { typeof(float), typeof(EnumRenderStage) },
            SkipAuroraPrefix);
        AddPatch(
            harmony,
            logger,
            patched,
            "Vintagestory.GameContent.CloudRenderer",
            "OnRenderFrame",
            new[] { typeof(float), typeof(EnumRenderStage) },
            SkipCloudsPrefix);
        AddPatch(
            harmony,
            logger,
            patched,
            "FluffyClouds.CloudRendererMap",
            "OnRenderFrame",
            new[] { typeof(float), typeof(EnumRenderStage) },
            SkipCloudsPrefix);
        AddPatch(
            harmony,
            logger,
            patched,
            "FluffyClouds.CloudRendererVolumetric",
            "OnRenderFrame",
            new[] { typeof(float), typeof(EnumRenderStage) },
            SkipCloudsPrefix);
        return patched;
    }

    private static void AddPatch(
        Harmony harmony,
        ILogger logger,
        ICollection<MethodBase> patched,
        string typeName,
        string methodName,
        Type[] arguments,
        MethodInfo prefix,
        bool required = false)
    {
        Type? type = AccessTools.TypeByName(typeName);
        MethodInfo? target = type is null ? null : AccessTools.Method(type, methodName, arguments);
        if (target is null)
        {
            if (required)
            {
                logger.Warning(
                    "VS Director: green-screen sky hook {0}.{1} was not found.",
                    typeName,
                    methodName);
            }
            return;
        }
        harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
        patched.Add(target);
    }

    private static bool PrefixSkipSky()
        => !DirectorGreenScreenRuntime.SkyEnabled;

    private static bool PrefixSkipStars()
        => !DirectorGreenScreenRuntime.StarsSuppressed;

    private static bool PrefixSkipSunMoon()
        => !DirectorGreenScreenRuntime.SunMoonSuppressed;

    private static bool PrefixSkipAurora()
        => !DirectorGreenScreenRuntime.AuroraSuppressed;

    private static bool PrefixRenderSky(object __instance, float deltaTime)
        => !DirectorGreenScreenRuntime.TryRenderSky(__instance, deltaTime);

    private static bool PrefixSkipClouds()
        => !DirectorGreenScreenRuntime.CloudsSuppressed;
}
