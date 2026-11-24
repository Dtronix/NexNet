using System.Globalization;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Model;

/// <summary>
/// One row of the inspector's Message trace tab (property-tree README: 21 px rows Step · time · bytes; interleaved
/// frames indented 12 px at 60 % opacity; the current step selected with the trace bar). A row per step of the
/// <see cref="MessageTrace"/>, made when the trace changes; the list is virtualized.
/// </summary>
public sealed class TraceStepRow
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <param name="step">The step.</param>
    /// <param name="origin">The capture's first tick (the time column counts from it).</param>
    /// <param name="frequency">Ticks per second.</param>
    /// <param name="pipe">What the step's pipe carries (the CH / CO chip); <see cref="PipeContent.Raw"/> for none.</param>
    public TraceStepRow(in TraceStep step, long origin, long frequency, PipeContent pipe = PipeContent.Raw)
    {
        PipeKind = step.IsInterleaved ? PipeContent.Raw : pipe;
        Step = step;
        Label = step.Label;
        Time = ((step.Ticks - origin) * 1000.0 / Math.Max(1, frequency)).ToString("N3", Invariant);
        Bytes = step.Bytes.ToString("N0", Invariant);
        IsInterleaved = step.IsInterleaved;
        IsInbound = step.Frame.Direction == TapDirection.Inbound;
        Glyph = step.IsInterleaved ? "" : IsInbound ? "▼" : "▲";
        IsTransportSplit = !step.IsInterleaved && (step.Flags & FrameFlags.TransportSplit) != 0;
        Detail = DetailOf(step);
    }

    /// <summary>The step.</summary>
    public TraceStep Step { get; }

    /// <summary>"MessageStart", "Chunk 2/5", "↳ pipe 3·1, Ping".</summary>
    public string Label { get; }

    /// <summary>Milliseconds from the capture's start, 3 decimals.</summary>
    public string Time { get; }

    /// <summary>Bytes on the wire.</summary>
    public string Bytes { get; }

    /// <summary>▼ inbound, ▲ outbound; nothing for an interleaved run.</summary>
    public string Glyph { get; }

    /// <summary>True for an inbound frame (the glyph's colour).</summary>
    public bool IsInbound { get; }

    /// <summary>True for a run of other messages' frames: indented and dimmed.</summary>
    public bool IsInterleaved { get; }

    /// <summary>True when the frame came in more than one transport op (the T chip).</summary>
    public bool IsTransportSplit { get; }

    /// <summary>What the step's pipe carries: a channel or collection shows its chip beside the step.</summary>
    public PipeContent PipeKind { get; }

    /// <summary>True when the step shows the CH / CO chip.</summary>
    public bool HasPipeChip => PipeKind != PipeContent.Raw;

    /// <summary>The tooltip: frame, offset in the message, stream offset, length, transport ops.</summary>
    public string Detail { get; }

    /// <summary>The row's opacity: 60 % for interleaved frames.</summary>
    public double RowOpacity => IsInterleaved ? 0.6 : 1;

    /// <summary>The label's indent: 12 px for interleaved frames.</summary>
    public Avalonia.Thickness LabelMargin => new(IsInterleaved ? 12 : 0, 0, 0, 0);

    /// <summary>What the pipe of a step's frame carries; <see cref="PipeContent.Raw"/> when it is no pipe's. Call under the session's gate.</summary>
    public static PipeContent PipeKindOf(CaptureSession session, in TraceStep step)
    {
        ArgumentNullException.ThrowIfNull(session);
        MessageAssembler assembler = session.Assembler;
        FrameLink link = assembler.GetLink(step.Frame.Direction, step.Frame.Seq);
        return link.Entity == EntityKind.Pipe && (uint)link.EntityIndex < (uint)assembler.Pipes.Length
            ? assembler.Pipes[link.EntityIndex].Content
            : PipeContent.Raw;
    }

    private static string DetailOf(in TraceStep step)
    {
        string unit = step.Frame.Direction == TapDirection.Inbound ? "read" : "write";
        string ops = step.FirstOpId == step.LastOpId
            ? unit + " #" + step.FirstOpId.ToString(Invariant)
            : unit + "s #" + step.FirstOpId.ToString(Invariant) + "–#" + step.LastOpId.ToString(Invariant);
        string frame = "#" + step.Frame.Seq.ToString("N0", Invariant);
        if (step.IsInterleaved)
            return step.Frames.ToString(Invariant) + (step.Frames == 1 ? " frame" : " frames") + " from " + frame + " · " + ops;
        string inMessage = step.MessageOffset >= 0
            ? " · message +" + step.MessageOffset.ToString("N0", Invariant) + " · " + step.Length.ToString("N0", Invariant) + " B of it"
            : "";
        return frame + inMessage + " · stream 0x" + step.StreamOffset.ToString("X6", Invariant) + " · " + ops;
    }
}
