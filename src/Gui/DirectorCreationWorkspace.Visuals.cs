using static VSDirector.Gui.DirectorCreationText;
using ImGuiNET;
using VSDirector.Data;
using VSDirector.Replay;
using System.Numerics;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private void DrawView()
    {
        if (Project is not { } project) return;
        var settings = project.Visuals;
        void Flag(string key, bool value, Action<bool> set) => Toggle(L("replay-visual-" + key), value, set);
        void Value(string key, double value, Action<double> set, double min, double max, float step = .01f)
            => Number(L("replay-visual-" + key), value, set, min, max, step);
        void Color(string key, string value, Action<string> set)
        {
            var rgba = DirectorReplayVisualRuntime.ParseColor(value, new(0, 0, 0, 1));
            Vector3 rgb = new(rgba.X, rgba.Y, rgba.Z);
            ImGui.PushID(key); ImGui.TextWrapped(L("replay-visual-" + key)); ImGui.SetNextItemWidth(-1);
            bool changed = ImGui.ColorEdit3("##color", ref rgb);
            Widget(key, changed, () => set($"#{(int)(Math.Clamp(rgb.X,0,1)*255):X2}{(int)(Math.Clamp(rgb.Y,0,1)*255):X2}{(int)(Math.Clamp(rgb.Z,0,1)*255):X2}")); ImGui.PopID();
        }
        Hint(L("creation-view-scope"));
        var world = settings.World;
        if (Header(L("replay-visual-group-world")))
        {
            Flag("world-show-terrain", world.ShowTerrain, v => world.ShowTerrain = v);
            Flag("world-show-entities", world.ShowEntities, v => world.ShowEntities = v);
            Flag("world-show-players", world.ShowPlayers, v => world.ShowPlayers = v);
            Flag("world-show-name-tags", world.ShowNameTags, v => world.ShowNameTags = v);
        }
        var sky = settings.Sky;
        if (Header(L("replay-visual-group-sky")))
        {
            Choice(L("replay-visual-sky-mode"), sky.Mode, v => sky.Mode = v, [L("replay-visual-option-recorded"), L("replay-visual-option-solid-color"), L("replay-visual-option-green-screen"), L("replay-visual-option-transparent")]);
            Color("sky-background-color", sky.BackgroundColor, v => sky.BackgroundColor = v);
            Flag("sky-show-sky", sky.ShowSky, v => sky.ShowSky = v);
            Flag("sky-show-clouds", sky.ShowClouds, v => sky.ShowClouds = v);
            Flag("sky-override-clouds", sky.OverrideClouds, v => sky.OverrideClouds = v);
            if (sky.OverrideClouds) Value("sky-cloud-density", sky.CloudDensity, v => sky.CloudDensity = (float)v, 0, 4);
        }
        var environment = settings.Environment;
        if (Header(L("replay-visual-group-environment")))
        {
            Flag("environment-override-time", environment.OverrideTime, v => environment.OverrideTime = v);
            if (environment.OverrideTime) Value("environment-time-of-day-tick", environment.TimeOfDayTick, v => environment.TimeOfDayTick = (int)v, 0, 24000, 10);
            Flag("environment-override-season", environment.OverrideSeason, v => environment.OverrideSeason = v);
            if (environment.OverrideSeason) Value("environment-season", environment.Season, v => environment.Season = (float)v, 0, 1);
            Choice(L("replay-visual-environment-weather"), environment.Weather, v => environment.Weather = v, [L("replay-visual-option-recorded"), L("replay-visual-option-clear"), L("replay-visual-option-rain"), L("replay-visual-option-snow"), L("replay-visual-option-storm")]);
            Value("environment-precipitation", environment.Precipitation, v => environment.Precipitation = (float)v, 0, 1);
            Flag("environment-override-fog", environment.OverrideFog, v => environment.OverrideFog = v);
            if (environment.OverrideFog)
            { Color("environment-fog-color", environment.FogColor, v => environment.FogColor = v); Value("environment-fog-density", environment.FogDensity, v => environment.FogDensity = (float)v, 0, .02, .0001f); }
            Flag("environment-full-bright", environment.FullBright, v => environment.FullBright = v);
        }
        if (Header(L("replay-visual-group-particles"))) Flag("particles-show-particles", settings.Particles.ShowParticles, v => settings.Particles.ShowParticles = v);
    }
}
