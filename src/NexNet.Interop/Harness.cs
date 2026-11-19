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

/// <summary>Creates the in-process C# client (<see cref="InProcClientDriver{TClientNexus, TServerProxy}"/>).</summary>
public sealed class InProcClientDriverFactory : IClientDriverFactory
{
    /// <inheritdoc />
    public Task<IClientUnderTest> CreateAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec)
        where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
        => Task.FromResult<IClientUnderTest>(new InProcClientDriver<TClientNexus, TServerProxy>(target, spec));
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
    /// not connected. Skips the test (visibly) when <see cref="ClientTarget.SkipReason"/> is set.
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

        var resolved = spec.Clone();
        resolved.Transport = target.Transport;
        return await factory.CreateAsync<TClientNexus, TServerProxy>(target, resolved).ConfigureAwait(false);
    }
}
