using System.Globalization;
using Vintagestory.API.Config;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private readonly HashSet<Guid> expandedReplayPresetGroups = new();

    private bool ToggleReplayPresetGroup(Guid id)
    {
        if (!expandedReplayPresetGroups.Add(id)) expandedReplayPresetGroups.Remove(id);
        ComposeTimeline();
        return true;
    }

    private void ImGuiReplayVisualElement(DirectorImGuiForm form, DirectorReplayElementKeyframe source)
    {
        var frame = source.DeepClone();
        var visuals = DirectorReplayVisualSettings.Normalize(frame.VisualSettings);
        frame.VisualSettings = visuals;
        form.Number(ImGuiLabel("replay-element-time-seconds"), frame.TimeSeconds,
            value => frame.TimeSeconds = Math.Clamp(value, 0, owner.ReplayDurationSeconds));
        if (frame.TrackType == DirectorReplayElementTrackType.Marker)
            form.Text(ImGuiLabel("name"), frame.Text, value => frame.Text = value);
        else
        {
            form.Check(ImGuiLabel("replay-element-ends"), frame.EndsElement, value => frame.EndsElement = value);
            string section = frame.TrackType switch
            {
                DirectorReplayElementTrackType.Weather => "environment",
                DirectorReplayElementTrackType.Sky => "sky",
                _ => "composition"
            };
            foreach (var descriptor in ReplayVisualDescriptors.Where(d => d.GroupCode == section))
            {
                object? value = GetReplayVisualValue(visuals, descriptor);
                Type type = descriptor.ValueProperty.PropertyType;
                string label = Lang.Get(descriptor.LanguageKey);
                void Set(object next)
                {
                    if (TryConvertReplayVisualValue(type, next, out var converted))
                        descriptor.ValueProperty.SetValue(descriptor.GroupProperty.GetValue(visuals), converted);
                }
                if (descriptor.Editor == ReplayVisualEditor.Time)
                    form.Time(label, TimeSpan.FromMinutes(Math.Round(Convert.ToInt32(value) * 1440d / 24000) % 1440),
                        v => Set((int)Math.Round(v.TotalMinutes * 24000 / 1440)));
                else if (descriptor.Editor == ReplayVisualEditor.Color) form.Color(label, (string?)value ?? "#FFFFFF", v => Set(v));
                else if (descriptor.Range is { } range)
                    form.Slider(label, Convert.ToDouble(value, CultureInfo.InvariantCulture), range.MinimumValue,
                        range.MaximumValue, range.Step / range.Scale, v => Set(v));
                else if (type == typeof(bool)) form.Check(label, (bool)value!, v => Set(v));
                else if (type.IsEnum)
                    form.Select(label, value!.ToString()!, Enum.GetNames(type)
                        .Select(v => (v, ImGuiLabel("replay-visual-option-" + ToKebabCase(v)))), v => Set(v));
                else if (type == typeof(string)) form.Text(label, (string?)value ?? "", v => Set(v));
                else form.Number(label, Convert.ToDouble(value, CultureInfo.InvariantCulture), v => Set(v));
            }
        }
        KeyframeAction(form, "apply", () =>
        {
            var timeline = owner.ReplayViewportTimeline;
            int index = timeline.ElementKeyframes.FindIndex(item => item.Id == frame.Id);
            if (index < 0) return true;
            frame.InterpolationToNext = DirectorReplayElementInterpolation.Hold;
            timeline.ElementKeyframes[index] = frame;
            if (CommitReplayViewportTimeline(timeline))
            {
                CloseReplayFloatingEditor();
                ComposeInspector();
                ComposeTimeline();
            }
            return true;
        }, "primary");
        KeyframeAction(form, "delete", DeleteSelectedReplayElementKeyframe, "danger");
    }
}
