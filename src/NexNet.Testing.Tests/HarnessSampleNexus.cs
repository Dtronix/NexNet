using System.Threading.Tasks;
using NexNet;
using NexNet.Invocation;

namespace NexNet.Testing.Tests;

[Nexus<IDemoServerNexus, IDemoClientNexus>(NexusType = NexusType.Server)]
internal partial class DemoServerNexus : ServerNexusBase<DemoServerNexus.ClientProxy>, IDemoServerNexus
{
    public int PingCount;

    public ValueTask<int> Ping(int value)
    {
        Interlocked.Increment(ref PingCount);
        return ValueTask.FromResult(value);
    }

    public ValueTask Notify(string message)
    {
        return ValueTask.CompletedTask;
    }
}

[Nexus<IDemoClientNexus, IDemoServerNexus>(NexusType = NexusType.Client)]
internal partial class DemoClientNexus : ClientNexusBase<DemoClientNexus.ServerProxy>, IDemoClientNexus
{
    public string? LastMessage;

    public ValueTask ReceiveBroadcast(string message)
    {
        LastMessage = message;
        return ValueTask.CompletedTask;
    }
}

internal partial interface IDemoServerNexus
{
    ValueTask<int> Ping(int value);
    ValueTask Notify(string message);
}

internal partial interface IDemoClientNexus
{
    ValueTask ReceiveBroadcast(string message);
}
