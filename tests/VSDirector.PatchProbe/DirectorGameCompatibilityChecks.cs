using System.Collections;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using VSDirector;
using VSDirector.Data;

internal static class DirectorGameCompatibilityChecks
{
    private sealed class TestEntity : Entity { }
    private sealed class TestBlockEntity : BlockEntity { }
    private static readonly Type Adapter = typeof(VSDirectorModSystem).Assembly.GetType("VSDirector.Compatibility.DirectorGameCompatibility", true)!;
    internal static TreeAttribute ReadTree(byte[] bytes) => (TreeAttribute)AccessTools.Method(Adapter, "Tree").Invoke(null, [bytes])!;
    internal static AnimationMetaData ReadAnimation(byte[] bytes) => (AnimationMetaData)AccessTools.Method(Adapter, "Animation")
        .Invoke(null, [bytes, Vintagestory.API.Config.GameVersion.ShortGameVersion])!;
    internal static AnimationManager Animation(IAnimator? animator = null)
    {
        var constructor = typeof(AnimationManager).GetConstructor([typeof(Entity)]);
        var manager = (AnimationManager)(constructor is null
            ? Activator.CreateInstance(typeof(AnimationManager))! : constructor.Invoke([new TestEntity()]));
        manager.Animator = animator!;
        return manager;
    }

    internal static void Run(string? storageFixture = null)
    {
        var tree = new TreeAttribute();
        tree.SetString("name", "回放"); tree.SetLong("entity", 9876543210);
        tree.GetOrAddTreeAttribute("nested").SetBytes("payload", [1, 7, 255]);
        Check(ReadTree(tree.ToBytes()).ToBytes().SequenceEqual(tree.ToBytes()), "Native tree payload did not round trip");
        try { ReadTree([255]); throw new Exception("Malformed tree payload was accepted"); }
        catch (TargetInvocationException e) when (e.InnerException is not TypeInitializationException) { }

        var item = new Item { ItemId = 42, Code = new AssetLocation("game:compatibility-probe") };
        var world = ProbeDispatchProxy.Create<IWorldAccessor>((m, _) => m.Name == "GetItem" ? item : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var stack = new ItemStack(item, 3); stack.Attributes.SetString("quality", "preserved");
        var restored = (ItemStack)AccessTools.Method(Adapter, "Item").Invoke(null, [stack.ToBytes(), world])!;
        Check(restored.Item == item && restored.StackSize == 3 && restored.Attributes.GetString("quality") == "preserved",
            "Item reader lost item resolution, quantity or attributes");
        var particles = new SimpleParticleProperties(2, 4, -1, new Vec3d(1, 2, 3), new Vec3d(4, 5, 6),
            new Vec3f(1, 2, 3), new Vec3f(4, 5, 6), 2, .5f) { MinSize = .1f, MaxSize = .9f };
        byte[] Bytes(IParticlePropertiesProvider provider)
        { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); provider.ToBytes(writer); return stream.ToArray(); }
        var copy = new SimpleParticleProperties();
        AccessTools.Method(Adapter, "ParticleState").Invoke(null, [copy, Bytes(particles), world]);
        Check(Bytes(copy).SequenceEqual(Bytes(particles)), "Native particle payload did not round trip");

        // Exercise the real entity reader, including its version-specific
        // constructor/animation path, rather than only resolving the method.
        var entity = new TestEntity { EntityId = 123, Code = new AssetLocation("game:compatibility-probe") };
        entity.WatchedAttributes.SetString("probe", "kept");
        entity.Pos.SetPos(-32, 70, 64);
        using var entityStream = new MemoryStream();
        using var entityWriter = new BinaryWriter(entityStream);
        entity.ToBytes(entityWriter, true);
        var entityCopy = new TestEntity();
        AccessTools.Method(Adapter, "EntityState").Invoke(null, [entityCopy, entityStream.ToArray(), true]);
        Check(entityCopy.EntityId == 123 && entityCopy.WatchedAttributes.GetString("probe") == "kept"
            && entityCopy.Pos.X == -32, "Native entity state did not round trip");
        if (storageFixture is not null)
        {
            if (!File.Exists(storageFixture))
            {
                var recording = new DirectorReplayDocument { RecordedGameVersion = Vintagestory.API.Config.GameVersion.LongGameVersion,
                    NativePayloadVersion = DirectorReplayDocument.CurrentNativePayloadVersion };
                var actor = new DirectorReplayActorTrack { ActorKey = "probe", InitialState = entityStream.ToArray() };
                actor.Samples.Add(new() { X = -32, RightHandItem = stack.ToBytes() });
                actor.Samples.Add(new() { TimeSeconds = 5, X = 64, RightHandItem = stack.ToBytes() });
                recording.Actors.Add(actor);
                recording.WorldSnapshot.BlockEntities.Add(new() { ClassName = "probe", Data = tree.ToBytes() });
                DirectorReplayIO.Save(storageFixture, recording);
            }
            var opened = DirectorReplayIO.LoadPagedAsync(storageFixture).GetAwaiter().GetResult();
            var recordedActor = opened.Actors.Single();
            var native = new TestEntity();
            AccessTools.Method(Adapter, "EntityState").Invoke(null, [native, recordedActor.InitialState, true]);
            var recordedStack = (ItemStack)AccessTools.Method(Adapter, "Item").Invoke(null, [recordedActor.Samples[1].RightHandItem, world])!;
            Check(native.WatchedAttributes.GetString("probe") == "kept" && recordedStack.Attributes.GetString("quality") == "preserved"
                && recordedActor.Samples[0].X == -32 && ReadTree(opened.WorldSnapshot.BlockEntities[0].Data).GetString("name") == "回放",
                "Reading a native payload from the indexed recording failed");
            Console.WriteLine($"PASS indexed fixture {opened.RecordedGameVersion} on {Vintagestory.API.Config.GameVersion.LongGameVersion}");
        }

        var entitiesType = typeof(IWorldChunk).GetProperty("BlockEntities")!.PropertyType;
        var entities = (IDictionary)Activator.CreateInstance(entitiesType)!;
        var first = new TestBlockEntity { Pos = new BlockPos(-33, 70, 64, 2) };
        var second = new TestBlockEntity { Pos = new BlockPos(-32, 71, 65, 2) };
        entities.Add(entitiesType.GenericTypeArguments[0] == typeof(int) ? (object)1 : first.Pos, first);
        entities.Add(entitiesType.GenericTypeArguments[0] == typeof(int) ? (object)2 : second.Pos, second);
        var chunk = ProbeDispatchProxy.Create<IWorldChunk>((m, _) => m.Name == "get_BlockEntities" ? entities : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var found = ((IEnumerable<BlockEntity>)AccessTools.Method(Adapter, "BlockEntities").Invoke(null, [chunk])!).ToArray();
        Check(found.Length == 2 && found.Contains(first) && found.Contains(second) && first.Pos.dimension == 2,
            "Chunk adaptation lost block entities or their dimension");
        Check(!((IEnumerable<BlockEntity>)AccessTools.Method(Adapter, "BlockEntities").Invoke(null, [null])!).Any(), "Unloaded chunk was not empty");
        Console.WriteLine("PASS version-adapted native tree/item/entity/particle reads and block-entity enumeration on " + typeof(Entity).Assembly.GetName().Version);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
