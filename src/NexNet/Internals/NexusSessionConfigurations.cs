using System;
using System.Threading.Tasks;
using NexNet.Diagnostics;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pools;
using NexNet.RateLimiting;
using NexNet.Transports;

namespace NexNet.Internals;

internal readonly struct NexusSessionConfigurations<TNexus, TProxy>
    where TNexus : NexusBase<TProxy>, IMethodInvoker, IInvocationMethodHash
    where TProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    public required ConfigBase Configs { get; init; }

    public required ConnectionState ConnectionState { get; init; }

    public required ITransport Transport { get; init; }

    public required SessionPoolManager<TProxy> Pool { get; init; }

    public required IServerSessionManager? SessionManager { get; init; }

    public required long Id { get; init; }

    public required TNexus Nexus { get; init; }
    public required INexusLogger? Logger { get; init; }

    public TaskCompletionSource? ReadyTaskCompletionSource { get; init; }
    public TaskCompletionSource<DisconnectReason>? DisconnectedTaskCompletionSource { get; init; }
    public INexusClient? Client { get; init; }
    public NexusCollectionManager CollectionManager { get; init; }

    /// <summary>
    /// Remote address string for rate limiting release on disconnect.
    /// This is the same value passed to IConnectionRateLimiter.TryAcquire().
    /// </summary>
    public string? RateLimiterAddress { get; init; }

    /// <summary>
    /// Rate limiter reference for release on disconnect.
    /// </summary>
    public IConnectionRateLimiter? RateLimiter { get; init; }

    /// <summary>
    /// Server only: called once when the session completes authentication or ends before that, to free its
    /// <c>MaxPendingHandshakes</c> slot.
    /// </summary>
    public Action? HandshakeEnded { get; init; }

    /// <summary>
    /// Server only: receives sessions that end before authentication completes.
    /// </summary>
    public PreAuthFailureLog? PreAuthFailureLog { get; init; }

    /// <summary>
    /// Tags the session is counted under in metrics. Resolved from <see cref="Configs"/> when null.
    /// </summary>
    public MetricsTagSet? MetricsTags { get; init; }
}
