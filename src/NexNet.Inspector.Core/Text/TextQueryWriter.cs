namespace NexNet.Inspector.Core.Text;

/// <summary>
/// Writes query records (<see cref="TextQuery"/>) of one or more sessions as text or ndjson within
/// <see cref="TextOptions.MaxLines"/> and <see cref="TextOptions.MaxTokens"/>: a query that hits a budget ends with the
/// renderer's budget line, and its cursor (`--after`) resumes the same query at the first withheld line.
/// </summary>
/// <remarks>
/// A query cursor is a <see cref="TextCursor"/> whose <see cref="TextCursor.Skip"/> counts the session's lines already
/// written and whose filter is the query's fingerprint (<see cref="Fingerprint"/> of its command line), so it resumes only the
/// same query; sessions before the cursor's are skipped. Text output puts a blank line between sessions.
/// </remarks>
public sealed class TextQueryWriter
{
    private readonly TextOptions _options;
    private readonly ulong _fingerprint;
    private readonly TextRecordWriter _writer;

    /// <summary>Creates a writer.</summary>
    /// <param name="options">Format, budgets and <see cref="TextOptions.After"/>.</param>
    /// <param name="query">A canonical text of the query (command, IDs, options that change its lines) for cursors.</param>
    /// <exception cref="ArgumentException"><see cref="TextOptions.After"/> was made by another query.</exception>
    public TextQueryWriter(TextOptions options, string query)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(query);
        _options = options;
        _fingerprint = Fingerprint(query);
        _writer = new TextRecordWriter(options);
        if (options.After is { } after && after.Filter != _fingerprint)
            throw new ArgumentException("The cursor was made by another query.", nameof(options));
    }

    /// <summary>A stable 64-bit hash of a query's text.</summary>
    /// <param name="query">The query.</param>
    public static ulong Fingerprint(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var hash = 0xcbf29ce484222325UL;
        foreach (var c in query)
        {
            hash ^= c;
            hash *= 0x100000001b3UL;
        }

        return hash;
    }

    /// <summary>Writes the records of each session in order, within the budgets.</summary>
    /// <param name="output">Where lines go.</param>
    /// <param name="sessions">Each session's ID and records.</param>
    /// <returns>Lines written and withheld, tokens, and the cursor to resume at.</returns>
    public TextRenderResult Write(TextWriter output, IReadOnlyList<(long SessionId, List<TextRecord> Records)> sessions)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sessions);
        var start = 0;
        var skip = 0;
        if (_options.After is { } after)
        {
            start = sessions.Count;
            for (var i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].SessionId == after.SessionId)
                {
                    start = i;
                    skip = after.Skip;
                    break;
                }
            }
        }

        var lines = 0;
        var withheld = 0;
        long tokens = 0;
        TextCursor? next = null;
        var written = false;
        for (var s = start; s < sessions.Count; s++)
        {
            var (sessionId, records) = sessions[s];
            var first = s == start ? Math.Min(skip, records.Count) : 0;
            if (next is not null)
            {
                withheld += records.Count - first;
                continue;
            }

            if (written && _options.Format == TextFormat.Text && first < records.Count)
                output.Write('\n');
            for (var r = first; r < records.Count; r++)
            {
                var cost = _writer.Measure(records[r]);
                if ((_options.MaxLines > 0 && lines + 1 > _options.MaxLines) || (_options.MaxTokens > 0 && tokens + cost > _options.MaxTokens))
                {
                    next = new TextCursor(sessionId, _options.Level, 1, 0, 0, r, _fingerprint);
                    withheld += records.Count - r;
                    break;
                }

                _writer.Write(output);
                written = true;
                lines++;
                tokens += cost;
            }
        }

        if (next is { } cursor)
        {
            _writer.WriteBudget(output, withheld, cursor);
            return new TextRenderResult(lines, withheld, tokens, cursor, false);
        }

        var end = sessions.Count > 0 ? sessions[^1].SessionId : 0;
        return new TextRenderResult(lines, 0, tokens, new TextCursor(end, _options.Level, 2, 0, 0, 0, _fingerprint), true);
    }
}
