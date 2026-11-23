using NexNet.Collections;
using NexNet.Diagnostics;
using NexNet.Invocation;
using NexNet.Transports;

namespace NexNet.Inspector.Tap;

/// <summary>Opt-in helpers that install a <see cref="NexusInspector"/> on a server or client configuration.</summary>
public static class InspectorConfigExtensions
{
    /// <summary>Taps every session of the server, without a schema.</summary>
    public static NexusInspector UseInspector(this ServerConfig config, InspectorOptions? options = null) =>
        Install(config, NexusInspector.Create(options));

    /// <summary>Taps every session of the server and serves <typeparamref name="TNexus"/>'s schema.</summary>
    public static NexusInspector UseInspector<TNexus>(this ServerConfig config, InspectorOptions? options = null)
        where TNexus : IInspectorSchemaSource =>
        Install(config, NexusInspector.Create<TNexus>(options));

    /// <summary>Taps the client's connection, without a schema.</summary>
    public static NexusInspector UseInspector(this ClientConfig config, InspectorOptions? options = null) =>
        Install(config, NexusInspector.Create(options));

    /// <summary>Taps the client's connection and serves <typeparamref name="TNexus"/>'s schema.</summary>
    public static NexusInspector UseInspector<TNexus>(this ClientConfig config, InspectorOptions? options = null)
        where TNexus : IInspectorSchemaSource =>
        Install(config, NexusInspector.Create<TNexus>(options));

    /// <summary>Takes attach snapshots from <paramref name="server"/>.</summary>
    public static NexusInspector UseSnapshotsOf<TServerNexus, TClientProxy>(this NexusInspector inspector,
        NexusServer<TServerNexus, TClientProxy> server)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(server);
        inspector.SnapshotProvider = id =>
        {
            foreach (var session in server.GetSnapshot().Sessions)
            {
                if (session.Id == id)
                    return session;
            }

            return null;
        };
        return inspector;
    }

    /// <summary>Takes attach snapshots from <paramref name="client"/>'s connection.</summary>
    public static NexusInspector UseSnapshotsOf<TClientNexus, TServerProxy>(this NexusInspector inspector,
        NexusClient<TClientNexus, TServerProxy> client)
        where TClientNexus : ClientNexusBase<TServerProxy>, IMethodInvoker, IInvocationMethodHash, ICollectionConfigurer
        where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(client);
        inspector.SnapshotProvider = _ => client.GetSnapshot();
        return inspector;
    }

    private static NexusInspector Install(ConfigBase config, NexusInspector inspector)
    {
        inspector.Install(config);
        return inspector;
    }
}
