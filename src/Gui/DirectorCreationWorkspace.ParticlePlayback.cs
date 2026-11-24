using ImGuiNET;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private bool ShowParticleToolbarProgress => Project?.Objects.Any(o=>o.ParticleEffect is not null)==true
        && !Runtime.ParticlesReady && !Runtime.ParticleRebuildCancelled && Runtime.ParticleError.Length==0;

    private void DrawParticleTools()
    {
        if(SelectedObject?.ParticleEffect is null)return;
        float side=ImGui.GetFrameHeight();
        float right=ImGui.GetCursorScreenPos().X+ImGui.GetContentRegionAvail().X;
        foreach(string icon in new[]{"refresh","viewfinder"})
        {
            if(ImGui.GetItemRectMax().X+ImGui.GetStyle().ItemSpacing.X+side<=right)ImGui.SameLine();
            using(DirectorImGuiTheme.Button(icon=="viewfinder"&&particleShowGuides?"active":"normal"))
                if(DirectorImGuiIcons.Button(DirectorImGuiIcons.Get(icon)+"##particle-tool-"+icon,new(side,side)))
                {
                    if(icon=="refresh")Runtime.InvalidateParticleEnvironment();
                    else particleShowGuides=!particleShowGuides;
                }
            if(ImGui.IsItemHovered())DirectorImGuiTooltip.Show(icon=="refresh"?L("creation-particle-refresh-environment"):P("guides"));
        }
        if(Runtime.ParticleError.Length>0)ImGui.TextWrapped(Runtime.ParticleError);
        else if(Runtime.ParticleRebuildCancelled)ImGui.TextWrapped(L("creation-particle-cancelled"));
    }

    private void DrawParticleToolbarProgress(float width)
    {
        if(width<=0)return;
        using var style=DirectorImGuiTheme.Toolbar();
        ImGui.SetCursorPos(new(ImGui.GetWindowWidth()-ImGui.GetStyle().WindowPadding.X-width,Math.Max(0,(ImGui.GetWindowHeight()-ImGui.GetFrameHeight())*.5f)));
        ImGui.ProgressBar((float)Runtime.ParticleProgress,new(width,ImGui.GetFrameHeight()));
        if(ImGui.IsItemHovered())DirectorImGuiTooltip.Show(L("creation-particle-cancel"));
        if(ImGui.BeginPopupContextItem("particle-rebuild-menu"))
        {
            if(ImGui.MenuItem(L("creation-particle-cancel")))
            {if(Runtime.Exporting)Runtime.CancelExport();Runtime.CancelParticleRebuild();}
            ImGui.EndPopup();
        }
    }
}
