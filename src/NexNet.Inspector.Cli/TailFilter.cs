using System.Globalization;
using System.Text;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;

namespace NexNet.Inspector.Cli;

/// <summary>What <c>tail --only</c> keeps (any of them).</summary>
[Flags]
internal enum TailOnly : byte
{
    None = 0,

    /// <summary>Calls that failed (exception, unauthorized, refused), refused pipes, a failed handshake, problem frames.</summary>
    Errors = 1 << 0,

    /// <summary>Calls whose duration reached <c>--slow</c>.</summary>
    Slow = 1 << 1,

    /// <summary>Canceled calls and cancellations.</summary>
    Cancel = 1 << 2,

    /// <summary>Gaps: ops the capture dropped.</summary>
    Gaps = 1 << 3,
}

/// <summary>
/// A <c>--pipe</c> ID: the pipe's client and server local IDs (`4.4`, `4·4`, `≋4·4`), or one ID matching either
/// side (`4`).
/// </summary>
internal readonly record struct TailPipeId(int Client, int Server)
{
    /// <summary>Server for an ID that matches either side.</summary>
    public const int Either = -1;

    public static bool TryParse(string text, out TailPipeId id)
    {
        id = default;
        var span = text.AsSpan().Trim();
        if (!span.IsEmpty && span[0] is '≋' or '☰' or '⇄')
            span = span[1..];
        var separator = span.IndexOfAny('·', '.');
        if (separator < 0)
        {
            if (!byte.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out var one))
                return false;
            id = new TailPipeId(one, Either);
            return true;
        }

        if (!byte.TryParse(span[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var client)
            || !byte.TryParse(span[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var server))
            return false;
        id = new TailPipeId(client, server);
        return true;
    }

    /// <summary>Reads a rendered pipe ID (`≋4·4`) back.</summary>
    public static bool TryRead(ReadOnlySpan<char> rendered, out int client, out int server)
    {
        client = server = 0;
        if (rendered.IsEmpty || rendered[0] is not ('≋' or '☰' or '⇄'))
            return false;
        rendered = rendered[1..];
        var end = rendered.IndexOf(' ');
        if (end >= 0)
            rendered = rendered[..end];
        var dot = rendered.IndexOf('·');
        return dot > 0
               && int.TryParse(rendered[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out client)
               && int.TryParse(rendered[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out server);
    }

    public bool Matches(int client, int server) =>
        Server == Either ? client == Client || server == Client : client == Client && server == Server;

    public override string ToString() => Server == Either
        ? Client.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Client}.{Server}");
}

/// <summary>
/// The content filters of <c>nexnet-tap tail</c> (<c>--method</c>, <c>--pipe</c>, <c>--only</c>, <c>--slow</c>,
/// <c>--grep</c>) as a <see cref="TextFilter.Match"/> test, with a <see cref="TextFilter.MatchKey"/> so cursors tell
/// filtered renderings apart. <c>--dir</c> is the renderer's own <see cref="TextFilter.Directions"/>. Header, summary
/// and end lines always pass. One instance per renderer: it reads the session under the renderer's gate.
/// </summary>
internal sealed class TailFilter
{
    private readonly CliArguments _args;
    private readonly CaptureSession _session;
    private readonly StringBuilder _line = new();
    private readonly double _slowMs;
    private bool _frameKept = true;

    private TailFilter(CliArguments args, CaptureSession session)
    {
        _args = args;
        _session = session;
        _slowMs = args.Slow.TotalMilliseconds;
    }

    /// <summary>Set once the renderer exists: method names come from it.</summary>
    public TextRenderer? Renderer { get; set; }

    /// <summary>True when any content filter is given.</summary>
    public static bool IsActive(CliArguments args) =>
        args.Methods.Length > 0 || args.Pipes.Length > 0 || args.Only != TailOnly.None || args.Grep is not null;

    /// <summary>The canonical text of the content filters, for the cursor fingerprint; null when none is given.</summary>
    public static string? Key(CliArguments args)
    {
        if (!IsActive(args))
            return null;
        var key = new StringBuilder();
        key.Append("m=").AppendJoin(',', args.Methods);
        key.Append(";p=").AppendJoin(',', args.Pipes);
        key.Append(CultureInfo.InvariantCulture, $";o={(int)args.Only}");
        if ((args.Only & TailOnly.Slow) != 0)
            key.Append(CultureInfo.InvariantCulture, $";s={args.Slow.Ticks}");
        if (args.Grep is { } grep)
            key.Append(";g=").Append(grep);
        return key.ToString();
    }

    /// <summary>The renderer filter of a tail: directions, and the content test when any is given.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="session">The session the renderer reads; null for the fingerprint alone.</param>
    /// <param name="filter">The tail filter to hand the renderer to; null without content filters.</param>
    public static TextFilter Create(CliArguments args, CaptureSession? session, out TailFilter? filter)
    {
        filter = null;
        var key = Key(args);
        if (key is null)
            return new TextFilter { Directions = args.Directions };
        if (session is null)
            return new TextFilter { Directions = args.Directions, MatchKey = key, Match = static _ => true };
        var tail = new TailFilter(args, session);
        filter = tail;
        return new TextFilter { Directions = args.Directions, MatchKey = key, Match = tail.Matches };
    }

    /// <summary>A call, result or frame that reports a failure, a refused pipe, a failed handshake, a problem frame.</summary>
    public static bool IsError(in TextRecord record) => record.Kind switch
    {
        TextRecordKind.Call or TextRecordKind.Result => record.Outcome is "exception" or "unauthorized" or "refused",
        TextRecordKind.Problem => true,
        TextRecordKind.Pipe => record.Value is "refused",
        TextRecordKind.Handshake => record.Outcome is "fail",
        TextRecordKind.Frame => record.Name is "Malformed"
                                || record.Outcome?.Contains('!', StringComparison.Ordinal) == true
                                || record.Value?.EndsWith(" exception", StringComparison.Ordinal) == true
                                || record.Value?.EndsWith(" unauthorized", StringComparison.Ordinal) == true,
        _ => false,
    };

    /// <summary>A canceled call, a call whose caller canceled, a cancellation.</summary>
    public static bool IsCancel(in TextRecord record) => record.Kind switch
    {
        TextRecordKind.Cancel => true,
        TextRecordKind.Call or TextRecordKind.Result => record.Outcome is "canceled" or "… cancel sent",
        TextRecordKind.Frame => record.Name is "InvocationCancellation",
        _ => false,
    };

    /// <summary>Glob match (`*`, `?`), case-insensitive.</summary>
    public static bool Glob(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    /// <summary>The method a frame line names: `#5 SendMessage …` gives `SendMessage`.</summary>
    public static bool TryFrameMethod(in TextRecord record, out ReadOnlySpan<char> method)
    {
        method = default;
        if (record.Kind != TextRecordKind.Frame || record.Value is not { Length: > 1 } value || value[0] != '#')
            return false;
        var rest = value.AsSpan();
        var space = rest.IndexOf(' ');
        if (space < 0)
            return false;
        rest = rest[(space + 1)..];
        var end = rest.IndexOf(' ');
        method = end < 0 ? rest : rest[..end];
        return true;
    }

    private bool Matches(TextRecord record)
    {
        switch (record.Kind)
        {
            case TextRecordKind.Header:
            case TextRecordKind.Stat:
            case TextRecordKind.End:
                return true;
            case TextRecordKind.Bytes:
                return _frameKept;
        }

        var kept = Test(record);
        if (record.Kind == TextRecordKind.Frame)
            _frameKept = kept;
        return kept;
    }

    private bool Test(in TextRecord record)
    {
        if (_args.Methods.Length > 0 || _args.Pipes.Length > 0)
        {
            var named = _args.Methods.Length > 0 && TryMethod(record, out var method) && AnyGlob(method);
            var piped = !named && _args.Pipes.Length > 0 && TryPipe(record, out var client, out var server) && AnyPipe(client, server);
            if (!named && !piped)
                return false;
        }

        var only = _args.Only;
        if (only != TailOnly.None)
        {
            var any = ((only & TailOnly.Errors) != 0 && IsError(record))
                      || ((only & TailOnly.Cancel) != 0 && IsCancel(record))
                      || ((only & TailOnly.Gaps) != 0 && record.Kind == TextRecordKind.Gap)
                      || ((only & TailOnly.Slow) != 0 && record.Kind is TextRecordKind.Call or TextRecordKind.Result
                                                      && record.DurationMs >= _slowMs);
            if (!any)
                return false;
        }

        if (_args.Grep is { } grep)
        {
            _line.Clear();
            TextRecordWriter.AppendText(_line, record);
            if (!Contains(_line, grep))
                return false;
        }

        return true;
    }

    private bool AnyGlob(ReadOnlySpan<char> name)
    {
        foreach (var glob in _args.Methods)
        {
            if (Glob(glob, name))
                return true;
        }

        return false;
    }

    private bool AnyPipe(int client, int server)
    {
        foreach (var pipe in _args.Pipes)
        {
            if (pipe.Matches(client, server))
                return true;
        }

        return false;
    }

    private bool TryMethod(in TextRecord record, out ReadOnlySpan<char> method)
    {
        method = default;
        switch (record.Kind)
        {
            case TextRecordKind.Call:
            case TextRecordKind.Result:
                method = record.Name;
                return record.Name is not null;
            case TextRecordKind.Cancel:
                var invocations = _session.Assembler.Invocations;
                if (Renderer is null || (uint)record.Entity >= (uint)invocations.Length)
                    return false;
                method = Renderer.MethodName(invocations[record.Entity]);
                return true;
            default:
                return TryFrameMethod(record, out method);
        }
    }

    private bool TryPipe(in TextRecord record, out int client, out int server)
    {
        client = server = 0;
        switch (record.Kind)
        {
            case TextRecordKind.Item:
            case TextRecordKind.Collection:
            case TextRecordKind.Data:
            case TextRecordKind.Pipe:
                return record.Id is { } id && TailPipeId.TryRead(id, out client, out server);
            case TextRecordKind.Noise:
            case TextRecordKind.Frame:
                // "window ≋4·4" / "≋4·4 LogLine …": the pipe ID after the first word, or first.
                if (record.Value is not { } value)
                    return false;
                if (TailPipeId.TryRead(value, out client, out server))
                    return true;
                var space = value.IndexOf(' ', StringComparison.Ordinal);
                return space > 0 && TailPipeId.TryRead(value.AsSpan(space + 1), out client, out server);
            default:
                return false;
        }
    }

    private static bool Contains(StringBuilder line, string text) =>
        line.ToString().Contains(text, StringComparison.OrdinalIgnoreCase);
}
