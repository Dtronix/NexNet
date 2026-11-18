using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;
using NexNet.Generator.Models;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Targets.CSharpTest;

/// <summary>
/// What the C# test-scaffolding step produces for a compilation: the generated sources and NEXNET049 diagnostics.
/// Equatable, so an unchanged compilation re-emits nothing.
/// </summary>
internal sealed record TestScaffoldingOutput(EquatableArray<GeneratedFile> Files, EquatableArray<TargetDiagnostic> Diagnostics)
{
    public static readonly TestScaffoldingOutput Empty = new(EquatableArray<GeneratedFile>.Empty, EquatableArray<TargetDiagnostic>.Empty);
}

// The models below live only inside the transform (they are built from symbols and turned into text right away), so
// they need no value equality.

/// <summary>One scaffolded nexus interface with the C# spelling of everything the emitters write.</summary>
internal sealed class ScaffoldInterface
{
    public ScaffoldInterface(string metadataName, string fullName, string name, string ns, bool isPublic, InterfaceIr api, EquatableArray<TypeDeclIr> decls, LocationData? location)
    {
        MetadataName = metadataName;
        FullName = fullName;
        Name = name;
        Namespace = ns;
        IsPublic = isPublic;
        Api = api;
        Decls = decls;
        Location = location;
    }

    /// <summary>Full metadata name, e.g. <c>Ns.IServerNexus</c> (the IR's <c>InterfaceIr.ClrName</c>).</summary>
    public string MetadataName { get; }

    /// <summary>Fully qualified C# name, e.g. <c>global::Ns.IServerNexus</c>.</summary>
    public string FullName { get; }

    /// <summary>Simple name.</summary>
    public string Name { get; }

    /// <summary>Namespace; empty for the global namespace.</summary>
    public string Namespace { get; }

    /// <summary>True when the interface is public (the generated class is public too); otherwise internal.</summary>
    public bool IsPublic { get; }

    public InterfaceIr Api { get; }

    /// <summary>The declared types the interface pair reaches.</summary>
    public EquatableArray<TypeDeclIr> Decls { get; }

    /// <summary>The [NexusTestScaffolding] attribute that first named the interface.</summary>
    public LocationData? Location { get; }

    /// <summary>Invocable methods, by id.</summary>
    public List<ScaffoldMethod> Methods { get; } = new();

    /// <summary>Synchronized collections, by id.</summary>
    public List<ScaffoldCollection> Collections { get; } = new();

    /// <summary>Explicit implementations of the members that cannot be invoked (ignored methods, plain properties, events).</summary>
    public List<string> UnsupportedMembers { get; } = new();
}

/// <summary>One invocable method.</summary>
internal sealed class ScaffoldMethod
{
    public ScaffoldMethod(MethodIr ir, string declaringInterface, string returnType, string? resultType, List<ScaffoldParam> parameters)
    {
        Ir = ir;
        DeclaringInterface = declaringInterface;
        ReturnType = returnType;
        ResultType = resultType;
        Params = parameters;
    }

    public MethodIr Ir { get; }

    /// <summary>Fully qualified C# name of the interface that declares the method.</summary>
    public string DeclaringInterface { get; }

    /// <summary>C# return type (<c>void</c>, <c>ValueTask</c>, <c>ValueTask&lt;T&gt;</c>).</summary>
    public string ReturnType { get; }

    /// <summary>For <see cref="ReturnKind.TaskOf"/>: the C# result type.</summary>
    public string? ResultType { get; }

    public List<ScaffoldParam> Params { get; }

    /// <summary>The escaped method name.</summary>
    public string Name => CSharpNames.Escape(Ir.Name);

    /// <summary>The serialized parameters, in wire order.</summary>
    public IEnumerable<ScaffoldParam> Values => Params.Where(p => p.Ir.Kind == ParamKind.Value).OrderBy(p => p.Ir.WireIndex);

    public ScaffoldParam? Pipe => Params.FirstOrDefault(p => p.Ir.Kind is ParamKind.DuplexPipe or ParamKind.DuplexChannel);

    public ScaffoldParam? Cancellation => Params.FirstOrDefault(p => p.Ir.Kind == ParamKind.CancellationToken);
}

/// <summary>One method parameter.</summary>
internal sealed class ScaffoldParam
{
    public ScaffoldParam(ParamIr ir, string name, string type, string? defaultValue)
    {
        Ir = ir;
        Name = name;
        Type = type;
        DefaultValue = defaultValue;
    }

    public ParamIr Ir { get; }

    /// <summary>The escaped parameter name.</summary>
    public string Name { get; }

    /// <summary>The C# parameter type (for a channel parameter, its item type is in <see cref="ItemType"/>).</summary>
    public string Type { get; }

    /// <summary>For a channel parameter: the C# item type.</summary>
    public string? ItemType { get; set; }

    /// <summary>The C# default value expression, or null when the parameter has none.</summary>
    public string? DefaultValue { get; }
}

/// <summary>One synchronized collection.</summary>
internal sealed class ScaffoldCollection
{
    public ScaffoldCollection(CollectionIr ir, string declaringInterface, string propertyType, string itemType, bool hasSetter)
    {
        Ir = ir;
        DeclaringInterface = declaringInterface;
        PropertyType = propertyType;
        ItemType = itemType;
        HasSetter = hasSetter;
    }

    public CollectionIr Ir { get; }

    public string DeclaringInterface { get; }

    public string PropertyType { get; }

    public string ItemType { get; }

    public bool HasSetter { get; }

    public string Name => CSharpNames.Escape(Ir.Name);
}
