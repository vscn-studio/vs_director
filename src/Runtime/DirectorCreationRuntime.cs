using static VSDirector.Gui.DirectorCreationText;
using System.Diagnostics;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using VSDirector.Data;
using VSDirector.Rendering;
using VSDirector.Recording;
using VSDirector.Replay;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime : IDirectorOffscreenFrameSink, IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly VSDirectorModSystem owner;
    internal DirectorCreationSession Session { get; }
    internal DirectorCreationRenderer Renderer { get; }
    internal DirectorCreationExtensions Extensions { get; } = new();
    internal bool Enabled { get => Renderer.Visible; set { if (!value && Exporting) CancelExport(); if (!value) FinishPerformanceBeforeTransition(); Renderer.Visible = value; if (!value) { Session.Play(false); RestoreEntities(); RestoreVisualEnvironment(); } } }
    internal bool Preview { get; set; }
    internal bool Guides { get; set; } = true;
    internal int PreviewTexture { get; private set; }
    internal string Status { get; private set; } = "";
    internal bool Exporting => pendingExport is not null || fileSink is not null || finishing is not null;
    internal double ExportProgress => Session.Project is { } p ? exportFrame / Math.Max(1d, Math.Ceiling(p.Duration * p.Output.Fps)) : 0;
    private readonly Guid viewId = Guid.NewGuid();
    private DirectorOffscreenMultiViewRenderer? offscreen;
    private readonly long tick;
    private DirectorOffscreenFfmpegSink? fileSink;
    private Task? finishing;
    private int exportFrame;
    private double restoreTime;
    private bool frameSubmitted, stopRequested;
    private readonly DirectorReplayExportFrameGate exportGate = new(2);
    private string videoPath = "", screenshot = "";
    private DirectorReplayAudioExportPlan? replayAudioPlan;
    private List<DirectorReplayAudioFrame> replayAudioFrames = new();
    private CancellationTokenSource? exportCancellation;
    private readonly Dictionary<Guid, ILoadedSound> sounds = new();
    private readonly HashSet<AssetLocation> soundAssets = new();
    private double previousSoundTime = -1;
    private double previousParticleTime = -1;
    private bool disposed;
    internal DirectorCreationRuntime(ICoreClientAPI api, VSDirectorModSystem owner)
    {
        this.api = api; this.owner = owner;
        Session = new(new DirectorCreationStore(Path.Combine(api.GetOrCreateDataPath("ModData"), "VSDirector", "Projects")));
        Renderer = new(api, Session) { Visible = false };
        Current = this; Session.FrameReady = FrameReady;
        tick = api.Event.RegisterGameTickListener(Tick, 16);
    }
    internal void SetStatus(string message) => Status = Message(message);
    internal void New(string name)
    {
        FinishPerformanceBeforeTransition();
        if (Exporting) throw new InvalidOperationException(L("creation-finish-or-cancel-the-export-first"));
        if (!owner.TryCaptureCreationView(out var pose)) throw new InvalidOperationException(L("creation-the-current-world-view-is-unavailable"));
        StopSounds();
        var project = new DirectorCreationProject { Name = string.IsNullOrWhiteSpace(name) ? L("creation-new-project") : name, WorldId = api.World.SavegameIdentifier,
            Dimension = pose.Dimension, Origin = new(Math.Floor(pose.X), Math.Floor(pose.Y - 1.6), Math.Floor(pose.Z)) };
        var camera = new CreationSceneObject { Name = L("creation-camera"), Kind = CreationObjectKind.Camera,
            Transform = new() { Position = new(pose.X - project.Origin.X, pose.Y - project.Origin.Y, pose.Z - project.Origin.Z), Rotation = DirectorCreationMath.CameraRotationFromNative(pose) }, Fov = pose.Fov };
        project.Objects.Add(camera); project.CameraId = camera.Id;
        Session.Open(project); Renderer.ReloadTextures(); Session.SelectedObject = camera.Id; Session.Save(); Enabled = true;
        Status = L("creation-create-an-actor-or-import-a-shape-model-then-add-keyframes");
    }
    internal void Open(Guid id, bool recovery = false)
    {
        FinishPerformanceBeforeTransition();
        if (Exporting) throw new InvalidOperationException(L("creation-finish-or-cancel-the-export-first"));
        StopSounds(); Session.Open(Session.Store.Load(id, recovery)); Renderer.ReloadTextures(); Enabled = true;
        if (recovery) Session.Changed();
    }
    internal CreationVector CursorPosition()
    {
        if (Session.Project is not { } p || !owner.TryCaptureCreationView(out var camera)) return default;
        var forward = EntityPos.GetViewVector(camera.Pitch * GameMath.DEG2RAD, camera.Yaw * GameMath.DEG2RAD);
        return new CreationVector(camera.X + forward.X * 3, camera.Y + forward.Y * 3, camera.Z + forward.Z * 3) - p.Origin;
    }
    internal void CaptureCamera(CreationSceneObject obj)
    {
        if (Session.Project is not { } p || !owner.TryCaptureCreationView(out var camera)) return;
        Session.Edit(L("creation-capture-camera"), _ => { var tr = obj.Transform with { Position = new(camera.X - p.Origin.X, camera.Y - p.Origin.Y, camera.Z - p.Origin.Z), Rotation = DirectorCreationMath.CameraRotationFromNative(camera) }; Session.SetTransform(obj.Id, Guid.Empty, tr); obj.Fov = camera.Fov; });
    }
    internal void Seek(double time)
    {
        Session.Seek(time);
        if (fileSink is not null) { exportGate.BeginSeek(); exportWaitStarted = Environment.TickCount64; }
        if (!PerformanceActive && Session.EditingAnimation is null && Session.Project is { BackgroundReplayId: var id } p && id != Guid.Empty)
        {
            if (id != owner.ActiveReplayId) { Status = L("creation-load-the-linked-background-replay-from-the-replay-list"); return; }
            owner.SeekCreationBackground(BackgroundTime(time));
        }
    }
    private void Tick(float delta)
    {
        if (disposed || Session.Project is not { } project) return;
        try
        {
            if (finishing is { IsCompleted: true }) finishing = null;
            if (!Enabled) return;
            UpdateVisualEnvironment();
            TickPerformance(delta);
            SyncBackgroundSelection();
            TickPerformanceBackground();
            TickExportPreparation();
            if (fileSink is not null)
            {
                if (!frameSubmitted && Environment.TickCount64 - exportWaitStarted > 60000)
                    throw new InvalidOperationException(L("creation-export-frame-timeout"));
                if (frameSubmitted)
                {
                    frameSubmitted = false; exportFrame++;
                    if (exportFrame >= Math.Ceiling(project.Duration * project.Output.Fps)) stopRequested = true;
                    else Seek(exportFrame / (double)project.Output.Fps);
                }
                if (stopRequested) FinishExport();
            }
            else
            {
                if (performanceRunning && DirectorReplayRuntime.Manager is { } recorder)
                {
                    Session.AdvancePerformance(performanceStart + recorder.LiveActionRecordingSeconds);
                    CapturePerformanceBones();
                    if (Session.Time >= 86400) StopPerformance();
                }
                else
                {
                    bool advancing = Session.Playing;
                    Session.Tick(delta);
                    if (!PerformanceActive && Session.EditingAnimation is null && advancing && project.BackgroundReplayId == owner.ActiveReplayId && project.BackgroundReplayId != Guid.Empty)
                        owner.SeekCreationBackground(BackgroundTime(Session.Time), advancing: true);
                }
            }
            if (Session.Recording && api.World.Player?.Entity is { } entity)
            {
                Session.RecordSample(new(entity.Pos.X, entity.Pos.Y, entity.Pos.Z), new(entity.Pos.Pitch * GameMath.RAD2DEG, entity.Pos.Yaw * GameMath.RAD2DEG, entity.Pos.Roll * GameMath.RAD2DEG));
                if (!Session.Playing) Session.StopRecording();
            }
            UpdateSounds();
            UpdateParticles();
            UpdateView();
        }
        catch (Exception e)
        {
            try { FinishPerformanceBeforeTransition(); } catch (Exception cleanup) { api.Logger.Error(cleanup.ToString()); }
            CancelExport(); Status = Message(e.Message); Session.Play(false);
        }
    }
    private void UpdateView()
    {
        if (Session.Project is not { } project || Session.Frame is not { } frame) return;
        if (ControllingActor || !Preview && fileSink is null && screenshot.Length == 0) { offscreen?.SetPreviewView(viewId, null); return; }
        var camera = DirectorCreationEvaluator.Camera(project, frame);
        if (camera is null) { Status = L("creation-select-a-shooting-camera"); return; }
        foreach (var effect in Extensions.CameraEffects.Values) camera = effect.Evaluate(camera, frame.Time);
        offscreen ??= owner.CreationOffscreen(); offscreen.AddSink(this);
        var output = project.Output;
        bool full = fileSink is not null || screenshot.Length > 0;
        int width = full ? output.Width : Math.Min(960, output.Width), height = full ? output.Height : (int)Math.Round(width * output.Height / (double)output.Width);
        offscreen.SetPreviewView(viewId, new DirectorOffscreenViewRequest { ViewId = viewId, Name = L("creation-creation-preview"), Width = width, Height = Math.Max(16, height),
            Pose = new(camera.X, camera.Y, camera.Z, camera.Dimension, camera.Yaw, camera.Pitch, camera.Roll, camera.Fov),
            CaptureClockMilliseconds = fileSink is null ? null : (long)Math.Ceiling(exportFrame * 1000d / output.Fps), RenderLocalPlayer = false });
    }
    public void OnFrame(in DirectorOffscreenFrame frame)
    {
        if (frame.ViewId != viewId) return;
        PreviewTexture = frame.ColorTextureId;
        if (screenshot.Length > 0)
        {
            try { using var bitmap = api.Render.GrabScreenshot(frame.Width, frame.Height, false, true); bitmap.Save(screenshot); Status = L("creation-screenshot-saved") + screenshot; }
            catch (Exception e) { Status = Message(e.Message); }
            screenshot = "";
        }
        if (fileSink is null || frameSubmitted || stopRequested) return;
        bool backgroundReady = Session.Project is not { BackgroundReplayId: var background } || background == Guid.Empty
            || (background == owner.ActiveReplayId && owner.CreationBackgroundReady);
        if (exportGate.Next(backgroundReady) != DirectorReplayExportFrameAction.Capture) return;
        long before = fileSink.Statistics.SubmittedReadbacks;
        fileSink.OnFrame(frame);
        if (fileSink.Statistics.SubmittedReadbacks > before)
        {
            frameSubmitted = true;
            if (replayAudioPlan is not null && Session.Project is { } p && Session.Frame is { } f && DirectorCreationEvaluator.Camera(p, f) is { } camera)
                replayAudioFrames.Add(new(BackgroundTime(exportFrame / (double)p.Output.Fps), camera));
        }
    }
    internal void Screenshot(string path) { screenshot = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!); UpdateView(); }
    private void StartExport(string path)
    {
        if (PerformanceActive) throw new InvalidOperationException(L("creation-recording-unavailable"));
        if (Exporting || Session.Project is not { } p) return;
        p.Validate();
        if (p.CameraId == Guid.Empty) throw new InvalidOperationException(L("creation-select-a-shooting-camera"));
        if (p.BackgroundReplayId != Guid.Empty && p.BackgroundReplayId != owner.ActiveReplayId) throw new InvalidOperationException(L("creation-load-the-linked-background-replay-first"));
        Enabled = true;
        string ffmpeg = DirectorFfmpegLocator.ResolveRequired(owner.Config.FfmpegPath);
        videoPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(videoPath)!);
        var sink = new DirectorOffscreenFfmpegSink(api, new DirectorOffscreenFfmpegOptions { ViewId = viewId, OutputPath = videoPath,
            Width = p.Output.Width, Height = p.Output.Height, FramesPerSecond = p.Output.Fps, VideoQuality = p.Output.Quality,
            Encoder = Enum.TryParse<DirectorOffscreenVideoEncoder>(p.Output.Encoder, out var encoder) ? encoder : DirectorOffscreenVideoEncoder.Auto,
            FfmpegPath = ffmpeg, RequireExactFrames = true });
        replayAudioPlan = null; replayAudioFrames = new();
        if (p.Output.SceneAudio && p.BackgroundReplayId != Guid.Empty && p.BackgroundReplayId == owner.ActiveReplayId && owner.ActiveReplayDocument is { } replayDocument)
        {
            replayAudioPlan = DirectorReplayAudioExporter.Prepare((ClientMain)api.World, replayDocument, api.World.Player?.PlayerUID ?? string.Empty);
        }
        var originalTime = Session.Time;
        exportCancellation?.Dispose(); exportCancellation = new();
        sink.Start(); restoreTime = originalTime; Session.Play(false); StopSounds();
        fileSink = sink; frameSubmitted = stopRequested = false; exportFrame = 0; Seek(0); UpdateView(); Status = L("creation-exporting-frames");
    }
    internal void CancelExport() { pendingExport = null; exportCancellation?.Cancel(); if (fileSink is not null) { stopRequested = true; FinishExport(); } Status = L("creation-export-cancelled"); }
    private void FinishExport()
    {
        var sink = fileSink; if (sink is null || Session.Project is not { } project) return;
        fileSink = null; bool complete = exportFrame >= Math.Ceiling(project.Duration * project.Output.Fps);
        var result = sink.Stop(); sink.Dispose(); Seek(restoreTime);
        if (!result.Success || !complete) { Status = complete ? result.Error : L("creation-export-cancelled-the-partial-output-is-kept"); return; }
        var frozen = project.Clone(); string path = videoPath; string ffmpeg = owner.Config.FfmpegPath;
        var audioPlan = replayAudioPlan; var audioFrames = replayAudioFrames.ToArray();
        var cancellation = exportCancellation?.Token ?? CancellationToken.None;
        finishing = Task.Run(() =>
        {
            try
            {
                bool baseAudio = false;
                if (audioPlan is { } plan && audioFrames.Length > 0)
                {
                    var replayAudio = DirectorReplayAudioExporter.RenderAndMux(plan, audioFrames, frozen.Output.Fps, path, DirectorFfmpegLocator.ResolveRequired(ffmpeg), 192, DateTimeOffset.UtcNow, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (!replayAudio.Success) throw new IOException(replayAudio.Error);
                    baseAudio = replayAudio.Success && replayAudio.HasAudio;
                }
                MixAudio(frozen, path, DirectorFfmpegLocator.ResolveRequired(ffmpeg), baseAudio, cancellation); Status = L("creation-video-saved") + path;
            }
            catch (OperationCanceledException) { Status = L("creation-export-cancelled-328"); }
            catch (Exception e) { Status = L("creation-video-saved-but-audio-mixing-failed") + Message(e.Message); }
        });
    }
    private void MixAudio(DirectorCreationProject project, string path, string ffmpeg, bool baseAudio, CancellationToken cancellation)
    {
        var clips = project.Clips.Where(c => c.Enabled && c.AudioAssetId != Guid.Empty && c.Start < project.Duration).ToArray();
        if (clips.Length == 0) return;
        string output = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".audio.mp4");
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        void A(params string[] values) { foreach (var v in values) info.ArgumentList.Add(v); }
        static string N(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
        A("-y", "-i", path);
        foreach (var clip in clips)
        {
            var asset = project.Assets.Find(a => a.Id == clip.AudioAssetId) ?? throw new InvalidDataException(L("creation-audio-asset-is-missing"));
            if (clip.Loop) A("-stream_loop", "-1");
            A("-i", Session.Store.AssetPath(project.Id, asset.Source));
        }
        var filters = new List<string>();
        if (baseAudio) filters.Add("[0:a]volume=1[base]");
        for (int i = 0; i < clips.Length; i++)
        {
            var c = clips[i]; double speed = c.Speed; var tempo = new List<string>();
            while (speed > 2) { tempo.Add("atempo=2"); speed /= 2; } while (speed < .5) { tempo.Add("atempo=0.5"); speed *= 2; } tempo.Add("atempo=" + N(speed));
            string fades = (c.FadeIn > 0 ? $",afade=t=in:d={N(c.FadeIn)}" : "")
                + (c.FadeOut > 0 ? $",afade=t=out:st={N(Math.Max(0,c.Duration-c.FadeOut))}:d={N(c.FadeOut)}" : "");
            filters.Add($"[{i + 1}:a]atrim=start={N(c.SourceStart)}:duration={N(c.Duration * c.Speed)},asetpts=PTS-STARTPTS,{string.Join(',', tempo)},volume={N(c.Gain)}dB{fades},adelay={N(c.Start*1000)}:all=1[a{i}]");
        }
        string inputs = (baseAudio ? "[base]" : "") + string.Concat(Enumerable.Range(0, clips.Length).Select(i => $"[a{i}]"));
        filters.Add(inputs + $"amix=inputs={clips.Length + (baseAudio ? 1 : 0)}:normalize=0,apad,atrim=duration={N(project.Duration)}[mix]");
        A("-filter_complex", string.Join(';', filters), "-map", "0:v:0", "-map", "[mix]", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-t", N(project.Duration), output);
        using var process = Process.Start(info) ?? throw new IOException(L("creation-unable-to-start-ffmpeg"));
        using var registration = cancellation.Register(() => { try { process.Kill(true); } catch { } });
        string error = process.StandardError.ReadToEnd(); process.WaitForExit(); cancellation.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException(error.Length > 1500 ? error[^1500..] : error);
        File.Move(output, path, true);
    }
    private void UpdateSounds()
    {
        if (Session.Project is not { } p) return;
        if (Session.EditingAnimation is not null || !Session.Playing || fileSink is not null) { StopSounds(); return; }
        bool seek = previousSoundTime < 0 || Session.Time < previousSoundTime || Session.Time - previousSoundTime > .3;
        previousSoundTime = Session.Time;
        var active = p.Clips.Where(c => c.Enabled && c.AudioAssetId != Guid.Empty && Session.Time >= c.Start && Session.Time < c.Start + c.Duration).ToArray();
        foreach (var id in sounds.Keys.Where(id => !active.Any(c => c.Id == id)).ToArray()) { sounds[id].Stop(); sounds[id].Dispose(); sounds.Remove(id); }
        foreach (var clip in active)
        {
            if (!sounds.TryGetValue(clip.Id, out var sound))
            {
                var asset = p.Assets.Find(a => a.Id == clip.AudioAssetId); if (asset is null) continue;
                var location = new AssetLocation("vsdirector", "sounds/creation/" + asset.Id.ToString("N") + Path.GetExtension(asset.Source));
                if (soundAssets.Add(location)) api.Assets.Add(location, new Asset(location) { Data = File.ReadAllBytes(Session.Store.AssetPath(p.Id, asset.Source)) });
                sound = api.World.LoadSound(new SoundParams(location) { RelativePosition = true, Position = new(), DisposeOnFinish = false, ShouldLoop = clip.Loop, Pitch = (float)clip.Speed, Volume = 1, SoundType = EnumSoundType.Music });
                if (sound is null) { Status = L("creation-audio-preview-supports-game-decodable-ogg-wav-other-formats-can-still"); continue; }
                sounds[clip.Id] = sound; sound.Start(); seek = true;
            }
            if (seek) sound.PlaybackPosition = (float)(clip.SourceStart + (Session.Time - clip.Start) * clip.Speed);
            double fade = Math.Min(clip.FadeIn <= 0 ? 1 : (Session.Time - clip.Start) / clip.FadeIn, clip.FadeOut <= 0 ? 1 : (clip.Start + clip.Duration - Session.Time) / clip.FadeOut);
            sound.SetVolume((float)Math.Clamp(Math.Pow(10, clip.Gain / 20) * fade, 0, 1));
        }
    }
    private void StopSounds() { foreach (var sound in sounds.Values) { sound.Stop(); sound.Dispose(); } sounds.Clear(); previousSoundTime = -1; }
    internal void ReplaceAppearance(CreationSceneObject obj) { Session.Edit(L("creation-local-appearance-replacement"), p => { foreach (var o in p.Objects) o.ReplacePlayerUid = ""; obj.ReplacePlayerUid = api.World.Player.PlayerUID; }); }
    internal void RestoreAppearance() { RestoreEntities(); if (Session.Project is not null) Session.Edit(L("creation-restore-appearance"), p => { foreach (var o in p.Objects) o.ReplacePlayerUid = ""; }); }
    internal void Suspend(bool force = false)
    {
        FinishPerformanceBeforeTransition();
        try { if (Session.Dirty) Session.Save(); }
        catch (Exception e) when (force) { api.Logger.Error("VS Director: could not save creation project during shutdown: {0}", e.Message); }
        if (Exporting) CancelExport();
        RestoreEntities(); Enabled = false; Session.Play(false); Preview = false; StopSounds();
        RestoreVisualEnvironment();
        Renderer.NativeParticlesActive = false; screenshot = ""; offscreen?.SetPreviewView(viewId, null);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; api.Event.UnregisterGameTickListener(tick);
        RestoreEntities(); if (ReferenceEquals(Current,this)) Current=null;
        exportCancellation?.Cancel(); fileSink?.Stop(); fileSink?.Dispose(); fileSink = null;
        try { finishing?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        finishing = null; exportCancellation?.Dispose(); exportCancellation = null;
        Suspend(force: true); offscreen?.RemoveSink(this); Renderer.Dispose();
        foreach (var location in soundAssets) api.Assets.AllAssets.Remove(location); soundAssets.Clear();
    }
}
