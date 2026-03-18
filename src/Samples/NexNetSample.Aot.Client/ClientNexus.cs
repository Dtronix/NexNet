using System.Threading.Tasks;
using NexNet;
using NexNet.Messages;
using NexNetSample.Aot.Shared;

namespace NexNetSample.Aot.Client;

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
public partial class ClientNexus
{
    public ValueTask ReceiveMessage(string user, string message)
    {
        Console.WriteLine($"[Client] Received: {user}: {message}");
        return ValueTask.CompletedTask;
    }

    public void OnStatusChanged(int status)
    {
        Console.WriteLine($"[Client] Status changed to: {status}");
    }

    protected override ValueTask OnConnected(bool isReconnected)
    {
        Console.WriteLine($"[Client] Connected to server (reconnect={isReconnected})");
        return default;
    }

    protected override ValueTask OnDisconnected(DisconnectReason reason)
    {
        Console.WriteLine($"[Client] Disconnected: {reason}");
        return default;
    }
}
