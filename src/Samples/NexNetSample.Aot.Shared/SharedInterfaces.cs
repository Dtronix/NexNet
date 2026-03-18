using System.Threading.Tasks;
using NexNet;

namespace NexNetSample.Aot.Shared;

/// <summary>
/// Server-side hub interface - methods callable by the client.
/// </summary>
public interface IServerNexus
{
    /// <summary>
    /// Simple fire-and-forget notification.
    /// </summary>
    void Ping();

    /// <summary>
    /// Awaitable method with arguments.
    /// </summary>
    ValueTask SendMessage(string user, string message);

    /// <summary>
    /// Awaitable method with return value.
    /// </summary>
    ValueTask<string> GetServerInfo();

    /// <summary>
    /// Method with multiple argument types to test ValueTuple serialization.
    /// </summary>
    ValueTask<int> Add(int a, int b);
}

/// <summary>
/// Client-side hub interface - methods callable by the server.
/// </summary>
public interface IClientNexus
{
    /// <summary>
    /// Server pushes a message to the client.
    /// </summary>
    ValueTask ReceiveMessage(string user, string message);

    /// <summary>
    /// Server notifies client of a status change.
    /// </summary>
    void OnStatusChanged(int status);
}
