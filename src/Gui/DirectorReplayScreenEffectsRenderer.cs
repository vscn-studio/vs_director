using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using VSDirector.Replay;

namespace VSDirector.Gui;

/// <summary>
/// Applies the replay ColorGrade element to the game's final-composition uniforms
/// (sepia, contrast, damage vignette, bloom, glitch) for one frame at a time, and draws
/// Title element cards. The grade is set just before the final composition and the
/// game's own values are put back right after it, so the game's dynamic grading and
/// damage effects keep working.
/// </summary>
internal sealed class DirectorReplayScreenEffectsRenderer : IRenderer, IDisposable
{
    private readonly ICoreClientAPI capi;
    private readonly GradeRestorer restorer;
    private LoadedTexture titleTexture;
    private string renderedTitleKey = string.Empty;
    private bool disposed;

    internal DirectorReplayScreenEffectsRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        titleTexture = new LoadedTexture(capi);
        restorer = new GradeRestorer(capi);
        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterPostProcessing, "vsdirector-replay-grade");
        capi.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "vsdirector-replay-title");
        capi.Event.RegisterRenderer(restorer, EnumRenderStage.AfterFinalComposition, "vsdirector-replay-grade-restore");
    }

    public double RenderOrder => 0.992d;

    public int RenderRange => int.MaxValue;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage == EnumRenderStage.AfterPostProcessing)
        {
            ApplyGrade();
        }
        else if (stage == EnumRenderStage.Ortho)
        {
            RenderTitle();
        }
    }

    private void ApplyGrade()
    {
        if (DirectorReplayVisualRuntime.Grade is not DirectorReplayScreenGrade grade)
        {
            return;
        }
        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        restorer.Capture(uniforms);
        uniforms.ExtraSepia = Math.Clamp(uniforms.ExtraSepia + grade.Sepia, 0f, 1.5f);
        uniforms.ExtraContrastLevel += grade.Contrast;
        uniforms.DamageVignetting = Math.Max(uniforms.DamageVignetting, grade.Vignette);
        uniforms.AmbientBloomLevelAdd[3] += grade.Bloom;
        // Keep below 0.5: the sound engine starts distorting audio above that.
        uniforms.GlitchStrength = Math.Max(uniforms.GlitchStrength, Math.Min(0.45f, grade.Glitch));
    }

    private void RenderTitle()
    {
        if (DirectorReplayVisualRuntime.Grade is { Fade: > 0.001f } faded)
        {
            RenderFade(faded.Fade);
        }
        if (DirectorReplayVisualRuntime.Title is not DirectorReplayActiveTitle title || title.Alpha <= 0.001f)
        {
            return;
        }
        int width = Math.Max(1, capi.Render.FrameWidth);
        int height = Math.Max(1, capi.Render.FrameHeight);
        string key = string.Join("\u001f", title.Text, title.SubText, title.Position, width, height);
        if (key != renderedTitleKey)
        {
            ComposeTitle(title, width, height);
            renderedTitleKey = key;
        }
        capi.Render.Render2DTexturePremultipliedAlpha(titleTexture.TextureId, 0d, 0d, width, height, 9990f,
            new Vec4f(title.Alpha, title.Alpha, title.Alpha, title.Alpha));
    }

    private LoadedTexture? blackTexture;

    private void RenderFade(float amount)
    {
        if (blackTexture is null)
        {
            blackTexture = new LoadedTexture(capi);
            using ImageSurface surface = new(Format.Argb32, 4, 4);
            using (Context context = new(surface))
            {
                context.SetSourceRGBA(0d, 0d, 0d, 1d);
                context.Paint();
            }
            capi.Gui.LoadOrUpdateCairoTexture(surface, linearMag: false, ref blackTexture);
        }
        float alpha = Math.Clamp(amount, 0f, 1f);
        capi.Render.Render2DTexturePremultipliedAlpha(blackTexture.TextureId, 0d, 0d,
            capi.Render.FrameWidth, capi.Render.FrameHeight, 9980f, new Vec4f(alpha, alpha, alpha, alpha));
    }

    private void ComposeTitle(DirectorReplayActiveTitle title, int width, int height)
    {
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        double scale = height / 1080d;
        double titleSize = 64d * scale;
        double subSize = 30d * scale;
        context.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
        context.SetFontSize(titleSize);
        TextExtents main = context.TextExtents(title.Text);
        context.SelectFontFace("Sans", FontSlant.Italic, FontWeight.Normal);
        context.SetFontSize(subSize);
        TextExtents sub = context.TextExtents(title.SubText);
        double blockHeight = (title.Text.Length > 0 ? titleSize : 0d)
            + (title.SubText.Length > 0 ? subSize * 1.5d : 0d);
        double top = title.Position switch
        {
            DirectorReplayTitlePosition.Center => (height - blockHeight) / 2d,
            DirectorReplayTitlePosition.Top => height * 0.08d,
            _ => height * 0.72d
        };
        bool lowerThird = title.Position == DirectorReplayTitlePosition.LowerThird;
        double left = lowerThird ? width * 0.07d : 0d;
        if (lowerThird)
        {
            double barWidth = Math.Max(main.Width, sub.Width) + 60d * scale;
            context.SetSourceRGBA(0d, 0d, 0d, 0.55d);
            context.Rectangle(left - 24d * scale, top - 18d * scale, barWidth, blockHeight + 36d * scale);
            context.Fill();
            context.SetSourceRGBA(1d, 0.78d, 0.25d, 0.95d);
            context.Rectangle(left - 24d * scale, top - 18d * scale, 6d * scale, blockHeight + 36d * scale);
            context.Fill();
        }
        double y = top;
        if (title.Text.Length > 0)
        {
            context.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
            context.SetFontSize(titleSize);
            double x = lowerThird ? left : (width - main.Width) / 2d - main.XBearing;
            DrawShadowedText(context, title.Text, x, y + titleSize * 0.8d, scale);
            y += titleSize;
        }
        if (title.SubText.Length > 0)
        {
            context.SelectFontFace("Sans", FontSlant.Italic, FontWeight.Normal);
            context.SetFontSize(subSize);
            double x = lowerThird ? left : (width - sub.Width) / 2d - sub.XBearing;
            DrawShadowedText(context, title.SubText, x, y + subSize * 1.1d, scale);
        }
        capi.Gui.LoadOrUpdateCairoTexture(surface, linearMag: true, ref titleTexture);
    }

    private static void DrawShadowedText(Context context, string text, double x, double y, double scale)
    {
        context.SetSourceRGBA(0d, 0d, 0d, 0.75d);
        context.MoveTo(x + 3d * scale, y + 3d * scale);
        context.ShowText(text);
        context.SetSourceRGBA(1d, 1d, 1d, 1d);
        context.MoveTo(x, y);
        context.ShowText(text);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        restorer.Restore();
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterPostProcessing);
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        capi.Event.UnregisterRenderer(restorer, EnumRenderStage.AfterFinalComposition);
        titleTexture.Dispose();
        blackTexture?.Dispose();
    }

    /// <summary>Puts the game's uniform values back after the final composition used the grade.</summary>
    private sealed class GradeRestorer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private bool captured;
        private float sepia;
        private float contrast;
        private float vignette;
        private float bloom;
        private float glitch;

        internal GradeRestorer(ICoreClientAPI capi) => this.capi = capi;

        public double RenderOrder => 0d;

        public int RenderRange => int.MaxValue;

        internal void Capture(DefaultShaderUniforms uniforms)
        {
            if (captured)
            {
                return;
            }
            sepia = uniforms.ExtraSepia;
            contrast = uniforms.ExtraContrastLevel;
            vignette = uniforms.DamageVignetting;
            bloom = uniforms.AmbientBloomLevelAdd[3];
            glitch = uniforms.GlitchStrength;
            captured = true;
        }

        internal void Restore()
        {
            if (!captured)
            {
                return;
            }
            DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
            uniforms.ExtraSepia = sepia;
            uniforms.ExtraContrastLevel = contrast;
            uniforms.DamageVignetting = vignette;
            uniforms.AmbientBloomLevelAdd[3] = bloom;
            uniforms.GlitchStrength = glitch;
            captured = false;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage) => Restore();

        public void Dispose() => Restore();
    }
}
