using System.Threading.Tasks;
using NexNet.Cache;
using NexNet.Invocation;
using NexNet.Transports;

namespace NexNet.Internals;

internal readonly struct NexNetSessionConfigurations<THub, TProxy>
    where THub : HubBase<TProxy>, IMethodInvoker<TProxy>, IInvocationMethodHash
    where TProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    public required ConfigBase Configs { get; init; }

    public required ITransport Transport { get; init; }

    public required SessionCacheManager<TProxy> Cache { get; init; }

    public required SessionManager? SessionManager { get; init; }

    public required bool IsServer { get; init; }

    public required long Id { get; init; }

    public required THub Hub { get; init; }
    public TaskCompletionSource? ReadyTaskCompletionSource { get; init; }
    public TaskCompletionSource? DisconnectedTaskCompletionSource { get; init; }
}
