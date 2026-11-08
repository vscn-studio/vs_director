using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VSDirector.Gui;

/// <summary>Keep the two native surfaces adjacent when the game recalculates GUI bounds.</summary>
internal sealed class DirectorPauseWindowBounds : ElementBounds
{
    private readonly ICoreClientAPI api;
    private readonly ElementBounds menu;
    internal const double Gap = 12;

    internal DirectorPauseWindowBounds(ICoreClientAPI api, ElementBounds menu, double width, double height)
    {
        this.api = api;
        this.menu = menu;
        fixedWidth = width;
        fixedHeight = height;
        Alignment = EnumDialogArea.CenterMiddle;
    }

    public override void CalcWorldBounds()
    {
        double scale = RuntimeEnv.GUIScale;
        double screen = api.Render.FrameWidth / scale;
        double menuWidth = menu.OuterWidth / scale;
        double centeredMenuLeft = (screen - menuWidth) / 2;
        double left = Math.Max(12, Math.Min(centeredMenuLeft, screen - menuWidth - Gap - fixedWidth - 12));
        menu.fixedOffsetX = left - centeredMenuLeft;
        menu.CalcWorldBounds();
        fixedOffsetX = left + menuWidth + Gap - (screen - fixedWidth) / 2;
        base.CalcWorldBounds();
    }
}
