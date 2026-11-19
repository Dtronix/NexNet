using System;
using System.Collections.Generic;

namespace NexNet.Interop;

/// <summary>How a nexus method completes.</summary>
public enum RemoteReturnKind : byte
{
    /// <summary>Fire and forget (<c>void</c>).</summary>
    Void,

    /// <summary><c>ValueTask</c>.</summary>
    Task,

    /// <summary><c>ValueTask&lt;T&gt;</c>.</summary>
    TaskOf,
}

/// <summary>The pipe or channel a nexus method takes.</summary>
public enum RemotePipeKind : byte
{
    /// <summary>No pipe.</summary>
    None,

    /// <summary>An <c>INexusDuplexPipe</c>.</summary>
    DuplexPipe,

    /// <summary>An <c>INexusDuplexChannel&lt;T&gt;</c>.</summary>
    Channel,
}

/// <summary>The synchronization mode of a nexus collection.</summary>
public enum RemoteCollectionMode : byte
{
    /// <summary>Not set.</summary>
    Unset,

    /// <summary>Only the server changes the collection.</summary>
    ServerToClient,

    /// <summary>Both sides change the collection.</summary>
    BiDirectional,

    /// <summary>The server relays a collection of another server.</summary>
    Relay,
}

/// <summary>Identifies one nexus method for a remote invocation or a script slot. Generated; immutable.</summary>
public sealed class RemoteMethod
{
    /// <summary>Creates a descriptor.</summary>
    /// <param name="interfaceName">Full metadata name of the declaring nexus interface (with everything it inherits).</param>
    /// <param name="name">Method name.</param>
    /// <param name="id">Method id on the wire.</param>
    /// <param name="returnKind">How the method completes.</param>
    /// <param name="pipeKind">The pipe or channel the method takes.</param>
    /// <param name="hasCancellation">True when the method takes a cancellation token.</param>
    /// <param name="parameterNames">Names of the serialized parameters, in wire order.</param>
    public RemoteMethod(
        string interfaceName,
        string name,
        ushort id,
        RemoteReturnKind returnKind,
        RemotePipeKind pipeKind,
        bool hasCancellation,
        IReadOnlyList<string> parameterNames)
    {
        InterfaceName = interfaceName ?? throw new ArgumentNullException(nameof(interfaceName));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Id = id;
        ReturnKind = returnKind;
        PipeKind = pipeKind;
        HasCancellation = hasCancellation;
        ParameterNames = parameterNames ?? throw new ArgumentNullException(nameof(parameterNames));
    }

    /// <summary>Full metadata name of the nexus interface, e.g. <c>Ns.IServerNexus</c>.</summary>
    public string InterfaceName { get; }

    /// <summary>Method name.</summary>
    public string Name { get; }

    /// <summary>Method id on the wire.</summary>
    public ushort Id { get; }

    /// <summary>How the method completes.</summary>
    public RemoteReturnKind ReturnKind { get; }

    /// <summary>The pipe or channel the method takes.</summary>
    public RemotePipeKind PipeKind { get; }

    /// <summary>True when the method takes a cancellation token.</summary>
    public bool HasCancellation { get; }

    /// <summary>Names of the serialized parameters, in wire order (the order of the typed-JSON argument array).</summary>
    public IReadOnlyList<string> ParameterNames { get; }

    /// <inheritdoc />
    public override string ToString() => InterfaceName + "." + Name;
}

/// <summary>Identifies one synchronized collection of a server nexus. Generated; immutable.</summary>
public sealed class RemoteCollection
{
    /// <summary>Creates a descriptor.</summary>
    /// <param name="interfaceName">Full metadata name of the server nexus interface.</param>
    /// <param name="name">Collection (property) name.</param>
    /// <param name="id">Collection id on the wire.</param>
    /// <param name="mode">Synchronization mode.</param>
    public RemoteCollection(string interfaceName, string name, ushort id, RemoteCollectionMode mode)
    {
        InterfaceName = interfaceName ?? throw new ArgumentNullException(nameof(interfaceName));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Id = id;
        Mode = mode;
    }

    /// <summary>Full metadata name of the server nexus interface.</summary>
    public string InterfaceName { get; }

    /// <summary>Collection (property) name.</summary>
    public string Name { get; }

    /// <summary>Collection id on the wire.</summary>
    public ushort Id { get; }

    /// <summary>Synchronization mode.</summary>
    public RemoteCollectionMode Mode { get; }

    /// <inheritdoc />
    public override string ToString() => InterfaceName + "." + Name;
}
