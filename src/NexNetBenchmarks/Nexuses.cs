using System.Threading.Tasks;
using NexNet;
using NexNet.Pipes;

namespace NexNetBenchmarks;

interface IClientNexus
{

}

interface IServerNexus
{
    ValueTask InvocationNoArgument();
    ValueTask InvocationUnmanagedArgument(int argument);
    ValueTask InvocationUnmanagedMultipleArguments(int argument1, long argument2, ushort argument3, ulong argument4, double argument5);
    ValueTask<int> InvocationNoArgumentWithResult();

    ValueTask InvocationWithDuplexPipe_Upload(INexusDuplexPipe duplexPipe);

    ValueTask InvocationPocoArgument(BenchPerson person);
    ValueTask<BenchOrder> InvocationNestedRoundTrip(BenchOrder order);

    ValueTask ChannelPersons(INexusDuplexChannel<BenchPerson> channel);
    ValueTask ChannelIntArrays(INexusDuplexChannel<int[]> channel);
    ValueTask ChannelInts(INexusDuplexChannel<int> channel);
}

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus
{

}

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public ValueTask InvocationNoArgument()
    {
        // Do Work.
        return ValueTask.CompletedTask;
    }

    public ValueTask InvocationUnmanagedArgument(int argument)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InvocationUnmanagedMultipleArguments(
        int argument1, 
        long argument2, 
        ushort argument3,
        ulong argument4,
        double argument5)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> InvocationNoArgumentWithResult()
    {
        return new ValueTask<int>(12345);
    }

    public ValueTask InvocationPocoArgument(BenchPerson person) => ValueTask.CompletedTask;

    public ValueTask<BenchOrder> InvocationNestedRoundTrip(BenchOrder order) => new(order);

    /// <summary>
    /// Completed by the server once a channel has been fully read; awaited by channel benchmarks.
    /// </summary>
    public static System.Threading.Tasks.TaskCompletionSource<int> ChannelDone = new();

    public async ValueTask ChannelPersons(INexusDuplexChannel<BenchPerson> channel)
    {
        var count = 0;
        await foreach (var _ in await channel.GetReaderAsync())
            count++;
        ChannelDone.TrySetResult(count);
    }

    public async ValueTask ChannelIntArrays(INexusDuplexChannel<int[]> channel)
    {
        var count = 0;
        await foreach (var _ in await channel.GetReaderAsync())
            count++;
        ChannelDone.TrySetResult(count);
    }

    public async ValueTask ChannelInts(INexusDuplexChannel<int> channel)
    {
        var count = 0;
        await foreach (var _ in await channel.GetReaderAsync())
            count++;
        ChannelDone.TrySetResult(count);
    }

    public async ValueTask InvocationWithDuplexPipe_Upload(INexusDuplexPipe duplexPipe)
    {
        var reader = duplexPipe.Input;
        while (true)
        {
            var result = await reader.ReadAsync();

            if(result.IsCompleted || result.IsCanceled)
                break;

            reader.AdvanceTo(result.Buffer.End);
        }
    }
}
