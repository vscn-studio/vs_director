using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VSDirector.Rendering;

internal sealed class DirectorGreenScreenPreviewRenderer : IRenderer
{
    internal const string PlaneModelCode = "vsdirector:greenscreen-plane";
    internal const string RingModelCode = "vsdirector:greenscreen-ring";
    internal const string FloorModelCode = "vsdirector:greenscreen-floor";

    private static readonly int GreenColor = ColorUtil.ToRgba(255, 0, 255, 0);

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly Dictionary<string, MeshRef> meshes = new(StringComparer.OrdinalIgnoreCase);
    private string activeModelCode = string.Empty;
    private double defaultDistance = 5d;
    private double distance = 5d;
    private double yawOffset;
    private float modelSize = 1f;
    private bool disposed;

    internal DirectorGreenScreenPreviewRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        game = capi.World as ClientMain
            ?? throw new InvalidOperationException("VS Director green-screen preview requires the desktop client.");
        capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "vsdirector-green-screen-preview");
    }

    public double RenderOrder => 0.68d;

    public int RenderRange => int.MaxValue;

    internal bool IsActive => activeModelCode.Length > 0;

    internal static bool IsGreenScreenModelCode(string? code)
        => string.Equals(code, PlaneModelCode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, RingModelCode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, FloorModelCode, StringComparison.OrdinalIgnoreCase);

    internal bool Start(string modelCode, float size)
    {
        string code = modelCode?.Trim() ?? string.Empty;
        if (!IsGreenScreenModelCode(code))
        {
            return false;
        }
        activeModelCode = code;
        modelSize = float.IsFinite(size) ? Math.Clamp(size, 0.1f, 10f) : 1f;
        defaultDistance = code == RingModelCode ? 5.5d : 5d;
        distance = defaultDistance;
        yawOffset = 0d;
        EnsureMesh(code);
        return true;
    }

    internal void Stop()
    {
        activeModelCode = string.Empty;
        defaultDistance = 5d;
        distance = 5d;
        yawOffset = 0d;
        modelSize = 1f;
    }

    internal bool Rotate(double deltaYaw)
    {
        if (!IsActive || !double.IsFinite(deltaYaw))
        {
            return false;
        }
        yawOffset = NormalizeRadians(yawOffset + deltaYaw);
        return true;
    }

    internal bool ScaleDistance(double scale)
    {
        if (!IsActive || !double.IsFinite(scale) || scale <= 0d)
        {
            return false;
        }
        distance = Math.Clamp(distance * scale, 0.75d, 14d);
        return true;
    }

    internal bool ResetTransform()
    {
        if (!IsActive)
        {
            return false;
        }
        distance = defaultDistance;
        yawOffset = 0d;
        return true;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        _ = deltaTime;
        _ = stage;
        if (!IsActive
            || capi.World.Player?.Entity is not EntityPlayer player
            || !meshes.TryGetValue(activeModelCode, out MeshRef? mesh))
        {
            return;
        }

        Vec3d eye = game.MainCamera.CameraEyePos;
        Vec3d forward = game.MainCamera.forwardVec;
        double cameraYaw = game.MainCamera.Yaw;
        if (!Finite(eye) || !Finite(forward) || forward.LengthSq() < 0.25d)
        {
            eye = new Vec3d(
                player.CameraPos.X + player.LocalEyePos.X,
                player.CameraPos.Y + player.LocalEyePos.Y,
                player.CameraPos.Z + player.LocalEyePos.Z);
            Vec3f fallback = EntityPos.GetViewVector(
                capi.World.Player.CameraPitch,
                capi.World.Player.CameraYaw);
            forward = new Vec3d(fallback.X, fallback.Y, fallback.Z);
            cameraYaw = capi.World.Player.CameraYaw;
        }

        Vec3d center = eye.AddCopy(
            forward.X * distance,
            forward.Y * distance,
            forward.Z * distance);
        ShaderProgramAutocamera shader = ShaderPrograms.Autocamera;
        game.Platform.GlEnableDepthTest();
        game.Platform.GlDisableCullFace();
        shader.Use();
        game.GlMatrixModeModelView();
        game.GlPushMatrix();
        try
        {
            game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);
            game.GlTranslate(
                center.X - player.CameraPos.X,
                center.Y - player.CameraPos.Y,
                center.Z - player.CameraPos.Z);
            game.GlRotate(
                (float)((cameraYaw + yawOffset) * GameMath.RAD2DEG),
                0d,
                1d,
                0d);
            game.GlScale(modelSize, modelSize, modelSize);
            shader.ProjectionMatrix = game.CurrentProjectionMatrix;
            shader.ModelViewMatrix = game.CurrentModelViewMatrix;
            game.Platform.RenderMesh(mesh);
        }
        finally
        {
            game.GlPopMatrix();
            shader.Stop();
            game.Platform.GlDisableCullFace();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        foreach (MeshRef mesh in meshes.Values)
        {
            mesh.Dispose();
        }
        meshes.Clear();
    }

    private void EnsureMesh(string code)
    {
        if (meshes.ContainsKey(code))
        {
            return;
        }
        MeshData data = code switch
        {
            RingModelCode => BuildRing(),
            FloorModelCode => BuildFloor(),
            _ => BuildPlane()
        };
        meshes[code] = capi.Render.UploadMesh(data);
    }

    private static MeshData BuildPlane()
    {
        MeshData data = NewMesh(4, 6);
        AddQuad(data,
            new Vec3f(-2.75f, -1.6f, 0f),
            new Vec3f(2.75f, -1.6f, 0f),
            new Vec3f(2.75f, 1.6f, 0f),
            new Vec3f(-2.75f, 1.6f, 0f));
        return data;
    }

    private static MeshData BuildFloor()
    {
        MeshData data = NewMesh(4, 6);
        AddQuad(data,
            new Vec3f(-3f, -1.55f, 1.8f),
            new Vec3f(3f, -1.55f, 1.8f),
            new Vec3f(3f, -1.55f, -3.8f),
            new Vec3f(-3f, -1.55f, -3.8f));
        return data;
    }

    private static MeshData BuildRing()
    {
        const int segments = 28;
        const float radius = 3.2f;
        MeshData data = NewMesh(segments * 4, segments * 6);
        for (int segment = 0; segment < segments; segment++)
        {
            float first = GameMath.DEG2RAD * (-105f + 210f * segment / segments);
            float second = GameMath.DEG2RAD * (-105f + 210f * (segment + 1) / segments);
            Vec3f bottomLeft = RingPoint(first, -1.6f, radius);
            Vec3f bottomRight = RingPoint(second, -1.6f, radius);
            Vec3f topRight = RingPoint(second, 1.6f, radius);
            Vec3f topLeft = RingPoint(first, 1.6f, radius);
            AddQuad(data, bottomLeft, bottomRight, topRight, topLeft);
        }
        return data;
    }

    private static Vec3f RingPoint(float angle, float y, float radius)
        => new(
            GameMath.Sin(angle) * radius,
            y,
            (GameMath.Cos(angle) - 1f) * radius);

    private static MeshData NewMesh(int vertices, int indices)
    {
        MeshData data = new(vertices, indices, withNormals: false, withUv: false);
        data.SetMode(EnumDrawMode.Triangles);
        return data;
    }

    private static void AddQuad(MeshData data, Vec3f a, Vec3f b, Vec3f c, Vec3f d)
    {
        int first = data.VerticesCount;
        data.AddVertexSkipTex(a.X, a.Y, a.Z, GreenColor);
        data.AddVertexSkipTex(b.X, b.Y, b.Z, GreenColor);
        data.AddVertexSkipTex(c.X, c.Y, c.Z, GreenColor);
        data.AddVertexSkipTex(d.X, d.Y, d.Z, GreenColor);
        data.AddIndex(first);
        data.AddIndex(first + 1);
        data.AddIndex(first + 2);
        data.AddIndex(first);
        data.AddIndex(first + 2);
        data.AddIndex(first + 3);
    }

    private static bool Finite(Vec3d value)
        => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static double NormalizeRadians(double value)
    {
        double normalized = value % (Math.PI * 2d);
        return normalized > Math.PI
            ? normalized - Math.PI * 2d
            : normalized < -Math.PI
                ? normalized + Math.PI * 2d
                : normalized;
    }
}
