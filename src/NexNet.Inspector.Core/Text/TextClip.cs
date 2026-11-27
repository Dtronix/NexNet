using System.Globalization;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.Core.Text;

/// <summary>How <see cref="TextClip.Entity"/> describes an entity.</summary>
public enum TextClipDetail : byte
{
    /// <summary>The entity as `show` writes it: its line and decoded tree.</summary>
    Show = 0,

    /// <summary>The entity as `trace` writes it: the frames and transport ops that carried its messages.</summary>
    Trace = 1,
}

/// <summary>
/// "Copy for LLM" (the app's content, timeline and app menus): a part of a session in the text format
/// (docs/internals/inspector-text-format.md), the session's header line first, within a token budget
/// (<see cref="DefaultMaxTokens"/> unless the options set one). A time range is its messages-level lines; an entity is
/// its `show` or `trace` query. Built on <see cref="TextRenderer"/>, <see cref="TextQuery"/> and
/// <see cref="TextQueryWriter"/>, so a clip reads exactly like the CLI's output and a budget hit ends with the same
/// resumable budget line.
/// </summary>
public static class TextClip
{
    /// <summary>The budget of a clip when the options set none: about 4k tokens.</summary>
    public const long DefaultMaxTokens = 4000;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// The lines of <paramref name="session"/> that start inside <paramref name="window"/> (messages level by default),
    /// after its header and a `clip &lt;label&gt; +a..+b` line.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="window">The range, in the session's Stopwatch ticks.</param>
    /// <param name="label">What the range is ("view", "scope", "trace Invocation #5").</param>
    /// <param name="options">Level, widths and budget; the default budget is <see cref="DefaultMaxTokens"/>.</param>
    public static string Range(CaptureSession session, TimeWindow window, string label, TextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(label);
        var o = Budgeted(options);
        if (o.Level == TextLevel.Summary)
            o = o with { Level = TextLevel.Messages };
        var records = new List<TextRecord>();
        new TextRenderer(session, o with { After = null, MaxLines = 0, MaxTokens = 0, Header = true, Filter = o.Filter with { Window = window } })
            .Collect(records);

        double from, to;
        lock (session.Gate)
        {
            var frequency = Math.Max(1, session.Header.StopwatchFrequency);
            from = TimeWindow.MillisecondsFromTicks(window.Start - session.StartTicks, frequency);
            to = TimeWindow.MillisecondsFromTicks(window.End - session.StartTicks, frequency);
        }

        var at = records.Count > 0 && records[0].Kind == TextRecordKind.Header ? 1 : 0;
        records.Insert(at, new TextRecord
        {
            Kind = TextRecordKind.Stat,
            Ticks = window.Start,
            LastTicks = window.Start,
            Value = string.Create(Invariant, $"clip {label} +{from:0.0}..+{to:0.0} ({to - from:0.0}ms)"),
            Count = 1,
            Bytes = -1,
            Frame = -1,
            Entity = -1,
        });
        return Write(session, o, "clip " + label, records);
    }

    /// <summary>One entity (`#5`, `≋4·4`, `f18`) as `show` or `trace` writes it, after the session's header.</summary>
    /// <param name="session">The session.</param>
    /// <param name="id">A query ID (<see cref="TextQuery.TryResolve"/>).</param>
    /// <param name="detail">Show or trace.</param>
    /// <param name="options">Widths and budget; values default to 64 characters and depth 8, as the CLI's show.</param>
    /// <returns>The text; null when the session holds no such entity.</returns>
    public static string? Entity(CaptureSession session, string id, TextClipDetail detail, TextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(id);
        var o = Budgeted(options ?? new TextOptions { ValueWidth = 64, Depth = 8 });
        var records = new List<TextRecord>();
        var query = new TextQuery(session, o);
        var found = detail == TextClipDetail.Trace ? query.Trace(id, records) : query.Show(id, false, records);
        if (found <= 0)
            return null;
        return Write(session, o, (detail == TextClipDetail.Trace ? "trace " : "show ") + id, records);
    }

    /// <summary>The query ID of an invocation or pipe (`#5`, `≋4·4`); null for another entity kind or index.</summary>
    /// <param name="session">The session.</param>
    /// <param name="kind">The entity's kind.</param>
    /// <param name="index">Its index in the assembler's list.</param>
    public static string? IdOf(CaptureSession session, EntityKind kind, int index)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (session.Gate)
        {
            var ids = new TextRenderer(session);
            return kind switch
            {
                EntityKind.Invocation when (uint)index < (uint)session.Assembler.Invocations.Length => ids.InvocationId(index),
                EntityKind.Pipe when (uint)index < (uint)session.Assembler.Pipes.Length => ids.PipeId(index),
                _ => null,
            };
        }
    }

    /// <summary>The query ID of a frame (`f18`: its place in the session's frame order, from 1); null when not in it.</summary>
    /// <param name="session">The session.</param>
    /// <param name="frame">The frame.</param>
    public static string? IdOf(CaptureSession session, FrameKey frame)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (session.Gate)
        {
            var order = session.FrameOrder;
            for (var i = 0; i < order.Length; i++)
            {
                if (order[i] == frame)
                    return string.Create(Invariant, $"f{i + 1}");
            }
        }

        return null;
    }

    private static TextOptions Budgeted(TextOptions? options)
    {
        var o = options ?? TextOptions.Default;
        return o.MaxTokens > 0 || o.MaxLines > 0 ? o with { After = null } : o with { After = null, MaxTokens = DefaultMaxTokens };
    }

    private static string Write(CaptureSession session, TextOptions options, string query, List<TextRecord> records)
    {
        using var output = new StringWriter(Invariant);
        new TextQueryWriter(options, query).Write(output, [(session.SessionId, records)]);
        return output.ToString();
    }
}
