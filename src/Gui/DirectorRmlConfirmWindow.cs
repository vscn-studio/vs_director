using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSImGui.API;

namespace VSDirector.Gui;

/// <summary>Native ImGui confirmation dialog used for destructive actions.</summary>
internal sealed class DirectorRmlConfirmWindow : IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly string title;
    private readonly string message;
    private readonly Action onConfirm;
    private DirectorConfirmDialog? window;

    internal DirectorRmlConfirmWindow(ICoreClientAPI api, string title, string message, Action onConfirm)
    {
        this.api = api;
        this.title = title;
        this.message = message;
        this.onConfirm = onConfirm;
    }

    internal bool IsOpened => window?.IsVisible == true;

    internal void TryOpen()
    {
        window ??= new DirectorConfirmDialog(api, title, message, onConfirm);
        window.Open();
    }

    internal bool TryClose()
    {
        if (window is null) return true;
        window.Close();
        return true;
    }

    public void Dispose()
    {
        window?.Dispose();
        window = null;
    }

    private sealed class DirectorConfirmDialog : DirectorImGuiDialog
    {
        private readonly string message;
        private readonly Action onConfirm;
        private bool visible;

        internal DirectorConfirmDialog(ICoreClientAPI api, string title, string message, Action onConfirm)
            : base(api, ImGuiWindowFlags.AlwaysAutoResize, title, "vsdirector-confirm", false)
        {
            this.message = message;
            this.onConfirm = onConfirm;
        }

        internal bool IsVisible => visible;

        protected override bool OnOpen()
        {
            visible = true;
            return true;
        }

        protected override bool OnClose()
        {
            visible = false;
            return true;
        }

        protected override CallbackGUIStatus Draw(float deltaSeconds)
        {
            if (!Opened) return DrawStatus();
            using var font = DirectorImGuiRuntime.UseFont();

            ImGui.SetNextWindowSize(new System.Numerics.Vector2(520 * DirectorImGuiTheme.Scale, 0), ImGuiCond.FirstUseEver);
            bool opened = Opened;
            bool drawContents = ImGui.Begin($"{WindowTitle}###vsdirector-confirm", ref opened, Flags);
            try
            {
                ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);
                if (drawContents) OnDraw();
            }
            finally
            {
                ImGui.End();
            }
            if (!opened) Close();
            return DrawStatus();
        }

        protected override bool OnDraw()
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 480 * DirectorImGuiTheme.Scale);
            ImGui.TextWrapped(message);
            ImGui.PopTextWrapPos();
            ImGui.Spacing();

            float buttonWidth = MathF.Max(100 * DirectorImGuiTheme.Scale, ImGui.CalcTextSize(Lang.Get("vsdirector:cancel")).X + 32 * DirectorImGuiTheme.Scale);
            using var cancelTone = DirectorImGuiTheme.Button();
            if (ImGui.Button(Lang.Get("vsdirector:cancel"), new System.Numerics.Vector2(buttonWidth, 0)))
            {
                Close();
            }
            ImGui.SameLine();
            using var deleteTone = DirectorImGuiTheme.Button("danger");
            if (ImGui.Button(Lang.Get("vsdirector:confirm-delete"), new System.Numerics.Vector2(buttonWidth, 0)))
            {
                Close();
                onConfirm();
            }

            return true;
        }
    }
}
