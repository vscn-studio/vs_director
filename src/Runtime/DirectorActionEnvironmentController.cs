using System.Collections;
using System.Globalization;
using System.Reflection;
using Vintagestory.API.Client;

namespace VSDirector.Runtime;

internal sealed class DirectorActionEnvironmentController
{
    private const string WeatherSystemClientName = "Vintagestory.GameContent.WeatherSystemClient";

    private readonly ICoreClientAPI capi;
    private object? weatherSystem;
    private PropertyInfo? precipitationProperty;
    private object? clientWorld;
    private FieldInfo? clientTimelapsedCurrentField;
    private FieldInfo? clientTimelapseStepField;
    private string activeKey = string.Empty;
    private bool snapshotTaken;
    private bool precipitationSnapshotTaken;
    private bool seasonSnapshotTaken;
    private bool timelapseSnapshotTaken;
    private bool clientTimelapsedCurrentSnapshotTaken;
    private bool clientTimelapseStepSnapshotTaken;
    private object? savedPrecipitation;
    private float? savedSeason;
    private float savedTimelapse;
    private float savedClientTimelapsedCurrent;
    private float savedClientTimelapseStep;

    public DirectorActionEnvironmentController(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void Apply(string code, string data, Guid cardId)
    {
        string normalizedCode = code?.Trim().ToLowerInvariant() ?? string.Empty;
        string normalizedData = data?.Trim() ?? string.Empty;
        string key = cardId.ToString("N") + ":" + normalizedCode + ":" + normalizedData;
        bool continuousVisualOverride = normalizedCode is "time" or "season";
        if (key == activeKey && !continuousVisualOverride)
        {
            return;
        }
        if (!snapshotTaken)
        {
            try
            {
                TakeSnapshot();
            }
            catch (Exception exception)
            {
                snapshotTaken = true;
                LogFailure("snapshot", exception);
            }
        }
        activeKey = key;
        try
        {
            switch (normalizedCode)
            {
                case "time":
                    ApplyTime(normalizedData);
                    break;
                case "season":
                    ApplySeason(normalizedData);
                    break;
                case "clear":
                    SetPrecipitation(0f);
                    break;
                case "rain":
                case "snow":
                case "precipitation":
                    SetPrecipitation(ParseFloat(normalizedData, 1f, 0f, 1f));
                    break;
                case "weather":
                case "weather-pattern":
                    ApplyWeatherMethod("SetWeatherPattern", normalizedData);
                    break;
                case "weather-event":
                    ApplyWeatherMethod("SetWeatherEvent", normalizedData);
                    break;
            }
        }
        catch (Exception exception)
        {
            LogFailure(normalizedCode, exception);
        }
    }

    public void Restore()
    {
        activeKey = string.Empty;
        if (!snapshotTaken)
        {
            return;
        }

        try
        {
            if (precipitationSnapshotTaken
                && IsValidPropertyTarget(precipitationProperty, weatherSystem, requireWrite: true))
            {
                precipitationProperty!.SetValue(weatherSystem, savedPrecipitation);
            }
        }
        catch (Exception exception)
        {
            LogFailure("precipitation restore", exception);
        }
        try
        {
            if (seasonSnapshotTaken)
            {
                capi.World.Calendar.SeasonOverride = savedSeason;
            }
        }
        catch (Exception exception)
        {
            LogFailure("season restore", exception);
        }
        try
        {
            if (timelapseSnapshotTaken)
            {
                capi.World.Calendar.Timelapse = savedTimelapse;
            }
        }
        catch (Exception exception)
        {
            LogFailure("time restore", exception);
        }
        try
        {
            if (clientTimelapsedCurrentSnapshotTaken
                && IsValidFieldTarget(clientTimelapsedCurrentField, clientWorld))
            {
                clientTimelapsedCurrentField!.SetValue(clientWorld, savedClientTimelapsedCurrent);
            }
            if (clientTimelapseStepSnapshotTaken
                && IsValidFieldTarget(clientTimelapseStepField, clientWorld))
            {
                clientTimelapseStepField!.SetValue(clientWorld, savedClientTimelapseStep);
            }
        }
        catch (Exception exception)
        {
            LogFailure("client time restore", exception);
        }

        snapshotTaken = false;
        precipitationSnapshotTaken = false;
        seasonSnapshotTaken = false;
        timelapseSnapshotTaken = false;
        clientTimelapsedCurrentSnapshotTaken = false;
        clientTimelapseStepSnapshotTaken = false;
        savedPrecipitation = null;
    }

    private void TakeSnapshot()
    {
        snapshotTaken = true;
        precipitationSnapshotTaken = false;
        seasonSnapshotTaken = false;
        timelapseSnapshotTaken = false;
        clientTimelapsedCurrentSnapshotTaken = false;
        clientTimelapseStepSnapshotTaken = false;
        savedPrecipitation = null;

        try
        {
            ResolveWeatherSystem();
            if (IsValidPropertyTarget(precipitationProperty, weatherSystem, requireWrite: false))
            {
                savedPrecipitation = precipitationProperty!.GetValue(weatherSystem);
                precipitationSnapshotTaken = true;
            }
        }
        catch (Exception exception)
        {
            LogFailure("precipitation snapshot", exception);
        }
        try
        {
            savedSeason = capi.World.Calendar.SeasonOverride;
            seasonSnapshotTaken = true;
        }
        catch (Exception exception)
        {
            LogFailure("season snapshot", exception);
        }
        try
        {
            savedTimelapse = capi.World.Calendar.Timelapse;
            timelapseSnapshotTaken = true;
        }
        catch (Exception exception)
        {
            LogFailure("time snapshot", exception);
        }
        try
        {
            ResolveClientTimeFields();
            if (IsValidFieldTarget(clientTimelapsedCurrentField, clientWorld)
                && clientTimelapsedCurrentField!.GetValue(clientWorld) is float current)
            {
                savedClientTimelapsedCurrent = current;
                clientTimelapsedCurrentSnapshotTaken = true;
            }
            if (IsValidFieldTarget(clientTimelapseStepField, clientWorld)
                && clientTimelapseStepField!.GetValue(clientWorld) is float step)
            {
                savedClientTimelapseStep = step;
                clientTimelapseStepSnapshotTaken = true;
            }
        }
        catch (Exception exception)
        {
            LogFailure("client time snapshot", exception);
        }
    }

    private void ApplyTime(string value)
    {
        float hoursPerDay = Math.Max(1f, capi.World.Calendar.HoursPerDay);
        float desired = ParseFloat(value, capi.World.Calendar.HourOfDay, 0f, hoursPerDay);
        float timelapse = CalculateTimelapseForHour(
            capi.World.Calendar.TotalHours,
            capi.World.Calendar.Timelapse,
            hoursPerDay,
            desired);
        capi.World.Calendar.Timelapse = timelapse;
        ResolveClientTimeFields();
        if (IsValidFieldTarget(clientTimelapsedCurrentField, clientWorld))
        {
            clientTimelapsedCurrentField!.SetValue(clientWorld, timelapse);
        }
        if (IsValidFieldTarget(clientTimelapseStepField, clientWorld))
        {
            clientTimelapseStepField!.SetValue(clientWorld, 0f);
        }
    }

    private void ApplySeason(string value)
    {
        float season = ResolveSeasonOverride(value, capi.World.Calendar.SeasonOverride ?? 0.33f);
        capi.World.Calendar.SeasonOverride = season;
    }

    private void SetPrecipitation(float value)
    {
        ResolveWeatherSystem();
        if (IsValidPropertyTarget(precipitationProperty, weatherSystem, requireWrite: true))
        {
            precipitationProperty!.SetValue(weatherSystem, (float?)value);
        }
    }

    private void ApplyWeatherMethod(string methodName, string code)
    {
        if (code.Length == 0)
        {
            return;
        }
        ResolveWeatherSystem();
        object? target = weatherSystem;
        if (target is null)
        {
            return;
        }
        FieldInfo? regionsField = target.GetType().GetField(
            "weatherSimByMapRegion",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        if (regionsField is null
            || regionsField.DeclaringType?.IsInstanceOfType(target) != true
            || regionsField.GetValue(target) is not IDictionary regions)
        {
            return;
        }
        foreach (object? region in regions.Values)
        {
            if (region is null)
            {
                continue;
            }
            try
            {
                region.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null,
                    types: new[] { typeof(string), typeof(bool) },
                    modifiers: null)
                    ?.Invoke(region, new object[] { code, true });
            }
            catch (Exception exception)
            {
                LogFailure(methodName, exception);
            }
        }
    }

    private void ResolveWeatherSystem()
    {
        if (weatherSystem is not null)
        {
            return;
        }

        precipitationProperty = null;
        object? resolved = capi.ModLoader.GetModSystem(WeatherSystemClientName);
        if (resolved is null)
        {
            return;
        }

        weatherSystem = resolved;
        PropertyInfo? property = resolved.GetType().GetProperty(
            "OverridePrecipitation",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        if (!IsValidPropertyTarget(property, resolved, requireWrite: false))
        {
            return;
        }

        precipitationProperty = property;
    }

    private void ResolveClientTimeFields()
    {
        object world = capi.World;
        if (ReferenceEquals(clientWorld, world)
            && clientTimelapsedCurrentField is not null
            && clientTimelapseStepField is not null)
        {
            return;
        }

        clientWorld = world;
        Type type = world.GetType();
        clientTimelapsedCurrentField = type.GetField(
            "timelapsedCurrent",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        clientTimelapseStepField = type.GetField(
            "timelapse",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
    }

    private static float ResolveSeasonOverride(string value, float fallback)
        => value.Trim().ToLowerInvariant() switch
        {
            "spring" => 0.33f,
            "summer" => 0.60f,
            "autum" or "autumn" or "fall" => 0.77f,
            "winter" => 0.05f,
            _ => ParseFloat(value, fallback, 0f, 1f)
        };

    private static float CalculateTimelapseForHour(
        double totalHours,
        float currentTimelapse,
        float hoursPerDay,
        float desiredHour)
    {
        double dayHours = Math.Max(1d, hoursPerDay);
        double rawDays = totalHours / dayHours;
        double visualDays = rawDays + currentTimelapse;
        double visualDayStart = Math.Floor(visualDays);
        double desiredVisualDays = visualDayStart + Math.Clamp(desiredHour, 0f, hoursPerDay) / dayHours;
        return (float)(desiredVisualDays - rawDays);
    }

    private static bool IsValidPropertyTarget(PropertyInfo? property, object? target, bool requireWrite)
        => property is not null
            && target is not null
            && property.CanRead
            && (!requireWrite || property.CanWrite)
            && property.GetMethod?.IsStatic == false
            && property.DeclaringType?.IsInstanceOfType(target) == true;

    private static bool IsValidFieldTarget(FieldInfo? field, object? target)
        => field is not null
            && target is not null
            && !field.IsStatic
            && field.DeclaringType?.IsInstanceOfType(target) == true;

    private void LogFailure(string operation, Exception exception)
    {
        try
        {
            capi.Logger.Debug(
                "VS Director: action environment {0} could not be completed: {1}",
                operation,
                exception.GetBaseException().Message);
        }
        catch
        {
            // Environment overrides run on the render thread and must never escape through logging.
        }
    }

    private static float ParseFloat(string value, float fallback, float minimum, float maximum)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            && float.IsFinite(parsed)
                ? Math.Clamp(parsed, minimum, maximum)
                : fallback;
}
