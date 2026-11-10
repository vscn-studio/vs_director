using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using VSDirector.Data;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

internal static partial class DirectorImGuiProbe
{
    private static void CheckCreationActorVisibility(Assembly assembly)
    {
        string directory = Path.Combine(Path.GetTempPath(), "director-visibility-" + Guid.NewGuid().ToString("N"));
        Type type = assembly.GetType("VSDirector.Runtime.DirectorCreationRuntime", true)!;
        var current = AccessTools.Field(type, "<Current>k__BackingField"); var previous = current.GetValue(null);
        var runtime = RuntimeHelpers.GetUninitializedObject(type);
        var renderer = RuntimeHelpers.GetUninitializedObject(assembly.GetType("VSDirector.Rendering.DirectorCreationRenderer", true)!);
        var carrier = new EntityPlayer(); carrier.Pos.SetPos(100, 3, 100);
        var player = (Vintagestory.Client.NoObf.ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Client.NoObf.ClientPlayer));
        var playerData = (Vintagestory.Client.NoObf.ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Client.NoObf.ClientWorldPlayerData));
        playerData.PlayerUID = "Jack";
        AccessTools.Field(playerData.GetType(), "entityplayer").SetValue(playerData, carrier);
        AccessTools.Field(player.GetType(), "worlddata").SetValue(player, playerData);
        var world = ProbeDispatchProxy.Create<IClientWorldAccessor>((m, _) => m.Name switch { "get_Player" => player, "get_AllOnlinePlayers" => new IPlayer[] { player }, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType) });
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name == "get_World" ? world : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var actor = new CreationSceneObject { Kind = CreationObjectKind.Group, ReplacePlayerUid = "Jack" };
        var track = new CreationTrack { Channel = CreationChannel.Position };
        DirectorCreationEdits.SetKey(track, 0, new(1, 3, 1)); DirectorCreationEdits.SetKey(track, 2, new(9, 3, 1));
        var asset = new CreationAsset { Kind = CreationAssetKind.Performance, Source = "assets/test.performance.json" };
        var replay = Guid.NewGuid();
        var take = new CreationPerformance { BackgroundReplayId = replay, Action = new() { SourceActorKey = "player:Jack", Layers = [new() { Frames = [new() { TimeSeconds = 0 }, new() { TimeSeconds = 2 }] }] }, Tracks = [track] };
        var project = new DirectorCreationProject { Objects = [actor], Assets = [asset], BackgroundReplayId = replay, Clips = [new() { ObjectId = actor.Id, PerformanceAssetId = asset.Id, Duration = 2 }] };
        project.BackgroundClipId = project.Clips[0].Id;
        DirectorCreationEdits.SetKey(project, actor.Id, Guid.Empty, CreationChannel.Position, 0, new(1, 3, 1));
        project.Performances[asset.Id] = take;
        var session = new DirectorCreationSession(new(directory)); session.Open(project);
        AccessTools.Field(type, "<Session>k__BackingField").SetValue(runtime, session);
        AccessTools.Field(type, "<Renderer>k__BackingField").SetValue(runtime, renderer);
        AccessTools.Field(type, "<Extensions>k__BackingField").SetValue(runtime, new DirectorCreationExtensions());
        AccessTools.Field(type, "api").SetValue(runtime, api);
        AccessTools.Field(type, "hiddenEntities").SetValue(runtime, new Dictionary<Entity, (bool, bool)>());
        AccessTools.Property(renderer.GetType(), "Visible").SetValue(renderer, true);
        object? Call(string method, params object[] args) => AccessTools.Method(type, method).Invoke(runtime, args);
        session.FrameReady = (p, f) => Call("FrameReady", p, f);
        try
        {
            current.SetValue(null, runtime);
            foreach (double time in new[] { 0d, .5, 1, 1.9, .2 })
            {
                session.Seek(time);
                if (Math.Abs(session.Frame!.Objects[actor.Id].World.Translation.X - (1 + time * 4)) > .001) throw new Exception("Live appearance binding overwrote recorded root motion");
                if (!(bool)Call("ReplacesReplayActor", replay, "player:Jack")!) throw new Exception("Moving creation actor did not replace replay ghost");
            }
            session.Seek(2.1);
            if (session.Frame!.Objects[actor.Id].World.Translation.X != 9) throw new Exception("Linked background actor did not hold its final pose");
            if (!(bool)Call("ReplacesReplayActor", replay, "player:Jack")!) throw new Exception("Background ghost reappeared at clip end");
            session.Seek(project.Duration);
            if (session.Frame!.Objects[actor.Id].World.Translation.X != 9) throw new Exception("Project end reset actor position");
            project.Clips[0].Enabled = false; session.Refresh();
            if ((bool)Call("ReplacesReplayActor", replay, "player:Jack")!) throw new Exception("Disabled take still owns background actor");
            project.Clips[0].Enabled = true;
            project.Tracks[0].Enabled = false; session.Seek(1);
            if (session.Frame!.Objects[actor.Id].World.Translation.X != 5) throw new Exception("Unkeyed recorded actor lost root motion");
            var names = AccessTools.Method(assembly.GetType("VSDirector.Replay.DirectorReplayVisualEffectsPatch", true), "ShouldRenderNameTag");
            if ((bool)names.Invoke(null, [carrier])!) throw new Exception("Hidden editor player name tag still renders");
            project.Visuals.World.ShowNameTags = false;
            if ((bool)names.Invoke(null, [new EntityPlayer()])!) throw new Exception("Work name tag visibility switch ignored");
            Console.WriteLine("PASS recorded root motion overrides live appearance binding, seek/clip boundary ghost ownership and hidden player name tags");
            string? savedProject = Environment.GetEnvironmentVariable("DIRECTOR_PROBE_CREATION_PROJECT");
            if (!string.IsNullOrEmpty(savedProject))
            {
                var info = new DirectoryInfo(Path.GetDirectoryName(savedProject)!);
                var saved = System.Text.Json.JsonSerializer.Deserialize<DirectorCreationProject>(File.ReadAllText(savedProject), CreationJson.Options)!;
                saved.Validate();
                new DirectorCreationStore(info.Parent!.FullName).LoadPerformances(saved);
                var clip = saved.Clips.Single(c => c.Id == saved.BackgroundClipId);
                var takeData = saved.Performances[clip.PerformanceAssetId];
                var root = takeData.Tracks.Single(t => t.PartId == Guid.Empty && t.Channel == CreationChannel.Position);
                foreach (double t in new[] { 0d, clip.Duration * .25, clip.Duration * .75, saved.Duration })
                {
                    double source = clip.SourceStart + Math.Clamp(t - clip.Start, 0, clip.Duration) * clip.Speed;
                    var expected = DirectorCreationEvaluator.Sample(root, source, default).Float();
                    var actual = DirectorCreationEvaluator.Evaluate(saved, t).Objects[clip.ObjectId];
                    if (!actual.HasPerformance || Vector3.Distance(actual.World.Translation, expected) > .002) throw new Exception("Saved user take lost root motion or final actor ownership");
                }
                Console.WriteLine("PASS read-only saved user project: placement keys do not override recorded movement and duration padding holds final actor");
            }
        }
        finally { current.SetValue(null, previous); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
