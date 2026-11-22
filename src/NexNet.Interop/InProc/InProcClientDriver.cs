using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using NexNet.Collections;
using NexNet.Diagnostics;
using NexNet.Internals;
using NexNet.Interop.Reflection;
using NexNet.Interop.Scripting;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Pipes;

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
    private readonly ConcurrentDictionary<string, System.Threading.Channels.Channel<InProcRemotePipe>> _held = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly InProcMetricsRecorder? _metrics;
    private readonly Lock _lifecycleLock = new();
    private Task _lifecycle = Task.CompletedTask;
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
        if (spec.OnInvocationException is { } invocationError)
            InProcScriptBinder.SetInvocationExceptionHook(Nexus, invocationError);
        var config = InProcClientConfig.Create(spec, spec.Transport ?? target.Transport, out _fakeTime);
        if (spec.Metrics == MetricsRecording.Native)
            config.MeterFactory = _metrics = new InProcMetricsRecorder();
        _client = new NexusClient<TClientNexus, TServerProxy>(config, Nexus);
        _invoker = new InProcInvoker(() => _client.Proxy);

        var interpreter = new ScriptInterpreter(_signals, _invoker, RunNamedAsync);
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
            if (unbound.Contains(_facade.OnConnected))
            {
                // OnConnected runs where the C# client runs nexus.Connected: once the session has processed the
                // ServerGreeting (the handshake is complete), not when it reports Connected, which it does as soon as
                // the transport is up and before it sends its greeting. isReconnected is the value the client passes
                // to nexus.Connected: true for a session its reconnection loop opened (created in the Reconnecting
                // state, NexusSession's ReconnectingInProgress), false for a connect the test asked for.
                var onSessionSetup = config.InternalOnSessionSetup;
                config.InternalOnSessionSetup = session =>
                {
                    onSessionSetup?.Invoke(session);
                    var isReconnection = session.State == ConnectionState.Reconnecting;
                    var ready = SessionReadyTask(session);
                    EnqueueLifecycle(async () =>
                    {
                        try
                        {
                            await ready.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return; // The driver is disposed.
                        }

                        // The ready task also completes when the session ends during the handshake: no OnConnected then.
                        if (((IMeteredSession)session).IsEstablished)
                            binder.RunDetached(_facade.OnConnected, new JsonArray(JsonValue.Create(isReconnection)));
                    });
                };
            }

            if (unbound.Contains(_facade.OnDisconnected))
            {
                _client.StateChanged += (_, state) =>
                {
                    var previous = _lastState;
                    _lastState = state;
                    // Queued behind the session's OnConnected, so the two run in the order the client raised them.
                    if (state == ConnectionState.Disconnected && previous != ConnectionState.Disconnected)
                        EnqueueLifecycle(() =>
                        {
                            binder.RunDetached(_facade.OnDisconnected, new JsonArray());
                            return Task.CompletedTask;
                        });
                };
            }
        }
    }

    /// <summary>
    /// The task the C# client awaits before its connect returns (NexusSession's ready task, read by reflection: the
    /// client has no public handshake-complete event). It completes once the session has processed the ServerGreeting,
    /// right after the session starts nexus.Connected, or when the session ends first.
    /// </summary>
    private static Task SessionReadyTask(INexusSession session)
    {
        var field = session.GetType().GetField("_readyTaskCompletionSource", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"{session.GetType()} has no _readyTaskCompletionSource field; update {nameof(InProcClientDriver<TClientNexus, TServerProxy>)}.{nameof(SessionReadyTask)}.");
        return (field.GetValue(session) as TaskCompletionSource)?.Task ?? Task.CompletedTask;
    }

    /// <summary>Runs <paramref name="next"/> after the lifecycle deliveries queued before it.</summary>
    private void EnqueueLifecycle(Func<Task> next)
    {
        lock (_lifecycleLock)
        {
            _lifecycle = _lifecycle.ContinueWith(
                async _ =>
                {
                    try
                    {
                        await next().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // A lifecycle script has nobody to report to.
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
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
    public Task<byte[]?> GetRemoteCertificateAsync()
        => Task.FromResult(_client.State == ConnectionState.Connected ? Nexus.Context.RemoteCertificate?.RawData : null);

    /// <inheritdoc />
    public Task<bool> GetIsSecureAsync() => Task.FromResult(_client.IsSecure);

    /// <inheritdoc />
    public Task<RemoteEndpoint> GetRemoteEndpointAsync()
        => Task.FromResult(_client.State == ConnectionState.Connected
            ? new RemoteEndpoint(Nexus.Context.RemoteAddress, Nexus.Context.RemotePort)
            : RemoteEndpoint.None);

    /// <inheritdoc />
    public Task<NexusBudgetSnapshot?> GetBudgetAsync() => Task.FromResult(_client.GetSnapshot()?.Budget);

    /// <inheritdoc />
    /// <remarks>
    /// The reserved-ID count of the session's ID table (read through InternalsVisibleTo), the same count the language
    /// hosts report: a call canceled after send counts until its late reply releases its ID, and a call waiting for
    /// invocation credit holds no ID yet.
    /// </remarks>
    public Task<int> GetOutstandingInvocationsAsync()
        => Task.FromResult(_client.Session?.SessionInvocationStateManager.ReservedIdCount ?? 0);

    /// <inheritdoc />
    public Task<NexusSessionSnapshot?> GetSnapshotAsync() => Task.FromResult(_client.GetSnapshot());

    /// <inheritdoc />
    public Task<ClientMetrics> GetMetricsAsync()
        => _metrics == null
            ? throw new InvalidOperationException($"{Target}: create the client with ClientSpec.Metrics to read its metrics.")
            : Task.FromResult(_metrics.Read());

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
    public async Task<IRemotePipe> AcceptPipeAsync(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return await Held(name).Reader.ReadAsync(_lifetime.Token).ConfigureAwait(false);
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

        var watches = _invoker.Watches.Count;
        if (watches > 0)
            details.Add($"{watches} collection watch(es) not disposed");

        return new LeakReport(openClients, openPipes, 0, pending, watches, details);
    }

    /// <summary>
    /// The built-in handlers: <c>pipe.hold</c> needs this client's state (<see cref="AcceptPipeAsync"/>); the others are
    /// <see cref="InProcNamedHandlers"/>.
    /// </summary>
    private ValueTask<JsonNode?> RunNamedAsync(NamedStep step, ScriptCall call)
        => step.Handler == "pipe.hold" ? HoldAsync(step, call) : InProcNamedHandlers.RunAsync(step, call);

    /// <summary>
    /// <c>pipe.hold</c> (option <c>name</c>): publishes the call's pipe for <see cref="AcceptPipeAsync"/> and keeps the
    /// method running until the test disposes it, whatever happens to the call (a disconnect cancels the call, and the
    /// test still drives the pipe). Ends early only when the driver is disposed.
    /// </summary>
    private async ValueTask<JsonNode?> HoldAsync(NamedStep step, ScriptCall call)
    {
        var name = step.Options?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("pipe.hold needs the option 'name'.");
        if (call.Pipe is not INexusDuplexPipe pipe)
            throw new InvalidOperationException("The handler 'pipe.hold' needs a method that takes a pipe.");

        var held = new InProcRemotePipe(pipe, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _pipes.Add(held);
        await Held(name).Writer.WriteAsync(held).ConfigureAwait(false);
        try
        {
            await held.Released.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The driver is disposed.
        }

        return null;
    }

    private System.Threading.Channels.Channel<InProcRemotePipe> Held(string name)
        => _held.GetOrAdd(name, static _ => System.Threading.Channels.Channel.CreateUnbounded<InProcRemotePipe>());

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

        // Nothing waits for the disconnect message at the end of a test, whatever ClientSpec.DisconnectDelayMs says.
        _client.Config.DisconnectDelay = 0;
        await _client.DisposeAsync().ConfigureAwait(false);
        _metrics?.Dispose();
        _lifetime.Dispose();
    }
}
