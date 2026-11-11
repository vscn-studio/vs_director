using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VSDirector;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckCreationExport(Assembly assembly)
    {
        string root = Path.Combine(Path.GetTempPath(), "vsdirector-export-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var camera = new CreationSceneObject { Kind = CreationObjectKind.Camera };
            var model = new CreationModel { Name = "Actor skin", Parts = [new()], Textures = new() { ["all"] = "assets/skin.png" } };
            var actor = new CreationSceneObject { ModelId = model.Id };
            var unused = new CreationAsset { Name = "Unused", Kind = CreationAssetKind.Performance, Source = "assets/missing.json" };
            var project = new DirectorCreationProject { CameraId = camera.Id, Objects = [camera, actor], Models = [model], Assets = [unused] };
            var session = new DirectorCreationSession(new(root)); session.Open(project);
            var type = assembly.GetType("VSDirector.Runtime.DirectorCreationRuntime", true)!;
            var runtime = RuntimeHelpers.GetUninitializedObject(type);
            var owner = new VSDirectorModSystem();
            var world = ProbeDispatchProxy.Create<IClientWorldAccessor>((m, _) => m.Name == "get_SavegameIdentifier" ? "current-world" : ProbeDispatchProxy.DefaultValue(m.ReturnType));
            var assets = ProbeDispatchProxy.Create<IAssetManager>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
            var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch { "get_World" => world, "get_Assets" => assets, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType) });
            void Set(string field, object value) => AccessTools.Field(type, field).SetValue(runtime, value);
            object? Call(string method) => AccessTools.Method(type, method).Invoke(runtime, null);
            Set("<Session>k__BackingField", session); Set("owner", owner); Set("api", api);
            IReadOnlyList<string> Check() => (IReadOnlyList<string>)Call("CheckExportResources")!;
            var issues = Check();
            if (issues.Count != 1 || !issues[0].Contains("Actor skin")) throw new Exception("Preflight did not identify the used texture, or checked unused recordings");
            string texture = session.Store.AssetPath(project.Id, "assets/skin.png");
            Directory.CreateDirectory(Path.GetDirectoryName(texture)!); File.WriteAllBytes(texture, []);
            if (Check().Count != 0) throw new Exception("Real-world export still requires a background or unused imports");
            model.Textures["all"] = "game:missing/skin";
            if (Check().Count != 1) throw new Exception("Missing game texture was omitted from preflight");
            model.Textures["all"] = "assets/skin.png"; project.WorldId = "other-world";
            if (Check().Count != 1) throw new Exception("Wrong-world export was accepted");
            project.WorldId = ""; project.CameraId = Guid.Empty;
            if (Check().Count != 1) throw new Exception("Missing output camera was accepted");
            project.CameraId = camera.Id; var background = Guid.NewGuid(); project.SetBackground(background);
            void Prepare()
            {
                Set("pendingExport", Path.Combine(root, "never-start.mp4")); Set("exportBackground", background);
                Set("exportGeneration", session.DocumentGeneration); Set("exportPrepareStarted", Environment.TickCount64);
            }
            bool Exporting() => (bool)AccessTools.Property(type, "Exporting").GetValue(runtime)!;
            Prepare(); Call("TickExportPreparation");
            if (!Exporting() || AccessTools.Field(type, "fileSink").GetValue(runtime) is not null) throw new Exception("Export started before the background was ready");
            Call("CancelExport"); if (Exporting()) throw new Exception("Preparing export could not be cancelled");
            Prepare(); Set("exportGeneration", session.DocumentGeneration - 1); Call("TickExportPreparation");
            if (Exporting()) throw new Exception("Pending export survived a document change");
            Prepare(); Set("exportPrepareStarted", Environment.TickCount64 - 60001);
            bool timedOut = false;
            try { Call("TickExportPreparation"); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { timedOut = true; }
            Call("CancelExport");
            if (!timedOut || File.Exists(Path.Combine(root, "never-start.mp4"))) throw new Exception("Export readiness did not time out before creating a video");
            Console.WriteLine("PASS creation export preflight: current world, used/missing local and game textures, camera/world checks, async preparation, cancellation, document change and timeout");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
