using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using VSDirector;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckPerformanceEditing(Assembly assembly, ICoreClientAPI api)
    {
        string directory = Path.Combine(Path.GetTempPath(), "director-edit-take-" + Guid.NewGuid().ToString("N"));
        var io = ImGui.GetIO(); Vector2 mouse = io.MousePos;
        var bone = new CreationPart(); var model = new CreationModel { Parts = [bone] };
        var actor = new CreationSceneObject { ModelId = model.Id };
        var asset = new CreationAsset { Kind = CreationAssetKind.Performance, Source = "assets/take.json" };
        var track = new CreationTrack { PartId = bone.Id, Channel = CreationChannel.Rotation };
        DirectorCreationEdits.SetKey(track, 0, default); DirectorCreationEdits.SetKey(track, 4, new(0, 80, 0));
        var take = new CreationPerformance { Tracks = [track], Action = new() { Layers = [new() { Frames = [new() { TimeSeconds = 0 }, new() { TimeSeconds = 4 }] }] } };
        var clip = new CreationClip { ObjectId = actor.Id, PerformanceAssetId = asset.Id, Start = 2, Duration = 2, SourceStart = .5, Speed = 2 };
        var p = new DirectorCreationProject { Models = [model], Objects = [actor], Assets = [asset], Clips = [clip] }; p.Performances[asset.Id] = take;
        var session = new DirectorCreationSession(new(directory)); session.Open(p); session.Seek(2.5);
        Type R(string name) => assembly.GetType(name, true)!;
        var runtime = RuntimeHelpers.GetUninitializedObject(R("VSDirector.Runtime.DirectorCreationRuntime"));
        AccessTools.Field(runtime.GetType(), "<Session>k__BackingField").SetValue(runtime, session);
        var owner = new VSDirectorModSystem(); AccessTools.Field(owner.GetType(), "creation").SetValue(owner, runtime);
        var host = Activator.CreateInstance(R("VSDirector.Gui.DirectorImGuiWorkspace"), api)!;
        var workspace = Activator.CreateInstance(R("VSDirector.Gui.DirectorCreationWorkspace"), BindingFlags.Instance | BindingFlags.NonPublic, null, [api, owner, host], null)!;
        void Set(string name, object value) => AccessTools.Field(workspace.GetType(), name).SetValue(workspace, value);
        object? Call(string name, params object[] args) => AccessTools.Method(workspace.GetType(), name).Invoke(workspace, args);
        Set("clipId", clip.Id); Set("timelineEditor", true);
        Vector2 button = default;
        void Frame()
        {
            ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(500, 500));
            ImGui.Begin("take-conversion-probe", ImGuiWindowFlags.NoSavedSettings);
            button = ImGui.GetCursorScreenPos() + new Vector2(60, ImGui.GetFrameHeightWithSpacing() + ImGui.GetFrameHeight() / 2);
            Call("DrawTimelineSelection"); ImGui.End(); ImGui.Render();
        }
        try
        {
            Frame(); Frame(); io.MousePos = button; Frame();
            io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
            if (session.EditingAnimation is not { } animation || session.AnimationObject != actor.Id || Math.Abs(session.EditTime - 1.5) > .001)
                throw new Exception("Conversion click did not enter the actor's keys at the mapped source time");
            if ((bool)AccessTools.Field(workspace.GetType(), "timelineEditor").GetValue(workspace)!) throw new Exception("Conversion left the clip popup covering the key editor");
            if (animation.Tracks.Count != 1 || animation.Tracks[0].PartId != bone.Id) throw new Exception("Conversion did not expose bone tracks");
            animation.Tracks[0].Keys[0].Value = new(0, 30, 0);
            Call("ReturnToProject"); Set("clipId", clip.Id);
            Frame(); Frame(); io.MousePos = button; Frame(); io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
            if (p.Animations.Count != 1 || session.EditingAnimation?.Tracks[0].Keys[0].Value.Y != 30) throw new Exception("Reopening converted keys erased edits or duplicated an action");
            Call("ReturnToProject");
            if (Math.Abs(session.Time - 2.5) > .001 || session.EditingAnimation is not null) throw new Exception("Returning to the project changed its playhead");
            Console.WriteLine("PASS real conversion/edit button clicks open bone keys at clip source time, close clip editor, preserve edits and return to project");
        }
        finally { io.MouseDown[0] = false; io.MousePos = mouse; ((IDisposable)host).Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
