using System.Collections.Concurrent;
using NexNet.Collections;
using NexNet.Interop.Remote;
using NexNet.Invocation;
using TUnit.Core.Interfaces;

namespace NexNet.Interop.Hosts;

/// <summary>
/// The language hosts of a test session, one per remote language of <c>NEXNET_TEST_LANGS</c>. A TUnit
/// <c>ClassDataSource</c> shared per test session: initializing it launches every host (hello, schema check, warm-up)
/// and registers its driver with <see cref="Harness"/>, so <see cref="Harness.CreateClientAsync(ClientTarget, ClientSpec)"/>
/// creates remote clients for its rows.
/// </summary>
/// <remarks>
/// <para>
/// The pool is an <see cref="IAsyncDiscoveryInitializer"/>: TUnit initializes it while it discovers the tests, before it
/// builds the <see cref="ClientMatrixAttribute"/> rows of the class that takes it. So the transports and capabilities each
/// host reports in its hello (<see cref="ClientLanguages.Register"/>) decide which rows are skipped. Every test class with
/// remote rows takes the pool, so its rows are built after the hosts are probed.
/// </para>
/// <para>
/// A language whose host cannot start (not built, stale schema, crashed at launch) gets a driver that fails every row
/// with the reason and the command that rebuilds it; its rows keep the built-in capabilities of
/// <see cref="ClientLanguages"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [ClassDataSource&lt;LanguageHostPool&gt;(Shared = SharedType.PerTestSession)]
/// public class InvocationTests(LanguageHostPool hosts) { … }
/// </code>
/// </example>
public sealed class LanguageHostPool : IAsyncDiscoveryInitializer, IAsyncDisposable
{
    private readonly object _initializeGate = new();
    private Task? _initialization;
    private readonly ConcurrentDictionary<string, LanguageHost> _hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Exception> _failures = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<string> _languages;
    private readonly Func<string, HostLaunchConfig> _config;
    private readonly IHostLauncher _launcher;
    private readonly Func<LanguageHostOptions>? _options;

    /// <summary>The pool of the remote languages of <c>NEXNET_TEST_LANGS</c>.</summary>
    public LanguageHostPool()
        : this(ClientLanguages.FromEnvironment())
    {
    }

    /// <summary>A pool of <paramref name="languages"/> (<see cref="ClientLanguages.CSharp"/> is skipped: it runs in process).</summary>
    /// <param name="languages">The languages.</param>
    /// <param name="config">Loads a language's launch configuration; default <see cref="HostLaunchConfig.Load"/>.</param>
    /// <param name="launcher">Launches the hosts; default real processes.</param>
    /// <param name="options">Creates the supervision settings; default: the expected schema hash of the repository.</param>
    public LanguageHostPool(
        IEnumerable<string> languages,
        Func<string, HostLaunchConfig>? config = null,
        IHostLauncher? launcher = null,
        Func<LanguageHostOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(languages);
        _languages = languages.Where(l => l != ClientLanguages.CSharp).Distinct(StringComparer.Ordinal).ToArray();
        _config = config ?? (language => HostLaunchConfig.Load(language));
        _launcher = launcher ?? ProcessHostLauncher.Instance;
        _options = options;
    }

    /// <summary>The pool initialized last in this process, if any.</summary>
    public static LanguageHostPool? Current { get; private set; }

    /// <summary>The remote languages of the pool.</summary>
    public IReadOnlyList<string> Languages => _languages;

    /// <summary>The running hosts, by language.</summary>
    public IReadOnlyDictionary<string, LanguageHost> Hosts => _hosts;

    /// <summary>Why a language's host could not start, by language.</summary>
    public IReadOnlyDictionary<string, Exception> Failures => _failures;

    /// <summary>
    /// Launches every host and registers what each reports; failures are recorded and surface on the rows of their
    /// language. Runs once: later calls return the first call's task.
    /// </summary>
    public Task InitializeAsync()
    {
        lock (_initializeGate)
            return _initialization ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        Current = this;
        if (_languages.Count == 0)
            return;

        LanguageHostOptions? options = null;
        Exception? optionsFailure = null;
        try
        {
            options = _options?.Invoke() ?? DefaultOptions();
        }
        catch (Exception e)
        {
            optionsFailure = e;
        }

        await Task.WhenAll(_languages.Select(async language =>
        {
            try
            {
                if (optionsFailure != null)
                    throw optionsFailure;

                var host = new LanguageHost(language, _config(language), _launcher, options);
                try
                {
                    await host.StartAsync().ConfigureAwait(false);
                }
                catch
                {
                    await host.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                _hosts[language] = host;
                Harness.RegisterDriver(language, host.DriverFactory);
            }
            catch (Exception e)
            {
                _failures[language] = e;
                Harness.RegisterDriver(language, new FailingDriverFactory(language, e));
            }
        })).ConfigureAwait(false);
    }

    /// <summary>Stops every host.</summary>
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_hosts.Values.Select(h => h.DisposeAsync().AsTask())).ConfigureAwait(false);
        _hosts.Clear();
        if (Current == this)
            Current = null;
    }

    private static LanguageHostOptions DefaultOptions()
    {
        var root = HostLaunchConfig.FindRepoRoot() ?? throw new HostNotBuiltException(
            $"The repository root (holding src/NexNet.slnx) was not found above {AppContext.BaseDirectory}; set {HostLaunchConfig.RepoRootVariable}.");
        return new LanguageHostOptions { ExpectedSchemaHash = SchemaHash.Expected(root) };
    }

    /// <summary>Fails every row of a language whose host could not start.</summary>
    private sealed class FailingDriverFactory(string language, Exception reason) : IClientDriverFactory, IPoolDriverFactory
    {
        public Task<IClientUnderTest> CreateAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec)
            where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
            where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
            => Task.FromException<IClientUnderTest>(Unavailable());

        public Task<IPoolUnderTest> CreatePoolAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec, ClientPoolSpec pool)
            where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
            where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
            => Task.FromException<IPoolUnderTest>(Unavailable());

        private InvalidOperationException Unavailable()
            => new($"The {language} language host is not available: {reason.Message}", reason);
    }
}
