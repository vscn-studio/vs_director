using System.Globalization;
using Vintagestory.API.Client;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private sealed record ReplayPropertyRange(
        double MinimumValue,
        double MaximumValue,
        double Scale,
        int Step,
        string Format,
        bool TimeInput = false)
    {
        internal int Minimum => (int)Math.Round(MinimumValue * Scale);
        internal int Maximum => (int)Math.Round(MaximumValue * Scale);

        internal int ToSlider(double value)
        {
            if (!double.IsFinite(value))
            {
                value = MinimumValue;
            }
            return Math.Clamp((int)Math.Round(value * Scale), Minimum, Maximum);
        }

        internal double FromSlider(int value)
            => Math.Clamp(value, Minimum, Maximum) / Scale;
    }

    private static ReplayPropertyRange? ReplayPropertyRangeFor(string key)
    {
        if (!key.StartsWith("replayElement", StringComparison.Ordinal))
        {
            return null;
        }

        string field = key["replayElement".Length..];
        if (field.Length == 0)
        {
            return null;
        }
        field = char.ToLowerInvariant(field[0]) + field[1..];
        return field switch
        {
            "distance" => new(0.01d, 128d, 100d, 1, "0.00"),
            "yaw" or "pitch" or "roll" => new(-180d, 180d, 1d, 1, "0"),
            "frequencyX" or "frequencyY" => new(0.1d, 10d, 100d, 1, "0.00"),
            "amplitudeX" or "amplitudeY" => new(0d, 10d, 100d, 1, "0.00"),
            "fov" => new(1d, DirectorReplayElementTimeline.MaxFovDegrees, 1d, 1, "0"),
            "speed" => new(0.1d, 10d, 100d, 1, "0.00"),
            "timeOfDay" => new(0d, 24000d, 1d, 1, "0", TimeInput: true),
            "freezeDelay" => new(0d, DirectorReplayElementTimeline.MaxFreezeDelayTicks, 1d, 1, "0"),
            "volume" => new(0d, 200d, 1d, 1, "0"),
            "audioPitch" => new(0.1d, 2d, 100d, 1, "0.00"),
            _ => null
        };
    }

    private static ReplayPropertyRange? ReplayTimelineRangeFor(string key)
        => key is "replayBaseSpeed" or "replaySpeedValue"
            ? new(DirectorReplayTimeline.MinSpeed, DirectorReplayTimeline.MaxSpeed,
                100d, 1, "0.00")
            : null;

    private static string ReadReplayTimelineValue(
        GuiComposer composer,
        string key,
        string fallback)
    {
        if (ReplayTimelineRangeFor(key) is not { } range)
        {
            return composer.GetNumberInput(key)?.GetText() ?? fallback;
        }

        return composer.GetDirectorSlider(key) is { } slider
            ? range.FromSlider(slider.Value).ToString(range.Format, CultureInfo.InvariantCulture)
            : fallback;
    }

    private static void RestoreReplayTimelineValue(
        GuiComposer composer,
        string key,
        double value)
    {
        if (ReplayTimelineRangeFor(key) is not { } range)
        {
            SetNumber(composer, key, value);
            return;
        }
        composer.GetDirectorSlider(key)?.SetValues(
            range.ToSlider(value), range.Minimum, range.Maximum, range.Step);
    }

    private void SetReplayElementSliderValue(string key, ReplayPropertyRange range, int value)
    {
        string field = key["replayElement".Length..];
        field = char.ToLowerInvariant(field[0]) + field[1..];
        replayElementFieldValues[field] = range.FromSlider(value)
            .ToString(range.Format, CultureInfo.InvariantCulture);
    }

    private string ReadReplayPropertyValue(GuiComposer composer, string key, string fallback)
    {
        if (ReplayPropertyRangeFor(key) is not { } range)
        {
            return composer.GetNumberInput(key)?.GetText() ?? fallback;
        }

        if (range.TimeInput)
        {
            string text = composer.GetTextInput(key)?.GetText() ?? string.Empty;
            return DirectorTimeText.TryParseTicks(text, out int ticks)
                ? ticks.ToString(CultureInfo.InvariantCulture)
                : fallback;
        }

        return composer.GetDirectorSlider(key) is { } slider
            ? range.FromSlider(slider.Value).ToString(range.Format, CultureInfo.InvariantCulture)
            : fallback;
    }

    private static void RestoreReplayPropertyValue(
        GuiComposer composer,
        string key,
        double value)
    {
        if (ReplayPropertyRangeFor(key) is not { } range)
        {
            SetNumber(composer, key, value);
            return;
        }

        if (range.TimeInput)
        {
            SetInput(
                composer,
                key,
                DirectorTimeText.FormatTicks(
                    Math.Clamp((int)Math.Round(value), range.Minimum, range.Maximum)));
            return;
        }

        composer.GetDirectorSlider(key)?.SetValue(range.ToSlider(value));
    }
}
