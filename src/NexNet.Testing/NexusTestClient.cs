using NexNet.Collections;
using NexNet.Invocation;
using NexNet.Pipes;

namespace NexNet.Testing;

/// <summary>
/// Per-connection handle handed back by <c>NexusTestHost.ConnectAsync</c>. Exposes the
/// client-side proxy (for outbound invocations) and the user's client nexus instance (for
/// inspection of incoming-callback state). The harness records server-side dispatch on the
/// host's <c>ServerRecorder</c>; per-client assertions are added in a later phase.
/// </summary>
public sealed class NexusTestClient<TClientNexus, TServerProxy>
    where TClientNexus : ClientNexusBase<TServerProxy>, IMethodInvoker, IInvocationMethodHash, ICollectionConfigurer
    where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    /// <summary>Underlying <see cref="NexusClient{TClientNexus, TServerProxy}"/>.</summary>
    public NexusClient<TClientNexus, TServerProxy> Client { get; }

    /// <summary>Server-side proxy for outbound invocations.</summary>
    public TServerProxy Server => Client.Proxy;

    /// <summary>The user's client nexus instance.</summary>
    public TClientNexus Nexus { get; }

    internal NexusTestClient(NexusClient<TClientNexus, TServerProxy> client, TClientNexus nexus)
    {
        Client = client;
        Nexus = nexus;
    }

    /// <summary>
    /// Convenience: rents a duplex pipe via the client's session context. The same pipe can then
    /// be passed to a server proxy call that accepts an <see cref="IRentedNexusDuplexPipe"/>
    /// parameter, and driven via the streaming extensions in <c>NexNet.Testing.Streaming</c>.
    /// </summary>
    public IRentedNexusDuplexPipe CreatePipe() => Nexus.Context.CreatePipe();
}
