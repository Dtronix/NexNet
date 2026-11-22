using System.Collections.Concurrent;
using NexNet.Collections;
using NexNet.Interop.InProc;
using NexNet.Invocation;
using NexNet.TestNexus.Interop;

namespace NexNet.Interop;

/// <summary>Creates clients under test for one language.</summary>
public interface IClientDriverFactory
{
    /// <summary>
    /// Creates a client of the nexus pair (<typeparamref name="TClientNexus"/>, <typeparamref name="TServerProxy"/>) for
    /// <paramref name="target"/>; it is not connected. A remote driver identifies the pair by the client nexus
    /// interface; the in-process driver uses the types themselves.
    /// </summary>
    Task<IClientUnderTest> CreateAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec)
        where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new();
}

/// <summary>
/// Creates the in-process C# client (<see cref="InProcClientDriver{TClientNexus, TServerProxy}"/>) and client pool
/// (<see cref="InProcPoolDriver{TClientNexus, TServerProxy}"/>).
/// </summary>
public sealed class InProcClientDriverFactory : IClientDriverFactory, IPoolDriverFactory
{
    /// <inheritdoc />
    public Task<IClientUnderTest> CreateAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec)
        where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
        => Task.FromResult<IClientUnderTest>(new InProcClientDriver<TClientNexus, TServerProxy>(target, spec));

    /// <inheritdoc />
    public Task<IPoolUnderTest> CreatePoolAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec, ClientPoolSpec pool)
        where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
        => Task.FromResult<IPoolUnderTest>(new InProcPoolDriver<TClientNexus, TServerProxy>(target, spec, pool));
}

/// <summary>
/// Entry point of tests on the client matrix: creates the client under test of a <see cref="ClientTarget"/> row through
/// the driver registered for its language.
/// </summary>
/// <example>
/// <code>
/// [Test, ClientMatrix]
/// public async Task Echo(ClientTarget target)
/// {
///     await using var server = await InteropServerHost.StartAsync&lt;InteropServerNexus, InteropServerNexus.ClientProxy&gt;(() =&gt; new());
///     await using var client = await Harness.CreateClientAsync(target, ClientSpec.ForEndpoint(server.Endpoints, target.Transport));
///     await client.ConnectAsync();
///     var echoed = await client.Proxy&lt;IInteropServerNexus&gt;().EchoString("hi");
/// }
/// </code>
/// </example>
public static class Harness
{
    private static readonly ConcurrentDictionary<string, IClientDriverFactory> Drivers = new(StringComparer.Ordinal)
    {
        [ClientLanguages.CSharp] = new InProcClientDriverFactory(),
    };

    /// <summary>Registers the driver of <paramref name="language"/> (H05: the remote driver of each language host).</summary>
    public static void RegisterDriver(string language, IClientDriverFactory factory)
    {
        ArgumentNullException.ThrowIfNull(language);
        Drivers[language] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>Creates a client of the interop nexus (<see cref="InteropClientNexus"/>).</summary>
    public static Task<IClientUnderTest> CreateClientAsync(ClientTarget target, ClientSpec spec)
        => CreateClientAsync<InteropClientNexus, InteropClientNexus.ServerProxy>(target, spec);

    /// <summary>
    /// Creates a client of the nexus pair (<typeparamref name="TClientNexus"/>, <typeparamref name="TServerProxy"/>); it is
    /// not connected. Skips the test (visibly) when <see cref="ClientTarget.SkipReason"/> is set. The spec gets the
    /// target's transport and variant (<see cref="ClientSpec.WebSocketConnector"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">No driver is registered for the target's language.</exception>
    public static async Task<IClientUnderTest> CreateClientAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec)
        where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spec);
        target.SkipIfUnsupported();

        if (spec.Transport is { } transport && transport != target.Transport)
            throw new ArgumentException($"The spec is for {transport}, the target for {target.Transport}.", nameof(spec));

        if (!Drivers.TryGetValue(target.Language, out var factory))
        {
            throw new InvalidOperationException(
                $"No client driver is registered for '{target.Language}' ({ClientLanguages.EnvironmentVariable}). "
                + "Remote languages need their language host (H05).");
        }

        return await factory.CreateAsync<TClientNexus, TServerProxy>(target, Resolve(target, spec)).ConfigureAwait(false);
    }

    /// <summary>Creates a client pool of the interop nexus (<see cref="InteropClientNexus"/>).</summary>
    public static Task<IPoolUnderTest> CreatePoolAsync(ClientTarget target, ClientSpec spec, ClientPoolSpec? pool = null)
        => CreatePoolAsync<InteropClientNexus, InteropClientNexus.ServerProxy>(target, spec, pool);

    /// <summary>
    /// Creates a client pool of the nexus pair (<typeparamref name="TClientNexus"/>, <typeparamref name="TServerProxy"/>)
    /// whose clients use <paramref name="spec"/>; it holds no client yet. Skips the test (visibly) when
    /// <see cref="ClientTarget.SkipReason"/> is set. A test that creates pools declares
    /// <see cref="ClientCapabilities.ClientPool"/> (<see cref="ClientMatrixAttribute.Requires"/>), so a language without
    /// pools gets no row for it.
    /// </summary>
    /// <param name="target">The language and transport.</param>
    /// <param name="spec">The configuration of the pool's clients.</param>
    /// <param name="pool">The pool options; null for the runtime defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">A member of <paramref name="pool"/> is out of range (on every row).</exception>
    /// <exception cref="InvalidOperationException">No driver is registered for the target's language.</exception>
    /// <exception cref="NotSupportedException">
    /// The language lacks <see cref="ClientCapabilities.ClientPool"/> (the test does not declare it), or its driver has no
    /// pool driver.
    /// </exception>
    public static async Task<IPoolUnderTest> CreatePoolAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec, ClientPoolSpec? pool = null)
        where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spec);
        var options = pool?.Clone() ?? new ClientPoolSpec();
        options.Validate();
        target.SkipIfUnsupported();
        if ((ClientLanguages.Get(target.Language).Capabilities & ClientCapabilities.ClientPool) == 0)
        {
            throw new NotSupportedException(
                $"The {target.Language} client lacks: {ClientCapabilities.ClientPool}. Declare Requires = ClientCapabilities.ClientPool on the test's matrix.");
        }

        if (spec.Transport is { } transport && transport != target.Transport)
            throw new ArgumentException($"The spec is for {transport}, the target for {target.Transport}.", nameof(spec));

        if (!Drivers.TryGetValue(target.Language, out var factory))
        {
            throw new InvalidOperationException(
                $"No client driver is registered for '{target.Language}' ({ClientLanguages.EnvironmentVariable}). "
                + "Remote languages need their language host (H05).");
        }

        if (factory is not IPoolDriverFactory pools)
            throw new NotSupportedException($"The '{target.Language}' driver creates no client pools.");

        return await pools.CreatePoolAsync<TClientNexus, TServerProxy>(target, Resolve(target, spec), options).ConfigureAwait(false);
    }

    /// <summary>A copy of <paramref name="spec"/> with the transport and variant of <paramref name="target"/>.</summary>
    private static ClientSpec Resolve(ClientTarget target, ClientSpec spec)
    {
        var resolved = spec.Clone();
        resolved.Transport = target.Transport;
        if (target.Variant != null)
            resolved.WebSocketConnector = target.Variant;
        return resolved;
    }
}
