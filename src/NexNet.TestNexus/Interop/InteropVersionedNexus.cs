using NexNet;
using NexNet.TestNexus.Interop;

#pragma warning disable VSTHRD200

// Version 1 of the versioned interop nexus, exported to Java and JavaScript without a server class.
[assembly: NexusClientExport<IInteropVersionedServerNexusV1, IInteropVersionedClientNexus>(Targets = NexusTargets.Java | NexusTargets.JavaScript)]

namespace NexNet.TestNexus.Interop;

/// <summary>Version 1 of the versioned interop server nexus.</summary>
[NexusVersion(Version = "interop-v1")]
public partial interface IInteropVersionedServerNexusV1
{
    /// <summary>Returns <paramref name="a"/> + <paramref name="b"/>.</summary>
    [NexusMethod(1)]
    ValueTask<int> Add(int a, int b);

    /// <summary>Returns the identity's display name; empty when it has none.</summary>
    [NexusMethod(2)]
    ValueTask<string> GetIdentity();
}

/// <summary>Version 2 of the versioned interop server nexus: version 1 plus <see cref="CreateItem"/>.</summary>
[NexusVersion(Version = "interop-v2")]
public partial interface IInteropVersionedServerNexusV2 : IInteropVersionedServerNexusV1
{
    /// <summary>Returns a new item.</summary>
    [NexusMethod(3)]
    ValueTask<InteropItem> CreateItem(int id, string? name);
}

/// <summary>Client side of the versioned interop nexus. It has no methods.</summary>
public partial interface IInteropVersionedClientNexus
{
}

/// <summary>Server of the versioned interop nexus; accepts version 1 and version 2 clients.</summary>
[Nexus<IInteropVersionedServerNexusV2, IInteropVersionedClientNexus>(NexusType = NexusType.Server, Targets = NexusTargets.Java | NexusTargets.JavaScript)]
public partial class InteropVersionedServerNexus
{
    public ValueTask<int> Add(int a, int b) => new(a + b);

    public ValueTask<string> GetIdentity() => new(Context.Identity?.DisplayName ?? "");

    public ValueTask<InteropItem> CreateItem(int id, string? name) => new(new InteropItem(id, name));
}

/// <summary>C# client of version 1 of the versioned interop nexus.</summary>
[Nexus<IInteropVersionedClientNexus, IInteropVersionedServerNexusV1>(NexusType = NexusType.Client)]
public partial class InteropVersionedClientNexusV1
{
}

/// <summary>C# client of version 2 of the versioned interop nexus.</summary>
[Nexus<IInteropVersionedClientNexus, IInteropVersionedServerNexusV2>(NexusType = NexusType.Client)]
public partial class InteropVersionedClientNexusV2
{
}
