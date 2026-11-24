using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;
using VSDirector.Rendering;
using VSImGui.API;

namespace VSDirector.Gui;

/// <summary>Non-interactive viewport overlay, also visible while export hides the workspace.</summary>
internal sealed class DirectorReplayProgressOverlay(ICoreClientAPI api, VSDirectorModSystem owner) : ImGuiDialogBase(api)
{
    private readonly DirectorOperationProgress history = new();
    private double completedAt;
    protected override bool OnDraw() => true;

    internal void Reset()
    {
        history.Clear();
        if (Opened) Close();
    }

    internal void Update()
    {
        if (DirectorOffscreenRenderRuntime.IsRendering) return;
        if (owner.IsShuttingDown) { Reset(); return; }
        // OBS records the game surface; this overlay is for offscreen FFmpeg
        // and audio exports, so it must not appear in an OBS recording.
        if (owner.IsObsReplayExporting) { Reset(); return; }
        var state = owner.ReplayOperationStatus(history.Current?.Export == true);
        double now = ClientApi.ElapsedMilliseconds / 1000d;
        bool wasActive = history.Current?.Active == true;
        if (!state.Active && wasActive) completedAt = now;
        if (state.Active || wasActive) history.Update(state, now);
        bool visible = state.Active || history.Current is { } last
            && now - completedAt < (last.Error.Length > 0 ? 10 : last.Export ? 5 : 1.5);
        if (visible && !Opened) Open();
        else if (!visible && Opened) Close();
    }

    protected override CallbackGUIStatus Draw(float deltaSeconds)
    {
        if (!Opened || DirectorOffscreenRenderRuntime.IsRendering || history.Current is not { } state) return DrawStatus();
        var (position, viewport) = owner.ReplayProgressViewport(ImGui.GetIO().DisplaySize);
        float scale = DirectorImGuiTheme.Scale;
        var font = DirectorImGuiRuntime.CurrentDefaultFont();
        float fontSize = font.FontSize * scale;
        float width = Math.Max(1, Math.Min(420 * scale, Math.Min(viewport.X * .65f, viewport.X - 32 * scale)));
        float height = fontSize + 12 * scale;
        Vector2 center = position + viewport * .5f;
        Vector2 min = center - new Vector2(width, height) * .5f;
        Vector2 max = min + new Vector2(width, height);
        var draw = ImGui.GetForegroundDrawList();
        ImGui.PushFont(font);
        draw.PushClipRect(position, position + viewport, true);
        try
        {
            // Draw directly into the viewport: no window, border, controls or input capture.
            draw.AddRectFilled(min, max, 0xcc282020u);
            if (state.Progress > 0)
                draw.AddRectFilled(min, new Vector2(min.X + width * (float)state.Progress, max.Y), 0xff00b3e6u);
            Centered(state.Title, new Vector2(center.X, min.Y - 12 * scale - fontSize * .5f), width, fontSize);
            string phaseKey = state.Export || state.Phase is "error" or "stopped" ? "replay-export-state-" : "workspace-replay-phase-";
            string label = Lang.Get("vsdirector:replay-progress-phase-percent",
                Lang.Get("vsdirector:" + phaseKey + state.Phase), state.Progress * 100);
            Centered(label, center, width - 16 * scale, fontSize);

            // New messages enter at the bottom; each of the latest three has its own centered row.
            string[] latest = history.Lines.TakeLast(3).ToArray();
            float messageSize = fontSize * .85f;
            float lineHeight = messageSize + 6 * scale;
            for (int i = 0; i < latest.Length; i++)
            {
                string message = latest[i];
                int timestampEnd = message.IndexOf("] ", StringComparison.Ordinal);
                if (timestampEnd >= 0) message = message[(timestampEnd + 2)..];
                message = string.Join(" ", message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                Centered(message, new Vector2(center.X, max.Y + 12 * scale + messageSize * .5f + i * lineHeight),
                    width - 12 * scale, messageSize);
            }
        }
        finally { draw.PopClipRect(); ImGui.PopFont(); }
        return DrawStatus();

        void Centered(string text, Vector2 at, float available, float size)
        {
            Vector2 Measure(string value) => ImGui.CalcTextSize(value) * (size / ImGui.GetFontSize());
            if (Measure(text).X > available)
            {
                int length = text.Length;
                while (length > 0 && Measure(text[..length] + "…").X > available) length--;
                text = text[..length] + "…";
            }
            Vector2 topLeft = at - Measure(text) * .5f;
            draw.AddText(font, size, topLeft + new Vector2(scale), 0xee000000u, text);
            draw.AddText(font, size, topLeft, 0xffffffffu, text);
        }
    }
}
