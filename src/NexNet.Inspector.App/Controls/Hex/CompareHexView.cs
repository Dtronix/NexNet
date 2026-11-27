using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.Core.Compare;

namespace NexNet.Inspector.App.Controls.Hex;

/// <summary>
/// The A ⇄ B hex view's text columns (mono): the document letter, two spaces, the frame offset (at least four
/// uppercase hex digits), three spaces, sixteen two-character cells one space apart. No ASCII column: the two rows
/// one above the other are what is read.
/// </summary>
public static class CompareHexText
{
    /// <summary>Column of the offset's first digit.</summary>
    public const int OffsetColumn = 3;

    /// <summary>Spaces between the offset and the first cell.</summary>
    public const int OffsetGap = 3;

    private const string Hex = "0123456789ABCDEF";

    /// <summary>Column of a cell's first hex digit.</summary>
    public static int CellColumn(int digits, int cell) => OffsetColumn + digits + OffsetGap + (cell * HexText.CellStride);

    /// <summary>Characters in a line.</summary>
    public static int LineLength(int digits) => CellColumn(digits, HexLayout.BytesPerRow) - 1;

    /// <summary>A document's letter.</summary>
    public static char Letter(CompareSide side) => side == CompareSide.A ? 'A' : 'B';

    /// <summary>Writes a line into <paramref name="destination"/>, which must hold <see cref="LineLength"/> chars.</summary>
    public static void Format(CompareSide side, in HexRow row, int digits, Span<char> destination)
    {
        destination.Slice(0, LineLength(digits)).Fill(' ');
        destination[0] = Letter(side);
        long offset = row.Row;
        for (int i = OffsetColumn + digits - 1; i >= OffsetColumn; i--)
        {
            destination[i] = Hex[(int)(offset & 0xF)];
            offset >>= 4;
        }

        ReadOnlySpan<byte> bytes = row.Bytes;
        ReadOnlySpan<HexCellMarks> marks = row.Marks;
        for (int cell = row.First; cell < row.First + row.Count; cell++)
        {
            if ((marks[cell] & HexCellMarks.Unavailable) != 0)
                continue;
            int at = CellColumn(digits, cell);
            destination[at] = Hex[bytes[cell] >> 4];
            destination[at + 1] = Hex[bytes[cell] & 0xF];
        }
    }
}

/// <summary>
/// The A ⇄ B hex view (PKG compare, "Hex · A ⇄ B"; controls/hex-view on AvaloniaEdit): a <see cref="CompareHexModel"/>'s
/// two frames as interleaved 16-byte rows tagged A (file A's blue) and B (file B's orange), with the hex view's header
/// fills, differing bytes in split.nexnet over diff.band, and the selected field ringed in accent.trace on both sides.
/// Clicking a byte picks its field (<see cref="FieldPicked"/>), so the field diff table follows.
/// </summary>
/// <remarks>
/// Like <see cref="HexView"/>, the text document holds only the lines on screen, regenerated from the model when the
/// window moves, with this control's own scroll bar over the model's line count: a 64 KB frame on each side never
/// becomes one large text.
/// </remarks>
public sealed class CompareHexView : UserControl
{
    /// <summary>The hint shown when there are no bytes.</summary>
    public const string EmptyText = "No bytes to compare · pick a field";

    /// <summary>Lines scrolled per wheel notch.</summary>
    public const int WheelLines = 3;

    private const double DefaultLineHeight = 20;

    public static readonly StyledProperty<CompareHexModel?> ModelProperty =
        AvaloniaProperty.Register<CompareHexView, CompareHexModel?>(nameof(Model));

    private readonly TextView _textView;
    private readonly TextDocument _document;
    private readonly ScrollBar _scrollBar;
    private readonly TextBlock _hint;
    private int[] _lineIndex = new int[64];
    private HexRow[] _rows = new HexRow[64];
    private char[] _buffer = new char[4096];
    private int _windowCount;
    private int _top;
    private bool _attached;
    private bool _settingScroll;
    private CompareHexModel? _model;
    private double _lineHeight = DefaultLineHeight;

    public CompareHexView()
    {
        _document = new TextDocument();
        _document.UndoStack.SizeLimit = 0;
        _textView = new TextView
        {
            Document = _document,
            Options = new TextEditorOptions
            {
                EnableHyperlinks = false,
                EnableEmailHyperlinks = false,
                ShowBoxForControlCharacters = false,
                AllowScrollBelowDocument = false,
                EnableImeSupport = false,
            },
            ClipToBounds = true,
        };
        _textView.LineTransformers.Add(new CompareHexColorizer(this));
        _textView.BackgroundRenderers.Add(new CompareHexBackgroundRenderer(this));

        _scrollBar = new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, SmallChange = 1 };
        // The app's rail, as in HexView: overlaid on the text's right padding.
        ScrollRail.Attach(_scrollBar, this);
        _scrollBar.ValueChanged += (_, e) =>
        {
            if (!_settingScroll)
                ScrollToLine((int)Math.Round(e.NewValue));
        };

        _hint = new TextBlock
        {
            Text = EmptyText,
            Margin = new Thickness(10, 20, 16, 20),
            VerticalAlignment = VerticalAlignment.Top,
        };
        _hint.Bind(TextBlock.FontFamilyProperty, _hint.GetResourceObservable("Font.Ui"));
        _hint.Bind(TextBlock.FontSizeProperty, _hint.GetResourceObservable("FontSize.Body"));
        _hint.Bind(TextBlock.ForegroundProperty, _hint.GetResourceObservable("Brush.Text.Tertiary"));

        Grid grid = new();
        grid.Children.Add(new Border { Padding = new Thickness(10, 6, 16, 6), Child = _textView });
        grid.Children.Add(_hint);
        grid.Children.Add(_scrollBar);
        Content = grid;

        Focusable = true;
        ClipToBounds = true;
        Bind(BackgroundProperty, this.GetResourceObservable("Brush.Surface.Panel"));
        Bind(ForegroundProperty, this.GetResourceObservable("Brush.Text.Primary"));
        Bind(FontFamilyProperty, this.GetResourceObservable("Font.Mono"));
        Bind(FontSizeProperty, this.GetResourceObservable("FontSize.Small"));
        AutomationProperties.SetName(this, "Hex A and B");

        _textView.PointerPressed += OnTextPointerPressed;
        // The window's line count follows the text view's own height, which is laid out only once it shows bytes.
        _textView.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged)
                Rebuild();
        };
    }

    /// <summary>Raised when a click picks a byte's field: the row of <see cref="CompareHexModel.Diff"/>.</summary>
    public event Action<int>? FieldPicked;

    /// <summary>The bytes shown.</summary>
    public CompareHexModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>The first line shown.</summary>
    public int TopLine => _top;

    /// <summary>Lines in the window (the text document's line count).</summary>
    public int WindowLineCount => _windowCount;

    /// <summary>Whole lines that fit.</summary>
    public int VisibleLines => Math.Max(1, (int)Math.Floor(Math.Max(0, _textView.Bounds.Height) / _lineHeight));

    /// <summary>True while the empty hint shows.</summary>
    public bool ShowsHint => _hint.IsVisible;

    /// <summary>The view's own scroll bar (the rail).</summary>
    public ScrollBar ScrollBar => _scrollBar;

    /// <summary>The AvaloniaEdit text view.</summary>
    public TextView TextView => _textView;

    /// <summary>The window's text (tests read it).</summary>
    public string Text => _document.Text;

    /// <summary>The offset column's digit count.</summary>
    internal int Digits => _model?.Digits ?? CompareHexModel.MinDigits;

    /// <summary>The model line of a window line; false outside the window.</summary>
    internal bool TryGetLine(int index, out CompareHexLine line)
    {
        if ((uint)index < (uint)_windowCount && _model is { } model)
        {
            line = model.GetLine(_lineIndex[index]);
            return true;
        }

        line = default;
        return false;
    }

    /// <summary>The row of a window line.</summary>
    internal ref readonly HexRow RowAt(int index) => ref _rows[index];

    /// <summary>Scrolls so <paramref name="line"/> is the first line shown (clamped).</summary>
    public void ScrollToLine(int line)
    {
        int top = Math.Clamp(line, 0, MaxTop());
        if (top == _top && _windowCount > 0)
            return;
        _top = top;
        Rebuild();
    }

    /// <summary>Scrolls so a document's row is visible.</summary>
    public void Reveal(CompareSide side, int row)
    {
        if (_model is null)
            return;
        int line = _model.LineOf(side, row);
        int visible = VisibleLines;
        if (line >= 0 && (line < _top || line >= _top + visible))
            ScrollToLine(line - Math.Min(2, visible / 4));
    }

    /// <summary>The byte under a point of the text view; false over letters, offsets and blank cells.</summary>
    public bool TryHitTest(Point point, out CompareSide side, out int position)
    {
        side = default;
        position = -1;
        if (!_textView.VisualLinesValid)
            return false;
        VisualLine? visual = _textView.GetVisualLineFromVisualTop(point.Y + _textView.VerticalOffset);
        if (visual is null)
            return false;
        int index = visual.FirstDocumentLine.LineNumber - 1;
        if (!TryGetLine(index, out CompareHexLine line))
            return false;

        int digits = Digits;
        double cell0 = visual.GetVisualPosition(CompareHexText.CellColumn(digits, 0), VisualYPosition.LineTop).X;
        double cell1 = visual.GetVisualPosition(CompareHexText.CellColumn(digits, 1), VisualYPosition.LineTop).X;
        double stride = cell1 - cell0;
        double left = cell0 - (stride / (2 * HexText.CellStride));
        double x = point.X + _textView.HorizontalOffset;
        if (stride <= 0 || x < left || x >= left + (stride * HexLayout.BytesPerRow))
            return false;
        int cell = (int)((x - left) / stride);
        ref readonly HexRow row = ref _rows[index];
        if (!row.Shows(cell))
            return false;
        side = line.Side;
        position = (int)row.Row + cell;
        return true;
    }

    /// <summary>Picks the field at a byte, as a click does.</summary>
    /// <returns>The picked row, or -1 when the byte belongs to no field.</returns>
    public int PickAt(CompareSide side, int position)
    {
        int row = _model?.FindRow(side, position) ?? -1;
        if (row >= 0)
            FieldPicked?.Invoke(row);
        return row;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            if (_attached)
                Subscribe();
        }
        else if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            ApplyLineHeight();
            Redraw();
        }
        else if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            ApplyLineHeight();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ApplyLineHeight();
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Unsubscribe();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Rebuild();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Handled || e.Delta.Y == 0)
            return;
        ScrollToLine(_top - (int)Math.Round(e.Delta.Y * WheelLines));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;
        int page = Math.Max(1, VisibleLines - 1);
        int? to = e.Key switch
        {
            Key.Up => _top - 1,
            Key.Down => _top + 1,
            Key.PageUp => _top - page,
            Key.PageDown => _top + page,
            Key.Home => 0,
            Key.End => int.MaxValue / 2,
            _ => null,
        };
        if (to is not { } line)
            return;
        ScrollToLine(line);
        e.Handled = true;
    }

    private void Subscribe()
    {
        Unsubscribe();
        _model = Model;
        if (_model is not null)
            _model.Changed += OnModelChanged;
        _top = 0;
        Rebuild();
    }

    private void Unsubscribe()
    {
        if (_model is not null)
            _model.Changed -= OnModelChanged;
        _model = null;
    }

    private void OnModelChanged()
    {
        _top = 0;
        Rebuild();
        // The ringed field is what the pick asked to see: keep it on screen.
        if (_model is { } model)
        {
            for (int s = 0; s < 2; s++)
            {
                (int start, int end) = model.GetSelected((CompareSide)s);
                if (end > start)
                {
                    Reveal((CompareSide)s, start / HexLayout.BytesPerRow);
                    break;
                }
            }
        }
    }

    private void OnTextPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(_textView);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        Focus();
        if (TryHitTest(point.Position, out CompareSide side, out int position) && PickAt(side, position) >= 0)
            e.Handled = true;
    }

    private int MaxTop() => Math.Max(0, (_model?.LineCount ?? 0) - VisibleLines);

    private void ApplyLineHeight()
    {
        if (this.TryFindResource("LineHeight.Hex", ActualThemeVariant, out object? value) && value is double height && height > 0)
            _lineHeight = height;

        // AvaloniaEdit spaces lines by the font's natural height times LineHeightFactor; the design wants 20 px rows.
        double factor = _textView.Options.LineHeightFactor;
        double natural = _textView.DefaultLineHeight / Math.Max(0.01, factor);
        if (natural > 1 && Math.Abs((natural * factor) - _lineHeight) > 0.01)
            _textView.Options.LineHeightFactor = _lineHeight / natural;
    }

    /// <summary>Regenerates the window from the model and redraws.</summary>
    public void Rebuild()
    {
        CompareHexModel? model = _model;
        if (model is null || !model.HasBytes)
        {
            _windowCount = 0;
            if (_document.TextLength > 0)
                _document.Text = string.Empty;
            SetScroll(0, empty: true);
            return;
        }

        int visible = VisibleLines + 1;
        if (_lineIndex.Length < visible)
        {
            int size = Math.Max(visible, _lineIndex.Length * 2);
            Array.Resize(ref _lineIndex, size);
            Array.Resize(ref _rows, size);
        }

        _top = Math.Clamp(_top, 0, MaxTop());
        int count = Math.Min(visible, model.LineCount - _top);
        int digits = model.Digits;
        int lineLength = CompareHexText.LineLength(digits);
        int length = 0;
        int needed = count * (lineLength + 1);
        if (_buffer.Length < needed)
            Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
        for (int i = 0; i < count; i++)
        {
            int line = _top + i;
            _lineIndex[i] = line;
            model.BuildRow(line, ref _rows[i]);
            if (i > 0)
                _buffer[length++] = '\n';
            CompareHexText.Format(model.GetLine(line).Side, _rows[i], digits, _buffer.AsSpan(length, lineLength));
            length += lineLength;
        }

        _windowCount = count;
        string current = _document.Text;
        if (current.Length != length || !current.AsSpan().SequenceEqual(_buffer.AsSpan(0, length)))
            _document.Text = new string(_buffer, 0, length);
        Redraw();
        SetScroll(model.LineCount, empty: false);
    }

    private void SetScroll(int lines, bool empty)
    {
        _hint.IsVisible = empty;
        _textView.IsVisible = !empty;
        _settingScroll = true;
        try
        {
            int visible = VisibleLines;
            _scrollBar.Maximum = Math.Max(0, lines - visible);
            _scrollBar.ViewportSize = visible;
            _scrollBar.LargeChange = Math.Max(1, visible - 1);
            _scrollBar.Value = Math.Min(_top, _scrollBar.Maximum);
            _scrollBar.IsVisible = !empty && lines > visible;
        }
        finally
        {
            _settingScroll = false;
        }
    }

    private void Redraw()
    {
        _textView.Redraw();
        _textView.InvalidateVisual();
    }
}

/// <summary>
/// The A ⇄ B view's line transformer: the document letter in its file colour (A direction.inbound, B
/// direction.outbound), the offset in tertiary, differing cells in split.nexnet. Brush setters are cached per palette.
/// </summary>
internal sealed class CompareHexColorizer(CompareHexView view) : DocumentColorizingTransformer
{
    private readonly Action<VisualLineElement>?[] _paint = new Action<VisualLineElement>?[ThemeBrushes.Count];
    private ThemePalette? _palette;

    protected override void ColorizeLine(DocumentLine line)
    {
        int index = line.LineNumber - 1;
        if (!view.TryGetLine(index, out CompareHexLine hexLine) || line.Length == 0)
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
        Change(start, Math.Min(end, start + 1), hexLine.Side == CompareSide.A ? ThemeBrush.DirectionInbound : ThemeBrush.DirectionOutbound);
        Change(start + CompareHexText.OffsetColumn, Math.Min(end, start + CompareHexText.OffsetColumn + digits), ThemeBrush.TextTertiary);

        ref readonly HexRow row = ref view.RowAt(index);
        ReadOnlySpan<HexCellMarks> marks = row.Marks;
        int runStart = -1;
        for (int cell = row.First; cell <= row.First + row.Count; cell++)
        {
            bool differs = cell < row.First + row.Count && (marks[cell] & HexCellMarks.Differs) != 0;
            if (differs && runStart < 0)
                runStart = cell;
            if (!differs && runStart >= 0)
            {
                int from = start + CompareHexText.CellColumn(digits, runStart);
                int to = start + CompareHexText.CellColumn(digits, cell - 1) + 2;
                Change(from, Math.Min(end, to), ThemeBrush.SplitNexnet);
                runStart = -1;
            }
        }
    }

    private void Change(int start, int end, ThemeBrush brush)
    {
        if (end <= start)
            return;
        Action<VisualLineElement> paint = _paint[(int)brush] ??= Painter(_palette![brush]);
        ChangeLinePart(start, end, paint);
    }

    private static Action<VisualLineElement> Painter(Avalonia.Media.IBrush brush) => element => element.TextRunProperties.SetForegroundBrush(brush);
}

/// <summary>
/// The A ⇄ B view's background renderer: per line the hex view's header fills by role (2 px corners, three characters
/// wide so neighbours abut), diff.band under differing body cells, and the 1.5 px accent.trace ring of the selected field.
/// </summary>
internal sealed class CompareHexBackgroundRenderer(CompareHexView view) : IBackgroundRenderer
{
    private const double Ring = 1.5;
    private const double Radius = 2;

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, Avalonia.Media.DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid)
            return;

        ThemePalette palette = ThemeBrushes.For(view);
        int digits = view.Digits;
        foreach (VisualLine visual in textView.VisualLines)
        {
            int index = visual.FirstDocumentLine.LineNumber - 1;
            if (!view.TryGetLine(index, out _))
                continue;

            double top = visual.VisualTop - textView.VerticalOffset;
            double height = visual.Height;
            double x0 = visual.GetVisualPosition(CompareHexText.CellColumn(digits, 0), VisualYPosition.LineTop).X - textView.HorizontalOffset;
            double x1 = visual.GetVisualPosition(CompareHexText.CellColumn(digits, 1), VisualYPosition.LineTop).X - textView.HorizontalOffset;
            double stride = x1 - x0;
            double left = x0 - (stride / (2 * HexText.CellStride));
            ref readonly HexRow row = ref view.RowAt(index);
            ReadOnlySpan<HexCellRole> roles = row.Roles;
            ReadOnlySpan<HexCellMarks> marks = row.Marks;
            for (int cell = row.First; cell < row.First + row.Count; cell++)
            {
                HexCellMarks mark = marks[cell];
                if ((mark & HexCellMarks.Unavailable) != 0)
                    continue;
                Rect rect = new(left + (cell * stride), top, stride, height);
                if (HexBackgroundRenderer.Fill(roles[cell]) is { } fill)
                    drawingContext.DrawRectangle(palette[fill], null, rect, Radius, Radius);
                else if ((mark & HexCellMarks.Differs) != 0)
                    drawingContext.DrawRectangle(palette[ThemeBrush.DiffBand], null, rect, Radius, Radius);
                if ((mark & HexCellMarks.Selected) != 0)
                    drawingContext.DrawRectangle(null, palette.Pen(ThemeBrush.AccentTrace, Ring), rect.Deflate(Ring / 2), Radius, Radius);
            }
        }
    }
}
