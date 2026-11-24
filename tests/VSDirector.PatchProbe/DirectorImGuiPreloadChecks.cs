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
        Type windowType = assembly.GetType("VSDirector.Gui.DirectorPreloadWindow", true)!;
        Type stateType = assembly.GetType("VSDirector.Gui.DirectorPreloadMapState", true)!;
        var automatic = new HashSet<DirectorReplayPreloadColumn> { new(0, 0, 0), new(1, 0, 0) };
        var included = new HashSet<DirectorReplayPreloadColumn> { new(1, 1, 0) };
        var excluded = new HashSet<DirectorReplayPreloadColumn> { new(-1, 0, 0) };
        object data = Activator.CreateInstance(stateType, 0, 0, 0, 0, 0, automatic, included, excluded, automatic)!;
        object? current = null;
        Func<object?> getState = () => current;
        Delegate state = Expression.Lambda(typeof(Func<>).MakeGenericType(stateType),
            Expression.Convert(Expression.Invoke(Expression.Constant(getState)), stateType)).Compile();
        var paints = new List<(int X1, int Z1, int X2, int Z2, bool Erase)>();
        object window = Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [api, state, (Action<int, int, int, int, bool>)((x1, z1, x2, z2, erase) => paints.Add((x1, z1, x2, z2, erase))),
                (Action<int, int>)((_, _) => { }), (Action<double>)(_ => { }), true, 3661d, 65d], null)!;
        void Call(string method, params object[] args) => AccessTools.Method(windowType, method).Invoke(window, args);
        var io = ImGui.GetIO();
        Vector2 savedDisplay = io.DisplaySize;
        float savedScale = RuntimeEnv.GUIScale;
        void Frame(bool verifyLayout = true)
        {
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(new Vector2(8));
            Call("Draw", 1f / 60);
            ImGui.Begin("###vsdirector-preload");
            // Scroll limits use the previous frame's content size. Allow one
            // layout frame after opening, changing DPI or replacing the map.
            if (verifyLayout && (ImGui.GetScrollMaxY() > 1 || ImGui.GetWindowSize().X > io.DisplaySize.X))
                throw new Exception($"Preload controls exceed the viewport or hide the close button: scale={RuntimeEnv.GUIScale}, size={ImGui.GetWindowSize()}, display={io.DisplaySize}, scroll={ImGui.GetScrollMaxY()}.");
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
                Call("Open"); Frame(false); Frame();
                capture($"preload-empty-{scale * 100:0}");
                current = data;
                Frame(false); Frame();
                capture($"preload-map-{scale * 100:0}");
                if (scale == 1)
                {
                    io.MousePos = new(200, 220); Frame(); io.MouseDown[0] = true; Frame();
                    io.MousePos += new Vector2(85, 65); Frame(); io.MouseDown[0] = false; Frame();
                    if (paints.Count != 1 || paints[0].X2 <= paints[0].X1 || paints[0].Z2 <= paints[0].Z1 || paints[0].Erase)
                        throw new Exception("Preload selection did not commit on native mouse release without a game tick");
                    Call("SetTool", true);
                    for (int i = 0; i < 24; i++) Frame();
                    io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
                    if (paints.Count != 2 || !paints[1].Erase) throw new Exception("Preload erase did not commit");
                    io.MousePos = new(-100);
                }
                Call("Close");
                ImGui.NewFrame(); Call("Draw", 1f / 60); ImGui.Render();
                Call("Open"); Frame(false); Frame();
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
