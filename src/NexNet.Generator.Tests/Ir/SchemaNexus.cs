using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Pipes;

namespace NexNet.Generator.Tests.Ir.SchemaNexus;

// Compiled by the real generator with NexNetEmitInspectorSchema on (see the project file), so the tests can reach the
// emitted IInspectorSchemaSource members through a generic constraint.

/// <summary>Server API of the in-project inspector schema nexus.</summary>
public interface ISchemaServerNexus
{
    ValueTask<int> Add(int a, int b);

    ValueTask Upload(INexusDuplexPipe pipe);

    ValueTask Lines(INexusDuplexChannel<string> channel);

    [NexusCollection(NexusCollectionMode.ServerToClient)]
    INexusList<string> Names { get; }
}

/// <summary>Client API of the in-project inspector schema nexus.</summary>
public interface ISchemaClientNexus
{
    void Notify(string message);
}

[Nexus<ISchemaServerNexus, ISchemaClientNexus>(NexusType = NexusType.Server)]
public partial class SchemaServerNexus
{
    public ValueTask<int> Add(int a, int b) => new(a + b);

    public ValueTask Upload(INexusDuplexPipe pipe) => default;

    public ValueTask Lines(INexusDuplexChannel<string> channel) => default;
}

[Nexus<ISchemaClientNexus, ISchemaServerNexus>(NexusType = NexusType.Client)]
public partial class SchemaClientNexus
{
    public void Notify(string message)
    {
    }
}
