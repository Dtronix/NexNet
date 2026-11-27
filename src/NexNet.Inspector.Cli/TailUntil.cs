using System.Diagnostics.CodeAnalysis;
using NexNet.Inspector.Core.Text;
using NexNet.Messages;

namespace NexNet.Inspector.Cli;

/// <summary>What one <c>--until</c> term tests.</summary>
internal enum TailUntilTerm : byte
{
    /// <summary><c>method=&lt;glob&gt;</c>: a call or result line of a matching method.</summary>
    Method = 0,

    /// <summary><c>state=&lt;state&gt;</c>: a call or result line with that outcome.</summary>
    State = 1,

    /// <summary><c>error</c>: what <c>--only errors</c> keeps.</summary>
    Error = 2,

    /// <summary><c>gap</c>: a gap line.</summary>
    Gap = 3,

    /// <summary><c>type=&lt;kind|frame type&gt;</c>: a line kind (call, item, pipe, …) or a frame type (frames level).</summary>
    Type = 4,

    /// <summary><c>end</c>: the session's end line.</summary>
    End = 5,
}

/// <summary>An invocation outcome <c>state=</c> names.</summary>
internal enum TailCallState : byte
{
    Ok = 0,
    Exception = 1,
    Canceled = 2,
    Unauthorized = 3,
    Refused = 4,
    Pending = 5,
    Oneway = 6,
}

/// <summary>One parsed term.</summary>
internal readonly record struct TailUntilCondition(TailUntilTerm Term, string? Glob = null, TailCallState State = default,
    TextRecordKind Kind = default, MessageType? FrameType = null);

/// <summary>
/// The <c>tail --until</c> expression: alternatives separated by <c>|</c>, each a list of terms separated by
/// <c>,</c> that must all hold. Terms: <c>method=&lt;glob&gt;</c>, <c>state=ok|exception|canceled|unauthorized|refused|pending|oneway</c>,
/// <c>error</c>, <c>gap</c>, <c>end</c>, <c>type=&lt;line kind or frame type&gt;</c> (line kinds are the ndjson codes:
/// call, res, cancel, item, coll, data, pipe, noise, gap, end, frame, hex, prob, hs; frame types are names such as
/// <c>InvocationResult</c>). Example: <c>method=Send*,state=exception|gap</c>.
/// </summary>
internal sealed class TailUntil
{
    private readonly TailUntilCondition[][] _alternatives;

    private TailUntil(string text, TailUntilCondition[][] alternatives)
    {
        Text = text;
        _alternatives = alternatives;
    }

    /// <summary>The expression as given.</summary>
    public string Text { get; }

    public static bool TryParse(string text, [NotNullWhen(true)] out TailUntil? until, [NotNullWhen(false)] out string? error)
    {
        until = null;
        error = null;
        var alternatives = text.Split('|', StringSplitOptions.TrimEntries);
        var parsed = new TailUntilCondition[alternatives.Length][];
        for (var a = 0; a < alternatives.Length; a++)
        {
            var terms = alternatives[a].Split(',', StringSplitOptions.TrimEntries);
            parsed[a] = new TailUntilCondition[terms.Length];
            for (var t = 0; t < terms.Length; t++)
            {
                if (!TryParseTerm(terms[t], out parsed[a][t], out error))
                    return false;
            }
        }

        until = new TailUntil(text, parsed);
        return true;
    }

    /// <summary>True when the line meets the expression.</summary>
    public bool Matches(in TextRecord record)
    {
        foreach (var alternative in _alternatives)
        {
            var all = true;
            foreach (var condition in alternative)
            {
                if (!Holds(condition, record))
                {
                    all = false;
                    break;
                }
            }

            if (all)
                return true;
        }

        return false;
    }

    private static bool Holds(in TailUntilCondition condition, in TextRecord record)
    {
        switch (condition.Term)
        {
            case TailUntilTerm.Method:
                if (record.Kind is TextRecordKind.Call or TextRecordKind.Result && record.Name is { } name)
                    return TailFilter.Glob(condition.Glob, name);
                return TailFilter.TryFrameMethod(record, out var method) && TailFilter.Glob(condition.Glob, method);
            case TailUntilTerm.State:
                return record.Kind is TextRecordKind.Call or TextRecordKind.Result && StateOf(record.Outcome) == condition.State;
            case TailUntilTerm.Error:
                return TailFilter.IsError(record);
            case TailUntilTerm.Gap:
                return record.Kind == TextRecordKind.Gap;
            case TailUntilTerm.End:
                return record.Kind == TextRecordKind.End;
            default:
                if (condition.FrameType is { } type)
                    return record.Kind == TextRecordKind.Frame && record.Name == TextRenderer.TypeName(type);
                return record.Kind == condition.Kind;
        }
    }

    private static TailCallState? StateOf(string? outcome) => outcome switch
    {
        "ok" => TailCallState.Ok,
        "exception" => TailCallState.Exception,
        "canceled" => TailCallState.Canceled,
        "unauthorized" => TailCallState.Unauthorized,
        "refused" => TailCallState.Refused,
        "oneway" => TailCallState.Oneway,
        "…" or "… cancel sent" => TailCallState.Pending,
        _ => null,
    };

    private static bool TryParseTerm(string term, out TailUntilCondition condition, [NotNullWhen(false)] out string? error)
    {
        condition = default;
        error = null;
        var equals = term.IndexOf('=', StringComparison.Ordinal);
        var name = equals < 0 ? term : term[..equals].Trim();
        var value = equals < 0 ? null : term[(equals + 1)..].Trim();
        switch (name)
        {
            case "error" or "errors" when value is null:
                condition = new TailUntilCondition(TailUntilTerm.Error);
                return true;
            case "gap" when value is null:
                condition = new TailUntilCondition(TailUntilTerm.Gap);
                return true;
            case "end" when value is null:
                condition = new TailUntilCondition(TailUntilTerm.End);
                return true;
            case "method" when !string.IsNullOrEmpty(value):
                condition = new TailUntilCondition(TailUntilTerm.Method, Glob: value);
                return true;
            case "state" when value is not null:
                TailCallState? state = value switch
                {
                    "ok" => TailCallState.Ok,
                    "exception" => TailCallState.Exception,
                    "canceled" or "cancelled" => TailCallState.Canceled,
                    "unauthorized" => TailCallState.Unauthorized,
                    "refused" => TailCallState.Refused,
                    "pending" => TailCallState.Pending,
                    "oneway" => TailCallState.Oneway,
                    _ => null,
                };
                if (state is null)
                {
                    error = $"state= takes ok, exception, canceled, unauthorized, refused, pending or oneway, not '{value}'.";
                    return false;
                }

                condition = new TailUntilCondition(TailUntilTerm.State, State: state.Value);
                return true;
            case "type" when !string.IsNullOrEmpty(value):
                if (TryKind(value, out var kind))
                {
                    condition = new TailUntilCondition(TailUntilTerm.Type, Kind: kind);
                    return true;
                }

                if (!char.IsAsciiDigit(value[0]) && value[0] != '-' && Enum.TryParse<MessageType>(value, ignoreCase: true, out var type)
                    && Enum.IsDefined(type))
                {
                    condition = new TailUntilCondition(TailUntilTerm.Type, FrameType: type);
                    return true;
                }

                error = $"type= takes a line kind (call, res, cancel, item, coll, data, pipe, noise, gap, end, frame, hex, prob, hs) or a frame type (InvocationResult, …), not '{value}'.";
                return false;
            default:
                error = $"'{term}' is not a condition: use method=<glob>, state=<state>, error, gap, end or type=<kind> (',' = and, '|' = or).";
                return false;
        }
    }

    private static bool TryKind(string code, out TextRecordKind kind)
    {
        foreach (var candidate in Enum.GetValues<TextRecordKind>())
        {
            if (TextRecordWriter.Code(candidate) == code)
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}
