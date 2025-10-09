using System;
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
    ValueTask InvocationWithDuplexPipe_Channel(INexusDuplexPipe duplexPipe);
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

    public ValueTask InvocationWithDuplexPipe_Channel(INexusDuplexPipe duplexPipe)
    {
        return InvocationWithDuplexPipe_ChannelFunc?.Invoke(duplexPipe) ?? default;
    }

    public Func<INexusDuplexPipe, ValueTask>? InvocationWithDuplexPipe_ChannelFunc;
}
