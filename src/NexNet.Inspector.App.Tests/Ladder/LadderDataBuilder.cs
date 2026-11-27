using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Tests.Ladder;

/// <summary>
/// Builds a <see cref="LadderModel"/> by hand, in milliseconds on a microsecond clock, for layout, trace, input,
/// allocation and gallery tests: rows are added the way <see cref="LadderProjection"/> adds them, without a capture.
/// </summary>
internal sealed class LadderDataBuilder
{
    /// <summary>Ticks per second: one tick per microsecond keeps the arithmetic readable.</summary>
    public const long Frequency = 1_000_000;

    /// <summary>Invocation #812's entity index and its large argument's message index in <see cref="Demo"/>.</summary>
    public const int Invocation812 = 0;

    public const int Argument812 = 0;

    private readonly LadderModel _model = new() { Frequency = Frequency, LeftEndpoint = "Client · 192.168.1.40", RightEndpoint = "Server" };
    private long _inboundSeq;
    private long _outboundSeq;

    public static long Ticks(double ms) => (long)Math.Round(ms * Frequency / 1000.0);

    public static TimeWindow Window(double fromMs, double toMs) => new(Ticks(fromMs), Ticks(toMs));

    public LadderModel Model => _model;

    public static ushort PipeId(int client, int server) => (ushort)(client | (server << 8));

    /// <summary>The next frame of a direction (sequence numbers count up from 0).</summary>
    public LadderDataBuilder Row(TapDirection direction, double ms, MessageType type, int bytes = -1, long op = 0, long lastOp = -1,
        FrameFlags flags = FrameFlags.None, LadderTarget target = LadderTarget.None, ushort targetId = 0, LadderNote note = LadderNote.None,
        string? name = null, EntityKind entity = EntityKind.None, int entityIndex = -1, int message = -1, int part = 0)
    {
        long seq = direction == TapDirection.Inbound ? _inboundSeq++ : _outboundSeq++;
        _model.Add(new LadderRow(new FrameKey(direction, seq), Ticks(ms), FrameKind.Frame, type, flags, bytes, target, targetId, note,
            name is null ? -1 : _model.AddLabel(name), op, lastOp < 0 ? op : lastOp, entity, entityIndex, message, part));
        if (message >= 0 && _model.MessageOf(message).IsNone)
            _model.SetMessage(message, new MessageRef(MessageRefKind.Assembled, direction, seq, message));
        return this;
    }

    public LadderModel Build()
    {
        _model.NotifyChanged();
        return _model;
    }

    /// <summary>
    /// The inspect-live Ladder pane: invocation #812's large argument (MessageStart, five chunks, the result) interleaved
    /// with pipe writes, a ping and window updates, then two more invocations, a cancellation and a partial write.
    /// </summary>
    public static LadderModel Demo()
    {
        const TapDirection In = TapDirection.Inbound;
        const TapDirection Out = TapDirection.Outbound;
        const FrameFlags TN = FrameFlags.TransportSplit | FrameFlags.NexNetSplit;
        ushort pipe3 = PipeId(3, 1);
        LadderDataBuilder b = new();
        b.Row(In, 1_258.102, MessageType.MessageStart, 7, 4466, target: LadderTarget.Invocation, targetId: 812, note: LadderNote.Argument,
                entity: EntityKind.Invocation, entityIndex: Invocation812, message: Argument812)
            .Row(Out, 1_258.988, MessageType.InvocationCredit, 3, 3104)
            .Row(In, 1_259.880, MessageType.MessageChunk, 4_096, 4468, 4469, TN, LadderTarget.Invocation, 812,
                entity: EntityKind.Invocation, entityIndex: Invocation812, message: Argument812, part: 1)
            .Row(In, 1_260.812, MessageType.DuplexPipeWrite, 16_384, 4470, flags: FrameFlags.NexNetSplit, target: LadderTarget.Pipe,
                targetId: pipe3, entity: EntityKind.Pipe, entityIndex: 0, message: 1)
            .Row(In, 1_261.512, MessageType.Ping, op: 4470)
            .Row(Out, 1_262.006, MessageType.PipeWindowUpdate, 4, 3105, target: LadderTarget.Pipe, targetId: pipe3, entity: EntityKind.Pipe, entityIndex: 0)
            .Row(In, 1_262.418, MessageType.MessageChunk, 4_096, 4471, 4472, TN, LadderTarget.Invocation, 812,
                entity: EntityKind.Invocation, entityIndex: Invocation812, message: Argument812, part: 2)
            .Row(In, 1_263.611, MessageType.DuplexPipeWrite, 16_384, 4473, flags: FrameFlags.NexNetSplit, target: LadderTarget.Pipe,
                targetId: pipe3, entity: EntityKind.Pipe, entityIndex: 0, message: 1)
            .Row(Out, 1_264.207, MessageType.PipeWindowUpdate, 4, 3106, target: LadderTarget.Pipe, targetId: pipe3, entity: EntityKind.Pipe, entityIndex: 0)
            .Row(In, 1_265.003, MessageType.MessageChunk, 4_096, 4475, flags: FrameFlags.NexNetSplit, target: LadderTarget.Invocation, targetId: 812,
                entity: EntityKind.Invocation, entityIndex: Invocation812, message: Argument812, part: 3)
            .Row(In, 1_266.702, MessageType.DuplexPipeWrite, 16_384, 4476, target: LadderTarget.Pipe, targetId: pipe3, entity: EntityKind.Pipe, entityIndex: 0)
            .Row(In, 1_268.297, MessageType.MessageChunk, 4_096, 4478, 4479, TN, LadderTarget.Invocation, 812,
                entity: EntityKind.Invocation, entityIndex: Invocation812, message: Argument812, part: 4)
            .Row(In, 1_269.604, MessageType.DuplexPipeWrite, 612, 4480, flags: FrameFlags.NexNetSplit, target: LadderTarget.Channel,
                targetId: PipeId(4, 2), name: "LogLine", entity: EntityKind.Pipe, entityIndex: 1)
            .Row(In, 1_271.214, MessageType.MessageChunk, 2_038, 4482, flags: FrameFlags.NexNetSplit, target: LadderTarget.Invocation, targetId: 812,
                entity: EntityKind.Invocation, entityIndex: Invocation812, message: Argument812, part: 5)
            .Row(Out, 1_273.106, MessageType.InvocationResult, 23, 3110, target: LadderTarget.Invocation, targetId: 812, note: LadderNote.Completed,
                entity: EntityKind.Invocation, entityIndex: Invocation812)
            .Row(In, 1_274.010, MessageType.Invocation, 41, 4484, target: LadderTarget.Invocation, targetId: 813, name: "GetHistory",
                entity: EntityKind.Invocation, entityIndex: 1)
            .Row(In, 1_277.104, MessageType.Invocation, 19, 4486, target: LadderTarget.Invocation, targetId: 815, name: "Typing",
                entity: EntityKind.Invocation, entityIndex: 2)
            .Row(In, 1_278.611, MessageType.InvocationCancellation, 3, 4487, flags: FrameFlags.Cancel, target: LadderTarget.Invocation, targetId: 815,
                entity: EntityKind.Invocation, entityIndex: 2)
            .Row(Out, 1_279.402, MessageType.InvocationResult, 4, 3112, flags: FrameFlags.Cancel, target: LadderTarget.Invocation, targetId: 815,
                note: LadderNote.Canceled, entity: EntityKind.Invocation, entityIndex: 2)
            .Row(Out, 1_279.904, MessageType.DuplexPipeUpdateState, 6, 3111, target: LadderTarget.Pipe, targetId: PipeId(5, 1), entity: EntityKind.Pipe,
                entityIndex: 2)
            .Row(In, 1_289.904, MessageType.DuplexPipeWrite, 1_212, 4499, flags: FrameFlags.Incomplete, target: LadderTarget.Pipe, targetId: pipe3,
                note: LadderNote.Partial, entity: EntityKind.Pipe, entityIndex: 0);
        return b.Build();
    }

    /// <summary>
    /// A long session for virtualization and allocation: <paramref name="rows"/> frames alternating direction, an
    /// invocation of eight frames at a time (its chunks split), on a clock with pauses every 64 rows.
    /// </summary>
    public static LadderModel Busy(int rows)
    {
        LadderDataBuilder b = new();
        double ms = 0;
        for (int i = 0; i < rows; i++)
        {
            ms += i % 64 == 0 ? 25 : 0.05 + (i % 7 * 0.11);
            TapDirection direction = i % 3 == 2 ? TapDirection.Outbound : TapDirection.Inbound;
            int invocation = i / 8;
            bool chunk = i % 8 is > 0 and < 6 && direction == TapDirection.Inbound;
            b.Row(direction, ms, chunk ? MessageType.MessageChunk : MessageType.Invocation, 64 + (i % 900), 10_000 + i, 10_000 + i + (i % 4 == 1 ? 1 : 0),
                (i % 4 == 1 ? FrameFlags.TransportSplit : FrameFlags.None) | (chunk ? FrameFlags.NexNetSplit : FrameFlags.None),
                LadderTarget.Invocation, (ushort)(invocation % 60_000), entity: EntityKind.Invocation, entityIndex: invocation,
                message: chunk ? invocation : -1, part: chunk ? i % 8 : 0);
        }

        return b.Build();
    }
}
