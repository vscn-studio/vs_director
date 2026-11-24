using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;
using VSDirector;
using VSDirector.Data;

internal static class DirectorReplayActionChecks
{
    internal static void Run()
    {
        var type = typeof(VSDirectorModSystem).Assembly.GetType("VSDirector.Replay.DirectorReplayManager", true)!;
        var bindingType = type.GetNestedType("ActorBinding", BindingFlags.NonPublic)!;
        var player = new EntityPlayer();
        var binding = Activator.CreateInstance(bindingType, [new DirectorReplayActorTrack(), player,
            new DirectorReplayActorSample(), Array.Empty<byte>(), false, false, null, true, null, false, null, null])!;
        var animator = new ClientAnimator(() => 1d,
            new[] { "throw", "throwaim", "walk" }.Select(code => new Animation { Code = code, QuantityFrames = 30 }).ToArray(),
            Array.Empty<ShapeElement>(), new Dictionary<int, AnimationJoint>()) { CalculateMatrices = false };
        var manager = DirectorGameCompatibilityChecks.Animation(animator);
        var throwMeta = new AnimationMetaData { Code = "throw", Animation = "throw" };
        var aimMeta = new AnimationMetaData { Code = "aim", Animation = "throwaim" };
        var walkMeta = new AnimationMetaData { Code = "walk", Animation = "walk" };
        var trigger = AccessTools.Field(typeof(AnimationMetaData), "TriggeredBy");
        trigger.SetValue(walkMeta, Activator.CreateInstance(trigger.FieldType));
        AccessTools.Property(typeof(Entity), "Properties").SetValue(player, new EntityProperties { Client = new EntityClientProperties([], new())
        {
            Animations = [throwMeta, aimMeta, walkMeta],
            AnimationsByMetaCode = new Dictionary<string, AnimationMetaData> { ["throw"] = throwMeta, ["aim"] = aimMeta, ["walk"] = walkMeta }
        } });
        void Sync(params DirectorReplayAnimationSample[] states)
            => AccessTools.Method(type, "SyncReplayPlayerAnimations").Invoke(null,
                [binding, player, manager, new DirectorReplayActorSample { AnimationStates = states.ToList() }]);
        RunningAnimation Seed(AnimationMetaData meta, bool active = true)
        {
            if (active) manager.ActiveAnimationsByAnimCode[meta.Animation] = meta;
            var running = animator.GetAnimationState(meta.Animation);
            running.meta = meta; running.Active = active; running.Running = true;
            running.EasingFactor = 1; running.CurrentFrame = 15;
            return running;
        }

        var walk = Seed(walkMeta); var thrown = Seed(throwMeta);
        Sync(new DirectorReplayAnimationSample { Code = "throw", StateCode = "throw" });
        if (!thrown.Active || !walk.Active) throw new Exception("Recorded throw or control-driven walk was removed");
        // This is the real tracking reset used on seek/visibility/full-state refresh.
        AccessTools.Method(type, "ResetReplayAnimationTracking").Invoke(null, [binding]);
        Sync(new DirectorReplayAnimationSample { Code = "walk", StateCode = "walk" });
        if (manager.ActiveAnimationsByAnimCode.ContainsKey("throw") || thrown.Active || thrown.Running || thrown.EasingFactor != 0)
            throw new Exception("Throw survived after tracking reset and an idle/walk sample");
        if (!walk.Active) throw new Exception("Throw cleanup removed observer locomotion");

        // Entity deserialization can insert an aim layer we never tracked.
        var aim = Seed(aimMeta);
        Sync();
        if (manager.ActiveAnimationsByAnimCode.ContainsKey("throwaim") || aim.Running)
            throw new Exception("Untracked serialized aim survived an empty sample");
        // StopAnimation removes the dictionary entry before the layer has faded.
        thrown = Seed(throwMeta, active: false);
        Sync();
        if (thrown.Running || thrown.EasingFactor != 0) throw new Exception("Orphan fading throw is frozen on a paused replay frame");
        for (int i = 0; i < 60; i++) Sync();
        if (!walk.Active || thrown.Running || aim.Running) throw new Exception("Paused action cleanup is not stable");
        Console.WriteLine("PASS throw/aim exit after seek tracking reset, serialized orphan actions, paused fade cleanup and preserved locomotion");
    }
}
