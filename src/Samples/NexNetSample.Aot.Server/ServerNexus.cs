using System.Threading.Tasks;
using NexNet;
using NexNet.Messages;
using NexNetSample.Aot.Shared;

namespace NexNetSample.Aot.Server;

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
public partial class ServerNexus
{
    public void Ping()
    {
        Console.WriteLine($"[Server] Ping received from client {Context.Id}");
    }

    public async ValueTask SendMessage(string user, string message)
    {
        Console.WriteLine($"[Server] {user}: {message}");

        // Echo the message back to all connected clients.
        await Context.Clients.All.ReceiveMessage(user, message).ConfigureAwait(false);
    }

    public ValueTask<string> GetServerInfo()
    {
        return new ValueTask<string>("NexNet AOT Server v1.0");
    }

    public ValueTask<int> Add(int a, int b)
    {
        return new ValueTask<int>(a + b);
    }

    protected override ValueTask OnConnected(bool isReconnected)
    {
        Console.WriteLine($"[Server] Client {Context.Id} connected (reconnect={isReconnected})");
        return default;
    }

    protected override ValueTask OnDisconnected(DisconnectReason reason)
    {
        Console.WriteLine($"[Server] Client disconnected: {reason}");
        return default;
    }
}
