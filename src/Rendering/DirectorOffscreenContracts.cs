namespace VSDirector.Rendering;

[Flags]
public enum DirectorOffscreenCapabilities
{
    None = 0,
    IndependentColorDepthTarget = 1 << 0,
    IndependentCameraPose = 1 << 1,
    SinglePhysicalTick = 1 << 2,
    OpaqueTerrain = 1 << 3,
    OpaqueEntities = 1 << 4,
    IsolatedOpaqueParticles = 1 << 5,
    TransparentOit = 1 << 6,
    IndependentShadows = 1 << 7,
    IndependentPostProcessing = 1 << 8
}

public enum DirectorOffscreenPass
{
    OpaqueWorld,
    OpaqueParticles
}

/// <summary>
/// A dimension-local camera pose. Y does not include Vintage Story's 32768-block
/// dimension offset. Angles and field of view are expressed in degrees.
/// </summary>
public readonly record struct DirectorOffscreenCameraPose(
    double X,
    double Y,
    double Z,
    int Dimension,
    float Yaw,
    float Pitch,
    float Roll,
    float Fov);

public sealed class DirectorOffscreenViewRequest
{
    public Guid ViewId { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "Offscreen View";

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public DirectorOffscreenCameraPose Pose { get; init; }

    /// <summary>
    /// Optional monotonic capture clock for rate-controlled sinks. Fixed live
    /// views use the game's elapsed clock; workflow views use their shared
    /// Stopwatch timeline so every pass has the same time base.
    /// </summary>
    public long? CaptureClockMilliseconds { get; init; }

    public bool RenderTerrain { get; init; } = true;

    public bool RenderEntities { get; init; } = true;

    /// <summary>
    /// Rendering the local actor requires a third-person player shape prepared by
    /// the active director camera. It is disabled by default so an ordinary
    /// first-person arm mesh is never presented as a valid offscreen actor.
    /// </summary>
    public bool RenderLocalPlayer { get; init; }

    public bool RenderOpaqueParticles { get; init; } = true;
}

/// <summary>
/// A GPU frame produced on the render thread. The texture and framebuffer handles
/// remain owned by VS Director and are valid until the view is resized, removed,
/// or the renderer is disposed. A sink that needs the pixels after this callback
/// must enqueue its own GPU copy or fence before returning.
/// </summary>
public readonly record struct DirectorOffscreenFrame(
    Guid ViewId,
    string ViewName,
    long PhysicalFrameNumber,
    long WorldElapsedMilliseconds,
    int Width,
    int Height,
    int FramebufferId,
    int ColorTextureId,
    int DepthTextureId,
    DirectorOffscreenCapabilities Capabilities);

public interface IDirectorOffscreenFrameSink
{
    void OnFrame(in DirectorOffscreenFrame frame);
}

/// <summary>
/// Optional render-thread extension point. Contributors are called only inside
/// the explicitly named pass and must not advance world, animation, or particle
/// simulation state.
/// </summary>
public interface IDirectorOffscreenPassRenderer
{
    DirectorOffscreenPass Pass { get; }

    double RenderOrder { get; }

    void Render(in DirectorOffscreenPassContext context);
}

public readonly record struct DirectorOffscreenPassContext(
    DirectorOffscreenViewRequest View,
    long PhysicalFrameNumber,
    long WorldElapsedMilliseconds,
    int ColorTextureId,
    int DepthTextureId);
