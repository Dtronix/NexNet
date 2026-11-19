using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using NexNet.Interop.InProc;
using NexNet.Interop.Remote;
using NexNet.Interop.Scripting;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.TestNexus.Interop;

namespace NexNet.Interop.Tests.Fake;

/// <summary>
/// A client of the fake host: the C# in-process driver, with the interop client nexus bound to scripts set over the
/// control protocol (its own JSON step interpreter, written from the protocol specification §6.6).
/// </summary>
internal sealed class FakeClient : IAsyncDisposable
{
    private sealed class SlotScript
    {
        public JsonArray Steps = new();
        public int? Times;
        public int Calls;
    }

    private readonly ConcurrentDictionary<string, SlotScript> _scripts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _forwards = new();
    private readonly ConcurrentDictionary<string, IRemoteListWatch<InteropItem>> _watches = new(StringComparer.Ordinal);
    private readonly ScriptSignals _signals = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ConnectionState _lastState = ConnectionState.Disconnected;

    public FakeClient(FakeScope scope, string handle, InProcClientDriver<InteropClientNexus, InteropClientNexus.ServerProxy> driver)
    {
        Scope = scope;
        Handle = handle;
        Driver = driver;
        Invoker = ((IRemoteProxy)driver.Proxy<Remote_IInteropServerNexus>()).Invoker;
        Bind(driver.Nexus);
        driver.Client.StateChanged += (_, state) =>
        {
            var previous = _lastState;
            _lastState = state;
            Scope.Host.SendEvent(Scope.Test, Handle, "client.state", new JsonObject { ["state"] = state.ToString() });
            if (state == ConnectionState.Connected)
                _ = RunDetached("OnConnected", new JsonArray(previous == ConnectionState.Reconnecting));
            else if (state == ConnectionState.Disconnected && previous != ConnectionState.Disconnected)
                _ = RunDetached("OnDisconnected", new JsonArray());
        };
    }

    public FakeScope Scope { get; }

    public string Handle { get; }

    public InProcClientDriver<InteropClientNexus, InteropClientNexus.ServerProxy> Driver { get; }

    public IRemoteInvoker Invoker { get; }

    public Task<JsonObject> DispatchAsync(string op, JsonObject args)
    {
        switch (op)
        {
            case "client.connect":
                return Done(Driver.ConnectAsync());
            case "client.tryConnect":
                return Result(Driver.TryConnectAsync(), ok => new JsonObject { ["ok"] = ok });
            case "client.disconnect":
                return Done(Driver.DisconnectAsync());
            case "client.dispose":
                return Done(Scope.RemoveClientAsync(this));
            case "client.state":
                return Result(Driver.GetStateAsync(), s => new JsonObject { ["state"] = s.ToString() });
            case "client.awaitState":
                return Done(Driver.WaitForStateAsync(Enum.Parse<ConnectionState>(args["state"]!.GetValue<string>()), 3600));
            case "client.awaitReady":
                return Done(Driver.WhenReadyAsync(3600));
            case "client.awaitDisconnected":
                return Result(Driver.WhenDisconnectedAsync(3600), r => new JsonObject { ["reason"] = r.ToString() });
            case "time.advance":
                return Done(Driver.AdvanceTimeAsync(TimeSpan.FromMilliseconds(args["ms"]!.GetValue<long>())));
            case "invoke":
                return InvokeAsync(Scope, Invoker, args);
            case "script.set":
                _scripts[args["slot"]!.GetValue<string>()] = new SlotScript
                {
                    Steps = (JsonArray)args["steps"]!.DeepClone(),
                    Times = args["times"]?.GetValue<int>(),
                };
                return Done(Task.CompletedTask);
            case "script.clear":
                _scripts.TryRemove(args["slot"]!.GetValue<string>(), out _);
                return Done(Task.CompletedTask);
            case "signal":
                _signals.Raise(args["name"]!.GetValue<string>());
                return Done(Task.CompletedTask);
            case "callback.reply":
                Reply(args);
                return Done(Task.CompletedTask);
            case "pipe.create":
                return CreatePipeAsync();
            case "list.enable":
                return ListEnableAsync(args);
            case "list.disable":
                return Done(Items(args).DisableAsync().AsTask());
            case "list.snapshot":
                return ListSnapshotAsync(args);
            case "list.op":
                return ListOpAsync(args);
            case "list.watch":
                return ListWatchAsync(args);
            case "list.unwatch":
                return ListUnwatchAsync(args);
            default:
                throw new ArgumentException("Unknown op '" + op + "'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        foreach (var forward in _forwards.Values)
            forward.TrySetCanceled();
        foreach (var watch in _watches.Values)
            await watch.DisposeAsync();
        _watches.Clear();
        await Driver.DisposeAsync();
        _lifetime.Dispose();
    }

    // ----- Invocations -----

    /// <summary>The <c>invoke</c> op on <paramref name="invoker"/> (shared with pool rentals).</summary>
    internal static Task<JsonObject> InvokeAsync(FakeScope scope, IRemoteInvoker invoker, JsonObject args)
    {
        var method = Resolve(args["method"]!.AsObject(), out var clr);
        var arguments = (JsonArray)args["args"]!.DeepClone();
        var pipe = args["pipe"] is { } pipeHandle ? scope.Pipe(new JsonObject { ["pipe"] = pipeHandle.GetValue<string>() }).Pipe : null;
        var cancel = args["cancel"] switch
        {
            JsonObject c when c["afterMs"] is { } after => RemoteCancel.After(after.GetValue<int>()),
            JsonObject c when c["token"] is { } token => RemoteCancel.FromToken(scope.Token(token.GetValue<string>())),
            _ => RemoteCancel.None,
        };

        // A token canceled before the call is sent: the call is not sent (D17).
        if (cancel.Kind == RemoteCancelKind.Token && cancel.Token.IsCancellationRequested)
            throw new TaskCanceledException("Canceled before the invocation was sent.");

        var invocation = new RemoteInvocation(method, arguments, cancel, pipe);
        Task<JsonNode?> call;
        if (clr.ReturnType == typeof(void))
        {
            invoker.Invoke(invocation);
            call = Task.FromResult<JsonNode?>(null);
        }
        else if (clr.ReturnType == typeof(ValueTask))
        {
            call = invoker.InvokeAsync(invocation).AsTask().ContinueWith(t =>
            {
                t.GetAwaiter().GetResult();
                return (JsonNode?)null;
            }, TaskScheduler.Default);
        }
        else
        {
            call = invoker.InvokeWithResultAsync(invocation).AsTask();
        }

        if (args["detach"]?.GetValue<bool>() == true)
            return Task.FromResult(new JsonObject { ["future"] = scope.AddFuture(call) });
        return Result(call, r => new JsonObject { ["result"] = r });
    }

    private static RemoteMethod Resolve(JsonObject method, out MethodInfo clr)
    {
        var name = method["name"]!.GetValue<string>();
        clr = typeof(IInteropServerNexus).GetMethod(name) ?? throw new ArgumentException("Unknown method " + name + ".");
        var parameters = clr.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken) && p.ParameterType != typeof(INexusDuplexPipe)
                        && !(p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(INexusDuplexChannel<>)))
            .Select(p => p.Name!)
            .ToArray();
        var returnKind = clr.ReturnType == typeof(void) ? RemoteReturnKind.Void
            : clr.ReturnType == typeof(ValueTask) ? RemoteReturnKind.Task
            : RemoteReturnKind.TaskOf;
        return new RemoteMethod(method["nexus"]!.GetValue<string>(), name, method["id"]!.GetValue<ushort>(), returnKind, RemotePipeKind.None, false, parameters);
    }

    // ----- Pipes and lists -----

    private async Task<JsonObject> CreatePipeAsync()
    {
        var pipe = await Driver.CreatePipeAsync();
        return new JsonObject { ["pipe"] = Scope.AddPipe(this, pipe).Handle };
    }

    private IRemoteList<InteropItem> Items(JsonObject args)
    {
        var name = args["collection"]!["name"]!.GetValue<string>();
        return name == "Items"
            ? Driver.Proxy<Remote_IInteropServerNexus>().Items
            : throw new NotSupportedException("The fake host only knows the collection Items.");
    }

    private async Task<JsonObject> ListEnableAsync(JsonObject args)
    {
        var cancel = args["cancel"]?["afterMs"] is { } after ? RemoteCancel.After(after.GetValue<int>()) : RemoteCancel.None;
        return new JsonObject { ["ok"] = await Items(args).EnableAsync(cancel) };
    }

    private async Task<JsonObject> ListSnapshotAsync(JsonObject args)
    {
        var codec = TypedCodecs.Get<InteropItem>();
        var items = await Items(args).SnapshotAsync();
        return new JsonObject { ["items"] = new JsonArray(items.Select(i => codec.Encode(i)).ToArray()) };
    }

    private async Task<JsonObject> ListWatchAsync(JsonObject args)
    {
        var name = args["watch"]!.GetValue<string>();
        var codec = TypedCodecs.Get<InteropItem>();
        var watch = (RemoteListWatch<InteropItem>)await Items(args).WatchAsync();
        if (!_watches.TryAdd(name, watch))
        {
            await watch.DisposeAsync();
            throw new ArgumentException("The watch " + name + " exists.");
        }

        // Hooked before the response, so every change raised after list.watch completes is sent.
        watch.Recorded += change => Scope.Host.SendEvent(Scope.Test, Handle, "list.changed", ControlJson.ToJson(name, change, codec));
        return new JsonObject();
    }

    private async Task<JsonObject> ListUnwatchAsync(JsonObject args)
    {
        var name = args["watch"]!.GetValue<string>();
        if (!_watches.TryRemove(name, out var watch))
            throw new UnknownHandleException("Unknown watch " + name + ".");
        await watch.DisposeAsync();
        return new JsonObject();
    }

    private async Task<JsonObject> ListOpAsync(JsonObject args)
    {
        var list = Items(args);
        var codec = TypedCodecs.Get<InteropItem>();
        InteropItem Item() => codec.Decode(args["item"])!;
        int Int(string name) => args[name]!.GetValue<int>();
        var ok = args["op"]!.GetValue<string>() switch
        {
            "add" => await list.AddAsync(Item()),
            "insert" => await list.InsertAsync(Int("index"), Item()),
            "remove" => await list.RemoveAsync(Item()),
            "removeAt" => await list.RemoveAtAsync(Int("index")),
            "replace" => await list.ReplaceAsync(Int("index"), Item()),
            "move" => await list.MoveAsync(Int("from"), Int("to")),
            "clear" => await list.ClearAsync(),
            var other => throw new ArgumentException("Unknown list op " + other + "."),
        };
        return new JsonObject { ["ok"] = ok };
    }

    // ----- Scripts -----

    private void Bind(InteropClientNexus nexus)
    {
        nexus.ClientNotifyHandler = message => _ = RunDetached("ClientNotify", new JsonArray(Encode(message)));
        nexus.ClientPingHandler = async () => await RunAsync("ClientPing", new JsonArray(), CancellationToken.None, null);
        nexus.ClientEchoStringHandler = async value => Decode<string?>(await RunAsync("ClientEchoString", new JsonArray(Encode(value)), CancellationToken.None, null));
        nexus.ClientEchoAllTypesHandler = async value => Decode<InteropAllTypes?>(await RunAsync("ClientEchoAllTypes", new JsonArray(Encode(value)), CancellationToken.None, null));
        nexus.ClientEchoShapeHandler = async value => Decode<InteropShape?>(await RunAsync("ClientEchoShape", new JsonArray(Encode(value)), CancellationToken.None, null));
        nexus.ClientDelayHandler = async (ms, value, ct) => Decode<int>(await RunAsync("ClientDelay", new JsonArray(Encode(ms), Encode(value)), ct, null));
        nexus.ClientEchoPipeHandler = async pipe => await RunAsync("ClientEchoPipe", new JsonArray(), CancellationToken.None, pipe);
    }

    private static JsonNode? Encode<T>(T value) => TypedCodecs.Get<T>().Encode(value);

    private static T Decode<T>(JsonNode? node) => node == null ? default! : TypedCodecs.Get<T>().Decode(node)!;

    private async Task RunDetached(string slot, JsonArray args)
    {
        try
        {
            await RunAsync(slot, args, _lifetime.Token, null);
        }
        catch (Exception)
        {
            // Fire-and-forget slots report nothing.
        }
    }

    private async Task<JsonNode?> RunAsync(string slot, JsonArray args, CancellationToken token, INexusDuplexPipe? pipe)
    {
        if (!token.CanBeCanceled)
            token = _lifetime.Token;

        // 1. Report the call before any step.
        Scope.Host.SendEvent(Scope.Test, Handle, "script.call", new JsonObject { ["slot"] = slot, ["args"] = args.DeepClone() });

        // 2. Times limit, counted from the last script.set/clear.
        var steps = new JsonArray();
        if (_scripts.TryGetValue(slot, out var script))
        {
            var calls = Interlocked.Increment(ref script.Calls);
            if (script.Times == null || calls <= script.Times)
                steps = script.Steps;
        }

        // 3. Steps in order; return/forward/throw end the call.
        foreach (var node in steps.ToArray())
        {
            var step = node!.AsObject();
            switch (step["step"]!.GetValue<string>())
            {
                case "return":
                    return step["value"]?.DeepClone();
                case "throw":
                    throw ErrorKinds.ToException(Enum.Parse<ErrorKind>(step["kind"]!.GetValue<string>()), step["message"]?.GetValue<string>() ?? "");
                case "delay":
                    await Task.Delay(step["ms"]!.GetValue<int>(), token);
                    break;
                case "awaitSignal":
                    await _signals.WaitAsync(step["name"]!.GetValue<string>(), token);
                    break;
                case "awaitCancel":
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }

                    break;
                case "forward":
                    return await ForwardAsync(slot, args, token);
                case "invoke":
                    await InvokeStepAsync(step);
                    break;
                case "named":
                    await NamedAsync(step, pipe);
                    break;
                default:
                    throw new NotSupportedException("Unknown step " + step["step"] + ".");
            }
        }

        // 4. Steps ran out: the default result.
        return null;
    }

    private async Task<JsonNode?> ForwardAsync(string slot, JsonArray args, CancellationToken token)
    {
        var callId = Scope.Host.NewCallId();
        var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _forwards[callId] = reply;
        Scope.Host.SendEvent(Scope.Test, Handle, "script.forward", new JsonObject { ["callId"] = callId, ["slot"] = slot, ["args"] = args.DeepClone() });
        await using var registration = token.Register(() =>
        {
            Scope.Host.SendEvent(Scope.Test, Handle, "script.forwardCancel", new JsonObject { ["callId"] = callId });
            reply.TrySetCanceled(token);
        });
        try
        {
            return await reply.Task;
        }
        finally
        {
            _forwards.TryRemove(callId, out _);
        }
    }

    private void Reply(JsonObject args)
    {
        var callId = args["callId"]!.GetValue<long>();
        if (!_forwards.TryRemove(callId, out var reply))
            return;
        if (args["error"] is JsonObject error)
            reply.TrySetException(ControlErrors.ToException(error));
        else
            reply.TrySetResult(args["result"]?.DeepClone());
    }

    private async Task InvokeStepAsync(JsonObject step)
    {
        var method = Resolve(step["method"]!.AsObject(), out var clr);
        var invocation = new RemoteInvocation(method, (JsonArray)step["args"]!.DeepClone(), RemoteCancel.None, null);
        if (clr.ReturnType == typeof(void))
            Invoker.Invoke(invocation);
        else if (clr.ReturnType == typeof(ValueTask))
            await Invoker.InvokeAsync(invocation);
        else
            await Invoker.InvokeWithResultAsync(invocation);
    }

    private static async Task NamedAsync(JsonObject step, INexusDuplexPipe? pipe)
    {
        var handler = step["handler"]!.GetValue<string>();
        if (pipe == null)
            throw new InvalidOperationException("The handler '" + handler + "' needs a method that takes a pipe or channel.");

        switch (handler)
        {
            case "pipe.echo":
                while (true)
                {
                    var result = await pipe.Input.ReadAsync();
                    foreach (var segment in result.Buffer)
                        await pipe.Output.WriteAsync(segment);
                    pipe.Input.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted || result.IsCanceled)
                        break;
                }

                await pipe.CompleteAsync();
                break;
            case "pipe.drain":
                while (true)
                {
                    var result = await pipe.Input.ReadAsync();
                    pipe.Input.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted || result.IsCanceled)
                        break;
                }

                await pipe.CompleteAsync();
                break;
            default:
                throw new NotSupportedException("The fake host has no handler '" + handler + "'.");
        }
    }

    private static async Task<JsonObject> Done(Task task)
    {
        await task;
        return new JsonObject();
    }

    private static async Task<JsonObject> Result<T>(Task<T> task, Func<T, JsonObject> map) => map(await task);
}
