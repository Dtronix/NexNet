using System.Text.Json.Nodes;
using NexNet.Interop.Remote;
using NexNet.Messages;
using NexNet.TestNexus.Interop;

namespace NexNet.Interop.Tests.Fake;

/// <summary>A client pool of the fake host (control protocol §6.11): the C# in-process pool driver behind a pool handle.</summary>
internal sealed class FakePool(FakeScope scope, string handle, IPoolUnderTest driver) : IAsyncDisposable
{
    public string Handle { get; } = handle;

    public IPoolUnderTest Driver { get; } = driver;

    public Task<JsonObject> DispatchAsync(string op, JsonObject args)
    {
        switch (op)
        {
            case "pool.rent":
                return RentAsync(args);
            case "pool.return":
                return ReturnAsync(args);
            case "pool.state":
                return StateAsync();
            case "pool.advanceTime":
                return Done(Driver.AdvanceTimeAsync(TimeSpan.FromMilliseconds(args["ms"]!.GetValue<long>())));
            case "pool.dispose":
                return Done(Driver.DisposeAsync().AsTask());
            default:
                throw new ArgumentException("Unknown op '" + op + "'.");
        }
    }

    public ValueTask DisposeAsync() => Driver.DisposeAsync();

    private async Task<JsonObject> RentAsync(JsonObject args)
    {
        var token = args["cancel"] switch
        {
            JsonObject c when c["token"] is { } name => scope.Token(name.GetValue<string>()),
            _ => CancellationToken.None,
        };

        IRentedClientUnderTest rental;
        try
        {
            rental = await Driver.RentAsync(token);
        }
        catch (ClientPoolConnectionException e)
        {
            // A connection failure is a result (§6.11), so the runner rebuilds the ConnectionResult.
            var failed = new JsonObject
            {
                ["state"] = e.ConnectionResult.State.ToString(),
                ["reason"] = e.ConnectionResult.DisconnectReason.ToString(),
                ["message"] = e.Message,
            };
            if (e.ConnectionResult.Exception is { } inner)
                failed["error"] = ControlErrors.FromException(inner);
            return new JsonObject { ["failed"] = failed };
        }

        var fake = scope.AddRental(this, rental);
        return new JsonObject { ["h"] = fake.Handle, ["client"] = rental.ClientId };
    }

    private async Task<JsonObject> ReturnAsync(JsonObject args)
    {
        var rental = scope.Rental(args);
        if (rental.Pool != this)
            throw new UnknownHandleException("Client " + rental.Handle + " is not a rental of pool " + Handle + ".");
        await scope.RemoveRentalAsync(rental);
        return new JsonObject();
    }

    private async Task<JsonObject> StateAsync()
    {
        var state = await Driver.GetStateAsync();
        return new JsonObject { ["maxConnections"] = state.MaxConnections, ["idle"] = state.Idle, ["rented"] = state.Rented };
    }

    private static async Task<JsonObject> Done(Task task)
    {
        await task;
        return new JsonObject();
    }
}

/// <summary>A client rented from a <see cref="FakePool"/>: the client ops a rental handle accepts (§6.11).</summary>
internal sealed class FakeRental(FakeScope scope, string handle, FakePool pool, IRentedClientUnderTest rental)
{
    private readonly IRemoteInvoker _invoker = ((IRemoteProxy)rental.Proxy<Remote_IInteropServerNexus>()).Invoker;

    public string Handle { get; } = handle;

    public FakePool Pool { get; } = pool;

    public IRentedClientUnderTest Rental { get; } = rental;

    public Task<JsonObject> DispatchAsync(string op, JsonObject args)
    {
        switch (op)
        {
            case "pool.ensureConnected":
                return Result(Rental.EnsureConnectedAsync(), ok => new JsonObject { ["ok"] = ok });
            case "client.state":
                return Result(Rental.GetStateAsync(), s => new JsonObject { ["state"] = s.ToString() });
            case "client.awaitState":
                return Result(
                    Rental.WaitForStateAsync(Enum.Parse<ConnectionState>(args["state"]!.GetValue<string>()), 3600).ContinueWith(t =>
                    {
                        t.GetAwaiter().GetResult();
                        return 0;
                    }, TaskScheduler.Default),
                    _ => new JsonObject());
            case "client.awaitReady":
                return Result(Rental.WhenReadyAsync(3600).ContinueWith(t =>
                {
                    t.GetAwaiter().GetResult();
                    return 0;
                }, TaskScheduler.Default), _ => new JsonObject());
            case "client.awaitDisconnected":
                return Result(Rental.WhenDisconnectedAsync(3600), r => new JsonObject { ["reason"] = r.ToString() });
            case "invoke":
                return FakeClient.InvokeAsync(scope, _invoker, args);
            default:
                throw new NotSupportedException("A rented client has no op '" + op + "'.");
        }
    }

    public Task<LeakReport> AuditAsync() => Rental.EndTestAuditAsync();

    private static async Task<JsonObject> Result<T>(Task<T> task, Func<T, JsonObject> map) => map(await task);
}
