using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckPreloadWindow(Assembly assembly, ICoreClientAPI api, Action<string> capture)
    {
        Type windowType = assembly.GetType("VSDirector.Gui.DirectorPreloadRmlWindow", true)!;
        Type stateType = assembly.GetType("VSDirector.Gui.DirectorPreloadMapState", true)!;
        var automatic = new HashSet<DirectorReplayPreloadColumn> { new(0, 0, 0), new(1, 0, 0) };
        var included = new HashSet<DirectorReplayPreloadColumn> { new(1, 1, 0) };
        var excluded = new HashSet<DirectorReplayPreloadColumn> { new(-1, 0, 0) };
        object data = Activator.CreateInstance(stateType, 0, 0, 0, 0, 0, automatic, included, excluded, automatic)!;
        object? current = null;
        Func<object?> getState = () => current;
        Delegate state = Expression.Lambda(typeof(Func<>).MakeGenericType(stateType),
            Expression.Convert(Expression.Invoke(Expression.Constant(getState)), stateType)).Compile();
        object window = Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [api, state, (Action<int, int, int, int, bool>)((_, _, _, _, _) => { }),
                (Action<int, int>)((_, _) => { }), (Action<double>)(_ => { }), true, 3661d, 65d], null)!;
        void Call(string method, params object[] args) => AccessTools.Method(windowType, method).Invoke(window, args);
        var io = ImGui.GetIO();
        Vector2 savedDisplay = io.DisplaySize;
        float savedScale = RuntimeEnv.GUIScale;
        void Frame()
        {
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(new Vector2(8));
            Call("Draw", 1f / 60);
            ImGui.Begin("###vsdirector-preload");
            if (ImGui.GetScrollMaxY() > 1 || ImGui.GetWindowSize().X > io.DisplaySize.X)
                throw new Exception("Preload controls exceed the viewport or hide the close button.");
            ImGui.End();
            ImGui.Render();
            if (ImGui.GetDrawData().TotalVtxCount == 0) throw new Exception("Preload window has no draw data.");
        }
        try
        {
            io.DisplaySize = new Vector2(960, 640);
            if ((string)AccessTools.Method(windowType, "Clock").Invoke(null, [3661d])! != "01:01:01")
                throw new Exception("Preload clock formatting is incorrect.");
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                current = null;
                Call("Open"); Frame(); Frame();
                capture($"preload-empty-{scale * 100:0}");
                current = data;
                Frame(); Frame();
                capture($"preload-map-{scale * 100:0}");
                Call("Close");
                ImGui.NewFrame(); Call("Draw", 1f / 60); ImGui.Render();
                Call("Open"); Frame(); Frame();
                Call("Close");
                ImGui.NewFrame(); Call("Draw", 1f / 60); ImGui.Render();
            }
            Console.WriteLine("PASS preload clock, loading/map contents, visible footer and reopen at 100/150/200%");
        }
        finally
        {
            ((IDisposable)window).Dispose();
            io.DisplaySize = savedDisplay;
            RuntimeEnv.GUIScale = savedScale;
        }
    }
}
