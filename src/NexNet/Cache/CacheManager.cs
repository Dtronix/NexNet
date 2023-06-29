using NexNet.Internals;
using NexNet.Messages;

namespace NexNet.Cache;

internal class CacheManager
{
    public readonly CachedDeserializer<InvocationProxyResultMessage> InvocationProxyResultDeserializer = new();
    public readonly CachedDeserializer<InvocationCancellationRequestMessage> InvocationCancellationRequestDeserializer = new();
    public readonly CachedDeserializer<InvocationRequestMessage> InvocationRequestDeserializer = new();
    public readonly CachedDeserializer<ClientGreetingMessage> ClientGreetingDeserializer = new();
    public readonly CachedDeserializer<ServerGreetingMessage> ServerGreetingDeserializer = new();
    public readonly CachedResettableItem<RegisteredInvocationState> RegisteredInvocationStateCache = new();
    public readonly CachedCts CancellationTokenSourceCache = new();
    public readonly CachedPipe NexNetPipeCache = new();

    public virtual void Clear()
    {
        InvocationProxyResultDeserializer.Clear();
        InvocationCancellationRequestDeserializer.Clear();
        InvocationRequestDeserializer.Clear();
        ClientGreetingDeserializer.Clear();
        ServerGreetingDeserializer.Clear();

        RegisteredInvocationStateCache.Clear();
        CancellationTokenSourceCache.Clear();
        NexNetPipeCache.Clear();
    }
}
