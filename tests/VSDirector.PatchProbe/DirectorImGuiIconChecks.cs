using System.Numerics;
using ImGuiNET;

internal static partial class DirectorImGuiProbe
{
    private static unsafe void CheckIconQuads(ImFontPtr font, Dictionary<string, string> icons, int atlasWidth, int atlasHeight)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(1920, 1080);
        io.DeltaTime = 1f / 60;
        io.NativePtr->IniFilename = null;
        ImGui.NewFrame();
        ImGui.PushFont(font);
        io.Fonts.GetTexDataAsAlpha8(out nint tex, out int tw, out int th);
        var draw = ImGui.GetBackgroundDrawList();
        // Inspect what native RenderText actually emits. ImGui.NET's glyph fields
        // cannot validate this: its bitfield layout differs from bundled cimgui.
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        foreach (var icon in icons)
        {
            int first = draw.VtxBuffer.Size;
            float size = font.FontSize * scale;
            Vector2 origin = new(100, 100);
            draw.AddText(font, size, origin, uint.MaxValue, icon.Value);
            if (draw.VtxBuffer.Size != first + 4)
                throw new Exception("Icon did not render a complete quad: " + icon.Key);
            var top = draw.VtxBuffer[first];
            var bottom = draw.VtxBuffer[first + 2];
            Vector2 uvSize = bottom.uv - top.uv;
            if (!float.IsFinite(top.uv.X + top.uv.Y + bottom.uv.X + bottom.uv.Y)
                || top.uv.X < 0 || top.uv.Y < 0 || bottom.uv.X > 1 || bottom.uv.Y > 1
                || uvSize.X <= 0 || uvSize.Y <= 0)
                throw new Exception($"Icon texture coordinates are corrupt: {icon.Key}: {top.uv} to {bottom.uv}");
            Vector2 inkSize = (bottom.pos - top.pos) / scale;
            if (Math.Abs(inkSize.X - uvSize.X * atlasWidth) > .01f
                || Math.Abs(inkSize.Y - uvSize.Y * atlasHeight) > .01f)
                throw new Exception("Icon quad no longer matches its atlas pixels: " + icon.Key);
            float advance = ImGui.CalcTextSize(icon.Value).X * scale;
            float center = VisibleCenter(top, bottom, tex, tw, th);
            if (Math.Abs(center - origin.X - advance / 2) > .01f)
                throw new Exception("Rendered icon ink is not centered: " + icon.Key);
        }
        ImGui.PopFont();
        ImGui.Render();
        // Verify actual Button text placement as well as direct draw-list rendering.
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        {
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(Vector2.Zero);
            ImGui.SetNextWindowSize(io.DisplaySize);
            ImGui.Begin("icon-button-centering", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
            ImGui.PushFont(font);
            ImGui.SetWindowFontScale(scale);
            int index = 0;
            foreach (var icon in icons)
            {
                ImGui.SetCursorPos(new Vector2(8 + index % 12 * 100, 8 + index / 12 * 60));
                index++;
                var list = ImGui.GetWindowDrawList();
                int first = list.VtxBuffer.Size;
                ImGui.Button(icon.Value + "##" + icon.Key, new Vector2(68, 48));
                float buttonCenter = (ImGui.GetItemRectMin().X + ImGui.GetItemRectMax().X) / 2;
                // The final quad belongs to the single glyph following the button background.
                if (list.VtxBuffer.Size < first + 4) throw new Exception("Missing button icon: " + icon.Key);
                var top = list.VtxBuffer[list.VtxBuffer.Size - 4];
                var bottom = list.VtxBuffer[list.VtxBuffer.Size - 2];
                float center = VisibleCenter(top, bottom, tex, tw, th);
                if (Math.Abs(center - buttonCenter) > 1)
                    throw new Exception($"Visible button icon is off center: {icon.Key}, scale {scale}, offset {center - buttonCenter}");
            }
            ImGui.SetWindowFontScale(1);
            ImGui.PopFont();
            ImGui.End();
            ImGui.Render();
        }
        Console.WriteLine($"PASS {icons.Count} icon names: native rendered quads, valid atlas UVs, pixel dimensions and visible button centering at 100/150/200%");
    }

    private static unsafe float VisibleCenter(ImDrawVertPtr top, ImDrawVertPtr bottom, nint pixels, int width, int height)
    {
        int x0 = (int)MathF.Round(top.uv.X * width), x1 = (int)MathF.Round(bottom.uv.X * width);
        int y0 = (int)MathF.Round(top.uv.Y * height), y1 = (int)MathF.Round(bottom.uv.Y * height);
        int left = x1, right = x0 - 1;
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            if (((byte*)pixels)[y * width + x] < 32) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
        }
        if (right < left) throw new Exception("Icon atlas region contains no visible pixels.");
        return top.pos.X + (left + right + 1 - 2 * x0) * .5f * (bottom.pos.X - top.pos.X) / (x1 - x0);
    }

    private static void CheckIconGallery(ImFontPtr font, Dictionary<string, string> icons, Action<string> capture)
    {
        var io = ImGui.GetIO();
        Vector2 savedDisplay = io.DisplaySize;
        try
        {
            io.DisplaySize = new Vector2(960, 640);
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                for (int frame = 0; frame < 2; frame++)
                {
                    ImGui.NewFrame();
                    ImGui.SetNextWindowPos(Vector2.Zero);
                    ImGui.SetNextWindowSize(io.DisplaySize);
                    ImGui.Begin("Icon gallery", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
                    ImGui.PushFont(font);
                    ImGui.TextUnformatted($"Tabler icons / {scale * 100:0}%");
                    ImGui.SetWindowFontScale(scale);
                    int column = 0;
                    foreach (var icon in icons)
                    {
                        if (column++ % 12 != 0) ImGui.SameLine();
                        ImGui.Button(icon.Value + "##" + icon.Key, new Vector2(68, 48));
                    }
                    ImGui.SetWindowFontScale(1);
                    ImGui.PopFont();
                    ImGui.End();
                    ImGui.Render();
                }
                capture($"icons-{scale * 100:0}");
            }
        }
        finally { io.DisplaySize = savedDisplay; }
    }
}
