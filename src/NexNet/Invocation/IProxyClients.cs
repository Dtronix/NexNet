namespace NexNet.Invocation;

public interface IProxyClients<out TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    TProxy Caller { get; }
    TProxy All { get; }
    TProxy Others { get; }
    TProxy Client(long id);
    TProxy Clients(long[] ids);
    TProxy Group(string groupName);
    TProxy Groups(string[] groupName);
}
