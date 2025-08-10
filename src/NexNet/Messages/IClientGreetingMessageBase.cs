using System;

namespace NexNet.Messages;

/// <summary>
/// Base for client greeting messages.  Used for reconnection and initial connection processes
/// are the same.
/// </summary>
internal interface IClientGreetingMessageBase : IMessageBase
{
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
}
