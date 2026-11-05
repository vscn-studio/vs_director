using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Rendering;

/// <summary>Isolated scene snapshot and offscreen framebuffer; each accepted frame advances by exactly 1/fps.</summary>
internal sealed class DirectorStudioVideoExporter : IDirectorOffscreenFrameSink, IDirectorOffscreenPassRenderer, IDisposable
{
    private readonly DirectorStudioProject project;
    private readonly DirectorStudioSceneRenderer scene;
    private readonly DirectorOffscreenMultiViewRenderer renderer;
    private readonly DirectorOffscreenMultiViewRenderer? previous;
    private readonly DirectorOffscreenFfmpegSink sink;
    private readonly Guid viewId = Guid.NewGuid();
    private readonly int width, height;
    private readonly Action<bool, string> completed;
    private long frame;
    private readonly long total;
    private bool finishing, disposed;
    private string error = "";
    internal double Progress => frame / (double)total;
    public DirectorOffscreenPass Pass => DirectorOffscreenPass.OpaqueWorld;
    public double RenderOrder => .7;

    internal DirectorStudioVideoExporter(ICoreClientAPI api, DirectorStudioProject source, DirectorStudioResourceProvider resources,
        Func<Guid, DirectorModelAsset?> findModel, int width, int height, string ffmpeg, string path, Action<bool, string> completed)
    {
        project = DirectorStudioProject.Normalize(source.DeepClone());
        if (project.CameraKeys.Count == 0) throw new InvalidOperationException("Studio camera track is empty.");
        this.width = width; this.height = height; this.completed = completed;
        total = DirectorStudioTimeline.FrameCount(project.Duration, project.FramesPerSecond);
        previous = DirectorOffscreenRenderRuntime.Renderer;
        if (previous?.Enabled == true && previous.ActiveViewCount > 0) throw new InvalidOperationException("Another offscreen capture is active.");
        scene = new(api, resources, findModel) { DrawInWorld = false };
        scene.SetProject(project);
        sink = new(api, new DirectorOffscreenFfmpegOptions { ViewId = viewId, OutputPath = path, Width = width, Height = height,
            FramesPerSecond = project.FramesPerSecond, FfmpegPath = ffmpeg, RequireExactFrames = true, MaximumCatchUpFrames = 1 });
        try
        {
            sink.Start(); renderer = new(api);
            renderer.AddPassRenderer(this); renderer.AddSink(this);
            renderer.RenderFailed += Fail; SetFrame(); renderer.Enabled = true;
        }
        catch
        {
            scene.Dispose(); sink.Dispose(); DirectorOffscreenRenderRuntime.Renderer = previous; throw;
        }
    }
    private void SetFrame()
    {
        scene.Time = frame / (double)project.FramesPerSecond;
        var pose = DirectorStudioTimeline.Camera(project, scene.Time)!;
        renderer.SetViews([new() { ViewId = viewId, Name = project.Name, Width = width, Height = height,
            CaptureClockMilliseconds = (long)Math.Ceiling(frame * 1000d / project.FramesPerSecond),
            Pose = new(pose.X, pose.Y, pose.Z, pose.Dimension, pose.Yaw, pose.Pitch, pose.Roll, pose.Fov), RenderLocalPlayer = false }]);
    }
    public void Render(in DirectorOffscreenPassContext context)
    {
        var p = context.View.Pose;
        scene.RenderScene(new Vec3d(p.X, p.Y + p.Dimension * 32768d, p.Z));
        foreach (var item in project.SceneObjects)
            if (scene.Error(item) is { } failure) throw new InvalidOperationException(item.Name + ": " + failure);
    }
    public void OnFrame(in DirectorOffscreenFrame image)
    {
        if (finishing || disposed) return;
        long before = sink.Statistics.ScheduledFrames;
        sink.OnFrame(image);
        if (sink.FailureMessage.Length > 0) { Fail(new InvalidOperationException(sink.FailureMessage)); return; }
        if (sink.Statistics.ScheduledFrames == before) return;
        frame++;
        if (frame >= total) { finishing = true; renderer.Enabled = false; }
        else SetFrame();
    }
    private void Fail(Exception exception) { error = exception.GetBaseException().Message; finishing = true; renderer.Enabled = false; }
    internal void Tick()
    {
        if (!finishing || disposed) return;
        var result = sink.Stop();
        string integrity = DirectorReplayExportIntegrity.Validate(frame, result.Statistics.EncodedFrames,
            result.Statistics.DroppedFrames, result.Statistics.ReadbackFailures, result.Statistics.DuplicatedFrames);
        bool success = error.Length == 0 && result.Success && frame == total && integrity.Length == 0;
        string message = success ? result.OutputPath : error.Length > 0 ? error : result.Error.Length > 0 ? result.Error : integrity;
        Dispose(); completed(success, message);
    }
    internal void Cancel() { error = "Export cancelled."; finishing = true; renderer.Enabled = false; Tick(); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        renderer.Enabled = false; renderer.RenderFailed -= Fail;
        renderer.Dispose(); scene.Dispose(); sink.Dispose();
        DirectorOffscreenRenderRuntime.Renderer = previous;
    }
}
