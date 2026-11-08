using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Replay;

internal static class DirectorReplaySerialization
{
    private const string ReplayAnimationsAttribute = "vsdirectorReplayAnimations";
    private const int ReplayAnimationsFormatVersion = 1;
    private static readonly object AnimationUtilFieldCacheLock = new();
    private static readonly Dictionary<Type, FieldInfo[]> AnimationUtilFields = new();
    private static readonly ConditionalWeakTable<AnimationUtil, AnimationRevision>
        AnimationRevisions = new();
    [ThreadStatic]
    private static Dictionary<BlockEntity, int>? blockEntitySerializationDepths;

    public static byte[] SerializeEntity(
        Entity entity,
        long replayEntityId,
        bool useThirdPersonPlayerAnimations = false)
    {
        ArgumentNullException.ThrowIfNull(entity);
        long originalId = entity.EntityId;
        EntityPlayer? player = entity as EntityPlayer;
        bool originalShadowPass = player?.selfNowShadowPass ?? false;
        try
        {
            // EntityPlayer.ToBytes serializes whichever AnimManager the live
            // camera selects. Replay snapshots are later loaded into a
            // third-person ghost, so recording a local first-person manager
            // here would import its *-fp layers into TpAnimManager.
            if (useThirdPersonPlayerAnimations && player is not null)
            {
                player.selfNowShadowPass = true;
            }
            entity.EntityId = replayEntityId;
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            entity.ToBytes(writer, forClient: true);
            writer.Flush();
            return stream.ToArray();
        }
        finally
        {
            entity.EntityId = originalId;
            if (player is not null)
            {
                player.selfNowShadowPass = originalShadowPass;
            }
        }
    }

    public static byte[] SerializeBlockEntity(BlockEntity blockEntity)
    {
        ArgumentNullException.ThrowIfNull(blockEntity);
        Dictionary<BlockEntity, int> depths = blockEntitySerializationDepths ??=
            new Dictionary<BlockEntity, int>(ReferenceEqualityComparer.Instance);
        depths.TryGetValue(blockEntity, out int depth);
        depths[blockEntity] = depth + 1;
        try
        {
            TreeAttribute tree = new();
            blockEntity.ToTreeAttributes(tree);
            CaptureAnimations(blockEntity, tree);
            ReplayMechanicalPowerState.Capture(blockEntity, tree);
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            tree.ToBytes(writer);
            writer.Flush();
            return stream.ToArray();
        }
        finally
        {
            if (depth == 0)
            {
                depths.Remove(blockEntity);
                if (depths.Count == 0)
                {
                    blockEntitySerializationDepths = null;
                }
            }
            else
            {
                depths[blockEntity] = depth;
            }
        }
    }

    internal static bool IsSerializingBlockEntity(BlockEntity blockEntity)
        => blockEntitySerializationDepths?.ContainsKey(blockEntity) == true;

    public static void ApplyBlockEntityData(
        BlockEntity blockEntity,
        byte[] data,
        IWorldAccessor world,
        bool redrawOnClient = true)
    {
        if (data.Length == 0)
        {
            return;
        }

        using MemoryStream stream = new(data, writable: false);
        using BinaryReader reader = new(stream);
        TreeAttribute tree = new();
        tree.FromBytes(reader);
        blockEntity.FromTreeAttributes(tree, world);
        ApplyAnimations(blockEntity, tree);
        ReplayBlockEntityVisualStateRefresh.RefreshAfterDeserialization(blockEntity, world);
        ReplayMechanicalPowerState.Restore(blockEntity, tree);
        blockEntity.MarkDirty(redrawOnClient);
    }

    internal static Action? CreateActiveAnimationRetessellationCallback(BlockEntity blockEntity)
    {
        List<ActiveAnimationHandoff> activeAnimations = new();
        foreach (AnimationBinding binding in GetAnimationBindings(blockEntity))
        {
            AnimationUtil animation = binding.Animation;
            if (animation.renderer?.ShouldRender == true
                || !HasActiveAnimation(animation))
            {
                continue;
            }

            activeAnimations.Add(
                new ActiveAnimationHandoff(
                    animation,
                    CurrentAnimationRevision(animation),
                    ActiveWhenQueued: true));
        }
        if (activeAnimations.Count == 0)
        {
            return null;
        }

        // Keep the renderer visible when this is still the current handoff,
        // even if a short ease-out ended before upload; that lets AnimationUtil
        // request the final static-mesh rebuild. A newer revision instead wins
        // from its authoritative animation state, so stale callbacks cannot
        // resurrect an old door renderer.
        return () =>
        {
            foreach (ActiveAnimationHandoff handoff in activeAnimations)
            {
                AnimationUtil animation = handoff.Animation;
                if (animation.renderer is not null)
                {
                    animation.renderer.ShouldRender = ResolveAnimationRendererVisibility(
                        animation,
                        handoff.Revision,
                        handoff.ActiveWhenQueued);
                }
            }
        };
    }

    internal static long BeginAnimationStateChange(AnimationUtil animation)
        => AnimationRevisions
            .GetValue(animation, static _ => new AnimationRevision())
            .Begin();

    internal static long CurrentAnimationRevision(AnimationUtil animation)
        => AnimationRevisions
            .GetValue(animation, static _ => new AnimationRevision())
            .Current;

    internal static bool IsAnimationRevisionCurrent(
        AnimationUtil animation,
        long revision)
        => AnimationRevisions
            .GetValue(animation, static _ => new AnimationRevision())
            .IsCurrent(revision);

    internal static void InvalidateAnimationOwner(AnimationUtil animation)
        => AnimationRevisions
            .GetValue(animation, static _ => new AnimationRevision())
            .Invalidate();

    internal static void ReviveAnimationOwner(AnimationUtil animation)
        => AnimationRevisions
            .GetValue(animation, static _ => new AnimationRevision())
            .Revive();

    internal static bool ResolveAnimationRendererVisibility(
        AnimationUtil animation,
        long revision,
        bool activeWhenQueued)
    {
        AnimationRevision state = AnimationRevisions
            .GetValue(animation, static _ => new AnimationRevision());
        if (!state.IsAlive)
        {
            return false;
        }

        bool activeNow = HasActiveAnimation(animation);
        return state.IsCurrent(revision)
            ? activeWhenQueued || activeNow
            : activeNow;
    }

    internal static void InvalidateAnimationOwners(BlockEntity blockEntity)
    {
        foreach (AnimationBinding binding in GetAnimationBindings(blockEntity))
        {
            ReplayBlockEntityStateCapturePatch.UnregisterAnimationOwner(binding.Animation);
        }
    }

    internal static void RegisterAnimationOwners(BlockEntity blockEntity)
    {
        foreach (AnimationBinding binding in GetAnimationBindings(blockEntity))
        {
            ReplayBlockEntityStateCapturePatch.RegisterAnimationOwner(binding.Animation, blockEntity);
        }
    }

    private static void CaptureAnimations(BlockEntity blockEntity, TreeAttribute tree)
    {
        AnimationBinding[] bindings = GetAnimationBindings(blockEntity)
            .OrderBy(binding => binding.Key, StringComparer.Ordinal)
            .ToArray();
        if (bindings.Length == 0)
        {
            return;
        }

        TreeAttribute animationTree = new();
        animationTree.SetInt("version", ReplayAnimationsFormatVersion);
        animationTree.SetInt("count", bindings.Length);
        for (int bindingIndex = 0; bindingIndex < bindings.Length; bindingIndex++)
        {
            AnimationBinding binding = bindings[bindingIndex];
            ReplayBlockEntityStateCapturePatch.RegisterAnimationOwner(binding.Animation, blockEntity);
            TreeAttribute bindingTree = new();
            bindingTree.SetString("key", binding.Key);
            KeyValuePair<string, AnimationMetaData>[] animations =
                binding.Animation.activeAnimationsByAnimCode
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToArray();
            bindingTree.SetInt("count", animations.Length);
            for (int animationIndex = 0; animationIndex < animations.Length; animationIndex++)
            {
                bindingTree.SetBytes(
                    animationIndex.ToString(CultureInfo.InvariantCulture),
                    SerializeAnimation(animations[animationIndex].Key, animations[animationIndex].Value));
            }
            animationTree.SetAttribute(
                bindingIndex.ToString(CultureInfo.InvariantCulture),
                bindingTree);
        }

        tree.SetAttribute(ReplayAnimationsAttribute, animationTree);
    }

    private static void ApplyAnimations(BlockEntity blockEntity, TreeAttribute tree)
    {
        ITreeAttribute? animationTree = tree.GetTreeAttribute(ReplayAnimationsAttribute);
        if (animationTree is null
            || animationTree.GetInt("version", 0) != ReplayAnimationsFormatVersion)
        {
            return;
        }

        Dictionary<string, AnimationUtil> currentBindings = GetAnimationBindings(blockEntity)
            .ToDictionary(binding => binding.Key, binding => binding.Animation, StringComparer.Ordinal);
        Dictionary<string, List<CapturedAnimation>> capturedBindings = new(StringComparer.Ordinal);
        int bindingCount = Math.Max(0, animationTree.GetInt("count", 0));
        for (int bindingIndex = 0; bindingIndex < bindingCount; bindingIndex++)
        {
            ITreeAttribute? bindingTree = animationTree.GetTreeAttribute(
                bindingIndex.ToString(CultureInfo.InvariantCulture));
            string? key = bindingTree?.GetString("key");
            if (bindingTree is null || string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            List<CapturedAnimation> animations = new();
            int animationCount = Math.Max(0, bindingTree.GetInt("count", 0));
            for (int animationIndex = 0; animationIndex < animationCount; animationIndex++)
            {
                byte[]? data = bindingTree.GetBytes(
                    animationIndex.ToString(CultureInfo.InvariantCulture));
                if (TryDeserializeAnimation(data, out CapturedAnimation animation))
                {
                    animations.Add(animation);
                }
            }
            capturedBindings[key] = animations;
        }

        foreach ((string key, AnimationUtil animationUtil) in currentBindings)
        {
            ReplayBlockEntityStateCapturePatch.RegisterAnimationOwner(animationUtil, blockEntity);
            IReadOnlyList<CapturedAnimation> captured = capturedBindings.TryGetValue(
                key,
                out List<CapturedAnimation>? found)
                ? found
                : Array.Empty<CapturedAnimation>();
            Dictionary<string, CapturedAnimation> target = captured
                .GroupBy(animation => animation.Metadata.Code, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            foreach (string activeCode in animationUtil.activeAnimationsByAnimCode.Keys.ToArray())
            {
                if (!target.ContainsKey(activeCode))
                {
                    animationUtil.StopAnimation(activeCode);
                }
            }

            foreach ((string code, CapturedAnimation animation) in target)
            {
                if (animationUtil.activeAnimationsByAnimCode.TryGetValue(
                        code,
                        out AnimationMetaData? current)
                    && SerializeAnimation(code, current).AsSpan().SequenceEqual(animation.Data))
                {
                    continue;
                }

                animationUtil.StopAnimation(code);
                animationUtil.StartAnimation(animation.Metadata);
            }
        }
    }

    private static byte[] SerializeAnimation(string code, AnimationMetaData metadata)
    {
        AnimationMetaData snapshot = metadata.Clone();
        snapshot.Code = string.IsNullOrWhiteSpace(snapshot.Code) ? code : snapshot.Code;
        snapshot.Animation ??= snapshot.Code;
        snapshot.ElementWeight ??= new Dictionary<string, float>();
        snapshot.ElementBlendMode ??= new Dictionary<string, EnumAnimationBlendMode>(
            StringComparer.OrdinalIgnoreCase);
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        snapshot.ToBytes(writer);
        writer.Flush();
        return stream.ToArray();
    }

    private static bool TryDeserializeAnimation(
        byte[]? data,
        out CapturedAnimation animation)
    {
        animation = default;
        if (data is null || data.Length == 0)
        {
            return false;
        }

        try
        {
            using MemoryStream stream = new(data, writable: false);
            using BinaryReader reader = new(stream);
            AnimationMetaData metadata = AnimationMetaData.FromBytes(reader, GameVersion.ShortGameVersion);
            if (string.IsNullOrWhiteSpace(metadata.Code))
            {
                return false;
            }
            animation = new CapturedAnimation(metadata, data);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasActiveAnimation(AnimationUtil animation)
        => animation.activeAnimationsByAnimCode.Count > 0
            || (animation.animator is not null && animation.animator.ActiveAnimationCount > 0);

    private static IEnumerable<AnimationBinding> GetAnimationBindings(BlockEntity blockEntity)
    {
        HashSet<AnimationUtil> seen = new(ReferenceEqualityComparer.Instance);
        for (int behaviorIndex = 0; behaviorIndex < blockEntity.Behaviors.Count; behaviorIndex++)
        {
            BlockEntityBehavior behavior = blockEntity.Behaviors[behaviorIndex];
            foreach (FieldInfo field in GetAnimationUtilFields(behavior.GetType()))
            {
                if (field.GetValue(behavior) is AnimationUtil animation && seen.Add(animation))
                {
                    yield return new AnimationBinding(
                        $"behavior/{behaviorIndex}/{field.DeclaringType?.FullName}/{field.Name}",
                        animation);
                }
            }
        }

        foreach (FieldInfo field in GetAnimationUtilFields(blockEntity.GetType()))
        {
            if (field.GetValue(blockEntity) is AnimationUtil animation && seen.Add(animation))
            {
                yield return new AnimationBinding(
                    $"entity/{field.DeclaringType?.FullName}/{field.Name}",
                    animation);
            }
        }
    }

    private static FieldInfo[] GetAnimationUtilFields(Type ownerType)
    {
        lock (AnimationUtilFieldCacheLock)
        {
            if (AnimationUtilFields.TryGetValue(ownerType, out FieldInfo[]? cached))
            {
                return cached;
            }

            List<FieldInfo> fields = new();
            for (Type? current = ownerType; current is not null; current = current.BaseType)
            {
                fields.AddRange(current
                    .GetFields(
                        BindingFlags.Instance
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly)
                    .Where(field => typeof(AnimationUtil).IsAssignableFrom(field.FieldType)));
            }

            FieldInfo[] result = fields
                .OrderBy(field => field.DeclaringType?.FullName, StringComparer.Ordinal)
                .ThenBy(field => field.Name, StringComparer.Ordinal)
                .ToArray();
            AnimationUtilFields[ownerType] = result;
            return result;
        }
    }

    public static byte[] SerializeParticle(IParticlePropertiesProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        provider.ToBytes(writer);
        writer.Flush();
        return stream.ToArray();
    }

    public static DirectorReplayBlockEntitySnapshot CaptureBlockEntity(
        ICoreAPI api,
        BlockPos position,
        BlockEntity blockEntity)
    {
        return new DirectorReplayBlockEntitySnapshot
        {
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            Dimension = position.dimension,
            ClassName = api.ClassRegistry.GetBlockEntityClass(blockEntity.GetType()) ?? string.Empty,
            Data = SerializeBlockEntity(blockEntity)
        };
    }

    private readonly record struct AnimationBinding(string Key, AnimationUtil Animation);

    private readonly record struct ActiveAnimationHandoff(
        AnimationUtil Animation,
        long Revision,
        bool ActiveWhenQueued);

    private readonly record struct CapturedAnimation(AnimationMetaData Metadata, byte[] Data);

    private sealed class AnimationRevision
    {
        private long generation;
        private int alive = 1;

        internal long Current => Volatile.Read(ref generation);

        internal bool IsAlive => Volatile.Read(ref alive) != 0;

        internal long Begin()
            => Interlocked.Increment(ref generation);

        internal bool IsCurrent(long revision)
            => IsAlive && Volatile.Read(ref generation) == revision;

        internal void Invalidate()
        {
            Volatile.Write(ref alive, 0);
            Interlocked.Increment(ref generation);
        }

        internal void Revive()
            => Volatile.Write(ref alive, 1);
    }
}
