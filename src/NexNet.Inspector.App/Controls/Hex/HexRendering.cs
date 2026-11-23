using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.Hex;

/// <summary>
/// The hex view's line transformer: tints a byte line's offset column (tertiary), its dimmed cells (other-frame:
/// tertiary), its redaction dots (split.nexnet) and its ASCII column (secondary). Runs of cells with the same tint are
/// changed together, and the brush setters are cached per theme palette, so colourising allocates no closure per cell.
/// </summary>
internal sealed class HexColorizer(HexView view) : DocumentColorizingTransformer
{
    private readonly Action<VisualLineElement>?[] _paint = new Action<VisualLineElement>?[ThemeBrushes.Count];
    private ThemePalette? _palette;

    protected override void ColorizeLine(DocumentLine line)
    {
        int index = line.LineNumber - 1;
        if (!view.TryGetLine(index, out HexLine hexLine) || hexLine.Kind != HexLineKind.Bytes || line.Length == 0)
            return;

        ThemePalette palette = ThemeBrushes.For(view);
        if (!ReferenceEquals(palette, _palette))
        {
            _palette = palette;
            Array.Clear(_paint);
        }

        int digits = view.Digits;
        int start = line.Offset;
        int end = line.EndOffset;
        Change(start, Math.Min(end, start + digits), ThemeBrush.TextTertiary);

        ref readonly HexRow row = ref view.RowAt(index);
        ReadOnlySpan<HexCellRole> roles = row.Roles;
        ReadOnlySpan<HexCellMarks> marks = row.Marks;
        int runStart = -1;
        ThemeBrush runBrush = ThemeBrush.TextPrimary;
        for (int cell = row.First; cell <= row.First + row.Count; cell++)
        {
            ThemeBrush brush = ThemeBrush.TextPrimary;
            if (cell < row.First + row.Count)
            {
                if ((marks[cell] & HexCellMarks.Redacted) != 0)
                    brush = ThemeBrush.SplitNexnet;
                else if (roles[cell] == HexCellRole.OtherFrame)
                    brush = ThemeBrush.TextTertiary;
            }

            if (runStart >= 0 && (brush != runBrush || cell == row.First + row.Count))
            {
                if (runBrush != ThemeBrush.TextPrimary)
                {
                    int from = start + HexText.CellColumn(digits, runStart);
                    int to = start + HexText.CellColumn(digits, cell - 1) + 2;
                    Change(from, Math.Min(end, to), runBrush);
                }

                runStart = -1;
            }

            if (runStart < 0 && cell < row.First + row.Count)
            {
                runStart = cell;
                runBrush = brush;
            }
        }

        int ascii = start + HexText.AsciiColumn(digits);
        if (ascii < end)
            Change(ascii, end, ThemeBrush.TextSecondary);
    }

    private void Change(int start, int end, ThemeBrush brush)
    {
        if (end <= start)
            return;
        Action<VisualLineElement> paint = _paint[(int)brush] ??= Painter(_palette![brush]);
        ChangeLinePart(start, end, paint);
    }

    private static Action<VisualLineElement> Painter(IBrush brush) => element => element.TextRunProperties.SetForegroundBrush(brush);
}

/// <summary>
/// The hex view's element generator: an annotation line (read rule, frame starts) keeps its mono indent, so its text
/// lines up with the first cell, and the rest becomes one <see cref="HexAnnotationElement"/> set in the UI font at the
/// meta size, in split.transport (rules) or accent.trace (frame starts).
/// </summary>
internal sealed class HexAnnotationGenerator(HexView view) : VisualLineElementGenerator
{
    private ThemePalette? _palette;
    private Typeface _typeface;
    private double _size = 10.5;

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        ThemePalette palette = ThemeBrushes.For(view);
        if (!ReferenceEquals(palette, _palette))
        {
            _palette = palette;
            _typeface = new Typeface(palette.Ui);
            if (view.TryFindResource("FontSize.Meta", view.ActualThemeVariant, out object? size) && size is double meta)
                _size = meta;
        }
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
        int index = line.LineNumber - 1;
        if (!view.TryGetLine(index, out HexLine hexLine) || hexLine.Kind == HexLineKind.Bytes)
            return -1;
        int textStart = line.Offset + HexText.CellColumn(view.Digits, 0);
        return startOffset <= textStart && textStart < line.EndOffset ? textStart : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
        int index = line.LineNumber - 1;
        if (!view.TryGetLine(index, out HexLine hexLine) || view.AnnotationAt(index) is not { Length: > 0 } text)
            return null;
        int length = line.EndOffset - offset;
        if (length <= 0 || length != text.Length)
            return null;
        ThemeBrush brush = hexLine.Kind == HexLineKind.ReadRule ? ThemeBrush.SplitTransport : ThemeBrush.AccentTrace;
        return new HexAnnotationElement(text, _typeface, _size, _palette![brush]);
    }
}

/// <summary>An annotation's text as one run in the UI font (see <see cref="HexAnnotationGenerator"/>).</summary>
internal sealed class HexAnnotationElement(string text, Typeface typeface, double size, IBrush brush)
    : VisualLineElement(text.Length, text.Length)
{
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        TextRunProperties.SetTypeface(typeface);
        TextRunProperties.SetFontRenderingEmSize(size);
        TextRunProperties.SetForegroundBrush(brush);
        int skip = Math.Clamp(startVisualColumn - VisualColumn, 0, text.Length);
        return new TextCharacters(text.AsMemory(skip), TextRunProperties);
    }
}

/// <summary>
/// The hex view's background renderer: per byte line the cell fills by role (2 px corners, three characters wide so
/// neighbours abut), redacted stripes, the 2 px transport border on a read's first cell, and the 1.5 px inset rings of
/// the selected (accent.trace) and hovered (direction.inbound) cells; per read-rule line the 1.5 px split.transport rule.
/// </summary>
internal sealed class HexBackgroundRenderer(HexView view) : IBackgroundRenderer
{
    private const double Ring = 1.5;
    private const double Radius = 2;
    private const double ReadBorder = 2;

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid)
            return;

        ThemePalette palette = ThemeBrushes.For(view);
        double width = textView.Bounds.Width;
        int digits = view.Digits;
        foreach (VisualLine visual in textView.VisualLines)
        {
            int index = visual.FirstDocumentLine.LineNumber - 1;
            if (!view.TryGetLine(index, out HexLine line))
                continue;

            double top = visual.VisualTop - textView.VerticalOffset;
            double height = visual.Height;
            if (line.Kind == HexLineKind.ReadRule)
            {
                drawingContext.FillRectangle(palette[ThemeBrush.SplitTransport], new Rect(0, top, width, Ring));
                continue;
            }

            if (line.Kind != HexLineKind.Bytes)
                continue;

            double x0 = visual.GetVisualPosition(HexText.CellColumn(digits, 0), VisualYPosition.LineTop).X - textView.HorizontalOffset;
            double x1 = visual.GetVisualPosition(HexText.CellColumn(digits, 1), VisualYPosition.LineTop).X - textView.HorizontalOffset;
            double stride = x1 - x0;
            double left = x0 - (stride / (2 * HexText.CellStride));
            ref readonly HexRow row = ref view.RowAt(index);
            ReadOnlySpan<HexCellRole> roles = row.Roles;
            ReadOnlySpan<HexCellMarks> marks = row.Marks;
            for (int cell = row.First; cell < row.First + row.Count; cell++)
            {
                Rect rect = new(left + (cell * stride), top, stride, height);
                HexCellMarks mark = marks[cell];
                if ((mark & HexCellMarks.Unavailable) != 0)
                    continue;
                if ((mark & HexCellMarks.Redacted) != 0)
                    drawingContext.DrawRectangle(palette[ThemeBrush.HexRedacted], null, rect, Radius, Radius);
                else if (Fill(roles[cell]) is { } fill)
                    drawingContext.DrawRectangle(palette[fill], null, rect, Radius, Radius);

                if ((mark & HexCellMarks.ReadStart) != 0)
                    drawingContext.FillRectangle(palette[ThemeBrush.SplitTransport], new Rect(rect.X, top, ReadBorder, height));
                if ((mark & HexCellMarks.Selected) != 0)
                    drawingContext.DrawRectangle(null, palette.Pen(ThemeBrush.AccentTrace, Ring), rect.Deflate(Ring / 2), Radius, Radius);
                else if ((mark & HexCellMarks.Hovered) != 0)
                    drawingContext.DrawRectangle(null, palette.Pen(ThemeBrush.DirectionInbound, Ring), rect.Deflate(Ring / 2), Radius, Radius);
            }
        }
    }

    /// <summary>The token brush a role fills with; null for none.</summary>
    public static ThemeBrush? Fill(HexCellRole role) => role switch
    {
        HexCellRole.OtherFrame => ThemeBrush.HexOtherFrame,
        HexCellRole.Preamble => ThemeBrush.HexPreamble,
        HexCellRole.Type => ThemeBrush.HexType,
        HexCellRole.Length => ThemeBrush.HexLength,
        HexCellRole.PostHeader => ThemeBrush.HexPostHeader,
        HexCellRole.Body => ThemeBrush.HexBody,
        HexCellRole.BodySelected => ThemeBrush.HexBodySelected,
        _ => null,
    };
}
