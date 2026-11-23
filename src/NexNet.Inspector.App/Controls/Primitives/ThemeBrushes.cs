using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace NexNet.Inspector.App.Controls.Primitives;

/// <summary>The token brushes a drawn control (timeline, ladder, hex) can paint with: one per <c>Brush.*</c> key in Themes/Tokens.axaml.</summary>
public enum ThemeBrush : byte
{
    /// <summary>color.surface.app</summary>
    SurfaceApp,

    /// <summary>color.surface.chrome</summary>
    SurfaceChrome,

    /// <summary>color.surface.panel</summary>
    SurfacePanel,

    /// <summary>color.surface.raised</summary>
    SurfaceRaised,

    /// <summary>color.surface.lane</summary>
    SurfaceLane,

    /// <summary>color.border.default</summary>
    BorderDefault,

    /// <summary>color.border.strong</summary>
    BorderStrong,

    /// <summary>color.border.row</summary>
    BorderRow,

    /// <summary>color.text.primary</summary>
    TextPrimary,

    /// <summary>color.text.secondary</summary>
    TextSecondary,

    /// <summary>color.text.tertiary</summary>
    TextTertiary,

    /// <summary>color.text.on-accent</summary>
    TextOnAccent,

    /// <summary>color.direction.inbound</summary>
    DirectionInbound,

    /// <summary>color.direction.outbound</summary>
    DirectionOutbound,

    /// <summary>color.accent.trace</summary>
    AccentTrace,

    /// <summary>color.accent.trace-band</summary>
    AccentTraceBand,

    /// <summary>color.split.transport</summary>
    SplitTransport,

    /// <summary>color.split.nexnet</summary>
    SplitNexnet,

    /// <summary>color.state.selected</summary>
    StateSelected,

    /// <summary>color.status.ok</summary>
    StatusOk,

    /// <summary>color.status.recording</summary>
    StatusRecording,

    /// <summary>color.status.error</summary>
    StatusError,

    /// <summary>color.timeline.span</summary>
    TimelineSpan,

    /// <summary>color.timeline.gap: the 135-degree stripe tile.</summary>
    TimelineGap,

    /// <summary>color.scroll.thumb-rest</summary>
    ScrollThumbRest,

    /// <summary>color.scroll.thumb-near</summary>
    ScrollThumbNear,

    /// <summary>color.scroll.rail</summary>
    ScrollRail,

    /// <summary>color.overlay.scrim</summary>
    OverlayScrim,

    /// <summary>color.hex.other-frame</summary>
    HexOtherFrame,

    /// <summary>color.hex.type</summary>
    HexType,

    /// <summary>color.hex.length</summary>
    HexLength,

    /// <summary>color.hex.post-header</summary>
    HexPostHeader,

    /// <summary>color.hex.preamble</summary>
    HexPreamble,

    /// <summary>color.hex.body</summary>
    HexBody,

    /// <summary>color.hex.body-selected</summary>
    HexBodySelected,

    /// <summary>color.hex.redacted: the 135-degree stripe tile.</summary>
    HexRedacted,

    /// <summary>color.warning.surface</summary>
    WarningSurface,

    /// <summary>color.warning.border</summary>
    WarningBorder,

    /// <summary>color.warning.icon</summary>
    WarningIcon,

    /// <summary>color.diff.band</summary>
    DiffBand,

    /// <summary>color.heat.low</summary>
    HeatLow,

    /// <summary>The scroll-rail thumb under the pointer (OQ7: text.secondary).</summary>
    ScrollThumbHover,

    /// <summary>The scroll-rail thumb while dragged (OQ7: direction.inbound).</summary>
    ScrollThumbDrag,

    /// <summary>The close caption button on hover (#C42B1C).</summary>
    CaptionCloseHover,
}

/// <summary>
/// Token brushes, pens and fonts for the drawn controls (<c>Render</c>, no template). Each theme variant gets one
/// <see cref="ThemePalette"/>, built on first use by resolving every <see cref="ThemeBrush"/> at once; a control asks
/// for the palette of its own <c>ActualThemeVariant</c> in <c>Render</c>, so a theme switch moves it to the other
/// palette and a frame never looks a key up by string. Token values never change within a variant, so a palette never
/// goes stale; <see cref="Reset"/> exists for the one case they do (the token dictionaries replaced).
/// UI thread only, like the controls that use it.
/// </summary>
public static class ThemeBrushes
{
    /// <summary>The number of <see cref="ThemeBrush"/> values.</summary>
    public const int Count = (int)ThemeBrush.CaptionCloseHover + 1;

    private static ThemePalette? _light;
    private static ThemePalette? _dark;
    private static Dictionary<ThemeVariant, ThemePalette>? _custom;

    /// <summary>The resource key of <paramref name="brush"/> in Themes/Tokens.axaml.</summary>
    public static string Key(ThemeBrush brush) => brush switch
    {
        ThemeBrush.SurfaceApp => "Brush.Surface.App",
        ThemeBrush.SurfaceChrome => "Brush.Surface.Chrome",
        ThemeBrush.SurfacePanel => "Brush.Surface.Panel",
        ThemeBrush.SurfaceRaised => "Brush.Surface.Raised",
        ThemeBrush.SurfaceLane => "Brush.Surface.Lane",
        ThemeBrush.BorderDefault => "Brush.Border.Default",
        ThemeBrush.BorderStrong => "Brush.Border.Strong",
        ThemeBrush.BorderRow => "Brush.Border.Row",
        ThemeBrush.TextPrimary => "Brush.Text.Primary",
        ThemeBrush.TextSecondary => "Brush.Text.Secondary",
        ThemeBrush.TextTertiary => "Brush.Text.Tertiary",
        ThemeBrush.TextOnAccent => "Brush.Text.OnAccent",
        ThemeBrush.DirectionInbound => "Brush.Direction.Inbound",
        ThemeBrush.DirectionOutbound => "Brush.Direction.Outbound",
        ThemeBrush.AccentTrace => "Brush.Accent.Trace",
        ThemeBrush.AccentTraceBand => "Brush.Accent.TraceBand",
        ThemeBrush.SplitTransport => "Brush.Split.Transport",
        ThemeBrush.SplitNexnet => "Brush.Split.Nexnet",
        ThemeBrush.StateSelected => "Brush.State.Selected",
        ThemeBrush.StatusOk => "Brush.Status.Ok",
        ThemeBrush.StatusRecording => "Brush.Status.Recording",
        ThemeBrush.StatusError => "Brush.Status.Error",
        ThemeBrush.TimelineSpan => "Brush.Timeline.Span",
        ThemeBrush.TimelineGap => "Brush.Timeline.Gap",
        ThemeBrush.ScrollThumbRest => "Brush.Scroll.ThumbRest",
        ThemeBrush.ScrollThumbNear => "Brush.Scroll.ThumbNear",
        ThemeBrush.ScrollRail => "Brush.Scroll.Rail",
        ThemeBrush.OverlayScrim => "Brush.Overlay.Scrim",
        ThemeBrush.HexOtherFrame => "Brush.Hex.OtherFrame",
        ThemeBrush.HexType => "Brush.Hex.Type",
        ThemeBrush.HexLength => "Brush.Hex.Length",
        ThemeBrush.HexPostHeader => "Brush.Hex.PostHeader",
        ThemeBrush.HexPreamble => "Brush.Hex.Preamble",
        ThemeBrush.HexBody => "Brush.Hex.Body",
        ThemeBrush.HexBodySelected => "Brush.Hex.BodySelected",
        ThemeBrush.HexRedacted => "Brush.Hex.Redacted",
        ThemeBrush.WarningSurface => "Brush.Warning.Surface",
        ThemeBrush.WarningBorder => "Brush.Warning.Border",
        ThemeBrush.WarningIcon => "Brush.Warning.Icon",
        ThemeBrush.DiffBand => "Brush.Diff.Band",
        ThemeBrush.HeatLow => "Brush.Heat.Low",
        ThemeBrush.ScrollThumbHover => "Brush.Scroll.ThumbHover",
        ThemeBrush.ScrollThumbDrag => "Brush.Scroll.ThumbDrag",
        ThemeBrush.CaptionCloseHover => "Brush.Caption.CloseHover",
        _ => throw new ArgumentOutOfRangeException(nameof(brush), brush, null),
    };

    /// <summary>The palette for <paramref name="owner"/>'s theme variant (the application's when it has none yet).</summary>
    public static ThemePalette For(StyledElement owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ThemeVariant? variant = (owner as IThemeVariantHost)?.ActualThemeVariant ?? Application.Current?.ActualThemeVariant;
        return For(variant ?? ThemeVariant.Light);
    }

    /// <summary>The palette for <paramref name="variant"/>, built on first use.</summary>
    public static ThemePalette For(ThemeVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (variant == ThemeVariant.Light) return _light ??= new ThemePalette(variant);
        if (variant == ThemeVariant.Dark) return _dark ??= new ThemePalette(variant);

        _custom ??= [];
        if (!_custom.TryGetValue(variant, out ThemePalette? palette))
        {
            palette = new ThemePalette(variant);
            _custom[variant] = palette;
        }

        return palette;
    }

    /// <summary>Shorthand for <c>For(owner)[brush]</c>.</summary>
    public static IBrush Get(StyledElement owner, ThemeBrush brush) => For(owner)[brush];

    /// <summary>Shorthand for <c>For(owner).Pen(brush, width, dashed)</c>.</summary>
    public static IPen Pen(StyledElement owner, ThemeBrush brush, double width, bool dashed = false) => For(owner).Pen(brush, width, dashed);

    /// <summary>Drops every palette, so the next request resolves the tokens again.</summary>
    public static void Reset()
    {
        _light = null;
        _dark = null;
        _custom = null;
    }
}

/// <summary>
/// One theme variant's resolved tokens: every <see cref="ThemeBrush"/> (immutable where the brush allows it, so the
/// renderer can share it across frames), the two font families, and pens made on first request per brush, width and
/// dash. A token missing from the resources resolves to magenta, which a capture shows at once (ThemeBrushesTests fails
/// on it first).
/// </summary>
public sealed class ThemePalette
{
    private static readonly ImmutableDashStyle Dash = new([2, 1.6], 0);

    private readonly IBrush[] _brushes = new IBrush[ThemeBrushes.Count];
    private readonly Dictionary<(ThemeBrush Brush, double Width, bool Dashed), IPen> _pens = [];

    internal ThemePalette(ThemeVariant variant)
    {
        Variant = variant;
        Application? application = Application.Current;
        for (int i = 0; i < _brushes.Length; i++)
        {
            object? value = null;
            bool found = application is not null && application.TryGetResource(ThemeBrushes.Key((ThemeBrush)i), variant, out value);
            _brushes[i] = !found ? Brushes.Magenta : value switch
            {
                IImmutableBrush immutable => immutable,
                ISolidColorBrush solid => solid.ToImmutable(),
                IBrush brush => brush,
                _ => Brushes.Magenta,
            };
        }

        Ui = Font(application, "Font.Ui", variant);
        Mono = Font(application, "Font.Mono", variant);
    }

    /// <summary>The theme variant these tokens were resolved for.</summary>
    public ThemeVariant Variant { get; }

    /// <summary>typography.family.ui, for text a control draws itself (<see cref="FontFamily.Default"/> is the platform's, not the bundled font).</summary>
    public FontFamily Ui { get; }

    /// <summary>typography.family.mono: times, byte counts, offsets, hex, ids.</summary>
    public FontFamily Mono { get; }

    /// <summary>The brush for <paramref name="brush"/>.</summary>
    public IBrush this[ThemeBrush brush] => _brushes[(int)brush];

    /// <summary>A pen of <paramref name="brush"/>, cached per width and dash so a frame allocates none.</summary>
    public IPen Pen(ThemeBrush brush, double width, bool dashed = false)
    {
        if (_pens.TryGetValue((brush, width, dashed), out IPen? pen)) return pen;
        IBrush fill = _brushes[(int)brush];
        pen = fill is IImmutableBrush immutable
            ? new ImmutablePen(immutable, width, dashed ? Dash : null, PenLineCap.Flat)
            : new Pen(fill, width, dashed ? Dash : null, PenLineCap.Flat);
        _pens[(brush, width, dashed)] = pen;
        return pen;
    }

    private static FontFamily Font(Application? application, string key, ThemeVariant variant) =>
        application is not null && application.TryGetResource(key, variant, out object? value) && value is FontFamily family ? family : FontFamily.Default;
}
