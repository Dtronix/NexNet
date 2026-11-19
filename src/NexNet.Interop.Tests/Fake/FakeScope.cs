using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NexNet.Interop.Remote;
using NexNet.Logging;
using NexNet.TestNexus.Interop;

namespace NexNet.Interop.Tests.Fake;

/// <summary>One test scope of the fake host: its clients, pipes, tokens and futures.</summary>
internal sealed class FakeScope(FakeHost host, string test) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, FakeClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FakePipe> _pipes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<JsonNode?>> _futures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FakePool> _pools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FakeRental> _rentals = new(StringComparer.Ordinal);

    public FakeHost Host { get; } = host;

    public string Test { get; } = test;

    public CancellationToken Token(string name)
        => _tokens.TryGetValue(name, out var source) ? source.Token : throw new UnknownHandleException("Unknown token " + name + ".");

    public Task<JsonObject> DispatchAsync(string op, JsonObject args)
    {
        switch (op)
        {
            case "client.create":
                return CreateClientAsync(args);
            case "pool.create":
                return CreatePoolAsync(args);
            case "pool.ensureConnected":
                return Rental(args).DispatchAsync(op, args);
            case "token.create":
                _tokens[args["token"]!.GetValue<string>()] = new CancellationTokenSource();
                return Done();
            case "token.cancel":
                if (_tokens.TryGetValue(args["token"]!.GetValue<string>(), out var cancel))
                    cancel.Cancel();
                return Done();
            case "token.dispose":
                if (_tokens.TryRemove(args["token"]!.GetValue<string>(), out var disposed))
                    disposed.Dispose();
                return Done();
            case "future.await":
                return AwaitFutureAsync(args["future"]!.GetValue<string>());
            case "future.poll":
                return Task.FromResult(PollFuture(args["future"]!.GetValue<string>()));
        }

        if (op.StartsWith("pipe.", StringComparison.Ordinal) && op != "pipe.create" || op.StartsWith("channel.", StringComparison.Ordinal))
            return Pipe(args).DispatchAsync(op, args);

        if (op.StartsWith("pool.", StringComparison.Ordinal))
            return Pool(args).DispatchAsync(op, args);

        if (args["h"]?.GetValue<string>() is { } rentalHandle && _rentals.TryGetValue(rentalHandle, out var rental))
            return rental.DispatchAsync(op, args);

        return Client(args).DispatchAsync(op, args);
    }

    public string AddFuture(Task<JsonNode?> task)
    {
        var handle = Host.NewHandle("f");
        _futures[handle] = task;
        return handle;
    }

    public FakePipe AddPipe(FakeClient client, IRemotePipe pipe)
    {
        var fake = new FakePipe(Host.NewHandle("p"), client, pipe);
        _pipes[fake.Handle] = fake;
        return fake;
    }

    public FakePipe Pipe(JsonObject args)
    {
        var handle = args["pipe"]!.GetValue<string>();
        return _pipes.TryGetValue(handle, out var pipe) ? pipe : throw new UnknownHandleException("Unknown pipe " + handle + ".");
    }

    public void RemovePipe(FakePipe pipe) => _pipes.TryRemove(pipe.Handle, out _);

    public IEnumerable<FakePipe> PipesOf(FakeClient client) => _pipes.Values.Where(p => p.Client == client);

    public FakeRental AddRental(FakePool pool, IRentedClientUnderTest rental)
    {
        var fake = new FakeRental(this, Host.NewHandle("c"), pool, rental);
        _rentals[fake.Handle] = fake;
        return fake;
    }

    public FakeRental Rental(JsonObject args)
    {
        var handle = args["h"]?.GetValue<string>() ?? throw new ArgumentException("The op needs a client handle.");
        return _rentals.TryGetValue(handle, out var rental) ? rental : throw new UnknownHandleException("Unknown rented client " + handle + ".");
    }

    public async Task RemoveRentalAsync(FakeRental rental)
    {
        if (_rentals.TryRemove(rental.Handle, out _))
            await rental.Rental.ReturnAsync();
    }

    public async Task<JsonObject> AuditAsync(string? handle, string? pool = null)
    {
        var report = LeakReport.Clean;
        foreach (var fake in _pools.Values)
        {
            if (handle != null || pool != null && fake.Handle != pool)
                continue;
            var poolReport = await fake.Driver.EndTestAuditAsync();
            if (!poolReport.IsClean)
                report = LeakReports.Add(report, poolReport with { Details = poolReport.Details.Select(d => fake.Handle + ": " + d).ToArray() });
        }

        foreach (var rental in _rentals.Values)
        {
            if (pool != null || handle != null && rental.Handle != handle)
                continue;
            var rentalReport = await rental.AuditAsync();
            if (!rentalReport.IsClean)
                report = LeakReports.Add(report, rentalReport with { Details = rentalReport.Details.Select(d => rental.Handle + ": " + d).ToArray() });
        }

        foreach (var client in _clients.Values)
        {
            if (pool != null || handle != null && client.Handle != handle)
                continue;
            var clientReport = await client.Driver.EndTestAuditAsync();
            if (!clientReport.IsClean)
            {
                clientReport = clientReport with { Details = clientReport.Details.Select(d => client.Handle + ": " + d).ToArray() };
                report = LeakReports.Add(report, clientReport);
            }
        }

        return LeakReports.ToJson(report);
    }

    public async Task<JsonObject> EndAsync()
    {
        var report = await AuditAsync(null);
        await DisposeAsync();
        return report;
    }

    public async Task RemoveClientAsync(FakeClient client)
    {
        _clients.TryRemove(client.Handle, out _);
        foreach (var pipe in PipesOf(client).ToArray())
            await pipe.DisposeAsync();
        await client.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var rental in _rentals.Values.ToArray())
            await RemoveRentalAsync(rental);
        foreach (var pool in _pools.Values.ToArray())
        {
            _pools.TryRemove(pool.Handle, out _);
            await pool.DisposeAsync();
        }

        foreach (var client in _clients.Values.ToArray())
            await RemoveClientAsync(client);
        foreach (var token in _tokens.Values)
            token.Dispose();
        _tokens.Clear();
        _futures.Clear();
    }

    private async Task<JsonObject> CreateClientAsync(JsonObject args)
    {
        var nexus = args["nexus"]!.AsObject();
        if (nexus["client"]?.GetValue<string>() != typeof(IInteropClientNexus).FullName)
            throw new NotSupportedException("The fake host only has the interop nexus pair.");

        var spec = ParseSpec(args["spec"]!.AsObject());
        var target = ClientTarget.CSharp(spec.Transport!.Value);
        var driver = await Harness.CreateClientAsync(target, spec);
        var client = new FakeClient(this, Host.NewHandle("c"), (InProc.InProcClientDriver<InteropClientNexus, InteropClientNexus.ServerProxy>)driver);
        _clients[client.Handle] = client;
        return new JsonObject { ["h"] = client.Handle };
    }

    private async Task<JsonObject> CreatePoolAsync(JsonObject args)
    {
        var nexus = args["nexus"]!.AsObject();
        if (nexus["client"]?.GetValue<string>() != typeof(IInteropClientNexus).FullName)
            throw new NotSupportedException("The fake host only has the interop nexus pair.");

        var spec = ParseSpec(args["spec"]!.AsObject());
        var options = new ClientPoolSpec();
        if (args["options"] is JsonObject json)
        {
            foreach (var (name, value) in json)
            {
                switch (name)
                {
                    case "maxConnections":
                        options.MaxConnections = value!.GetValue<int>();
                        break;
                    case "maxIdleTimeMs":
                        options.MaxIdleTimeMs = value!.GetValue<int>();
                        break;
                    case "minIdleConnections":
                        options.MinIdleConnections = value!.GetValue<int>();
                        break;
                    default:
                        throw new NotSupportedException("Unknown pool option '" + name + "'.");
                }
            }
        }

        var driver = await Harness.CreatePoolAsync(ClientTarget.CSharp(spec.Transport!.Value), spec, options);
        var pool = new FakePool(this, Host.NewHandle("pool"), driver);
        _pools[pool.Handle] = pool;
        return new JsonObject { ["pool"] = pool.Handle };
    }

    private FakePool Pool(JsonObject args)
    {
        var handle = args["pool"]?.GetValue<string>() ?? throw new ArgumentException("The op needs a pool handle.");
        return _pools.TryGetValue(handle, out var pool) ? pool : throw new UnknownHandleException("Unknown pool " + handle + ".");
    }

    private FakeClient Client(JsonObject args)
    {
        var handle = args["h"]?.GetValue<string>() ?? throw new ArgumentException("The op needs a client handle.");
        return _clients.TryGetValue(handle, out var client) ? client : throw new UnknownHandleException("Unknown client " + handle + ".");
    }

    private async Task<JsonObject> AwaitFutureAsync(string handle)
    {
        if (!_futures.TryRemove(handle, out var future))
            throw new UnknownHandleException("Unknown future " + handle + ".");
        return new JsonObject { ["result"] = await future };
    }

    private JsonObject PollFuture(string handle)
    {
        if (!_futures.TryGetValue(handle, out var future))
            throw new UnknownHandleException("Unknown future " + handle + ".");
        if (!future.IsCompleted)
            return new JsonObject { ["done"] = false };

        _futures.TryRemove(handle, out _);
        return future.IsCompletedSuccessfully
            ? new JsonObject { ["done"] = true, ["ok"] = true, ["result"] = future.Result }
            : new JsonObject { ["done"] = true, ["ok"] = false, ["error"] = ControlErrors.FromException(future.Exception?.InnerException ?? new TaskCanceledException()) };
    }

    private static Task<JsonObject> Done() => Task.FromResult(new JsonObject());

    private static ClientSpec ParseSpec(JsonObject json)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "transport", "address", "tlsServerName", "caPem", "clientCertificatePem", "clientKeyPem", "pinSha256", "insecureSkipVerify",
            "connectionTimeoutMs", "timeoutMs", "handshakeTimeoutMs", "pingIntervalMs", "reconnectDelaysMs", "reconnectContinuously",
            "authToken", "authorizationHeader", "maxMessageSize", "invocationCredits", "fakeTime", "logLevel",
        };
        foreach (var (name, _) in json)
        {
            if (!known.Contains(name))
                throw new NotSupportedException("Unknown ClientSpec member '" + name + "'.");
        }

        return new ClientSpec
        {
            Transport = Enum.Parse<ClientTransport>(json["transport"]!.GetValue<string>()),
            Address = json["address"]?.GetValue<string>() ?? "",
            TlsServerName = json["tlsServerName"]?.GetValue<string>(),
            CaPem = json["caPem"]?.GetValue<string>(),
            ClientCertificatePem = json["clientCertificatePem"]?.GetValue<string>(),
            ClientKeyPem = json["clientKeyPem"]?.GetValue<string>(),
            PinSha256 = json["pinSha256"]?.GetValue<string>(),
            InsecureSkipVerify = json["insecureSkipVerify"]?.GetValue<bool>() ?? false,
            ConnectionTimeoutMs = json["connectionTimeoutMs"]?.GetValue<int>(),
            TimeoutMs = json["timeoutMs"]?.GetValue<int>(),
            HandshakeTimeoutMs = json["handshakeTimeoutMs"]?.GetValue<int>(),
            PingIntervalMs = json["pingIntervalMs"]?.GetValue<int>(),
            ReconnectDelaysMs = json["reconnectDelaysMs"] is JsonArray delays ? delays.Select(d => d!.GetValue<int>()).ToArray() : null,
            ReconnectContinuously = json["reconnectContinuously"]?.GetValue<bool>() ?? false,
            AuthToken = json["authToken"]?.GetValue<string>(),
            AuthorizationHeader = json["authorizationHeader"]?.GetValue<string>(),
            MaxMessageSize = json["maxMessageSize"]?.GetValue<int>(),
            InvocationCredits = json["invocationCredits"]?.GetValue<int>(),
            FakeTime = json["fakeTime"]?.GetValue<bool>() ?? false,
            LogLevel = json["logLevel"] is { } level ? Enum.Parse<NexusLogLevel>(level.GetValue<string>()) : null,
        };
    }
}
