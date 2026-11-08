using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using VSDirector.Rendering;
using VSDirector.Replay;
using DirectorKeyframe = VSDirector.Data.CameraKeyframe;

namespace VSDirector.Runtime;

internal readonly record struct DirectorReplayCameraGuideTarget(
    double X,
    double Y,
    double Z,
    int Dimension,
    long EntityId = 0L);

internal sealed record DirectorReplayCurrentCameraGuide(
    CameraPreset Camera,
    DirectorReplayCameraGuideTarget? Target = null);

internal sealed class DirectorPathPreviewRenderer : IRenderer
{
    private const int SamplesPerSegment = 24;
    private const float MarkerRadius = 0.35f;
    private static readonly IReadOnlySet<int> EmptySelectedKeyframes = new HashSet<int>();
    private static readonly int PathColor = ColorUtil.ToRgba(255, 55, 210, 235);
    private static readonly int MarkerColor = ColorUtil.ToRgba(255, 72, 218, 224);
    private static readonly int SelectedMarkerColor = ColorUtil.ToRgba(255, 250, 190, 45);
    private static readonly int SelectedPathColor = ColorUtil.ToRgba(255, 72, 224, 132);
    private static readonly int BezierHandleColor = ColorUtil.ToRgba(255, 190, 122, 212);
    private static readonly int HoverPreviewColor = ColorUtil.ToRgba(255, 92, 203, 224);
    private static readonly int CameraBodyColor = ColorUtil.ToRgba(255, 70, 150, 255);
    private static readonly int CameraDirectionColor = ColorUtil.ToRgba(255, 70, 150, 255);
    private static readonly int CurrentCameraColor = ColorUtil.ToRgba(255, 70, 150, 255);

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly Func<IReadOnlyList<CameraPreset>> camerasProvider;
    private readonly Func<IReadOnlyList<CameraPath>> pathsProvider;
    private readonly Func<IReadOnlyList<CameraPath>> replayCameraPathsProvider;
    private readonly Func<IReadOnlyList<DirectorReplayOrbitGuide>> replayOrbitGuidesProvider;
    private readonly Func<bool> editorVisibleProvider;
    private readonly Func<bool> captureActiveProvider;
    private readonly List<PreviewMesh> meshes = new();
    private readonly MeshRef trackingBoundsMesh;
    private readonly MeshRef lookDirectionMesh;
    private CameraPath? transientPath;
    private int transientSelectedKeyframe = -1;
    private readonly HashSet<int> transientSelectedKeyframes = new();
    private double? transientPreviewTime;
    private DirectorReplayCurrentCameraGuide? currentReplayGuide;
    private PreviewMesh? currentReplayGuideMesh;

    public DirectorPathPreviewRenderer(
        ICoreClientAPI capi,
        Func<IReadOnlyList<CameraPreset>> camerasProvider,
        Func<IReadOnlyList<CameraPath>> pathsProvider,
        Func<IReadOnlyList<CameraPath>> replayCameraPathsProvider,
        Func<IReadOnlyList<DirectorReplayOrbitGuide>> replayOrbitGuidesProvider,
        Func<bool>? editorVisibleProvider = null,
        Func<bool>? captureActiveProvider = null)
    {
        this.capi = capi;
        this.camerasProvider = camerasProvider;
        this.pathsProvider = pathsProvider;
        this.replayCameraPathsProvider = replayCameraPathsProvider;
        this.replayOrbitGuidesProvider = replayOrbitGuidesProvider;
        this.editorVisibleProvider = editorVisibleProvider ?? (() => false);
        this.captureActiveProvider = captureActiveProvider ?? (() => false);
        game = capi.World as ClientMain
            ?? throw new InvalidOperationException("VS Director path preview requires the desktop client.");
        trackingBoundsMesh = capi.Render.UploadMesh(BuildUnitBoundsMesh());
        lookDirectionMesh = capi.Render.UploadMesh(BuildLookDirectionMesh());
        capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "vsdirector-path-preview");
    }

    public double RenderOrder => 0.7d;

    public int RenderRange => int.MaxValue;

    public void Refresh()
    {
        ClearMeshes();
        foreach (CameraPreset camera in camerasProvider())
        {
            AddCamera(camera);
        }
        foreach (CameraPath path in pathsProvider())
        {
            if (transientPath?.Id == path.Id)
            {
                continue;
            }

            AddPath(path, selectedKeyframe: -1, selectedKeyframes: null);
        }
        foreach (CameraPath path in replayCameraPathsProvider())
        {
            AddReplayCameraPath(path);
        }
        foreach (DirectorReplayOrbitGuide guide in replayOrbitGuidesProvider())
        {
            AddReplayOrbitGuide(guide);
        }

        if (transientPath is not null)
        {
            AddPath(
                transientPath,
                transientSelectedKeyframe,
                transientSelectedKeyframes,
                transientPreviewTime);
        }
    }

    public void SetTransientPath(
        CameraPath? path,
        int selectedKeyframe = -1,
        double? previewTime = null,
        IReadOnlyCollection<int>? selectedKeyframes = null)
    {
        transientPath = path?.DeepClone();
        transientSelectedKeyframe = transientPath is null
            ? -1
            : Math.Clamp(selectedKeyframe, -1, transientPath.Keyframes.Count - 1);
        transientSelectedKeyframes.Clear();
        if (transientPath is not null)
        {
            foreach (int index in selectedKeyframes ?? Array.Empty<int>())
            {
                if (index >= 0 && index < transientPath.Keyframes.Count)
                {
                    transientSelectedKeyframes.Add(index);
                }
            }
            if (transientSelectedKeyframe >= 0)
            {
                transientSelectedKeyframes.Add(transientSelectedKeyframe);
            }
        }
        transientPreviewTime = transientPath is null || previewTime is not double time || !double.IsFinite(time)
            ? null
            : Math.Clamp(time, 0d, transientPath.DurationSeconds);
        Refresh();
    }


    public void SetCurrentReplayCameraGuide(DirectorReplayCurrentCameraGuide? guide)
    {
        if (SameCurrentReplayGuide(currentReplayGuide, guide))
        {
            return;
        }

        currentReplayGuide = guide is null
            ? null
            : new DirectorReplayCurrentCameraGuide(
                guide.Camera.DeepClone(),
                guide.Target);
        currentReplayGuideMesh?.Mesh.Dispose();
        currentReplayGuideMesh = currentReplayGuide is null
            ? null
            : BuildCurrentReplayCameraGuideMesh(currentReplayGuide);
    }

    private static bool SameCurrentReplayGuide(
        DirectorReplayCurrentCameraGuide? left,
        DirectorReplayCurrentCameraGuide? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is null || right is null || left.Target != right.Target)
        {
            return false;
        }

        CameraPreset a = left.Camera;
        CameraPreset b = right.Camera;
        return a.Dimension == b.Dimension
            && a.X == b.X
            && a.Y == b.Y
            && a.Z == b.Z
            && a.Yaw == b.Yaw
            && a.Pitch == b.Pitch
            && a.Roll == b.Roll
            && a.Fov == b.Fov;
    }


    public void Clear()
    {
        transientPath = null;
        transientSelectedKeyframe = -1;
        transientSelectedKeyframes.Clear();
        transientPreviewTime = null;
        currentReplayGuide = null;
        currentReplayGuideMesh?.Mesh.Dispose();
        currentReplayGuideMesh = null;
        ClearMeshes();
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        DirectorReplayCompositionVisuals? composition =
            DirectorReplayVisualRuntime.Current?.Composition;
        bool replayGuidesVisible = composition?.CameraPath == true;
        DirectorReplayWorldVisuals? replayWorld = DirectorReplayVisualRuntime.Current?.World;
        bool replayEntityVisualsVisible = replayWorld?.ShowEntityBoxes == true
            || replayWorld?.ShowEntityLookDirections == true;
        if (game.EntityPlayer is null
            || meshes.Count == 0 && currentReplayGuideMesh is null && !replayGuidesVisible
                && !replayEntityVisualsVisible
            || !(ShouldRenderPreview(
                game.ShouldRender2DOverlays,
                editorVisibleProvider(),
                captureActiveProvider(),
                DirectorOffscreenRenderRuntime.IsRendering)
                || replayGuidesVisible
                    && !captureActiveProvider()
                    && !DirectorOffscreenRenderRuntime.IsRendering))
        {
            return;
        }

        int playerDimension = game.EntityPlayer.Pos.Dimension;
        ShaderProgramAutocamera shader = ShaderPrograms.Autocamera;
        shader.Use();
        game.Platform.GLLineWidth(2f);
        game.Platform.BindTexture2d(0);
        foreach (PreviewMesh preview in meshes)
        {
            RenderPreviewMesh(preview, playerDimension, composition, shader);
        }
        if (currentReplayGuideMesh is { } current)
        {
            RenderPreviewMesh(current, playerDimension, composition, shader);
        }
        if (replayGuidesVisible
            && currentReplayGuide?.Target is { EntityId: not 0L } target)
        {
            RenderTrackingBounds(shader, playerDimension, target.EntityId);
        }
        if (replayEntityVisualsVisible)
        {
            RenderReplayEntityVisualizations(shader, playerDimension, replayWorld!);
        }

        shader.Stop();
    }

    private void RenderPreviewMesh(
        PreviewMesh preview,
        int playerDimension,
        DirectorReplayCompositionVisuals? composition,
        ShaderProgramAutocamera shader)
    {
        if (preview.Dimension != playerDimension || !ShouldRenderMesh(preview.Kind, composition))
        {
            return;
        }

        game.GlPushMatrix();
        game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);
        Vec3d cameraPos = game.EntityPlayer.CameraPos;
        game.GlTranslate(
            preview.OriginX - cameraPos.X,
            preview.OriginY - cameraPos.Y,
            preview.OriginZ - cameraPos.Z);
        shader.ProjectionMatrix = game.CurrentProjectionMatrix;
        shader.ModelViewMatrix = game.CurrentModelViewMatrix;
        game.Platform.RenderMesh(preview.Mesh);
        game.GlPopMatrix();
    }

    internal static bool ShouldRenderPreview(
        bool overlaysVisible,
        bool editorVisible,
        bool captureActive,
        bool offscreenRendering)
        => !captureActive
            && !offscreenRendering
            && (overlaysVisible || editorVisible);

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        ClearMeshes();
        currentReplayGuideMesh?.Mesh.Dispose();
        currentReplayGuideMesh = null;
        trackingBoundsMesh.Dispose();
        lookDirectionMesh.Dispose();
    }

    private PreviewMesh? BuildMesh(
        CameraPath path,
        int dimension,
        int selectedKeyframe,
        IReadOnlySet<int> selectedKeyframes,
        double? previewTime)
    {
        DirectorKeyframe first = path.Keyframes[0];
        double originX = Math.Floor(first.X);
        double originY = Math.Floor(first.Y + dimension * 32768d);
        double originZ = Math.Floor(first.Z);
        int estimatedLines = (path.Keyframes.Count - 1) * SamplesPerSegment + path.Keyframes.Count * 4;
        MeshData data = new(Math.Max(8, estimatedLines * 2), Math.Max(8, estimatedLines * 2), withNormals: false, withUv: false);
        data.SetMode(EnumDrawMode.Lines);

        bool hasSelectedRange = selectedKeyframes.Count > 1;

        for (int segment = 0; segment < path.Keyframes.Count - 1; segment++)
        {
            double start = path.Keyframes[segment].TimeSeconds;
            double end = path.Keyframes[segment + 1].TimeSeconds;
            if (!CameraPathSampler.TryEvaluate(path, start, out DirectorKeyframe previous))
            {
                continue;
            }

            bool selectedSegment = selectedKeyframes.Contains(segment)
                && selectedKeyframes.Contains(segment + 1);
            for (int sample = 1; sample <= SamplesPerSegment; sample++)
            {
                double time = start + (end - start) * sample / SamplesPerSegment;
                if (!CameraPathSampler.TryEvaluate(path, time, out DirectorKeyframe current))
                {
                    continue;
                }

                AddLine(data, previous, current, originX, originY, originZ,
                    selectedSegment ? SelectedPathColor : PathColor);
                previous = current;
            }
        }

        for (int index = 0; index < path.Keyframes.Count; index++)
        {
            DirectorKeyframe frame = path.Keyframes[index];
            bool rangeSelected = hasSelectedRange && selectedKeyframes.Contains(index);
            bool selected = rangeSelected || index == selectedKeyframe;
            float radius = selected ? MarkerRadius * 1.55f : MarkerRadius;
            int markerColor = rangeSelected
                ? SelectedPathColor
                : selected ? SelectedMarkerColor : MarkerColor;
            float x = (float)(frame.X - originX);
            float y = (float)(frame.Y + dimension * 32768d - originY);
            float z = (float)(frame.Z - originZ);
            AddLine(data, x - radius, y, z, x + radius, y, z, markerColor);
            AddLine(data, x, y - radius, z, x, y + radius, z, markerColor);
            AddLine(data, x, y, z - radius, x, y, z + radius, markerColor);

            if (index == selectedKeyframe && frame.BezierHandlesEnabled)
            {
                AddBezierHandle(data, x, y, z,
                    x + (float)frame.BezierInX,
                    y + (float)frame.BezierInY,
                    z + (float)frame.BezierInZ);
                AddBezierHandle(data, x, y, z,
                    x + (float)frame.BezierOutX,
                    y + (float)frame.BezierOutY,
                    z + (float)frame.BezierOutZ);
            }
        }

        if (previewTime is double previewTimeSeconds
            && CameraPathSampler.TryEvaluate(path, previewTimeSeconds, out DirectorKeyframe preview))
        {
            const float radius = MarkerRadius * 1.8f;
            float x = (float)(preview.X - originX);
            float y = (float)(preview.Y + dimension * 32768d - originY);
            float z = (float)(preview.Z - originZ);
            AddLine(data, x - radius, y, z, x + radius, y, z, HoverPreviewColor);
            AddLine(data, x, y - radius, z, x, y + radius, z, HoverPreviewColor);
            AddLine(data, x, y, z - radius, x, y, z + radius, HoverPreviewColor);
            Vec3f forward = EntityPos.GetViewVector(
                preview.Pitch * GameMath.DEG2RAD,
                preview.Yaw * GameMath.DEG2RAD);
            AddLine(data, x, y, z,
                x + forward.X * 1.6f,
                y + forward.Y * 1.6f,
                z + forward.Z * 1.6f,
                HoverPreviewColor);
        }

        if (data.VerticesCount == 0)
        {
            return null;
        }

        return new PreviewMesh(
            capi.Render.UploadMesh(data),
            dimension,
            originX,
            originY,
            originZ,
            PreviewKind.Editor);
    }

    private static void AddBezierHandle(
        MeshData data,
        float keyX,
        float keyY,
        float keyZ,
        float handleX,
        float handleY,
        float handleZ)
    {
        const float radius = 0.18f;
        AddLine(data, keyX, keyY, keyZ, handleX, handleY, handleZ, BezierHandleColor);
        AddLine(data, handleX - radius, handleY, handleZ,
            handleX + radius, handleY, handleZ, BezierHandleColor);
        AddLine(data, handleX, handleY - radius, handleZ,
            handleX, handleY + radius, handleZ, BezierHandleColor);
        AddLine(data, handleX, handleY, handleZ - radius,
            handleX, handleY, handleZ + radius, BezierHandleColor);
    }

    private PreviewMesh? BuildCameraMesh(CameraPreset camera)
    {
        double originX = Math.Floor(camera.X);
        double originY = Math.Floor(camera.Y + camera.Dimension * 32768d);
        double originZ = Math.Floor(camera.Z);
        float x = (float)(camera.X - originX);
        float y = (float)(camera.Y + camera.Dimension * 32768d - originY);
        float z = (float)(camera.Z - originZ);
        MeshData data = new(48, 48, withNormals: false, withUv: false);
        data.SetMode(EnumDrawMode.Lines);

        AddCameraFrustum(data, x, y, z, camera.Yaw, camera.Pitch, camera.Roll, camera.Fov);

        return new PreviewMesh(
            capi.Render.UploadMesh(data),
            camera.Dimension,
            originX,
            originY,
            originZ,
            PreviewKind.Editor);
    }

    private PreviewMesh? BuildReplayCameraPathMesh(
        CameraPath path,
        int dimension)
    {
        DirectorKeyframe? first = path.Keyframes.FirstOrDefault(frame => frame.Dimension == dimension);
        if (first is null)
        {
            return null;
        }

        double originX = Math.Floor(first.X);
        double originY = Math.Floor(first.Y + dimension * 32768d);
        double originZ = Math.Floor(first.Z);
        int estimatedLines = Math.Max(
            12,
            (path.Keyframes.Count - 1) * SamplesPerSegment);
        MeshData data = new(
            estimatedLines * 2,
            estimatedLines * 2,
            withNormals: false,
            withUv: false);
        data.SetMode(EnumDrawMode.Lines);

        for (int segment = 0; segment < path.Keyframes.Count - 1; segment++)
        {
            DirectorKeyframe left = path.Keyframes[segment];
            DirectorKeyframe right = path.Keyframes[segment + 1];
            if (left.Dimension != dimension || right.Dimension != dimension
                || !CameraPathSampler.TryEvaluate(path, left.TimeSeconds, out DirectorKeyframe previous))
            {
                continue;
            }

            for (int sample = 1; sample <= SamplesPerSegment; sample++)
            {
                double time = left.TimeSeconds
                    + (right.TimeSeconds - left.TimeSeconds) * sample / SamplesPerSegment;
                if (!CameraPathSampler.TryEvaluate(path, time, out DirectorKeyframe current)
                    || current.Dimension != dimension)
                {
                    continue;
                }

                AddLine(data, previous, current, originX, originY, originZ, PathColor);
                previous = current;
            }
        }

        return data.VerticesCount == 0
            ? null
            : new PreviewMesh(
                capi.Render.UploadMesh(data),
                dimension,
                originX,
                originY,
                originZ,
                PreviewKind.ReplayPath);
    }

    private PreviewMesh? BuildReplayOrbitGuideMesh(
        DirectorReplayOrbitGuide guide,
        int dimension)
    {
        CameraPreset? first = guide.Samples
            .Select(sample => sample.Camera)
            .Concat(guide.Keyframes.Select(keyframe => keyframe.Camera))
            .FirstOrDefault(camera => camera.Dimension == dimension);
        if (first is null)
        {
            return null;
        }

        double originX = Math.Floor(first.X);
        double originY = Math.Floor(first.Y + dimension * 32768d);
        double originZ = Math.Floor(first.Z);
        int estimatedLines = Math.Max(1, guide.Samples.Count - 1);
        MeshData data = new(
            estimatedLines * 2,
            estimatedLines * 2,
            withNormals: false,
            withUv: false);
        data.SetMode(EnumDrawMode.Lines);

        for (int index = 1; index < guide.Samples.Count; index++)
        {
            CameraPreset left = guide.Samples[index - 1].Camera;
            CameraPreset right = guide.Samples[index].Camera;
            if (left.Dimension != dimension || right.Dimension != dimension)
            {
                continue;
            }

            AddLine(
                data,
                left.X,
                left.Y,
                left.Z,
                right.X,
                right.Y,
                right.Z,
                dimension,
                originX,
                originY,
                originZ,
                PathColor);
        }

        return data.VerticesCount == 0
            ? null
            : new PreviewMesh(
                capi.Render.UploadMesh(data),
                dimension,
                originX,
                originY,
                originZ,
                PreviewKind.ReplayPath);
    }

    private PreviewMesh? BuildCurrentReplayCameraGuideMesh(
        DirectorReplayCurrentCameraGuide guide)
    {
        CameraPreset camera = guide.Camera;
        int dimension = camera.Dimension;
        double originX = Math.Floor(camera.X);
        double originY = Math.Floor(camera.Y + dimension * 32768d);
        double originZ = Math.Floor(camera.Z);
        MeshData data = new(32, 32, withNormals: false, withUv: false);
        data.SetMode(EnumDrawMode.Lines);
        float cameraX = (float)(camera.X - originX);
        float cameraY = (float)(camera.Y + dimension * 32768d - originY);
        float cameraZ = (float)(camera.Z - originZ);
        AddCameraFrustum(
            data,
            cameraX,
            cameraY,
            cameraZ,
            camera.Yaw,
            camera.Pitch,
            camera.Roll,
            camera.Fov,
            CurrentCameraColor);

        Vec3f forward = EntityPos.GetViewVector(camera.Pitch * GameMath.DEG2RAD, camera.Yaw * GameMath.DEG2RAD);
        AddLine(data, cameraX, cameraY, cameraZ, cameraX + forward.X * 2.05f,
            cameraY + forward.Y * 2.05f, cameraZ + forward.Z * 2.05f, CurrentCameraColor);

        if (guide.Target is { } target && target.Dimension == dimension)
        {
            float targetX = (float)(target.X - originX);
            float targetY = (float)(target.Y + dimension * 32768d - originY);
            float targetZ = (float)(target.Z - originZ);
            AddLine(data, cameraX, cameraY, cameraZ, targetX, targetY, targetZ, CurrentCameraColor);
            AddPointMarker(
                data,
                targetX,
                targetY,
                targetZ,
                MarkerRadius * 1.35f,
                CurrentCameraColor);
        }

        return new PreviewMesh(
            capi.Render.UploadMesh(data),
            dimension,
            originX,
            originY,
            originZ,
            PreviewKind.ReplayCurrent);
    }

    private static void AddCameraFrustum(
        MeshData data,
        float x,
        float y,
        float z,
        float yaw,
        float pitch,
        float roll,
        float fov)
        => AddCameraFrustum(
            data,
            x,
            y,
            z,
            yaw,
            pitch,
            roll,
            fov,
            CameraBodyColor);

    private static void AddCameraFrustum(
        MeshData data,
        float x,
        float y,
        float z,
        float yaw,
        float pitch,
        float roll,
        float fov,
        int bodyColor)
    {

        Vec3f forward = EntityPos.GetViewVector(
            pitch * GameMath.DEG2RAD,
            yaw * GameMath.DEG2RAD);
        Normalize(forward);
        float pitchRadians = pitch * GameMath.DEG2RAD;
        float yawRadians = yaw * GameMath.DEG2RAD;
        Vec3f up = new(
            GameMath.Sin(pitchRadians) * GameMath.Sin(yawRadians),
            GameMath.Cos(pitchRadians),
            GameMath.Sin(pitchRadians) * GameMath.Cos(yawRadians));
        Normalize(up);
        Vec3f right = new(
            forward.Y * up.Z - forward.Z * up.Y,
            forward.Z * up.X - forward.X * up.Z,
            forward.X * up.Y - forward.Y * up.X);
        if (right.X * right.X + right.Y * right.Y + right.Z * right.Z < 0.000001f)
        {
            right.Set(1f, 0f, 0f);
        }
        Normalize(right);
        float cosRoll = GameMath.Cos(roll * GameMath.DEG2RAD);
        float sinRoll = GameMath.Sin(roll * GameMath.DEG2RAD);
        Vec3f rolledRight = new(
            right.X * cosRoll - up.X * sinRoll,
            right.Y * cosRoll - up.Y * sinRoll,
            right.Z * cosRoll - up.Z * sinRoll);
        Vec3f rolledUp = new(
            right.X * sinRoll + up.X * cosRoll,
            right.Y * sinRoll + up.Y * cosRoll,
            right.Z * sinRoll + up.Z * cosRoll);

        const float depth = 1.25f;
        float fovScale = (float)Math.Tan(
            Math.Clamp(fov, 10f, DirectorReplayElementTimeline.MaxFovDegrees)
                * GameMath.DEG2RAD / 2f);
        float halfWidth = Math.Clamp(fovScale * 0.42f, 0.22f, 0.8f);
        float halfHeight = halfWidth * 0.62f;
        Vec3f center = new(x + forward.X * depth, y + forward.Y * depth, z + forward.Z * depth);
        Vec3f topLeft = Corner(center, rolledRight, rolledUp, -halfWidth, halfHeight);
        Vec3f topRight = Corner(center, rolledRight, rolledUp, halfWidth, halfHeight);
        Vec3f bottomRight = Corner(center, rolledRight, rolledUp, halfWidth, -halfHeight);
        Vec3f bottomLeft = Corner(center, rolledRight, rolledUp, -halfWidth, -halfHeight);
        foreach (Vec3f corner in new[] { topLeft, topRight, bottomRight, bottomLeft })
        {
            AddLine(data, x, y, z, corner.X, corner.Y, corner.Z, bodyColor);
        }
        AddLine(data, topLeft.X, topLeft.Y, topLeft.Z, topRight.X, topRight.Y, topRight.Z, bodyColor);
        AddLine(data, topRight.X, topRight.Y, topRight.Z, bottomRight.X, bottomRight.Y, bottomRight.Z, bodyColor);
        AddLine(data, bottomRight.X, bottomRight.Y, bottomRight.Z, bottomLeft.X, bottomLeft.Y, bottomLeft.Z, bodyColor);
        AddLine(data, bottomLeft.X, bottomLeft.Y, bottomLeft.Z, topLeft.X, topLeft.Y, topLeft.Z, bodyColor);
    }

    private static void AddPointMarker(
        MeshData data,
        float x,
        float y,
        float z,
        float radius,
        int color)
    {
        AddLine(data, x - radius, y, z, x + radius, y, z, color);
        AddLine(data, x, y - radius, z, x, y + radius, z, color);
        AddLine(data, x, y, z - radius, x, y, z + radius, color);
    }



    private static Vec3f Corner(
        Vec3f center,
        Vec3f right,
        Vec3f up,
        float horizontal,
        float vertical)
        => new(
            center.X + right.X * horizontal + up.X * vertical,
            center.Y + right.Y * horizontal + up.Y * vertical,
            center.Z + right.Z * horizontal + up.Z * vertical);

    private static void Normalize(Vec3f vector)
    {
        float length = vector.Length();
        if (length > 0.000001f)
        {
            vector.Mul(1f / length);
        }
    }

    private void AddCamera(CameraPreset camera)
    {
        if (!camera.PreviewVisible)
        {
            return;
        }

        PreviewMesh? preview = BuildCameraMesh(camera);
        if (preview is not null)
        {
            meshes.Add(preview);
        }
    }

    private void AddPath(
        CameraPath path,
        int selectedKeyframe,
        IReadOnlySet<int>? selectedKeyframes,
        double? previewTime = null)
    {
        if (!path.PreviewVisible || path.Keyframes.Count < 2)
        {
            return;
        }

        int dimension = path.Keyframes[0].Dimension;
        if (path.Keyframes.Any(frame => frame.Dimension != dimension))
        {
            return;
        }

        PreviewMesh? preview = BuildMesh(
            path,
            dimension,
            selectedKeyframe,
            selectedKeyframes ?? EmptySelectedKeyframes,
            previewTime);
        if (preview is not null)
        {
            meshes.Add(preview);
        }
    }

    private void AddReplayCameraPath(CameraPath path)
    {
        if (!path.PreviewVisible || path.Keyframes.Count == 0)
        {
            return;
        }

        foreach (int dimension in path.Keyframes.Select(frame => frame.Dimension).Distinct())
        {
            PreviewMesh? preview = BuildReplayCameraPathMesh(path, dimension);
            if (preview is not null)
            {
                meshes.Add(preview);
            }
        }
    }

    private void AddReplayOrbitGuide(DirectorReplayOrbitGuide guide)
    {
        int[] dimensions = guide.Samples
            .Select(sample => sample.Camera.Dimension)
            .Concat(guide.Keyframes.Select(keyframe => keyframe.Dimension))
            .Distinct()
            .ToArray();
        foreach (int dimension in dimensions)
        {
            PreviewMesh? preview = BuildReplayOrbitGuideMesh(guide, dimension);
            if (preview is not null)
            {
                meshes.Add(preview);
            }
        }
    }


    private static void AddLine(
        MeshData data,
        DirectorKeyframe from,
        DirectorKeyframe to,
        double originX,
        double originY,
        double originZ,
        int color)
    {
        AddLine(
            data,
            (float)(from.X - originX),
            (float)(from.Y + from.Dimension * 32768d - originY),
            (float)(from.Z - originZ),
            (float)(to.X - originX),
            (float)(to.Y + to.Dimension * 32768d - originY),
            (float)(to.Z - originZ),
            color);
    }

    private static void AddLine(
        MeshData data,
        double x1,
        double y1,
        double z1,
        double x2,
        double y2,
        double z2,
        int dimension,
        double originX,
        double originY,
        double originZ,
        int color)
    {
        AddLine(
            data,
            (float)(x1 - originX),
            (float)(y1 + dimension * 32768d - originY),
            (float)(z1 - originZ),
            (float)(x2 - originX),
            (float)(y2 + dimension * 32768d - originY),
            (float)(z2 - originZ),
            color);
    }

    private static void AddLine(
        MeshData data,
        float x1,
        float y1,
        float z1,
        float x2,
        float y2,
        float z2,
        int color)
    {
        int index = data.VerticesCount;
        data.AddVertexSkipTex(x1, y1, z1, color);
        data.AddIndex(index);
        data.AddVertexSkipTex(x2, y2, z2, color);
        data.AddIndex(index + 1);
    }

    private void ClearMeshes()
    {
        foreach (PreviewMesh preview in meshes)
        {
            preview.Mesh.Dispose();
        }

        meshes.Clear();
    }

    private static bool ShouldRenderMesh(
        PreviewKind kind,
        DirectorReplayCompositionVisuals? composition)
        => kind switch
        {
            PreviewKind.ReplayPath => composition?.CameraPath == true,
            PreviewKind.ReplayCurrent => composition?.CameraPath == true,
            _ => true
        };

    private void RenderTrackingBounds(
        ShaderProgramAutocamera shader,
        int dimension,
        long targetEntityId)
    {
        IReadOnlyList<Entity> entities = DirectorReplayRuntime.Manager?.ReplayTrackingEntities
            ?? Array.Empty<Entity>();
        Vec3d cameraPos = game.EntityPlayer.CameraPos;
        foreach (Entity entity in entities)
        {
            if (entity.EntityId != targetEntityId
                || entity.Pos.Dimension != dimension
                || entity.SelectionBox is not Cuboidf box)
            {
                continue;
            }
            game.GlPushMatrix();
            game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);
            game.GlTranslate(
                entity.Pos.X + box.X1 - cameraPos.X,
                entity.Pos.Y + dimension * 32768d + box.Y1 - cameraPos.Y,
                entity.Pos.Z + box.Z1 - cameraPos.Z);
            game.GlScale(
                Math.Max(0.01f, box.X2 - box.X1),
                Math.Max(0.01f, box.Y2 - box.Y1),
                Math.Max(0.01f, box.Z2 - box.Z1));
            shader.ProjectionMatrix = game.CurrentProjectionMatrix;
            shader.ModelViewMatrix = game.CurrentModelViewMatrix;
            game.Platform.RenderMesh(trackingBoundsMesh);
            game.GlPopMatrix();
        }
    }

    private void RenderReplayEntityVisualizations(
        ShaderProgramAutocamera shader,
        int playerDimension,
        DirectorReplayWorldVisuals visuals)
    {
        IReadOnlyList<Entity> entities = DirectorReplayRuntime.Manager?.ReplayTrackingEntities
            ?? Array.Empty<Entity>();
        Vec3d cameraPos = game.EntityPlayer.CameraPos;
        foreach (Entity entity in entities)
        {
            if (entity.Pos.Dimension != playerDimension)
            {
                continue;
            }

            if (visuals.ShowEntityBoxes && entity.SelectionBox is Cuboidf box)
            {
                game.GlPushMatrix();
                try
                {
                    game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);
                    game.GlTranslate(
                        entity.Pos.X + box.X1 - cameraPos.X,
                        entity.Pos.Y + playerDimension * 32768d + box.Y1 - cameraPos.Y,
                        entity.Pos.Z + box.Z1 - cameraPos.Z);
                    game.GlScale(
                        Math.Max(0.01f, box.X2 - box.X1),
                        Math.Max(0.01f, box.Y2 - box.Y1),
                        Math.Max(0.01f, box.Z2 - box.Z1));
                    shader.ProjectionMatrix = game.CurrentProjectionMatrix;
                    shader.ModelViewMatrix = game.CurrentModelViewMatrix;
                    game.Platform.RenderMesh(trackingBoundsMesh);
                }
                finally
                {
                    game.GlPopMatrix();
                }
            }

            if (!visuals.ShowEntityLookDirections
                || !TryGetReplayEntityLook(entity, out double x, out double y, out double z,
                    out double yaw, out double pitch))
            {
                continue;
            }

            game.GlPushMatrix();
            try
            {
                game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);
                game.GlTranslate(
                    x - cameraPos.X,
                    y + playerDimension * 32768d - cameraPos.Y,
                    z - cameraPos.Z);
                game.GlRotate((float)(yaw * 180d / Math.PI + 180d), 0f, 1f, 0f);
                game.GlRotate((float)(-pitch * 180d / Math.PI), 1f, 0f, 0f);
                game.GlScale(1.5f, 1.5f, 1.5f);
                shader.ProjectionMatrix = game.CurrentProjectionMatrix;
                shader.ModelViewMatrix = game.CurrentModelViewMatrix;
                game.Platform.RenderMesh(lookDirectionMesh);
            }
            finally
            {
                game.GlPopMatrix();
            }
        }
    }

    private static bool TryGetReplayEntityLook(
        Entity entity,
        out double x,
        out double y,
        out double z,
        out double yaw,
        out double pitch)
    {
        if (entity.SelectionBox is Cuboidf box)
        {
            double height = Math.Max(0.1d, box.Y2 - box.Y1);
            double eyeY = entity.LocalEyePos.Y;
            // LocalEyePos.X/Z are mutable camera offsets in immersive first
            // person. Keep the guide on the model's centreline and only use
            // the recorded vertical eye height when it is inside the model.
            x = entity.Pos.X + (box.X1 + box.X2) * 0.5d;
            z = entity.Pos.Z + (box.Z1 + box.Z2) * 0.5d;
            y = entity.Pos.Y + box.Y1
                + (double.IsFinite(eyeY) && eyeY >= box.Y1 - 0.05d
                    && eyeY <= box.Y2 + 0.05d
                    ? eyeY - box.Y1
                    : height * 0.86d);
        }
        else
        {
            x = entity.Pos.X + entity.LocalEyePos.X;
            y = entity.Pos.Y + entity.LocalEyePos.Y;
            z = entity.Pos.Z + entity.LocalEyePos.Z;
        }

        // The guide mesh is authored along +Z. Its placement applies the
        // matching half-turn, while player pitch is reduced from the native
        // PI-centered camera angle in the resolver.
        yaw = DirectorEntityViewResolver.ResolveHeadYaw(entity);
        pitch = DirectorEntityViewResolver.ResolveHeadPitch(entity);
        return double.IsFinite(x)
            && double.IsFinite(y)
            && double.IsFinite(z)
            && double.IsFinite(yaw)
            && double.IsFinite(pitch);
    }

    private static MeshData BuildUnitBoundsMesh()
    {
        MeshData data = new(24, 24, withNormals: false, withUv: false);
        data.SetMode(EnumDrawMode.Lines);
        float[,] corners =
        {
            { 0f, 0f, 0f }, { 1f, 0f, 0f }, { 1f, 0f, 1f }, { 0f, 0f, 1f },
            { 0f, 1f, 0f }, { 1f, 1f, 0f }, { 1f, 1f, 1f }, { 0f, 1f, 1f }
        };
        int[,] edges =
        {
            { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 },
            { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 },
            { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }
        };
        for (int edge = 0; edge < edges.GetLength(0); edge++)
        {
            int from = edges[edge, 0];
            int to = edges[edge, 1];
            AddLine(
                data,
                corners[from, 0], corners[from, 1], corners[from, 2],
                corners[to, 0], corners[to, 1], corners[to, 2],
                SelectedMarkerColor);
        }
        return data;
    }

    private static MeshData BuildLookDirectionMesh()
    {
        MeshData data = new(6, 6, withNormals: false, withUv: false);
        data.SetMode(EnumDrawMode.Lines);
        const float tip = 1.5f;
        const float wing = 0.22f;
        AddLine(data, 0f, 0f, 0f, 0f, 0f, tip, CameraDirectionColor);
        AddLine(data, 0f, 0f, tip, -wing, 0f, tip - wing, CameraDirectionColor);
        AddLine(data, 0f, 0f, tip, wing, 0f, tip - wing, CameraDirectionColor);
        return data;
    }

    private enum PreviewKind
    {
        Editor,
        ReplayPath,
        ReplayCurrent
    }

    private sealed record PreviewMesh(
        MeshRef Mesh,
        int Dimension,
        double OriginX,
        double OriginY,
        double OriginZ,
        PreviewKind Kind);
}
