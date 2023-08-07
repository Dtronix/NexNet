using NexNet;

namespace NexNetDemo.Samples.Messenger;

interface IMessengerSampleClientNexus
{
    ValueTask SendMessage(string message);
}

interface IMessengerSampleServerNexus
{
    ValueTask BroadcastMessage(string message);

    ValueTask DirectMessage(long userId, string message);
}


[Nexus<IMessengerSampleClientNexus, IMessengerSampleServerNexus>(NexusType = NexusType.Client)]
partial class MessengerSampleClientNexus
{
    public ValueTask SendMessage(string message)
    {
        Console.WriteLine(message);
        return default;
    }

    protected override ValueTask OnConnected(bool isReconnected)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            int count = 0;
            while (true)
            {
                await Context.Proxy.BroadcastMessage($"Message {count++}");
                await Task.Delay(1000);
            }
            // ReSharper disable once FunctionNeverReturns
        });

        return default;
    }
}

[Nexus<IMessengerSampleServerNexus, IMessengerSampleClientNexus>(NexusType = NexusType.Server)]
partial class MessengerSampleServerNexus
{
    public async ValueTask BroadcastMessage(string message)
    {
        message = $"Message from {Context.Id}:" + message;
        Console.WriteLine("Broadcast " + message);
        await Context.Clients.All.SendMessage(message);
    }

    public async ValueTask DirectMessage(long userId, string message)
    {
        message = $"Message from {Context.Id} to {userId}:" + message;
        Console.WriteLine("Direct "+ message);
        await Context.Clients.Client(userId).SendMessage(message);
    }
}
