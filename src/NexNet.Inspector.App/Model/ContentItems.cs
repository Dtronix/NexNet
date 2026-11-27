using System.Globalization;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.DataTable.Events;
using NexNet.Inspector.App.Controls.DataTable.FieldDiff;
using NexNet.Inspector.App.Controls.DataTable.Sessions;
using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Model;

/// <summary>
/// The <see cref="ContentItem"/> each content view's row or field becomes when it is right-clicked or copied: its text as
/// displayed, its value, path and TSV cells, and the bytes it maps to.
/// </summary>
public static class ContentItems
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>A frame's flags as the chips' letters, in display order ("T R").</summary>
    public static string Flags(FrameFlags flags)
    {
        var text = new System.Text.StringBuilder();
        foreach (var flag in FlagChip.DisplayOrder)
        {
            if ((flags & flag) == 0) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append(FlagChip.Letter(flag));
        }

        return text.ToString();
    }

    /// <summary>The bytes of a frame of <paramref name="session"/>; none when the frame is unknown.</summary>
    public static ContentBytes FrameBytes(CaptureSession? session, FrameKey key, MessageRef message)
    {
        if (session is null) return ContentBytes.None;
        lock (session.Gate)
            return session.TryGetFrame(key, out var frame) && frame.Length > 0 ? ContentBytes.OfFrame(frame, message) : ContentBytes.None;
    }

    /// <summary>The stem of a frame's saved file: "inbound-frame-1204".</summary>
    public static string FrameStem(FrameKey key) =>
        (key.Direction == TapDirection.Inbound ? "inbound" : "outbound") + "-frame-" + key.Seq.ToString(Invariant);

    /// <summary>Any frame by its key (a timeline mark): its type, direction and size, and its bytes.</summary>
    public static ContentItem? ForFrame(CaptureSession session, FrameKey key)
    {
        ArgumentNullException.ThrowIfNull(session);
        FrameRecord frame;
        MessageRef message;
        lock (session.Gate)
        {
            if (!session.TryGetFrame(key, out frame)) return null;
            message = session.Assembler.GetMessage(key.Direction, key.Seq);
        }

        string type = frame.Kind switch
        {
            FrameKind.Preamble => "Preamble",
            FrameKind.Malformed => "Malformed",
            _ => EventText.TypeName(frame.Type),
        };
        string direction = key.Direction == TapDirection.Inbound ? "▼ IN" : "▲ OUT";
        string number = "#" + key.Seq.ToString("N0", Invariant);
        string bytes = frame.Length.ToString("N0", Invariant);
        return new ContentItem
        {
            Title = "Frame " + number,
            Detail = type + " · " + bytes + " B",
            Text = Join("  ", number, direction, type, bytes + " B", Flags(frame.Flags)),
            Value = type,
            Row = ContentItem.Tsv(number, direction, type, bytes, Flags(frame.Flags)),
            Bytes = frame.Length > 0 ? ContentBytes.OfFrame(frame, message) : ContentBytes.None,
            FileStem = FrameStem(key),
        };
    }

    /// <summary>An Events row: its cells as shown (Copy, Copy row as TSV), its target as the value, and its frame.</summary>
    public static ContentItem ForEvent(CaptureSession? session, in EventRow row)
    {
        string direction = row.Direction == TapDirection.Inbound ? "▼ IN" : "▲ OUT";
        string type = EventText.Type(row);
        string target = EventText.Target(row);
        string bytes = EventText.Bytes(row);
        string flags = Flags(row.Flags);
        string number = EventText.Number(row);
        string time = EventText.Time(row);
        return new ContentItem
        {
            Title = "Frame #" + number,
            Detail = type + " · " + bytes + " B",
            Text = Join("  ", number, time + " ms", direction, type, target, bytes + " B", flags),
            Value = target.Length > 0 ? target : type,
            Row = ContentItem.Tsv(number, time, direction, type, target, bytes, flags),
            Bytes = FrameBytes(session, row.Frame, row.Message),
            FileStem = FrameStem(row.Frame),
        };
    }

    /// <summary>A Ladder row: its line as the ladder describes it, and its frame.</summary>
    public static ContentItem ForLadder(CaptureSession? session, LadderModel model, in LadderRow row)
    {
        ArgumentNullException.ThrowIfNull(model);
        int total = model.ChunkTotal(row.Message);
        string? name = model.GetLabel(row.Name);
        string title = LadderText.Title(row, total);
        string detail = LadderText.Detail(row, name);
        string time = LadderText.Time(row.Ticks, model.Origin, model.Frequency);
        string bytes = LadderText.Bytes(row) ?? "";
        string direction = row.Direction == TapDirection.Inbound ? "▼ IN" : "▲ OUT";
        return new ContentItem
        {
            Title = title,
            Detail = detail.Length > 0 ? detail : null,
            Text = LadderText.Describe(row, total, name, model.Origin, model.Frequency),
            Value = detail.Length > 0 ? detail : title,
            Row = ContentItem.Tsv(time, direction, title, detail, bytes, Flags(row.Flags)),
            Bytes = FrameBytes(session, row.Frame, row.Message >= 0 ? model.MessageOf(row.Message) : MessageRef.None),
            FileStem = FrameStem(row.Frame),
        };
    }

    /// <summary>A Message trace row: its step, time and bytes, and its frame (an interleaved run: its first frame).</summary>
    public static ContentItem ForTraceStep(CaptureSession? session, TraceStepRow row, MessageRef message)
    {
        ArgumentNullException.ThrowIfNull(row);
        var frame = row.Step.Frame;
        return new ContentItem
        {
            Title = row.Label,
            Detail = row.Detail,
            Text = Join("  ", row.Glyph, row.Label, row.Time + " ms", row.Bytes + " B"),
            Value = row.Detail,
            Row = ContentItem.Tsv(row.Glyph, row.Label, row.Time, row.Bytes, row.Detail),
            Bytes = row.IsInterleaved ? ContentBytes.None : FrameBytes(session, frame, message),
            FileStem = FrameStem(frame),
        };
    }

    /// <summary>A decoded property tree row: "Name  Value", its value, its dotted path and its byte range.</summary>
    public static ContentItem ForProperty(PropertyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string value = row.Value;
        var bytes = row.Bytes;
        ContentBytes content = bytes.IsNone
            ? ContentBytes.None
            : bytes.Stream.Length == bytes.Length || bytes.Message.Kind == MessageRefKind.Frame
                ? ContentBytes.OfRange(bytes.Direction, new StreamRange(bytes.Stream.Offset, bytes.Length))
                : ContentBytes.OfPayload(new PayloadRange(bytes.Message, bytes.Offset, bytes.Length));
        return new ContentItem
        {
            Title = row.Name,
            Detail = row.IsSection ? value : bytes.IsNone ? null : bytes.Length.ToString("N0", Invariant) + (bytes.Length == 1 ? " byte" : " bytes"),
            Text = value.Length > 0 ? row.Name + "  " + value : row.Name,
            Value = row.IsSection ? null : value,
            Path = row.IsSection ? null : row.Model.PathOf(row.Node),
            Row = ContentItem.Tsv(row.Name, value),
            Bytes = content,
            FileStem = "field-" + Stem(row.Name),
        };
    }

    /// <summary>A Compare field-diff row: the field and both files' values.</summary>
    public static ContentItem ForFieldDiff(in FieldDiffCells cells, string nameA, string nameB)
    {
        string a = cells.A ?? "—";
        string b = cells.B ?? "—";
        return new ContentItem
        {
            Title = cells.Name,
            Detail = cells.Diff == Core.Compare.FieldDiffKind.Same ? "same in both files" : "differs",
            Text = cells.Name + "  " + nameA + ": " + a + "  " + nameB + ": " + b,
            Value = cells.B ?? cells.A,
            Path = cells.Path.Length > 0 ? cells.Path : null,
            Row = ContentItem.Tsv(cells.Path.Length > 0 ? cells.Path : cells.Name, a, b),
        };
    }

    /// <summary>An entry of Compare's entity sidebar: its label and count.</summary>
    public static ContentItem ForEntity(string label, string count, bool hasDelta) => new()
    {
        Title = label,
        Detail = hasDelta ? count + " · differs" : count,
        Text = count.Length > 0 ? label + "  " + count : label,
        Value = label,
        Row = ContentItem.Tsv(label, count, hasDelta ? "Δ" : ""),
    };

    /// <summary>A row of the open-recording session list.</summary>
    public static ContentItem ForSession(in SessionListRow row) => new()
    {
        Title = row.Session,
        Detail = row.Remote,
        Text = Join("  ", row.Session, row.Remote, row.Transport, row.Start, row.Duration, row.Events + " events"),
        Value = "0x" + row.SessionId.ToString("X", Invariant),
        Row = ContentItem.Tsv(row.Session, row.Remote, row.Transport, row.Start, row.Duration, row.Events),
    };

    private static string Join(string separator, params ReadOnlySpan<string?> parts)
    {
        var text = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (text.Length > 0) text.Append(separator);
            text.Append(part);
        }

        return text.ToString();
    }

    // A name made safe for a file name: letters and digits, the rest as '-'.
    private static string Stem(string name)
    {
        var text = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            text.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return text.Length == 0 ? "bytes" : text.ToString().Trim('-');
    }
}
