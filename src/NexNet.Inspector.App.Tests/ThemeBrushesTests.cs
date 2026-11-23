using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using NexNet.Inspector.App.Checks;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Tests;

/// <summary>The drawn controls' brush cache: every enum value resolves, per theme, once, and pens are shared.</summary>
[NotInParallel("avalonia")]
public sealed class ThemeBrushesTests
{
    private static readonly ThemeBrush[] All = Enum.GetValues<ThemeBrush>();

    [Test]
    public async Task Every_brush_has_a_distinct_key_and_resolves_in_both_themes()
    {
        List<string> faults = await HeadlessSession.Run(() =>
        {
            ThemeBrushes.Reset();
            List<string> f = [];
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                ThemePalette palette = ThemeBrushes.For(variant);
                foreach (ThemeBrush brush in All)
                {
                    if (ReferenceEquals(palette[brush], Brushes.Magenta)) f.Add($"{ThemeBrushes.Key(brush)} ({variant}) did not resolve");
                }
            }

            return f;
        });

        using (Assert.Multiple())
        {
            await Assert.That(All.Length).IsEqualTo(ThemeBrushes.Count);
            await Assert.That(All.Select(ThemeBrushes.Key).Distinct().Count()).IsEqualTo(All.Length);
            await Assert.That(faults).IsEmpty();
        }
    }

    [Test]
    public async Task Solid_brushes_are_immutable_copies_of_the_tokens()
    {
        (bool immutable, Color light, Color dark, Color lightToken, Color darkToken, bool stripe) = await HeadlessSession.Run(() =>
        {
            ThemeBrushes.Reset();
            Application app = HeadlessSession.App;
            ThemePalette l = ThemeBrushes.For(ThemeVariant.Light);
            ThemePalette d = ThemeBrushes.For(ThemeVariant.Dark);
            bool allImmutable = All.Where(b => b is not ThemeBrush.TimelineGap and not ThemeBrush.HexRedacted)
                .All(b => l[b] is ImmutableSolidColorBrush && d[b] is ImmutableSolidColorBrush);
            app.TryGetResource("Color.Direction.Outbound", ThemeVariant.Light, out object? lt);
            app.TryGetResource("Color.Direction.Outbound", ThemeVariant.Dark, out object? dt);
            return (allImmutable, ((ISolidColorBrush)l[ThemeBrush.DirectionOutbound]).Color, ((ISolidColorBrush)d[ThemeBrush.DirectionOutbound]).Color,
                (Color)lt!, (Color)dt!, l[ThemeBrush.TimelineGap] is ITileBrush);
        });

        using (Assert.Multiple())
        {
            await Assert.That(immutable).IsTrue();
            await Assert.That(light).IsEqualTo(lightToken);
            await Assert.That(dark).IsEqualTo(darkToken);
            await Assert.That(light).IsNotEqualTo(dark);
            await Assert.That(stripe).IsTrue();
        }
    }

    [Test]
    public async Task A_palette_and_its_pens_are_built_once_per_theme()
    {
        (bool samePalette, bool sameBrush, bool samePen, bool otherWidth, bool dashed, bool reset, double width) = await HeadlessSession.Run(() =>
        {
            ThemeBrushes.Reset();
            ThemePalette first = ThemeBrushes.For(ThemeVariant.Dark);
            ThemePalette again = ThemeBrushes.For(ThemeVariant.Dark);
            IPen pen = first.Pen(ThemeBrush.BorderDefault, 1);
            IPen dash = first.Pen(ThemeBrush.BorderDefault, 1, dashed: true);
            IPen same = first.Pen(ThemeBrush.BorderDefault, 1);
            IPen wider = first.Pen(ThemeBrush.BorderDefault, 2);
            ThemeBrushes.Reset();
            bool r = !ReferenceEquals(first, ThemeBrushes.For(ThemeVariant.Dark));
            return (ReferenceEquals(first, again), ReferenceEquals(first[ThemeBrush.TextPrimary], again[ThemeBrush.TextPrimary]),
                ReferenceEquals(pen, same), !ReferenceEquals(pen, wider),
                dash.DashStyle is not null && pen.DashStyle is null, r, pen.Thickness);
        });

        using (Assert.Multiple())
        {
            await Assert.That(samePalette).IsTrue();
            await Assert.That(sameBrush).IsTrue();
            await Assert.That(samePen).IsTrue();
            await Assert.That(otherWidth).IsTrue();
            await Assert.That(dashed).IsTrue();
            await Assert.That(reset).IsTrue();
            await Assert.That(width).IsEqualTo(1);
        }
    }

    [Test]
    public async Task A_control_gets_the_palette_of_its_own_theme_and_the_bundled_fonts()
    {
        (ThemeVariant light, ThemeVariant dark, string ui, string mono) = await HeadlessSession.Run(() =>
        {
            Application app = HeadlessSession.App;
            ThemeVariant? requested = app.RequestedThemeVariant;
            app.RequestedThemeVariant = ThemeVariant.Light;
            Border plain = new();
            Border scoped = new();
            Window window = new()
            {
                Content = new StackPanel { Children = { plain, new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = scoped } } },
            };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                ThemePalette p = ThemeBrushes.For(plain);
                return (p.Variant, ThemeBrushes.For(scoped).Variant, p.Ui.Name, p.Mono.Name);
            }
            finally
            {
                window.Close();
                app.RequestedThemeVariant = requested;
                Dispatcher.UIThread.RunJobs();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(light).IsEqualTo(ThemeVariant.Light);
            await Assert.That(dark).IsEqualTo(ThemeVariant.Dark);
            await Assert.That(ui).IsEqualTo(SelfCheck.UiFamily);
            await Assert.That(mono).IsEqualTo(SelfCheck.MonoFamily);
        }
    }

    [Test]
    [Arguments("Light")]
    [Arguments("Dark")]
    public async Task The_gap_stripes_paint_both_of_their_colours(string theme)
    {
        ThemeVariant variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        // tokens.json color.timeline.gap: repeating-linear-gradient(135deg, a 0 4px, b 4px 8px).
        (Color a, Color b) = variant == ThemeVariant.Dark
            ? (Color.Parse("#2A2E37"), Color.Parse("#171A21"))
            : (Color.Parse("#DCDFE5"), Color.Parse("#F8F9FB"));
        HashSet<Color> seen = await HeadlessSession.Run(() =>
        {
            Application app = HeadlessSession.App;
            ThemeVariant? requested = app.RequestedThemeVariant;
            app.RequestedThemeVariant = variant;
            ThemeBrushes.Reset();
            Window window = new() { Width = 64, Height = 64, Content = new GapSurface() };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
                HashSet<Color> s = [];
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++) s.Add(FramePixels.At(frame, x, y));
                }

                return s;
            }
            finally
            {
                window.Close();
                app.RequestedThemeVariant = requested;
                Dispatcher.UIThread.RunJobs();
            }
        });

        await Assert.That(seen).Contains(a);
        await Assert.That(seen).Contains(b);
    }

    /// <summary>A drawn control as the timeline will be one: it paints from its palette, not from a resource key.</summary>
    private sealed class GapSurface : Control
    {
        public override void Render(DrawingContext context) =>
            context.FillRectangle(ThemeBrushes.Get(this, ThemeBrush.TimelineGap), new Rect(Bounds.Size));
    }
}
