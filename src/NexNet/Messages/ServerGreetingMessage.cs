using System.Threading;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Server response to a client greeting.
/// Body: <c>[version (int), clientId (int64), maxMessageSize (int), invocationCredits (int)]</c>.
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

    /// <summary>
    /// Largest argument, result or channel item the server accepts, in bytes.
    /// </summary>
    public int MaxMessageSize { get; set; }

    /// <summary>
    /// Invocations the client may have queued on the server at once.
    /// </summary>
    public int InvocationCredits { get; set; }

    public void Serialize(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(4);
        writer.Write(Version);
        writer.Write(ClientId);
        writer.Write(MaxMessageSize);
        writer.Write(InvocationCredits);
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(4);
        Version = reader.ReadInt32();
        ClientId = reader.ReadInt64();
        MaxMessageSize = reader.ReadInt32();
        InvocationCredits = reader.ReadInt32();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}
