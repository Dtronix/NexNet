using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Serialization;

namespace NexNet.Fuzz;

/// <summary>
/// Harness 4: flow control and session liveness (docs/internals/flow-control-v2.md). Each input is a program of
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
/// The session budget check is added with the budget itself (step 4).
/// </para>
/// <para>
/// The v2 frames (<c>PipeWindowUpdate</c>, <c>MessageStart</c>/<c>Chunk</c>/<c>Abort</c>, <c>InvocationCredit</c>)
/// use the proposed type numbers. Protocol v1 peers reject them as unknown types; each encoder is brought in line
/// with the final layout by the step that adds the message.
/// </para>
/// </remarks>
public static class FlowHarness
{
    public static TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(double.TryParse(Environment.GetEnvironmentVariable("NEXNET_FUZZ_PROBE_TIMEOUT_MS"), out var ms) ? ms : 1000);

    /// <summary>
    /// Value sizes, indexed by an operation argument. Includes the limits of v1 (one frame, the 256 KiB
    /// high-water cutoff) and of v2 (the default window).
    /// </summary>
    public static readonly int[] Sizes = [0, 1, 100, 1024, 16 * 1024, 40 * 1024, 65535, 65536, 300 * 1024, 1024 * 1024];

    private const int MaxBytesPerInput = 8 * 1024 * 1024;
    private const int MaxPausePerInput = 500;
    private const int MaxFrameBody = ushort.MaxValue;

    // Proposed v2 message types (spec 4.1).
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
    ];

    private static readonly Lazy<FuzzServerHost> Instance = new(() => FuzzServerHost.Start(
        static config => FlowServerNexus.CreateServer(config, static () => new FlowServerNexus()).StartAsync(),
        IInvocationMethodHash.GetMethodHash<FlowServerNexus>(),
        IInvocationMethodHash.GetMethodHash<FlowClientNexus>()));

    public static void Run(byte[] data)
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
        yield return Program(Call(FlowMethod.Cancellable, c: 40), Op(FlowOp.Cancel), ProbePing());

        // Pipes and channels with readers that keep up.
        yield return Program(Open(FlowPipe.Drain), Data(0, 65535), Data(0, 65535), Op(FlowOp.Complete), ProbePing(), ProbeCall());
        yield return Program(Open(FlowPipe.SlowItems), Item(0, 1024, 10), Item(0, 100), Op(FlowOp.Complete), ProbeCall());
        yield return Program(Open(FlowPipe.Drain), Open(FlowPipe.SlowItems), Data(0, 16 * 1024), Item(1, 100), ProbePing());

        // Pipe state: an unexpected state, data after Complete, state for an unknown pipe.
        yield return Program(Open(FlowPipe.Drain), Op(FlowOp.PipeState, 0, (byte)NexusDuplexPipe.State.Ready), ProbePing());
        yield return Program(Open(FlowPipe.Drain), Op(FlowOp.Complete), Data(0, 100), ProbePing());
        yield return Program(Op(FlowOp.PipeState, 0, (byte)NexusDuplexPipe.State.ClientWriterServerReaderComplete), ProbePing());

        // Every v2 frame once, plus raw frames, pauses and unsolicited pings.
        yield return Program(Open(FlowPipe.Drain), Op(FlowOp.WindowUpdate, 0, Size(65536)), ProbePing());
        yield return Program(Call(FlowMethod.Ping), Op(FlowOp.MessageStart, 0, 0, Size(300 * 1024)), Op(FlowOp.MessageChunk, 0, 0, Size(16 * 1024)), ProbePing());
        yield return Program(Op(FlowOp.MessageAbort, 0, 1, 2), ProbePing());
        yield return Program(Op(FlowOp.InvocationCredit, 4), ProbePing());
        yield return Program(Op(FlowOp.RawFrame, 200, Size(100)), ProbePing());
        yield return Program(Op(FlowOp.Ping), Op(FlowOp.Pause, 20), Op(FlowOp.Ping), ProbePing());
    }

    /// <summary>
    /// Reproductions of the defects in spec section 1, with the step of spec section 8 that fixes each. They fail
    /// until that step lands.
    /// </summary>
    public static IEnumerable<(string Name, byte[] Input, int FixedInStep)> Reproductions()
    {
        // A result larger than a frame breaks every later send on the session.
        yield return ("Oversized result, then a call", Program(Call(FlowMethod.Large, Size(300 * 1024)), ProbeCall()), 1);

        // Pipe methods hold an invocation slot for the pipe's lifetime; the read loop waits for a free slot.
        yield return ("Three pipe methods, then a call", Program(Open(FlowPipe.Drain), Open(FlowPipe.Drain), Open(FlowPipe.Drain), ProbeCall()), 3);

        // BufferData does not wake the reader for a frame that reaches the high-water mark, then waits for the reader
        // to drain below the low-water mark.
        yield return ("1 MiB in full frames to a reader that keeps up", Program(Open(FlowPipe.Drain), Data(0, 1024 * 1024), ProbePing()), 1);

        // The application's reproductions (spec section 9).
        yield return ("40 x 40 KiB items with a slow reader, then a call", Program(Open(FlowPipe.SlowItems), Item(0, 40 * 1024, 40), ProbeCall()), 4);
        yield return ("Pipe completed while the reader's buffer is full", Program(Open(FlowPipe.NeverRead), Data(0, 300 * 1024), Op(FlowOp.Complete), ProbePing()), 4);
        yield return ("One 300 KiB item", Program(Open(FlowPipe.SlowItems), Item(0, 300 * 1024), ProbePing()), 6);
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
        /// <summary>MessageStart for invocation <c>a</c>, kind <c>b</c>, totalLen <c>Sizes[c]</c>.</summary>
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
    }

    internal enum FlowPipe : byte
    {
        Drain,
        NeverRead,
        SlowItems,
    }

    /// <summary>
    /// The client side of one connection: turns operations into frames.
    /// </summary>
    private sealed class FlowClient
    {
        private readonly PipeWriter _writer;
        private readonly ServerOutput _output;
        private readonly List<ushort> _invocations = [];
        private readonly List<(byte ClientId, ushort FullId)> _pipes = [];
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
                    var kind = (FlowPipe)(a % 3);
                    if (!Invoke(FlowMethod.Drain + (byte)kind, (ref MsgPackWriter w) => w.Write(clientId), InvocationFlags.DuplexPipe, out _))
                        return null;
                    var fullId = (ushort)0;
                    if (!_output.WaitFor(o => o.ReadyPipes.TryGetValue(clientId, out fullId), ProbeTimeout))
                        return $"Pipe open ({kind}) not acknowledged within {ProbeTimeout.TotalMilliseconds} ms.";
                    if (fullId != 0)
                        _pipes.Add((clientId, fullId));
                    return null;
                }

                case FlowOp.PipeData:
                {
                    var data = new byte[Sizes[b % Sizes.Length]];
                    data.AsSpan().Fill(c);
                    PipeWrite(PipeId(a, c), data);
                    return null;
                }

                case FlowOp.ChannelItem:
                {
                    var item = NexusSerializer.Serialize(new byte[Sizes[b % Sizes.Length]]);
                    var count = Math.Max(1, c % 64);
                    var stream = new byte[(long)item.Length * count > MaxBytesPerInput ? item.Length : item.Length * count];
                    for (var offset = 0; offset < stream.Length; offset += item.Length)
                        item.CopyTo(stream, offset);
                    PipeWrite(PipeId(a, 0), stream);
                    return null;
                }

                case FlowOp.PipeState:
                    SendPipeState(PipeId(a, c), (NexusDuplexPipe.State)b);
                    return null;

                case FlowOp.Complete:
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
                        w.Write((uint)Sizes[c % Sizes.Length]);
                    });
                    return null;

                case FlowOp.MessageChunk:
                {
                    // [invocation id: u16 LE][kind: u8][payload], split into frames.
                    var remaining = Sizes[c % Sizes.Length];
                    do
                    {
                        var payload = Math.Min(remaining, MaxFrameBody - 3);
                        var body = new byte[3 + payload];
                        BinaryPrimitives.WriteUInt16LittleEndian(body, InvocationId(a));
                        body[2] = (byte)(b & 1);
                        SendFrame(MessageChunk, body);
                        remaining -= payload;
                    } while (remaining > 0);
                    return null;
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
        /// Sends an invocation with one argument (or none). Returns false if it does not fit in a frame.
        /// </summary>
        private bool Invoke(FlowMethod method, WriteAction? argument, InvocationFlags flags, out ushort id)
        {
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
            if (body.WrittenCount > MaxFrameBody)
                return false;

            _invocations.Add(id);
            SendFrame(MessageType.Invocation, body.WrittenSpan);
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

        private void PipeWrite(ushort pipeId, ReadOnlySpan<byte> data)
        {
            do
            {
                var length = Math.Min(data.Length, MaxFrameBody);
                var header = new byte[5];
                header[0] = (byte)MessageType.DuplexPipeWrite;
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1), (ushort)length);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), pipeId);
                if (!Send(header, data[..length]))
                    return;
                data = data[length..];
            } while (data.Length > 0);
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
