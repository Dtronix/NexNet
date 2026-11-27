using System.Globalization;
using System.Text;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.Core.Text;

/// <summary>What a query ID names.</summary>
public enum TextEntityKind : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>An invocation (`#5`, `#5.2`): an index into the assembler's invocations.</summary>
    Invocation = 1,

    /// <summary>A pipe (`≋4·4`, `p4`): an index into the assembler's pipes.</summary>
    Pipe = 2,

    /// <summary>A frame (`f18`): an index into the session's frame order.</summary>
    Frame = 3,
}

/// <summary>An entity a query ID resolved to.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Index">Its index (see <see cref="TextEntityKind"/>).</param>
public readonly record struct TextEntityRef(TextEntityKind Kind, int Index);

/// <summary>
/// The drill-down queries over one <see cref="CaptureSession"/> (`nexnet-tap summary|show|trace|around|errors|slow|pipes|collections|schema`,
/// and the MCP tools): each appends <see cref="TextRecord"/>s in the text format's forms (docs/internals/inspector-text-format.md
/// §10), using the <see cref="TextRenderer"/>'s lines, IDs and values; <see cref="TextQueryWriter"/> writes them with budgets.
/// </summary>
/// <remarks>
/// Reads what the session holds now (a live session: a snapshot). Takes the session's gate while it reads; not
/// thread-safe (one query object per reader). Every query starts with the session's header line when
/// <see cref="TextOptions.Header"/> is set.
/// </remarks>
public sealed partial class TextQuery
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly CaptureSession _session;
    private readonly TextRenderer _ids;
    private readonly TextValues _values;
    private readonly StringBuilder _sb = new();
    private Dictionary<FrameKey, int>? _frameIndex;

    /// <summary>Creates the queries of one session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="options">Format, widths and level; <see cref="TextOptions.Default"/> when null. Budgets and
    /// <see cref="TextOptions.After"/> are the writer's, not the query's.</param>
    public TextQuery(CaptureSession session, TextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        Options = (options ?? TextOptions.Default) with { After = null, MaxLines = 0, MaxTokens = 0 };
        _ids = new TextRenderer(session, Options);
        _values = new TextValues(Options);
    }

    /// <summary>The options.</summary>
    public TextOptions Options { get; }

    /// <summary>The session.</summary>
    public CaptureSession Session => _session;

    /// <summary>The session's header line (`@0x1BCB server tcp …`).</summary>
    public TextRecord Header()
    {
        var records = new List<TextRecord>(1);
        new TextRenderer(_session, Options with
        {
            Level = TextLevel.Summary,
            Header = true,
            Filter = new TextFilter { Match = static r => r.Kind == TextRecordKind.Header, MatchKey = "header" },
        }).Collect(records);
        return records[0];
    }

    /// <summary>
    /// Resolves a query ID: `#5`/`5`, `#5.2`/`5.2` (an invocation), `≋4·4`, `☰1·1`, `⇄3·3`, `p4·4`, `p4.4`, `p4` (a
    /// pipe, by its client-side ID and optionally its server-side one), `f18` (a frame).
    /// </summary>
    /// <param name="text">The ID as typed.</param>
    /// <param name="found">Receives every entity it names (a reused pipe ID can name several).</param>
    /// <returns>False when the text is not an ID; true (with nothing found) when it is one the session does not hold.</returns>
    public bool TryResolve(string text, List<TextEntityRef> found)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(found);
        var id = text.AsSpan().Trim();
        if (id.Length == 0)
            return false;

        if (id[0] is 'f' or 'F')
        {
            if (!int.TryParse(id[1..], NumberStyles.None, Invariant, out var number) || number <= 0)
                return false;
            lock (_session.Gate)
            {
                if (number <= _session.FrameOrder.Length)
                    found.Add(new TextEntityRef(TextEntityKind.Frame, number - 1));
            }

            return true;
        }

        if (id[0] is '≋' or '☰' or '⇄' or 'p' or 'P')
            return ResolvePipe(id[1..], found);

        if (id[0] == '#')
            id = id[1..];
        int wire, occurrence = 1;
        var dot = id.IndexOfAny('.', '·');
        if (dot >= 0)
        {
            if (!int.TryParse(id[..dot], NumberStyles.None, Invariant, out wire)
                || !int.TryParse(id[(dot + 1)..], NumberStyles.None, Invariant, out occurrence) || occurrence <= 0)
                return false;
        }
        else if (!int.TryParse(id, NumberStyles.None, Invariant, out wire))
        {
            return false;
        }

        var wanted = occurrence > 1 ? string.Create(Invariant, $"#{wire}.{occurrence}") : string.Create(Invariant, $"#{wire}");
        lock (_session.Gate)
        {
            var invocations = _session.Assembler.Invocations;
            for (var i = 0; i < invocations.Length; i++)
            {
                if (invocations[i].InvocationId == wire && _ids.InvocationId(i) == wanted)
                {
                    found.Add(new TextEntityRef(TextEntityKind.Invocation, i));
                    break;
                }
            }
        }

        return true;
    }

    private bool ResolvePipe(ReadOnlySpan<char> id, List<TextEntityRef> found)
    {
        var separator = id.IndexOfAny("·.:");
        int client, server = -1;
        if (separator >= 0)
        {
            if (!int.TryParse(id[..separator], NumberStyles.None, Invariant, out client)
                || !int.TryParse(id[(separator + 1)..], NumberStyles.None, Invariant, out server))
                return false;
        }
        else if (!int.TryParse(id, NumberStyles.None, Invariant, out client))
        {
            return false;
        }

        lock (_session.Gate)
        {
            var pipes = _session.Assembler.Pipes;
            for (var i = 0; i < pipes.Length; i++)
            {
                if ((pipes[i].PipeId & 0xFF) == client && (server < 0 || pipes[i].PipeId >> 8 == server))
                    found.Add(new TextEntityRef(TextEntityKind.Pipe, i));
            }
        }

        return true;
    }

    /// <summary>`errors`: calls that ended in an exception, unauthorized or refused, refused pipes, frames that did not
    /// frame or read, and a disconnect that was not graceful.</summary>
    /// <param name="destination">Receives the lines.</param>
    public void Errors(List<TextRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var before = destination.Count;
        Render(destination, Options with
        {
            Level = Options.Level is TextLevel.Summary ? TextLevel.Messages : Options.Level,
            Filter = Options.Filter with { Match = static r => r.Kind == TextRecordKind.Header || IsError(r), MatchKey = "errors" },
        });
        if (destination.Count - before <= (Options.Header ? 1 : 0))
            destination.Add(Stat("errors none"));
    }

    /// <summary>True for a line `errors` lists.</summary>
    /// <param name="record">A record of the messages or frames level.</param>
    public static bool IsError(in TextRecord record) => record.Kind switch
    {
        TextRecordKind.Call or TextRecordKind.Result => record.Outcome is "exception" or "unauthorized" or "refused",
        TextRecordKind.Problem => true,
        TextRecordKind.Pipe => record.Value is not null && record.Value.StartsWith("refused", StringComparison.Ordinal),
        TextRecordKind.End => record.Outcome is not ("Graceful" or "stopped" or "truncated"),
        TextRecordKind.Frame => record.Outcome is { } flags && flags.Contains('!'),
        _ => false,
    };

    /// <summary>`slow --over`: calls that took at least <paramref name="overMs"/>, slowest first.</summary>
    /// <param name="overMs">The threshold in milliseconds.</param>
    /// <param name="destination">Receives the lines.</param>
    public void Slow(double overMs, List<TextRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var calls = new List<TextRecord>();
        Render(calls, Options with
        {
            Level = TextLevel.Messages,
            Collapse = false,
            Header = false,
            Filter = Options.Filter with
            {
                Match = r => r.Kind is TextRecordKind.Call or TextRecordKind.Result && r.DurationMs >= overMs,
                MatchKey = string.Create(Invariant, $"slow {overMs}"),
            },
        });
        calls.Sort(static (a, b) => b.DurationMs!.Value.CompareTo(a.DurationMs!.Value) is var c && c != 0 ? c : a.Ticks.CompareTo(b.Ticks));
        AddHeader(destination);
        destination.Add(Stat(string.Create(Invariant, $"slow {calls.Count} over {Duration(overMs)}")));
        destination.AddRange(calls);
    }

    /// <summary>`around &lt;t&gt; [±w]`: what happened within <paramref name="windowMs"/> of <paramref name="atMs"/>, at the options' level.</summary>
    /// <param name="atMs">Milliseconds since the session's start (the lines' `+t`).</param>
    /// <param name="windowMs">Half the window, in milliseconds.</param>
    /// <param name="destination">Receives the lines.</param>
    public void Around(double atMs, double windowMs, List<TextRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var window = TextFilter.WindowFromMilliseconds(_session, atMs - Math.Abs(windowMs), atMs + Math.Abs(windowMs));
        var level = Options.Level is TextLevel.Summary ? TextLevel.Messages : Options.Level;
        Render(destination, Options with { Level = level, Filter = Options.Filter with { Window = window } });
    }

    // Renders with the renderer into a list.
    private void Render(List<TextRecord> destination, TextOptions options) => new TextRenderer(_session, options).Collect(destination);

    private void AddHeader(List<TextRecord> destination)
    {
        if (Options.Header)
            destination.Add(Header());
    }

    private TextRecord Stat(string text) => new()
    {
        Kind = TextRecordKind.Stat,
        Ticks = _session.StartTicks,
        LastTicks = _session.StartTicks,
        Value = text,
        Count = 1,
        Bytes = -1,
        Frame = -1,
        Entity = -1,
    };

    private TextRecord Field(int depth, string? name, string? value, long offset = 0, long bytes = -1) => new()
    {
        Kind = TextRecordKind.Field,
        Ticks = _session.StartTicks,
        LastTicks = _session.StartTicks,
        Name = name,
        Value = value,
        Depth = depth,
        Offset = offset,
        Count = 1,
        Bytes = bytes,
        Frame = -1,
        Entity = -1,
    };

    private TextRecord Line(TextRecordKind kind, long ticks, TapDirection? direction) => new()
    {
        Kind = kind,
        Ticks = ticks,
        LastTicks = ticks,
        TimeMs = _ids.Milliseconds(ticks),
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

    private static string Size(long bytes) => TextValues.AppendSize(new StringBuilder(), bytes).ToString();

    private static string Duration(double ms) => TextValues.AppendDuration(new StringBuilder(), ms).ToString();

    private static string Arrow(TapDirection direction) => direction == TapDirection.Outbound ? ">" : "<";

    // Frame order index of a frame; -1 when the frame is not in the order. Call under the gate.
    private int OrderIndex(TapDirection direction, long seq)
    {
        var order = _session.FrameOrder;
        if (_frameIndex is null || _frameIndex.Count != order.Length)
        {
            _frameIndex = new Dictionary<FrameKey, int>(order.Length);
            for (var i = 0; i < order.Length; i++)
                _frameIndex[order[i]] = i;
        }

        return _frameIndex.TryGetValue(new FrameKey(direction, seq), out var index) ? index : -1;
    }

    private long FrameTicks(TapDirection direction, long seq)
        => _session.Assembler.TryGetFrame(direction, seq, out var frame) ? frame.StartTicks : 0;

    private long FrameEndTicks(TapDirection direction, long seq)
        => _session.Assembler.TryGetFrame(direction, seq, out var frame) ? frame.EndTicks : 0;

    // The bytes of an invocation's request and result: the large message's size, or the embedded value's.
    private long RequestBytes(in InvocationRow row) => MessageBytes(row.Request, row.Arguments);

    private long ResultBytes(in InvocationRow row) => MessageBytes(row.Result, row.Value);

    private long MessageBytes(in MessageRef message, in PayloadRange value)
    {
        var assembler = _session.Assembler;
        if (message.Kind == MessageRefKind.Assembled && (uint)message.Index < (uint)assembler.Messages.Length)
        {
            var m = assembler.Messages[message.Index];
            return m.TotalLength >= 0 ? m.TotalLength : m.Length;
        }

        return value.IsNone ? -1 : value.Length;
    }

    private static string StateWord(InvocationState state) => state switch
    {
        InvocationState.Completed => "ok",
        InvocationState.Exception => "exception",
        InvocationState.Canceled => "canceled",
        InvocationState.Unauthorized => "unauthorized",
        InvocationState.OneWay => "oneway",
        InvocationState.Refused => "refused",
        _ => "pending",
    };

    private static string LifecycleWord(PipeLifecycle lifecycle) => lifecycle switch
    {
        PipeLifecycle.Open => "open",
        PipeLifecycle.Refused => "refused",
        PipeLifecycle.Closing => "closing",
        _ => "closed",
    };

    private bool IsServerDirection(TapDirection direction)
        => (direction == TapDirection.Outbound) == (_session.Role == NntlSessionRole.Server);
}
