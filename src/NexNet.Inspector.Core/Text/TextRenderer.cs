using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.Core.Text;

/// <summary>What one <see cref="TextRenderer.Render"/> did.</summary>
/// <param name="Lines">Lines written (the budget line not counted).</param>
/// <param name="Withheld">Lines the budget held back, counted over what the session holds now.</param>
/// <param name="Tokens">Approximate tokens written (see <see cref="TextBudget"/>).</param>
/// <param name="Next">Where the next render resumes: after the last line written, or at the first withheld one.</param>
/// <param name="Complete">True once the end line was written: the session finished and nothing is left.</param>
public readonly record struct TextRenderResult(int Lines, int Withheld, long Tokens, TextCursor Next, bool Complete)
{
    /// <summary>True when a budget held lines back.</summary>
    public bool BudgetHit => Withheld > 0;
}

/// <summary>
/// Renders a <see cref="CaptureSession"/> as token-condensed text or ndjson (docs/internals/inspector-text-format.md):
/// a header, then one line per message (or frame), in capture order, with pings, credits, window updates, item runs and
/// repeated lines collapsed, values cut to a width and depth, and budgets that stop at a resumable cursor.
/// </summary>
/// <remarks>
/// <para>
/// Whole session: <see cref="RenderToString"/> or one <see cref="Render(TextWriter)"/>. Incremental (a live tail):
/// keep the renderer and call <see cref="Render(TextWriter)"/> again as records arrive; each call writes what is new
/// and the end line once the session finished. A call that hits a budget writes `… budget hit, N withheld, --after
/// &lt;cursor&gt;`, and the next call (or a new renderer with <see cref="TextOptions.After"/>) resumes at that cursor.
/// </para>
/// <para>
/// An invocation's line shows its outcome when the result is known as the line is written; otherwise it shows
/// `-&gt; …` and a later call writes a result line when the result arrives. Rendering a finished session therefore
/// never writes result lines, and rendering it again gives the same lines. Not thread-safe: one renderer per reader;
/// it takes the session's <see cref="CaptureSession.Gate"/> while it reads, in batches.
/// </para>
/// </remarks>
public sealed partial class TextRenderer
{
    private const int BatchEvents = 2048;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly CaptureSession _session;
    private readonly TextValues _values;
    private readonly TextRecordWriter _writer;
    private readonly StringBuilder _sb = new();
    private readonly List<TextRecord> _records = [];
    private readonly List<TraceFragment> _fragments = [];
    private readonly ulong _fingerprint;
    private readonly double _msPerTick;

    // Invocation IDs: the wire ID, and its occurrence when the session reused it (#5, #5.2).
    private readonly List<int> _occurrence = [];
    private readonly Dictionary<ushort, int> _idCounts = [];

    // Position: phase 0 before the header, 1 in the events, 2 done.
    private byte _phase;
    private int _frame;
    private int _gap;

    // The event being processed, and how many of its records were produced; records of the resume event below
    // _skip were written before the cursor.
    private int _eventFrame;
    private int _eventGap;
    private int _sub;
    private bool _skipping;
    private int _skipFrame;
    private int _skipGap;
    private byte _skipPhase;
    private int _skip;

    // Collapse state.
    private bool _hasHeld;
    private TextRecord _held;
    private NoiseRun _noise;

    // Per (pipe, direction) the next of its items to show.
    private readonly Dictionary<long, int> _itemNext = [];

    // Calls shown with "-> …" whose result is still to be shown on its own line.
    private readonly HashSet<int> _pendingShown = [];

    /// <summary>Creates a renderer.</summary>
    /// <param name="session">The session.</param>
    /// <param name="options">Options; <see cref="TextOptions.Default"/> when null.</param>
    /// <exception cref="ArgumentException">
    /// <see cref="TextOptions.After"/> belongs to another session, level or filter.
    /// </exception>
    public TextRenderer(CaptureSession session, TextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        Options = options ?? TextOptions.Default;
        _values = new TextValues(Options);
        _writer = new TextRecordWriter(Options);
        _fingerprint = Options.Filter.Fingerprint();
        var frequency = Math.Max(1, session.Header.StopwatchFrequency);
        _msPerTick = 1000.0 / frequency;

        if (Options.After is { } after)
        {
            if (after.SessionId != session.SessionId)
                throw new ArgumentException($"The cursor belongs to session {after.SessionId}, not {session.SessionId}.", nameof(options));
            if (after.Level != Options.Level)
                throw new ArgumentException($"The cursor was made at level {after.Level}, not {Options.Level}.", nameof(options));
            if (after.Filter != _fingerprint)
                throw new ArgumentException("The cursor was made with different filters.", nameof(options));
            MoveTo(after);
        }
    }

    /// <summary>The options.</summary>
    public TextOptions Options { get; }

    /// <summary>The session.</summary>
    public CaptureSession Session => _session;

    /// <summary>Where the next render starts.</summary>
    public TextCursor Position => new(_session.SessionId, Options.Level, _phase, _frame, _gap, _skipping ? _skip : 0, _fingerprint);

    /// <summary>True once the end line was produced.</summary>
    public bool IsComplete => _phase == 2;

    /// <summary>Renders a whole session (what it holds now) to a string.</summary>
    /// <param name="session">The session.</param>
    /// <param name="options">Options.</param>
    public static string RenderToString(CaptureSession session, TextOptions? options = null)
    {
        using var output = new StringWriter(Invariant);
        new TextRenderer(session, options).Render(output);
        return output.ToString();
    }

    /// <summary>
    /// Writes what is new since the last call (from the start, or <see cref="TextOptions.After"/>, the first time),
    /// within the budgets; collapsed runs still open are written at the end of the call.
    /// </summary>
    /// <param name="output">Where lines go, one per line.</param>
    /// <returns>Lines written and withheld, tokens, and the cursor to resume at.</returns>
    public TextRenderResult Render(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var lines = 0;
        var withheld = 0;
        long tokens = 0;
        TextRecord? firstWithheld = null;
        var more = true;
        while (more)
        {
            lock (_session.Gate)
            {
                more = Produce(BatchEvents);
                if (!more)
                {
                    FlushHeld();
                    FlushNoise();
                }
            }

            foreach (var record in CollectionsMarshal.AsSpan(_records))
            {
                if (firstWithheld is not null)
                {
                    withheld++;
                    continue;
                }

                var cost = _writer.Measure(record);
                if ((Options.MaxLines > 0 && lines + 1 > Options.MaxLines) || (Options.MaxTokens > 0 && tokens + cost > Options.MaxTokens))
                {
                    firstWithheld = record;
                    withheld++;
                    continue;
                }

                _writer.Write(output);
                lines++;
                tokens += cost;
            }

            _records.Clear();
        }

        if (firstWithheld is { } first)
        {
            var cursor = first.CursorAt(_session.SessionId, Options.Level, _fingerprint);
            MoveTo(cursor);
            _writer.WriteBudget(output, withheld, cursor);
            return new TextRenderResult(lines, withheld, tokens, cursor, false);
        }

        return new TextRenderResult(lines, 0, tokens, Position, IsComplete);
    }

    /// <summary>
    /// Produces records without writing them or applying budgets (for queries and findings that read records), from
    /// the current position to what the session holds now.
    /// </summary>
    /// <param name="destination">Receives the records in order.</param>
    /// <returns>Records added.</returns>
    public int Collect(List<TextRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var before = destination.Count;
        var more = true;
        while (more)
        {
            lock (_session.Gate)
            {
                more = Produce(BatchEvents);
                if (!more)
                {
                    FlushHeld();
                    FlushNoise();
                }
            }

            destination.AddRange(_records);
            _records.Clear();
        }

        return destination.Count - before;
    }

    /// <summary>Formats one record as a text line (without newline), for callers that hold records.</summary>
    /// <param name="record">A record of this renderer.</param>
    public string Format(in TextRecord record)
    {
        _writer.Measure(record);
        return _writer.Current;
    }

    internal double Milliseconds(long ticks) => (ticks - _session.StartTicks) * _msPerTick;

    internal double Span(long from, long to) => (to - from) * _msPerTick;

    private void MoveTo(in TextCursor cursor)
    {
        _phase = cursor.Phase;
        _frame = cursor.Frame;
        _gap = cursor.Gap;
        _skipping = cursor.Skip > 0;
        _skip = cursor.Skip;
        _skipPhase = cursor.Phase;
        _skipFrame = cursor.Frame;
        _skipGap = cursor.Gap;
        _hasHeld = false;
        _held = default;
        _noise = default;
        _itemNext.Clear();
        _pendingShown.Clear();
        _records.Clear();
    }

    // Produces the records of up to `budget` events. Call under the gate. False once the session's current end is
    // reached.
    private bool Produce(int budget)
    {
        if (_phase == 0)
        {
            BeginEvent(0, 0, 0);
            if (Options.Header)
                Header();
            _phase = 1;
            _frame = 0;
            _gap = 0;
        }

        if (_phase == 2)
            return false;

        if (Options.Level == TextLevel.Summary)
        {
            Summary();
            _phase = 2;
            return false;
        }

        var order = _session.FrameOrder;
        var marks = _session.GapMarks;
        var gaps = _session.Gaps;
        while (budget-- > 0)
        {
            if (_gap < marks.Length && _gap < gaps.Length && marks[_gap].FrameIndex <= _frame)
            {
                BeginEvent(1, _frame, _gap);
                OnGap(gaps[_gap]);
                _gap++;
                continue;
            }

            if (_frame < order.Length)
            {
                BeginEvent(1, _frame, _gap);
                if (_frame == 0 && Options.Level == TextLevel.Messages && !_session.IsFromInception(order[0].Direction))
                    Attached(order[0]);
                OnFrame(_frame, order[_frame]);
                _frame++;
                continue;
            }

            if (_session.State != CaptureState.Open)
            {
                BeginEvent(2, _frame, _gap);
                FlushHeld();
                FlushNoise();
                End();
                _phase = 2;
            }

            return false;
        }

        return true;
    }

    private void BeginEvent(byte phase, int frame, int gap)
    {
        _eventFrame = frame;
        _eventGap = gap;
        _sub = 0;
        if (_skipping && (phase != _skipPhase || frame != _skipFrame || gap != _skipGap))
            _skipping = false;
        _phaseOfEvent = phase;
    }

    private byte _phaseOfEvent;

    // Stamps a record with its event and passes it through the filter and the collapse rules.
    private void Emit(TextRecord record)
    {
        var filter = Options.Filter;
        if (record.Kind is not (TextRecordKind.Header or TextRecordKind.Stat))
        {
            if (record.Direction is { } direction
                && (filter.Directions & (direction == TapDirection.Inbound ? TextDirections.Inbound : TextDirections.Outbound)) == 0)
                return;
            if (filter.Window is { } window && !window.Contains(record.Ticks))
                return;
        }

        if (filter.Match is { } match && !match(record))
            return;

        record = record with { Phase = _phaseOfEvent, EventFrame = _eventFrame, EventGap = _eventGap, Sub = _sub++ };
        if (_skipping && record.Sub < _skip)
            return;

        if (!Options.Collapse)
        {
            _records.Add(record);
            return;
        }

        if (record.Kind == TextRecordKind.Noise)
        {
            _noise.Add(record);
            return;
        }

        if (record.Key is null)
        {
            FlushHeld();
            FlushNoise();
            _records.Add(record);
            return;
        }

        if (_hasHeld && _noise.Count == 0 && _held.Key == record.Key)
        {
            _held = _held with
            {
                Count = _held.Count + 1,
                LastTicks = record.LastTicks,
                SpanMs = Span(_held.Ticks, record.LastTicks),
                LastId = record.Id,
                Bytes = _held.Bytes >= 0 && record.Bytes >= 0 ? _held.Bytes + record.Bytes : _held.Bytes,
            };
            return;
        }

        FlushHeld();
        FlushNoise();
        _held = record;
        _hasHeld = true;
    }

    private void FlushHeld()
    {
        if (!_hasHeld)
            return;
        _records.Add(_held);
        _hasHeld = false;
        _held = default;
    }

    private void FlushNoise()
    {
        if (_noise.Count == 0)
            return;
        _records.Add(_noise.ToRecord(this));
        _noise = default;
    }

    private TextRecord NewRecord(TextRecordKind kind, long ticks, TapDirection? direction) => new()
    {
        Kind = kind,
        Ticks = ticks,
        LastTicks = ticks,
        TimeMs = Milliseconds(ticks),
        Direction = direction,
        Count = 1,
        Bytes = -1,
        Frame = -1,
        Entity = -1,
    };

    private string Text()
    {
        var text = _sb.ToString();
        _sb.Clear();
        return text;
    }

    // The noise between two lines: counts per kind, collapsed into one line.
    private struct NoiseRun
    {
        public int Count;
        public int Pings;
        public int Credits;
        public int Windows;
        public TextRecord First;
        public long LastTicks;

        public void Add(in TextRecord record)
        {
            if (Count == 0)
                First = record;
            Count++;
            LastTicks = record.LastTicks;
            switch (record.Name)
            {
                case "ping":
                    Pings++;
                    break;
                case "credit":
                    Credits++;
                    break;
                default:
                    Windows++;
                    break;
            }
        }

        public readonly TextRecord ToRecord(TextRenderer renderer)
        {
            if (Count == 1)
                return First;

            var sb = renderer._sb;
            Append(sb, "ping", Pings);
            Append(sb, "credit", Credits);
            Append(sb, "window", Windows);
            return First with
            {
                Direction = null,
                Name = null,
                Value = renderer.Text(),
                Count = Count,
                LastTicks = LastTicks,
                SpanMs = renderer.Span(First.Ticks, LastTicks),
            };
        }

        private static void Append(StringBuilder sb, string word, int count)
        {
            if (count == 0)
                return;
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(word).Append(Invariant, $" ×{count}");
        }
    }
}
