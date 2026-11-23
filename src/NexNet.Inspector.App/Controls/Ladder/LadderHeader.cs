using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.Ladder;

/// <summary>
/// The ladder's 20 px header: "Time ms", the two endpoints over their lifelines (the remote peer left, the tapped
/// endpoint right) and "Transport", on the rows' grid. Drawn; text is shaped when the names or the theme change.
/// </summary>
public sealed class LadderHeader : Control
{
    /// <summary>The header's height (ladder-row.md: 20 px).</summary>
    public const double HeaderHeight = 20;

    private const double TextSize = 10.5;

    private ThemePalette? _palette;
    private string _left = "";
    private string _right = "";
    private TextLayout? _time;
    private TextLayout? _transport;
    private TextLayout? _leftText;
    private TextLayout? _rightText;
    private string? _shapedLeft;
    private string? _shapedRight;

    /// <summary>Sets the endpoint names; redraws when they differ.</summary>
    public void SetEndpoints(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (string.Equals(left, _left, StringComparison.Ordinal) && string.Equals(right, _right, StringComparison.Ordinal)) return;
        _left = left;
        _right = right;
        InvalidateVisual();
    }

    /// <summary>The left endpoint's name.</summary>
    public string LeftEndpoint => _left;

    /// <summary>The right endpoint's name.</summary>
    public string RightEndpoint => _right;

    protected override Size MeasureOverride(Size availableSize) => new(0, HeaderHeight);

    public override void Render(DrawingContext context)
    {
        ThemePalette palette = ThemeBrushes.For(this);
        Shape(palette);
        double width = Bounds.Width;
        context.FillRectangle(palette[ThemeBrush.SurfacePanel], new Rect(0, 0, width, HeaderHeight));
        context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(0, HeaderHeight - 1, width, 1));

        double ladderLeft = LadderRowControl.BracketWidth + LadderRowControl.TimeWidth;
        double ladderRight = Math.Max(ladderLeft, width - LadderRowControl.RightPadding - LadderRowControl.OpsWidth);
        double span = ladderRight - ladderLeft;
        double left = Math.Round(ladderLeft + (span * LadderRowControl.LifelineInset));
        double right = Math.Round(ladderRight - (span * LadderRowControl.LifelineInset));
        double middle = (HeaderHeight - 1) / 2;

        _time!.Draw(context, new Point(LadderRowControl.BracketWidth, middle - (_time.Height / 2)));
        _transport!.Draw(context, new Point(ladderRight, middle - (_transport.Height / 2)));
        using (context.PushClip(new Rect(left, 0, Math.Max(0, right - left), HeaderHeight)))
        {
            _rightText!.Draw(context, new Point(right - _rightText.Width, middle - (_rightText.Height / 2)));
            _leftText!.Draw(context, new Point(left, middle - (_leftText.Height / 2)));
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
    }

    private void Shape(ThemePalette palette)
    {
        bool theme = !ReferenceEquals(palette, _palette);
        if (theme)
        {
            _time?.Dispose();
            _transport?.Dispose();
            _time = new TextLayout("Time ms", new Typeface(palette.Ui), TextSize, palette[ThemeBrush.TextSecondary]);
            _transport = new TextLayout("Transport", new Typeface(palette.Ui), TextSize, palette[ThemeBrush.TextSecondary]);
            _palette = palette;
        }

        if (theme || !ReferenceEquals(_shapedLeft, _left) || !ReferenceEquals(_shapedRight, _right))
        {
            _leftText?.Dispose();
            _rightText?.Dispose();
            Typeface strong = new(palette.Ui, FontStyle.Normal, FontWeight.SemiBold);
            _leftText = new TextLayout(_left, strong, TextSize, palette[ThemeBrush.TextPrimary]);
            _rightText = new TextLayout(_right, strong, TextSize, palette[ThemeBrush.TextPrimary]);
            _shapedLeft = _left;
            _shapedRight = _right;
        }
    }
}
