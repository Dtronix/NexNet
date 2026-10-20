using System.Buffers;
using System.IO.Pipelines;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Serialization;

namespace NexNet.Fuzz;

/// <summary>
/// Parses what the server sends: the server greeting, pongs, invocation results, pipe Ready states and the
/// disconnect reason.
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
    public HashSet<ushort> Results { get; } = [];
    public Dictionary<byte, ushort> ReadyPipes { get; } = [];
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

            var postHeader = type == MessageType.DuplexPipeWrite ? 2 : 0;
            if (!reader.TryReadLittleEndian(out short signedLength) || reader.Remaining < postHeader + (ushort)signedLength)
            {
                // Incomplete frame: leave it for the next read.
                reader.Rewind(reader.Consumed - frameStart);
                break;
            }

            var length = (ushort)signedLength;
            reader.Advance(postHeader);
            var body = buffer.Slice(reader.Position, length);
            reader.Advance(length);
            Handle(type, body);
        }

        return reader.Position;
    }

    private void Handle(MessageType type, ReadOnlySequence<byte> body)
    {
        try
        {
            switch (type)
            {
                case MessageType.ServerGreeting:
                    Greeted = true;
                    break;

                case MessageType.DuplexPipeWrite:
                    break;

                case MessageType.InvocationResult:
                {
                    var reader = new MsgPackReader(body);
                    reader.ReadArrayHeader();
                    Results.Add(reader.ReadUInt16());
                    break;
                }

                case MessageType.DuplexPipeUpdateState:
                {
                    var reader = new MsgPackReader(body);
                    reader.ReadArrayHeader(2);
                    var pipeId = reader.ReadUInt16();
                    var state = (NexusDuplexPipe.State)reader.ReadByte();
                    if ((state & NexusDuplexPipe.State.Ready) != 0)
                        ReadyPipes.TryAdd((byte)pipeId, pipeId);
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
