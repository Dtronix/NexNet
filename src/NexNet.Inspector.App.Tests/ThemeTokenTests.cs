using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using NexNet.Inspector.App.Checks;

namespace NexNet.Inspector.App.Tests;

/// <summary>
/// Themes/Tokens.axaml holds every design token in both themes, and nothing else in the App holds a colour. The expected
/// keys are pinned here, generated once from the package's tokens.json (PLAN §5 key scheme): the test never reads the
/// package, so a token added to the design shows up as a deliberate edit to this list and to Tokens.axaml together.
/// </summary>
[NotInParallel("avalonia")]
public sealed partial class ThemeTokenTests
{
    /// <summary>color.* tokens with a Color.X and a Brush.X in each theme.</summary>
    private static readonly string[] Colours =
    [
        "Surface.App", "Surface.Chrome", "Surface.Panel", "Surface.Raised", "Surface.Lane", "Border.Default",
        "Border.Strong", "Border.Row", "Text.Primary", "Text.Secondary", "Text.Tertiary", "Text.OnAccent",
        "Direction.Inbound", "Direction.Outbound", "Accent.Trace", "Accent.TraceBand", "Split.Transport",
        "Split.Nexnet", "State.Selected", "Status.Ok", "Status.Recording", "Status.Error", "Timeline.Span",
        "Scroll.ThumbRest", "Scroll.ThumbNear", "Scroll.Rail", "Overlay.Scrim", "Hex.OtherFrame", "Hex.Type",
        "Hex.Length", "Hex.PostHeader", "Hex.Preamble", "Hex.Body", "Hex.BodySelected", "Warning.Surface",
        "Warning.Border", "Warning.Icon", "Diff.Band", "Heat.Low",
    ];

    /// <summary>The CSS stripe tokens: a tiled Brush.X per theme, no Color.</summary>
    private static readonly string[] Stripes = ["Timeline.Gap", "Hex.Redacted"];

    /// <summary>PLAN §5's added brushes (no design token), and the button theme's (I14).</summary>
    private static readonly string[] AddedBrushes =
    [
        "Scroll.ThumbHover", "Scroll.ThumbDrag", "Caption.CloseHover", "Caption.CloseGlyphHover", "Button.PrimaryHover",
        "Button.PrimaryPressed",
    ];

    /// <summary>The only translucent colours (tokens.json $description).</summary>
    private static readonly string[] Translucent = ["Accent.TraceBand", "Diff.Band"];

    private static readonly string[] Shadows = ["Shadow.Menu", "Shadow.Popover", "Shadow.Dialog", "Shadow.Palette"];

    /// <summary>typography.size, typography.line-height, spacing, size and opacity.</summary>
    private static readonly string[] Doubles =
    [
        "FontSize.LaneLabel", "FontSize.CapsLabel", "FontSize.Meta", "FontSize.Data", "FontSize.Small",
        "FontSize.Body", "FontSize.Title", "FontSize.Large", "FontSize.Display", "LineHeight.Dense",
        "LineHeight.RowMin", "LineHeight.RowMax", "LineHeight.Hex", "Spacing.1", "Spacing.2", "Spacing.3",
        "Spacing.4", "Spacing.6", "Spacing.0.5", "Spacing.1.5", "Spacing.2.5", "Spacing.3.5", "Size.TitleBar",
        "Size.CaptionButton", "Size.HeaderControl", "Size.PaneHeader", "Size.Lane", "Size.LaneExpanded",
        "Size.LaneGroup", "Size.LaneGutter", "Size.EventRow", "Size.LadderRow", "Size.PropertyRow", "Size.MenuItem",
        "Size.PaletteItem", "Size.Input", "Size.ButtonSm", "Size.ButtonMd", "Size.Inspector", "Size.CompareHeatRow",
        "Size.ComparePackedRow", "Size.CompareGutter", "Size.ScrollRail.Rest", "Size.ScrollRail.Near",
        "Size.ScrollRail.ThumbNear", "Size.ScrollRail.ThumbMin", "Size.ScrollRail.Proximity",
        "Opacity.Scrim.Palette", "Opacity.DimmedUntraced", "Opacity.InterleavedRow",
    ];

    private static readonly string[] Radii = ["Radius.Xs", "Radius.Sm", "Radius.Md", "Radius.Lg", "Radius.Xl", "Radius.2Xl", "Radius.Pill"];

    private static readonly string[] Weights = ["FontWeight.Regular", "FontWeight.Medium", "FontWeight.SemiBold", "FontWeight.Bold"];

    private static readonly string[] Fonts = ["Font.Ui", "Font.Mono"];

    private static readonly string[] Durations = ["Motion.RailWidth", "Motion.RailFade", "Motion.RailFadeReduced"];

    /// <summary>Fluent's accent and error colours, re-keyed to direction.inbound and status.error.</summary>
    private static readonly string[] FluentColours =
    [
        "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3", "SystemErrorTextColor",
    ];

    /// <summary>Fluent's app-wide brushes, each with the colour token it takes.</summary>
    private static readonly (string Key, string Token)[] FluentBrushes =
    [
        ("SystemRegionBrush", "Surface.App"), ("SystemControlBackgroundAltHighBrush", "Surface.App"),
        ("SystemControlPageBackgroundAltHighBrush", "Surface.App"), ("SystemControlForegroundBaseHighBrush", "Text.Primary"),
        ("SystemControlHighlightAccentBrush", "Direction.Inbound"), ("SystemControlBackgroundAccentBrush", "Direction.Inbound"),
        ("SystemControlForegroundAccentBrush", "Direction.Inbound"), ("SystemControlHyperlinkTextBrush", "Direction.Inbound"),
        ("SystemControlFocusVisualPrimaryBrush", "Direction.Inbound"), ("SystemControlHighlightListAccentLowBrush", "State.Selected"),
        ("SystemControlErrorTextForegroundBrush", "Status.Error"), ("SystemControlFocusVisualSecondaryBrush", "Surface.App"),
        ("TextControlBackground", "Surface.Raised"), ("TextControlBackgroundPointerOver", "Surface.Raised"),
        ("TextControlBackgroundFocused", "Surface.Raised"), ("TextControlBackgroundDisabled", "Surface.Raised"),
        ("TextControlBorderBrush", "Border.Default"), ("TextControlBorderBrushPointerOver", "Border.Strong"),
        ("TextControlBorderBrushFocused", "Direction.Inbound"), ("TextControlBorderBrushDisabled", "Border.Default"),
        ("TextControlForeground", "Text.Primary"), ("TextControlForegroundPointerOver", "Text.Primary"),
        ("TextControlForegroundFocused", "Text.Primary"), ("TextControlForegroundDisabled", "Text.Tertiary"),
        ("TextControlPlaceholderForeground", "Text.Tertiary"), ("TextControlPlaceholderForegroundPointerOver", "Text.Tertiary"),
        ("TextControlPlaceholderForegroundFocused", "Text.Tertiary"), ("TextControlPlaceholderForegroundDisabled", "Text.Tertiary"),
        ("TextControlSelectionHighlightColor", "State.Selected"),
    ];

    private static readonly ThemeVariant[] Variants = [ThemeVariant.Light, ThemeVariant.Dark];

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [GeneratedRegex("[\"'>]\\s*#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\\s*[\"'<]")]
    private static partial Regex HexLiteral();

    [GeneratedRegex("\\b(?:Background|Foreground|BorderBrush|Fill|Stroke|Color|Brush)=\"(?!\\{)[A-Za-z]+\"")]
    private static partial Regex NamedColourAttribute();

    [GeneratedRegex("\\b(?:Colors|Brushes)\\.[A-Z]|\\bColor\\.(?:FromRgb|FromArgb|FromUInt32)\\(\\s*(?:0x)?[0-9]|\\bColor\\.(?:Parse|TryParse)\\(\\s*\"")]
    private static partial Regex CodeColour();

    /// <summary>The App project's source directory, found above the test binaries (src/NexNet.slnx sits beside it).</summary>
    private static string AppSource()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "NexNet.slnx"))) return Path.Combine(d.FullName, "NexNet.Inspector.App");
        }

        throw new InvalidOperationException("NexNet.slnx not found above " + AppContext.BaseDirectory);
    }

    private static XElement TokensDocument() => XDocument.Load(Path.Combine(AppSource(), "Themes", "Tokens.axaml")).Root!;

    private static string[] ThemeKeys(XElement root, string theme) =>
    [
        .. root.Elements().Single(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries")
            .Elements().Single(e => (string?)e.Attribute(X + "Key") == theme)
            .Elements().Select(e => (string?)e.Attribute(X + "Key")).OfType<string>().Order(StringComparer.Ordinal),
    ];

    private static IEnumerable<string> PinnedThemeKeys() =>
        Colours.SelectMany(c => new[] { "Color." + c, "Brush." + c })
            .Concat(Stripes.Select(s => "Brush." + s))
            .Concat(AddedBrushes.Select(a => "Brush." + a))
            .Concat(Shadows)
            .Append("Opacity.Scrim.Dialog")
            .Concat(FluentColours)
            .Concat(FluentBrushes.Select(b => b.Key));

    private static IEnumerable<string> PinnedSharedKeys() =>
        Doubles.Concat(Radii).Concat(Weights).Concat(Fonts).Concat(Durations).Append("Motion.Ease");

    private static bool Resolves<T>(Application app, string key, ThemeVariant variant) =>
        app.TryGetResource(key, variant, out object? value) && value is T;

    private static Color ColourOf(Application app, string key, ThemeVariant variant) =>
        app.TryGetResource(key, variant, out object? value) && value is Color c ? c : throw new InvalidOperationException($"{key} ({variant}) is not a Color");

    [Test]
    public async Task Light_and_dark_define_the_same_keys()
    {
        XElement root = TokensDocument();
        string[] light = ThemeKeys(root, "Light");
        string[] dark = ThemeKeys(root, "Dark");

        await Assert.That(light.Length).IsGreaterThan(0);
        await Assert.That(light).IsEquivalentTo(dark, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Tokens_axaml_defines_exactly_the_pinned_keys()
    {
        XElement root = TokensDocument();
        string[] shared = [.. root.Elements().Select(e => (string?)e.Attribute(X + "Key")).OfType<string>().Order(StringComparer.Ordinal)];
        string[] theme = ThemeKeys(root, "Light");

        await Assert.That(shared).IsEquivalentTo(PinnedSharedKeys().Order(StringComparer.Ordinal), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(theme).IsEquivalentTo(PinnedThemeKeys().Order(StringComparer.Ordinal), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Every_token_resolves_in_both_themes_with_its_type()
    {
        List<string> faults = await HeadlessSession.Run(() =>
        {
            Application app = HeadlessSession.App;
            List<string> f = [];
            foreach (ThemeVariant v in Variants)
            {
                void Check<T>(IEnumerable<string> keys)
                {
                    foreach (string key in keys)
                    {
                        if (!Resolves<T>(app, key, v)) f.Add($"{key} ({v}) is missing or not a {typeof(T).Name}");
                    }
                }

                Check<Color>(Colours.Select(c => "Color." + c));
                Check<ISolidColorBrush>(Colours.Concat(AddedBrushes).Select(c => "Brush." + c));
                Check<DrawingBrush>(Stripes.Select(s => "Brush." + s));
                Check<BoxShadows>(Shadows);
                Check<double>(Doubles.Append("Opacity.Scrim.Dialog"));
                Check<CornerRadius>(Radii);
                Check<FontWeight>(Weights);
                Check<FontFamily>(Fonts);
                Check<TimeSpan>(Durations);
                Check<Easing>(["Motion.Ease"]);
                Check<Color>(FluentColours);
            }

            return f;
        });

        await Assert.That(faults).IsEmpty();
    }

    [Test]
    public async Task Brushes_carry_their_colour_and_only_the_two_bands_are_translucent()
    {
        List<string> faults = await HeadlessSession.Run(() =>
        {
            Application app = HeadlessSession.App;
            List<string> f = [];
            foreach (ThemeVariant v in Variants)
            {
                foreach (string c in Colours)
                {
                    Color colour = ColourOf(app, "Color." + c, v);
                    bool translucent = Translucent.Contains(c);
                    if ((colour.A < 255) != translucent) f.Add($"Color.{c} ({v}) alpha {colour.A}");
                    if (!app.TryGetResource("Brush." + c, v, out object? brush) || brush is not ISolidColorBrush solid || solid.Color != colour)
                        f.Add($"Brush.{c} ({v}) is not Color.{c}");
                }

                foreach ((string key, string token) in FluentBrushes)
                {
                    if (!app.TryGetResource(key, v, out object? brush) || brush is not ISolidColorBrush solid || solid.Color != ColourOf(app, "Color." + token, v))
                        f.Add($"{key} ({v}) is not Color.{token}");
                }
            }

            return f;
        });

        await Assert.That(faults).IsEmpty();
    }

    [Test]
    public async Task Pinned_values_match_the_design()
    {
        (Color appLight, Color appDark, Color bandLight, Color bandDark, Color hover, Color drag, double scrimDark, BoxShadows menu) = await HeadlessSession.Run(() =>
        {
            Application app = HeadlessSession.App;
            app.TryGetResource("Brush.Scroll.ThumbHover", ThemeVariant.Dark, out object? h);
            app.TryGetResource("Brush.Scroll.ThumbDrag", ThemeVariant.Light, out object? d);
            app.TryGetResource("Opacity.Scrim.Dialog", ThemeVariant.Dark, out object? s);
            app.TryGetResource("Shadow.Menu", ThemeVariant.Light, out object? m);
            return (ColourOf(app, "Color.Surface.App", ThemeVariant.Light), ColourOf(app, "Color.Surface.App", ThemeVariant.Dark),
                ColourOf(app, "Color.Accent.TraceBand", ThemeVariant.Light), ColourOf(app, "Color.Accent.TraceBand", ThemeVariant.Dark),
                ((ISolidColorBrush)h!).Color, ((ISolidColorBrush)d!).Color, (double)s!, (BoxShadows)m!);
        });

        using (Assert.Multiple())
        {
            await Assert.That(appLight).IsEqualTo(Color.Parse("#EEF0F3"));
            await Assert.That(appDark).IsEqualTo(Color.Parse("#111318"));
            // rgba(124,58,237,0.07) and rgba(195,166,255,0.09): alpha rounded to the nearest of 255 steps.
            await Assert.That(bandLight).IsEqualTo(Color.Parse("#127C3AED"));
            await Assert.That(bandDark).IsEqualTo(Color.Parse("#17C3A6FF"));
            // OQ7: hover = text.secondary, drag = direction.inbound.
            await Assert.That(hover).IsEqualTo(Color.Parse("#A0A7B4"));
            await Assert.That(drag).IsEqualTo(Color.Parse("#3C50F8"));
            await Assert.That(scrimDark).IsEqualTo(0.72);
            await Assert.That(menu.ToString()).IsEqualTo(BoxShadows.Parse("0 10 28 0 #2E000000").ToString());
        }
    }

    [Test]
    public async Task A_plain_window_takes_the_app_surface_through_fluent()
    {
        (Color light, Color lightToken, Color dark, Color darkToken, Color accent, Color inbound) = await HeadlessSession.Run(() =>
        {
            Application app = HeadlessSession.App;
            ThemeVariant? requested = app.RequestedThemeVariant;
            try
            {
                (Color, Color) Paint(ThemeVariant variant)
                {
                    app.RequestedThemeVariant = variant;
                    // No Background of its own: Fluent's window theme paints it, from the re-keyed palette.
                    Window window = new() { Width = 64, Height = 64 };
                    window.Show();
                    try
                    {
                        Dispatcher.UIThread.RunJobs();
                        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Dispatcher.UIThread.RunJobs();
                        using WriteableBitmap frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)
                            ?? throw new InvalidOperationException("no frame");
                        return (FramePixels.At(frame, 8, 8), ColourOf(app, "Color.Surface.App", variant));
                    }
                    finally
                    {
                        window.Close();
                    }
                }

                (Color l, Color lt) = Paint(ThemeVariant.Light);
                (Color d, Color dt) = Paint(ThemeVariant.Dark);
                return (l, lt, d, dt, ColourOf(app, "SystemAccentColor", ThemeVariant.Light), ColourOf(app, "Color.Direction.Inbound", ThemeVariant.Light));
            }
            finally
            {
                app.RequestedThemeVariant = requested;
                Dispatcher.UIThread.RunJobs();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(light).IsEqualTo(lightToken);
            await Assert.That(dark).IsEqualTo(darkToken);
            await Assert.That(accent).IsEqualTo(inbound);
        }
    }

    [Test]
    public async Task No_colour_literal_outside_the_tokens()
    {
        string root = AppSource();
        string[] allowed = ["Themes/Tokens.axaml", "Controls/Primitives/ThemeBrushes.cs"];
        List<string> offenders = [];
        foreach (string path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal)) continue;
            if (allowed.Contains(relative)) continue;
            bool xaml = relative.EndsWith(".axaml", StringComparison.Ordinal);
            bool code = relative.EndsWith(".cs", StringComparison.Ordinal);
            if (!xaml && !code) continue;

            string text = File.ReadAllText(path);
            if (HexLiteral().IsMatch(text) || (xaml && NamedColourAttribute().IsMatch(text)) || (code && CodeColour().IsMatch(text)))
                offenders.Add(relative);
        }

        await Assert.That(offenders).IsEmpty().Because("take colours as DynamicResource from Themes/Tokens.axaml, or through ThemeBrushes in drawn controls");
    }
}
