using System.Collections;
using System.Reflection;
using HarmonyLib;
using VSDirector;
using VSDirector.Data;

internal static class DirectorReplayAudioUpdateChecks
{
    public static void Run()
    {
        Type Audio(string name) => typeof(VSDirectorModSystem).Assembly.GetType("VSDirector.Replay.DirectorReplayAudio" + name, true)!;
        object New(Type type, params object?[] args) => Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null)!;
        const int rate = 48000;
        float[] source = Enumerable.Range(0, rate).Select(i => (float)(.25 * Math.Sin(i * 2 * Math.PI * 440 / rate))).ToArray();
        var assets = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), Audio("Asset")))!;
        assets["loop"] = New(Audio("Asset"), source, 1, rate);
        var events = Array.CreateInstance(Audio("Event"), 4);
        void Add(int index, double time, DirectorReplaySoundAction action, float cursor, float volume)
        {
            events.SetValue(New(Audio("Event"), time, "loop", 1L, action, 0d, 0d, 0d, true, true, cursor,
                volume, 1f, 0f, 32f, 3f, 0, 1f, 0f, 0f, Enum.Parse(Audio("Bus"), "General")), index);
        }
        Add(0, 0, DirectorReplaySoundAction.Play, 0, 1);
        Add(1, .04, DirectorReplaySoundAction.Update, .048f, 1);
        Add(2, .08, DirectorReplaySoundAction.Update, .09375f, .5f);
        Add(3, .12, DirectorReplaySoundAction.Pause, .125f, .5f);
        var plan = New(Audio("ExportPlan"), events, assets, 0, new DirectorReplayAudioVisuals { SpatialAudio=false },
            Array.Empty<DirectorReplayVoiceFrame>(), new DirectorReplayViewportTimeline());
        var mixerType = AccessTools.Inner(Audio("Exporter"), "DirectorReplayAudioMixer");
        float[] Mix(double start, int count)
        {
            var mixer = New(mixerType, plan, null);
            AccessTools.Method(mixerType, "InitializeAt").Invoke(mixer, [start]);
            var left = new float[count];
            AccessTools.Method(mixerType, "MixFrame").Invoke(mixer,
                [new CameraPreset(), start, start + (double)count/rate, 1d, left, new float[count], count]);
            return left;
        }
        var continuous = Mix(0, 6400);
        for (int i=0; i<continuous.Length; i++)
        {
            float expected = i >= 5760 ? 0 : source[i] * (i >= 3840 ? .5f : 1f);
            if (Math.Abs(continuous[i] - expected) > 1e-6) throw new Exception($"Parameter update introduced an audio discontinuity at sample {i}");
        }
        var seek = Mix(.09, 480);
        for (int i=0; i<seek.Length; i++)
            if (Math.Abs(seek[i] - source[4500+480+i]*.5f) > 1e-6) throw new Exception($"Wrong audio cursor after seek at sample {i}");
        Console.WriteLine("PASS continuous loop waveform, gain changes, pause and recorded cursor restoration after seek");
    }
}
