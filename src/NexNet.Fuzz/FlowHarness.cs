using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Serialization;

namespace NexNet.Fuzz;

/// <summary>
/// Harness 4: flow control and session liveness (docs/internals/flow-control.md). Each input is a program of
/// operations that a client runs against a long-lived server over an in-memory transport, while it reads and parses
/// everything the server sends back.
/// </summary>
/// <remarks>
/// <para>
/// Input layout: a sequence of 4-byte operations <c>[op, a, b, c]</c>; see <see cref="FlowOp"/>. Sizes are picked
/// from <see cref="Sizes"/> by index, so mutations stay within sizes that matter to the protocol.
/// </para>
/// <para>
/// Failures:
/// <list type="bullet">
/// <item>a probe (ping, call or pipe open) that is neither answered nor ends in a disconnect within
/// <see cref="ProbeTimeout"/>: something on the session waits on an application reader (requirement R1);</item>
/// <item>no server greeting within <see cref="ProbeTimeout"/>: the program needs a completed connection;</item>
/// <item>a session still open <see cref="SessionHarness.SessionTimeout"/> after the stream ends;</item>
/// <item>a disconnect reason outside <see cref="AllowedDisconnects"/>, or malformed server output;</item>
/// <item>the server logging an exception type that hostile input should never cause.</item>
/// </list>
/// The session budget is checked too: no reservation is ever released twice
/// (<see cref="Internals.SessionBudget.InvariantViolations"/>).
/// </para>
/// <para>
/// The client behaves like a NexNet client unless an operation says otherwise: it writes pipe data only within the
/// credit the server granted, and answers the server's pipe closes.
/// </para>
/// <para>
/// Operations can also send the flow control frames (<c>PipeWindowUpdate</c>, <c>MessageStart</c>/<c>Chunk</c>/<c>Abort</c>,
/// <c>InvocationCredit</c>) directly, valid or not.
/// </para>
/// </remarks>
public static class FlowHarness
{
    public static TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(double.TryParse(Environment.GetEnvironmentVariable("NEXNET_FUZZ_PROBE_TIMEOUT_MS"), out var ms) ? ms : 1000);

    /// <summary>
    /// Value sizes, indexed by an operation argument. Includes the old limits (one frame, the 256 KiB
    /// high-water cutoff) and the default window.
    /// </summary>
    public static readonly int[] Sizes = [0, 1, 100, 1024, 16 * 1024, 40 * 1024, 65535, 65536, 300 * 1024, 1024 * 1024];

    private const int MaxBytesPerInput = 8 * 1024 * 1024;

    // How long a write waits for more credit before the client gives up on the rest of it, as a writer cancelling
    // its flush would.
    private static readonly TimeSpan CreditWait = TimeSpan.FromMilliseconds(200);
    private const int MaxPausePerInput = 500;
    private const int MaxFrameBody = ushort.MaxValue;

    // Flow control message types (design 4.1).
    internal const MessageType PipeWindowUpdate = (MessageType)51;
    internal const MessageType MessageStart = (MessageType)113;
    internal const MessageType MessageChunk = (MessageType)114;
    internal const MessageType MessageAbort = (MessageType)115;
    internal const MessageType InvocationCredit = (MessageType)116;

    /// <summary>
    /// Disconnect reasons hostile input may cause. Any other reason means the server failed internally.
    /// </summary>
    internal static readonly HashSet<DisconnectReason> AllowedDisconnects =
    [
        DisconnectReason.ProtocolError,
        DisconnectReason.Timeout,
        DisconnectReason.FlowControl,
        DisconnectReason.Stalled,
    ];

    private static readonly Lazy<FuzzServerHost> Instance = new(() => FuzzServerHost.Start(
        static config => FlowServerNexus.CreateServer(config, static () => new FlowServerNexus()).StartAsync(),
        IInvocationMethodHash.GetMethodHash<FlowServerNexus>(),
        IInvocationMethodHash.GetMethodHash<FlowClientNexus>(),
        // Short enough that the hostile cases see a stalled close inside the harness timeout.
        static config => config.ProgressTimeout = 1000));

    public static void Run(byte[] data) => Execute(data);

    /// <summary>
    /// Runs a program and checks the invariants. Returns the server's disconnect reason, if it sent one.
    /// </summary>
    public static DisconnectReason? Execute(byte[] data)
    {
        var host = Instance.Value;
        var transport = new FuzzTransport(readsOutput: true);
        var output = new ServerOutput();
        var reading = Task.Run(() => output.ReadAsync(transport.ServerOutput));
        host.Accept(transport);

        var client = new FlowClient(transport.ClientToServer.Writer, output);
        client.Send(FuzzServerHost.Preamble);
        client.Send(host.Greeting);

        // Anything sent before the connection completes is a protocol error, which would end every program here.
        string? stall = output.WaitFor(o => o.Greeted, ProbeTimeout) && output.Greeted
            ? null
            : "Server did not complete the handshake.";
        for (var i = 0; i + 4 <= data.Length && stall == null && !output.IsClosed; i += 4)
            stall = client.Execute((FlowOp)(data[i] % (int)FlowOp.Count), data[i + 1], data[i + 2], data[i + 3]);

        transport.ClientToServer.Writer.Complete();

        if (!transport.Closed.Wait(SessionHarness.SessionTimeout))
            throw new TimeoutException($"Session did not close within {SessionHarness.SessionTimeout.TotalSeconds:F0} s after the stream ended.");
        if (!reading.Wait(SessionHarness.SessionTimeout))
            throw new TimeoutException("Server output did not end after the session closed.");

        host.Logger.ThrowIfUnexpected();
        output.ThrowIfMalformed();

        if (stall != null)
            throw new TimeoutException(stall);
        if (output.Disconnect is { } reason && !AllowedDisconnects.Contains(reason))
            throw new InvalidOperationException($"Server disconnected with {reason}.");
        if (Interlocked.Read(ref Internals.SessionBudget.InvariantViolations) != 0)
            throw new InvalidOperationException("A session budget was released below zero.");

        return output.Disconnect;
    }

    /// <summary>
    /// Programs that must pass on the current protocol.
    /// </summary>
    public static IEnumerable<byte[]> Seeds()
    {
        // Calls.
        yield return Program(Call(FlowMethod.Ping), ProbeCall());
        yield return Program(Call(FlowMethod.Echo, Size(1024)), Call(FlowMethod.Echo, Size(40 * 1024)), ProbeCall());
        yield return Program(Call(FlowMethod.Large, Size(16 * 1024)), ProbeCall());

        // Large arguments and results, streamed both ways.
        yield return Program(Op(FlowOp.CallLarge, 0, Size(300 * 1024)), ProbePing());
        yield return Program(Op(FlowOp.CallLarge, 0, Size(1024 * 1024)), Op(FlowOp.CallLarge, 0, Size(65536)), ProbeCall());
        yield return Program(Call(FlowMethod.Cancellable, c: 40), Op(FlowOp.Cancel), ProbePing());

        // Pipes and channels with readers that keep up.
        yield return Program(Open(FlowPipe.Drain), Data(0, 65535), Data(0, 65535), Op(FlowOp.Complete), ProbePing(), ProbeCall());
        yield return Program(Open(FlowPipe.SlowItems), Item(0, 1024, 10), Item(0, 100), Op(FlowOp.Complete), ProbeCall());
        yield return Program(Open(FlowPipe.Drain), Open(FlowPipe.SlowItems), Data(0, 16 * 1024), Item(1, 100), ProbePing());

        // Pipe state: an unexpected state, data after Complete, state for an unknown pipe.
        yield return Program(Open(FlowPipe.Drain), Op(FlowOp.PipeState, 0, (byte)NexusDuplexPipe.State.Ready), ProbePing());
        yield return Program(Open(FlowPipe.Drain), Op(FlowOp.Complete), Data(0, 100), ProbePing());
        yield return Program(Op(FlowOp.PipeState, 0, (byte)NexusDuplexPipe.State.ClientWriterServerReaderComplete), ProbePing());

        // Every flow control frame once, plus raw frames, pauses and unsolicited pings.
        yield return Program(Open(FlowPipe.Drain), Op(FlowOp.WindowUpdate, 0, Size(65536)), ProbePing());
        yield return Program(Call(FlowMethod.Ping), Op(FlowOp.MessageStart, 0, 0, Size(300 * 1024)), Op(FlowOp.MessageChunk, 0, 0, Size(16 * 1024)), ProbePing());
        yield return Program(Op(FlowOp.MessageAbort, 0, 1, 2), ProbePing());
        yield return Program(Op(FlowOp.InvocationCredit, 4), ProbePing());
        yield return Program(Op(FlowOp.RawFrame, 200, Size(100)), ProbePing());
        yield return Program(Op(FlowOp.Ping), Op(FlowOp.Pause, 20), Op(FlowOp.Ping), ProbePing());
    }

    /// <summary>
    /// Hostile peers (spec section 9) and the disconnect each must cause, or null if the session must stay up.
    /// </summary>
    public static IEnumerable<(string Name, byte[] Input, DisconnectReason? Expected)> HostileCases()
    {
        yield return ("Sends beyond its window",
            Program(Open(FlowPipe.NeverRead), Op(FlowOp.Overrun, 0, Size(300 * 1024)), Op(FlowOp.AwaitClose)),
            DisconnectReason.FlowControl);

        yield return ("Never acknowledges a close",
            Program(Op(FlowOp.NoCloseAcks), Open(FlowPipe.CloseAtOnce), Op(FlowOp.AwaitClose)),
            DisconnectReason.Stalled);

        // MaxPipes is 32: the rest are refused and the session stays up.
        yield return ("Floods pipe opens",
            Program([.. Enumerable.Repeat(Open(FlowPipe.NeverRead), 40).SelectMany(o => o), .. ProbePing(), .. ProbeCall()]),
            null);

        yield return ("Never reads its pipe",
            Program(Open(FlowPipe.NeverRead), Data(0, 1024 * 1024), Open(FlowPipe.Drain), Data(1, 1024 * 1024), ProbePing(), ProbeCall()),
            null);

        // The server advertises 32 credits. Each call holds one of its two slots for 50 ms, so most of the 100 stay
        // queued and the credits run out.
        yield return ("Invokes beyond its credit",
            Program(Op(FlowOp.InvokeIgnoringCredit, 100), Op(FlowOp.AwaitClose)),
            DisconnectReason.FlowControl);

        // The channel's pipe is reset; the session stays up.
        yield return ("Sends a prefix that disagrees with its item",
            Program(Open(FlowPipe.SlowItems), Op(FlowOp.BadItem, 0, Size(100), 0xFF), Op(FlowOp.Pause, 50), ProbePing(), ProbeCall()),
            null);

        yield return ("Sends an item over MaxMessageSize",
            Program(Open(FlowPipe.SlowItems), Op(FlowOp.BadItem, 0, Size(100), 0x80), Op(FlowOp.Pause, 50), ProbePing(), ProbeCall()),
            null);

        // Larger than the window: the reader grants extra credit, which the writer never uses.
        yield return ("Sends half of an item larger than the window, then stops",
            Program(Open(FlowPipe.SlowItems), Op(FlowOp.PartialItem, 0, Size(300 * 1024)), Op(FlowOp.AwaitClose)),
            DisconnectReason.Stalled);

        yield return ("Declares a large argument, then stops",
            Program(Call(FlowMethod.Ping), Op(FlowOp.MessageStart, 1, 0, Size(1024 * 1024)), Op(FlowOp.MessageChunk, 1, 0, Size(16 * 1024)), Op(FlowOp.AwaitClose)),
            DisconnectReason.Stalled);

        yield return ("Chunk beyond the declared length",
            Program(Op(FlowOp.MessageStart, 0, 0, Size(100)), Op(FlowOp.MessageChunk, 0, 0, Size(1024)), Op(FlowOp.AwaitClose)),
            DisconnectReason.ProtocolError);

        yield return ("Chunk for a stream never started",
            Program(Op(FlowOp.MessageChunk, 0, 0, Size(100)), Op(FlowOp.AwaitClose)),
            DisconnectReason.ProtocolError);

        yield return ("Result for a call never made",
            Program(Op(FlowOp.MessageStart, 0, 1, Size(100)), Op(FlowOp.AwaitClose)),
            DisconnectReason.ProtocolError);

        yield return ("Declares more than MaxMessageSize",
            Program(Op(FlowOp.MessageStart, 0, 0, 0xFF), Op(FlowOp.AwaitClose)),
            DisconnectReason.ProtocolError);

        yield return ("Start for a key already active",
            Program(Op(FlowOp.MessageStart, 0, 0, Size(1024 * 1024)), Op(FlowOp.MessageStart, 0, 0, Size(1024 * 1024)), Op(FlowOp.AwaitClose)),
            DisconnectReason.ProtocolError);

        yield return ("Data for a pipe that was never opened",
            Program(Op(FlowOp.PipeData, 0, Size(100)), Op(FlowOp.AwaitClose)),
            DisconnectReason.ProtocolError);
    }

    /// <summary>
    /// Reproductions of the defects in spec section 1, with the step of spec section 8 that fixes each. They fail
    /// until that step lands.
    /// </summary>
    public static IEnumerable<(string Name, byte[] Input, int FixedInStep)> Reproductions()
    {
        // A result larger than a frame breaks every later send on the session.
        yield return ("Oversized result, then a call", Program(Call(FlowMethod.Large, Size(300 * 1024)), ProbeCall()), 1);

        // Disconnecting from another thread (here the idle timeout) completed the input while the read loop was still
        // processing a buffer from it; the read loop then read recycled memory.
        yield return ("Disconnect while the read loop is inside a pipe write", Program(Open(FlowPipe.NeverRead), Data(0, 300 * 1024)), 3);

        // Pipe methods hold an invocation slot for the pipe's lifetime; the read loop waits for a free slot.
        yield return ("Three pipe methods, then a call", Program(Open(FlowPipe.Drain), Open(FlowPipe.Drain), Open(FlowPipe.Drain), ProbeCall()), 3);

        // BufferData does not wake the reader for a frame that reaches the high-water mark, then waits for the reader
        // to drain below the low-water mark.
        yield return ("1 MiB in full frames to a reader that keeps up", Program(Op(FlowOp.StrictWrites), Open(FlowPipe.Drain), Data(0, 1024 * 1024), ProbePing()), 1);

        // The application's reproductions (spec section 9).
        yield return ("40 x 40 KiB items with a slow reader, then a call", Program(Op(FlowOp.StrictWrites), Open(FlowPipe.SlowItems), Item(0, 40 * 1024, 40), ProbeCall()), 4);
        yield return ("Pipe completed while the reader's buffer is full", Program(Open(FlowPipe.NeverRead), Data(0, 300 * 1024), Op(FlowOp.Complete), ProbePing()), 4);
        // The session stays up from step 4 on; the item itself needs more than the window (step 6).
        yield return ("One 300 KiB item", Program(Op(FlowOp.StrictWrites), Open(FlowPipe.SlowItems), Item(0, 300 * 1024), ProbePing()), 6);
    }

    private static byte[] Program(params byte[][] ops) => ops.SelectMany(o => o).ToArray();
    private static byte[] Op(FlowOp op, byte a = 0, byte b = 0, byte c = 0) => [(byte)op, a, b, c];
    private static byte[] Call(FlowMethod method, byte size = 0, byte c = 0) => Op(FlowOp.Call, (byte)method, size, c);
    private static byte[] Open(FlowPipe kind) => Op(FlowOp.Open, (byte)kind);
    private static byte[] Data(byte slot, int size) => Op(FlowOp.PipeData, slot, Size(size), 0x5A);
    private static byte[] Item(byte slot, int size, byte count = 1) => Op(FlowOp.ChannelItem, slot, Size(size), count);
    private static byte[] ProbePing() => Op(FlowOp.ProbePing);
    private static byte[] ProbeCall() => Op(FlowOp.ProbeCall);

    private static byte Size(int size)
    {
        var index = Array.IndexOf(Sizes, size);
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "Not in the size table.");
        return (byte)index;
    }

    /// <summary>
    /// Operations. Arguments <c>a</c>, <c>b</c>, <c>c</c> are reduced modulo whatever they select from.
    /// </summary>
    internal enum FlowOp : byte
    {
        /// <summary>Invoke method <c>a</c> (<see cref="FlowMethod"/>) with size <c>b</c>, or delay <c>c</c> ms for Cancellable.</summary>
        Call,
        /// <summary>Open a pipe of kind <c>a</c> (<see cref="FlowPipe"/>) and wait for the server's Ready.</summary>
        Open,
        /// <summary>Write <c>Sizes[b]</c> raw bytes filled with <c>c</c> to pipe slot <c>a</c>.</summary>
        PipeData,
        /// <summary>Write <c>c</c> % 64 (at least 1) byte[] channel items of <c>Sizes[b]</c> to pipe slot <c>a</c>
        /// as one stream, split into full frames like the channel writer does.</summary>
        ChannelItem,
        /// <summary>Send pipe state <c>b</c> for pipe slot <c>a</c>.</summary>
        PipeState,
        /// <summary>Complete the writer of pipe slot <c>a</c>.</summary>
        Complete,
        /// <summary>PipeWindowUpdate of <c>Sizes[b]</c> for pipe slot <c>a</c>.</summary>
        WindowUpdate,
        /// <summary>MessageStart for invocation <c>a</c>, kind <c>b</c>, totalLen <c>Sizes[c]</c> (for <c>c</c> = 0xFF,
        /// <see cref="uint.MaxValue"/>).</summary>
        MessageStart,
        /// <summary>MessageChunk of <c>Sizes[c]</c> bytes for invocation <c>a</c>, kind <c>b</c>.</summary>
        MessageChunk,
        /// <summary>MessageAbort for invocation <c>a</c>, kind <c>b</c>, reason <c>c</c>.</summary>
        MessageAbort,
        /// <summary>InvocationCredit of <c>a</c>.</summary>
        InvocationCredit,
        /// <summary>Cancel invocation <c>a</c>.</summary>
        Cancel,
        /// <summary>Unsolicited ping.</summary>
        Ping,
        /// <summary>Wait <c>a</c> % 101 ms.</summary>
        Pause,
        /// <summary>A frame of type <c>a</c> with <c>Sizes[b]</c> bytes filled with <c>c</c>.</summary>
        RawFrame,
        /// <summary>Ping and wait for the reply.</summary>
        ProbePing,
        /// <summary>Call <see cref="FlowMethod.Ping"/> and wait for the result.</summary>
        ProbeCall,
        /// <summary>Write <c>Sizes[b]</c> bytes to pipe slot <c>a</c> ignoring the credit granted.</summary>
        Overrun,
        /// <summary>Stop answering the server's pipe closes.</summary>
        NoCloseAcks,
        /// <summary>Wait up to 3 s for the server to close the connection.</summary>
        AwaitClose,
        /// <summary>From now on, a write that can't be sent in full within <see cref="ProbeTimeout"/> for lack of
        /// credit is a failure: the data must reach the server.</summary>
        StrictWrites,
        /// <summary>Send <c>a</c> invocations of <see cref="FlowMethod.Cancellable"/> (50 ms each) ignoring the
        /// invocation credit.</summary>
        InvokeIgnoringCredit,
        /// <summary>Call <see cref="FlowMethod.Echo"/> with <c>Sizes[b]</c> bytes and wait for the result; large
        /// arguments and results are streamed.</summary>
        CallLarge,
        /// <summary>Write the first half of a byte[] channel item of <c>Sizes[b]</c> to pipe slot <c>a</c>.</summary>
        PartialItem,
        /// <summary>Write a byte[] channel item of <c>Sizes[b]</c> to pipe slot <c>a</c> whose length prefix is off
        /// by <c>c</c> (signed), or for <c>c</c> = 0x80, declares <see cref="int.MaxValue"/>.</summary>
        BadItem,
        Count,
    }

    /// <summary>
    /// Method IDs of <see cref="IFlowServerNexus"/> (declaration order).
    /// </summary>
    internal enum FlowMethod : byte
    {
        Ping,
        Echo,
        Large,
        Cancellable,
        Drain,
        NeverRead,
        SlowItems,
        CloseAtOnce,
    }

    internal enum FlowPipe : byte
    {
        Drain,
        NeverRead,
        SlowItems,
        CloseAtOnce,
    }

    /// <summary>
    /// The client side of one connection: turns operations into frames.
    /// </summary>
    private sealed class FlowClient
    {
        private readonly PipeWriter _writer;
        private readonly ServerOutput _output;
        private readonly List<ushort> _invocations = [];
        private readonly List<(byte ClientId, ushort FullId, FlowPipe Kind)> _pipes = [];
        private readonly Dictionary<ushort, long> _sent = [];
        private long _invocationsSent;
        private bool _ackCloses = true;
        private bool _strictWrites;

        // Pipes the server may rightly stop taking data on: this client changed their state (completed, reset, ...)
        // or sent them data their reader can't accept (which resets a channel).
        private readonly HashSet<ushort> _stateChanged = [];
        private string? _writeStall;
        private ushort _nextInvocationId = 1;
        private byte _nextPipeId = 1;
        private int _bytesSent;
        private int _paused;

        public FlowClient(PipeWriter writer, ServerOutput output)
        {
            _writer = writer;
            _output = output;
        }

        /// <summary>
        /// Runs one operation. Returns a failure message if a probe stalled.
        /// </summary>
        public string? Execute(FlowOp op, byte a, byte b, byte c)
        {
            // Answer pipes the server closed since the last operation, as a NexNet client does.
            while (_output.TryTakeClosedByServer(out var closed))
            {
                if (_ackCloses)
                    SendPipeState(closed, NexusDuplexPipe.State.Complete);
            }

            switch (op)
            {
                case FlowOp.Call:
                    Call((FlowMethod)(a % 4), Sizes[b % Sizes.Length], c % 50);
                    return null;

                case FlowOp.Open:
                {
                    if (_nextPipeId == 0)
                        return null;
                    var clientId = _nextPipeId++;
                    var kind = (FlowPipe)(a % 4);
                    if (!Invoke(FlowMethod.Drain + (byte)kind, (ref MsgPackWriter w) => w.Write(clientId), InvocationFlags.DuplexPipe, out _))
                        return null;
                    var fullId = (ushort)0;
                    if (!_output.WaitFor(o => o.ReadyPipes.TryGetValue(clientId, out fullId) || o.RefusedPipes.Contains(clientId), ProbeTimeout))
                        return $"Pipe open ({kind}) neither acknowledged nor refused within {ProbeTimeout.TotalMilliseconds} ms.";
                    if (fullId != 0)
                        _pipes.Add((clientId, fullId, kind));
                    return null;
                }

                case FlowOp.PipeData:
                {
                    var data = new byte[Sizes[b % Sizes.Length]];
                    data.AsSpan().Fill(c);
                    PipeWrite(PipeId(a, c), data, suits: FlowPipe.Drain);
                    return _writeStall;
                }

                case FlowOp.ChannelItem:
                {
                    var item = Framed(NexusSerializer.Serialize(new byte[Sizes[b % Sizes.Length]]), 0);
                    var count = Math.Max(1, c % 64);
                    var stream = new byte[(long)item.Length * count > MaxBytesPerInput ? item.Length : item.Length * count];
                    for (var offset = 0; offset < stream.Length; offset += item.Length)
                        item.CopyTo(stream, offset);
                    PipeWrite(PipeId(a, 0), stream, suits: FlowPipe.SlowItems);
                    return _writeStall;
                }

                case FlowOp.PipeState:
                    _stateChanged.Add(PipeId(a, c));
                    SendPipeState(PipeId(a, c), (NexusDuplexPipe.State)b);
                    return null;

                case FlowOp.Complete:
                    _stateChanged.Add(PipeId(a, c));
                    SendPipeState(PipeId(a, c), NexusDuplexPipe.State.ClientWriterServerReaderComplete);
                    return null;

                case FlowOp.WindowUpdate:
                {
                    // [type][length][pipe id][+bytes: u32 LE], laid out like DuplexPipeWrite.
                    Span<byte> frame = stackalloc byte[9];
                    frame[0] = (byte)PipeWindowUpdate;
                    BinaryPrimitives.WriteUInt16LittleEndian(frame[1..], 4);
                    BinaryPrimitives.WriteUInt16LittleEndian(frame[3..], PipeId(a, c));
                    BinaryPrimitives.WriteUInt32LittleEndian(frame[5..], (uint)Sizes[b % Sizes.Length]);
                    Send(frame);
                    return null;
                }

                case FlowOp.MessageStart:
                    SendMessagePack(MessageStart, (ref MsgPackWriter w) =>
                    {
                        w.WriteArrayHeader(4);
                        w.Write(InvocationId(a));
                        w.Write((byte)(b & 1));
                        w.Write((byte)((b & 1) == 0 ? MessageType.Invocation : MessageType.InvocationResult));
                        w.Write(c == 0xFF ? uint.MaxValue : (uint)Sizes[c % Sizes.Length]);
                    });
                    return null;

                case FlowOp.MessageChunk:
                    SendChunks(InvocationId(a), (byte)(b & 1), new byte[Sizes[c % Sizes.Length]]);
                    return null;

                case FlowOp.CallLarge:
                {
                    // Echo with an argument of Sizes[b]: streamed when it does not fit a frame, as is the result.
                    if (!Invoke(FlowMethod.Echo, (ref MsgPackWriter w) => w.WriteBinary(new byte[Sizes[b % Sizes.Length]]), InvocationFlags.None, out var id))
                        return null;
                    return _output.WaitFor(o => o.Results.Contains(id), ProbeTimeout)
                        ? null
                        : $"Large call not answered within {ProbeTimeout.TotalMilliseconds} ms.";
                }

                case FlowOp.MessageAbort:
                    SendMessagePack(MessageAbort, (ref MsgPackWriter w) =>
                    {
                        w.WriteArrayHeader(3);
                        w.Write(InvocationId(a));
                        w.Write((byte)(b & 1));
                        w.Write((byte)(c % 4));
                    });
                    return null;

                case FlowOp.InvocationCredit:
                    SendMessagePack(InvocationCredit, (ref MsgPackWriter w) =>
                    {
                        w.WriteArrayHeader(1);
                        w.Write((ushort)a);
                    });
                    return null;

                case FlowOp.Cancel:
                    Send(FuzzServerHost.FrameMessage(MessageType.InvocationCancellation, new InvocationCancellationMessage { InvocationId = InvocationId(a) }));
                    return null;

                case FlowOp.Ping:
                    Send([(byte)MessageType.Ping]);
                    return null;

                case FlowOp.Pause:
                {
                    var delay = Math.Min(a % 101, MaxPausePerInput - _paused);
                    if (delay > 0)
                    {
                        _paused += delay;
                        Thread.Sleep(delay);
                    }
                    return null;
                }

                case FlowOp.RawFrame:
                {
                    var body = new byte[Math.Min(Sizes[b % Sizes.Length], MaxFrameBody)];
                    body.AsSpan().Fill(c);
                    SendFrame((MessageType)a, body);
                    return null;
                }

                case FlowOp.ProbePing:
                {
                    var pongs = _output.Pongs;
                    Send([(byte)MessageType.Ping]);
                    return _output.WaitFor(o => o.Pongs > pongs, ProbeTimeout)
                        ? null
                        : $"Ping not answered within {ProbeTimeout.TotalMilliseconds} ms.";
                }

                case FlowOp.ProbeCall:
                {
                    if (!Invoke(FlowMethod.Ping, null, InvocationFlags.None, out var id))
                        return null;
                    return _output.WaitFor(o => o.Results.Contains(id), ProbeTimeout)
                        ? null
                        : $"Call not answered within {ProbeTimeout.TotalMilliseconds} ms.";
                }

                case FlowOp.Overrun:
                {
                    var data = new byte[Sizes[b % Sizes.Length]];
                    PipeWrite(PipeId(a, 0), data, respectCredit: false);
                    return null;
                }

                case FlowOp.NoCloseAcks:
                    _ackCloses = false;
                    return null;

                case FlowOp.StrictWrites:
                    _strictWrites = true;
                    return null;

                case FlowOp.PartialItem:
                {
                    var item = Framed(NexusSerializer.Serialize(new byte[Sizes[b % Sizes.Length]]), 0);
                    PipeWrite(PipeId(a, 0), item.AsSpan(0, item.Length / 2));
                    return null;
                }

                case FlowOp.BadItem:
                {
                    // A prefix that disagrees with its item: c says by how much (as a signed byte, never 0); 0x80
                    // declares the largest length a prefix can hold.
                    var item = NexusSerializer.Serialize(new byte[Sizes[b % Sizes.Length]]);
                    var delta = (sbyte)c switch
                    {
                        0 => 1,
                        sbyte.MinValue => int.MaxValue - item.Length,
                        var d => d,
                    };
                    PipeWrite(PipeId(a, 0), Framed(item, delta));
                    return null;
                }

                case FlowOp.InvokeIgnoringCredit:
                    for (var i = 0; i < a; i++)
                        Invoke(FlowMethod.Cancellable, (ref MsgPackWriter w) => w.Write(50), InvocationFlags.None, out _, respectCredit: false);
                    return null;

                case FlowOp.AwaitClose:
                    _output.WaitFor(o => false, TimeSpan.FromSeconds(3));
                    return null;

                default:
                    return null;
            }
        }

        private void Call(FlowMethod method, int size, int delay)
        {
            switch (method)
            {
                case FlowMethod.Ping:
                    Invoke(method, null, InvocationFlags.None, out _);
                    break;
                case FlowMethod.Echo:
                    Invoke(method, (ref MsgPackWriter w) => w.WriteBinary(new byte[size]), InvocationFlags.None, out _);
                    break;
                case FlowMethod.Large:
                    Invoke(method, (ref MsgPackWriter w) => w.Write(size), InvocationFlags.None, out _);
                    break;
                case FlowMethod.Cancellable:
                    Invoke(method, (ref MsgPackWriter w) => w.Write(delay), InvocationFlags.None, out _);
                    break;
            }
        }

        /// <summary>
        /// Sends an invocation with one argument (or none). Returns false if it does not fit in a frame, or (unless
        /// <paramref name="respectCredit"/> is false) the server granted no invocation credit within
        /// <see cref="ProbeTimeout"/>.
        /// </summary>
        private bool Invoke(FlowMethod method, WriteAction? argument, InvocationFlags flags, out ushort id, bool respectCredit = true)
        {
            id = 0;
            if (respectCredit && !_output.WaitFor(o => o.InvocationCredits > _invocationsSent, ProbeTimeout))
                return false;

            var arguments = new ArrayBufferWriter<byte>();
            var writer = new MsgPackWriter(arguments);
            writer.WriteArrayHeader(argument == null ? 0 : 1);
            argument?.Invoke(ref writer);
            writer.Flush();

            id = _nextInvocationId++;
            var message = new InvocationMessage { InvocationId = id, MethodId = (ushort)method, Flags = flags, Arguments = arguments.WrittenMemory.ToArray() };
            var body = new ArrayBufferWriter<byte>();
            var bodyWriter = new MsgPackWriter(body);
            message.Serialize(ref bodyWriter);
            bodyWriter.Flush();
            _invocations.Add(id);
            _invocationsSent++;
            if (body.WrittenCount <= MaxFrameBody)
            {
                SendFrame(MessageType.Invocation, body.WrittenSpan);
                return true;
            }

            // Larger than a frame: stream it like a NexNet client does.
            var bodyLength = body.WrittenCount;
            var streamId = id;
            SendMessagePack(MessageStart, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(4);
                w.Write(streamId);
                w.Write((byte)0);
                w.Write((byte)MessageType.Invocation);
                w.Write((uint)bodyLength);
            });
            SendChunks(id, 0, body.WrittenSpan);
            return true;
        }

        /// <summary>
        /// An open pipe's ID by slot, or for the slot past the last one, an ID no pipe has.
        /// </summary>
        private ushort PipeId(byte slot, byte salt)
        {
            var index = slot % (_pipes.Count + 1);
            return index < _pipes.Count ? _pipes[index].FullId : (ushort)(0xF000 | salt);
        }

        /// <summary>
        /// An issued invocation ID by index, or for the index past the last one, an ID never issued.
        /// </summary>
        private ushort InvocationId(byte index)
        {
            var i = index % (_invocations.Count + 1);
            return i < _invocations.Count ? _invocations[i] : (ushort)(0xF000 | index);
        }

        /// <summary>
        /// Writes pipe data in frames. For an open pipe it sends only what the server's grants allow, waiting up to
        /// <see cref="CreditWait"/> for more, and gives up on the rest when none comes. An unknown pipe ID, or
        /// <paramref name="respectCredit"/> false, sends everything regardless.
        /// </summary>
        /// <param name="suits">The pipe kind this data is well-formed for (raw bytes for Drain, channel items for
        /// SlowItems), or null for data no reader accepts.</param>
        private void PipeWrite(ushort pipeId, ReadOnlySpan<byte> data, bool respectCredit = true, FlowPipe? suits = null)
        {
            var index = _pipes.FindIndex(p => p.FullId == pipeId);
            var limited = respectCredit && index >= 0;

            // Data the pipe's reader can't accept may reset it; after that the server need not take more.
            if (index >= 0 && _pipes[index].Kind != suits)
                _stateChanged.Add(pipeId);

            // Only a pipe the server reads, given only data it accepts, can be required to take all of it.
            var strict = _strictWrites && index >= 0 && _pipes[index].Kind == suits && !_stateChanged.Contains(pipeId);
            var sent = _sent.GetValueOrDefault(pipeId);
            while (data.Length > 0)
            {
                var length = Math.Min(data.Length, MaxFrameBody);
                if (limited)
                {
                    long granted = 0;
                    var wait = strict ? ProbeTimeout : CreditWait;
                    if (!_output.WaitFor(o => (granted = o.Granted.GetValueOrDefault(pipeId)) > sent, wait) || granted <= sent)
                    {
                        if (strict && !_output.IsClosed)
                            _writeStall = $"Pipe write got no credit for {data.Length} more bytes within {ProbeTimeout.TotalMilliseconds} ms.";
                        break;
                    }

                    length = (int)Math.Min(length, granted - sent);
                }

                var header = new byte[5];
                header[0] = (byte)MessageType.DuplexPipeWrite;
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1), (ushort)length);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), pipeId);
                if (!Send(header, data[..length]))
                    break;
                sent += length;
                data = data[length..];
            }

            _sent[pipeId] = sent;
        }

        /// <summary>
        /// A channel item as the channel writer sends it: its length prefix, then the item. The prefix is off by
        /// <paramref name="delta"/> (0 for a correct one).
        /// </summary>
        private static byte[] Framed(byte[] item, int delta)
        {
            var prefix = new byte[ChannelFraming.MaxPrefixLength];
            var prefixLength = ChannelFraming.WriteLength(prefix, Math.Max(0, item.Length + delta));
            return [.. prefix.AsSpan(0, prefixLength), .. item];
        }

        /// <summary>
        /// Sends <paramref name="payload"/> as MessageChunk frames: post-header [invocation id: u16 LE][kind: u8].
        /// </summary>
        private void SendChunks(ushort invocationId, byte kind, ReadOnlySpan<byte> payload)
        {
            do
            {
                var length = Math.Min(payload.Length, MaxFrameBody);
                var header = new byte[6];
                header[0] = (byte)MessageChunk;
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1), (ushort)length);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), invocationId);
                header[5] = kind;
                if (!Send(header, payload[..length]))
                    return;
                payload = payload[length..];
            } while (payload.Length > 0);
        }

        private void SendPipeState(ushort pipeId, NexusDuplexPipe.State state)
            => Send(FuzzServerHost.FrameMessage(MessageType.DuplexPipeUpdateState, new DuplexPipeUpdateStateMessage { PipeId = pipeId, State = state }));

        private void SendMessagePack(MessageType type, WriteAction write)
        {
            var body = new ArrayBufferWriter<byte>();
            var writer = new MsgPackWriter(body);
            write(ref writer);
            writer.Flush();
            SendFrame(type, body.WrittenSpan);
        }

        private void SendFrame(MessageType type, ReadOnlySpan<byte> body)
        {
            Span<byte> header = stackalloc byte[3];
            header[0] = (byte)type;
            BinaryPrimitives.WriteUInt16LittleEndian(header[1..], (ushort)body.Length);
            Send(header, body);
        }

        public bool Send(ReadOnlySpan<byte> bytes) => Send(bytes, default);

        /// <summary>
        /// Writes and flushes. Returns false once the per-input byte limit is reached.
        /// </summary>
        private bool Send(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            if (_bytesSent + first.Length + second.Length > MaxBytesPerInput)
                return false;
            _bytesSent += first.Length + second.Length;
            _writer.Write(first);
            if (second.Length > 0)
                _writer.Write(second);
            _writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            return true;
        }

        public delegate void WriteAction(ref MsgPackWriter writer);
    }
}

public interface IFlowServerNexus
{
    ValueTask Ping();
    ValueTask<byte[]?> Echo(byte[]? value);
    ValueTask<byte[]> Large(int size);
    ValueTask Cancellable(int delay, CancellationToken cancellationToken);
    ValueTask Drain(INexusDuplexPipe pipe);
    ValueTask NeverRead(INexusDuplexPipe pipe);
    ValueTask SlowItems(INexusDuplexChannel<byte[]> channel);
    ValueTask CloseAtOnce(INexusDuplexPipe pipe);
}

public interface IFlowClientNexus
{
}

[Nexus<IFlowServerNexus, IFlowClientNexus>(NexusType = NexusType.Server)]
public partial class FlowServerNexus
{
    public ValueTask Ping() => default;
    public ValueTask<byte[]?> Echo(byte[]? value) => new(value);
    public ValueTask<byte[]> Large(int size) => new(new byte[Math.Clamp(size, 0, 4 * 1024 * 1024)]);

    public async ValueTask Cancellable(int delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Math.Clamp(delay, 0, 50), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask Drain(INexusDuplexPipe pipe)
    {
        while (true)
        {
            var result = await pipe.Input.ReadAsync();
            pipe.Input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted || result.IsCanceled)
                break;
        }
    }

    /// <summary>
    /// Never reads; returns when the pipe completes (at the latest when the session ends).
    /// </summary>
    public async ValueTask NeverRead(INexusDuplexPipe pipe) => await pipe.CompleteTask;

    /// <summary>
    /// Returns at once, so the server closes the pipe straight away.
    /// </summary>
    public ValueTask CloseAtOnce(INexusDuplexPipe pipe) => default;

    /// <summary>
    /// Reads items with a delay per item.
    /// </summary>
    public async ValueTask SlowItems(INexusDuplexChannel<byte[]> channel)
    {
        await foreach (var _ in await channel.GetReaderAsync())
            await Task.Delay(10);
    }
}

[Nexus<IFlowClientNexus, IFlowServerNexus>(NexusType = NexusType.Client)]
public partial class FlowClientNexus
{
}
