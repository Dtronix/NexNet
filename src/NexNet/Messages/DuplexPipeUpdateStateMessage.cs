using System.Threading;
using NexNet.Pipes;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Updates the state of a duplex pipe.
/// Body: <c>[pipeId (uint16, clientId | serverId &lt;&lt; 8), state (uint8)]</c>.
/// </summary>
internal partial class DuplexPipeUpdateStateMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.DuplexPipeUpdateState;

    private IPooledMessage? _messageCache = null!;

    public IPooledMessage? MessageCache
    {
        set => _messageCache = value;
    }

    public ushort PipeId { get; set; }

    public NexusDuplexPipe.State State { get; set; }

    public void Serialize(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(2);
        writer.Write(PipeId);
        writer.Write((byte)State);
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(2);
        PipeId = reader.ReadUInt16();
        State = (NexusDuplexPipe.State)reader.ReadByte();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}
