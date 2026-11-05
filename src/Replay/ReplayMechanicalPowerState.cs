using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VSDirector.Replay;

// Mechanical block entities serialize their network id but the client-side
// network speed is delivered separately through live packets. Replay has no
// recorded packet stream, so preserve the small amount of network state needed
// for renderers to keep moving while a recorded topology is active.
internal static class ReplayMechanicalPowerState
{
    private const string BehaviorTypeName =
        "Vintagestory.GameContent.Mechanics.BEBehaviorMPBase";
    private const string StateAttributeName = "vsdirectorReplayMechanicalPower";
    private const int StateFormatVersion = 1;

    private static readonly ConcurrentDictionary<Type, MechanicalNetworkBindings>
        NetworkBindingCache = new();
    private static Type? behaviorType;
    private static FieldInfo? networkField;
    private static PropertyInfo? networkIdProperty;

    internal static bool IsAvailable
    {
        get
        {
            Type? type = ResolveBehaviorType();
            return type is not null
                && ResolveNetworkField(type) is not null
                && ResolveNetworkIdProperty(type) is not null;
        }
    }

    internal static void Capture(BlockEntity blockEntity, TreeAttribute tree)
    {
        ArgumentNullException.ThrowIfNull(blockEntity);
        ArgumentNullException.ThrowIfNull(tree);

        Type? type = ResolveBehaviorType();
        FieldInfo? resolvedNetworkField = type is null ? null : ResolveNetworkField(type);
        PropertyInfo? resolvedNetworkIdProperty = type is null
            ? null
            : ResolveNetworkIdProperty(type);
        if (type is null || resolvedNetworkField is null || resolvedNetworkIdProperty is null)
        {
            return;
        }

        TreeAttribute stateTree = new();
        int stateCount = 0;
        for (int behaviorIndex = 0; behaviorIndex < blockEntity.Behaviors.Count; behaviorIndex++)
        {
            BlockEntityBehavior behavior = blockEntity.Behaviors[behaviorIndex];
            if (!type.IsInstanceOfType(behavior))
            {
                continue;
            }

            object? network = resolvedNetworkField.GetValue(behavior);
            if (network is null
                || !TryReadNetworkId(resolvedNetworkIdProperty, behavior, out long networkId)
                || networkId <= 0)
            {
                continue;
            }

            MechanicalNetworkBindings bindings = NetworkBindingCache.GetOrAdd(
                network.GetType(),
                ResolveNetworkBindings);
            if (bindings.Speed is null || bindings.TurnDirection is null)
            {
                continue;
            }

            float speed = ReadFiniteFloat(bindings.Speed.GetValue(network));
            int turnDirection = ReadInt(bindings.TurnDirection.GetValue(network));
            TreeAttribute behaviorTree = new();
            behaviorTree.SetInt("behaviorIndex", behaviorIndex);
            behaviorTree.SetLong("networkId", networkId);
            behaviorTree.SetFloat("speed", speed);
            behaviorTree.SetInt("turnDirection", turnDirection);
            stateTree.SetAttribute(
                stateCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                behaviorTree);
            stateCount++;
        }

        if (stateCount == 0)
        {
            return;
        }

        stateTree.SetInt("version", StateFormatVersion);
        stateTree.SetInt("count", stateCount);
        tree.SetAttribute(StateAttributeName, stateTree);
    }

    internal static int Restore(BlockEntity blockEntity, ITreeAttribute tree)
    {
        ArgumentNullException.ThrowIfNull(blockEntity);
        ArgumentNullException.ThrowIfNull(tree);

        ITreeAttribute? stateTree = tree.GetTreeAttribute(StateAttributeName);
        if (stateTree is null || stateTree.GetInt("version", 0) != StateFormatVersion)
        {
            return 0;
        }

        Type? type = ResolveBehaviorType();
        FieldInfo? resolvedNetworkField = type is null ? null : ResolveNetworkField(type);
        PropertyInfo? resolvedNetworkIdProperty = type is null
            ? null
            : ResolveNetworkIdProperty(type);
        if (type is null || resolvedNetworkField is null || resolvedNetworkIdProperty is null)
        {
            return 0;
        }

        int restored = 0;
        int stateCount = Math.Max(0, stateTree.GetInt("count", 0));
        for (int stateIndex = 0; stateIndex < stateCount; stateIndex++)
        {
            ITreeAttribute? behaviorTree = stateTree.GetTreeAttribute(
                stateIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (behaviorTree is null)
            {
                continue;
            }

            int behaviorIndex = behaviorTree.GetInt("behaviorIndex", -1);
            if (behaviorIndex < 0 || behaviorIndex >= blockEntity.Behaviors.Count)
            {
                continue;
            }

            BlockEntityBehavior behavior = blockEntity.Behaviors[behaviorIndex];
            if (!type.IsInstanceOfType(behavior)
                || !TryReadNetworkId(resolvedNetworkIdProperty, behavior, out long networkId)
                || networkId != behaviorTree.GetLong("networkId", 0L))
            {
                continue;
            }

            object? network = resolvedNetworkField.GetValue(behavior);
            if (network is null)
            {
                continue;
            }

            MechanicalNetworkBindings bindings = NetworkBindingCache.GetOrAdd(
                network.GetType(),
                ResolveNetworkBindings);
            if (bindings.Speed is null || bindings.TurnDirection is null)
            {
                continue;
            }

            float speed = ReadFiniteFloat(behaviorTree.GetFloat("speed", 0f));
            bindings.Speed.SetValue(network, speed);
            if (bindings.ClientSpeed is not null)
            {
                bindings.ClientSpeed.SetValue(network, speed);
            }

            int turnDirection = behaviorTree.GetInt("turnDirection", 0);
            bindings.TurnDirection.SetValue(
                network,
                Enum.ToObject(bindings.TurnDirection.PropertyType, turnDirection));
            bindings.DirectionHasReversed?.SetValue(network, false);
            restored++;
        }

        return restored;
    }

    private static bool TryReadNetworkId(
        PropertyInfo networkId,
        object behavior,
        out long value)
    {
        object? raw = networkId.GetValue(behavior);
        if (raw is long longValue)
        {
            value = longValue;
            return true;
        }

        try
        {
            value = Convert.ToInt64(raw);
            return true;
        }
        catch
        {
            value = 0L;
            return false;
        }
    }

    private static float ReadFiniteFloat(object? value)
    {
        float result = value is float floatValue
            ? floatValue
            : Convert.ToSingle(value ?? 0f);
        return float.IsFinite(result) ? result : 0f;
    }

    private static int ReadInt(object? value)
    {
        try
        {
            return Convert.ToInt32(value ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    private static Type? ResolveBehaviorType()
    {
        Type? resolved = Volatile.Read(ref behaviorType);
        if (resolved is not null)
        {
            return resolved;
        }

        resolved = AccessTools.TypeByName(BehaviorTypeName);
        if (resolved is not null)
        {
            Interlocked.CompareExchange(ref behaviorType, resolved, null);
        }
        return resolved;
    }

    private static FieldInfo? ResolveNetworkField(Type type)
    {
        FieldInfo? resolved = Volatile.Read(ref networkField);
        if (resolved is not null)
        {
            return resolved;
        }

        resolved = AccessTools.Field(type, "network");
        if (resolved is not null)
        {
            Interlocked.CompareExchange(ref networkField, resolved, null);
        }
        return resolved;
    }

    private static PropertyInfo? ResolveNetworkIdProperty(Type type)
    {
        PropertyInfo? resolved = Volatile.Read(ref networkIdProperty);
        if (resolved is not null)
        {
            return resolved;
        }

        resolved = AccessTools.Property(type, "NetworkId");
        if (resolved is not null)
        {
            Interlocked.CompareExchange(ref networkIdProperty, resolved, null);
        }
        return resolved;
    }

    private static MechanicalNetworkBindings ResolveNetworkBindings(Type networkType)
        => new(
            AccessTools.Property(networkType, "Speed"),
            AccessTools.Property(networkType, "TurnDir"),
            AccessTools.Field(networkType, "clientSpeed"),
            AccessTools.Property(networkType, "DirectionHasReversed"));

    private sealed record MechanicalNetworkBindings(
        PropertyInfo? Speed,
        PropertyInfo? TurnDirection,
        FieldInfo? ClientSpeed,
        PropertyInfo? DirectionHasReversed);
}
