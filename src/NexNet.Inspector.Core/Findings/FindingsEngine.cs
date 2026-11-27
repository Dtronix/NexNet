using System.Globalization;
using System.Text;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;
using NexNet.Messages;

namespace NexNet.Inspector.Core.Findings;

/// <summary>
/// Rule-based detectors over a <see cref="CaptureSession"/>: slow calls, cancellation races and orphan results, credit
/// and window stalls, large messages and chunk storms, pipes and calls left open at the end, protocol errors, schema
/// mismatches, gaps and ping round-trip outliers (docs/articles/inspector.md, "Findings"). Reconnect loops span the
/// sessions of a file: <see cref="AnalyzeAcross"/>.
/// </summary>
/// <remarks>
/// <para>
/// Incremental: keep the engine and call <see cref="Update"/> as records arrive (a live tail); each call returns the
/// findings detected since the last one, in detection order, and the end-of-session findings (calls pending at
/// disconnect, pipes never completed) once the session ended. <see cref="Analyze"/> runs it once over what a session
/// holds and ranks the result (<see cref="Rank"/>). Each finding cites the text format's stable IDs (`#5`, `≋4·4`,
/// `f18`, `@0x1BCB`), which the queries accept.
/// </para>
/// <para>
/// Every event is judged once, against what came before it, so a live run and a run over the finished session find
/// the same things in the same order. The one exception: slow-call outliers of a method with too few earlier calls are
/// judged again at the end, against all its calls. Not thread-safe; it takes the session's
/// <see cref="CaptureSession.Gate"/> while it reads.
/// </para>
/// </remarks>
public sealed partial class FindingsEngine
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const int KindCount = (int)FindingKind.ReconnectLoop + 1;

    private readonly CaptureSession _session;
    private readonly TextRenderer _ids;
    private readonly double _msPerTick;
    private readonly StringBuilder _sb = new();
    private readonly List<Finding> _found = [];
    private readonly int[] _perKind = new int[KindCount];
    private readonly int[] _suppressed = new int[KindCount];

    private bool _started;
    private bool _finished;

    /// <summary>Creates an engine.</summary>
    /// <param name="session">The session.</param>
    /// <param name="options">Thresholds; <see cref="FindingsOptions.Default"/> when null.</param>
    public FindingsEngine(CaptureSession session, FindingsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        Options = options ?? FindingsOptions.Default;
        _ids = new TextRenderer(session);
        _msPerTick = 1000.0 / Math.Max(1, session.Header.StopwatchFrequency);
        _windowTicks = (long)Math.Max(1, Options.ChunkStormWindowMs / _msPerTick);
    }

    /// <summary>The thresholds.</summary>
    public FindingsOptions Options { get; }

    /// <summary>The session.</summary>
    public CaptureSession Session => _session;

    /// <summary>True once the session ended and its end-of-session findings were produced.</summary>
    public bool IsComplete => _finished;

    /// <summary>Runs the detectors once over what a session holds and ranks the findings (<see cref="Rank"/>).</summary>
    /// <param name="session">The session.</param>
    /// <param name="options">Thresholds.</param>
    public static List<Finding> Analyze(CaptureSession session, FindingsOptions? options = null)
    {
        var findings = new List<Finding>();
        new FindingsEngine(session, options).Update(findings);
        Rank(findings);
        return findings;
    }

    /// <summary>Sorts findings most severe first, then by time, kind, session and text: a stable report order.</summary>
    /// <param name="findings">The findings.</param>
    public static void Rank(List<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        findings.Sort(Compare);
    }

    /// <summary>The ranking order of <see cref="Rank"/>.</summary>
    public static int Compare(Finding a, Finding b)
    {
        var c = b.Severity.CompareTo(a.Severity);
        if (c != 0)
            return c;
        c = a.StartMs.CompareTo(b.StartMs);
        if (c != 0)
            return c;
        c = a.Kind.CompareTo(b.Kind);
        if (c != 0)
            return c;
        c = a.EndMs.CompareTo(b.EndMs);
        if (c != 0)
            return c;
        c = a.SessionId.CompareTo(b.SessionId);
        if (c != 0)
            return c;
        c = string.CompareOrdinal(a.IdText, b.IdText);
        return c != 0 ? c : string.CompareOrdinal(a.Message, b.Message);
    }

    /// <summary>
    /// Runs the detectors over what is new since the last call and adds the findings detected, in detection order; once
    /// the session ended, adds the end-of-session findings too. Later calls add nothing more.
    /// </summary>
    /// <param name="destination">Receives the new findings.</param>
    /// <returns>Findings added.</returns>
    public int Update(List<Finding> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_session.Gate)
        {
            if (!_started)
            {
                _started = true;
                Start();
            }

            if (!_finished)
            {
                var ended = _session.State != CaptureState.Open;
                ScanFrames();
                ScanGaps();
                ScanInvocations();
                ScanMessages();
                ScanItems();
                if (ended)
                {
                    _finished = true;
                    Finish();
                }
            }
        }

        destination.AddRange(_found);
        var added = _found.Count;
        _found.Clear();
        return added;
    }

    // Records a finding, unless its kind already listed MaxPerKind (then it is counted for the "more" finding).
    private void Add(FindingSeverity severity, FindingKind kind, string message, string[] ids, long start, long end)
    {
        if (_perKind[(int)kind]++ >= Options.MaxPerKind)
        {
            _suppressed[(int)kind]++;
            return;
        }

        if (end < start)
            end = start;
        _found.Add(new Finding(severity, kind, message, ids, new TimeWindow(start, end))
        {
            SessionId = _session.SessionId,
            StartMs = Ms(start),
            EndMs = Ms(end),
        });
    }

    private double Ms(long ticks) => (ticks - _session.StartTicks) * _msPerTick;

    private double Span(long from, long to) => (to - from) * _msPerTick;

    private string Text()
    {
        var text = _sb.ToString();
        _sb.Clear();
        return text;
    }

    private string SessionTag => string.Create(Invariant, $"@0x{_session.SessionId:X}");

    private static TapDirection Opposite(TapDirection direction)
        => direction == TapDirection.Inbound ? TapDirection.Outbound : TapDirection.Inbound;

    private static char Arrow(TapDirection direction) => direction == TapDirection.Outbound ? '>' : '<';

    // Session-wide findings known from the start: the schema decision.
    private void Start()
    {
        var start = _session.StartTicks;
        switch (_session.Schema.Status)
        {
            case SchemaStatus.Mismatch:
                Add(FindingSeverity.Warning, FindingKind.SchemaMismatch,
                    "the recorded schema differs from the workspace schema: values decode with the chosen one and may misread",
                    [SessionTag], start, start);
                break;
            case SchemaStatus.Unreadable:
                Add(FindingSeverity.Warning, FindingKind.SchemaMismatch,
                    "the session names a schema the recording does not hold or that does not read: values show raw",
                    [SessionTag], start, start);
                break;
        }
    }

    // Findings once the session ended: calls and pipes left open, how it ended, outliers judged late, stall totals and
    // the findings over each kind's limit.
    private void Finish()
    {
        JudgeDeferred();
        PendingAtEnd();
        PipesAtEnd();
        Ending();
        StallTotals();

        var end = _session.Close?.Ticks ?? _session.EndTicks;
        for (var kind = 0; kind < KindCount; kind++)
        {
            if (_suppressed[kind] == 0)
                continue;
            var more = _suppressed[kind];
            _suppressed[kind] = 0;
            _perKind[kind] = int.MinValue;
            Add(FindingSeverity.Info, (FindingKind)kind,
                string.Create(Invariant, $"{more} more {FindingsText.Code((FindingKind)kind)} findings not listed"),
                [SessionTag], end, end);
        }
    }

    /// <summary>
    /// Detectors across the sessions of one file or stream: reconnect loops (a peer whose sessions keep failing and
    /// reconnecting, or that reconnects with a reconnection greeting). Ranked.
    /// </summary>
    /// <param name="sessions">The sessions, any order; they share one clock (one file or stream).</param>
    /// <param name="options">Thresholds.</param>
    public static List<Finding> AnalyzeAcross(IReadOnlyList<CaptureSession> sessions, FindingsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        options ??= FindingsOptions.Default;
        var findings = new List<Finding>();
        if (sessions.Count < 2)
            return findings;

        var rows = new List<SessionRow>(sessions.Count);
        foreach (var session in sessions)
        {
            lock (session.Gate)
            {
                var reconnect = false;
                foreach (var greeting in session.Assembler.Greetings)
                    reconnect |= greeting.Kind == GreetingKind.ClientReconnection;
                var failed = session.Close is { Kind: NntlCloseKind.Disconnected } close && !IsOrderly(close.DisconnectReason);
                rows.Add(new SessionRow(session, session.StartTicks, session.Close?.Ticks ?? session.EndTicks, Host(session.Open.RemoteEndpoint),
                    reconnect, failed));
            }
        }

        rows.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Session.SessionId.CompareTo(b.Session.SessionId));
        var origin = rows[0].Start;
        var msPerTick = 1000.0 / Math.Max(1, rows[0].Session.Header.StopwatchFrequency);
        var within = (long)(options.ReconnectWithinMs / msPerTick);
        var hosts = new List<string>();
        foreach (var row in rows)
        {
            if (!hosts.Contains(row.Host))
                hosts.Add(row.Host);
        }

        foreach (var host in hosts)
        {
            var reconnects = new List<SessionRow>();
            var afterFailure = 0;
            var greetings = 0;
            SessionRow? previous = null;
            foreach (var row in rows)
            {
                if (row.Host != host)
                    continue;
                var failedBefore = previous is { Failed: true } p && row.Start - p.End <= within;
                if (row.Reconnect || failedBefore)
                {
                    reconnects.Add(row);
                    if (failedBefore)
                        afterFailure++;
                    if (row.Reconnect)
                        greetings++;
                }

                previous = row;
            }

            if (reconnects.Count < options.ReconnectLoopMin)
                continue;
            var ids = new List<string>();
            foreach (var row in reconnects)
            {
                if (ids.Count == 8)
                    break;
                ids.Add(string.Create(Invariant, $"@0x{row.Session.SessionId:X}"));
            }

            var first = reconnects[0].Start;
            var last = reconnects[^1].Start;
            var sb = new StringBuilder();
            sb.Append(Invariant, $"{reconnects.Count} reconnections in ");
            TextValues.AppendDuration(sb, (last - first) * msPerTick);
            sb.Append(host.Length > 0 ? " from " + host : "");
            sb.Append(Invariant, $" ({afterFailure} after a failed session, {greetings} with a reconnection greeting)");
            if (reconnects.Count > ids.Count)
                sb.Append(Invariant, $", +{reconnects.Count - ids.Count} more");
            findings.Add(new Finding(FindingSeverity.Warning, FindingKind.ReconnectLoop, sb.ToString(), ids.ToArray(), new TimeWindow(first, last))
            {
                SessionId = reconnects[0].Session.SessionId,
                StartMs = (first - origin) * msPerTick,
                EndMs = (last - origin) * msPerTick,
            });
        }

        Rank(findings);
        return findings;
    }

    // A close that is not a failure: graceful, or the server going away on purpose.
    private static bool IsOrderly(DisconnectReason reason)
        => reason is DisconnectReason.None or DisconnectReason.Graceful or DisconnectReason.ServerShutdown or DisconnectReason.ServerRestarting;

    // The host of "host:port" (a client's sessions come from new ports each time).
    private static string Host(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
            return "";
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 && endpoint.IndexOf(']') < colon ? endpoint[..colon] : endpoint;
    }

    private readonly record struct SessionRow(CaptureSession Session, long Start, long End, string Host, bool Reconnect, bool Failed);
}
