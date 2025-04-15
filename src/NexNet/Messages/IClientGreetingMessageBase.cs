using System;

namespace NexNet.Messages;

/// <summary>
/// Base for client greeting messages.  Used for interium where the reconnection and initial connection processes
/// are the same.
/// </summary>
internal interface IClientGreetingMessageBase : IMessageBase
{
    /// <summary>
    /// Version of the connecting client.
    /// </summary>
    int Version { get; set; }

    /// <summary>
    /// This is the hash of the server's methods.  If this does not match the server's hash,
    /// then the server and client method invocations are out of sync.
    /// </summary>
    int ServerNexusMethodHash { get; set; }

    /// <summary>
    /// This is the hash of the client's methods.  If this does not match the server's hash,
    /// then the server and client method invocations are out of sync.
    /// </summary>
    int ClientNexusMethodHash { get; set; }

    /// <summary>
    /// (Optional) Token to be passed to the server upon connection for validation.
    /// </summary>
    Memory<byte> AuthenticationToken { get; set; }
}
