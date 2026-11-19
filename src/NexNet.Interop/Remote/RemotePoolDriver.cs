using System.Globalization;
using System.Text.Json.Nodes;
using NexNet.Messages;

namespace NexNet.Interop.Remote;

/// <summary>
/// A client pool in a language host, as an <see cref="IPoolUnderTest"/>: every member is a <c>pool.*</c> request
/// (docs/internals/interop-control-protocol.md §6.11). Rentals are <see cref="RemoteClientDriver"/>s of the rental handle.
/// </summary>
public sealed class RemotePoolDriver : IPoolUnderTest
{
    private readonly string _clientInterface;
    private readonly string _serverInterface;
    private long _nextToken;
    private int _disposed;

    /// <summary>Creates the driver of pool <paramref name="handle"/>; use <see cref="RemoteClientDriverFactory"/>.</summary>
    /// <param name="connection">The host's control connection.</param>
    /// <param name="test">The test scope of the pool.</param>
    /// <param name="handle">The pool handle.</param>
    /// <param name="target">The language and transport.</param>
    /// <param name="clientInterface">Full metadata name of the client nexus interface.</param>
    /// <param name="serverInterface">Full metadata name of the server nexus interface.</param>
    public RemotePoolDriver(ControlConnection connection, string test, string handle, ClientTarget target, string clientInterface, string serverInterface)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Test = test ?? throw new ArgumentNullException(nameof(test));
        Handle = handle ?? throw new ArgumentNullException(nameof(handle));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        _clientInterface = clientInterface ?? throw new ArgumentNullException(nameof(clientInterface));
        _serverInterface = serverInterface ?? throw new ArgumentNullException(nameof(serverInterface));
        Capabilities = connection.Hello.Capabilities;
    }

    /// <summary>The host's control connection.</summary>
    public ControlConnection Connection { get; }

    /// <summary>The test scope.</summary>
    public string Test { get; }

    /// <summary>The pool handle in the host.</summary>
    public string Handle { get; }

    /// <inheritdoc />
    public ClientTarget Target { get; }

    /// <inheritdoc />
    public ClientCapabilities Capabilities { get; }

    private bool IsOpen => !Connection.IsClosed && Connection.HasScope(Test);

    /// <inheritdoc />
    public async Task<IRentedClientUnderTest> RentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var args = new JsonObject { ["pool"] = Handle };
        string? token = null;
        var registration = default(CancellationTokenRegistration);
        if (cancellationToken.CanBeCanceled)
        {
            // A runner-named token (§6.5), created before the rent so the host sees it with the request.
            token = Handle + ".r" + Interlocked.Increment(ref _nextToken).ToString(CultureInfo.InvariantCulture);
            Post("token.create", new JsonObject { ["token"] = token });
            registration = cancellationToken.Register(() => Post("token.cancel", new JsonObject { ["token"] = token }));
            args["cancel"] = new JsonObject { ["token"] = token };
        }

        JsonObject result;
        try
        {
            result = await Connection.RequestAsync(Test, "pool.rent", args, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            if (token != null && IsOpen)
                Post("token.dispose", new JsonObject { ["token"] = token });
        }

        if (result["failed"] is JsonObject failed)
            throw ConnectionFailure(failed);

        var handle = result["h"]?.GetValue<string>() ?? throw new ControlProtocolException("pool.rent returned no handle.");
        var clientId = result["client"]?.GetValue<string>() ?? throw new ControlProtocolException("pool.rent returned no client id.");
        var inner = new RemoteClientDriver(Connection, Test, handle, Target, _clientInterface, _serverInterface);
        return new RentedClient(
            inner,
            this,
            clientId,
            async () => (await inner.CallAsync("pool.ensureConnected", null, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false))["ok"]?.GetValue<bool>() == true,
            async () =>
            {
                try
                {
                    if (IsOpen)
                        await Connection.RequestAsync(Test, "pool.return", new JsonObject { ["pool"] = Handle, ["h"] = handle }, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);
                }
                catch (Exception e) when (e is HostCrashedException or ObjectDisposedException)
                {
                    // The host is gone, and the pool with it.
                }
                finally
                {
                    inner.Release();
                }
            });
    }

    /// <inheritdoc />
    public async Task<ClientPoolState> GetStateAsync()
    {
        var result = await Request("pool.state", null, RemoteClientDriver.DefaultOpMs).ConfigureAwait(false);
        return new ClientPoolState(Int(result, "maxConnections"), Int(result, "idle"), Int(result, "rented"));
    }

    /// <inheritdoc />
    public Task AdvanceTimeAsync(TimeSpan by)
        => Request("pool.advanceTime", new JsonObject { ["ms"] = checked((long)by.TotalMilliseconds) }, RemoteClientDriver.DefaultOpMs);

    /// <inheritdoc />
    public async Task<LeakReport> EndTestAuditAsync()
        => LeakReports.Parse(await Connection.RequestAsync(Test, "test.audit", new JsonObject { ["pool"] = Handle }, RemoteClientDriver.DefaultOpMs).ConfigureAwait(false));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            // After test.end the host has released the pool already.
            if (IsOpen)
                await Request("pool.dispose", null, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HostCrashedException or ObjectDisposedException)
        {
            // The host is gone, and the pool with it.
        }
    }

    /// <summary>The C# exception of a failed rent: <c>{state, reason, message, error?}</c>.</summary>
    internal static ClientPoolConnectionException ConnectionFailure(JsonObject failed)
    {
        var state = Enum.TryParse<ConnectionResult.StateValue>(failed["state"]?.GetValue<string>(), out var s) ? s : ConnectionResult.StateValue.Exception;
        var reason = Enum.TryParse<DisconnectReason>(failed["reason"]?.GetValue<string>(), out var r) ? r : DisconnectReason.None;
        var error = failed["error"] is JsonObject e ? ControlErrors.ToException(e) : null;
        var message = failed["message"]?.GetValue<string>() ?? "Failed to connect after renting.";
        return new ClientPoolConnectionException(message, new ConnectionResult(state, reason, error));
    }

    private Task<JsonObject> Request(string op, JsonObject? args, int timeoutMs)
    {
        args ??= new JsonObject();
        args["pool"] = Handle;
        return Connection.RequestAsync(Test, op, args, timeoutMs);
    }

    private void Post(string op, JsonObject args)
    {
        if (!IsOpen)
            return;
        _ = Connection.RequestAsync(Test, op, args, RemoteClientDriver.DefaultOpMs).ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static int Int(JsonObject result, string name) => result[name]?.GetValue<int>() ?? 0;
}
