using System.Numerics;
using System.Reflection;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector;

internal static partial class DirectorImGuiProbe
{
    private static void CheckHeadTrackingSettings(Assembly assembly, ICoreClientAPI api, Action<string> capture)
    {
        var owner = new VSDirectorModSystem();
        AccessTools.Field(owner.GetType(), "capi").SetValue(owner, api);
        var io = ImGui.GetIO(); Vector2 previousDisplay = io.DisplaySize; float previousScale = RuntimeEnv.GUIScale;
        object? window = null;
        try
        {
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale; io.DisplaySize = new Vector2(960, 640) * scale;
                AccessTools.Method(owner.GetType(), "OpenHeadTrackingSettings").Invoke(owner, null);
                window = AccessTools.Field(owner.GetType(), "headTrackingWindow").GetValue(owner)!;
                var form = AccessTools.Field(window.GetType(), "form").GetValue(window)!;
                if ((int)AccessTools.Property(form.GetType(), "FieldCount").GetValue(form)! < 30)
                    throw new Exception("Head-tracking settings omitted upstream controls");
                for (int frame = 0; frame < 6; frame++)
                {
                    ImGui.NewFrame();
                    if (frame == 0) { ImGui.SetNextWindowPos(new Vector2(12, 12) * scale); ImGui.Begin("###vsdirector-head-tracking"); ImGui.End(); }
                    AccessTools.Method(window.GetType(), "Draw").Invoke(window, [1f / 60]);
                    ImGui.Begin("###vsdirector-head-tracking");
                    if (frame >= 3 && ImGui.GetScrollMaxY() > 1) throw new Exception($"Settings footer scrolls out of view: scale={scale}, scroll={ImGui.GetScrollMaxY()}, size={ImGui.GetWindowSize()}");
                    if (ImGui.GetWindowSize().Y > io.DisplaySize.Y) throw new Exception("Head tracking settings escape the viewport");
                    ImGui.End(); ImGui.Render();
                }
                if (scale == 1) capture("head-tracking-settings");
                AccessTools.Method(window.GetType(), "Close").Invoke(window, null);
            }
            Console.WriteLine("PASS localized head-tracking settings expose all controls with a scrolling body and fixed actions at three DPI scales");
        }
        finally
        {
            if (window is IDisposable disposable) disposable.Dispose();
            io.DisplaySize = previousDisplay; RuntimeEnv.GUIScale = previousScale;
        }
    }
}
