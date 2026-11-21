using System.Text.Json.Nodes;
using NexNet.Collections;

namespace NexNet.Interop.Remote;

/// <summary>A synchronized collection of a client in a language host (control protocol §6.10).</summary>
internal sealed class RemoteList<T>(RemoteClientDriver driver, RemoteCollection collection, TypedCodec<T> codec) : IRemoteList<T>
{
    public RemoteCollection Collection { get; } = collection;

    public async ValueTask<bool> EnableAsync(RemoteCancel? cancel = null)
    {
        var args = new JsonObject { ["collection"] = ControlJson.ToJson(Collection) };
        var cancelJson = RemoteInvoker.Cancel(driver, cancel ?? RemoteCancel.None, out var token);
        if (cancelJson != null)
            args["cancel"] = cancelJson;
        try
        {
            var result = await driver.CallAsync("list.enable", args, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);
            return result["ok"]?.GetValue<bool>() == true;
        }
        finally
        {
            token?.Dispose();
        }
    }

    public async ValueTask DisableAsync()
        => await driver.CallAsync("list.disable", new JsonObject { ["collection"] = ControlJson.ToJson(Collection) }, RemoteClientDriver.DefaultWaitMs)
            .ConfigureAwait(false);

    public ValueTask<bool> AddAsync(T item) => Op("add", item: item);

    public ValueTask<bool> InsertAsync(int index, T item) => Op("insert", item: item, index: index);

    public ValueTask<bool> RemoveAsync(T item) => Op("remove", item: item);

    public ValueTask<bool> RemoveAtAsync(int index) => Op("removeAt", index: index);

    public ValueTask<bool> ReplaceAsync(int index, T item) => Op("replace", item: item, index: index);

    public ValueTask<bool> MoveAsync(int fromIndex, int toIndex) => Op("move", from: fromIndex, to: toIndex);

    public ValueTask<bool> ClearAsync() => Op("clear");

    public async ValueTask<IReadOnlyList<T>> SnapshotAsync()
    {
        var result = await driver.CallAsync("list.snapshot", new JsonObject { ["collection"] = ControlJson.ToJson(Collection) }, RemoteClientDriver.DefaultOpMs)
            .ConfigureAwait(false);
        return (result["items"] as JsonArray ?? new JsonArray()).Select(n => codec.Decode(n)!).ToArray();
    }

    public async ValueTask<RemoteListState> GetStateAsync()
    {
        var result = await driver.CallAsync("list.state", new JsonObject { ["collection"] = ControlJson.ToJson(Collection) }, RemoteClientDriver.DefaultOpMs)
            .ConfigureAwait(false);
        return new RemoteListState(
            Enum.Parse<RemoteCollectionMode>(result["mode"]!.GetValue<string>()),
            Enum.Parse<NexusCollectionState>(result["state"]!.GetValue<string>()),
            result["disabled"]!.GetValue<bool>(),
            result["readOnly"]?.GetValue<bool>());
    }

    public ValueTask<IRemoteListWatch<T>> WatchAsync() => WatchAsync(throws: false);

    /// <summary>
    /// Starts a watch (<c>list.watch</c>); with <paramref name="throws"/>, its listener throws a native exception after
    /// sending each change (<see cref="RemoteListExtensions.WatchThrowingAsync{T}"/>).
    /// </summary>
    public async ValueTask<IRemoteListWatch<T>> WatchAsync(bool throws)
    {
        // The runner names the watch and routes its events before asking, so no event can arrive unrouted.
        var name = driver.NewName("w");
        var watch = new RemoteListWatch<T>(Collection, async () =>
        {
            try
            {
                if (!driver.Connection.IsClosed && driver.Connection.HasScope(driver.Test))
                    await driver.CallAsync("list.unwatch", new JsonObject { ["watch"] = name }, RemoteClientDriver.DefaultOpMs).ConfigureAwait(false);
            }
            finally
            {
                driver.RemoveListWatch(name);
            }
        });
        driver.AddListWatch(name, evt => watch.Add(ControlJson.ToListChange(evt, codec)));
        try
        {
            var args = new JsonObject { ["collection"] = ControlJson.ToJson(Collection), ["watch"] = name };
            if (throws)
                args["throws"] = true;
            await driver.CallAsync("list.watch", args, RemoteClientDriver.DefaultOpMs).ConfigureAwait(false);
        }
        catch
        {
            driver.RemoveListWatch(name);
            throw;
        }

        return watch;
    }

    public async ValueTask<IReadOnlyList<T>> WaitForAsync(Func<IReadOnlyList<T>, bool> predicate, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var snapshot = await SnapshotAsync().ConfigureAwait(false);
            if (predicate(snapshot))
                return snapshot;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"The collection {Collection} did not reach the expected state within {timeout}; it has {snapshot.Count} items.");
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> Op(string op, T? item = default, int? index = null, int? from = null, int? to = null)
    {
        var args = new JsonObject { ["collection"] = ControlJson.ToJson(Collection), ["op"] = op };
        if (op is "add" or "insert" or "remove" or "replace")
            args["item"] = codec.Encode(item!);
        if (index != null)
            args["index"] = index.Value;
        if (from != null)
            args["from"] = from.Value;
        if (to != null)
            args["to"] = to.Value;
        var result = await driver.CallAsync("list.op", args, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);
        return result["ok"]?.GetValue<bool>() == true;
    }
}
