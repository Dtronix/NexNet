using System.Threading.Tasks;
using NexNet;
using NexNet.Pipes;

namespace NexNetBenchmarks;

interface IClientNexus
{

}

interface IServerNexus
{
    Task InvocationNoArgument();
    Task InvocationUnmanagedArgument(int argument);
    Task InvocationUnmanagedMultipleArguments(int argument1, long argument2, ushort argument3, ulong argument4, double argument5);
    Task<int> InvocationNoArgumentWithResult();

    Task InvocationWithDuplexPipe_Upload(INexusDuplexPipe duplexPipe);
}

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus
{

}

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public Task InvocationNoArgument()
    {
        // Do Work.
        return Task.CompletedTask;
    }

    public Task InvocationUnmanagedArgument(int argument)
    {
        return Task.CompletedTask;
    }

    public Task InvocationUnmanagedMultipleArguments(
        int argument1, 
        long argument2, 
        ushort argument3,
        ulong argument4,
        double argument5)
    {
        return Task.CompletedTask;
    }

    public Task<int> InvocationNoArgumentWithResult()
    {
        return Task.FromResult(12345);
    }

    public async Task InvocationWithDuplexPipe_Upload(INexusDuplexPipe duplexPipe)
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
