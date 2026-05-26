using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Invocation;
using NexNet.Pipes;
using NexNet.Testing.Recording;

namespace NexNet.Testing;

/// <summary>
/// Per-connection handle handed back by <c>NexusTestHost.ConnectAsync</c>. Exposes the
/// client-side proxy (for outbound invocations), the user's client nexus instance (for
/// inspection of incoming-callback state), and per-client assertion methods that scan only
/// the invocations this client's session dispatched.
/// </summary>
public sealed class NexusTestClient<TClientNexus, TServerProxy>
    where TClientNexus : ClientNexusBase<TServerProxy>, IMethodInvoker, IInvocationMethodHash, ICollectionConfigurer
    where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private readonly InvocationRecorder _recorder;
    private readonly RecorderAssertions _assertions;

    /// <summary>Underlying <see cref="NexusClient{TClientNexus, TServerProxy}"/>.</summary>
    public NexusClient<TClientNexus, TServerProxy> Client { get; }

    /// <summary>Server-side proxy for outbound invocations.</summary>
    public TServerProxy Server => Client.Proxy;

    /// <summary>The user's client nexus instance.</summary>
    public TClientNexus Nexus { get; }

    internal NexusTestClient(
        NexusClient<TClientNexus, TServerProxy> client,
        TClientNexus nexus,
        InvocationRecorder recorder)
    {
        Client = client;
        Nexus = nexus;
        _recorder = recorder;
        _assertions = new RecorderAssertions(recorder);
    }

    /// <summary>
    /// Convenience: rents a duplex pipe via the client's session context. The same pipe can then
    /// be passed to a server proxy call that accepts an <see cref="IRentedNexusDuplexPipe"/>
    /// parameter, and driven via the streaming extensions in <c>NexNet.Testing.Streaming</c>.
    /// </summary>
    public IRentedNexusDuplexPipe CreatePipe() => Nexus.Context.CreatePipe();

    /// <summary>
    /// Asserts this client's nexus has dispatched a method matching <paramref name="expression"/>
    /// exactly <paramref name="times"/> times. Call after <c>host.QuiesceAsync</c> so that any
    /// in-flight invocations have been recorded.
    /// </summary>
    public void AssertReceived<TInterface>(Expression<Action<TInterface>> expression, int times = 1)
        => _assertions.AssertReceived(expression, times);

    /// <summary>
    /// Asserts no invocation matching <paramref name="expression"/> has been dispatched on this
    /// client's nexus. Call after <c>host.QuiesceAsync</c>.
    /// </summary>
    public void AssertNotReceived<TInterface>(Expression<Action<TInterface>> expression)
        => _assertions.AssertNotReceived(expression);

    /// <summary>
    /// Awaits an invocation matching <paramref name="expression"/> arriving on this client's
    /// nexus within <paramref name="timeout"/> (default 5 seconds). Returns immediately if a
    /// match is already recorded; throws <see cref="TimeoutException"/> on deadline.
    /// </summary>
    public Task WaitFor<TInterface>(
        Expression<Action<TInterface>> expression,
        TimeSpan? timeout = null)
        => _assertions.WaitFor(expression, timeout);
}
