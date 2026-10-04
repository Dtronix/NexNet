using System;
using System.Threading;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Client greeting sent to the server on connection.
/// Body: <c>[version (str|nil), serverNexusHash (int), clientNexusHash (int), authenticationToken (bin|nil)]</c>.
/// </summary>
internal partial class ClientGreetingMessage : IClientGreetingMessageBase
{
    private bool _isArgumentPoolArray;
    public static MessageType Type { get; } = MessageType.ClientGreeting;

    private IPooledMessage? _messageCache = null!;

    public IPooledMessage? MessageCache
    {
        set => _messageCache = value;
    }

    public string? Version { get; set; }

    public int ServerNexusHash { get; set; }

    public int ClientNexusHash { get; set; }

    public Memory<byte> AuthenticationToken { get; set; }

    public void Serialize(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(4);
        writer.Write(Version);
        writer.Write(ServerNexusHash);
        writer.Write(ClientNexusHash);
        writer.WriteBinary(AuthenticationToken.Span);
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(4);
        Version = reader.ReadString();
        ServerNexusHash = reader.ReadInt32();
        ClientNexusHash = reader.ReadInt32();
        AuthenticationToken = reader.ReadBinaryToPooled(out _);
        _isArgumentPoolArray = true;
    }

    public void Dispose()
    {
        var cache = Interlocked.Exchange(ref _messageCache, null);

        if (cache == null)
            return;

        if (_isArgumentPoolArray)
        {

            // Reset the pool flag.
            _isArgumentPoolArray = false;

            if (!AuthenticationToken.IsEmpty)
            {
                // Clear sensitive authentication data before returning buffer to pool
                AuthenticationToken.Span.Clear();
                IMessageBase.ReturnPooledMemory(AuthenticationToken);
            }

            AuthenticationToken = default;
        }

        cache.Return(this);
    }
}
