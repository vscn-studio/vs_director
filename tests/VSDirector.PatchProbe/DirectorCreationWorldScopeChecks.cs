using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;
using System.Runtime.CompilerServices;
using HarmonyLib;
using System.Reflection;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckCreationWorldIsolation(Assembly assembly)
    {
        string root = Path.Combine(Path.GetTempPath(), "director-world-entry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new DirectorCreationSession(new(root));
            var current = new DirectorCreationProject { WorldId = "probe-world" };
            session.Open(current);
            var type = assembly.GetType("VSDirector.Runtime.DirectorCreationRuntime", true)!;
            var runtime = RuntimeHelpers.GetUninitializedObject(type);
            AccessTools.Field(type, "api").SetValue(runtime, CreationScopeApi());
            AccessTools.Field(type, "<Session>k__BackingField").SetValue(runtime, session);
            foreach (var target in new[] { new DirectorCreationProject { WorldId = "other-world" },
                new DirectorCreationProject { WorldId = "probe-world", Dimension = 1 }, new DirectorCreationProject() })
            {
                session.Store.Save(target); session.Store.Save(target, recovery: true);
                foreach (bool recovery in new[] { false, true })
                {
                    bool rejected = false;
                    try { AccessTools.Method(type, "Open").Invoke(runtime, [target.Id, recovery]); }
                    catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { rejected = true; }
                    if (!rejected || !ReferenceEquals(session.Project, current))
                        throw new Exception("Mismatched work/recovery replaced the active document");
                }
                bool copyRejected = false;
                try { AccessTools.Method(type, "DuplicateProject").Invoke(runtime, [target.Id, false, false]); }
                catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { copyRejected = true; }
                if (!copyRejected) throw new Exception("Ordinary duplicate bypassed world scope");
            }
            AccessTools.Method(type, "RequireProjectWorld").Invoke(runtime, [current]);
            Console.WriteLine("PASS world/dimension/legacy open and recovery guards preserve the active document; ordinary duplicate cannot bypass scope");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static ICoreClientAPI CreationScopeApi()
    {
        var entity = new EntityPlayer();
        var player = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
        var data = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
        AccessTools.Field(typeof(ClientWorldPlayerData), "entityplayer").SetValue(data, entity);
        AccessTools.Field(typeof(ClientPlayer), "worlddata").SetValue(player, data);
        var world = ProbeDispatchProxy.Create<IClientWorldAccessor>((method, _) => method.Name switch
        {
            "get_Player" => player,
            "get_SavegameIdentifier" => "probe-world",
            _ => ProbeDispatchProxy.DefaultValue(method.ReturnType)
        });
        return ProbeDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name == "get_World" ? world : ProbeDispatchProxy.DefaultValue(method.ReturnType));
    }
}
