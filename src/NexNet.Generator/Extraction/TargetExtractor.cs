using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NexNet.Generator.Ir;
using NexNet.Generator.Models;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Extraction;

/// <summary>The opt-in of one [Nexus] class: its <c>Targets</c> and, for a server nexus, its IR export fragment.</summary>
/// <param name="Targets">The <c>NexusTargets</c> flags set on the attribute (never 0).</param>
/// <param name="IsClient">True when the class is a client nexus (NEXNET043).</param>
/// <param name="TypeName">The nexus class name, for diagnostics.</param>
/// <param name="Location">Location of the <c>Targets</c> argument, for diagnostics.</param>
/// <param name="Fragment">The export and the types it reaches; null for a client nexus or one that cannot be built.</param>
internal sealed record NexusTargetsData(int Targets, bool IsClient, string TypeName, LocationData? Location, IrFragment? Fragment);

/// <summary>What the transform of one [Nexus] class produces: the C# generation data and its opt-in, if any.</summary>
internal sealed record NexusProduct(NexusGenerationData Data, NexusTargetsData? Targets);

/// <summary>A diagnostic found while reading the assembly attributes; reported in the output phase.</summary>
internal sealed record TargetDiagnostic(string Id, string Arg0, string Arg1, LocationData? Location);

/// <summary>The assembly-level opt-in: client exports, native formatter mappings and their diagnostics.</summary>
/// <param name="Exports">The fragment of every <c>NexusClientExport</c> with targets (exports and the types they reach).</param>
/// <param name="Formatters">Valid <c>NexusTargetFormatter</c> mappings, one per (type, target bit).</param>
/// <param name="Diagnostics">NEXNET042 and NEXNET048 diagnostics.</param>
internal sealed record AssemblyTargetsData(
    IrFragment Exports,
    EquatableArray<TargetFormatterMapping> Formatters,
    EquatableArray<TargetDiagnostic> Diagnostics)
{
    public static readonly AssemblyTargetsData Empty = new(
        IrFragment.Empty, EquatableArray<TargetFormatterMapping>.Empty, EquatableArray<TargetDiagnostic>.Empty);
}

/// <summary>
/// Reads the multi-language opt-in: <c>[Nexus(Targets = …)]</c>, <c>[assembly: NexusClientExport&lt;TServer, TClient&gt;]</c>
/// and <c>[assembly: NexusTargetFormatter&lt;T&gt;]</c>, and builds the compilation's IR from them. Nothing here runs
/// beyond reading attributes unless a nexus opts in.
/// </summary>
internal static class TargetExtractor
{
    /// <summary>Every defined <c>NexusTargets</c> bit.</summary>
    public const int KnownTargets = 1 | 2;

    private const string TargetsProperty = "Targets";

    /// <summary>
    /// The opt-in of a [Nexus] class, or null when it sets no targets. A server nexus gets its export fragment
    /// (unless NEXNET042 blocks it); a client nexus gets none and is reported as NEXNET043.
    /// </summary>
    public static NexusTargetsData? ForNexusClass(GeneratorAttributeSyntaxContext context, NexusGenerationData data, CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol)
            return null;

        var attribute = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass is { Name: "NexusAttribute", TypeArguments.Length: 2 });
        if (attribute == null)
            return null;

        var targets = NamedInt(attribute, TargetsProperty);
        if (targets == 0)
            return null;

        var location = NamedArgumentLocation(attribute, TargetsProperty, cancellationToken) ?? data.IdentifierLocation;
        if (!data.NexusAttribute.IsServer)
            return new NexusTargetsData(targets, IsClient: true, data.TypeName, location, null);

        // NEXNET042 blocks the nexus: names no longer identify methods, so there is no export to build.
        if (NexusDataExtractor.HasDuplicateMethodNames(data.NexusInterface.AllMethods)
            || NexusDataExtractor.HasDuplicateMethodNames(data.ProxyInterface.AllMethods))
            return new NexusTargetsData(targets, IsClient: false, data.TypeName, location, null);

        var fragment = IrBuilder.Build(context.SemanticModel.Compilation, symbol, data, cancellationToken);
        return new NexusTargetsData(targets, IsClient: false, data.TypeName, location, WithTargets(fragment, targets));
    }

    /// <summary>Reads the assembly attributes of the opt-in. Cheap when there are none.</summary>
    public static AssemblyTargetsData ExtractAssembly(Compilation compilation, CancellationToken cancellationToken)
    {
        List<IrFragment>? exports = null;
        List<TargetFormatterMapping>? formatters = null;
        List<TargetDiagnostic>? diagnostics = null;
        ShapeBuilder? shapes = null;

        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass is not { IsGenericType: true } cls)
                continue;

            var ns = cls.ContainingNamespace?.ToDisplayString();
            if (cls.Name == "NexusClientExportAttribute" && cls.TypeArguments.Length == 2 && ns == "NexNet")
            {
                shapes ??= new ShapeBuilder(compilation);
                var fragment = ClientExport(attribute, cls, shapes, ref diagnostics, cancellationToken);
                if (fragment != null)
                    (exports ??= new List<IrFragment>()).Add(fragment);
            }
            else if (cls.Name == "NexusTargetFormatterAttribute" && cls.TypeArguments.Length == 1 && ns == "NexNet.Serialization")
            {
                TargetFormatter(attribute, cls.TypeArguments[0], formatters ??= new List<TargetFormatterMapping>(), ref diagnostics, cancellationToken);
            }
        }

        if (exports == null && formatters == null && diagnostics == null)
            return AssemblyTargetsData.Empty;

        return new AssemblyTargetsData(
            exports == null
                ? IrFragment.Empty
                : new IrFragment(
                    new EquatableArray<ExportIr>(exports.SelectMany(f => f.Exports).ToArray()),
                    new EquatableArray<TypeDeclIr>(exports.SelectMany(f => f.Types).ToArray())),
            formatters == null ? EquatableArray<TargetFormatterMapping>.Empty : new EquatableArray<TargetFormatterMapping>(formatters.ToArray()),
            diagnostics == null ? EquatableArray<TargetDiagnostic>.Empty : new EquatableArray<TargetDiagnostic>(diagnostics.ToArray()));
    }

    /// <summary>
    /// The compilation's IR: every opted-in export, every type they reach, the [NexusObject] types and the
    /// <c>[assembly: NexusSerializable&lt;T&gt;]</c> roots, with the native formatter mappings applied. Null when no
    /// export opts in, so a compilation without targets does no IR work.
    /// </summary>
    public static NexusIr? BuildIr(
        IEnumerable<NexusTargetsData> classTargets,
        AssemblyTargetsData assembly,
        IEnumerable<string> nexusObjectIds,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var fragments = classTargets
            .Select(t => t.Fragment)
            .Where(f => f != null)
            .Select(f => f!)
            .ToList();
        if (assembly.Exports.Exports.Length > 0)
            fragments.Add(assembly.Exports);

        if (fragments.Count == 0)
            return null;

        var objects = nexusObjectIds
            .Select(id => (ITypeSymbol?)compilation.GetTypeByMetadataName(id))
            .Where(t => t != null)
            .Select(t => t!)
            .ToArray();
        if (objects.Length > 0)
            fragments.Add(IrBuilder.BuildTypes(new ShapeBuilder(compilation), objects, cancellationToken));
        fragments.Add(IrBuilder.BuildAssemblyDeclared(compilation, cancellationToken));

        return IrNativeFormatters.Apply(IrBuilder.Merge(fragments), assembly.Formatters);
    }

    private static IrFragment? ClientExport(
        AttributeData attribute,
        INamedTypeSymbol cls,
        ShapeBuilder shapes,
        ref List<TargetDiagnostic>? diagnostics,
        CancellationToken cancellationToken)
    {
        var targets = NamedInt(attribute, TargetsProperty);
        if (targets == 0
            || cls.TypeArguments[0] is not INamedTypeSymbol { TypeKind: TypeKind.Interface } server
            || cls.TypeArguments[1] is not INamedTypeSymbol { TypeKind: TypeKind.Interface } client)
            return null;

        var location = AttributeLocation(attribute, cancellationToken);
        var (serverData, clientData) = NexusDataExtractor.ExtractExportInterfaces(server, client, shapes, cancellationToken);
        foreach (var data in new[] { serverData, clientData })
        {
            var duplicate = data.AllMethods
                .GroupBy(m => m.Name, StringComparer.Ordinal)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
            {
                (diagnostics ??= new List<TargetDiagnostic>()).Add(new TargetDiagnostic(
                    DiagnosticDescriptors.DuplicateMethodName.Id, data.TypeName, duplicate.Key, location));
                return null;
            }
        }

        var name = NamedString(attribute, "Name");
        if (string.IsNullOrWhiteSpace(name))
            name = DefaultExportName(server.Name);

        return IrBuilder.BuildExport(
            shapes,
            name!,
            server.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
            targets,
            server,
            serverData,
            client,
            clientData,
            IrDocs.FromSymbol(server, cancellationToken),
            location,
            cancellationToken);
    }

    private static void TargetFormatter(
        AttributeData attribute,
        ITypeSymbol type,
        List<TargetFormatterMapping> formatters,
        ref List<TargetDiagnostic>? diagnostics,
        CancellationToken cancellationToken)
    {
        if (attribute.ConstructorArguments.Length != 2
            || attribute.ConstructorArguments[0].Value is not int target)
            return;

        var formatter = attribute.ConstructorArguments[1].Value as string;
        var typeName = type.ToDisplayString();

        string? problem = null;
        if (target == 0)
            problem = "the target is NexusTargets.None";
        else if ((target & ~KnownTargets) != 0)
            problem = "the target " + target + " is not a defined NexusTargets value";
        else if (string.IsNullOrWhiteSpace(formatter))
            problem = "the native formatter name is empty";

        var clrName = SerializationBuilder.MetadataFullName(type.OriginalDefinition);
        if (problem == null)
        {
            foreach (var bit in Bits(target))
            {
                if (formatters.Any(f => f.ClrName == clrName && f.Target == bit))
                {
                    problem = "a formatter for " + TargetName(bit) + " is already declared";
                    break;
                }
            }
        }

        if (problem != null)
        {
            (diagnostics ??= new List<TargetDiagnostic>()).Add(new TargetDiagnostic(
                DiagnosticDescriptors.InvalidTargetFormatter.Id, typeName, problem, AttributeLocation(attribute, cancellationToken)));
            return;
        }

        foreach (var bit in Bits(target))
            formatters.Add(new TargetFormatterMapping(clrName, bit, formatter!.Trim()));
    }

    /// <summary>"IChatServerNexus" gives "ChatServerNexus"; a name not of the form I + capital is kept.</summary>
    internal static string DefaultExportName(string interfaceName)
        => interfaceName.Length > 1 && interfaceName[0] == 'I' && char.IsUpper(interfaceName[1])
            ? interfaceName.Substring(1)
            : interfaceName;

    private static IrFragment WithTargets(IrFragment fragment, int targets)
        => fragment with
        {
            Exports = new EquatableArray<ExportIr>(fragment.Exports.Select(e => e with { Targets = targets }).ToArray()),
        };

    private static IEnumerable<int> Bits(int flags)
    {
        for (var bit = 1; bit <= KnownTargets; bit <<= 1)
        {
            if ((flags & bit) != 0)
                yield return bit;
        }
    }

    private static string TargetName(int bit) => bit switch
    {
        1 => "Java",
        2 => "JavaScript",
        _ => bit.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static int NamedInt(AttributeData attribute, string name)
    {
        foreach (var arg in attribute.NamedArguments)
        {
            if (arg.Key == name && arg.Value.Value is int value)
                return value;
        }

        return 0;
    }

    private static string? NamedString(AttributeData attribute, string name)
    {
        foreach (var arg in attribute.NamedArguments)
        {
            if (arg.Key == name)
                return arg.Value.Value as string;
        }

        return null;
    }

    private static LocationData? AttributeLocation(AttributeData attribute, CancellationToken cancellationToken)
        => LocationData.FromSyntax(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken));

    private static LocationData? NamedArgumentLocation(AttributeData attribute, string name, CancellationToken cancellationToken)
    {
        if (attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) is not AttributeSyntax syntax)
            return null;

        var argument = syntax.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == name);
        return LocationData.FromSyntax((SyntaxNode?)argument ?? syntax);
    }
}
