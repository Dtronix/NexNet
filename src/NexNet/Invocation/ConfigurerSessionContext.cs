using System;

namespace NexNet.Invocation;

/// <summary>
/// This is a class that is used to configure collections.
/// </summary>
/// <typeparam name="TClientProxy">Proxy</typeparam>
internal class ConfigurerSessionContext<TClientProxy>(NexusCollectionManager collectionManager)
    : SessionContext<TClientProxy>(null!, null)
    where TClientProxy : ProxyInvocationBase, new()
{
    public NexusCollectionManager CollectionManager { get; } = collectionManager ?? throw new ArgumentNullException(nameof(collectionManager));

    public override void Reset()
    {
        // Noop as this context is only used upon starting the server.
    }
}
