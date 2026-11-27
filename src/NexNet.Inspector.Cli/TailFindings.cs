using NexNet.Inspector.Core.Findings;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;

namespace NexNet.Inspector.Cli;

/// <summary>
/// <c>tail --findings</c>: one <see cref="FindingsEngine"/> per tailed session, updated after each batch of lines, its new
/// findings written as inline <c>!</c> lines (text or ndjson <c>find</c>, with the session tag of interleaved output).
/// Finding lines are extra: they do not count against the line and token budgets and carry no cursor.
/// </summary>
internal sealed class TailFindings(TextWriter output, TextOptions options, CliArguments args)
{
    private readonly Dictionary<CaptureSession, FindingsEngine> _engines = [];
    private readonly List<Finding> _found = [];
    private readonly TextRecordWriter _writer = new(options);
    private readonly FindingsOptions _options = args.Has(CliOption.Slow)
        ? new FindingsOptions { SlowCallMs = args.Slow.TotalMilliseconds }
        : FindingsOptions.Default;

    /// <summary>Writes what the session's detectors found since the last call.</summary>
    /// <param name="session">The session just rendered.</param>
    /// <param name="tag">The session tag of interleaved output; null for none.</param>
    public void Write(CaptureSession session, string? tag)
    {
        if (!_engines.TryGetValue(session, out var engine))
        {
            engine = new FindingsEngine(session, _options);
            _engines[session] = engine;
        }

        _found.Clear();
        if (engine.Update(_found) == 0)
            return;
        _writer.SessionTag = tag;
        foreach (var finding in _found)
        {
            _writer.Measure(FindingsText.ToRecord(finding));
            _writer.Write(output);
        }

        output.Flush();
    }
}
