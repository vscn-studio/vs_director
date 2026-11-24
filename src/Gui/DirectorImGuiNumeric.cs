using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace VSDirector.Gui;

internal static class DirectorImGuiNumeric
{
    private static uint stepPointerId;
    private static int stepPointerRegion;
    private static bool stepDragged, stepCommitted;
    internal static bool FinishedEditing => stepCommitted || ImGui.IsItemDeactivatedAfterEdit();
    private static uint transformPointerId, transformTextId;
    private static int transformPointerRegion, transformTextFrame;
    private static bool transformDragged, transformTextFocus;

    internal static unsafe bool TransformDouble(string id, ref double value, float step,
        uint axisColor, string tooltip, out bool finished)
    {
        uint widget = ImGui.GetID(id);
        int frame = ImGui.GetFrameCount();
        if (transformTextId != 0 && frame - transformTextFrame > 1) transformTextId = 0;
        finished = false;
        bool changed;
        double edited = value;
        if (transformTextId == widget)
        {
            transformTextFrame = frame;
            if (transformTextFocus) ImGui.SetKeyboardFocusHere();
            transformTextFocus = false;
            changed = ImGui.InputDouble(id, ref edited, 0, 0, "%.3f", ImGuiInputTextFlags.AutoSelectAll);
            finished = ImGui.IsItemDeactivated();
            if (finished) transformTextId = 0;
        }
        else
        {
            var io = ImGui.GetIO();
            bool control = io.KeyCtrl || ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl);
            bool shift = io.KeyShift || ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
            double bound = 0;
            // Keep native drag capture and precision, but draw the compact value/chevrons ourselves.
            ImGui.PushStyleColor(ImGuiCol.Text, 0u);
            try
            {
                changed = ImGui.DragScalar(id, ImGuiDataType.Double, (nint)(&edited),
                    DragSpeed(step, control, shift, io.KeyShift, io.KeyAlt), (nint)(&bound), (nint)(&bound),
                    "%.3f", ImGuiSliderFlags.NoInput);
            }
            finally { ImGui.PopStyleColor(); }
            finished = ImGui.IsItemDeactivatedAfterEdit();
            Vector2 min = ImGui.GetItemRectMin(), max = ImGui.GetItemRectMax();
            bool hovered = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
            float arrowWidth = Math.Min(16 * DirectorImGuiTheme.Scale, (max.X - min.X) * .22f);
            int Region() => io.MousePos.X < min.X + arrowWidth ? -1 : io.MousePos.X >= max.X - arrowWidth ? 1 : 0;
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                transformPointerId = widget;
                transformPointerRegion = Region();
                transformDragged = false;
            }
            if (transformPointerId == widget)
            {
                transformDragged |= ImGui.IsMouseDragging(ImGuiMouseButton.Left);
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    if (!transformDragged && hovered && Region() == transformPointerRegion)
                    {
                        if (transformPointerRegion != 0)
                        {
                            edited += transformPointerRegion * (double)step * (control ? .1 : 1) * (shift ? 10 : 1);
                            changed = finished = true;
                        }
                        else
                        {
                            transformTextId = widget;
                            transformTextFrame = frame;
                            transformTextFocus = true;
                        }
                    }
                    if (transformDragged) finished = true;
                    transformPointerId = 0;
                }
            }
            var draw = ImGui.GetWindowDrawList();
            float scale = DirectorImGuiTheme.Scale;
            uint textColor = ImGui.GetColorU32(ImGuiCol.Text);
            string full = edited.ToString("0.###", CultureInfo.InvariantCulture), text = full;
            float fontSize = ImGui.GetFontSize() * .9f;
            var font = ImGui.GetFont();
            float available = Math.Max(0, max.X - min.X - 2 * arrowWidth - 4 * scale);
            Vector2 Measure(string s) => s.Length == 0 ? Vector2.Zero : ImGui.CalcTextSize(s) * .9f;
            while (text.Length > 0 && Measure(text).X > available)
            {
                string prefix = text.EndsWith('…') ? text[..^1] : text;
                text = prefix.Length > 1 ? prefix[..^1] + "…" : "";
            }
            draw.PushClipRect(min, max, true);
            if (text.Length > 0) draw.AddText(font, fontSize, (min + max - Measure(text)) / 2, textColor, text);
            float corner = Math.Min(7 * scale, (max.Y - min.Y) * .25f);
            draw.AddTriangleFilled(new(max.X - corner, min.Y), new(max.X, min.Y), new(max.X, min.Y + corner),
                ImGui.GetColorU32(axisColor));
            if (hovered || active)
            {
                for (int direction = -1; direction <= 1; direction += 2)
                {
                    float x = direction < 0 ? min.X + arrowWidth / 2 : max.X - arrowWidth / 2;
                    float y = (min.Y + max.Y) / 2, half = Math.Min(4 * scale, arrowWidth * .28f);
                    uint color = hovered && Region() == direction ? ImGui.GetColorU32(ImGuiCol.CheckMark) : textColor;
                    draw.AddLine(new(x - direction * half / 2, y - half), new(x + direction * half / 2, y), color, 1.5f * scale);
                    draw.AddLine(new(x + direction * half / 2, y), new(x - direction * half / 2, y + half), color, 1.5f * scale);
                }
            }
            draw.PopClipRect();
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
                if (!active && text != full) DirectorImGuiTooltip.Show(full);
            }
        }
        if (changed && double.IsFinite(edited)) value = edited;
        else changed = false;
        return changed;
    }

    // ImGui applies Alt/Shift fine/coarse adjustment. Normalize Alt so Ctrl
    // is the fine modifier throughout the workbench, keeping Shift coarse.
    internal static float DragSpeed(float step, bool control, bool shift, bool nativeShift, bool nativeAlt)
        => step / (10 * Math.Max(.5f, DirectorImGuiTheme.Scale)) * (control ? .1f : 1) * (shift ? 10 : 1)
            / (nativeShift ? 10 : 1) / (nativeAlt ? .01f : 1);

    internal static unsafe bool Double(string id, ref double value, float step = 1,
        double min = 0, double max = 0, string format = "%.3f")
    {
        var io = ImGui.GetIO();
        bool control = io.KeyCtrl || ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl);
        bool shift = io.KeyShift || ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
        double edited = value;
        bool changed, nativeControl = io.KeyCtrl;
        stepCommitted = false;
        uint widget = ImGui.GetID(id);
        Vector2 start = ImGui.GetCursorScreenPos();
        float width = ImGui.CalcItemWidth();
        float edge = Math.Min(18 * DirectorImGuiTheme.Scale, width * .22f);
        bool pointerOnEdge = io.MousePos.X < start.X + edge || io.MousePos.X >= start.X + width - edge;
        // Some backends deliver physical modifier keys without aggregate ModCtrl.
        // Normalize just for this widget so Ctrl+click also enters text mode.
        io.KeyCtrl = control;
        try
        {
            changed = ImGui.DragScalar(id, ImGuiDataType.Double, (nint)(&edited),
                DragSpeed(step, control, shift, io.KeyShift, io.KeyAlt), (nint)(&min), (nint)(&max),
                format, ImGuiSliderFlags.AlwaysClamp | (pointerOnEdge && !io.WantTextInput
                    && ImGui.IsMouseClicked(ImGuiMouseButton.Left) ? ImGuiSliderFlags.NoInput : ImGuiSliderFlags.None));
        }
        finally { io.KeyCtrl = nativeControl; }
        Vector2 lo = ImGui.GetItemRectMin(), hi = ImGui.GetItemRectMax();
        bool hovered = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
        int Region() => io.MousePos.X < lo.X + edge ? -1 : io.MousePos.X >= hi.X - edge ? 1 : 0;
        if (hovered && !(active && io.WantTextInput) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            stepPointerId = widget;
            stepPointerRegion = Region();
            stepDragged = false;
        }
        if (stepPointerId == widget)
        {
            stepDragged |= ImGui.IsMouseDragging(ImGuiMouseButton.Left);
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                if (!stepDragged && hovered && stepPointerRegion != 0 && Region() == stepPointerRegion)
                {
                    edited += stepPointerRegion * (double)step * (control ? .1 : 1) * (shift ? 10 : 1);
                    if (max > min) edited = Math.Clamp(edited, min, max);
                    changed = stepCommitted = edited != value && double.IsFinite(edited);
                }
                stepPointerId = 0;
            }
        }
        if ((hovered || active) && !(active && io.WantTextInput))
        {
            var draw = ImGui.GetWindowDrawList();
            float scale = DirectorImGuiTheme.Scale;
            draw.PushClipRect(lo, hi, true);
            for (int direction = -1; direction <= 1; direction += 2)
            {
                float x = direction < 0 ? lo.X + edge / 2 : hi.X - edge / 2;
                float y = (lo.Y + hi.Y) / 2, half = Math.Min(4 * scale, edge * .28f);
                uint bg = ImGui.GetColorU32(active ? ImGuiCol.FrameBgActive : ImGuiCol.FrameBgHovered);
                draw.AddRectFilled(new(x - edge / 2, lo.Y), new(x + edge / 2, hi.Y), bg);
                uint color = ImGui.GetColorU32(hovered && Region() == direction ? ImGuiCol.CheckMark : ImGuiCol.Text);
                draw.AddLine(new(x - direction * half / 2, y - half), new(x + direction * half / 2, y), color, 1.5f * scale);
                draw.AddLine(new(x + direction * half / 2, y), new(x - direction * half / 2, y + half), color, 1.5f * scale);
            }
            draw.PopClipRect();
        }
        if (changed && double.IsFinite(edited)) value = edited;
        else changed = false;
        if (hovered) ImGui.SetMouseCursor(Region() == 0 ? ImGuiMouseCursor.ResizeEW : ImGuiMouseCursor.Hand);
        return changed;
    }
}
