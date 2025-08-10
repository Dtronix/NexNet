using System.Threading.Tasks;
using NexNet.Cache;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Transports;

namespace NexNet.Internals;

internal readonly struct NexusSessionConfigurations<TNexus, TProxy>
    where TNexus : NexusBase<TProxy>, IMethodInvoker, IInvocationMethodHash
    where TProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    public required ConfigBase Configs { get; init; }

    public required ConnectionState ConnectionState { get; init; }

    public required ITransport Transport { get; init; }

    public required SessionCacheManager<TProxy> Cache { get; init; }

    public required SessionManager? SessionManager { get; init; }

    public required long Id { get; init; }

    public required TNexus Nexus { get; init; }

    public TaskCompletionSource? ReadyTaskCompletionSource { get; init; }
    public TaskCompletionSource<DisconnectReason>? DisconnectedTaskCompletionSource { get; init; }
    public INexusClient? Client { get; init; }
    public NexusCollectionManager CollectionManager { get; init; }
}
