using System.Text.Json.Nodes;

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
