using System.Runtime.InteropServices;
using NexNet.Inspector.Core.Text;

namespace NexNet.Inspector.Cli;

/// <summary>Why a tail stopped before its source ended.</summary>
internal enum TailStop : byte
{
    /// <summary>Still running, or the source ended.</summary>
    None = 0,

    /// <summary><c>--max-lines</c> or <c>--max-tokens</c>.</summary>
    Budget = 1,

    /// <summary>The <c>--until</c> condition held.</summary>
    Until = 2,

    /// <summary>Ctrl+C or <c>--duration</c>, in the middle of a batch.</summary>
    Cancelled = 3,
}

/// <summary>
/// The output stage of <c>nexnet-tap tail</c>: takes the renderer's records (already filtered), applies
/// <c>--sample</c>, <c>--until</c> with its context, and the whole tail's budgets, and writes each line through one
/// <see cref="TextRecordWriter"/>, flushing per line.
/// </summary>
internal sealed class TailPrinter
{
    private readonly TextWriter _output;
    private readonly TextRecordWriter _writer;
    private readonly TailUntil? _until;
    private readonly int _context;
    private readonly int _sample;
    private readonly int _maxLines;
    private readonly long _maxTokens;
    private readonly bool _resumable;
    private readonly Queue<Held> _ring = new();
    private Held? _header;
    private long _seen;

    /// <param name="output">Where lines go.</param>
    /// <param name="options">The renderer options (format).</param>
    /// <param name="args">The command line: sample, until, context, budgets.</param>
    /// <param name="resumable">True when cursors resume the source (a file); a live attach prints none.</param>
    public TailPrinter(TextWriter output, TextOptions options, CliArguments args, bool resumable)
    {
        _output = output;
        _writer = new TextRecordWriter(options);
        _until = args.Until;
        _context = args.Context;
        _sample = Math.Max(1, args.Sample);
        _maxLines = args.MaxLines;
        _maxTokens = args.MaxTokens;
        _resumable = resumable;
    }

    /// <summary>Lines written (stop lines not counted).</summary>
    public int Lines { get; private set; }

    /// <summary>Approximate tokens written.</summary>
    public long Tokens { get; private set; }

    /// <summary>Why the printer stopped the tail.</summary>
    public TailStop Stopped { get; private set; }

    /// <summary>True once the <c>--until</c> condition held.</summary>
    public bool UntilMet { get; private set; }

    /// <summary>Where a batch cut by cancellation resumes.</summary>
    public TextCursor? CancelledAt { get; private set; }

    /// <summary>The renderer whose batch cancellation cut (<see cref="CancelledAt"/>).</summary>
    public TextRenderer? CancelledIn { get; private set; }

    /// <summary>
    /// Writes a batch of one renderer's records. False once the tail must stop: a budget was hit or <c>--until</c>
    /// held (the printer wrote the stop line), or <paramref name="cancel"/> fired (<see cref="CancelledAt"/>).
    /// </summary>
    /// <param name="records">The records, in order.</param>
    /// <param name="renderer">The renderer that produced them (session, level and filter of the cursors).</param>
    /// <param name="tag">The session tag of interleaved output; null for none.</param>
    /// <param name="cancel">Ctrl+C or <c>--duration</c>.</param>
    public bool Write(List<TextRecord> records, TextRenderer renderer, string? tag, CancellationToken cancel)
    {
        if (Stopped != TailStop.None)
            return false;

        var span = CollectionsMarshal.AsSpan(records);
        for (var i = 0; i < span.Length; i++)
        {
            ref readonly var record = ref span[i];
            if (cancel.IsCancellationRequested)
            {
                CancelledAt = CursorAt(renderer, record);
                CancelledIn = renderer;
                Stopped = TailStop.Cancelled;
                return false;
            }

            var sampled = IsAlwaysShown(record) || _sample <= 1 || _seen++ % _sample == 0;
            if (_until is not null)
            {
                if (_until.Matches(record))
                {
                    UntilMet = true;

                    // The session's header first, so the context says which session it is.
                    if (_header is { } header && (_ring.Count == 0 || _ring.Peek().Record.Kind != TextRecordKind.Header)
                        && record.Kind != TextRecordKind.Header && !Emit(header.Record, header.Renderer, header.Tag, withheld: 1))
                        return false;
                    while (_ring.TryDequeue(out var held))
                    {
                        if (!Emit(held.Record, held.Renderer, held.Tag, withheld: 1))
                            return false;
                    }

                    if (!Emit(record, renderer, tag, withheld: 1))
                        return false;
                    var next = i + 1 < span.Length ? CursorAt(renderer, span[i + 1]) : renderer.Position;
                    WriteStop("until " + _until.Text + " matched", _resumable ? next : null, tag);
                    Stopped = TailStop.Until;
                    return false;
                }

                if (record.Kind == TextRecordKind.Header)
                    _header = new Held(record, renderer, tag);
                if (sampled && _context > 0)
                {
                    _ring.Enqueue(new Held(record, renderer, tag));
                    if (_ring.Count > _context)
                        _ring.Dequeue();
                }

                continue;
            }

            if (sampled && !Emit(record, renderer, tag, Withheld(span[(i + 1)..]) + 1))
                return false;
        }

        return true;
    }

    /// <summary>Writes a stop line: <c>… stopped: &lt;reason&gt;, N lines[, --after &lt;cursor&gt;]</c>.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="cursor">Where to resume; dropped for a live source.</param>
    /// <param name="tag">The session tag of interleaved output.</param>
    public void WriteStop(string reason, TextCursor? cursor, string? tag)
    {
        _writer.SessionTag = tag;
        _writer.WriteStop(_output, reason, Lines, _resumable ? cursor : null);
        _output.Flush();
    }

    private static bool IsAlwaysShown(in TextRecord record) =>
        record.Kind is TextRecordKind.Header or TextRecordKind.End or TextRecordKind.Stat;

    private static TextCursor CursorAt(TextRenderer renderer, in TextRecord record) =>
        record.CursorAt(renderer.Session.SessionId, renderer.Options.Level, renderer.Options.Filter.Fingerprint());

    // Lines the rest of a batch would still write, after sampling.
    private int Withheld(ReadOnlySpan<TextRecord> rest)
    {
        var count = 0;
        var seen = _seen;
        foreach (ref readonly var record in rest)
        {
            if (IsAlwaysShown(record) || _sample <= 1 || seen++ % _sample == 0)
                count++;
        }

        return count;
    }

    private bool Emit(in TextRecord record, TextRenderer renderer, string? tag, int withheld)
    {
        _writer.SessionTag = tag;
        var cost = _writer.Measure(record);
        if ((_maxLines > 0 && Lines + 1 > _maxLines) || (_maxTokens > 0 && Tokens + cost > _maxTokens))
        {
            if (_resumable)
                _writer.WriteBudget(_output, withheld, CursorAt(renderer, record));
            else
                _writer.WriteStop(_output, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"budget hit, {withheld} withheld"), Lines, null);
            _output.Flush();
            Stopped = TailStop.Budget;
            return false;
        }

        _writer.Write(_output);
        _output.Flush();
        Lines++;
        Tokens += cost;
        return true;
    }

    private readonly record struct Held(TextRecord Record, TextRenderer Renderer, string? Tag);
}
