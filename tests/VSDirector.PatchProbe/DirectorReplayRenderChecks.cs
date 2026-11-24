using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Diagnostics;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VSDirector;
using VSDirector.Data;
using VSDirector.Rendering;

internal static class DirectorReplayRenderChecks
{
    internal static void Run()
    {
        var assembly = typeof(VSDirectorModSystem).Assembly;
        Type ModType(string name) => assembly.GetType("VSDirector." + name, true)!;
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        var registered = new List<(EnumRenderStage Stage, IRenderer Renderer)>();
        var events = ProbeDispatchProxy.Create<IClientEventAPI>((m, args) =>
        {
            if (m.Name == "RegisterRenderer") registered.Add(((EnumRenderStage)args![1]!, (IRenderer)args[0]!));
            return ProbeDispatchProxy.DefaultValue(m.ReturnType);
        });
        var uniforms = new DefaultShaderUniforms { ExtraSepia = .1f, ExtraContrastLevel = .2f };
        uniforms.AmbientBloomLevelAdd[3] = .3f;
        var render = ProbeDispatchProxy.Create<IRenderAPI>((m, _) =>
            m.Name == "get_ShaderUniforms" ? uniforms : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_World" => game, "get_Event" => events, "get_Render" => render,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var effectsType = ModType("Gui.DirectorReplayScreenEffectsRenderer");
        var effects = (IRenderer)Activator.CreateInstance(effectsType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [api], null)!;
        var grade = AccessTools.Property(ModType("Replay.DirectorReplayVisualRuntime"), "Grade");
        grade.SetValue(null, Activator.CreateInstance(ModType("Replay.DirectorReplayScreenGrade"), .2f, .3f, .4f, .5f, .2f, 0f));
        var enter = AccessTools.Method(ModType("Rendering.DirectorOffscreenRenderRuntime"), "Enter");
        for (int i = 0; i < 3; i++)
        {
            using ((IDisposable)enter.Invoke(null, [new DirectorOffscreenViewRequest(), 1L])!)
            {
                effects.OnRenderFrame(0, EnumRenderStage.AfterPostProcessing);
                if (Math.Abs(uniforms.ExtraSepia - .3f) > 1e-6 || uniforms.ExtraContrastLevel != .5f
                    || uniforms.AmbientBloomLevelAdd[3] != .8f) throw new Exception("Grade accumulated across views");
            }
            if (uniforms.ExtraSepia != .1f || uniforms.ExtraContrastLevel != .2f || uniforms.AmbientBloomLevelAdd[3] != .3f)
                throw new Exception("Offscreen grade leaked to the following window frame");
        }
        grade.SetValue(null, null);

        var exportType = ModType("Replay.DirectorReplayMultiTrackExporter");
        int readBoundary = 0;
        var exporter = (IRenderer)Activator.CreateInstance(exportType, api, null, null, null,
            (Func<bool>)(() => { readBoundary++; return false; }), null)!;
        var session = AccessTools.Field(exportType, "session");
        var active = RuntimeHelpers.GetUninitializedObject(session.FieldType);
        AccessTools.Field(session.FieldType, "<FrameGate>k__BackingField").SetValue(active, new DirectorReplayExportFrameGate(1));
        session.SetValue(exporter, active);
        var ortho = registered.Where(r => r.Stage == EnumRenderStage.Ortho).OrderBy(r => r.Renderer.RenderOrder).ToArray();
        if (!ortho.Select(r => r.Renderer).SequenceEqual(new[] { effects, exporter }) || exporter.RenderOrder >= 1d)
            throw new Exception("Capture must run after titles and before GuiManager");
        exporter.OnRenderFrame(0, EnumRenderStage.Ortho);
        if (readBoundary != 1) throw new Exception("Missed capture before GUI");
        exporter.OnRenderFrame(0, EnumRenderStage.Done);
        if (readBoundary != 1) throw new Exception("Captured a second time after drawing the progress overlay");
        exporter.OnRenderFrame(0, EnumRenderStage.Done);
        if (readBoundary != 2) throw new Exception("Missing HUD-disabled capture");
        CheckEncodedFrame(api, exportType);

        var owner = new VSDirectorModSystem();
        var smooth = AccessTools.Method(typeof(VSDirectorModSystem), "SmoothReplayLookAt");
        Guid id = Guid.NewGuid();
        CameraPreset Aim(double time, float angle)
        {
            var pose = new CameraPreset { Yaw=angle, Pitch=angle/2, Fov=70+angle/4 };
            smooth.Invoke(owner, [id, pose, time, .3d]);
            return pose;
        }
        Aim(0, 0);
        var moving = Aim(.1, 40);
        if (moving.Yaw <= 0 || moving.Yaw >= 40 || Aim(.1, 40).Yaw != moving.Yaw)
            throw new Exception("Advancing or repeatedly sampling one frame broke tracking smoothing");
        var edited = Aim(.1, 60);
        if (edited.Yaw != 60 || edited.Pitch != 30 || edited.Fov != 85 || Aim(0, 10).Yaw != 10)
            throw new Exception("Paused edit or rewind retained stale tracking");
        CheckWhip(ModType("Gui.DirectorWorkspaceHud"));
        Console.WriteLine("PASS export stage order and HUD-disabled fallback; per-view grade restoration; paused look-at edits; adjacent and legacy follow intervals");
    }

    private static void CheckEncodedFrame(ICoreClientAPI api, Type exportType)
    {
        const int size = 64;
        using var window = new NativeWindow(new NativeWindowSettings { StartVisible=false,
            ClientSize=new(size, size), APIVersion=new Version(3, 3) });
        window.MakeCurrent();
        string video = Path.Combine(Path.GetTempPath(), $"vsdirector-render-{Guid.NewGuid():N}.mp4");
        var track = new DirectorRecordingTrack { Id=Guid.NewGuid(), Name="probe" };
        try
        {
            using var sink = new DirectorOffscreenFfmpegSink(api, new() { ViewId=track.Id, OutputPath=video,
                Width=size, Height=size, FramesPerSecond=30, Encoder=DirectorOffscreenVideoEncoder.Libx264, RequireExactFrames=true });
            sink.Start();
            using var timecodes = new StreamWriter(Stream.Null);
            Type trackType = AccessTools.Inner(exportType, "ExportTrack");
            var tracks = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(trackType))!;
            tracks.Add(Activator.CreateInstance(trackType, track, sink, timecodes, video, 0d, 1d, 30L,
                new DirectorReplayElementExportClock(new(), 0, 1, 1, 30)));
            var field = AccessTools.Field(exportType, "session");
            var exporter = (IRenderer)Activator.CreateInstance(exportType, api, null, null,
                (Func<double,bool>)(_ => true), (Func<bool>)(() => true), null)!;
            field.SetValue(exporter, Activator.CreateInstance(field.FieldType, "probe", tracks, DateTimeOffset.UtcNow,
                Path.GetTempPath(), 0d, 1d, 1d, 30, size, size, 30L, 1, false, null, null, "", null, "ffmpeg", 128));
            GL.ClearColor(0, 0, 1, 1); GL.Clear(ClearBufferMask.ColorBufferBit);
            exporter.OnRenderFrame(0, EnumRenderStage.Ortho);
            // Cover the captured scene at the main GUI stage, as a progress bar would.
            GL.ClearColor(1, 1, 0, 1); GL.Clear(ClearBufferMask.ColorBufferBit);
            exporter.OnRenderFrame(0, EnumRenderStage.Done);
            GL.Finish();
            var result = sink.Stop();
            if (!result.Success || result.Statistics.EncodedFrames != 1) throw new Exception($"Test export: {result}");
            var decode = new ProcessStartInfo("ffmpeg") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true };
            foreach (string arg in new[] { "-v","error","-i",video,"-f","rawvideo","-pix_fmt","rgb24","pipe:1" })
                decode.ArgumentList.Add(arg);
            using var process = Process.Start(decode)!;
            using var pixels = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(pixels);
            process.WaitForExit();
            byte[] rgb = pixels.ToArray();
            if (process.ExitCode != 0 || rgb.Length != size*size*3) throw new Exception("Unexpected decoded frame count or dimensions");
            for (int i=0; i<rgb.Length; i+=3)
                if (rgb[i]>10 || rgb[i+1]>10 || rgb[i+2]<240) throw new Exception("Main GUI leaked into encoded pixels");
            Console.WriteLine("PASS real GL readback and FFmpeg encode/decode: all 4096 pixels retained the pre-GUI scene");
        }
        finally { File.Delete(video); }
    }

    private static void CheckWhip(Type hud)
    {
        var contextType = AccessTools.Inner(hud, "ReplayPresetContext");
        var context = RuntimeHelpers.GetUninitializedObject(contextType);
        var timeline = new DirectorReplayViewportTimeline();
        AccessTools.Property(contextType, "Timeline").SetValue(context, timeline);
        var track = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.FollowEntity };
        var add = AccessTools.Method(hud, "AddPresetKeyframe");
        void Key(double time, double distance, bool end, DirectorReplayElementInterpolation blend)
            => add.Invoke(null, [context, track, new DirectorReplayElementKeyframe { TimeSeconds=time,
                FollowDistance=distance, FollowBlend=blend, EntityId=1, FollowEntityRole=end
                    ? DirectorReplayFollowEntityKeyframeRole.End : DirectorReplayFollowEntityKeyframeRole.Start }]);
        Key(0, 25, false, DirectorReplayElementInterpolation.EaseIn);
        Key(2.5, 3, true, DirectorReplayElementInterpolation.Hold);
        Key(2.5, 3, false, DirectorReplayElementInterpolation.Hold);
        Key(5.5, 3, true, DirectorReplayElementInterpolation.Hold);
        if (timeline.ElementKeyframes.Count != 4) throw new Exception("Preset replaced its incoming endpoint");
        for (int variant = 0; variant < 2; variant++)
        {
            foreach (double time in new[] { 0d, 2.49999, 2.5, 2.50001, 5.49 })
            {
                if (!DirectorReplayElementTimeline.TrySampleFollowEntityInterval(timeline, track.Id, time, out var start, out var end))
                    throw new Exception("Follow interval gap");
                var sample = DirectorReplayFollowBlend.Sample(start, end, time);
                if (Math.Abs(sample.FollowDistance - (time == 0 ? 25 : 3)) > .001)
                    throw new Exception("Whip-in distance jumped on reaching the hold");
            }
            timeline.ElementKeyframes.RemoveAll(k => k.TimeSeconds == 2.5 && k.FollowEntityRole == DirectorReplayFollowEntityKeyframeRole.End);
        }
    }
}
