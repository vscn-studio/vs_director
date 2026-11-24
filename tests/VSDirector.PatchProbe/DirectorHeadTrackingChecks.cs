using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VSDirector;
using VSDirector.Data;

internal static class DirectorHeadTrackingChecks
{
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP FreeTrack shared-memory probe on non-Windows"); return; }
        var assembly = typeof(VSDirectorModSystem).Assembly;
        Type T(string name) => assembly.GetType("VSDirector.HeadTracking." + name, true)!;
        void Set(Type type, string field, object? value, object? target = null) => AccessTools.Field(type, field).SetValue(target, value);
        object? Call(Type type, string method, object? target, params object?[] args) => AccessTools.Method(type, method).Invoke(target, args);
        var readerType = T("FreeTrackReader"); var bridge = T("VsdofModSystem"); var configType = T("HeadTrackingConfig");
        var harmony = new Harmony("vsdirector.probe.head-tracking");
        try
        {
            harmony.CreateClassProcessor(T("PlayerCameraTrackingPatch")).Patch();
            harmony.CreateClassProcessor(T("PlayerModelVisibilityPatch")).Patch();
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        string mapName = "VSDirector-HeadMotion-Probe-" + Guid.NewGuid().ToString("N");
        using var mapping = MemoryMappedFile.CreateNew(mapName, 64);
        using var memory = mapping.CreateViewAccessor();
        using var reader = (IDisposable)Activator.CreateInstance(readerType, BindingFlags.Instance | BindingFlags.NonPublic, null, [mapName], null)!;
        memory.Write(0, 1u); memory.Write(4, 640); memory.Write(8, 480);
        memory.Write(12, .1f); memory.Write(16, .2f); memory.Write(20, .3f);
        memory.Write(24, 10f); memory.Write(28, 20f); memory.Write(32, 30f);
        bool Read()
        {
            object?[] args = [Activator.CreateInstance(T("FTData"))];
            return (bool)AccessTools.Method(readerType, "TryRead").Invoke(reader, args)!;
        }
        if (!Read()) throw new Exception("FreeTrack layout/read-only mapping failed");
        memory.Write(12, float.NaN);
        if (Read()) throw new Exception("FreeTrack accepted NaN");
        memory.Write(12, .1f);
        Set(readerType, "lastPacket", Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2, reader);
        if (Read()) throw new Exception("Stopped tracker remained connected");
        Set(readerType, "lastOpen", 0L, reader);
        if (Read()) throw new Exception("Reopening a stale mapping made a stopped tracker appear live");
        memory.Write(0, 2u); Set(readerType, "lastOpen", 0L, reader);
        if (!Read()) throw new Exception("Reader did not recover when fresh packets arrived");
        var saved = new Dictionary<string, object?>();
        foreach (string field in new[] { "Capi", "Config", "State", "AllowLive", "reader", "hasCenter", "hasFiltered", "filteredAt" })
            saved[field] = AccessTools.Field(bridge, field).GetValue(null);
        try
        {
            var values = new Dictionary<string, int> { ["fieldOfView"] = 80, ["mouseSensivity"] = 100, ["mouseSmoothing"] = 20 };
            var ints = ProbeDispatchProxy.Create<ISettingsClass<int>>((m, a) =>
            {
                if (m.Name == "get_Item") return values.GetValueOrDefault((string)a![0]!);
                if (m.Name == "set_Item") values[(string)a![0]!] = (int)a[1]!;
                return ProbeDispatchProxy.DefaultValue(m.ReturnType);
            });
            var settings = ProbeDispatchProxy.Create<ISettings>((m, _) => m.Name == "get_Int" ? ints : ProbeDispatchProxy.DefaultValue(m.ReturnType));
            var input = ProbeDispatchProxy.Create<IInputAPI>((m, _) => m.Name == "get_KeyboardKeyState" ? new bool[512] : ProbeDispatchProxy.DefaultValue(m.ReturnType));
            var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch
            { "get_Settings" => settings, "get_Input" => input, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType) });
            var config = Activator.CreateInstance(configType)!;
            Set(configType, "EnableTracking", true, config); Set(configType, "SmoothingSeconds", 0f, config);
            Set(configType, "BaselineOffsetY", 0f, config); Set(configType, "BaselineOffsetZ", 0f, config);
            Set(bridge, "Capi", api); Set(bridge, "Config", config); Set(bridge, "reader", reader);
            Set(bridge, "State", Activator.CreateInstance(T("TrackingState")));
            Set(bridge, "AllowLive", (Func<bool>)(() => true)); Set(bridge, "hasCenter", false); Set(bridge, "hasFiltered", false);
            DirectorHeadMotionSample Sample()
            {
                object?[] args = [default(DirectorHeadMotionSample)];
                if (!(bool)AccessTools.Method(bridge, "TrySample", [typeof(DirectorHeadMotionSample).MakeByRefType()]).Invoke(null, args)!) throw new Exception("Live input sample failed");
                return (DirectorHeadMotionSample)args[0]!;
            }
            var sample = Sample();
            if (Math.Abs(sample.X + .05f) > .001 || Math.Abs(sample.Yaw + .1f * 2 * 180 / MathF.PI) > .001)
                throw new Exception("FreeTrack units/signs changed between capture and camera");
            Call(bridge, "Recenter", null);
            var centered = Sample();
            if (Math.Abs(centered.X) > .001 || Math.Abs(centered.Yaw) > .001) throw new Exception("Recenter retained offsets");
            memory.Write(24, 20f); memory.Write(0, 3u);
            var owner = new VSDirectorModSystem();
            var source = new CameraPreset { X = 10, Yaw = 35, Fov = 80 };
            var first = (CameraPreset)Call(typeof(VSDirectorModSystem), "ApplyLiveHeadMotion", owner, source)!;
            for (int frame = 0; frame < 20; frame++)
            {
                var repeated = (CameraPreset)Call(typeof(VSDirectorModSystem), "ApplyLiveHeadMotion", owner, source)!;
                if (Math.Abs(repeated.X - first.X) > 1e-6 || Math.Abs(source.X - 10) > 1e-6) throw new Exception("Live tracking accumulated camera offsets");
            }
            Set(bridge, "AllowLive", (Func<bool>)(() => false));
            var export = (CameraPreset)Call(typeof(VSDirectorModSystem), "ApplyLiveHeadMotion", owner, source)!;
            if (export.X != source.X || export.Yaw != source.Yaw) throw new Exception("Export gate accepted live tracking input");
            Set(bridge, "AllowLive", (Func<bool>)(() => true)); Set(configType, "EnableTracking", false, config);
            var disabled = (CameraPreset)Call(typeof(VSDirectorModSystem), "ApplyLiveHeadMotion", owner, source)!;
            if (disabled.X != source.X) throw new Exception("Disabling tracking retained a camera offset");
            var player = new EntityPlayer();
            var rawType = T("FTData"); var raw = Activator.CreateInstance(rawType)!;
            var patch = T("PlayerCameraTrackingPatch");
            void Crouch(float y) { Set(rawType, "Y", y, raw); Call(patch, "HandleCrouch", null, player, raw, config); }
            Crouch(-130); if (!player.Controls.Sneak) throw new Exception("Head crouch hold did not engage");
            Crouch(0); if (player.Controls.Sneak) throw new Exception("Head crouch did not release");
            player.Controls.Sneak = true; Crouch(0);
            if (!player.Controls.Sneak) throw new Exception("Inactive head crouch overwrote physical sneak");
            player.Controls.Sneak = false;
            Set(configType, "CrouchMode", "toggle", config);
            Crouch(-130); Crouch(-130); Crouch(0);
            if (!player.Controls.Sneak) throw new Exception("Toggle crouch did not latch");
            Crouch(-130); if (player.Controls.Sneak) throw new Exception("Toggle crouch did not unlatch");
            Set(configType, "EnableLeanToZoom", true, config);
            Set(configType, "EnableLeanToZoomAxis", true, config);
            Set(rawType, "Z", -40f, raw);
            Call(patch, "HandleLeanToZoom", null, raw, config);
            if (values["fieldOfView"] != 20 || values["mouseSensivity"] != 50) throw new Exception("Continuous zoom did not apply FOV/sensitivity");
            Set(rawType, "Z", 0f, raw); Call(patch, "HandleLeanToZoom", null, raw, config);
            if (values["fieldOfView"] != 80 || values["mouseSensivity"] != 100 || values["mouseSmoothing"] != 20)
                throw new Exception("Zoom release did not restore settings");
            Set(configType, "EnableLeanToZoomAxis", false, config); Set(rawType, "Z", -40f, raw);
            Call(patch, "HandleLeanToZoom", null, raw, config);
            if (values["fieldOfView"] != 20) throw new Exception("Threshold zoom did not engage without Zoom Button");
            Set(configType, "EnableLeanToZoom", false, config); Call(patch, "HandleLeanToZoom", null, raw, config);
            if (values["fieldOfView"] != 80) throw new Exception("Disabling zoom left the settings modified");
        }
        finally { foreach (var field in saved) Set(bridge, field.Key, field.Value); }
        Console.WriteLine("PASS tracking/model patch targets; synthetic FreeTrack packets, disconnect/reconnect, recenter, stable camera/export gates; crouch hold/toggle and continuous/threshold zoom restore controls");
    }
}
