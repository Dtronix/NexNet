using System.Globalization;
using System.Text;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.Core.Text;

public sealed partial class TextQuery
{
    private const int DiffFieldRows = 8;

    /// <summary>
    /// `diff a b`: the Δ list of a compare as text, worst first (severity, then time): one line per entity whose sides
    /// differ (`high #5 SendMessage only B`, `medium #2 SendMessage content`) or capture gap, then the field
    /// diffs of the <paramref name="top"/> worst entities that both sides hold (`~ args.message.Text "hello" → "hello everyone!"`).
    /// Times are compare time (ms from the earlier aligned start).
    /// </summary>
    /// <param name="compare">The compare (its current alignment).</param>
    /// <param name="options">Widths and format; <see cref="TextOptions.Default"/> when null.</param>
    /// <param name="top">Entities whose field diffs are listed.</param>
    /// <param name="destination">Receives the lines.</param>
    public static void Diff(CompareSession compare, TextOptions? options, int top, List<TextRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(compare);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= TextOptions.Default;
        var a = new TextQuery(compare.A, options);
        var b = new TextQuery(compare.B, options);
        var headerA = a.Header();
        var headerB = b.Header();
        destination.Add(a.Stat("A " + headerA.Id + " " + headerA.Value));
        destination.Add(a.Stat("B " + headerB.Id + " " + headerB.Value));

        var entries = compare.Deltas.Entries.ToArray();
        Array.Sort(entries, static (x, y) => y.Severity != x.Severity ? y.Severity.CompareTo(x.Severity) : DeltaList.Compare(x, y));
        var matches = compare.Matches;
        var differing = 0;
        foreach (ref readonly var match in matches)
        {
            if (match.HasDifferences)
                differing++;
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"deltas {entries.Length}");
        foreach (var severity in (ReadOnlySpan<DeltaSeverity>)[DeltaSeverity.High, DeltaSeverity.Medium, DeltaSeverity.Low, DeltaSeverity.Info])
        {
            var count = 0;
            foreach (var entry in entries)
            {
                if (entry.Severity == severity)
                    count++;
            }

            if (count > 0)
                sb.Append(' ').Append(SeverityWord(severity)).Append(CultureInfo.InvariantCulture, $" {count}");
        }

        sb.Append(CultureInfo.InvariantCulture, $"; entities {matches.Length} differing {differing}; aligned {compare.Alignment.ToString().ToLowerInvariant()}");
        destination.Add(a.Stat(sb.ToString()));

        var detailed = new List<int>();
        foreach (var entry in entries)
        {
            sb.Clear();
            string? id = null, name = null;
            if (entry.Kind == DeltaKind.Gap)
            {
                var session = compare.Get(entry.Side);
                lock (session.Gate)
                {
                    var gaps = session.Gaps;
                    if ((uint)entry.Gap < (uint)gaps.Length)
                    {
                        var gap = gaps[entry.Gap];
                        sb.Append(gap.Direction == Model.TapDirection.Outbound ? "out " : "in ")
                            .Append(CultureInfo.InvariantCulture, $"{gap.DroppedOps} ops ");
                        TextValues.AppendSize(sb, gap.DroppedBytes);
                    }
                }

                id = entry.Side == CompareSide.A ? "A" : "B";
                name = "GAP";
            }
            else
            {
                var match = matches[entry.Match];
                id = DiffId(a, b, match);
                name = compare.GetLabel(entry.Match);
                Differences(sb, compare, match);
                if (match.A.IsPresent && match.B.IsPresent && (match.Differences & (EntityDifferences.Content | EntityDifferences.Outcome | EntityDifferences.Items)) != 0
                    && detailed.Count < top)
                    detailed.Add(entry.Match);
            }

            destination.Add(new TextRecord
            {
                Kind = TextRecordKind.Delta,
                TimeMs = entry.Time / (CompareClock.Frequency / 1000.0),
                Outcome = SeverityWord(entry.Severity),
                Id = id,
                Name = name,
                Value = sb.ToString(),
                Count = 1,
                Bytes = -1,
                Frame = -1,
                Entity = entry.Match,
            });
        }

        foreach (var index in detailed)
            FieldDiffs(a, b, compare, index, destination);
    }

    private static string SeverityWord(DeltaSeverity severity) => severity switch
    {
        DeltaSeverity.High => "high",
        DeltaSeverity.Medium => "medium",
        DeltaSeverity.Low => "low",
        DeltaSeverity.Info => "info",
        _ => "none",
    };

    // The entity's ID on each side: "#5" when both agree, "A#5 B#6" when they differ, "A#5" when only A holds it.
    private static string? DiffId(TextQuery a, TextQuery b, in EntityMatch match)
    {
        var idA = match.A.IsPresent ? a.EntityId(match.Key.Kind, match.A.Index) : null;
        var idB = match.B.IsPresent ? b.EntityId(match.Key.Kind, match.B.Index) : null;
        if (idA is null && idB is null)
            return null;
        if (idA == idB)
            return idA;
        return idA is null ? "B" + idB : idB is null ? "A" + idA : "A" + idA + " B" + idB;
    }

    private string? EntityId(CompareEntityKind kind, int index)
    {
        lock (_session.Gate)
        {
            var assembler = _session.Assembler;
            switch (kind)
            {
                case CompareEntityKind.Invocation when (uint)index < (uint)assembler.Invocations.Length:
                    return _ids.InvocationId(index);
                case CompareEntityKind.Pipe when (uint)index < (uint)assembler.Pipes.Length:
                    return _ids.PipeId(index);
                case CompareEntityKind.Collection when (uint)index < (uint)assembler.Collections.Length:
                    return _ids.PipeId(assembler.Collections[index].Pipe);
                default:
                    return null;
            }
        }
    }

    private static void Differences(StringBuilder sb, CompareSession compare, in EntityMatch match)
    {
        var d = match.Differences;
        if ((d & EntityDifferences.Presence) != 0)
        {
            sb.Append(match.A.IsPresent ? "only A" : "only B");
            return;
        }

        void Word(StringBuilder s, string word)
        {
            if (s.Length > 0)
                s.Append(", ");
            s.Append(word);
        }

        if ((d & EntityDifferences.Outcome) != 0)
            Word(sb, "outcome " + OutcomeWord(match.A.Outcome) + " → " + OutcomeWord(match.B.Outcome));
        if ((d & EntityDifferences.Content) != 0)
            Word(sb, "content");
        if ((d & EntityDifferences.Items) != 0)
            Word(sb, string.Create(CultureInfo.InvariantCulture, $"items {match.A.Items} → {match.B.Items}"));
        if ((d & EntityDifferences.Bytes) != 0)
            Word(sb, "bytes " + Size(match.A.Bytes) + " → " + Size(match.B.Bytes));
        if ((d & EntityDifferences.Frames) != 0)
            Word(sb, string.Create(CultureInfo.InvariantCulture, $"frames {match.A.Frames} → {match.B.Frames}"));
        if ((d & EntityDifferences.Timing) != 0)
        {
            var start = compare.GetStartDelta(match) / (CompareClock.Frequency / 1000.0);
            var duration = compare.GetDurationDelta(match) / (CompareClock.Frequency / 1000.0);
            Word(sb, string.Create(CultureInfo.InvariantCulture, $"timing start {start:+0.0;-0.0;0.0}ms duration {duration:+0.0;-0.0;0.0}ms"));
        }
    }

    private static string OutcomeWord(EntityOutcome outcome) => outcome switch
    {
        EntityOutcome.Completed => "ok",
        EntityOutcome.Exception => "exception",
        EntityOutcome.Canceled => "canceled",
        EntityOutcome.Unauthorized => "unauthorized",
        EntityOutcome.OneWay => "oneway",
        EntityOutcome.Refused => "refused",
        EntityOutcome.Pending => "pending",
        EntityOutcome.Open => "open",
        EntityOutcome.Closing => "closing",
        EntityOutcome.Closed => "closed",
        EntityOutcome.None => "absent",
        _ => "seen",
    };

    private static void FieldDiffs(TextQuery a, TextQuery b, CompareSession compare, int match, List<TextRecord> destination)
    {
        var diff = compare.GetFieldDiff(match);
        var row = compare.Matches[match];
        var entityId = DiffId(a, b, row);
        destination.Add(a.Stat("fields " + (entityId is null ? "" : entityId + " ") + compare.GetLabel(match)
                               + string.Create(CultureInfo.InvariantCulture, $" ({diff.DifferenceCount} differ)")));
        var shown = 0;
        var parts = diff.Parts;
        for (var p = 0; p < parts.Length; p++)
        {
            var part = parts[p];
            if (part.Differing == 0)
                continue;
            var rows = diff.GetRows(p);
            var skipBelow = int.MaxValue;
            for (var r = 0; r < rows.Length; r++)
            {
                var field = rows[r];
                if (field.Depth > skipBelow)
                    continue;
                skipBelow = int.MaxValue;
                if (field.Kind == FieldDiffKind.Same)
                    continue;
                if (field.Kind == FieldDiffKind.Changed && HasDifferingChild(rows, r))
                    continue;
                if (field.Kind is FieldDiffKind.Added or FieldDiffKind.Removed)
                    skipBelow = field.Depth;
                if (shown++ == DiffFieldRows)
                {
                    destination.Add(a.Field(1, null, string.Create(CultureInfo.InvariantCulture, $"…+{diff.DifferenceCount - DiffFieldRows} more")));
                    return;
                }

                var path = PartWord(part) + BelowRoot(field.Path);
                string marker;
                string value;
                switch (field.Kind)
                {
                    case FieldDiffKind.Added:
                        marker = "+ ";
                        value = a.FieldText(part.B, field.FieldB);
                        break;
                    case FieldDiffKind.Removed:
                        marker = "- ";
                        value = a.FieldText(part.A, field.FieldA);
                        break;
                    default:
                        marker = "~ ";
                        value = a.FieldText(part.A, field.FieldA) + " → " + a.FieldText(part.B, field.FieldB);
                        break;
                }

                destination.Add(a.Field(1, marker + path, value));
            }
        }
    }

    // A field path without the value's root name (the part word names the root): ".message.Text", "[1]", "".
    private static string BelowRoot(string path)
    {
        var cut = path.AsSpan().IndexOfAny('.', '[');
        return cut < 0 ? "" : path[cut..];
    }

    private static bool HasDifferingChild(ReadOnlySpan<FieldDiff> rows, int index)
    {
        var depth = rows[index].Depth;
        for (var r = index + 1; r < rows.Length && rows[r].Depth > depth; r++)
        {
            if (rows[r].Kind != FieldDiffKind.Same)
                return true;
        }

        return false;
    }

    private static string PartWord(in FieldDiffPart part) => part.Kind switch
    {
        FieldDiffPartKind.Arguments => "args",
        FieldDiffPartKind.Result => "result",
        FieldDiffPartKind.ChannelItem => string.Create(CultureInfo.InvariantCulture, $"item[{part.Ordinal}]"),
        FieldDiffPartKind.CollectionMessage => string.Create(CultureInfo.InvariantCulture, $"message[{part.Ordinal}]"),
        FieldDiffPartKind.PipeData => string.Create(CultureInfo.InvariantCulture, $"data[{part.Ordinal}]"),
        _ => "greeting",
    };

    private string FieldText(Schema.DecodedValue? value, int field)
    {
        if (value is null || (uint)field >= (uint)value.Fields.Length)
            return "(none)";
        var text = _values.AppendField(_sb, value, field, Math.Max(0, _values.Depth - 1)).ToString();
        _sb.Clear();
        return text;
    }
}
