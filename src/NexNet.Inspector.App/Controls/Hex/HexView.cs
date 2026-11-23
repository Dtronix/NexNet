using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Controls.Hex;

/// <summary>
/// The hex stream (PKG controls/hex-view): an AvaloniaEdit <see cref="TextView"/> over a <see cref="HexLayout"/>, 16
/// bytes per row (offset, hex cells, ASCII), with field fills, redacted stripes, read rules and frame annotations, the
/// selected field ring (accent.trace) and the hovered field ring (direction.inbound). Hovering a byte publishes its
/// decoded field on the <see cref="SyncBus"/> (the property tree lights it), a field hovered in the tree rings its bytes
/// here, a click selects the field and a drag selects a byte range.
/// </summary>
/// <remarks>
/// <para>
/// Virtual document: a capture can be hundreds of megabytes, so the <see cref="TextDocument"/> never holds it. It holds
/// only the window of lines on screen (the layout generates them on demand from the op stores), rebuilt when the window
/// moves; this control's own scroll bar spans the layout's full line count and maps its value to the window's first
/// line. Opening a large capture therefore costs the layout's per-op index, never text.
/// </para>
/// <para>
/// Rendering: byte lines are mono text built from spans into one reused buffer (no string per byte or per line); the
/// <see cref="HexColorizer"/> line transformer tints offsets, dimmed cells, redaction dots and ASCII, the
/// <see cref="HexAnnotationGenerator"/> element generator sets annotation lines in the UI font, and the
/// <see cref="HexBackgroundRenderer"/> paints fills, stripes, read borders, rules and rings from the window's
/// <see cref="HexRow"/>s. No highlighting definition is loaded (AvaloniaEdit's XSHD loader uses reflection; AOT).
/// </para>
/// </remarks>
public sealed class HexView : UserControl
{
    /// <summary>The hint shown when there are no bytes (PKG hex-view empty state).</summary>
    public const string EmptyText = "No bytes in view · select a frame or scrub the timeline";

    /// <summary>Lines scrolled per wheel notch.</summary>
    public const int WheelLines = 3;

    private const double DefaultLineHeight = 20;

    public static readonly StyledProperty<HexLayout?> LayoutProperty =
        AvaloniaProperty.Register<HexView, HexLayout?>(nameof(Layout));

    public static readonly StyledProperty<SyncBus?> SyncProperty =
        AvaloniaProperty.Register<HexView, SyncBus?>(nameof(Sync));

    private readonly TextView _textView;
    private readonly TextDocument _document;
    private readonly ScrollBar _scrollBar;
    private readonly TextBlock _hint;
    private readonly HexLinker _linker = new();
    private readonly HexHighlight _highlight = new();
    private HexLine[] _lines = new HexLine[64];
    private HexRow[] _rows = new HexRow[64];
    private string?[] _annotations = new string?[64];
    private char[] _buffer = new char[4096];
    private int _windowCount;
    private int _digits = HexText.MinDigits;
    private long _top;
    private bool _stickToEnd;
    private bool _attached;
    private bool _settingScroll;
    private bool _revealPending;
    private HexLayout? _layout;
    private SyncBus? _sync;
    private HexRange? _localHover;
    private Selection _ownSelection = Selection.None;
    private long _ownPlayhead = long.MinValue;
    private (TapDirection Direction, long Offset, FrameKey Frame)? _anchor;
    private bool _dragged;
    private double _lineHeight = DefaultLineHeight;

    public HexView()
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
        _textView.ElementGenerators.Add(new HexAnnotationGenerator(this));
        _textView.LineTransformers.Add(new HexColorizer(this));
        _textView.BackgroundRenderers.Add(new HexBackgroundRenderer(this));

        _scrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            Minimum = 0,
            SmallChange = 1,
            HorizontalAlignment = HorizontalAlignment.Right,
            AllowAutoHide = false,
        };
        _scrollBar.ValueChanged += OnScrollBarValueChanged;

        _hint = new TextBlock
        {
            Text = EmptyText,
            Margin = new Thickness(10, 20, 16, 20),
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = true,
        };
        _hint.Bind(TextBlock.FontFamilyProperty, _hint.GetResourceObservable("Font.Ui"));
        _hint.Bind(TextBlock.FontSizeProperty, _hint.GetResourceObservable("FontSize.Body"));
        _hint.Bind(TextBlock.ForegroundProperty, _hint.GetResourceObservable("Brush.Text.Tertiary"));

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Border text = new() { Padding = new Thickness(10, 6, 16, 6), Child = _textView };
        grid.Children.Add(text);
        grid.Children.Add(_hint);
        Grid.SetColumn(_scrollBar, 1);
        grid.Children.Add(_scrollBar);
        Content = grid;

        Focusable = true;
        ClipToBounds = true;
        Bind(BackgroundProperty, this.GetResourceObservable("Brush.Surface.Panel"));
        Bind(ForegroundProperty, this.GetResourceObservable("Brush.Text.Primary"));
        Bind(FontFamilyProperty, this.GetResourceObservable("Font.Mono"));
        Bind(FontSizeProperty, this.GetResourceObservable("FontSize.Small"));
        AutomationProperties.SetName(this, "Hex stream");

        _textView.PointerMoved += OnTextPointerMoved;
        _textView.PointerPressed += OnTextPointerPressed;
        _textView.PointerReleased += OnTextPointerReleased;
        _textView.PointerExited += OnTextPointerExited;
        _textView.PointerCaptureLost += (_, _) => _anchor = null;
    }

    /// <summary>The lines shown.</summary>
    public HexLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>The selection, hovered field, playhead and window shared with every view.</summary>
    public SyncBus? Sync
    {
        get => GetValue(SyncProperty);
        set => SetValue(SyncProperty, value);
    }

    /// <summary>The first line of the window.</summary>
    public long TopLine => _top;

    /// <summary>Lines in the window (the text document's line count).</summary>
    public int WindowLineCount => _windowCount;

    /// <summary>Whole lines that fit.</summary>
    public int VisibleLines => Math.Max(1, (int)Math.Floor(Math.Max(0, _textView.Bounds.Height) / _lineHeight));

    /// <summary>True while the empty hint shows.</summary>
    public bool ShowsHint => _hint.IsVisible;

    /// <summary>The AvaloniaEdit text view (tests and I26's context menu read positions from it).</summary>
    public TextView TextView => _textView;

    /// <summary>What the selection and hover light up now.</summary>
    public HexHighlight Highlight => _highlight;

    /// <summary>
    /// The header field under the pointer when it is no decoded field (the local hover ring); settable to show the
    /// hover state without a pointer, as the gallery does.
    /// </summary>
    public HexRange? HoveredBytes
    {
        get => _localHover;
        set
        {
            if (value == _localHover)
                return;
            _localHover = value;
            Rebuild();
        }
    }

    /// <summary>The offset column's digit count.</summary>
    internal int Digits => _digits;

    /// <summary>A window line, by its index in the document (0-based).</summary>
    internal bool TryGetLine(int index, out HexLine line)
    {
        if ((uint)index < (uint)_windowCount)
        {
            line = _lines[index];
            return true;
        }

        line = default;
        return false;
    }

    /// <summary>The row of a window byte line.</summary>
    internal ref readonly HexRow RowAt(int index) => ref _rows[index];

    /// <summary>The text of a window annotation line.</summary>
    internal string? AnnotationAt(int index) => (uint)index < (uint)_windowCount ? _annotations[index] : null;

    /// <summary>Scrolls so <paramref name="line"/> is the first line shown (clamped).</summary>
    public void ScrollToLine(long line)
    {
        long max = MaxTop();
        long top = Math.Clamp(line, 0, max);
        _stickToEnd = top >= max && max > 0;
        if (top == _top && _windowCount > 0)
            return;
        _top = top;
        Rebuild();
    }

    /// <summary>Scrolls by <paramref name="lines"/> (positive: down), as the user does; the timeline window follows.</summary>
    public void ScrollBy(long lines)
    {
        ScrollToLine(_top + lines);
        FollowWindow();
    }

    /// <summary>Scrolls so the byte at <paramref name="offset"/> of <paramref name="direction"/> is visible.</summary>
    /// <returns>False when the byte is not shown.</returns>
    public bool Reveal(TapDirection direction, long offset)
    {
        if (_layout?.Session is not { } session)
            return false;
        long line;
        lock (session.Gate)
            line = _layout.FindLine(direction, offset);
        if (line < 0)
            return false;
        int visible = VisibleLines;
        if (line < _top || line >= _top + visible)
            ScrollToLine(line - Math.Min(2, visible / 4));
        return true;
    }

    /// <summary>The byte under a point of the text view; false over offsets, annotations and blank cells.</summary>
    public bool TryHitTest(Point point, out TapDirection direction, out long offset)
    {
        direction = default;
        offset = -1;
        if (!_textView.VisualLinesValid)
            return false;

        VisualLine? visual = _textView.GetVisualLineFromVisualTop(point.Y + _textView.VerticalOffset);
        if (visual is null)
            return false;
        int index = visual.FirstDocumentLine.LineNumber - 1;
        if (!TryGetLine(index, out HexLine line) || line.Kind != HexLineKind.Bytes)
            return false;

        double x = point.X + _textView.HorizontalOffset;
        double cell0 = visual.GetVisualPosition(HexText.CellColumn(_digits, 0), VisualYPosition.LineTop).X;
        double cell1 = visual.GetVisualPosition(HexText.CellColumn(_digits, 1), VisualYPosition.LineTop).X;
        double stride = cell1 - cell0;
        double ascii = visual.GetVisualPosition(HexText.AsciiColumn(_digits), VisualYPosition.LineTop).X;
        int cell = -1;
        double charWidth = stride / HexText.CellStride;
        double left = cell0 - (charWidth / 2);
        if (stride > 0 && x >= left && x < left + (stride * HexLayout.BytesPerRow))
            cell = (int)((x - left) / stride);
        else if (charWidth > 0 && x >= ascii && x < ascii + (charWidth * HexLayout.BytesPerRow))
            cell = (int)((x - ascii) / charWidth);

        ref readonly HexRow row = ref _rows[index];
        if (cell < 0 || !row.Shows(cell))
            return false;
        direction = row.Direction;
        offset = row.Row + cell;
        return true;
    }

    /// <summary>Hovers the byte at <paramref name="offset"/>, as the pointer does; a negative offset clears the hover.</summary>
    public void HoverByte(TapDirection direction, long offset)
    {
        HexRange? local = null;
        FieldHover hover = FieldHover.None;
        if (offset >= 0 && _layout?.Session is { } session)
        {
            lock (session.Gate)
            {
                if (_linker.TryResolve(session, direction, offset, out HexFieldHit hit))
                {
                    hover = hit.Hover;
                    if (hover.IsNone)
                        local = hit.Field;
                }
            }
        }

        bool localChanged = local != _localHover;
        _localHover = local;
        _sync?.SetHover(hover);
        if (localChanged)
            Rebuild();
    }

    /// <summary>Selects the field around a byte, as a click does.</summary>
    public void SelectFieldAt(TapDirection direction, long offset)
    {
        if (_sync is not { } sync || _layout?.Session is not { } session)
            return;

        HexFieldHit hit;
        long ticks;
        lock (session.Gate)
        {
            if (!_linker.TryResolve(session, direction, offset, out hit))
                return;
            session.TryGetFrame(hit.Frame, out var frame);
            ticks = frame.StartTicks;
        }

        Select(Selection.OfBytes(direction, new StreamRange(hit.Field.Start, hit.Field.End - hit.Field.Start)) with { Frame = hit.Frame },
            ticks);
    }

    /// <summary>Selects the bytes from <paramref name="anchor"/> to <paramref name="focus"/> (inclusive), as a drag does.</summary>
    public void SelectRange(TapDirection direction, long anchor, long focus)
    {
        if (_sync is null || _layout?.Session is not { } session)
            return;

        long start = Math.Min(anchor, focus);
        long end = Math.Max(anchor, focus) + 1;
        FrameKey frame = new(direction, -1);
        long ticks = -1;
        lock (session.Gate)
        {
            ReadOnlySpan<Core.Framing.FrameRecord> frames = session.Assembler.GetFrames(direction);
            int at = HexLayout.FrameAt(frames, start);
            if (at >= 0)
            {
                frame = new FrameKey(direction, frames[at].Seq);
                ticks = frames[at].StartTicks;
            }
        }

        Select(Selection.OfBytes(direction, new StreamRange(start, end - start)) with { Frame = frame }, ticks >= 0 ? ticks : null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayoutProperty || change.Property == SyncProperty)
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
        RevealPending();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Handled || e.Delta.Y == 0)
            return;
        ScrollBy(-(long)Math.Round(e.Delta.Y * WheelLines));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        int page = Math.Max(1, VisibleLines - 1);
        long? to = e.Key switch
        {
            Key.Up => _top - 1,
            Key.Down => _top + 1,
            Key.PageUp => _top - page,
            Key.PageDown => _top + page,
            Key.Home when (e.KeyModifiers & KeyModifiers.Control) != 0 => 0,
            Key.End when (e.KeyModifiers & KeyModifiers.Control) != 0 => long.MaxValue / 2,
            _ => null,
        };
        if (to is not { } line)
            return;
        ScrollBy(line - _top);
        e.Handled = true;
    }

    private void Subscribe()
    {
        Unsubscribe();
        _layout = Layout;
        _sync = Sync;
        if (_layout is not null)
            _layout.Changed += OnLayoutChanged;
        if (_sync is not null)
            _sync.Changed += OnSyncChanged;
        _top = 0;
        Rebuild();
        // A view placed after the selection was made (a pane switched to Hex, a document opened) shows it too, once
        // it knows how many lines fit.
        _revealPending = true;
        RevealPending();
    }

    private void RevealPending()
    {
        if (!_revealPending || Bounds.Height <= 0 || _sync is not { } sync || _layout?.Session is null)
            return;
        _revealPending = false;
        if (!sync.Selection.IsNone)
            RevealSelection(sync.Selection);
    }

    private void Unsubscribe()
    {
        if (_layout is not null)
            _layout.Changed -= OnLayoutChanged;
        if (_sync is not null)
            _sync.Changed -= OnSyncChanged;
        _layout = null;
        _sync = null;
    }

    private void OnLayoutChanged()
    {
        if (_stickToEnd)
            _top = MaxTop();
        Rebuild();
    }

    private void OnSyncChanged(SyncChanges changes)
    {
        if ((changes & SyncChanges.Selection) != 0 && _sync is { } sync && sync.Selection != _ownSelection)
            RevealSelection(sync.Selection);
        else if ((changes & SyncChanges.Playhead) != 0 && _sync is { } bus && bus.Playhead != _ownPlayhead
                 && (changes & SyncChanges.Selection) == 0)
            RevealTicks(bus.Playhead);

        if ((changes & (SyncChanges.Selection | SyncChanges.Hover)) != 0)
            Rebuild();
    }

    private void RevealSelection(in Selection selection)
    {
        if (_layout?.Session is not { } session)
            return;

        switch (selection.Kind)
        {
            case SelectionKind.ByteRange:
                Reveal(selection.Direction, selection.Bytes.Offset);
                break;
            case SelectionKind.Frame:
            case SelectionKind.Message:
                // A message picked from one of its frames (a chunk) shows that frame; otherwise its first.
                FrameKey key = selection.Kind == SelectionKind.Frame || selection.Frame.Seq >= 0
                    ? selection.Frame
                    : new FrameKey(selection.Message.Direction, selection.Message.Seq);
                long offset;
                lock (session.Gate)
                {
                    if (!session.TryGetFrame(key, out var frame))
                        return;
                    offset = frame.Offset;
                }

                Reveal(key.Direction, offset);
                break;
        }
    }

    private void RevealTicks(long ticks)
    {
        if (_layout?.Session is not { } session)
            return;
        long line;
        lock (session.Gate)
            line = _layout.FindLineAtTicks(ticks);
        if (line >= 0 && (line < _top || line >= _top + VisibleLines))
            ScrollToLine(line);
    }

    private void Select(Selection selection, long? ticks)
    {
        _ownSelection = selection;
        if (ticks is { } t)
            _ownPlayhead = t;
        _sync?.Select(selection, ticks);
    }

    // The user scrolled: the timeline window moves to keep the top op in view (PKG: scrolling moves the window).
    private void FollowWindow()
    {
        if (_sync is not { Window.IsEmpty: false } sync || _layout?.Session is not { } session || _layout.LineCount == 0)
            return;
        long ticks;
        lock (session.Gate)
            ticks = _layout.TicksAtLine(_top);
        TimeWindow window = sync.Window;
        if (ticks >= window.Start && ticks <= window.End)
            return;
        sync.Pan(ticks - window.Start - (window.Duration / 10));
    }

    private void OnScrollBarValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_settingScroll)
            return;
        ScrollToLine((long)Math.Round(e.NewValue));
        FollowWindow();
    }

    private void OnTextPointerMoved(object? sender, PointerEventArgs e)
    {
        Point point = e.GetPosition(_textView);
        bool hit = TryHitTest(point, out TapDirection direction, out long offset);
        if (_anchor is { } anchor && e.GetCurrentPoint(_textView).Properties.IsLeftButtonPressed)
        {
            if (hit && direction == anchor.Direction && offset != anchor.Offset)
            {
                _dragged = true;
                SelectRange(direction, anchor.Offset, offset);
            }

            return;
        }

        HoverByte(direction, hit ? offset : -1);
    }

    private void OnTextPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(_textView);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        Focus();
        if (!TryHitTest(point.Position, out TapDirection direction, out long offset))
            return;
        _anchor = (direction, offset, default);
        _dragged = false;
        e.Pointer.Capture(_textView);
        e.Handled = true;
    }

    private void OnTextPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_anchor is not { } anchor)
            return;
        _anchor = null;
        e.Pointer.Capture(null);
        if (!_dragged)
            SelectFieldAt(anchor.Direction, anchor.Offset);
        e.Handled = true;
    }

    private void OnTextPointerExited(object? sender, PointerEventArgs e)
    {
        if (_anchor is null)
            HoverByte(default, -1);
    }

    private long MaxTop()
    {
        long lines = _layout?.LineCount ?? 0;
        return Math.Max(0, lines - VisibleLines);
    }

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

    /// <summary>Rebuilds the window from the layout and redraws (also run on every data, selection or hover change).</summary>
    public void Rebuild()
    {
        HexLayout? layout = _layout;
        CaptureSession? session = layout?.Session;
        if (layout is null || session is null)
        {
            SetWindow(0, empty: true);
            return;
        }

        int visible = VisibleLines + 1;
        EnsureCapacity(visible);
        int count;
        int length = 0;
        lock (session.Gate)
        {
            long max = Math.Max(0, layout.LineCount - VisibleLines);
            _top = Math.Clamp(_top, 0, max);
            count = layout.Read(_top, _lines.AsSpan(0, visible));
            _digits = HexText.OffsetDigits(layout.MaxOffset);
            _linker.Build(session, _sync?.Selection ?? Selection.None, _sync?.Hover ?? FieldHover.None, _localHover, _highlight);
            int lineLength = HexText.LineLength(_digits);
            int indent = HexText.CellColumn(_digits, 0);
            for (int i = 0; i < count; i++)
            {
                ref readonly HexLine line = ref _lines[i];
                if (i > 0)
                    Append('\n', ref length);
                switch (line.Kind)
                {
                    case HexLineKind.Bytes:
                        HexRowBuilder.Build(layout, line, _highlight, ref _rows[i]);
                        _annotations[i] = null;
                        Reserve(length + lineLength);
                        HexText.FormatBytes(_rows[i], _digits, _buffer.AsSpan(length, lineLength));
                        length += lineLength;
                        break;
                    default:
                        string text = line.Kind == HexLineKind.ReadRule ? HexText.Rule(layout, line) : HexText.Frames(layout, line);
                        _annotations[i] = text;
                        Reserve(length + indent + text.Length);
                        _buffer.AsSpan(length, indent).Fill(' ');
                        length += indent;
                        text.CopyTo(_buffer.AsSpan(length));
                        length += text.Length;
                        break;
                }
            }

        }

        _windowCount = count;
        SetText(length);
        SetWindow(layout.LineCount, empty: layout.LineCount == 0);
    }

    private void SetText(int length)
    {
        string current = _document.Text;
        if (current.Length != length || !current.AsSpan().SequenceEqual(_buffer.AsSpan(0, length)))
            _document.Text = new string(_buffer, 0, length);
        Redraw();
    }

    private void SetWindow(long lines, bool empty)
    {
        if (empty)
        {
            _windowCount = 0;
            if (_document.TextLength > 0)
                _document.Text = string.Empty;
        }

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

    private void EnsureCapacity(int lines)
    {
        if (_lines.Length >= lines)
            return;
        int size = Math.Max(lines, _lines.Length * 2);
        Array.Resize(ref _lines, size);
        Array.Resize(ref _rows, size);
        Array.Resize(ref _annotations, size);
    }

    private void Reserve(int length)
    {
        if (_buffer.Length < length)
            Array.Resize(ref _buffer, Math.Max(length, _buffer.Length * 2));
    }

    private void Append(char c, ref int length)
    {
        Reserve(length + 1);
        _buffer[length++] = c;
    }
}
