using Avalonia;
using Avalonia.Media;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.DataTable.Events;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Tests.DataTable;

/// <summary>A source of any number of rows that holds none: it counts what the table asks it to prepare.</summary>
internal sealed class CountingSource(int count) : DataTableSource
{
    private int _count = count;

    public override int Count => _count;

    public int Prepared { get; private set; }

    public int VisualsCreated { get; private set; }

    public void Append(int rows)
    {
        int start = _count;
        _count += rows;
        NotifyAdded(start, rows);
    }

    public override DataTableRowVisual CreateVisual()
    {
        VisualsCreated++;
        return new IndexVisual();
    }

    public override void Prepare(DataTableRowVisual visual, int index)
    {
        Prepared++;
        ((IndexVisual)visual).Set(index);
    }

    internal sealed class IndexVisual : DataTableRowVisual
    {
        public void Set(int index) => Index = index;

        public override void Render(DrawingContext context, ThemePalette palette, ReadOnlySpan<DataTableColumnSlot> columns, Size size)
        {
        }
    }
}

/// <summary>Events rows given as values (the gallery's and the package's sample rows), drawn by the real Events visual.</summary>
internal sealed class EventRowArray(params EventRow[] rows) : DataTableSource
{
    public override int Count => rows.Length;

    public override DataTableRowVisual CreateVisual() => new EventRowVisual();

    public override void Prepare(DataTableRowVisual visual, int index) => ((EventRowVisual)visual).Set(index, rows[index]);

    /// <summary>data-table.html's five rows: chunk 1/5 traced, 2/5 (index 3) the one the states select.</summary>
    public static EventRowArray Sample()
    {
        MessageRef traced = new(MessageRefKind.Assembled, TapDirection.Inbound, 18_100, 7);
        return new EventRowArray(
            Row(18_229, 1_259.880, TapDirection.Inbound, MessageType.MessageChunk, EventTargetKind.Invocation, 812, 4_096,
                FrameFlags.TransportSplit | FrameFlags.NexNetSplit, chunk: 1, chunks: 5) with { IsTraced = true, Message = traced },
            Row(18_230, 1_260.812, TapDirection.Inbound, MessageType.DuplexPipeWrite, EventTargetKind.Pipe, 3 | (1 << 8), 16_384, FrameFlags.NexNetSplit),
            Row(18_232, 1_262.006, TapDirection.Outbound, MessageType.PipeWindowUpdate, EventTargetKind.Pipe, 3 | (1 << 8), 4, FrameFlags.None),
            Row(18_233, 1_262.418, TapDirection.Inbound, MessageType.MessageChunk, EventTargetKind.Invocation, 812, 4_096,
                FrameFlags.TransportSplit | FrameFlags.NexNetSplit, chunk: 2, chunks: 5) with { IsTraced = true, Message = traced },
            Row(18_268, 1_278.611, TapDirection.Inbound, MessageType.InvocationCancellation, EventTargetKind.Invocation, 815, 3, FrameFlags.Cancel));
    }

    public static EventRow Row(long number, double ms, TapDirection direction, MessageType type, EventTargetKind target, int targetId,
        int length, FrameFlags flags, int chunk = 0, int chunks = 0) => new()
        {
            Number = number,
            Frame = new FrameKey(direction, number),
            TimeMs = ms,
            Kind = FrameKind.Frame,
            Type = type,
            Target = target,
            TargetId = (ushort)targetId,
            Length = length,
            Flags = flags,
            ChunkNumber = chunk,
            ChunkCount = chunks,
            Message = MessageRef.ForFrame(direction, number),
        };
}
