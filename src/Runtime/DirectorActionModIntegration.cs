using System.Collections;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using VSDirector.Data;

namespace VSDirector.Runtime;

internal sealed record DirectorExternalModelInfo(
    string Code,
    string EntityCode,
    string Name,
    string Group,
    string Icon,
    IReadOnlyList<string> Animations,
    IReadOnlyList<string> Expressions,
    bool PreviewOnly = false);

internal sealed record DirectorExternalAnimationInfo(
    AnimationMetaData Metadata,
    int QuantityFrames,
    EnumEntityAnimationEndHandling EndHandling);

internal sealed record DirectorExternalActionInfo(
    string Code,
    string Name,
    string Animation,
    bool RequiresTarget,
    float PairDistance,
    string SourceModId,
    double DurationSeconds,
    float AnimationSpeed,
    float Weight,
    float EaseInSpeed,
    float EaseOutSpeed,
    int BlendMode);

internal sealed class DirectorActionModIntegration
{
    private readonly ICoreClientAPI capi;

    public DirectorActionModIntegration(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public bool HasPlayerModelLibrary => capi.ModLoader.IsModEnabled("playermodellib");
    public bool HasEmotes => capi.ModLoader.IsModEnabled("emotes");

    public IReadOnlyList<DirectorExternalModelInfo> DiscoverModels()
    {
        object? system = GetModSystem("PlayerModelLib.CustomModelsSystem");
        if (system is null || !BoolProperty(system, "ModelsLoaded"))
        {
            return Array.Empty<DirectorExternalModelInfo>();
        }
        object? models = system?.GetType().GetProperty("CustomModels")?.GetValue(system);
        if (models is not IEnumerable enumerable)
        {
            return Array.Empty<DirectorExternalModelInfo>();
        }

        List<DirectorExternalModelInfo> result = new();
        foreach (object? entry in enumerable)
        {
            object? key = entry?.GetType().GetProperty("Key")?.GetValue(entry);
            object? value = entry?.GetType().GetProperty("Value")?.GetValue(entry);
            string code = key?.ToString()?.Trim() ?? string.Empty;
            if (code.Length == 0 || value is null || !BoolProperty(value, "Enabled", true))
            {
                continue;
            }
            string name = TextProperty(value, "Name", code);
            string group = TextProperty(value, "Group", string.Empty);
            string icon = TextProperty(value, "Icon", string.Empty);
            IReadOnlyList<string> animations = DictionaryKeys(
                value.GetType().GetProperty("AnimationsMetaData")?.GetValue(value));
            IReadOnlyList<string> expressions = DiscoverExpressions(value);
            result.Add(new DirectorExternalModelInfo(
                code,
                VSDirectorModSystem.VanillaPlayerModelCode,
                name,
                group,
                icon,
                animations,
                expressions));
        }
        return result.OrderBy(model => model.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public DirectorExternalAnimationInfo? ResolveModelAnimation(
        string modelCode,
        string animationCode)
    {
        object? model = FindExternalModel(modelCode);
        if (model is null)
        {
            return null;
        }
        string requested = animationCode?.Trim() ?? string.Empty;
        AnimationMetaData? metadata = null;
        object? animationsMetadata = model.GetType().GetProperty("AnimationsMetaData")?.GetValue(model);
        if (animationsMetadata is IEnumerable entries)
        {
            foreach (object? entry in entries)
            {
                string key = entry?.GetType().GetProperty("Key")?.GetValue(entry)?.ToString()?.Trim()
                    ?? string.Empty;
                AnimationMetaData? candidate = entry?.GetType().GetProperty("Value")?.GetValue(entry)
                    as AnimationMetaData;
                if (candidate is not null
                    && (string.Equals(key, requested, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidate.Code, requested, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidate.Animation, requested, StringComparison.OrdinalIgnoreCase)))
                {
                    metadata = candidate.Clone();
                    break;
                }
            }
        }

        Shape? shape = model.GetType().GetProperty("Shape")?.GetValue(model) as Shape;
        string shapeAnimationCode = string.IsNullOrWhiteSpace(metadata?.Animation)
            ? requested
            : metadata.Animation;
        Animation? animation = shape?.Animations?.FirstOrDefault(candidate => candidate is not null
            && (string.Equals(candidate.Code, shapeAnimationCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Name, shapeAnimationCode, StringComparison.OrdinalIgnoreCase)));
        if (metadata is null && animation is null)
        {
            return null;
        }
        metadata ??= new AnimationMetaData
        {
            Code = requested,
            Animation = animation?.Code ?? requested
        };
        metadata.Code = string.IsNullOrWhiteSpace(metadata.Code) ? requested : metadata.Code;
        metadata.Animation = string.IsNullOrWhiteSpace(metadata.Animation)
            ? animation?.Code ?? requested
            : metadata.Animation;
        return new DirectorExternalAnimationInfo(
            metadata,
            Math.Max(2, animation?.QuantityFrames ?? 31),
            animation?.OnAnimationEnd ?? EnumEntityAnimationEndHandling.Repeat);
    }

    public IReadOnlyList<DirectorExternalActionInfo> DiscoverEmotes()
    {
        Type? type = FindType("Emotes.EmotesModSystem");
        MethodInfo? method = type?.GetMethod("GetEmotes", BindingFlags.Public | BindingFlags.Static);
        if (method?.Invoke(null, null) is not IEnumerable enumerable)
        {
            return Array.Empty<DirectorExternalActionInfo>();
        }

        List<DirectorExternalActionInfo> result = new();
        foreach (object? entry in enumerable)
        {
            object? value = entry?.GetType().GetProperty("Value")?.GetValue(entry);
            if (value is null)
            {
                continue;
            }
            string code = TextProperty(value, "Code", string.Empty);
            string animation = TextProperty(value, "Animation", code);
            if (code.Length == 0 || animation.Length == 0)
            {
                continue;
            }
            AnimationMetaData? metadata = ResolvePlayerAnimationMetadata(animation);
            result.Add(new DirectorExternalActionInfo(
                code,
                TextProperty(value, "DisplayName", TextProperty(value, "Name", code)),
                animation,
                BoolProperty(value, "RequiresTarget"),
                FloatProperty(value, "PairDistance", 0.5f),
                "emotes",
                ResolvePlayerAnimationDuration(animation, metadata),
                metadata?.AnimationSpeed ?? 1f,
                metadata?.Weight ?? 1f,
                metadata?.EaseInSpeed ?? 10f,
                metadata?.EaseOutSpeed ?? 10f,
                (int)(metadata?.BlendMode ?? EnumAnimationBlendMode.Add)));
        }
        return result.OrderBy(action => action.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool TryApplyModel(Entity entity, string modelCode, float size = 1f)
    {
        if (string.IsNullOrWhiteSpace(modelCode))
        {
            return false;
        }
        string requestedModel = modelCode.Trim();
        bool vanillaPlayer = string.Equals(
            requestedModel,
            VSDirectorModSystem.VanillaPlayerModelCode,
            StringComparison.OrdinalIgnoreCase);
        string directEntityCode = requestedModel.StartsWith("entity/", StringComparison.OrdinalIgnoreCase)
            ? requestedModel["entity/".Length..]
            : requestedModel;
        bool directEntityModel = string.Equals(
            directEntityCode,
            entity.Code?.ToShortString(),
            StringComparison.OrdinalIgnoreCase);
        if (directEntityModel && !vanillaPlayer)
        {
            ApplyEntityTypeSize(entity, size);
            return true;
        }
        if (vanillaPlayer)
        {
            ApplyEntityTypeSize(entity, size);
            return true;
        }
        if (!HasPlayerModelLibrary)
        {
            return false;
        }
        try
        {
            object? behavior = entity.GetBehavior("skinnableplayercustommodel");
            if (behavior is null)
            {
                return false;
            }
            MethodInfo? setCurrentModel = behavior.GetType().GetMethod(
                "SetCurrentModel",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(string), typeof(float) },
                modifiers: null);
            if (setCurrentModel is null)
            {
                return false;
            }
            string normalizedCode = requestedModel;
            float normalizedSize = Math.Clamp(size, 0.1f, 10f);
            entity.WatchedAttributes.SetString("skinModel", normalizedCode);
            entity.WatchedAttributes.SetFloat("entitySize", normalizedSize);
            entity.WatchedAttributes.MarkPathDirty("skinModel");
            entity.WatchedAttributes.MarkPathDirty("entitySize");
            setCurrentModel.Invoke(behavior, new object[] { normalizedCode, normalizedSize });
            behavior.GetType().GetMethod("UpdateEntityProperties", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(behavior, null);
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("VS Director: PlayerModelLib model {0} could not be applied: {1}", modelCode, exception.Message);
            return false;
        }
    }

    private void ApplyEntityTypeSize(Entity entity, float size)
    {
        if (entity.Properties?.Client is null)
        {
            return;
        }
        float baseSize = entity.Code is null
            ? 1f
            : capi.World.GetEntityType(entity.Code)?.Client?.Size ?? 1f;
        entity.Properties.Client.Size = baseSize * Math.Clamp(size, 0.1f, 10f);
    }

    public bool TryApplyExpression(Entity entity, string expressionCode)
    {
        if (string.IsNullOrWhiteSpace(expressionCode))
        {
            return false;
        }
        string expression = expressionCode.Trim();
        try
        {
            if (HasPlayerModelLibrary
                && TrySelectExpression(
                    entity.GetBehavior("skinnableplayercustommodel"),
                    "SelectSkinPart",
                    expression))
            {
                entity.WatchedAttributes.MarkPathDirty("skinConfig");
                return true;
            }
            bool applied = TrySelectExpression(
                entity.GetBehavior("skinnableplayer") ?? entity.GetBehavior("extraskinnable"),
                "selectSkinPart",
                expression);
            if (applied)
            {
                entity.WatchedAttributes.MarkPathDirty("skinConfig");
            }
            return applied;
        }
        catch (Exception exception)
        {
            capi.Logger.Debug(
                "VS Director: expression {0} could not be applied: {1}",
                expressionCode,
                exception.Message);
            return false;
        }
    }

    public IReadOnlyList<string> DiscoverVanillaExpressions(Entity? entity)
    {
        object? behavior = entity?.GetBehavior("skinnableplayer")
            ?? entity?.GetBehavior("extraskinnable");
        if (behavior is null)
        {
            return Array.Empty<string>();
        }
        object? parts = MemberValue(behavior, "AvailableSkinPartsByCode");
        object? expressionPart = null;
        if (parts is IEnumerable entries)
        {
            foreach (object? entry in entries)
            {
                string key = MemberValue(entry, "Key")?.ToString()?.Trim() ?? string.Empty;
                object? value = MemberValue(entry, "Value");
                if (string.Equals(key, "facialexpression", StringComparison.OrdinalIgnoreCase))
                {
                    expressionPart = value;
                    break;
                }
                if (value is null
                    && string.Equals(
                        MemberValue(entry, "Code")?.ToString(),
                        "facialexpression",
                        StringComparison.OrdinalIgnoreCase))
                {
                    expressionPart = entry;
                    break;
                }
            }
        }
        if (expressionPart is null)
        {
            object? availableParts = MemberValue(behavior, "AvailableSkinParts");
            if (availableParts is IEnumerable availableEntries)
            {
                expressionPart = availableEntries.Cast<object?>().FirstOrDefault(part => string.Equals(
                    MemberValue(part, "Code")?.ToString(),
                    "facialexpression",
                    StringComparison.OrdinalIgnoreCase));
            }
        }
        object? variants = MemberValue(expressionPart, "Variants");
        return variants is IEnumerable variantEntries
            ? variantEntries.Cast<object?>()
                .Select(variant => MemberValue(variant, "Code")?.ToString()?.Trim() ?? string.Empty)
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();
    }

    private static bool TrySelectExpression(object? behavior, string methodName, string expression)
    {
        MethodInfo? method = behavior?.GetType().GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(bool), typeof(bool) },
            modifiers: null);
        if (method is null)
        {
            return false;
        }
        method.Invoke(behavior, new object[] { "facialexpression", expression, true, false });
        return true;
    }

    private object? GetModSystem(string fullTypeName)
    {
        Type? type = FindType(fullTypeName);
        MethodInfo? method = capi.ModLoader.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(candidate => candidate.Name == "GetModSystem"
                && candidate.IsGenericMethodDefinition
                && candidate.GetParameters().Length == 0);
        return type is null || method is null ? null : method.MakeGenericMethod(type).Invoke(capi.ModLoader, null);
    }

    private static Type? FindType(string fullName)
        => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(type => type is not null);

    private AnimationMetaData? ResolvePlayerAnimationMetadata(string configCode)
    {
        EntityPlayer? player = capi.World.Player?.Entity;
        if (player?.Properties?.Client?.AnimationsByMetaCode is not { } animations)
        {
            return null;
        }
        return animations.TryGetValue(configCode, out AnimationMetaData? metadata)
            ? metadata
            : animations.Values.FirstOrDefault(item =>
                string.Equals(item.Code, configCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Animation, configCode, StringComparison.OrdinalIgnoreCase));
    }

    private double ResolvePlayerAnimationDuration(string animationCode, AnimationMetaData? metadata)
    {
        EntityPlayer? player = capi.World.Player?.Entity;
        RunningAnimation? running = player?.TpAnimManager?.Animator?.Animations?.FirstOrDefault(item =>
            string.Equals(item.Animation?.Code, animationCode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Animation?.Code, metadata?.Animation, StringComparison.OrdinalIgnoreCase));
        int frames = Math.Max(2, running?.Animation?.QuantityFrames ?? 31);
        float speed = metadata is not null && float.IsFinite(metadata.AnimationSpeed)
            ? Math.Abs(metadata.AnimationSpeed)
            : 1f;
        return (frames - 1) / (30d * Math.Max(0.01f, speed));
    }

    private static IReadOnlyList<string> DiscoverExpressions(object modelData)
    {
        object? skinParts = modelData.GetType().GetProperty("SkinParts")?.GetValue(modelData);
        if (skinParts is not IEnumerable enumerable)
        {
            return Array.Empty<string>();
        }
        foreach (object? entry in enumerable)
        {
            string key = entry?.GetType().GetProperty("Key")?.GetValue(entry)?.ToString() ?? string.Empty;
            if (!key.Equals("facialexpression", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            object? value = entry?.GetType().GetProperty("Value")?.GetValue(entry);
            object? variants = value?.GetType().GetProperty("Variants")?.GetValue(value);
            if (variants is not IEnumerable variantEnumerable)
            {
                return Array.Empty<string>();
            }
            return variantEnumerable.Cast<object?>()
                .Select(variant => TextProperty(variant, "Code", string.Empty))
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        return Array.Empty<string>();
    }

    private object? FindExternalModel(string modelCode)
    {
        object? system = GetModSystem("PlayerModelLib.CustomModelsSystem");
        object? models = system?.GetType().GetProperty("CustomModels")?.GetValue(system);
        if (models is not IEnumerable entries)
        {
            return null;
        }
        string requested = modelCode?.Trim() ?? string.Empty;
        foreach (object? entry in entries)
        {
            string key = entry?.GetType().GetProperty("Key")?.GetValue(entry)?.ToString()?.Trim()
                ?? string.Empty;
            if (string.Equals(key, requested, StringComparison.OrdinalIgnoreCase))
            {
                return entry?.GetType().GetProperty("Value")?.GetValue(entry);
            }
        }
        return null;
    }

    private static IReadOnlyList<string> DictionaryKeys(object? dictionary)
    {
        if (dictionary is not IEnumerable enumerable)
        {
            return Array.Empty<string>();
        }
        return enumerable.Cast<object?>()
            .Select(entry => entry?.GetType().GetProperty("Key")?.GetValue(entry)?.ToString()?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string TextProperty(object? value, string property, string fallback)
        => MemberValue(value, property)?.ToString()?.Trim() is { Length: > 0 } text
            ? text
            : fallback;

    private static object? MemberValue(object? value, string member)
        => value?.GetType().GetProperty(
                member,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(value)
            ?? value?.GetType().GetField(
                member,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(value);

    private static bool BoolProperty(object value, string property)
        => value.GetType().GetProperty(property)?.GetValue(value) is bool result && result;

    private static bool BoolProperty(object value, string property, bool fallback)
        => value.GetType().GetProperty(property)?.GetValue(value) is bool result ? result : fallback;

    private static float FloatProperty(object value, string property, float fallback)
        => value.GetType().GetProperty(property)?.GetValue(value) is float result && float.IsFinite(result)
            ? result
            : fallback;
}
