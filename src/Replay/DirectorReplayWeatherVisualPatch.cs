using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using ApiOrderedDictionary = Vintagestory.API.Datastructures.OrderedDictionary<string, Vintagestory.API.Common.AmbientModifier>;

namespace VSDirector.Replay;

/// <summary>
/// Supplies replay weather through read-time copies. The native weather
/// reader and climate field remain owned by the game, which is important
/// because precipitation is consumed by an asynchronous particle callback.
/// </summary>
internal static class DirectorReplayWeatherVisualPatch
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance
        | BindingFlags.Public
        | BindingFlags.NonPublic;
    private const float PrecipitationRampPerSecond = 1.5f;
    private const float MaxPrecipitationFrameSeconds = 0.1f;
    // Vanilla divides the heavy-thunder config rates by 100 before the
    // client tick consumes them as a per-tick probability.
    private const float StormNearLightningRate = 0.0035f;
    private const float StormDistantLightningRate = 0.0075f;

    private static readonly object PrecipitationSync = new();
    private static DirectorReplayWeatherMode precipitationMode =
        DirectorReplayWeatherMode.Recorded;
    private static float precipitation;
    private static bool precipitationInitialized;

    [ThreadStatic]
    private static int particleSpawnScopeDepth;

    internal static bool AllowsLiveParticleSpawns
        => particleSpawnScopeDepth > 0 && ActiveEnvironment() is not null;

    internal static bool IsWeatherParticleSpawnScope
        => particleSpawnScopeDepth > 0;

    internal static IReadOnlyList<MethodBase> TryPatch(Harmony harmony, ILogger logger)
    {
        List<MethodBase> patched = new();
        Type? weatherSystem = AccessTools.TypeByName(
            "Vintagestory.GameContent.WeatherSystemClient");

        AddPostfix(
            harmony,
            logger,
            patched,
            weatherSystem,
            "OnRenderFrame",
            new[] { typeof(float), typeof(EnumRenderStage) },
            nameof(WeatherSystemPostfix));

        Type? particles = AccessTools.TypeByName(
            "Vintagestory.GameContent.WeatherSimulationParticles");
        AddParticlePatch(
            harmony,
            logger,
            patched,
            AccessTools.Method(
                particles,
                "asyncParticleSpawn",
                new[] { typeof(float), typeof(IAsyncParticleManager) }));

        Type? sound = AccessTools.TypeByName(
            "Vintagestory.GameContent.WeatherSimulationSound");
        AddTranspiler(
            harmony,
            logger,
            patched,
            AccessTools.Method(sound, "updateSounds", new[] { typeof(float) }));

        Type? lightning = AccessTools.TypeByName(
            "Vintagestory.GameContent.WeatherSimulationLightning");
        AddTranspiler(
            harmony,
            logger,
            patched,
            AccessTools.Method(lightning, "ClientTick", new[] { typeof(float) }));

        Type? ambient = AccessTools.TypeByName(
            "Vintagestory.Client.NoObf.AmbientManager");
        MethodInfo? ambientUpdate = ambient is null
            ? null
            : AccessTools.Method(ambient, "UpdateAmbient", new[] { typeof(float) });
        AddPostfix(
            harmony,
            logger,
            patched,
            ambientUpdate,
            nameof(AmbientPostfix));

        return patched;
    }

    internal static void ClearOverrides()
    {
        lock (PrecipitationSync)
        {
            precipitationMode = DirectorReplayWeatherMode.Recorded;
            precipitation = 0f;
            precipitationInitialized = false;
        }
    }

    private static void WeatherSystemPostfix(
        object __instance,
        float dt,
        EnumRenderStage stage)
    {
        if ((int)stage != 0 || ActiveEnvironment() is not { } environment)
        {
            return;
        }

        try
        {
            AdvancePrecipitation(environment, dt);
            if (GetFieldValue(__instance, "capi") is not ICoreClientAPI capi
                || GetFieldValue(__instance, "WeatherDataAtPlayer") is not object reader
                || GetFieldValue(reader, "BlendedWeatherData") is not object source)
            {
                return;
            }

            // OnRenderFrame has already completed the native blend. Only its
            // ambient result is published, leaving the source snapshot intact.
            object overrideSnapshot = CreateSnapshotOverride(source, environment);
            if (GetFieldValue(overrideSnapshot, "Ambient") is not AmbientModifier overrideAmbient)
            {
                return;
            }
            ApiOrderedDictionary modifiers = capi.Ambient.CurrentModifiers;
            modifiers["weather"] = overrideAmbient;
        }
        catch
        {
            // Reflection hooks are optional across Vintage Story versions.
        }
    }

    private static void AmbientPostfix(object __instance)
    {
        if (__instance is not AmbientManager ambient)
        {
            return;
        }

        try
        {
            DirectorReplayVisualRuntime.ApplyAmbientOverrides(ambient);
        }
        catch
        {
            // Ambient overrides are best-effort across game versions.
        }
    }

    private static ClimateCondition? GetClimateForReplay(object weatherSystem)
    {
        ClimateCondition? original = GetFieldValue(weatherSystem, "clientClimateCond")
            as ClimateCondition;
        DirectorReplayEnvironmentVisuals? environment = ActiveEnvironment();
        if (original is null || environment is null)
        {
            return original!;
        }

        ClimateCondition clone = CloneClimate(original);
        float rainfall = CurrentPrecipitation(environment);
        clone.Rainfall = rainfall;
        clone.RainCloudOverlay = rainfall;

        // Vanilla lightning suppresses flashes below lightningMinTemp. A
        // replay storm should remain visible even in a cold recorded biome.
        if (environment.Weather == DirectorReplayWeatherMode.Storm)
        {
            clone.Temperature = Math.Max(clone.Temperature, 40f);
        }
        return clone;
    }

    private static void WeatherParticlePrefix(out bool __state)
    {
        // Keep the scope active while recording as well. The capture patch
        // uses it to tag every provider emitted by the weather callback,
        // including splash and dust providers that are plain particles.
        __state = true;
        particleSpawnScopeDepth++;
    }

    private static Exception? WeatherParticleFinalizer(
        bool __state,
        Exception? __exception)
    {
        if (__state && particleSpawnScopeDepth > 0)
        {
            particleSpawnScopeDepth--;
        }
        return __exception;
    }

    private static IEnumerable<CodeInstruction> WeatherInputTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        Type? weatherSystem = AccessTools.TypeByName(
            "Vintagestory.GameContent.WeatherSystemClient");
        FieldInfo? climateField = weatherSystem is null
            ? null
            : AccessTools.Field(weatherSystem, "clientClimateCond");
        MethodInfo? climateResolver = AccessTools.Method(
            typeof(DirectorReplayWeatherVisualPatch),
            nameof(GetClimateForReplay));
        MethodInfo? weatherGetter = AccessTools.PropertyGetter(
            weatherSystem,
            "BlendedWeatherData");
        MethodInfo? weatherResolver = AccessTools.Method(
            typeof(DirectorReplayWeatherVisualPatch),
            nameof(GetBlendedWeatherDataForReplay));
        if ((climateField is null || climateResolver is null)
            && (weatherGetter is null || weatherResolver is null))
        {
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
            }
            yield break;
        }

        foreach (CodeInstruction instruction in instructions)
        {
            if (climateField is not null
                && climateResolver is not null
                && instruction.opcode == OpCodes.Ldfld
                && instruction.operand is FieldInfo field
                && field == climateField)
            {
                // The field load leaves a WeatherSystemClient on the stack;
                // the resolver consumes it and returns the replay copy.
                instruction.opcode = OpCodes.Call;
                instruction.operand = climateResolver;
                yield return instruction;
                continue;
            }

            if (weatherGetter is not null
                && weatherResolver is not null
                && instruction.operand is MethodInfo method
                && method == weatherGetter)
            {
                // Keep the original return type on the stack after the
                // object-based helper, so this works without referencing the
                // optional VSEssentials assembly at compile time.
                yield return new CodeInstruction(OpCodes.Call, weatherResolver);
                yield return new CodeInstruction(OpCodes.Castclass, weatherGetter.ReturnType);
                continue;
            }
            yield return instruction;
        }
    }

    private static object? GetBlendedWeatherDataForReplay(object weatherSystem)
    {
        object? source;
        try
        {
            PropertyInfo? property = weatherSystem.GetType().GetProperty(
                "BlendedWeatherData",
                InstanceFlags);
            source = property?.GetValue(weatherSystem);
        }
        catch
        {
            source = GetFieldValue(weatherSystem, "blendedWeatherDataCached");
        }

        if (source is null || ActiveEnvironment() is not { } environment)
        {
            return source;
        }

        try
        {
            return CreateSnapshotOverride(source, environment);
        }
        catch
        {
            return source;
        }
    }

    private static object CreateSnapshotOverride(
        object source,
        DirectorReplayEnvironmentVisuals environment)
    {
        object clone = CloneSnapshot(source);
        float amount = CurrentPrecipitation(environment);
        string typeName = environment.Weather switch
        {
            DirectorReplayWeatherMode.Snow => "Snow",
            DirectorReplayWeatherMode.Clear => "Auto",
            _ => "Rain"
        };
        SetEnumField(clone, "nowPrecType", typeName);
        SetEnumField(clone, "BlendedPrecType", typeName);
        SetFloatField(clone, "PrecIntensity", amount);
        SetFloatField(clone, "PrecParticleSize", amount);
        float lightningScale = environment.Weather == DirectorReplayWeatherMode.Storm
            ? amount
            : 0f;
        SetFloatField(clone, "nearLightningRate", StormNearLightningRate * lightningScale);
        SetFloatField(
            clone,
            "distantLightningRate",
            StormDistantLightningRate * lightningScale);
        return clone;
    }

    private static void AdvancePrecipitation(
        DirectorReplayEnvironmentVisuals environment,
        float dt)
    {
        float seconds = float.IsFinite(dt)
            ? Math.Clamp(dt, 0f, MaxPrecipitationFrameSeconds)
            : 0f;
        float target = environment.Weather == DirectorReplayWeatherMode.Clear
            ? 0f
            : Math.Clamp(
                float.IsFinite(environment.Precipitation)
                    ? environment.Precipitation
                    : 0f,
                0f,
                1f);
        lock (PrecipitationSync)
        {
            if (!precipitationInitialized || precipitationMode != environment.Weather)
            {
                precipitationMode = environment.Weather;
                precipitation = 0f;
                precipitationInitialized = true;
            }

            float maximumStep = PrecipitationRampPerSecond * seconds;
            precipitation = Math.Clamp(
                target,
                precipitation - maximumStep,
                precipitation + maximumStep);
        }
    }

    private static float CurrentPrecipitation(
        DirectorReplayEnvironmentVisuals environment)
    {
        lock (PrecipitationSync)
        {
            if (!precipitationInitialized || precipitationMode != environment.Weather)
            {
                precipitationMode = environment.Weather;
                precipitation = 0f;
                precipitationInitialized = true;
            }
            return precipitation;
        }
    }

    internal static bool IsRecordedWeatherParticle(DirectorReplayParticleEvent particle)
    {
        if (ActiveEnvironment() is null)
        {
            return false;
        }

        if (particle.IsWeatherParticle)
        {
            return true;
        }

        if (particle.Kind != DirectorReplayParticleKind.Provider)
        {
            return false;
        }

        string providerType = particle.ProviderType?.Trim() ?? string.Empty;
        return providerType.StartsWith(
                "Vintagestory.GameContent.WeatherParticleProps",
                StringComparison.Ordinal)
            || providerType.StartsWith(
                "Vintagestory.GameContent.HailParticleProps",
                StringComparison.Ordinal);
    }

    internal static bool IsRecordedWeatherSound(DirectorReplaySoundEvent sound)
    {
        if (ActiveEnvironment() is null)
        {
            return false;
        }

        if (sound.SoundType == (int)EnumSoundType.Weather)
        {
            return true;
        }

        string location = sound.Location?.Trim().Replace('\\', '/') ?? string.Empty;
        return location.Contains("sounds/weather/", StringComparison.OrdinalIgnoreCase);
    }

    private static object CloneSnapshot(object source)
    {
        object clone = CloneObject(source)!;
        FieldInfo? ambientField = FindField(source.GetType(), "Ambient");
        if (ambientField?.GetValue(source) is AmbientModifier ambient)
        {
            ambientField.SetValue(clone, ambient.Clone());
        }
        FieldInfo? climateField = FindField(source.GetType(), "climateCond");
        if (climateField?.GetValue(source) is object climate)
        {
            object? climateClone = CloneObject(climate);
            if (climateClone is not null)
            {
                climateField.SetValue(clone, climateClone);
            }
        }
        FieldInfo? windField = FindField(source.GetType(), "curWindSpeed");
        if (windField?.GetValue(source) is Vec3f wind)
        {
            windField.SetValue(clone, wind.Clone());
        }
        return clone;
    }

    private static ClimateCondition CloneClimate(ClimateCondition source)
        => (ClimateCondition)CloneObject(source)!;

    private static object? CloneObject(object source)
    {
        try
        {
            MethodInfo? cloneMethod = source.GetType().GetMethod(
                "MemberwiseClone",
                InstanceFlags);
            return cloneMethod?.Invoke(source, null);
        }
        catch
        {
            return null;
        }
    }

    private static DirectorReplayEnvironmentVisuals? ActiveEnvironment()
    {
        DirectorReplayVisualSettings? visuals = DirectorReplayVisualRuntime.Current;
        if (visuals?.Environment is not { } environment
            || environment.Weather == DirectorReplayWeatherMode.Recorded)
        {
            return null;
        }
        return environment;
    }

    private static void SetEnumField(object target, string name, string value)
    {
        FieldInfo? field = FindField(target.GetType(), name);
        if (field is null || !field.FieldType.IsEnum)
        {
            return;
        }
        try
        {
            field.SetValue(target, Enum.Parse(field.FieldType, value, ignoreCase: true));
        }
        catch
        {
        }
    }

    private static void SetFloatField(object target, string name, float value)
    {
        FieldInfo? field = FindField(target.GetType(), name);
        if (field?.FieldType != typeof(float))
        {
            return;
        }
        try
        {
            field.SetValue(target, value);
        }
        catch
        {
        }
    }

    private static object? GetFieldValue(object target, string name)
        => FindField(target.GetType(), name)?.GetValue(target);

    private static FieldInfo? FindField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(name, InstanceFlags);
            if (field is not null)
            {
                return field;
            }
        }
        return null;
    }

    private static void AddPostfix(
        Harmony harmony,
        ILogger logger,
        ICollection<MethodBase> patched,
        Type? type,
        string methodName,
        Type[] argumentTypes,
        string patchMethodName)
    {
        MethodInfo? target = type is null
            ? null
            : AccessTools.Method(type, methodName, argumentTypes);
        AddPostfix(harmony, logger, patched, target, patchMethodName);
    }

    private static void AddPostfix(
        Harmony harmony,
        ILogger logger,
        ICollection<MethodBase> patched,
        MethodInfo? target,
        string patchMethodName)
    {
        if (target is null)
        {
            logger.Debug("VS Director: replay weather hook target was not found.");
            return;
        }
        MethodInfo? postfix = AccessTools.Method(
            typeof(DirectorReplayWeatherVisualPatch),
            patchMethodName);
        if (postfix is null)
        {
            return;
        }
        harmony.Patch(target, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
        patched.Add(target);
    }

    private static void AddTranspiler(
        Harmony harmony,
        ILogger logger,
        ICollection<MethodBase> patched,
        MethodInfo? target)
    {
        if (target is null)
        {
            logger.Debug("VS Director: replay weather input target was not found.");
            return;
        }
        MethodInfo? transpiler = AccessTools.Method(
            typeof(DirectorReplayWeatherVisualPatch),
            nameof(WeatherInputTranspiler));
        if (transpiler is null)
        {
            return;
        }
        harmony.Patch(
            target,
            transpiler: new HarmonyMethod(transpiler) { priority = Priority.Last });
        patched.Add(target);
    }

    private static void AddParticlePatch(
        Harmony harmony,
        ILogger logger,
        ICollection<MethodBase> patched,
        MethodInfo? target)
    {
        if (target is null)
        {
            logger.Debug("VS Director: replay weather particle target was not found.");
            return;
        }
        MethodInfo? prefix = AccessTools.Method(
            typeof(DirectorReplayWeatherVisualPatch),
            nameof(WeatherParticlePrefix));
        MethodInfo? transpiler = AccessTools.Method(
            typeof(DirectorReplayWeatherVisualPatch),
            nameof(WeatherInputTranspiler));
        MethodInfo? finalizer = AccessTools.Method(
            typeof(DirectorReplayWeatherVisualPatch),
            nameof(WeatherParticleFinalizer));
        if (prefix is null || transpiler is null || finalizer is null)
        {
            return;
        }
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(prefix) { priority = Priority.First },
            transpiler: new HarmonyMethod(transpiler) { priority = Priority.Last },
            finalizer: new HarmonyMethod(finalizer) { priority = Priority.Last });
        patched.Add(target);
    }
}
