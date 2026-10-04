using System.Threading;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Server response to a client greeting.
/// Body: <c>[version (int), clientId (int64)]</c>.
/// </summary>
internal partial class ServerGreetingMessage : IMessageBase
{
    public static MessageType Type => MessageType.ServerGreeting;

    private IPooledMessage? _messageCache = null!;

    public IPooledMessage? MessageCache
    {
        set => _messageCache = value;
    }

    public int Version { get; set; }

    public long ClientId { get; set; }

    public void Serialize(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(2);
        writer.Write(Version);
        writer.Write(ClientId);
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(2);
        Version = reader.ReadInt32();
        ClientId = reader.ReadInt64();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}
