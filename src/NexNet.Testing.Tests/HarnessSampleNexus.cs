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

    public async ValueTask JoinGroup(string groupName)
    {
        await Context.Groups.AddAsync(groupName);
    }

    public async ValueTask BroadcastToGroup(string groupName, string message)
    {
        await Context.Clients.Group(groupName).ReceiveBroadcast(message);
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
    ValueTask JoinGroup(string groupName);
    ValueTask BroadcastToGroup(string groupName, string message);
}

internal partial interface IDemoClientNexus
{
    ValueTask ReceiveBroadcast(string message);
}
