using System.Text;

namespace NexNet.Inspector.Cli;

/// <summary>
/// A thread-safe writer that keeps what a background command (a live tail, a recording) printed as numbered lines, so
/// an MCP client can page through them with a line cursor. Holds the last <see cref="Capacity"/> lines; older ones are
/// dropped (and counted).
/// </summary>
internal sealed class McpLineBuffer : TextWriter
{
    private readonly object _gate = new();
    private readonly StringBuilder _partial = new();
    private readonly List<string> _lines = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Absolute number of the first line held (lines before it were dropped).
    private long _first;

    /// <param name="capacity">How many lines are kept.</param>
    public McpLineBuffer(int capacity = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        NewLine = "\n";
    }

    public int Capacity { get; }

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>Lines written so far, including dropped ones: the cursor past the last line.</summary>
    public long Count
    {
        get
        {
            lock (_gate)
                return _first + _lines.Count;
        }
    }

    /// <summary>Completes when a line arrives after this call (or the buffer is <see cref="Complete"/>d).</summary>
    public Task Changed
    {
        get
        {
            lock (_gate)
                return _changed.Task;
        }
    }

    /// <summary>
    /// Copies up to <paramref name="max"/> lines from line number <paramref name="after"/> (0 = the first line).
    /// </summary>
    /// <returns>The cursor after the last line copied.</returns>
    /// <param name="after">The first line wanted.</param>
    /// <param name="max">At most this many lines.</param>
    /// <param name="lines">Receives the lines.</param>
    /// <param name="dropped">Lines at or after <paramref name="after"/> that were dropped before they were read.</param>
    public long Read(long after, int max, List<string> lines, out long dropped)
    {
        lock (_gate)
        {
            var start = Math.Max(after, _first);
            dropped = start - Math.Max(after, 0);
            var end = Math.Min(_first + _lines.Count, start + max);
            for (var i = start; i < end; i++)
                lines.Add(_lines[(int)(i - _first)]);
            return Math.Max(end, start);
        }
    }

    /// <summary>Ends a partial last line and wakes readers for good.</summary>
    public void Complete()
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (_partial.Length > 0)
                CommitLocked();
            changed = _changed;
        }

        changed.TrySetResult();
    }

    public override void Write(char value) => Write(new ReadOnlySpan<char>(in value));

    public override void Write(string? value) => Write(value.AsSpan());

    public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));

    public override void Write(ReadOnlySpan<char> buffer)
    {
        TaskCompletionSource? changed = null;
        lock (_gate)
        {
            while (!buffer.IsEmpty)
            {
                var newline = buffer.IndexOf('\n');
                var part = newline < 0 ? buffer : buffer[..newline];
                if (!part.IsEmpty && part[^1] == '\r')
                    part = part[..^1];
                _partial.Append(part);
                if (newline < 0)
                    break;
                CommitLocked();
                if (changed is null)
                {
                    changed = _changed;
                    _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                buffer = buffer[(newline + 1)..];
            }
        }

        changed?.TrySetResult();
    }

    public override void WriteLine(string? value)
    {
        Write(value.AsSpan());
        Write('\n');
    }

    public override void WriteLine() => Write('\n');

    public override Task WriteAsync(char value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(string? value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(char[] buffer, int index, int count)
    {
        Write(buffer, index, count);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(char value)
    {
        Write(value);
        WriteLine();
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync()
    {
        WriteLine();
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        WriteLine();
        return Task.CompletedTask;
    }

    public override Task FlushAsync() => Task.CompletedTask;

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Everything held, one line per line (tests, short command output).</summary>
    public override string ToString()
    {
        lock (_gate)
            return string.Join('\n', _lines) + (_partial.Length > 0 ? (_lines.Count > 0 ? "\n" : "") + _partial : "");
    }

    private void CommitLocked()
    {
        _lines.Add(_partial.ToString());
        _partial.Clear();
        if (_lines.Count > Capacity)
        {
            // Drop in chunks: one copy per many lines.
            var drop = Math.Min(_lines.Count, Math.Max(_lines.Count - Capacity, Capacity / 10));
            _lines.RemoveRange(0, drop);
            _first += drop;
        }
    }
}
