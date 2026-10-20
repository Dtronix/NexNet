using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Serialization;

namespace NexNet.Fuzz;

/// <summary>
/// Parses what the server sends: the server greeting, pongs, invocation results, pipe opens (Ready with its window, or
/// Refused), window grants, pipe closes and the disconnect reason.
/// </summary>
internal sealed class ServerOutput
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _changed = new(0);
    private bool _preambleRead;
    private string? _malformed;

    /// <summary>
    /// True once the server greeting arrived: the connection is complete and accepts other messages.
    /// </summary>
    public bool Greeted { get; private set; }

    public int Pongs { get; private set; }

    /// <summary>
    /// Invocation credit granted by the server in total: its greeting's advertised credits plus every
    /// InvocationCredit message.
    /// </summary>
    public long InvocationCredits { get; private set; }
    public HashSet<ushort> Results { get; } = [];
    public Dictionary<byte, ushort> ReadyPipes { get; } = [];

    /// <summary>
    /// Client pipe IDs whose open the server refused.
    /// </summary>
    public HashSet<byte> RefusedPipes { get; } = [];

    /// <summary>
    /// Credit the server granted per full pipe ID, in total.
    /// </summary>
    public Dictionary<ushort, long> Granted { get; } = [];

    /// <summary>
    /// Full pipe IDs the server closed (sent its Complete), in order.
    /// </summary>
    public Queue<ushort> ClosedByServer { get; } = new();
    public DisconnectReason? Disconnect { get; private set; }

    /// <summary>
    /// True once the server sent a disconnect or its output ended.
    /// </summary>
    public bool IsClosed { get; private set; }

    public async Task ReadAsync(PipeReader reader)
    {
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync().ConfigureAwait(false);
                SequencePosition consumed;
                lock (_gate)
                    consumed = Parse(result.Buffer);
                _changed.Release();
                reader.AdvanceTo(consumed, result.Buffer.End);
                if (result.IsCompleted)
                    break;
            }
        }
        finally
        {
            lock (_gate)
                IsClosed = true;
            _changed.Release();
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> holds or the server closed. Returns false on timeout.
    /// </summary>
    public bool WaitFor(Func<ServerOutput, bool> condition, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            lock (_gate)
            {
                if (IsClosed || condition(this))
                    return true;
            }

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0 || !_changed.Wait(TimeSpan.FromMilliseconds(remaining)))
            {
                lock (_gate)
                    return IsClosed || condition(this);
            }
        }
    }

    /// <summary>
    /// Takes the next pipe the server closed, if any.
    /// </summary>
    public bool TryTakeClosedByServer(out ushort pipeId)
    {
        lock (_gate)
            return ClosedByServer.TryDequeue(out pipeId);
    }

    public void ThrowIfMalformed()
    {
        if (_malformed != null)
            throw new InvalidOperationException($"Server sent malformed output: {_malformed}");
    }

    private SequencePosition Parse(ReadOnlySequence<byte> buffer)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!_preambleRead)
        {
            if (reader.Remaining < FuzzServerHost.Preamble.Length)
                return reader.Position;
            reader.Advance(FuzzServerHost.Preamble.Length);
            _preambleRead = true;
        }

        while (!IsClosed)
        {
            var frameStart = reader.Consumed;
            if (!reader.TryRead(out var typeByte))
                break;

            var type = (MessageType)typeByte;
            if (typeByte is >= 20 and < 39)
            {
                Disconnect = (DisconnectReason)typeByte;
                IsClosed = true;
                reader.Advance(reader.Remaining);
                break;
            }

            if (type == MessageType.Ping)
            {
                Pongs++;
                continue;
            }

            var postHeader = type is MessageType.DuplexPipeWrite or MessageType.PipeWindowUpdate ? 2 : 0;
            if (!reader.TryReadLittleEndian(out short signedLength) || reader.Remaining < postHeader + (ushort)signedLength)
            {
                // Incomplete frame: leave it for the next read.
                reader.Rewind(reader.Consumed - frameStart);
                break;
            }

            var length = (ushort)signedLength;
            ushort pipeId = 0;
            if (postHeader == 2)
            {
                reader.TryReadLittleEndian(out short signedPipeId);
                pipeId = (ushort)signedPipeId;
            }
            var body = buffer.Slice(reader.Position, length);
            reader.Advance(length);
            Handle(type, pipeId, body);
        }

        return reader.Position;
    }

    private void Handle(MessageType type, ushort pipeId, ReadOnlySequence<byte> body)
    {
        try
        {
            switch (type)
            {
                case MessageType.ServerGreeting:
                {
                    var reader = new MsgPackReader(body);
                    reader.ReadArrayHeader(4);
                    reader.ReadInt32();
                    reader.ReadInt64();
                    reader.ReadInt32();
                    InvocationCredits += reader.ReadInt32();
                    Greeted = true;
                    break;
                }

                case MessageType.InvocationCredit:
                {
                    var reader = new MsgPackReader(body);
                    reader.ReadArrayHeader(1);
                    InvocationCredits += reader.ReadUInt16();
                    break;
                }

                case MessageType.DuplexPipeWrite:
                    break;

                case MessageType.InvocationResult:
                {
                    var reader = new MsgPackReader(body);
                    reader.ReadArrayHeader();
                    Results.Add(reader.ReadUInt16());
                    break;
                }

                case MessageType.PipeWindowUpdate:
                {
                    if (body.Length != sizeof(uint))
                    {
                        _malformed ??= $"window update body of {body.Length} bytes";
                        break;
                    }

                    Span<byte> credit = stackalloc byte[sizeof(uint)];
                    body.CopyTo(credit);
                    Granted[pipeId] = Granted.GetValueOrDefault(pipeId) + BinaryPrimitives.ReadUInt32LittleEndian(credit);
                    break;
                }

                case MessageType.DuplexPipeUpdateState:
                {
                    var reader = new MsgPackReader(body);
                    reader.ReadArrayHeader(3);
                    var statePipeId = reader.ReadUInt16();
                    var state = (NexusDuplexPipe.State)reader.ReadByte();
                    var window = reader.ReadUInt32();
                    if ((state & NexusDuplexPipe.State.Refused) != 0)
                    {
                        RefusedPipes.Add((byte)statePipeId);
                    }
                    else if ((state & NexusDuplexPipe.State.Ready) != 0 && ReadyPipes.TryAdd((byte)statePipeId, statePipeId))
                    {
                        Granted[statePipeId] = Granted.GetValueOrDefault(statePipeId) + window;
                    }

                    if (state == NexusDuplexPipe.State.Complete)
                        ClosedByServer.Enqueue(statePipeId);
                    break;
                }

                default:
                    _malformed ??= $"unexpected message type {type}";
                    break;
            }
        }
        catch (NexusSerializationException e)
        {
            _malformed ??= $"{type}: {e.Message}";
        }
    }
}
