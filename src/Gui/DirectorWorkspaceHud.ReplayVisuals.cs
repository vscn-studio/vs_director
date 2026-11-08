using System.Globalization;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using VSDirector.Replay;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private const double ReplayVisualGroupHeaderHeight = 30d;
    private const double ReplayVisualRowHeight = 34d;
    private const double ReplayVisualWheelStep = ReplayVisualRowHeight * 3d;

    private static readonly ReplayVisualGroupDefinition[] ReplayVisualGroupDefinitions =
    {
        new("world", nameof(DirectorReplayVisualSettings.World), new[]
        {
            nameof(DirectorReplayWorldVisuals.ShowTerrain),
            nameof(DirectorReplayWorldVisuals.ShowEntities),
            nameof(DirectorReplayWorldVisuals.ShowPlayers),
            nameof(DirectorReplayWorldVisuals.ShowNameTags),
            nameof(DirectorReplayWorldVisuals.HideTrackedEntity),
            nameof(DirectorReplayWorldVisuals.ShowEntityBoxes),
            nameof(DirectorReplayWorldVisuals.ShowEntityLookDirections)
        }),
        new("sky", nameof(DirectorReplayVisualSettings.Sky), new[]
        {
            nameof(DirectorReplaySkyVisuals.Mode),
            nameof(DirectorReplaySkyVisuals.BackgroundColor),
            nameof(DirectorReplaySkyVisuals.ShowSky),
            nameof(DirectorReplaySkyVisuals.ShowClouds),
            nameof(DirectorReplaySkyVisuals.OverrideClouds),
            nameof(DirectorReplaySkyVisuals.CloudDensity)
        }),
        new("environment", nameof(DirectorReplayVisualSettings.Environment), new[]
        {
            nameof(DirectorReplayEnvironmentVisuals.OverrideTime),
            nameof(DirectorReplayEnvironmentVisuals.TimeOfDayTick),
            nameof(DirectorReplayEnvironmentVisuals.OverrideSeason),
            nameof(DirectorReplayEnvironmentVisuals.Season),
            nameof(DirectorReplayEnvironmentVisuals.Weather),
            nameof(DirectorReplayEnvironmentVisuals.Precipitation),
            nameof(DirectorReplayEnvironmentVisuals.OverrideFog),
            nameof(DirectorReplayEnvironmentVisuals.FogColor),
            nameof(DirectorReplayEnvironmentVisuals.FogDensity),
            nameof(DirectorReplayEnvironmentVisuals.FullBright)
        }),
        new("camera", nameof(DirectorReplayVisualSettings.Camera), new[]
        {
            nameof(DirectorReplayCameraVisuals.OverrideFov),
            nameof(DirectorReplayCameraVisuals.Fov),
            nameof(DirectorReplayCameraVisuals.OverrideRoll),
            nameof(DirectorReplayCameraVisuals.Roll),
            nameof(DirectorReplayCameraVisuals.CameraCollision),
            nameof(DirectorReplayCameraVisuals.OverrideNearClip),
            nameof(DirectorReplayCameraVisuals.NearClip),
            nameof(DirectorReplayCameraVisuals.CameraShake),
            nameof(DirectorReplayCameraVisuals.SplitShake),
            nameof(DirectorReplayCameraVisuals.ShakeFrequencyX),
            nameof(DirectorReplayCameraVisuals.ShakeAmplitudeX),
            nameof(DirectorReplayCameraVisuals.ShakeFrequencyY),
            nameof(DirectorReplayCameraVisuals.ShakeAmplitudeY)
        }),
        new("particles", nameof(DirectorReplayVisualSettings.Particles), new[]
        {
            nameof(DirectorReplayParticleVisuals.ShowParticles)
        }),
        new("audio", nameof(DirectorReplayVisualSettings.Audio), new[]
        {
            nameof(DirectorReplayAudioVisuals.Enabled),
            nameof(DirectorReplayAudioVisuals.MasterVolume),
            nameof(DirectorReplayAudioVisuals.PreservePitchWithReplaySpeed),
            nameof(DirectorReplayAudioVisuals.SpatialAudio)
        }),
        new("composition", nameof(DirectorReplayVisualSettings.Composition), new[]
        {
            nameof(DirectorReplayCompositionVisuals.RuleOfThirds),
            nameof(DirectorReplayCompositionVisuals.CenterGuide),
            nameof(DirectorReplayCompositionVisuals.CameraPath)
        })
    };

    private static readonly IReadOnlyList<ReplayVisualDescriptor> ReplayVisualDescriptors =
        BuildReplayVisualDescriptors();

    private ElementBounds? replayVisualSettingsBounds;
    private double replayVisualScrollOffset;
    private double replayVisualContentHeight;

    private void ComposeReplayVisualSettings(
        GuiComposer composer,
        CairoFont small,
        ref int y)
    {
        double panelWidth = layout.Panel("inspector").Width;
        double viewportHeight = Math.Max(90d, layout.Panel("inspector").Height - y - 8d);
        replayVisualSettingsBounds = ElementBounds.Fixed(
            0d,
            y,
            Math.Max(180d, panelWidth),
            viewportHeight);

        composer
            .BeginDirectorStaticClip(replayVisualSettingsBounds, "replayVisualStaticClip");

        double viewportTop = y;
        double contentViewportHeight = Math.Max(1d, viewportHeight);
        IReadOnlyList<ReplayVisualRow> rows = BuildReplayVisualRows();
        replayVisualContentHeight = rows.Sum(row => row.Height);
        replayVisualScrollOffset = Math.Clamp(
            replayVisualScrollOffset,
            0d,
            Math.Max(0d, replayVisualContentHeight - contentViewportHeight));

        double contentY = viewportTop - replayVisualScrollOffset;
        foreach (ReplayVisualRow row in rows)
        {
            double rowTop = contentY;
            contentY += row.Height;
            if (rowTop + row.Height <= viewportTop || rowTop >= y + viewportHeight)
            {
                continue;
            }
            if (row.IsPreloadEditor)
            {
                AddReplayPreloadEditorRow(composer, small, rowTop, panelWidth);
            }
            else if (row.Descriptor is null)
            {
                AddReplayVisualGroupRow(
                    composer,
                    row.GroupCode,
                    rowTop,
                    panelWidth,
                    row.ShowSeparator);
            }
            else
            {
                AddReplayVisualSettingRow(composer, small, row.Descriptor, rowTop, panelWidth);
            }
        }

        composer.EndDirectorStaticClip("replayVisualStaticClipEnd");
        y += (int)Math.Ceiling(viewportHeight);
    }

    private bool ReplayVisualSettingsContain(int mouseX, int mouseY)
    {
        ElementBounds? bounds = replayVisualSettingsBounds;
        if (bounds is null
            || bounds.ParentBounds is null
            || !double.IsFinite(bounds.absX)
            || !double.IsFinite(bounds.absY)
            || !double.IsFinite(bounds.OuterWidth)
            || !double.IsFinite(bounds.OuterHeight))
        {
            return false;
        }

        return mouseX >= bounds.absX
            && mouseY >= bounds.absY
            && mouseX <= bounds.absX + bounds.OuterWidth
            && mouseY <= bounds.absY + bounds.OuterHeight;
    }

    private void AddReplayVisualGroupRow(
        GuiComposer composer,
        string groupCode,
        double rowTop,
        double panelWidth,
        bool showSeparator)
    {
        if (showSeparator)
        {
            composer.AddCustomRender(
                ElementBounds.Fixed(
                    0d,
                    rowTop + 1d,
                    Math.Max(180d, panelWidth),
                    1d),
                RenderReplayVisualGroupSeparator);
        }

        composer.AddDirectorSingleLineText(
            Lang.Get("vsdirector:replay-visual-group-" + groupCode),
            DirectorFont.WhiteSmallBoldText(),
            ElementBounds.Fixed(10d, rowTop + 2d, Math.Max(170d, panelWidth - 20d), 26d));
    }

    private void RenderReplayVisualGroupSeparator(float deltaTime, ElementBounds bounds)
    {
        _ = deltaTime;
        // Bounds are already in physical pixels.  Snap the filled divider to
        // one row so fractional scroll offsets cannot straddle two rows.
        double left = Math.Round(bounds.renderX);
        double right = Math.Round(bounds.renderX + bounds.OuterWidth);
        double top = Math.Round(bounds.renderY);
        solidTextureRenderer.Render(
            left,
            top,
            Math.Max(1d, right - left),
            1d,
            100.1f,
            ColorUtil.ToRgba(255, 255, 255, 255));
    }

    private void AddReplayVisualSettingRow(
        GuiComposer composer,
        CairoFont font,
        ReplayVisualDescriptor descriptor,
        double rowTop,
        double panelWidth)
    {
        double labelWidth = Math.Clamp(panelWidth * 0.47d, 118d, 220d);
        double controlX = 16d + labelWidth;
        double controlWidth = Math.Max(74d, panelWidth - controlX - 12d);
        string key = descriptor.InputKey;
        composer.AddDirectorSingleLineText(
            Ellipsize(Lang.Get(descriptor.LanguageKey), Math.Max(12, (int)(labelWidth / 7d))),
            font,
            ElementBounds.Fixed(10d, rowTop + 6d, labelWidth, 22d));

        object? value = GetReplayVisualValue(CurrentReplayVisuals(), descriptor);
        Type type = descriptor.ValueProperty.PropertyType;
        if (type == typeof(bool))
        {
            composer.AddSwitch(
                enabled => SetReplayVisualValue(descriptor, enabled, recompose: false),
                ElementBounds.Fixed(
                    Math.Max(controlX, panelWidth - 44d), rowTop, 28d, 28d),
                key,
                28d,
                3d);
            return;
        }
        if (type.IsEnum)
        {
            string[] codes = Enum.GetNames(type);
            string[] names = codes.Select(code => Lang.Get(
                "vsdirector:replay-visual-option-" + ToKebabCase(code))).ToArray();
            int selectedIndex = Math.Max(0, Array.IndexOf(codes, value?.ToString() ?? string.Empty));
            composer.AddDropDown(
                codes,
                names,
                selectedIndex,
                (code, selected) =>
                {
                    if (selected && Enum.TryParse(type, code, ignoreCase: true, out object? parsed))
                    {
                        SetReplayVisualValue(descriptor, parsed, recompose: true);
                    }
                },
                ElementBounds.Fixed(controlX, rowTop, controlWidth, 28d),
                key);
            return;
        }
        if (descriptor.Editor == ReplayVisualEditor.Color)
        {
            composer.AddDirectorColorPopover(
                value?.ToString() ?? "#000000",
                color => SetReplayVisualValue(descriptor, color, recompose: false),
                ElementBounds.Fixed(controlX, rowTop, controlWidth, 28d),
                key);
            return;
        }
        if (descriptor.Editor == ReplayVisualEditor.Time)
        {
            composer.AddTextInput(
                ElementBounds.Fixed(controlX, rowTop, controlWidth, 28d),
                null,
                DirectorFont.TextInput(),
                key,
                () => CommitReplayVisualTimeInput(descriptor));
            return;
        }
        if (descriptor.Range is { } range)
        {
            int sliderValue = range.ToSlider(value);
            composer.AddDirectorSlider(
                next =>
                {
                    SetReplayVisualValue(descriptor, range.FromSlider(next), recompose: false);
                    return true;
                },
                ElementBounds.Fixed(controlX, rowTop, controlWidth, 28d),
                key);
            GuiElementDirectorSlider? slider = composer.GetDirectorSlider(key);
            slider?.SetValues(sliderValue, range.Minimum, range.Maximum, range.Step);
            if (slider is not null)
            {
                slider.OnSliderTooltip = range.Format;
            }
            return;
        }
        if (type == typeof(string))
        {
            composer.AddTextInput(
                ElementBounds.Fixed(controlX, rowTop, controlWidth, 28d),
                null,
                DirectorFont.TextInput(),
                key,
                () => CommitReplayVisualInput(descriptor));
            return;
        }

        composer.AddNumberInput(
            ElementBounds.Fixed(controlX, rowTop, controlWidth, 28d),
            null,
            DirectorFont.TextInput(),
            key,
            () => CommitReplayVisualInput(descriptor));
    }

    private void AddReplayPreloadEditorRow(
        GuiComposer composer,
        CairoFont font,
        double rowTop,
        double panelWidth)
    {
        double labelWidth = Math.Clamp(panelWidth * 0.47d, 118d, 220d);
        double controlX = 16d + labelWidth;
        double controlWidth = Math.Max(74d, panelWidth - controlX - 12d);
        const string key = "replayChunkPreloadEdit";
        composer
            .AddDirectorSingleLineText(
                Ellipsize(
                    Lang.Get("vsdirector:replay-chunk-preload"),
                    Math.Max(12, (int)(labelWidth / 7d))),
                font,
                ElementBounds.Fixed(10d, rowTop + 6d, labelWidth, 22d))
            .AddDirectorButton(
                Lang.Get("vsdirector:edit"),
                OpenReplayPreloadEditor,
                ElementBounds.Fixed(controlX, rowTop, controlWidth, 28d),
                EnumButtonStyle.Small,
                key);
        composer.GetButton(key).Enabled = PreloadEditorReplayInfo() is not null;
    }

    private bool OpenReplayPreloadEditor()
    {
        DirectorReplayInfo? replay = PreloadEditorReplayInfo();
        if (!CanManageChunkPreload || replay is null)
        {
            return true;
        }

        owner.RequestReplayPreloadEditor(replay.Id);
        preloadEditorExpanded = true;
        selectingPreloadGrid = false;
        panningPreloadGrid = false;
        preloadPanChunkX = 0;
        preloadPanChunkZ = 0;
        ComposeWorkflowCanvas();
        return true;
    }

    private IReadOnlyList<ReplayVisualRow> BuildReplayVisualRows()
    {
        List<ReplayVisualRow> rows = new();
        bool firstGroup = true;
        foreach (ReplayVisualGroupDefinition definition in ReplayVisualGroupDefinitions)
        {
            string groupCode = definition.Code;
            rows.Add(new ReplayVisualRow(
                groupCode,
                null,
                ReplayVisualGroupHeaderHeight,
                ShowSeparator: !firstGroup));
            rows.AddRange(ReplayVisualDescriptors
                .Where(descriptor => descriptor.GroupCode == groupCode)
                .Select(descriptor => new ReplayVisualRow(
                    groupCode,
                    descriptor,
                    ReplayVisualRowHeight)));
            if (groupCode == "world" && CanManageChunkPreload)
            {
                rows.Add(new ReplayVisualRow(
                    groupCode,
                    null,
                    ReplayVisualRowHeight,
                    IsPreloadEditor: true));
            }
            firstGroup = false;
        }
        return rows;
    }

    private void ScrollReplayVisualSettings(double direction)
    {
        if (Math.Abs(direction) <= double.Epsilon || replayVisualSettingsBounds is null)
        {
            return;
        }
        double viewportHeight = Math.Max(
            1d,
            replayVisualSettingsBounds.fixedHeight);
        replayVisualScrollOffset = Math.Clamp(
            replayVisualScrollOffset - Math.Sign(direction) * ReplayVisualWheelStep,
            0d,
            Math.Max(0d, replayVisualContentHeight - viewportHeight));
        ComposeInspector();
    }

    private DirectorReplayVisualSettings CurrentReplayVisuals()
        => DirectorReplayVisualSettings.Normalize(CurrentReplayViewportTimeline().Visuals);

    private void CommitReplayVisualInput(ReplayVisualDescriptor descriptor)
    {
        if (Composers[InspectorKey] is not GuiComposer composer)
        {
            return;
        }
        Type type = descriptor.ValueProperty.PropertyType;
        if (type == typeof(string))
        {
            string text = composer.GetTextInput(descriptor.InputKey)?.GetText() ?? string.Empty;
            SetReplayVisualValue(descriptor, text, recompose: true);
            return;
        }
        string numberText = composer.GetNumberInput(descriptor.InputKey)?.GetText() ?? string.Empty;
        if (!double.TryParse(
                numberText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double number)
            || !double.IsFinite(number))
        {
            RestoreReplayVisualInputs(composer);
            return;
        }
        SetReplayVisualValue(descriptor, number, recompose: true);
    }

    private void CommitReplayVisualTimeInput(ReplayVisualDescriptor descriptor)
    {
        if (Composers[InspectorKey] is not GuiComposer composer)
        {
            return;
        }

        string text = composer.GetTextInput(descriptor.InputKey)?.GetText() ?? string.Empty;
        if (!DirectorTimeText.TryParseTicks(text, out int ticks))
        {
            RestoreReplayVisualInputs(composer);
            return;
        }

        SetReplayVisualValue(descriptor, ticks, recompose: false);
        SetInput(composer, descriptor.InputKey, DirectorTimeText.FormatTicks(ticks));
    }

    private bool TrySubmitFocusedReplayVisualTimeInput()
    {
        if (section != DirectorWorkspaceSection.Replay
            || Composers[InspectorKey] is not GuiComposer composer)
        {
            return false;
        }

        foreach (ReplayVisualDescriptor descriptor in ReplayVisualDescriptors)
        {
            if (descriptor.Editor == ReplayVisualEditor.Time
                && composer.GetTextInput(descriptor.InputKey)?.HasFocus == true)
            {
                CommitReplayVisualTimeInput(descriptor);
                return true;
            }
        }
        return false;
    }

    private void SetReplayVisualValue(
        ReplayVisualDescriptor descriptor,
        object value,
        bool recompose)
    {
        if (!TryConvertReplayVisualValue(
                descriptor.ValueProperty.PropertyType,
                value,
                out object converted))
        {
            if (Composers[InspectorKey] is GuiComposer composer)
            {
                RestoreReplayVisualInputs(composer);
            }
            return;
        }
        DirectorReplayViewportTimeline timeline = CurrentReplayViewportTimeline();
        timeline.Visuals = DirectorReplayVisualSettings.Normalize(timeline.Visuals);
        object group = descriptor.GroupProperty.GetValue(timeline.Visuals)
            ?? throw new InvalidOperationException("Replay visual group is unavailable.");
        descriptor.ValueProperty.SetValue(group, converted);
        timeline.Visuals = DirectorReplayVisualSettings.Normalize(timeline.Visuals);
        timeline.SyncActiveSceneFromTimeline();
        if (CommitReplayViewportTimeline(timeline) && recompose)
        {
            ComposeInspector();
        }
    }

    private static bool TryConvertReplayVisualValue(
        Type targetType,
        object value,
        out object converted)
    {
        Type effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (effectiveType.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        try
        {
            if (effectiveType.IsEnum)
            {
                if (value is string text
                    && Enum.TryParse(effectiveType, text, ignoreCase: true, out object? parsed)
                    && parsed is not null)
                {
                    converted = parsed;
                    return true;
                }
                converted = value;
                return false;
            }

            object? result = effectiveType == typeof(string)
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : Convert.ChangeType(value, effectiveType, CultureInfo.InvariantCulture);
            if (result is null
                || result is float floatValue && !float.IsFinite(floatValue)
                || result is double doubleValue && !double.IsFinite(doubleValue))
            {
                converted = value;
                return false;
            }
            converted = result;
            return true;
        }
        catch (Exception exception) when (exception is InvalidCastException
                                          or FormatException
                                          or OverflowException)
        {
            converted = value;
            return false;
        }
    }

    private void RestoreReplayVisualInputs(GuiComposer composer)
    {
        if (section != DirectorWorkspaceSection.Replay)
        {
            return;
        }
        DirectorReplayVisualSettings visuals = CurrentReplayVisuals();
        foreach (ReplayVisualDescriptor descriptor in ReplayVisualDescriptors)
        {
            object? value = GetReplayVisualValue(visuals, descriptor);
            Type type = descriptor.ValueProperty.PropertyType;
            if (type == typeof(bool))
            {
                composer.GetSwitch(descriptor.InputKey)?.SetValue(value is true);
            }
            else if (type == typeof(string))
            {
                if (descriptor.Editor == ReplayVisualEditor.Color)
                {
                    composer.GetDirectorColorPopover(descriptor.InputKey)?.SetValue(
                        value?.ToString() ?? "#000000");
                }
                else
                {
                    SetInput(composer, descriptor.InputKey, value?.ToString() ?? string.Empty);
                }
            }
            else if (descriptor.Editor == ReplayVisualEditor.Time)
            {
                int ticks = value is IConvertible convertible
                    ? convertible.ToInt32(CultureInfo.InvariantCulture)
                    : 0;
                SetInput(composer, descriptor.InputKey, DirectorTimeText.FormatTicks(ticks));
            }
            else if (descriptor.Range is { } range)
            {
                composer.GetDirectorSlider(descriptor.InputKey)?.SetValue(range.ToSlider(value));
            }
            else if (!type.IsEnum && value is IConvertible convertible)
            {
                SetNumber(
                    composer,
                    descriptor.InputKey,
                    convertible.ToDouble(CultureInfo.InvariantCulture));
            }
        }
    }

    private static object? GetReplayVisualValue(
        DirectorReplayVisualSettings visuals,
        ReplayVisualDescriptor descriptor)
    {
        object? group = descriptor.GroupProperty.GetValue(visuals);
        return group is null ? null : descriptor.ValueProperty.GetValue(group);
    }

    private static IReadOnlyList<ReplayVisualDescriptor> BuildReplayVisualDescriptors()
    {
        List<ReplayVisualDescriptor> descriptors = new();
        Type settingsType = typeof(DirectorReplayVisualSettings);
        foreach (ReplayVisualGroupDefinition definition in ReplayVisualGroupDefinitions)
        {
            PropertyInfo group = settingsType.GetProperty(definition.Property)
                ?? throw new InvalidOperationException("Replay visual group property was not found.");
            foreach (string propertyName in definition.VisibleProperties)
            {
                PropertyInfo property = group.PropertyType.GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public)
                    ?? throw new InvalidOperationException(
                        $"Replay visual property '{definition.Property}.{propertyName}' was not found.");
                if (!property.CanRead || !property.CanWrite)
                {
                    throw new InvalidOperationException(
                        $"Replay visual property '{definition.Property}.{propertyName}' is not editable.");
                }
                string propertyCode = ToKebabCase(property.Name);
                descriptors.Add(new ReplayVisualDescriptor(
                    definition.Code,
                    group,
                    property,
                    "replayVisual" + ReplayVisualKey(definition.Code + "-" + propertyCode),
                    "vsdirector:replay-visual-" + definition.Code + "-" + propertyCode,
                    ReplayVisualEditorFor(property),
                    ReplayVisualRangeFor(definition.Code, property.Name)));
            }
        }
        return descriptors;
    }

    private static string ReplayVisualKey(string code)
        => string.Concat(code.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static string ToKebabCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        List<char> result = new(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (char.IsUpper(current) && index > 0)
            {
                result.Add('-');
            }
            result.Add(char.ToLowerInvariant(current));
        }
        return new string(result.ToArray());
    }

    private sealed record ReplayVisualGroupDefinition(
        string Code,
        string Property,
        IReadOnlyList<string> VisibleProperties);

    private sealed record ReplayVisualDescriptor(
        string GroupCode,
        PropertyInfo GroupProperty,
        PropertyInfo ValueProperty,
        string InputKey,
        string LanguageKey,
        ReplayVisualEditor Editor = ReplayVisualEditor.None,
        ReplayVisualRange? Range = null);

    private enum ReplayVisualEditor
    {
        None,
        Color,
        Time
    }

    private sealed record ReplayVisualRange(
        double MinimumValue,
        double MaximumValue,
        double Scale,
        int Step,
        string FormatPattern)
    {
        internal int Minimum => (int)Math.Round(MinimumValue * Scale);
        internal int Maximum => (int)Math.Round(MaximumValue * Scale);

        internal int ToSlider(object? value)
        {
            double numeric = value is IConvertible convertible
                ? convertible.ToDouble(CultureInfo.InvariantCulture)
                : MinimumValue;
            if (!double.IsFinite(numeric))
            {
                numeric = MinimumValue;
            }
            return Math.Clamp((int)Math.Round(numeric * Scale), Minimum, Maximum);
        }

        internal double FromSlider(int value)
            => Math.Clamp(value, Minimum, Maximum) / Scale;

        internal string Format(int value)
            => (FromSlider(value)).ToString(FormatPattern, CultureInfo.InvariantCulture);
    }

    private static ReplayVisualEditor ReplayVisualEditorFor(PropertyInfo property)
        => property.Name is nameof(DirectorReplaySkyVisuals.BackgroundColor)
            or nameof(DirectorReplayEnvironmentVisuals.FogColor)
            ? ReplayVisualEditor.Color
            : property.Name == nameof(DirectorReplayEnvironmentVisuals.TimeOfDayTick)
                ? ReplayVisualEditor.Time
                : ReplayVisualEditor.None;

    private static ReplayVisualRange? ReplayVisualRangeFor(string groupCode, string propertyName)
        => (groupCode, propertyName) switch
        {
            ("sky", nameof(DirectorReplaySkyVisuals.CloudDensity))
                => new(0d, 4d, 100d, 1, "0.00"),
            ("environment", nameof(DirectorReplayEnvironmentVisuals.Season))
                => new(0d, 1d, 100d, 1, "0.00"),
            ("environment", nameof(DirectorReplayEnvironmentVisuals.Precipitation))
                => new(0d, 1d, 100d, 1, "0.00"),
            ("environment", nameof(DirectorReplayEnvironmentVisuals.FogDensity))
                => new(0d, 0.02d, 10000d, 1, "0.0000"),
            ("camera", nameof(DirectorReplayCameraVisuals.Fov))
                => new(10d, 170d, 1d, 1, "0"),
            ("camera", nameof(DirectorReplayCameraVisuals.Roll))
                => new(-180d, 180d, 1d, 1, "0"),
            ("camera", nameof(DirectorReplayCameraVisuals.NearClip))
                => new(0.001d, 16d, 1000d, 1, "0.###"),
            ("camera", nameof(DirectorReplayCameraVisuals.ShakeFrequencyX))
                or ("camera", nameof(DirectorReplayCameraVisuals.ShakeFrequencyY))
                => new(0.1d, 10d, 100d, 1, "0.00"),
            ("camera", nameof(DirectorReplayCameraVisuals.ShakeAmplitudeX))
                or ("camera", nameof(DirectorReplayCameraVisuals.ShakeAmplitudeY))
                => new(0d, 10d, 100d, 1, "0.00"),
            ("audio", nameof(DirectorReplayAudioVisuals.MasterVolume))
                => new(0d, 2d, 100d, 1, "0.00"),
            _ => null
        };

    private sealed record ReplayVisualRow(
        string GroupCode,
        ReplayVisualDescriptor? Descriptor,
        double Height,
        bool ShowSeparator = false,
        bool IsPreloadEditor = false);
}
