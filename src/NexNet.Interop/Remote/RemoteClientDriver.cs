using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NexNet.Interop.Reflection;
using NexNet.Messages;

namespace NexNet.Interop.Remote;

/// <summary>
/// A client in a language host, as an <see cref="IClientUnderTest"/>: every member is a control request
/// (docs/internals/interop-control-protocol.md §6), and error kinds come back as the C# client's exceptions.
/// </summary>
public sealed class RemoteClientDriver : IClientUnderTest
{
    /// <summary>The bound of ops that wait for something, when the caller gives none.</summary>
    internal const int DefaultWaitMs = 120_000;

    /// <summary>The bound of ops that complete at once.</summary>
    internal const int DefaultOpMs = 30_000;

    private readonly ConcurrentDictionary<Type, object> _proxies = new();
    private readonly ConcurrentDictionary<string, object> _lists = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _outstanding = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _forwards = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Action<ControlEvent>> _listWatches = new(StringComparer.Ordinal);
    private readonly IDisposable _events;
    private readonly ScriptFacade? _facade;
    private readonly RemoteInvoker _invoker;
    private readonly string _serverInterface;
    private Exception? _postFailure;
    private long _nextToken;
    private int _disposed;

    /// <summary>Creates the driver of client <paramref name="handle"/>; use <see cref="RemoteClientDriverFactory"/>.</summary>
    /// <param name="connection">The host's control connection.</param>
    /// <param name="test">The test scope of the client.</param>
    /// <param name="handle">The client handle.</param>
    /// <param name="target">The language and transport.</param>
    /// <param name="clientInterface">Full metadata name of the client nexus interface (for the script facade).</param>
    /// <param name="serverInterface">Full metadata name of the server nexus interface (for <see cref="List{T}"/>).</param>
    public RemoteClientDriver(ControlConnection connection, string test, string handle, ClientTarget target, string clientInterface, string serverInterface)
    {
        _serverInterface = serverInterface ?? throw new ArgumentNullException(nameof(serverInterface));
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Test = test ?? throw new ArgumentNullException(nameof(test));
        Handle = handle ?? throw new ArgumentNullException(nameof(handle));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        ArgumentNullException.ThrowIfNull(clientInterface);
        Capabilities = connection.Hello.Capabilities;
        _invoker = new RemoteInvoker(this);

        // The facade exists from the start, so calls the server makes before the test asks for it (OnConnected) are
        // recorded, and an unscripted slot behaves as on every other row.
        var facadeType = NexusTypes.FindScriptFacade(clientInterface);
        if (facadeType != null)
        {
            _facade = (ScriptFacade)Activator.CreateInstance(facadeType)!;
            foreach (var slot in _facade.Slots)
                slot.Changed += OnSlotChanged;
        }

        _events = connection.Subscribe(test, handle, OnEvent);
    }

    /// <summary>The host's control connection.</summary>
    public ControlConnection Connection { get; }

    /// <summary>The test scope.</summary>
    public string Test { get; }

    /// <summary>The client handle in the host.</summary>
    public string Handle { get; }

    /// <inheritdoc />
    public ClientTarget Target { get; }

    /// <inheritdoc />
    public ClientCapabilities Capabilities { get; }

    /// <inheritdoc />
    public Task ConnectAsync() => CallAsync("client.connect", null, DefaultWaitMs);

    /// <inheritdoc />
    public async Task<bool> TryConnectAsync()
        => (await CallAsync("client.tryConnect", null, DefaultWaitMs).ConfigureAwait(false))["ok"]?.GetValue<bool>() == true;

    /// <inheritdoc />
    public Task DisconnectAsync() => CallAsync("client.disconnect", null, DefaultWaitMs);

    /// <inheritdoc />
    public async Task<ConnectionState> GetStateAsync()
        => Enum.Parse<ConnectionState>((await CallAsync("client.state", null, DefaultOpMs).ConfigureAwait(false))["state"]!.GetValue<string>());

    /// <inheritdoc />
    public async Task WaitForStateAsync(ConnectionState state, double seconds)
    {
        try
        {
            await CallAsync("client.awaitState", new JsonObject { ["state"] = state.ToString() }, Milliseconds(seconds)).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{Target}: state {state} not reached within {seconds} s. {e.Message}", e);
        }
    }

    /// <inheritdoc />
    public async Task<DisconnectReason> WhenDisconnectedAsync(double seconds)
    {
        try
        {
            var result = await CallAsync("client.awaitDisconnected", null, Milliseconds(seconds)).ConfigureAwait(false);
            return Enum.Parse<DisconnectReason>(result["reason"]!.GetValue<string>());
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{Target}: not disconnected within {seconds} s. {e.Message}", e);
        }
    }

    /// <inheritdoc />
    public async Task WhenReadyAsync(double seconds)
    {
        try
        {
            await CallAsync("client.awaitReady", null, Milliseconds(seconds)).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{Target}: not ready within {seconds} s. {e.Message}", e);
        }
    }

    /// <inheritdoc />
    public TProxy Proxy<TProxy>()
        => (TProxy)_proxies.GetOrAdd(typeof(TProxy), t => NexusTypes.CreateRemoteProxy(NexusTypes.GetRemoteProxyType(t), _invoker));

    /// <inheritdoc />
    public TScript Callbacks<TScript>()
    {
        if (_facade is TScript script)
            return script;

        throw new InvalidOperationException(_facade == null
            ? $"No generated script facade for the client nexus of {Target}. Add [assembly: NexusTestScaffolding<TServer, TClient>] for its nexus pair."
            : $"The script facade of {Target} is {_facade.GetType()}, not {typeof(TScript)}.");
    }

    /// <inheritdoc />
    public async Task<IRemotePipe> CreatePipeAsync()
    {
        var result = await CallAsync("pipe.create", null, DefaultOpMs).ConfigureAwait(false);
        return new RemotePipe(this, result["pipe"]!.GetValue<string>());
    }

    /// <inheritdoc />
    public IRemoteList<T> List<T>(string collectionName)
    {
        ArgumentNullException.ThrowIfNull(collectionName);
        return (IRemoteList<T>)_lists.GetOrAdd(collectionName, name =>
        {
            var serverType = FindType(_serverInterface)
                             ?? throw new InvalidOperationException($"The server nexus interface {_serverInterface} is not loaded.");
            // The generated remote proxy holds the collection's descriptor.
            var proxy = _proxies.GetOrAdd(serverType, t => NexusTypes.CreateRemoteProxy(NexusTypes.GetRemoteProxyType(t), _invoker));
            return proxy.GetType().GetProperty(name, typeof(IRemoteList<T>))?.GetValue(proxy) as IRemoteList<T>
                   ?? throw new ArgumentException($"The server nexus has no collection '{name}' of {typeof(T)}.", nameof(collectionName));
        });
    }

    /// <inheritdoc />
    public Task SignalAsync(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return CallAsync("signal", new JsonObject { ["name"] = name }, DefaultOpMs);
    }

    /// <inheritdoc />
    public async Task BarrierAsync()
    {
        while (true)
        {
            var pending = _outstanding.Keys.ToArray();
            if (pending.Length == 0)
                break;
            await Task.WhenAll(pending.Select(t => t.ContinueWith(static _ => { }, TaskScheduler.Default))).ConfigureAwait(false);
        }

        if (Interlocked.Exchange(ref _postFailure, null) is { } failure)
            throw new InvalidOperationException($"{Target}: a queued control request failed: {failure.Message}", failure);
    }

    /// <inheritdoc />
    public Task AdvanceTimeAsync(TimeSpan by)
        => CallAsync("time.advance", new JsonObject { ["ms"] = checked((long)by.TotalMilliseconds) }, DefaultOpMs);

    /// <inheritdoc />
    public async Task<LeakReport> EndTestAuditAsync()
    {
        await BarrierAsync().ConfigureAwait(false);
        var report = await Connection.RequestAsync(Test, "test.audit", new JsonObject { ["h"] = Handle }, DefaultOpMs).ConfigureAwait(false);
        return LeakReports.Parse(report);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_facade != null)
        {
            foreach (var slot in _facade.Slots)
                slot.Changed -= OnSlotChanged;
        }

        foreach (var forward in _forwards.Values)
            forward.Cancel();

        try
        {
            // After test.end the host has released the client already.
            if (!Connection.IsClosed && Connection.HasScope(Test))
                await CallAsync("client.dispose", null, DefaultWaitMs).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HostCrashedException or ObjectDisposedException)
        {
            // The host is gone, and the client with it.
        }
        finally
        {
            _events.Dispose();
        }
    }

    /// <summary>Sends a request about this client (adds <c>h</c>).</summary>
    internal Task<JsonObject> CallAsync(string op, JsonObject? args, int timeoutMs)
    {
        args ??= new JsonObject();
        args["h"] = Handle;
        return Connection.RequestAsync(Test, op, args, timeoutMs);
    }

    /// <summary>Sends a request without waiting for it; <see cref="BarrierAsync"/> waits for it and reports its failure.</summary>
    internal Task<JsonObject> Post(string op, JsonObject args, int timeoutMs = DefaultOpMs)
    {
        var task = Connection.RequestAsync(Test, op, args, timeoutMs);
        Track(task);
        return task;
    }

    /// <summary>Tracks a request for <see cref="BarrierAsync"/>.</summary>
    internal void Track(Task task)
    {
        if (task.IsCompleted)
        {
            Observe(task);
            return;
        }

        _outstanding.TryAdd(task, 0);
        task.ContinueWith(
            t =>
            {
                Observe(t);
                _outstanding.TryRemove(t, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Creates a runner-named cancellation token in the host (control protocol §6.5) that follows <paramref name="token"/>.</summary>
    internal RemoteToken CreateToken(CancellationToken token)
    {
        var name = NewName("r");
        _ = Post("token.create", new JsonObject { ["token"] = name });
        var registration = token.Register(() => Post("token.cancel", new JsonObject { ["token"] = name }));
        return new RemoteToken(this, name, registration);
    }

    /// <summary>A runner-chosen name, unique per driver (tokens, list watches).</summary>
    internal string NewName(string prefix)
        => prefix + Interlocked.Increment(ref _nextToken).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Routes the <c>list.changed</c> events of watch <paramref name="watch"/> to <paramref name="onChanged"/>.</summary>
    internal void AddListWatch(string watch, Action<ControlEvent> onChanged) => _listWatches[watch] = onChanged;

    /// <summary>Stops routing the events of watch <paramref name="watch"/>.</summary>
    internal void RemoveListWatch(string watch) => _listWatches.TryRemove(watch, out _);

    private void Observe(Task task)
    {
        if (task.Exception is { } e)
            Interlocked.CompareExchange(ref _postFailure, e.InnerException ?? e, null);
    }

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && assembly.GetType(fullName) is { } type)
                return type;
        }

        return null;
    }

    private static int Milliseconds(double seconds) => (int)Math.Clamp(seconds * 1000, 1, int.MaxValue);

    private void OnSlotChanged(object? sender, EventArgs e)
    {
        if (sender is not ScriptSlot slot || Volatile.Read(ref _disposed) != 0)
            return;

        var steps = slot.Steps;
        var times = slot.TimesLimit;
        var args = new JsonObject { ["h"] = Handle, ["slot"] = slot.Name };
        if (steps.Count == 0 && times == null)
        {
            _ = Post("script.clear", args);
            return;
        }

        args["steps"] = new JsonArray(steps.Select(s => (JsonNode?)ControlJson.ToJson(s)).ToArray());
        if (times != null)
            args["times"] = times.Value;
        _ = Post("script.set", args);
    }

    private void OnEvent(ControlEvent evt)
    {
        switch (evt.Name)
        {
            case "script.call":
                FindSlot(evt)?.ReportCall(evt.Data["args"] as JsonArray ?? new JsonArray());
                break;
            case "script.forward":
                Forward(evt);
                break;
            case "list.changed":
                if (evt.Data["watch"]?.GetValue<string>() is { } watch && _listWatches.TryGetValue(watch, out var onChanged))
                    onChanged(evt);
                break;
            case "script.forwardCancel":
                if (evt.Data["callId"]?.ToJsonString() is { } id && _forwards.TryGetValue(id, out var source))
                    source.Cancel();
                break;
        }
    }

    private ScriptSlot? FindSlot(ControlEvent evt)
    {
        var name = evt.Data["slot"]?.GetValue<string>();
        return name == null ? null : _facade?.Find(name);
    }

    private void Forward(ControlEvent evt)
    {
        var callId = evt.Data["callId"]?.DeepClone();
        var key = callId?.ToJsonString() ?? "";
        var slot = FindSlot(evt);
        var arguments = (evt.Data["args"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();
        var source = new CancellationTokenSource();
        _forwards[key] = source;
        _ = Task.Run(async () =>
        {
            var reply = new JsonObject { ["h"] = Handle, ["callId"] = callId };
            try
            {
                if (slot == null)
                    throw new InvalidOperationException($"No script slot '{evt.Data["slot"]}' to forward to.");
                reply["result"] = await slot.ForwardAsync(arguments, source.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                var info = ErrorKinds.FromException(e);
                reply["error"] = ControlErrors.Create(info.Kind.ToString(), info.Message);
            }
            finally
            {
                _forwards.TryRemove(key, out _);
                source.Dispose();
            }

            if (!Connection.IsClosed)
                _ = Post("callback.reply", reply);
        });
    }
}

/// <summary>A runner-named cancellation token in a host; disposing it stops following the local token and releases it.</summary>
internal sealed class RemoteToken(RemoteClientDriver driver, string name, CancellationTokenRegistration registration) : IDisposable
{
    public string Name { get; } = name;

    public void Dispose()
    {
        registration.Dispose();
        if (!driver.Connection.IsClosed)
            _ = driver.Post("token.dispose", new JsonObject { ["token"] = Name });
    }
}
