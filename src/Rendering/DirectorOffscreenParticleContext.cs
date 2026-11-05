using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VSDirector.Rendering;

internal sealed class DirectorOffscreenParticleFrame
{
    internal IReadOnlyList<PoolSnapshot> OpaquePools { get; }

    private DirectorOffscreenParticleFrame(IReadOnlyList<PoolSnapshot> opaquePools)
    {
        OpaquePools = opaquePools;
    }

    internal static DirectorOffscreenParticleFrame CaptureEmpty()
        => new(Array.Empty<PoolSnapshot>());

    internal static DirectorOffscreenParticleFrame Capture(ClientMain game)
    {
        SystemRenderParticles? system = game.clientSystems?
            .OfType<SystemRenderParticles>()
            .FirstOrDefault();
        if (system is null
            || !DirectorOffscreenEngineBindings.TryGetParticlePools(
                system,
                out IParticlePool[] mainThread,
                out IParticlePool[] offThread))
        {
            return new DirectorOffscreenParticleFrame(Array.Empty<PoolSnapshot>());
        }

        List<PoolSnapshot> snapshots = new(2);
        CapturePool(mainThread[1], offThread: false, game.EntityPlayer.CameraPos, snapshots);
        CapturePool(offThread[1], offThread: true, game.EntityPlayer.CameraPos, snapshots);
        return new DirectorOffscreenParticleFrame(snapshots);
    }

    private static void CapturePool(
        IParticlePool source,
        bool offThread,
        Vec3d mainCameraPosition,
        ICollection<PoolSnapshot> snapshots)
    {
        if (source is not ParticlePoolQuads pool
            || source.QuantityAlive <= 0
            || !DirectorOffscreenEngineBindings.TryGetParticleBuffer(
                pool,
                offThread,
                mainCameraPosition,
                out MeshData buffer,
                out Vec3d origin)
            || buffer.CustomFloats?.Values is not { } positions
            || buffer.CustomBytes?.Values is not { } colors
            || buffer.Flags is null)
        {
            return;
        }

        int count = Math.Min(
            source.QuantityAlive,
            Math.Min(positions.Length / 4, Math.Min(colors.Length / 12, buffer.Flags.Length)));
        if (count <= 0)
        {
            return;
        }

        float[] positionCopy = new float[count * 4];
        byte[] colorCopy = new byte[count * 12];
        int[] flagCopy = new int[count];
        Array.Copy(positions, positionCopy, positionCopy.Length);
        Array.Copy(colors, colorCopy, colorCopy.Length);
        Array.Copy(buffer.Flags, flagCopy, flagCopy.Length);
        snapshots.Add(new PoolSnapshot(pool, origin, count, positionCopy, colorCopy, flagCopy));
    }

    internal sealed record PoolSnapshot(
        ParticlePoolQuads Source,
        Vec3d Origin,
        int Count,
        float[] Positions,
        byte[] Colors,
        int[] Flags);
}

internal sealed class DirectorOffscreenParticleTarget : IDisposable
{
    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly Dictionary<ParticlePoolQuads, ParticleMesh> meshes = new();
    private bool disposed;

    internal DirectorOffscreenParticleTarget(ICoreClientAPI capi, ClientMain game)
    {
        this.capi = capi;
        this.game = game;
    }

    internal void Render(
        DirectorOffscreenParticleFrame frame,
        DirectorOffscreenCameraPose pose)
    {
        if (disposed || frame.OpaquePools.Count == 0)
        {
            return;
        }

        double absoluteY = pose.Y + pose.Dimension * 32768d;
        Vec3d viewOrigin = new(pose.X, absoluteY, pose.Z);
        List<(ParticleMesh Mesh, int Count)> draws = new(frame.OpaquePools.Count);
        foreach (DirectorOffscreenParticleFrame.PoolSnapshot snapshot in frame.OpaquePools)
        {
            if (!meshes.TryGetValue(snapshot.Source, out ParticleMesh? mesh))
            {
                mesh = new ParticleMesh(capi, snapshot.Source);
                meshes.Add(snapshot.Source, mesh);
            }

            mesh.Update(snapshot, viewOrigin);
            draws.Add((mesh, snapshot.Count));
        }

        if (draws.Count == 0)
        {
            return;
        }

        IShaderProgram shader = ShaderPrograms.Particlescube;
        shader.Use();
        game.Platform.GlToggleBlend(on: true);
        game.GlMatrixModeModelView();
        game.GlPushMatrix();
        game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);
        shader.Uniform("rgbaFogIn", capi.Render.FogColor);
        shader.Uniform("rgbaAmbientIn", capi.Render.AmbientColor);
        shader.Uniform("fogMinIn", capi.Render.FogMin);
        shader.Uniform("fogDensityIn", capi.Render.FogDensity);
        shader.UniformMatrix("projectionMatrix", game.CurrentProjectionMatrix);
        shader.UniformMatrix("modelViewMatrix", game.CurrentModelViewMatrix);
        foreach ((ParticleMesh mesh, int count) in draws)
        {
            capi.Render.RenderMeshInstanced(mesh.Reference, count);
        }

        shader.Stop();
        game.GlPopMatrix();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (ParticleMesh mesh in meshes.Values)
        {
            mesh.Dispose();
        }
        meshes.Clear();
    }

    private sealed class ParticleMesh : IDisposable
    {
        private readonly ICoreClientAPI capi;
        private readonly ParticlePoolQuads source;
        private MeshRef? mesh;
        private MeshData? updateBuffer;
        private int capacity;

        internal MeshRef Reference
            => mesh ?? throw new InvalidOperationException("The particle mesh has not been allocated.");

        internal ParticleMesh(ICoreClientAPI capi, ParticlePoolQuads source)
        {
            this.capi = capi;
            this.source = source;
        }

        internal void Update(
            DirectorOffscreenParticleFrame.PoolSnapshot snapshot,
            Vec3d viewOrigin)
        {
            EnsureCapacity(snapshot.Count);
            MeshData target = updateBuffer!;
            Array.Copy(snapshot.Positions, target.CustomFloats.Values, snapshot.Positions.Length);
            Array.Copy(snapshot.Colors, target.CustomBytes.Values, snapshot.Colors.Length);
            Array.Copy(snapshot.Flags, target.Flags, snapshot.Flags.Length);
            float dx = (float)(snapshot.Origin.X - viewOrigin.X);
            float dy = (float)(snapshot.Origin.Y - viewOrigin.Y);
            float dz = (float)(snapshot.Origin.Z - viewOrigin.Z);
            for (int index = 0; index < snapshot.Count; index++)
            {
                int position = index * 4;
                target.CustomFloats.Values[position] += dx;
                target.CustomFloats.Values[position + 1] += dy;
                target.CustomFloats.Values[position + 2] += dz;
            }

            target.CustomFloats.Count = snapshot.Count * 4;
            target.CustomBytes.Count = snapshot.Count * 12;
            target.VerticesCount = snapshot.Count;
            capi.Render.UpdateMesh(Reference, target);
        }

        private void EnsureCapacity(int required)
        {
            if (mesh is not null && required <= capacity)
            {
                return;
            }

            mesh?.Dispose();
            capacity = NextPowerOfTwo(Math.Max(16, required));
            MeshData model = source.LoadModel();
            model.CustomFloats = new CustomMeshDataPartFloat
            {
                Instanced = true,
                StaticDraw = false,
                Values = new float[capacity * 4],
                InterleaveSizes = new[] { 3, 1 },
                InterleaveStride = 16,
                InterleaveOffsets = new[] { 0, 12 },
                Count = capacity * 4
            };
            model.CustomBytes = new CustomMeshDataPartByte
            {
                Conversion = DataConversion.NormalizedFloat,
                Instanced = true,
                StaticDraw = false,
                Values = new byte[capacity * 12],
                InterleaveSizes = new[] { 4, 4, 4 },
                InterleaveStride = 12,
                InterleaveOffsets = new[] { 0, 4, 8 },
                Count = capacity * 12
            };
            model.Flags = new int[capacity];
            model.FlagsInstanced = true;
            mesh = capi.Render.UploadMesh(model);
            updateBuffer = new MeshData
            {
                CustomFloats = new CustomMeshDataPartFloat
                {
                    Values = new float[capacity * 4],
                    Count = capacity * 4
                },
                CustomBytes = new CustomMeshDataPartByte
                {
                    Values = new byte[capacity * 12],
                    Count = capacity * 12
                },
                Flags = new int[capacity],
                FlagsInstanced = true
            };
        }

        public void Dispose()
        {
            mesh?.Dispose();
            mesh = null;
            updateBuffer = null;
            capacity = 0;
        }

        private static int NextPowerOfTwo(int value)
        {
            int result = 1;
            while (result < value && result < 1 << 30)
            {
                result <<= 1;
            }
            return result;
        }
    }
}
