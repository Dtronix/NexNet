using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.DataTable.Sessions;

/// <summary>
/// Draws one session-list row (open-recording): the 12 px radio (1.5 px border.strong ring, or a 4 px
/// direction.inbound ring when picked, as the RadioRow theme draws it), Session mono 11, Remote in the UI font 12 with
/// an ellipsis, Transport UI 12 secondary, Start mono 11 secondary, Duration mono 11 and Events mono 11 right-aligned.
/// Text is shaped once per row and theme.
/// </summary>
public sealed class SessionRowVisual : DataTableRowVisual
{
    /// <summary>The mono data size (typography mono 11).</summary>
    public const double DataSize = 11;

    /// <summary>The UI text size (typography body 12).</summary>
    public const double BodySize = 12;

    /// <summary>The radio's diameter.</summary>
    public const double RadioSize = 12;

    // The radio sits 3 px into its column, so it lines up under the dialog's 14 px body padding plus the bar.
    private const double RadioInset = 3;

    private ThemePalette? _palette;
    private TextLayout? _session;
    private TextLayout? _remote;
    private TextLayout? _transport;
    private TextLayout? _start;
    private TextLayout? _duration;
    private TextLayout? _events;
    private double _remoteWidth = -1;

    /// <summary>The row shown.</summary>
    public SessionListRow Row { get; private set; }

    /// <summary>True when the row's radio is on.</summary>
    public bool IsPicked { get; private set; }

    /// <summary>Shows <paramref name="row"/> at <paramref name="index"/>; text is shaped again only when it changed.</summary>
    public void Set(int index, in SessionListRow row, bool picked)
    {
        Index = index;
        IsPicked = picked;
        if (row == Row && _session is not null) return;
        Row = row;
        Clear();
    }

    public override void Render(DrawingContext context, ThemePalette palette, ReadOnlySpan<DataTableColumnSlot> columns, Size size)
    {
        if (columns.Length <= SessionListColumns.Events || Index < 0) return;
        if (!ReferenceEquals(palette, _palette))
        {
            Clear();
            _palette = palette;
        }

        SessionListRow row = Row;
        Typeface mono = new(palette.Mono);
        Typeface ui = new(palette.Ui);
        _session ??= Text(row.Session, mono, DataSize, palette[ThemeBrush.TextPrimary]);
        _transport ??= Text(row.Transport, ui, BodySize, palette[ThemeBrush.TextSecondary], columns[SessionListColumns.Transport].Width);
        _start ??= Text(row.Start, mono, DataSize, palette[ThemeBrush.TextSecondary]);
        _duration ??= Text(row.Duration, mono, DataSize, palette[ThemeBrush.TextPrimary]);
        _events ??= Text(row.Events, mono, DataSize, palette[ThemeBrush.TextPrimary]);
        DataTableColumnSlot remoteSlot = columns[SessionListColumns.Remote];
        if (_remote is null || _remoteWidth != remoteSlot.Width)
        {
            _remote?.Dispose();
            _remote = Text(row.Remote, ui, BodySize, palette[ThemeBrush.TextPrimary], Math.Max(0, remoteSlot.Width - 8));
            _remoteWidth = remoteSlot.Width;
        }

        double height = size.Height;
        DrawRadio(context, palette, columns[SessionListColumns.Radio].X + RadioInset, height, IsPicked);
        DrawLeft(context, _session, columns[SessionListColumns.Session], height);
        if (remoteSlot.Width > 0) DrawLeft(context, _remote, remoteSlot, height);
        DrawLeft(context, _transport, columns[SessionListColumns.Transport], height);
        DrawLeft(context, _start, columns[SessionListColumns.Start], height);
        DrawLeft(context, _duration, columns[SessionListColumns.Duration], height);
        DataTableColumnSlot eventsSlot = columns[SessionListColumns.Events];
        _events.Draw(context, new Point(eventsSlot.Right - _events.WidthIncludingTrailingWhitespace, Middle(_events, height)));
    }

    public override string? ToolTipAt(int column) => column == SessionListColumns.Remote && Index >= 0 ? Row.Remote : null;

    public override string? AccessibleName =>
        Index < 0 ? null : $"Session {Row.Session}, {Row.Remote}, {Row.Transport}, start {Row.Start}, duration {Row.Duration}, {Row.Events} events{(IsPicked ? ", selected" : "")}";

    /// <summary>The radio: a ring, or the thick inbound ring when picked.</summary>
    public static void DrawRadio(DrawingContext context, ThemePalette palette, double x, double height, bool picked)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(palette);
        double stroke = picked ? 4 : 1.5;
        double radius = (RadioSize - stroke) / 2;
        Point centre = new(x + (RadioSize / 2), Math.Round(height / 2));
        IPen pen = palette.Pen(picked ? ThemeBrush.DirectionInbound : ThemeBrush.BorderStrong, stroke);
        context.DrawEllipse(null, pen, centre, radius, radius);
    }

    private static void DrawLeft(DrawingContext context, TextLayout text, DataTableColumnSlot slot, double height) =>
        text.Draw(context, new Point(slot.X, Middle(text, height)));

    private static double Middle(TextLayout text, double height) => Math.Round((height - text.Height) / 2);

    private static TextLayout Text(string text, Typeface face, double size, IBrush ink, double maxWidth = double.PositiveInfinity) =>
        new(text, face, size, ink, textTrimming: double.IsFinite(maxWidth) ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            maxWidth: double.IsFinite(maxWidth) ? Math.Max(0, maxWidth) : double.PositiveInfinity, maxLines: 1);

    private void Clear()
    {
        _session?.Dispose();
        _remote?.Dispose();
        _transport?.Dispose();
        _start?.Dispose();
        _duration?.Dispose();
        _events?.Dispose();
        _session = _remote = _transport = _start = _duration = _events = null;
        _remoteWidth = -1;
    }
}
