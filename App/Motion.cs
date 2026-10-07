using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace DotMic;

// Composition owns time: no per-control animation timers and no semantic ambient motion.
internal sealed class MotionHub
{
    private readonly UISettings system = new();
    private readonly List<Region> regions = [];
    private readonly ConditionalWeakTable<FrameworkElement, RevealAnimations> reveals = new();
    private readonly ConditionalWeakTable<FrameworkElement, ScalarKeyFrameAnimation> stagger = new();
    private bool active;
    internal MotionMode Mode => system.AnimationsEnabled ? MotionMode.Full : MotionMode.Off;
    internal sealed class Region
    {
        internal required FrameworkElement Element;
        internal required Visual Visual;
        internal required ScalarKeyFrameAnimation Opacity;
        internal required Vector3KeyFrameAnimation Offset;
        internal required Vector3KeyFrameAnimation Scale;
        internal required ScalarKeyFrameAnimation Ambient;
        internal required Vector3KeyFrameAnimation Drift;
        internal required string Kind;
        internal Control? Input;
        internal double Period;
        internal float IdleOpacity = 1;
        internal bool Temporary;
        internal bool Hovered, Focused, Pressed;
        internal required CompositionEasingFunction Easing;
    }
    private sealed class RevealAnimations
    {
        internal required ScalarKeyFrameAnimation Opacity;
        internal required Vector3KeyFrameAnimation Offset, Scale;
        internal required CompositionEasingFunction Enter, Exit;
    }
    internal MotionHub(AudioViewModel vm) { }
    internal static Visual VisualFor(FrameworkElement element)
    {
        // XAML owns Offset for layout. Translation is the supported additive motion property.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (visual.Properties.TryGetVector3("Translation", out _) != CompositionGetValueStatus.Succeeded)
            Translate(visual, Vector3.Zero);
        return visual;
    }
    internal static void Translate(Visual visual, Vector3 value) => visual.Properties.InsertVector3("Translation", value);
    internal Region Register(FrameworkElement decoration, string kind, double period, int phase = 0, bool temporary = false)
    {
        var v = VisualFor(decoration); var c = v.Compositor;
        var ambient = c.CreateScalarKeyFrameAnimation(); ambient.Duration = TimeSpan.FromSeconds(Math.Max(6, period));
        ambient.IterationBehavior = AnimationIterationBehavior.Forever;
        ambient.InsertKeyFrame(0, .35f); ambient.InsertKeyFrame(.5f, .8f); ambient.InsertKeyFrame(1, .35f);
        ambient.DelayTime = TimeSpan.FromMilliseconds(phase * 120);
        var drift = c.CreateVector3KeyFrameAnimation(); drift.Duration = ambient.Duration; drift.IterationBehavior = AnimationIterationBehavior.Forever;
        // Ambient light only. Instrument scales and semantic indicators never drift.
        var delta = Vector3.Zero;
        drift.InsertKeyFrame(0, Vector3.Zero); drift.InsertKeyFrame(.5f, delta); drift.InsertKeyFrame(1, Vector3.Zero); drift.DelayTime = ambient.DelayTime;
        var r = new Region { Element = decoration, Visual = v, Kind = kind, Period = period, Temporary = temporary, Easing = c.CreateCubicBezierEasingFunction(new Vector2(.2f, 0), new Vector2(0, 1)), Opacity = c.CreateScalarKeyFrameAnimation(),
             Offset = c.CreateVector3KeyFrameAnimation(), Scale = c.CreateVector3KeyFrameAnimation(), Ambient = ambient, Drift = drift };
        regions.Add(r);
        decoration.SizeChanged += (_, _) => v.CenterPoint = new Vector3((float)decoration.ActualWidth / 2, (float)decoration.ActualHeight / 2, 0);
        return r;
    }
    internal void Attach(Control input, Region r)
    {
        r.Input = input;
        input.PointerEntered += (_, _) => { r.Hovered = true; if (active) { Offset(r, r.Kind == "menu" ? new Vector3(1, 0, 0) : Vector3.Zero); Fade(r, 1); } };
        input.PointerExited += (_, _) => { r.Hovered = false; ReturnIdle(r); };
        input.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) =>
        {
            if (!active) return;
            r.Pressed = true;
            Fade(r, .65f, 70);
        }), true);
        input.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => { r.Pressed = false; Scale(r, 1, 167); ReturnIdle(r); }), true);
        input.AddHandler(UIElement.PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => { r.Pressed = false; Scale(r, 1, 167); ReturnIdle(r); }), true);
        input.GotFocus += (_, _) => { r.Focused = true; Fade(r, 1); };
        input.LostFocus += (_, _) => { r.Focused = false; ReturnIdle(r); };
    }
    private void ReturnIdle(Region r)
    {
        if (!active || r.Pressed) return;
        var batch = r.Visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Offset(r, Vector3.Zero); Fade(r, r.Hovered || r.Focused || r.Period <= 0 ? 1 : .58f);
        batch.Completed += (_, _) =>
        {
            if (active && Mode == MotionMode.Full && !r.Hovered && !r.Focused && !r.Pressed && r.Period > 0 && r.Element.IsLoaded)
            { r.Visual.StartAnimation("Opacity", r.Ambient); r.Visual.StartAnimation("Translation", r.Drift); }
            batch.Dispose();
        };
        batch.End();
    }
    internal void SetActive(bool value)
    {
        active = value;
        foreach (var r in regions)
        {
            foreach (string p in new[] { "Opacity", "Translation", "Scale" }) r.Visual.StopAnimation(p);
            r.Visual.Opacity = r.Period > 0 ? .58f : r.IdleOpacity; Translate(r.Visual, Vector3.Zero); r.Visual.Scale = Vector3.One;
            if (value && Mode == MotionMode.Full && r.Period > 0 && r.Element.IsLoaded) { r.Visual.StartAnimation("Opacity", r.Ambient); r.Visual.StartAnimation("Translation", r.Drift); }
        }
    }
    internal void ReleaseTemporary()
    {
        foreach (var r in regions.Where(r => r.Temporary))
        { r.Visual.StopAnimation("Opacity"); r.Visual.StopAnimation("Translation"); r.Visual.StopAnimation("Scale"); }
        regions.RemoveAll(r => r.Temporary);
    }
    internal bool HasAttachment(Control input) => regions.Any(r => ReferenceEquals(r.Input, input));
    internal void Stagger(Panel panel)
    {
        int index = 0;
        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            var visual = VisualFor(child);
            visual.StopAnimation("Opacity");
            if (Mode != MotionMode.Full) { visual.Opacity = 1; continue; }
            var animation = stagger.GetValue(child, _ => visual.Compositor.CreateScalarKeyFrameAnimation());
            animation.Duration = TimeSpan.FromMilliseconds(160); animation.DelayTime = TimeSpan.FromMilliseconds(index++ * 12);
            animation.InsertExpressionKeyFrame(0, "this.StartingValue"); animation.InsertKeyFrame(1, 1);
            visual.Opacity = 0; visual.StartAnimation("Opacity", animation);
        }
    }
    internal void Fade(Region r, float target, int ms = 167)
    {
        if (!active || Mode == MotionMode.Off) { r.Visual.StopAnimation("Opacity"); r.Visual.Opacity = target; return; }
        r.Opacity.Duration = TimeSpan.FromMilliseconds(Mode == MotionMode.Reduced ? Math.Min(ms, 100) : ms);
        r.Opacity.InsertExpressionKeyFrame(0, "this.StartingValue"); r.Opacity.InsertKeyFrame(1, target, r.Easing); r.Visual.StartAnimation("Opacity", r.Opacity);
    }
    internal void Tick(Region r)
    {
        r.Visual.StopAnimation("Opacity");
        if (!active || Mode != MotionMode.Full) { r.Visual.Opacity = 0; return; }
        r.Opacity.Duration = TimeSpan.FromMilliseconds(80);
        r.Opacity.InsertKeyFrame(0, .8f); r.Opacity.InsertKeyFrame(.35f, .35f); r.Opacity.InsertKeyFrame(1, 0);
        r.Visual.StartAnimation("Opacity", r.Opacity);
    }
    internal void Offset(Region r, Vector3 target, int ms = 167)
    {
        if (!active || Mode != MotionMode.Full) { r.Visual.StopAnimation("Translation"); Translate(r.Visual, r.Kind.EndsWith("-state", StringComparison.Ordinal) ? target : Vector3.Zero); return; }
        r.Offset.Duration = TimeSpan.FromMilliseconds(ms); r.Offset.InsertExpressionKeyFrame(0, "this.StartingValue"); r.Offset.InsertKeyFrame(1, target, r.Easing); r.Visual.StartAnimation("Translation", r.Offset);
    }
    internal void Scale(Region r, float target, int ms)
    {
        if (!active || Mode != MotionMode.Full) { r.Visual.StopAnimation("Scale"); r.Visual.Scale = Vector3.One; return; }
        r.Scale.Duration = TimeSpan.FromMilliseconds(ms); r.Scale.InsertExpressionKeyFrame(0, "this.StartingValue"); r.Scale.InsertKeyFrame(1, new Vector3(target, target, 1), r.Easing); r.Visual.StartAnimation("Scale", r.Scale);
    }
    internal void Reveal(FrameworkElement element, bool entering, bool popup = false, bool retarget = false)
    {
        var v = VisualFor(element); var c = v.Compositor;
        var animations = reveals.GetValue(element, _ => new RevealAnimations
        { Opacity = c.CreateScalarKeyFrameAnimation(), Offset = c.CreateVector3KeyFrameAnimation(), Scale = c.CreateVector3KeyFrameAnimation(),
          Enter = c.CreateCubicBezierEasingFunction(new Vector2(.16f, 1), new Vector2(.3f, 1)), Exit = c.CreateCubicBezierEasingFunction(new Vector2(.4f, 0), new Vector2(1, 1)) });
        v.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        v.StopAnimation("Opacity"); v.StopAnimation("Translation"); v.StopAnimation("Scale");
        if (Mode == MotionMode.Off) { v.Opacity = entering ? 1 : 0; Translate(v, Vector3.Zero); v.Scale = Vector3.One; return; }
        var easing = entering ? animations.Enter : animations.Exit;
        var alpha = animations.Opacity; alpha.Duration = TimeSpan.FromMilliseconds(Mode == MotionMode.Reduced ? 100 : entering ? 200 : 167);
        if (retarget) alpha.InsertExpressionKeyFrame(0, "this.StartingValue"); else alpha.InsertKeyFrame(0, entering ? 0 : v.Opacity);
        alpha.InsertKeyFrame(1, entering ? 1 : 0, easing); v.StartAnimation("Opacity", alpha);
        if (Mode != MotionMode.Full) { Translate(v, Vector3.Zero); v.Scale = Vector3.One; return; }
        var y = animations.Offset; y.Duration = alpha.Duration;
        v.Properties.TryGetVector3("Translation", out var currentTranslation);
        if (retarget) y.InsertExpressionKeyFrame(0, "this.StartingValue"); else y.InsertKeyFrame(0, entering ? new Vector3(0, popup ? 4 : 0, 0) : currentTranslation);
        y.InsertKeyFrame(1, entering ? Vector3.Zero : new Vector3(0, 4, 0), easing); v.StartAnimation("Translation", y);
        var scale = animations.Scale; scale.Duration = alpha.Duration;
        scale.InsertKeyFrame(0, Vector3.One); scale.InsertKeyFrame(1, Vector3.One); v.StartAnimation("Scale", scale);
    }
}
