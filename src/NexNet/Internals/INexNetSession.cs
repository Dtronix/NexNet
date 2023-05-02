using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Cache;
using NexNet.Invocation;
using NexNet.Messages;

namespace NexNet.Internals;

internal interface INexNetSession
{
    long Id { get; }
    List<int> RegisteredGroups { get; }
    Task DisconnectAsync(DisconnectReason reason);
    SessionManager? SessionManager { get; }

    SessionInvocationStateManager SessionInvocationStateManager { get; }
    long LastReceived { get; }

    ValueTask SendHeaderWithBody<TMessage>(TMessage body, CancellationToken cancellationToken = default)
        where TMessage : IMessageBodyBase;

    ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default);
}

internal interface INexNetSession<TProxy> : INexNetSession
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    internal SessionCacheManager<TProxy> CacheManager { get; }

    public SessionStore SessionStore { get; }
}

