using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Cache;
using NexNet.Invocation;
using NexNet.Messages;

namespace NexNet.Internals;

/// <summary>
/// Base interface for all sessions.
/// </summary>
internal interface INexNetSession
{
    /// <summary>
    /// ID of this session.
    /// </summary>
    long Id { get; }

    /// <summary>
    /// List all the groups that this session is registered with.  Always an empty list on client sessions.
    /// </summary>
    List<int> RegisteredGroups { get; }

    /// <summary>
    /// Disconnects the client for with the specified reason.  Notifies the other side of the session upon calling.
    /// </summary>
    /// <param name="reason">Reason for disconnect.</param>
    /// <returns>Task which completes upon disconnection.</returns>
    Task DisconnectAsync(DisconnectReason reason);

    /// <summary>
    /// Manages all the sessions connection on the server. Null if this is not a server session.
    /// </summary>
    SessionManager? SessionManager { get; }

    /// <summary>
    ///  Store for maintaining data between invocations on the hub.
    /// </summary>
    public SessionStore SessionStore { get; }

    /// <summary>
    /// Manages all the invocations for this session.
    /// </summary>
    SessionInvocationStateManager SessionInvocationStateManager { get; }

    /// <summary>
    /// Last tick that this session received a message.
    /// </summary>
    long LastReceived { get; }

    /// <summary>
    /// Sends a header with a message body.
    /// </summary>
    /// <typeparam name="TMessage">Type of message to send. Must implement IMessageBodyBase</typeparam>
    /// <param name="body">Message to send.</param>
    /// <param name="cancellationToken">Cancellation token to cancel sending.</param>
    /// <returns>Task which completes upon sending.</returns>
    ValueTask SendHeaderWithBody<TMessage>(TMessage body, CancellationToken cancellationToken = default)
        where TMessage : IMessageBodyBase;

    /// <summary>
    /// Sends the passed sequence with prefixed header type and length.
    /// </summary>
    /// <param name="type">Type of header to send.</param>
    /// <param name="body">Sequence of data to send in teh body</param>
    /// <param name="cancellationToken">Cancellation token for sending.</param>
    /// <returns>Task which completes upon sending.</returns>
    ValueTask SendHeaderWithBody(MessageType type, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a header over the wire.
    /// </summary>
    /// <param name="type">Type of header to send.</param>
    /// <param name="cancellationToken">Cancellation token to cancel sending.</param>
    /// <returns>Task which completes upon sending.</returns>
    ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects the session if the last received time is less than the timeout ticks.
    /// </summary>
    /// <param name="timeoutTicks">Normally use Environment.TickCount64 - Timeout</param>
    /// <returns>True upon successful disconnection due to timeout.  False otherwise.</returns>
    bool DisconnectIfTimeout(long timeoutTicks);

}

/// <summary>
/// Base session with proxy information.
/// </summary>
/// <typeparam name="TProxy">Proxy type for the session.</typeparam>
internal interface INexNetSession<TProxy> : INexNetSession
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Contains 
    /// </summary>
    internal SessionCacheManager<TProxy> CacheManager { get; }
}

