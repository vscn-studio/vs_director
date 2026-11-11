using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Rendering;

internal sealed partial class DirectorCreationRenderer
{
    private IShaderProgram? selectionMaskShader, selectionOutlineShader;
    private FrameBufferRef? selectionMask, selectionOutline;
    private MeshRef? selectionTriangle;
    internal IReadOnlyCollection<Guid> AdditionalSelectedParts { get; set; } = System.Array.Empty<Guid>();

    // Submitted as a clipped ImGui image rather than a world post-process:
    // editor highlights stay behind dialogs and never enter previews/exports.
    internal int PoseSelectionTexture(CreationModel model, CreationObjectState state,
        Guid selected, Guid hovered, float[] view, float[] projection, Vector2 size, float scale)
    {
        if (api is null || !Visible || !state.Visible || state.Source.Opacity < .01
            || session.Project is not { } project || selected == Guid.Empty && hovered == Guid.Empty) return 0;
        if (!project.Visuals.World.ShowEntities || !project.Visuals.World.ShowPlayers
            && state.Source.ActorSource?.EntityCode.Contains("player", StringComparison.OrdinalIgnoreCase) == true) return 0;
        int width = Math.Clamp((int)MathF.Ceiling(size.X), 1, 8192), height = Math.Clamp((int)MathF.Ceiling(size.Y), 1, 8192);
        var previousShader = api.Render.CurrentActiveShader;
        var previousBuffer = game.Platform.CurrentFrameBuffer;
        using var saved = new DirectorCreationRenderState();
        using var targetState = new DirectorCreationSelectionState();
        try
        {
            previousShader?.Stop();
            EnsureSelectionResources(width, height);
            targetState.Begin(selectionMask!);
            selectionMaskShader!.Use();
            selectionMaskShader.UniformMatrix("viewMatrix", view);
            selectionMaskShader.UniformMatrix("projectionMatrix", projection);
            var selectedParts = SelectionBranch(model, selected);
            foreach(var id in AdditionalSelectedParts??System.Array.Empty<Guid>())selectedParts.UnionWith(SelectionBranch(model,id));
            var hoveredParts = SelectionBranch(model, hovered == selected ? Guid.Empty : hovered);
            var deformations = DirectorCreationJointSeams.Deformations(model, state);
            foreach (var part in model.Parts.Where(p => !p.Group && p.Visible && !state.HiddenParts.Contains(p.Id)))
            {
                bool isSelected = selectedParts.Contains(part.Id), isHovered = hoveredParts.Contains(part.Id);
                if ((!isSelected && !isHovered) || !state.Bones.TryGetValue(part.Id, out var bone)) continue;
                selectionMaskShader.Uniform("channels", isSelected ? 1f : 0f, isHovered ? 1f : 0f);
                selectionMaskShader.UniformMatrix("modelMatrix", Array(bone * state.World));
                foreach (var face in part.Faces)
                {
                    if (face.Value is not JsonObject f || f["enabled"]?.ToString() == "false") continue;
                    var mesh = ModelFaceMesh(model, part, face.Key, f, state.Source.Id, deformations, revision != session.Revision);
                    string texture = DirectorCreationShape.S(f, "texture", "#all").TrimStart('#');
                    BindTexture(project, model.Textures.GetValueOrDefault(texture, ""), selectionMaskShader);
                    game.Platform.RenderMesh(mesh);
                }
            }
            selectionMaskShader.Stop();
            targetState.Begin(selectionOutline!, union: false);
            selectionOutlineShader!.Use();
            selectionOutlineShader.BindTexture2D("maskTex", selectionMask!.ColorTextureIds[0], 0);
            selectionOutlineShader.Uniform("radius", Math.Clamp((int)MathF.Ceiling(2 * scale), 1, 8));
            selectionOutlineShader.Uniform("selectedColor", 1f, .82f, .44f, 1f);
            selectionOutlineShader.Uniform("hoveredColor", .5f, .82f, .94f, 1f);
            game.Platform.RenderMesh(selectionTriangle!);
            return selectionOutline!.ColorTextureIds[0];
        }
        finally
        {
            selectionMaskShader?.Stop(); selectionOutlineShader?.Stop();
            // Restore both the engine's framebuffer bookkeeping and the precise
            // GL read/draw bindings (which can differ during viewport blits).
            game.Platform.CurrentFrameBuffer = previousBuffer;
            previousShader?.Use();
        }
    }

    private static HashSet<Guid> SelectionBranch(CreationModel model, Guid root)
    {
        var result = new HashSet<Guid>();
        if (root == Guid.Empty || !model.Parts.Any(p => p.Id == root)) return result;
        result.Add(root);
        bool added;
        do
        {
            added = false;
            foreach (var part in model.Parts.Where(p => result.Contains(p.ParentId))) added |= result.Add(part.Id);
        } while (added);
        return result;
    }

    private MeshRef ModelFaceMesh(CreationModel model, CreationPart part, string side, JsonObject face, Guid objectId,
        Dictionary<Guid, Dictionary<Vector3, Vector3>> deformations, bool geometryChanged)
    {
        bool seam = model.RigEditing.Seams.Any(s => s.Parent == part.Id || s.Child == part.Id);
        string key = model.Id + "/" + part.Id + "/" + side + (seam ? "/seam/" + objectId : "");
        if (!meshes.TryGetValue(key, out var mesh) || geometryChanged)
            mesh = Geometry(key, JsonSerializer.Serialize(new { Size = part.To - part.From, part.Inflate, model.TextureWidth,
                model.TextureHeight, Face = face, Sizes = model.Extra["textureSizes"] }), () => CubeFace(part, side, face, model));
        if (seam)
        {
            var data = CubeFace(part, side, face, model);
            if (deformations.TryGetValue(part.Id, out var vertices))
                for (int i = 0; i < data.VerticesCount; i++)
                {
                    var vertex = new Vector3(data.xyz[i * 3], data.xyz[i * 3 + 1], data.xyz[i * 3 + 2]);
                    if (vertices.TryGetValue(vertex, out var replacement))
                    { data.xyz[i * 3] = replacement.X; data.xyz[i * 3 + 1] = replacement.Y; data.xyz[i * 3 + 2] = replacement.Z; }
                }
            api.Render.UpdateMesh(mesh, data);
        }
        return mesh;
    }

    private void EnsureSelectionResources(int width, int height)
    {
        selectionMaskShader ??= CreateSelectionShader("creationselectionmask");
        selectionOutlineShader ??= CreateSelectionShader("creationselectionoutline");
        if (selectionTriangle is null)
        {
            var data = new MeshData(3, 3, withNormals: false, withUv: false);
            data.SetMode(EnumDrawMode.Triangles);
            data.AddVertexSkipTex(-1, -1, 0, ColorUtil.WhiteArgb);
            data.AddVertexSkipTex(3, -1, 0, ColorUtil.WhiteArgb);
            data.AddVertexSkipTex(-1, 3, 0, ColorUtil.WhiteArgb);
            data.AddIndex(0); data.AddIndex(1); data.AddIndex(2);
            selectionTriangle = api.Render.UploadMesh(data);
        }
        if (selectionMask is { Disposed: false } && selectionOutline is { Disposed: false }
            && selectionMask.Width == width && selectionMask.Height == height) return;
        DisposeSelectionBuffers();
        selectionMask = CreateSelectionBuffer("VSDirector-PoseMask", width, height);
        selectionOutline = CreateSelectionBuffer("VSDirector-PoseOutline", width, height);
    }

    private IShaderProgram CreateSelectionShader(string name)
    {
        var program = api.Shader.NewShaderProgram(); program.AssetDomain = "vsdirector";
        program.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader);
        program.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader);
        api.Shader.RegisterFileShaderProgram(name, program);
        if (program.Compile()) return program;
        program.Dispose(); throw new InvalidOperationException("Pose selection shader compilation failed: " + name);
    }

    private FrameBufferRef CreateSelectionBuffer(string name, int width, int height)
        => game.Platform.CreateFramebuffer(new FramebufferAttrs(name, width, height)
        {
            Attachments = [new FramebufferAttrsAttachment
            {
                AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                Texture = new RawTexture
                {
                    Width = width, Height = height,
                    PixelInternalFormat = EnumTextureInternalFormat.Rgba8, PixelFormat = EnumTexturePixelFormat.Rgba,
                    MinFilter = EnumTextureFilter.Nearest, MagFilter = EnumTextureFilter.Nearest
                }
            }]
        });

    private void DisposeSelectionBuffers()
    {
        if (selectionMask is { Disposed: false }) game.Platform.DisposeFrameBuffer(selectionMask, disposeTextures: true);
        if (selectionOutline is { Disposed: false }) game.Platform.DisposeFrameBuffer(selectionOutline, disposeTextures: true);
        selectionMask = selectionOutline = null;
    }

    private void DisposeSelectionShaders()
    {
        selectionMaskShader?.Dispose(); selectionOutlineShader?.Dispose();
        selectionMaskShader = selectionOutlineShader = null;
    }
}
