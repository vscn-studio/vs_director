using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using VSDirector;
using VSDirector.Data;
using Vintagestory.Client.NoObf;

internal static class DirectorCreationRecordingChecks
{
    internal static void Run()
    {
        var assembly = typeof(VSDirectorModSystem).Assembly;
        var managerType = assembly.GetType("VSDirector.Replay.DirectorReplayManager", true)!;
        var runtimeType = assembly.GetType("VSDirector.Replay.DirectorReplayRuntime", true)!;
        var managerProperty = AccessTools.Property(runtimeType, "Manager");
        var ordinaryProperty = AccessTools.Property(runtimeType, "OrdinaryManager");
        var creationProperty = AccessTools.Property(runtimeType, "CreationManager");
        object? savedOrdinary = ordinaryProperty.GetValue(null);
        object? savedCreation = creationProperty.GetValue(null);
        object ordinary = RuntimeHelpers.GetUninitializedObject(managerType);
        object creation = RuntimeHelpers.GetUninitializedObject(managerType);
        object otherCreation = RuntimeHelpers.GetUninitializedObject(managerType);
        string root = Path.Combine(Path.GetTempPath(), "director-creation-owner-" + Guid.NewGuid().ToString("N"));
        AccessTools.Field(managerType, "creationProjectDirectory").SetValue(creation, root);
        AccessTools.Field(managerType, "creationProjectDirectory").SetValue(otherCreation, root + "-other");
        void Activate(object value) => AccessTools.Method(runtimeType, "ActivateCreationSession").Invoke(null, [value]);
        void Release(object value) => AccessTools.Method(runtimeType, "ReleaseCreationSession").Invoke(null, [value]);
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        bool Rejects(Action action, Type expected)
        {
            try { action(); return false; }
            catch (TargetInvocationException exception) when (exception.InnerException is { } inner && expected.IsInstanceOfType(inner))
            { return true; }
        }
        try
        {
            managerProperty.SetValue(null, null);
            Activate(creation);
            managerProperty.SetValue(null, ordinary);
            Check(ReferenceEquals(managerProperty.GetValue(null), creation)
                && ReferenceEquals(ordinaryProperty.GetValue(null), ordinary),
                "Creation hooks did not retain an owner separate from the ordinary replay catalog.");
            Check(Rejects(() => Activate(otherCreation), typeof(InvalidOperationException)),
                "A second creation session replaced the active world owner.");

            object?[] recordArguments = ["not-a-replay-entry", null];
            Check(!(bool)AccessTools.Method(managerType, "StartRecording").Invoke(ordinary, recordArguments)!
                && Equals(recordArguments[1], "creation-recording-unavailable"),
                "Ordinary recording could start while a creation session owns the world.");
            object?[] playbackArguments = ["unused", 0, null, false];
            Check(!(bool)AccessTools.Method(managerType, "StartPlayback").Invoke(ordinary, playbackArguments)!
                && Equals(playbackArguments[2], "creation-recording-unavailable"),
                "Ordinary playback could displace a creation world.");
            object?[] actionArguments = ["", "unused", null, null];
            Check(!(bool)AccessTools.Method(managerType, "StartLiveActionRecording").Invoke(ordinary, actionArguments)!
                && Equals(actionArguments[3], "creation-recording-unavailable"),
                "Ordinary actor capture could acquire a second clock during creation capture.");

            var load = AccessTools.Method(managerType, "StartCreationPlayback");
            object?[] outsideArguments = [Path.Combine(root, "..", "outside.vsdreplay.gz"), "outside", "", null];
            Check(Rejects(() => load.Invoke(creation, outsideArguments), typeof(InvalidDataException)),
                "A project-owned recording accepted an outside asset path.");
            object?[] missingArguments = [Path.Combine(root, "recordings", "missing.vsdreplay.gz"), "missing", "", null];
            Check(!(bool)load.Invoke(creation, missingArguments)!
                && Equals(missingArguments[3], "creation-performance-unavailable"),
                "A missing project recording was accepted for playback.");

            Release(ordinary);
            Check(ReferenceEquals(managerProperty.GetValue(null), creation),
                "An unrelated manager released creation ownership.");
            Release(creation);
            Check(ReferenceEquals(managerProperty.GetValue(null), ordinary) && creationProperty.GetValue(null) is null,
                "Stopping creation did not return hooks to the ordinary replay manager.");
            Check(Rejects(() => Activate(ordinary), typeof(InvalidOperationException)),
                "An ordinary manager was accepted as a project-owned session.");

            var playbackType = managerType.GetNestedType("Playback", BindingFlags.NonPublic)!;
            managerProperty.SetValue(null, null);
            CheckParticlePlayback(assembly, managerType, playbackType, creation, () => Activate(creation));
            managerProperty.SetValue(null, ordinary);
            object playback = RuntimeHelpers.GetUninitializedObject(playbackType);
            AccessTools.Field(managerType, "playback").SetValue(ordinary, playback);
            Check(Rejects(() => Activate(creation), typeof(InvalidOperationException))
                && ReferenceEquals(AccessTools.Field(managerType, "playback").GetValue(ordinary), playback),
                "Creation silently displaced an ordinary playback session.");
            Console.WriteLine("PASS creation recording ownership, ordinary replay/capture exclusion, project path boundaries and owner restoration");
        }
        finally
        {
            managerProperty.SetValue(null, savedOrdinary);
            creationProperty.SetValue(null, savedCreation);
        }
    }

    private static void CheckParticlePlayback(Assembly assembly, Type managerType, Type playbackType,
        object manager, Action activate)
    {
        object playback = RuntimeHelpers.GetUninitializedObject(playbackType);
        AccessTools.Field(playbackType, "<Document>k__BackingField").SetValue(playback,
            new DirectorReplayDocument { ParticleCaptureVersion = DirectorReplayDocument.CurrentParticleCaptureVersion });
        AccessTools.Property(playbackType, "Paused").SetValue(playback, true);
        AccessTools.Field(managerType, "playback").SetValue(manager, playback);
        AccessTools.Field(managerType, "creationPlaybackAdvancing").SetValue(manager, true);
        AccessTools.Field(managerType, "replayParticleFrameClock").SetValue(manager,
            new DirectorReplayParticleFrameClock<ParticlePoolQuads>());
        var rendererType = assembly.GetType("VSDirector.Rendering.DirectorDedicatedReplayRenderer", true)!;
        object renderer = RuntimeHelpers.GetUninitializedObject(rendererType);
        AccessTools.Field(rendererType, "active").SetValue(renderer, true);
        object pool = RuntimeHelpers.GetUninitializedObject(typeof(ParticlePoolQuads));
        AccessTools.Field(rendererType, "quadPool").SetValue(renderer, pool);
        AccessTools.Field(managerType, "dedicatedReplayRenderer").SetValue(manager, renderer);
        var exporterType = AccessTools.Field(managerType, "exporter").FieldType;
        AccessTools.Field(managerType, "exporter").SetValue(manager, RuntimeHelpers.GetUninitializedObject(exporterType));
        activate();

        var pending = AccessTools.Property(playbackType, "PendingParticleDeltaSeconds");
        var publish = AccessTools.Method(managerType, "BeginReplayParticleRenderFrame");
        var consume = AccessTools.Method(managerType, "ReplayParticlePoolDeltaTime");
        var nativeCallback = AccessTools.Method(assembly.GetType("VSDirector.Replay.ReplayOpaqueParticleTimePatch", true)!, "Prefix");
        float Consume() => (float)consume.Invoke(manager, [pool, true, .016f])!;
        void Equal(float actual, float expected, string message)
        { if (Math.Abs(actual - expected) > .00001f) throw new InvalidOperationException(message); }
        void NativeFrame() => nativeCallback.Invoke(null, new object[] { .016f });
        try
        {
            pending.SetValue(playback, .1f);
            NativeFrame();
            Equal((float)pending.GetValue(playback)!, .1f, "Native particles consumed the dedicated creation frame delta.");
            publish.Invoke(manager, null);
            NativeFrame();
            Equal(Consume(), .1f, "Creation particles lost their delta while the internal replay was paused.");
            Equal(Consume(), 0, "A second particle pass advanced the same pool twice.");

            // Preview rendering must neither age particles nor erase the clock
            // snapshot awaited by the primary frame's asynchronous pool.
            pending.SetValue(playback, .2f);
            publish.Invoke(manager, null);
            var offscreenType = assembly.GetType("VSDirector.Rendering.DirectorOffscreenRenderRuntime", true)!;
            using ((IDisposable)AccessTools.Method(offscreenType, "Enter").Invoke(null, [null, 1L])!)
            {
                publish.Invoke(manager, null);
                Equal(Consume(), 0, "An offscreen preview advanced particle lifetime.");
            }
            Equal(Consume(), .2f, "Preview rendering erased the primary particle frame snapshot.");

            AccessTools.Field(managerType, "creationPlaybackAdvancing").SetValue(manager, false);
            pending.SetValue(playback, .3f);
            publish.Invoke(manager, null);
            Equal(Consume(), 0, "Paused creation playback continued aging particles.");
            Console.WriteLine("PASS creation particle lifetime clock: single dedicated publisher, internally paused replay, once-per-pool advance, offscreen preservation and transport pause");
        }
        finally
        {
            AccessTools.Field(managerType, "playback").SetValue(manager, null);
            AccessTools.Method(assembly.GetType("VSDirector.Replay.DirectorReplayRuntime", true)!, "ReleaseCreationSession").Invoke(null, [manager]);
        }
    }
}
