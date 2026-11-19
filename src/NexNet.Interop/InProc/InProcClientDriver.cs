using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using NexNet.Collections;
using NexNet.Interop.Reflection;
using NexNet.Interop.Scripting;
using NexNet.Invocation;
using NexNet.Messages;

namespace NexNet.Interop.InProc;

/// <summary>
/// The C# client in process, as an <see cref="IClientUnderTest"/>. It wraps a real <see cref="NexusClient{TClientNexus, TServerProxy}"/>
/// and goes through the same typed-JSON proxies and the same <see cref="ScriptInterpreter"/> as the language hosts.
/// </summary>
/// <typeparam name="TClientNexus">The client nexus.</typeparam>
/// <typeparam name="TServerProxy">Its server proxy.</typeparam>
public sealed class InProcClientDriver<TClientNexus, TServerProxy> : IClientUnderTest
    where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
    where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    private readonly NexusClient<TClientNexus, TServerProxy> _client;
    private readonly FakeTimeProvider? _fakeTime;
    private readonly InProcInvoker _invoker;
    private readonly ScriptSignals _signals = new();
    private readonly ScriptFacade? _facade;
    private readonly ConcurrentDictionary<Type, object> _proxies = new();
    private readonly ConcurrentDictionary<string, object> _lists = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<InProcRemotePipe> _pipes = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ConnectionState _lastState = ConnectionState.Disconnected;
    private int _disposed;

    /// <summary>Creates the driver; the client is not connected.</summary>
    public InProcClientDriver(ClientTarget target, ClientSpec spec)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spec);
        Target = target;
        Capabilities = ClientLanguages.Get(ClientLanguages.CSharp).Capabilities;
        Nexus = new TClientNexus();
        var config = InProcClientConfig.Create(spec, spec.Transport ?? target.Transport, out _fakeTime);
        _client = new NexusClient<TClientNexus, TServerProxy>(config, Nexus);
        _invoker = new InProcInvoker(() => _client.Proxy);

        var interpreter = new ScriptInterpreter(_signals, _invoker, InProcNamedHandlers.RunAsync);
        var binder = new InProcScriptBinder(interpreter, () => _lifetime.Token);

        // The script facade of the client nexus is bound up front, so an unscripted method behaves as on every other
        // language: it completes with its default result.
        var facadeType = typeof(TClientNexus).GetInterfaces()
            .Select(i => i.FullName == null ? null : NexusTypes.FindScriptFacade(i.FullName))
            .FirstOrDefault(t => t != null);
        if (facadeType != null)
        {
            _facade = (ScriptFacade)Activator.CreateInstance(facadeType)!;
            var unbound = binder.Bind(_facade, Nexus);
            if (unbound.Count > 0)
            {
                _client.StateChanged += (_, state) =>
                {
                    var previous = _lastState;
                    _lastState = state;
                    foreach (var slot in unbound)
                    {
                        if (state == ConnectionState.Connected && slot == _facade.OnConnected)
                            binder.RunDetached(slot, new JsonArray(JsonValue.Create(previous == ConnectionState.Reconnecting)));
                        else if (state == ConnectionState.Disconnected && previous != ConnectionState.Disconnected && slot == _facade.OnDisconnected)
                            binder.RunDetached(slot, new JsonArray());
                    }
                };
            }
        }
    }

    /// <summary>The C# client nexus. In-process only: tests must not depend on it to stay portable.</summary>
    public TClientNexus Nexus { get; }

    /// <summary>The C# client. In-process only: tests must not depend on it to stay portable.</summary>
    public NexusClient<TClientNexus, TServerProxy> Client => _client;

    /// <inheritdoc />
    public ClientTarget Target { get; }

    /// <inheritdoc />
    public ClientCapabilities Capabilities { get; }

    /// <inheritdoc />
    public Task ConnectAsync() => _client.ConnectAsync();

    /// <inheritdoc />
    public async Task<bool> TryConnectAsync() => (await _client.TryConnectAsync().ConfigureAwait(false)).Success;

    /// <inheritdoc />
    public Task DisconnectAsync() => _client.DisconnectAsync();

    /// <inheritdoc />
    public Task<ConnectionState> GetStateAsync() => Task.FromResult(_client.State);

    /// <inheritdoc />
    public async Task WaitForStateAsync(ConnectionState state, double seconds)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, ConnectionState current)
        {
            if (current == state)
                reached.TrySetResult();
        }

        _client.StateChanged += OnChanged;
        try
        {
            if (_client.State == state)
                return;
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{Target}: state {state} not reached within {seconds} s; the state is {_client.State}.");
        }
        finally
        {
            _client.StateChanged -= OnChanged;
        }
    }

    /// <inheritdoc />
    public async Task<DisconnectReason> WhenDisconnectedAsync(double seconds)
    {
        var task = _client.DisconnectedTask;
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{Target}: not disconnected within {seconds} s.");
        }

        return task is Task<DisconnectReason> withReason ? withReason.Result : DisconnectReason.None;
    }

    /// <inheritdoc />
    public Task WhenReadyAsync(double seconds) => WaitForStateAsync(ConnectionState.Connected, seconds);

    /// <inheritdoc />
    public TProxy Proxy<TProxy>()
        => (TProxy)_proxies.GetOrAdd(typeof(TProxy), t => NexusTypes.CreateRemoteProxy(NexusTypes.GetRemoteProxyType(t), _invoker));

    /// <inheritdoc />
    public TScript Callbacks<TScript>()
    {
        if (_facade is TScript script)
            return script;

        throw new InvalidOperationException(_facade == null
            ? $"No generated script facade for {typeof(TClientNexus)}. Add [assembly: NexusTestScaffolding<TServer, TClient>] for its nexus pair."
            : $"The script facade of {typeof(TClientNexus)} is {_facade.GetType()}, not {typeof(TScript)}.");
    }

    /// <inheritdoc />
    public Task<IRemotePipe> CreatePipeAsync()
    {
        var pipe = new InProcRemotePipe(_client.CreatePipe());
        _pipes.Add(pipe);
        return Task.FromResult<IRemotePipe>(pipe);
    }

    /// <inheritdoc />
    public IRemoteList<T> List<T>(string collectionName)
    {
        ArgumentNullException.ThrowIfNull(collectionName);
        return (IRemoteList<T>)_lists.GetOrAdd(collectionName, name =>
        {
            var serverInterface = typeof(TServerProxy).GetInterfaces()
                .FirstOrDefault(i => i.GetProperty(name) != null && i.FullName != null)
                ?? throw new ArgumentException($"The server nexus has no collection '{name}'.", nameof(collectionName));
            // The generated remote proxy holds the collection's descriptor.
            var proxy = _proxies.GetOrAdd(serverInterface, t => NexusTypes.CreateRemoteProxy(NexusTypes.GetRemoteProxyType(t), _invoker));
            return proxy.GetType().GetProperty(name, typeof(IRemoteList<T>))?.GetValue(proxy) as IRemoteList<T>
                   ?? throw new ArgumentException($"The collection '{name}' does not hold {typeof(T)}.", nameof(collectionName));
        });
    }

    /// <inheritdoc />
    public Task SignalAsync(string name)
    {
        _signals.Raise(name);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task BarrierAsync() => _invoker.WhenDetachedCompleteAsync();

    /// <inheritdoc />
    public Task AdvanceTimeAsync(TimeSpan by)
    {
        if (_fakeTime == null)
            throw new InvalidOperationException($"{Target}: create the client with ClientSpec.FakeTime to move its clock.");
        _fakeTime.Advance(by);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<LeakReport> EndTestAuditAsync()
    {
        // Give completions already in flight a moment to settle before counting.
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (_invoker.Pending > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10).ConfigureAwait(false);

        var details = new List<string>();
        var openClients = Volatile.Read(ref _disposed) == 0 && _client.State != ConnectionState.Disconnected ? 1 : 0;
        if (openClients > 0)
            details.Add($"client {Target} is {_client.State} and not disposed");

        var openPipes = _pipes.Count(p => !p.IsDisposed && !p.Pipe.CompleteTask.IsCompleted);
        if (openPipes > 0)
            details.Add($"{openPipes} pipe(s) neither completed nor disposed");

        var pending = _invoker.Pending;
        if (pending > 0)
            details.Add($"{pending} invocation(s) still running");

        return new LeakReport(openClients, openPipes, 0, pending, 0, details);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var pipe in _pipes)
        {
            try
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The session may already be gone.
            }
        }

        // Nothing waits for the disconnect message at the end of a test.
        _client.Config.DisconnectDelay = 0;
        await _client.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
