using System.Threading;
using NexNet.Pipes;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Updates the state of a duplex pipe.
/// Body: <c>[pipeId (uint16, clientId | serverId &lt;&lt; 8), state (uint8), window (uint32)]</c>. <c>window</c> is the
/// receive credit granted with a Ready state, otherwise 0.
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

    /// <summary>
    /// Receive credit granted by the sender, in bytes. Sent with the Ready acknowledgement; otherwise 0.
    /// </summary>
    public uint Window { get; set; }

    public void Serialize(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(3);
        writer.Write(PipeId);
        writer.Write((byte)State);
        writer.Write(Window);
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(3);
        PipeId = reader.ReadUInt16();
        State = (NexusDuplexPipe.State)reader.ReadByte();
        Window = reader.ReadUInt32();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}
