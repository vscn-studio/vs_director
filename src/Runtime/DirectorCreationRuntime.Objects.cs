using static VSDirector.Gui.DirectorCreationText;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using VSDirector.Replay;
using System.Numerics;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    private void UpdateParticles()
    {
        // Native effects use the game's simulation during live playback. Scrubbing,
        // replay suppression and exact-frame output use the absolute-time renderer.
        bool active = Session.EditingAnimation is null && Enabled && Session.Playing && !Exporting && Session.Project?.Visuals.Particles.ShowParticles != false
            && DirectorReplayRuntime.Manager?.ShouldSuppressLiveParticleSpawns != true;
        Renderer.NativeParticlesActive = active;
        if (!active || Session.Project is not { } project || Session.Frame is not { } frame
            || api.World.Player?.Entity.Pos.Dimension != project.Dimension)
        { previousParticleTime = -1; return; }
        double previous = previousParticleTime;
        if (previous < 0 || frame.Time < previous || frame.Time - previous > .3) previous = Math.Max(0, frame.Time - .016);
        previousParticleTime = frame.Time;
        foreach (var state in frame.Objects.Values.Where(s => s.Visible && s.Source.Kind == CreationObjectKind.Particle && s.Source.NativeParticles))
        {
            var obj = state.Source;
            double from = Math.Clamp(previous - obj.ParticleStart, 0, obj.ParticleDuration);
            double to = Math.Clamp(frame.Time - obj.ParticleStart, 0, obj.ParticleDuration);
            int count = (int)(Math.Floor(to * obj.ParticleCount) - Math.Floor(from * obj.ParticleCount));
            if (count <= 0) continue;
            var position = CreationVector.From(state.World.Translation) + project.Origin;
            bool calm = obj.ResourceCode is "smoke" or "glow" or "snow";
            var minVelocity = obj.ResourceCode switch
            {
                "smoke" => new System.Numerics.Vector3(-.125f, .3f, -.125f),
                "snow" => new System.Numerics.Vector3(-.15f, -.4f, -.15f),
                "glow" => new System.Numerics.Vector3(-.125f, 0, -.125f),
                _ => new System.Numerics.Vector3(-.8f, .8f, -.8f)
            };
            var maxVelocity = new System.Numerics.Vector3(-minVelocity.X, obj.ResourceCode switch { "smoke" => .6f, "snow" => -.2f, "glow" => .2f, _ => 1.6f }, -minVelocity.Z);
            // Transform all corners so rotated emitters keep valid component bounds.
            var lo = new System.Numerics.Vector3(float.PositiveInfinity); var hi = new System.Numerics.Vector3(float.NegativeInfinity);
            for (int i = 0; i < 8; i++)
            {
                var v = System.Numerics.Vector3.TransformNormal(new((i & 1) == 0 ? minVelocity.X : maxVelocity.X, (i & 2) == 0 ? minVelocity.Y : maxVelocity.Y, (i & 4) == 0 ? minVelocity.Z : maxVelocity.Z), state.World);
                lo = System.Numerics.Vector3.Min(lo, v); hi = System.Numerics.Vector3.Max(hi, v);
            }
            int color = ColorUtil.ToRgba((int)(obj.Opacity * 255), (int)(Math.Clamp(state.Color.X, 0, 1) * 255), (int)(Math.Clamp(state.Color.Y, 0, 1) * 255), (int)(Math.Clamp(state.Color.Z, 0, 1) * 255));
            var pos = new Vec3d(position.X, position.Y + project.Dimension * (double)BlockPos.DimensionBoundary, position.Z);
            api.World.SpawnParticles(Math.Min(256, count), color, pos, pos.Clone(), new(lo.X, lo.Y, lo.Z), new(hi.X, hi.Y, hi.Z),
                (float)obj.ParticleLife, calm ? 0 : .2f, obj.ResourceCode == "smoke" ? 2 : 1, EnumParticleModel.Quad);
        }
    }

    internal static DirectorCreationRuntime? Current { get; private set; }
    private readonly Dictionary<Entity, (bool Rendered, bool Shadow)> hiddenEntities = new();
    internal bool ShouldHideEntity(Entity entity) => Enabled && Session.Project is not null
        && !ControllingActor && (ReferenceEquals(entity, api.World.Player?.Entity) || hiddenEntities.ContainsKey(entity)
            || ActiveVisuals?.World.ShowEntities == false || entity is EntityPlayer && ActiveVisuals?.World.ShowPlayers == false);
    internal void RestoreEntities()
    {
        foreach (var entry in hiddenEntities) { entry.Key.IsRendered = entry.Value.Rendered; entry.Key.IsShadowRendered = entry.Value.Shadow; }
        hiddenEntities.Clear();
    }
    internal void HideReplacedEntities()
    {
        if (!Enabled || Session.Project is not { } project) return;
        if (ControllingActor) { RestoreEntities(); return; }
        foreach (var entity in api.World.LoadedEntities.Values)
            if (ActiveVisuals?.World.ShowEntities == false || entity is EntityPlayer && ActiveVisuals?.World.ShowPlayers == false)
            { hiddenEntities.TryAdd(entity, (entity.IsRendered, entity.IsShadowRendered)); entity.IsRendered = entity.IsShadowRendered = false; }
        if (api.World.Player?.Entity is { } local)
        {
            hiddenEntities.TryAdd(local, (local.IsRendered, local.IsShadowRendered));
            local.IsRendered = local.IsShadowRendered = false;
        }
        foreach (var obj in project.Objects.Where(o => o.Visible && o.ReplacePlayerUid.Length > 0))
        {
            var entity = api.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == obj.ReplacePlayerUid)?.Entity;
            if (entity is null) continue;
            hiddenEntities.TryAdd(entity, (entity.IsRendered, entity.IsShadowRendered));
            entity.IsRendered = entity.IsShadowRendered = false;
        }
    }
    private void FrameReady(DirectorCreationProject project, CreationFrame frame)
    {
        foreach (var state in frame.Objects.Values)
        {
            if (performanceRunning && state.Source.Id == performanceActor && (observingWorldActor ? api.World.GetEntityById(observedEntity) : api.World.Player?.Entity) is { } player)
            {
                var tr = state.Transform with { Position = new CreationVector(player.Pos.X, player.Pos.Y, player.Pos.Z) - project.Origin, Rotation = new(0, (player is EntityAgent agent ? agent.BodyYaw : player.Pos.Yaw) * GameMath.RAD2DEG, 0) };
                state.Transform = tr; state.World = tr.Matrix();
                // The live native player renders its own first-person hands/body.
                // Do not draw a second unsmoothed actor around the same camera.
                state.Visible = false;
            }
            foreach (var behavior in Extensions.Behaviors.Values) behavior.Evaluate(state, frame.Time);
            if (state.Source.ReplacePlayerUid.Length > 0 && !state.HasPerformance && !state.HasPositionAnimation)
            {
                var entity = api.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == state.Source.ReplacePlayerUid)?.Entity;
                if (entity is not null)
                {
                    state.World = state.Transform.Matrix();
                    var matrix = state.World;
                    matrix.Translation = new((float)(entity.Pos.X-project.Origin.X),(float)(entity.Pos.Y-project.Origin.Y),(float)(entity.Pos.Z-project.Origin.Z));
                    state.World = matrix;
                }
            }
            if (project.Models.Find(m => m.Id == state.Source.ModelId) is { } model && Extensions.Behaviors.Count > 0)
            { state.Bones.Clear(); DirectorCreationEvaluator.EvaluateBones(model,state); }
        }
        RefreshAttachments(frame);
    }
    private readonly Dictionary<byte[], ItemStack?> performanceItems = new(ReferenceEqualityComparer.Instance);
    private void RefreshAttachments(CreationFrame frame)
    {
        var resolved = new HashSet<Guid>();
        Matrix4x4 Resolve(CreationObjectState state)
        {
            if (!resolved.Add(state.Source.Id) || !frame.Objects.TryGetValue(state.Source.ParentId, out var parent)) return state.World;
            var parentWorld = Resolve(parent);
            var bone = parent.Bones.GetValueOrDefault(state.Source.ParentPartId, Matrix4x4.Identity);
            var local = state.Transform.Matrix();
            if (state.Source.ActorHand is 1 or 2)
            {
                bool right = state.Source.ActorHand == 1;
                ItemStack? stack = null; bool overrides = false;
                if (performanceRunning && parent.Source.Id == performanceActor)
                {
                    var entity = observingWorldActor ? api.World.GetEntityById(observedEntity) : api.World.Player?.Entity;
                    if (entity is EntityAgent agent) { stack = (right ? agent.RightHandItemSlot : agent.LeftHandItemSlot)?.Itemstack; overrides = true; }
                }
                else if (parent.PerformanceSample is { } sample)
                {
                    byte[] bytes = right ? sample.RightHandItem : sample.LeftHandItem; overrides = true;
                    if (bytes.Length > 0 && !performanceItems.TryGetValue(bytes, out stack))
                    {
                        try { using var reader = new BinaryReader(new MemoryStream(bytes, false)); stack = new ItemStack(reader, api.World); }
                        catch (Exception e) { api.Logger.Debug("VS Director: unavailable recorded hand item: {0}", e.Message); }
                        if (performanceItems.Count > 4096) performanceItems.Clear();
                        performanceItems[bytes] = stack;
                    }
                }
                if (overrides) state.ResourceOverride = stack?.Collectible?.Code?.ToString() ?? "";
                string code = state.ResourceOverride ?? state.Source.ResourceCode;
                CollectibleObject? item = stack?.Collectible;
                if (item is null && code.Length > 0) item = (CollectibleObject?)api.World.GetItem(new AssetLocation(code)) ?? api.World.GetBlock(new AssetLocation(code));
                var transform = right ? item?.TpHandTransform : item?.TpOffHandTransform ?? item?.TpHandTransform;
                if (transform is not null)
                {
                    float[] m = transform.AsMatrix;
                    local *= new Matrix4x4(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
                }
            }
            state.World = local * bone * parentWorld; state.Visible &= parent.Visible;
            return state.World;
        }
        foreach (var state in frame.Objects.Values) Resolve(state);
    }
    internal CreationVector SelectedBlock()
    {
        var pos = api.World.Player.CurrentBlockSelection?.Position ?? throw new InvalidOperationException(L("creation-point-at-a-block-first"));
        return new(pos.X,pos.InternalY,pos.Z);
    }
    internal CreationSceneObject CaptureStructure(CreationVector a, CreationVector b)
    {
        if (Session.Project is not { } project) throw new InvalidOperationException(L("creation-open-a-project-first"));
        int x0=(int)Math.Min(a.X,b.X), y0=(int)Math.Min(a.Y,b.Y), z0=(int)Math.Min(a.Z,b.Z);
        int x1=(int)Math.Max(a.X,b.X), y1=(int)Math.Max(a.Y,b.Y), z1=(int)Math.Max(a.Z,b.Z);
        if ((long)(x1-x0+1)*(y1-y0+1)*(z1-z0+1) > 4096) throw new InvalidOperationException(L("creation-a-static-structure-can-contain-at-most-4096-blocks"));
        var obj = new CreationSceneObject { Kind=CreationObjectKind.Structure, Name=L("creation-scene-structure"), Transform=new() { Position=new CreationVector(x0,y0-project.Dimension*(double)BlockPos.DimensionBoundary,z0)-project.Origin } };
        for (int y=y0;y<=y1;y++) for (int z=z0;z<=z1;z++) for (int x=x0;x<=x1;x++)
        {
            var block = api.World.BlockAccessor.GetBlock(new BlockPos(x,y,z));
            if (block.Id != 0) obj.Blocks.Add(new() { Code=block.Code.ToString(),Position=new(x-x0,y-y0,z-z0) });
        }
        Session.Edit(L("creation-capture-static-structure"),p=>p.Objects.Add(obj)); return obj;
    }
    internal void ValidateItem(string code)
    {
        var location = new AssetLocation(code);
        if (api.World.GetItem(location) is null && api.World.GetBlock(location) is null) throw new InvalidDataException(L("creation-item-or-block-not-found-340")+code);
    }
}
