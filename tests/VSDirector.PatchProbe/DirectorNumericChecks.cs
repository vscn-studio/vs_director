using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

internal static partial class DirectorImGuiProbe
{
    private static void CheckTransformNumberControls(Assembly assembly, Action<string> capture)
    {
        var method = AccessTools.Method(assembly.GetType("VSDirector.Gui.DirectorImGuiNumeric", true)!, "TransformDouble");
        var io = ImGui.GetIO();
        float savedScale = RuntimeEnv.GUIScale;
        Vector2 savedDisplay = io.DisplaySize;
        RuntimeEnv.GUIScale = 1;
        io.DisplaySize = new(960, 640);
        double value = 9.125;
        bool finished = false;
        Vector2 min = default, max = default;
        void Frame(Vector2 pointer, bool down)
        {
            io.MousePos = pointer; io.MouseDown[0] = down;
            ImGui.NewFrame();
            using var theme = (IDisposable)AccessTools.Method(assembly.GetType("VSDirector.Gui.DirectorImGuiTheme", true)!, "Use").Invoke(null, null)!;
            ImGui.SetNextWindowPos(new(40, 40)); ImGui.SetNextWindowSize(new(400, 180));
            ImGui.Begin("Transform number probe", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDecoration);
            ImGui.SetWindowFontScale(1);
            ImGui.SetNextItemWidth(200);
            object?[] args = ["##transform", value, 1f, 0xff6161f2u, "X 轴向枢轴", false];
            method.Invoke(null, args); value = (double)args[1]!; finished = (bool)args[5]!;
            min = ImGui.GetItemRectMin(); max = ImGui.GetItemRectMax();
            ImGui.End(); ImGui.Render();
        }
        void Idle()
        {
            // Keep independent gestures outside the native double-click interval.
            for (int i = 0; i < 24; i++) Frame(new(-100), false);
        }
        void Click(Vector2 point)
        {
            Frame(point, false); Frame(point, true); Frame(point, false);
        }
        Vector2 Center() => (min + max) / 2;
        Vector2 Arrow(int sign) => new(sign < 0 ? min.X + 2 : max.X - 2, (min.Y + max.Y) / 2);
        Frame(new(-100), false); Frame(new(-100), false);
        Click(Arrow(-1));
        if (value != 8.125 || !finished) throw new Exception($"Transform decrement arrow did not step and finish: value={value}, finished={finished}, bounds={min}/{max}.");
        Idle(); Click(Arrow(1));
        if (value != 9.125 || !finished) throw new Exception("Transform increment arrow did not step and finish.");
        Idle(); io.AddKeyEvent(ImGuiKey.LeftCtrl, true); Frame(new(-100), false); Click(Arrow(1));
        if (Math.Abs(value - 9.225) > .00001) throw new Exception("Transform Ctrl arrow ignored fine adjustment.");
        io.AddKeyEvent(ImGuiKey.LeftCtrl, false); Idle();
        io.AddKeyEvent(ImGuiKey.RightShift, true); Frame(new(-100), false); Click(Arrow(-1));
        if (Math.Abs(value + .775) > .00001) throw new Exception("Transform Shift arrow ignored coarse adjustment.");
        io.AddKeyEvent(ImGuiKey.RightShift, false); Idle();
        double Drag(int pixels, bool fromArrow = false, ImGuiKey modifier = ImGuiKey.None)
        {
            Vector2 start = fromArrow ? Arrow(1) : Center();
            Frame(start, false); Frame(start, true);
            if (modifier != ImGuiKey.None) io.AddKeyEvent(modifier, true);
            Frame(start, true);
            Vector2 target = start + new Vector2(pixels, 0);
            Frame(target, true); Frame(target, true); Frame(target, false);
            if (!finished) throw new Exception("Transform drag did not finish on release.");
            if (modifier != ImGuiKey.None) io.AddKeyEvent(modifier, false);
            double result = value; Idle(); return result;
        }
        value = 0; double ordinary = Drag(20);
        value = 0; double fine = Drag(20, modifier: ImGuiKey.LeftCtrl);
        value = 0; double coarse = Drag(20, modifier: ImGuiKey.LeftShift);
        if (ordinary <= 0 || Math.Abs(fine / ordinary - .1) > .01 || Math.Abs(coarse / ordinary - 10) > .01)
            throw new Exception("Transform drag direction or modifiers failed.");
        value = 0;
        if (Math.Abs(Drag(-20, fromArrow: true) + ordinary) > .001)
            throw new Exception("Dragging from an arrow also applied a click step.");
        value = 30000000.125;
        if (Math.Abs(Drag(20, modifier: ImGuiKey.LeftCtrl) - 30000000.125 - fine) > .001)
            throw new Exception("Transform drag lost large-coordinate precision.");
        Click(Center()); Frame(Center(), false); Frame(Center(), false);
        io.AddInputCharactersUTF8("123.375"); Frame(Center(), false);
        io.AddKeyEvent(ImGuiKey.Enter, true); Frame(Center(), false);
        if (Math.Abs(value - 123.375) > .00001 || !finished)
            throw new Exception("A single click did not enable direct transform input/commit: " + value);
        io.AddKeyEvent(ImGuiKey.Enter, false); Idle();
        // Defocusing text must also commit, with no extra arrow step.
        Click(Center()); Frame(Center(), false); Frame(Center(), false);
        io.AddInputCharactersUTF8("-2.5"); Frame(Center(), false);
        Frame(new(350, 150), true);
        if (value != -2.5 || !finished) throw new Exception("Transform text did not commit when clicking outside.");
        Frame(new(350, 150), false); Idle();
        value = 9.125; Frame(Center(), false); Frame(Center(), false);
        capture("transform-number-hover");
        RuntimeEnv.GUIScale = savedScale;
        io.DisplaySize = savedDisplay;
        Console.WriteLine("PASS transform hover arrows/click stepping, drag from value or arrows, Ctrl/Shift, large coordinates, single-click text input and Enter/outside commit");
    }

    private static void CheckLegacyNumericBinding(Assembly assembly, ICoreClientAPI api)
    {
        NativeLibrary.SetDllImportResolver(typeof(Cairo.Context).Assembly, (name, _, _) => name == "libcairo-2"
            ? NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(GamePaths.AssetsPath)!, "Lib", "libcairo-2.dll")) : 0);
        var gui = ProbeDispatchProxy.Create<IGuiAPI>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var numericApi = ProbeDispatchProxy.Create<ICoreClientAPI>((m, args) => m.Name == "get_Gui" ? gui : m.Invoke(api, args));
        Type type = assembly.GetType("VSDirector.Gui.GuiElementDirectorNumberInput", true)!;
        var control = (GuiElementNumberInput)Activator.CreateInstance(type, numericApi, ElementBounds.Fixed(0, 0, 160, 28),
            null, CairoFont.WhiteSmallText(), null)!;
        try
        {
            foreach (string name in new[] { "renderText", "textMouseDown", "recomposeText" })
                if (AccessTools.Field(type, name).GetValue(control) is not Delegate)
                    throw new Exception("Legacy numeric control failed to bind text editing: " + name);
            if (AccessTools.Field(typeof(GuiElementTextInput), "rightSpacing") is null)
                throw new Exception("Legacy numeric layout no longer supports removing stepper space.");
            Console.WriteLine("PASS legacy numeric control constructs with native text-render/edit delegates");
        }
        finally { control.Dispose(); }
    }

    private static void CheckNumericControls(Assembly assembly, Action<string> capture)
    {
        CheckTransformAxisWidths(assembly, capture);
        var method = AccessTools.Method(assembly.GetType("VSDirector.Gui.DirectorImGuiNumeric", true)!, "Double");
        var io = ImGui.GetIO();
        double value = 0, min = 0, max = 0;
        string format = "%.3f";
        Vector2 center = default;
        void Frame(Vector2 pointer, bool down)
        {
            io.MousePos = pointer;
            io.MouseDown[0] = down;
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(new(40, 40));
            ImGui.SetNextWindowSize(new(400, 130));
            ImGui.Begin("Numeric drag probe", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDecoration);
            ImGui.SetNextItemWidth(300);
            object?[] args = ["##number", value, 1f, min, max, format];
            method.Invoke(null, args);
            value = (double)args[1]!;
            center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
            ImGui.End();
            ImGui.Render();
        }
        double Drag(float pixels, ImGuiKey modifier = ImGuiKey.None)
        {
            Frame(new(-100), false);
            Frame(center, false);
            Frame(center, true);
            // Apply modifiers after activation: Ctrl+click enters text mode.
            if (modifier != ImGuiKey.None) io.AddKeyEvent(modifier, true);
            Frame(center, true);
            if (modifier != ImGuiKey.None && !ImGui.IsKeyDown(modifier))
                throw new Exception("Numeric test modifier event was not delivered: " + modifier);
            Vector2 target = center + new Vector2(pixels, 0);
            Frame(target, true);
            Frame(target, true);
            Frame(target, false);
            if (modifier != ImGuiKey.None) io.AddKeyEvent(modifier, false);
            Frame(new(-100), false);
            // Keep separate gestures outside ImGui's double-click interval.
            for (int i = 0; i < 24; i++) Frame(new(-100), false);
            return value;
        }
        Frame(new(-100), false); Frame(new(-100), false);
        double ordinary = Drag(20);
        if (ordinary <= 0 || Drag(-20) >= ordinary) throw new Exception("Numeric drag direction is reversed or unresponsive.");
        value = 0;
        double fine = Drag(20, ImGuiKey.LeftCtrl);
        value = 0;
        double coarse = Drag(20, ImGuiKey.LeftShift);
        if (Math.Abs(fine / ordinary - .1) > .01 || Math.Abs(coarse / ordinary - 10) > .01)
            throw new Exception($"Numeric drag modifiers failed: normal={ordinary}, Ctrl={fine}, Shift={coarse}.");
        io.AddKeyEvent(ImGuiKey.ModShift, true);
        value = 0;
        double nativeCoarse = Drag(20, ImGuiKey.RightShift);
        io.AddKeyEvent(ImGuiKey.ModShift, false);
        Frame(new(-100), false);
        if (Math.Abs(nativeCoarse - coarse) > .001) throw new Exception("Native Shift multiplier was applied twice.");
        value = 30000000.125;
        double precise = Drag(20, ImGuiKey.LeftCtrl);
        if (Math.Abs(precise - 30000000.125 - fine) > .001)
            throw new Exception("Numeric drag lost precision at large world coordinates.");
        min = -2; max = 2; value = 0;
        if (Drag(100) != 2 || Drag(-100) != -2) throw new Exception("Numeric drag ignored bounds.");
        min = max = 0;
        format = "X %.3f";
        io.AddKeyEvent(ImGuiKey.LeftCtrl, true);
        Frame(center, false); Frame(center, true); Frame(center, false);
        io.AddKeyEvent(ImGuiKey.LeftCtrl, false);
        io.AddInputCharactersUTF8("123.375");
        Frame(center, false);
        io.AddKeyEvent(ImGuiKey.Enter, true);
        Frame(center, false);
        io.AddKeyEvent(ImGuiKey.Enter, false);
        Frame(new(-100), false);
        if (Math.Abs(value - 123.375) > .0001) throw new Exception("Numeric direct input did not commit: " + value);
        Console.WriteLine("PASS numeric drag direction, Ctrl/Shift multipliers, double precision, bounds and direct text entry");
    }

    private static void CheckTransformAxisWidths(Assembly assembly, Action<string> capture)
    {
        var row = AccessTools.Method(assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud", true)!, "DrawCameraTransformAxes");
        var number = AccessTools.Method(assembly.GetType("VSDirector.Gui.DirectorImGuiNumeric", true)!, "TransformDouble");
        var io = ImGui.GetIO();
        Vector2 display = io.DisplaySize;
        float savedScale = RuntimeEnv.GUIScale;
        try
        {
            io.DisplaySize = new(960, 640);
            io.MousePos = new(-100);
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            foreach (float width in new[] { 240f, 320f, 520f })
            for (int frame = 0; frame < 8; frame++)
            {
                RuntimeEnv.GUIScale = scale;
                ImGui.NewFrame();
                using var theme = (IDisposable)AccessTools.Method(assembly.GetType("VSDirector.Gui.DirectorImGuiTheme", true)!, "Use").Invoke(null, null)!;
                ImGui.SetNextWindowPos(new(40, 40));
                ImGui.SetNextWindowSize(new(width * scale, 300 * scale));
                ImGui.Begin("Transform axes probe", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDecoration);
                ImGui.SetWindowFontScale(scale);
                float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
                foreach (string label in new[] { "位置", "旋转", "枢轴点" })
                {
                    var bounds = new (Vector2 Min, Vector2 Max)[3];
                    ImGui.PushID(label);
                    row.Invoke(null, [label, ImGui.CalcTextSize("枢轴点").X, (Action<int>)(axis =>
                    {
                        ImGui.PushID(axis);
                        number.Invoke(null, ["##value", axis == 2 ? -30000000.125 : 0d, 1f, 0xff6161f2u, "轴", false]);
                        bounds[axis] = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
                        ImGui.PopID();
                    })]);
                    ImGui.PopID();
                    float[] widths = bounds.Select(b => b.Max.X - b.Min.X).ToArray();
                    if (widths.Min() <= 0 || widths.Max() - widths.Min() > 2
                        || bounds.Any(b => b.Max.X > right + 1)
                        || bounds.Skip(1).Where((b, i) => b.Min.X - bounds[i].Max.X < 6 * scale - 1 || Math.Abs(b.Min.Y - bounds[i].Min.Y) > 1).Any())
                        throw new Exception($"Transform XYZ inputs lack spacing, wrap or have unequal widths at {width}px / {scale}: {string.Join(", ", widths)}");
                }
                ImGui.End();
                ImGui.Render();
                if (width == 320 && scale == 1 && frame == 7) capture("transform-axis-spacing");
            }
            Console.WriteLine("PASS transform XYZ equal widths, one row and panel bounds at 240/320/520px and 100/150/200%, including long coordinates");
        }
        finally { io.DisplaySize = display; RuntimeEnv.GUIScale = savedScale; }
    }
}
