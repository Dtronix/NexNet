using System;

namespace NexNet.Messages;

/// <summary>
/// Base for client greeting messages.  Used for reconnection and initial connection processes
/// are the same.
/// </summary>
internal interface IClientGreetingMessageBase : IMessageBase
{
    /// <summary>
    /// Largest authentication token accepted, in bytes. Checked before the token is allocated.
    /// </summary>
    public const int MaxAuthenticationTokenSize = 8192;

    /// <summary>
    /// Reads the authentication token into a pooled array after checking its declared length.
    /// </summary>
    protected static Memory<byte> ReadAuthenticationToken(ref NexNet.Serialization.MsgPackReader reader)
    {
        var probe = reader;
        // ReadBinaryHeader returns -1 for nil.
        if (probe.ReadBinaryHeader() > MaxAuthenticationTokenSize)
            throw new NexNet.Serialization.NexusSerializationException(
                $"Authentication token exceeds the maximum of {MaxAuthenticationTokenSize} bytes.");

        return reader.ReadBinaryToPooled(out _);
    }

    /// <summary>
    /// Targeted API version that the client is expecting.
    /// </summary>
    string? Version { get; set; }

    /// <summary>
    /// This is the hash of the server's implementation which matches the tarted API version.
    /// If this does not match the server's hash, then the server and client method invocations are out of sync
    /// and the client will not be allowed to connect to the server.
    /// </summary>
    int ServerNexusHash { get; set; }

    /// <summary>
    /// This is the hash of the client's nexus implementation.  If this does not match the server's allowed client
    /// hash, then the server and client method invocations are out of sync.
    /// </summary>
    int ClientNexusHash { get; set; }

    /// <summary>
    /// (Optional) Token to be passed to the server upon connection for validation.
    /// </summary>
    Memory<byte> AuthenticationToken { get; set; }

    /// <summary>
    /// Largest argument, result or channel item the client accepts, in bytes.
    /// </summary>
    int MaxMessageSize { get; set; }

    /// <summary>
    /// Invocations the server may have queued on the client at once.
    /// </summary>
    int InvocationCredits { get; set; }
}
