using System.Threading;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Server response to a client greeting.
/// Body: <c>[version (int), clientId (int64), maxMessageSize (int), invocationCredits (int), idleTimeoutMs (uint32)]</c>.
/// </summary>
/// <remarks>
/// <c>idleTimeoutMs</c> is a trailing field: a reader accepts a body without it (read as 0, not advertised) and skips
/// any elements after it, so fields can be appended later without breaking older readers.
/// </remarks>
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

    /// <summary>
    /// The server's idle timeout for this session (its endpoint's <c>ConfigBase.Timeout</c>), in milliseconds; 0 when
    /// the server did not advertise one. The client derives its default ping interval from it.
    /// </summary>
    public uint IdleTimeoutMs { get; set; }

    /// <summary>
    /// Elements before the trailing <see cref="IdleTimeoutMs"/> field; a body must have at least this many.
    /// </summary>
    private const int RequiredCount = 4;

    public void Serialize(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(RequiredCount + 1);
        writer.Write(Version);
        writer.Write(ClientId);
        writer.Write(MaxMessageSize);
        writer.Write(InvocationCredits);
        writer.Write(IdleTimeoutMs);
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        var count = reader.ReadArrayHeader();
        if (count < RequiredCount)
            throw NexusSerializationException.UnexpectedCount(RequiredCount + 1, count);

        Version = reader.ReadInt32();
        ClientId = reader.ReadInt64();
        MaxMessageSize = reader.ReadInt32();
        InvocationCredits = reader.ReadInt32();

        // Trailing fields: absent from older writers, and newer writers may append more.
        IdleTimeoutMs = count > RequiredCount ? reader.ReadUInt32() : 0;
        for (var i = RequiredCount + 1; i < count; i++)
            reader.Skip();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}
