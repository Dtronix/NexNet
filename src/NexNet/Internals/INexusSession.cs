using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Pipes;
using NexNet.Pools;
using NexNet.Transports;

namespace NexNet.Internals;

/// <summary>
/// Base interface for all sessions.
/// </summary>
internal interface INexusSession : ISessionMessenger
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
    /// Lock for the group registrations.
    /// </summary>
    Lock RegisteredGroupsLock { get; }

    /// <summary>
    /// Manages all the sessions connection on the server. Null if this is not a server session.
    /// </summary>
    IServerSessionManager? SessionManager { get; }

    /// <summary>
    ///  Store for maintaining data between invocations on the nexus.
    /// </summary>
    public SessionStore SessionStore { get; }

    /// <summary>
    /// Manages all the invocations for this session.
    /// </summary>
    ISessionInvocationStateManager SessionInvocationStateManager { get; }

    /// <summary>
    /// Last tick that this session received a message.
    /// </summary>
    long LastReceived { get; }

    INexusLogger? Logger { get; }

    /// <summary>
    /// Pool manager for the session.
    /// </summary>
    internal PoolManager PoolManager { get; }
    
    /// <summary>
    /// Manager for all the shared collections.
    /// </summary>
    internal NexusCollectionManager CollectionManager { get; }

    /// <summary>
    /// Configurations for the session.
    /// </summary>
    ConfigBase Config { get; }

    ConnectionState State { get; }
    bool IsServer { get; }
    NexusPipeManager PipeManager { get; }

    /// <summary>
    /// The remote address of the connected client.
    /// </summary>
    string? RemoteAddress { get; }

    /// <summary>
    /// The remote port of the connected client.
    /// </summary>
    int? RemotePort { get; }

    /// <summary>
    /// Disconnects the session if the last received time is less than the timeout ticks.
    /// </summary>
    /// <param name="timeoutTicks">Normally use <c>Config.Time.GetTickCount64() - Config.Timeout</c>.</param>
    /// <returns>True upon successful disconnection due to timeout.  False otherwise.</returns>
    bool DisconnectIfTimeout(long timeoutTicks);

    /// <summary>
    /// Receive memory budget for this session.
    /// </summary>
    SessionBudget Budget { get; }

    /// <summary>
    /// Largest argument, result or channel item this side may send: the smaller of its own and the peer's
    /// MaxMessageSize.
    /// </summary>
    int SendMaxMessageSize { get; }

    /// <summary>
    /// Sends an invocation once the peer has credit for it (docs/internals/flow-control-v2.md, section 4.7). Waits
    /// for credit in the caller's task, never in the read loop.
    /// </summary>
    /// <param name="message">The invocation.</param>
    /// <param name="cancellationToken">Cancels the wait for credit.</param>
    /// <param name="creditTimeout">Gives up when no credit comes within this time; null waits until the session
    /// ends.</param>
    /// <returns>False if the invocation was not sent: the session ended, or no credit came in time.</returns>
    ValueTask<bool> SendInvocation<TMessage>(TMessage message, CancellationToken cancellationToken = default, TimeSpan? creditTimeout = null)
        where TMessage : IMessageBase;

    /// <summary>
    /// Writes an invocation to the session's output if that needs no waiting: the peer has credit, no other send
    /// is in progress (including a flush to a backed-up transport) and the arguments fit in one frame. Like
    /// <c>ChannelWriter.TryWrite</c>, <see cref="InvocationWriteResult.Written"/> means the invocation is committed
    /// to the output in order, not that the peer has received it.
    /// </summary>
    /// <param name="message">The invocation. Not modified, so one message can be written to several sessions.</param>
    /// <param name="frame">The invocation already encoded as a frame (<see cref="MessageFrame"/>), copied instead of
    /// encoding <paramref name="message"/> again; empty to encode it.</param>
    /// <returns><see cref="InvocationWriteResult.Busy"/> if writing would have to wait; nothing was taken or written.
    /// Use <see cref="SendInvocation{TMessage}"/> to wait.</returns>
    InvocationWriteResult TryWriteInvocation<TMessage>(TMessage message, ReadOnlySpan<byte> frame = default)
        where TMessage : IInvocationMessage, IMessageBase;

    /// <summary>
    /// Sends control replies (pongs, window grants, close acknowledgements) off the read loop.
    /// </summary>
    SessionControlSender ControlSender { get; }
}

/// <summary>
/// Base session with proxy information.
/// </summary>
/// <typeparam name="TProxy">Proxy type for the session.</typeparam>
internal interface INexusSession<TProxy> : INexusSession
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Pool manager for the session.
    /// </summary>
    internal new SessionPoolManager<TProxy> PoolManager { get; }
}

