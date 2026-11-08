using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;

namespace VSDirector.Replay;

// Some block-entity behaviors deserialize their inventory without rebuilding
// the CPU mesh cached from that inventory. The vanilla interaction path does
// both operations, but replay state restoration only has the serialized tree.
internal static class ReplayBlockEntityVisualStateRefresh
{
    private const string MannequinBehaviorTypeName =
        "Vintagestory.GameContent.BEBehaviorMannequin";
    private const string MannequinRefreshMethodName = "regenMesh";
    private const string ConstructableBehaviorTypeName =
        "Vintagestory.GameContent.BEBehaviorRightClickConstructable";
    private const string ConstructableRefreshMethodName = "updateShape";
    private const string MechanicalPowerBehaviorTypeName =
        "Vintagestory.GameContent.Mechanics.BEBehaviorMPBase";
    private const string MechanicalPowerRefreshMethodName = "updateShape";

    private static readonly ConcurrentDictionary<Type, byte> ReportedFailures = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> MannequinRefreshMethods = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> ConstructableRefreshMethods = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> MechanicalPowerRefreshMethods = new();
    private static Type? mannequinBehaviorType;
    private static Type? constructableBehaviorType;
    private static Type? mechanicalPowerBehaviorType;

    internal static bool IsAvailable
        => ResolveMannequinBehaviorType() is { } type
            && ResolveMannequinRefreshMethod(type) is not null;

    internal static bool IsConstructableRefreshAvailable
        => ResolveConstructableBehaviorType() is { } type
            && ResolveConstructableRefreshMethod(type) is not null;

    internal static bool IsMechanicalPowerRefreshAvailable
        => ResolveMechanicalPowerBehaviorType() is { } type
            && ResolveMechanicalPowerRefreshMethod(type) is not null;

    internal static int RefreshAfterDeserialization(
        BlockEntity blockEntity,
        IWorldAccessor world)
    {
        ArgumentNullException.ThrowIfNull(blockEntity);
        ArgumentNullException.ThrowIfNull(world);
        int refreshed = 0;

        // Constructable behaviors restore their stage data without rebuilding
        // the derived shape. Do this first so the mechanical behavior can read
        // the completed water-wheel shape instead of its placement shape.
        foreach (BlockEntityBehavior behavior in blockEntity.Behaviors)
        {
            if (TryRefreshConstructableBehavior(behavior, out Exception? failure))
            {
                refreshed++;
            }
            else if (failure is not null)
            {
                ReportFailure(
                    blockEntity,
                    behavior.GetType(),
                    "constructable shape",
                    failure);
            }
        }

        Type? mannequinType = ResolveMannequinBehaviorType();
        foreach (BlockEntityBehavior behavior in blockEntity.Behaviors)
        {
            if (mannequinType?.IsInstanceOfType(behavior) != true)
            {
                continue;
            }

            if (TryRefreshMannequinBehavior(behavior, out Exception? failure))
            {
                refreshed++;
                continue;
            }

            if (failure is not null)
            {
                ReportFailure(blockEntity, behavior.GetType(), "mannequin mesh", failure);
            }
        }

        // Mechanical behaviors cache their own shape separately. Refresh that
        // cache after the constructable stage; the vanilla Shape setter then
        // replaces its renderer entry when the shape changed.
        foreach (BlockEntityBehavior behavior in blockEntity.Behaviors)
        {
            if (TryRefreshMechanicalPowerBehavior(behavior, world, out Exception? failure))
            {
                refreshed++;
            }
            else if (failure is not null)
            {
                ReportFailure(
                    blockEntity,
                    behavior.GetType(),
                    "mechanical-power renderer",
                    failure);
            }
        }
        return refreshed;
    }

    internal static bool TryRefreshConstructableBehavior(object behavior)
        => TryRefreshConstructableBehavior(behavior, out _);

    private static bool TryRefreshConstructableBehavior(
        object behavior,
        out Exception? failure)
    {
        failure = null;
        Type? constructableType = ResolveConstructableBehaviorType();
        if (constructableType?.IsInstanceOfType(behavior) != true)
        {
            return false;
        }

        MethodInfo? refresh = ResolveConstructableRefreshMethod(behavior.GetType());
        if (refresh is null)
        {
            failure = new MissingMethodException(
                behavior.GetType().FullName,
                ConstructableRefreshMethodName);
            return false;
        }

        try
        {
            refresh.Invoke(behavior, null);
            return true;
        }
        catch (Exception exception)
        {
            failure = exception;
            return false;
        }
    }

    internal static bool TryRefreshMechanicalPowerBehavior(
        object behavior,
        IWorldAccessor world)
        => TryRefreshMechanicalPowerBehavior(behavior, world, out _);

    private static bool TryRefreshMechanicalPowerBehavior(
        object behavior,
        IWorldAccessor world,
        out Exception? failure)
    {
        failure = null;
        Type? mechanicalType = ResolveMechanicalPowerBehaviorType();
        if (mechanicalType?.IsInstanceOfType(behavior) != true)
        {
            return false;
        }

        MethodInfo? refresh = ResolveMechanicalPowerRefreshMethod(behavior.GetType());
        if (refresh is null)
        {
            failure = new MissingMethodException(
                behavior.GetType().FullName,
                MechanicalPowerRefreshMethodName);
            return false;
        }

        try
        {
            refresh.Invoke(behavior, new object[] { world });
            return true;
        }
        catch (Exception exception)
        {
            failure = exception;
            return false;
        }
    }

    internal static bool TryRefreshMannequinBehavior(object behavior)
        => TryRefreshMannequinBehavior(behavior, out _);

    private static bool TryRefreshMannequinBehavior(
        object behavior,
        out Exception? failure)
    {
        failure = null;
        Type? mannequinType = ResolveMannequinBehaviorType();
        if (mannequinType?.IsInstanceOfType(behavior) != true)
        {
            return false;
        }

        MethodInfo? refresh = ResolveMannequinRefreshMethod(behavior.GetType());
        if (refresh is null)
        {
            failure = new MissingMethodException(
                behavior.GetType().FullName,
                MannequinRefreshMethodName);
            return false;
        }

        try
        {
            refresh.Invoke(behavior, null);
            return true;
        }
        catch (Exception exception)
        {
            failure = exception;
            return false;
        }
    }

    private static Type? ResolveMannequinBehaviorType()
    {
        Type? resolved = Volatile.Read(ref mannequinBehaviorType);
        if (resolved is not null)
        {
            return resolved;
        }

        resolved = AccessTools.TypeByName(MannequinBehaviorTypeName);
        if (resolved is not null)
        {
            Interlocked.CompareExchange(ref mannequinBehaviorType, resolved, null);
        }
        return resolved;
    }

    private static MethodInfo? ResolveMannequinRefreshMethod(Type behaviorType)
    {
        if (MannequinRefreshMethods.TryGetValue(behaviorType, out MethodInfo? cached))
        {
            return cached;
        }

        MethodInfo? resolved = AccessTools.Method(
            behaviorType,
            MannequinRefreshMethodName,
            Type.EmptyTypes);
        return resolved is null
            ? null
            : MannequinRefreshMethods.GetOrAdd(behaviorType, resolved);
    }

    private static Type? ResolveConstructableBehaviorType()
    {
        Type? resolved = Volatile.Read(ref constructableBehaviorType);
        if (resolved is not null)
        {
            return resolved;
        }

        resolved = AccessTools.TypeByName(ConstructableBehaviorTypeName);
        if (resolved is not null)
        {
            Interlocked.CompareExchange(ref constructableBehaviorType, resolved, null);
        }
        return resolved;
    }

    private static MethodInfo? ResolveConstructableRefreshMethod(Type behaviorType)
    {
        if (ConstructableRefreshMethods.TryGetValue(behaviorType, out MethodInfo? cached))
        {
            return cached;
        }

        MethodInfo? resolved = AccessTools.Method(
            behaviorType,
            ConstructableRefreshMethodName,
            Type.EmptyTypes);
        return resolved is null
            ? null
            : ConstructableRefreshMethods.GetOrAdd(behaviorType, resolved);
    }

    private static Type? ResolveMechanicalPowerBehaviorType()
    {
        Type? resolved = Volatile.Read(ref mechanicalPowerBehaviorType);
        if (resolved is not null)
        {
            return resolved;
        }

        resolved = AccessTools.TypeByName(MechanicalPowerBehaviorTypeName);
        if (resolved is not null)
        {
            Interlocked.CompareExchange(ref mechanicalPowerBehaviorType, resolved, null);
        }
        return resolved;
    }

    private static MethodInfo? ResolveMechanicalPowerRefreshMethod(Type behaviorType)
    {
        if (MechanicalPowerRefreshMethods.TryGetValue(behaviorType, out MethodInfo? cached))
        {
            return cached;
        }

        MethodInfo? resolved = AccessTools.Method(
            behaviorType,
            MechanicalPowerRefreshMethodName,
            new[] { typeof(IWorldAccessor) });
        return resolved is null
            ? null
            : MechanicalPowerRefreshMethods.GetOrAdd(behaviorType, resolved);
    }

    private static void ReportFailure(
        BlockEntity blockEntity,
        Type behaviorType,
        string visualState,
        Exception failure)
    {
        if (ReportedFailures.TryAdd(behaviorType, 0))
        {
            blockEntity.Api?.Logger.Warning(
                "VS Director: {0} could not be rebuilt after replay state restore: {1}",
                visualState,
                failure.GetBaseException().Message);
        }
    }
}
