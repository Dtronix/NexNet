using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NexNet.Generator.Ir;
using NexNet.Invocation;

namespace NexNet.Generator.Tests.HashParity;

/// <summary>
/// The hash surface of one compiled <see cref="IInvocationMethodHash"/> type: what the C# runtime reads from it during
/// the handshake and for every received invocation.
/// </summary>
/// <param name="MethodHash"><c>IInvocationMethodHash.MethodHash</c>.</param>
/// <param name="VersionHashTable"><c>VersionHashTable</c> in enumeration order (the order the session reads it in).</param>
/// <param name="VersionMethodHashSet"><c>VersionMethodHashSet</c>, ascending.</param>
internal sealed record HashSurface(int MethodHash, KeyValuePair<string, int>[] VersionHashTable, long[] VersionMethodHashSet);

/// <summary>A compiled nexus class: the <c>TNexus</c> and <c>TProxy</c> its session is built over.</summary>
internal sealed record CompiledNexus(string Name, bool IsServer, HashSurface Nexus, HashSurface Proxy)
{
    /// <summary>The client greeting values, derived exactly as <c>NexusSession.StartAsClient</c> does.</summary>
    public HandshakeIr ClientGreeting()
    {
        if (IsServer)
            throw new InvalidOperationException($"{Name} is a server nexus.");

        var latestVersion = Proxy.VersionHashTable.Select(p => p.Key).LastOrDefault();
        var serverHash = latestVersion == null
            ? Proxy.MethodHash
            : Proxy.VersionHashTable.First(p => p.Key == latestVersion).Value;
        return new HandshakeIr(latestVersion, serverHash, Nexus.MethodHash);
    }

    /// <summary>
    /// The verification of a client greeting by this server nexus, exactly as <c>NexusSession</c> does on receipt of
    /// the greeting. Returns the version hash the session then checks invocations against, or null on a mismatch.
    /// </summary>
    public int? AcceptGreeting(HandshakeIr greeting)
    {
        if (!IsServer)
            throw new InvalidOperationException($"{Name} is a client nexus.");

        var verificationHash = Nexus.MethodHash;
        if (Nexus.VersionHashTable.Length == 0)
        {
            if (greeting.Version != null)
                return null;
        }
        else
        {
            if (greeting.Version == null)
                return null;
            var match = Nexus.VersionHashTable.Where(p => p.Key == greeting.Version).ToArray();
            if (match.Length == 0)
                return null;
            verificationHash = match[0].Value;
        }

        return verificationHash == greeting.ServerNexusHash ? verificationHash : null;
    }
}

/// <summary>The IR and the compiled nexus classes of one fixture.</summary>
internal sealed record HashParityRun(NexusIr Ir, IReadOnlyDictionary<string, CompiledNexus> Nexuses)
{
    public ExportIr Export(string name)
        => Ir.Exports.FirstOrDefault(e => e.Name == name)
           ?? throw new InvalidOperationException($"Export '{name}' not found. Exports: {string.Join(", ", Ir.Exports.Select(e => e.Name))}.");

    public CompiledNexus Nexus(string name)
        => Nexuses.TryGetValue(name, out var nexus)
            ? nexus
            : throw new InvalidOperationException($"Nexus class '{name}' not found. Classes: {string.Join(", ", Nexuses.Keys)}.");
}

/// <summary>
/// Compiles fixture sources in memory with the real generator, stubs the nexus members the fixtures leave
/// unimplemented, emits and loads the assembly in a collectible context, and reads the runtime hash values of every
/// generated nexus and proxy through the <see cref="IInvocationMethodHash"/> accessors.
/// </summary>
internal static class HashParityRunner
{
    private static readonly ConcurrentDictionary<string, Lazy<HashParityRun>> Cache = new(StringComparer.Ordinal);

    private static readonly SymbolDisplayFormat StubFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Runs (once per <paramref name="key"/>) the fixture made of <paramref name="sources"/>.</summary>
    public static HashParityRun Run(string key, params string[] sources)
        => Cache.GetOrAdd(key, _ => new Lazy<HashParityRun>(() => Compile(key, sources))).Value;

    private static HashParityRun Compile(string key, string[] sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp13);
        var compilation = CSharpGeneratorRunner.CreateCompilation(sources[0]);
        compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), compilation.SyntaxTrees.Single().WithFilePath("Source0.cs"));
        compilation = compilation.AddSyntaxTrees(sources.Skip(1).Select((s, i) => CSharpSyntaxTree.ParseText(s, parseOptions, path: $"Source{i + 1}.cs")));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new NexusGenerator().AsSourceGenerator() },
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);

        var result = driver.GetRunResult();
        if (result.Results.FirstOrDefault(r => r.Exception != null).Exception is { } exception)
            throw new InvalidOperationException("The generator threw.", exception);

        var steps = result.Results.Single().TrackedSteps;
        var ir = steps.TryGetValue(NexusGenerator.IrTrackingName, out var step)
            ? (NexusIr?)step.Single().Outputs.Single().Value
            : null;
        if (ir is null)
            throw new InvalidOperationException("The generator produced no IR: no nexus of the fixture opts in to a target.");

        // A fixture the generator rejects leaves classes without their generated half: fail on it before reading them.
        var generatorErrors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (generatorErrors.Length > 0)
            throw new InvalidOperationException($"Fixture '{key}' has generator errors:\n{string.Join("\n", generatorErrors.Select(d => d.ToString()))}");

        var nexusClasses = NexusClasses(generated).ToArray();
        var stubs = CSharpSyntaxTree.ParseText(Stubs(generated, nexusClasses), parseOptions, path: "HashParityStubs.cs");
        var final = generated.AddSyntaxTrees(stubs)
            .WithAssemblyName("HashParity_" + key)
            .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        using var image = new MemoryStream();
        var emit = final.Emit(image);
        if (!emit.Success)
        {
            var errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString());
            throw new InvalidOperationException($"Fixture '{key}' does not compile:\n{string.Join("\n", errors)}");
        }

        image.Position = 0;
        var context = new AssemblyLoadContext("HashParity_" + key, isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(image);
            var nexuses = nexusClasses.ToDictionary(
                c => c.Name,
                c => Read(assembly.GetType(c.MetadataName, throwOnError: true)!),
                StringComparer.Ordinal);
            return new HashParityRun(ir, nexuses);
        }
        finally
        {
            context.Unload();
        }
    }

    private sealed record NexusClass(string Name, string MetadataName, INamedTypeSymbol Symbol);

    private static IEnumerable<NexusClass> NexusClasses(Compilation compilation)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not { } symbol
                    || !symbol.GetAttributes().Any(a => a.AttributeClass is { Name: "NexusAttribute", TypeArguments.Length: 2 }))
                    continue;
                if (seen.Add(MetadataName(symbol)))
                    yield return new NexusClass(symbol.Name, MetadataName(symbol), symbol);
            }
        }
    }

    private static string MetadataName(INamedTypeSymbol symbol)
        => symbol.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "." + symbol.MetadataName
            : symbol.MetadataName;

    /// <summary>
    /// Public implementations (the generated nexus calls them by name), throwing, of every interface member a fixture nexus class leaves unimplemented (the
    /// fixtures declare interfaces only; the hashes do not depend on the bodies).
    /// </summary>
    private static string Stubs(Compilation compilation, IEnumerable<NexusClass> classes)
    {
        var sb = new StringBuilder("#nullable enable\n");
        foreach (var nexus in classes)
        {
            // Re-bind: the symbol must come from the compilation that holds the generated half of the class.
            var symbol = compilation.GetTypeByMetadataName(nexus.MetadataName)!;
            var members = new StringBuilder();
            foreach (var iface in symbol.AllInterfaces)
            {
                foreach (var member in iface.GetMembers())
                {
                    if (member.IsStatic || symbol.FindImplementationForInterfaceMember(member) != null)
                        continue;
                    switch (member)
                    {
                        case IMethodSymbol { MethodKind: MethodKind.Ordinary } method:
                            var parameters = string.Join(", ", method.Parameters.Select((p, i) => $"{p.Type.ToDisplayString(StubFormat)} p{i}"));
                            var returns = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(StubFormat);
                            members.AppendLine($"    public {returns} {method.Name}({parameters}) => throw new global::System.NotSupportedException();");
                            break;
                        case IPropertySymbol property:
                            members.AppendLine($"    public {property.Type.ToDisplayString(StubFormat)} {property.Name} => throw new global::System.NotSupportedException();");
                            break;
                    }
                }
            }

            if (members.Length == 0)
                continue;
            var ns = symbol.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;
            if (ns != null)
                sb.Append("namespace ").Append(ns).AppendLine(" {");
            sb.Append("partial class ").Append(symbol.Name).AppendLine(" {").Append(members).AppendLine("}");
            if (ns != null)
                sb.AppendLine("}");
        }

        return sb.ToString();
    }

    /// <summary>The session types of a compiled nexus class, from its generated <c>CreateClient</c> / <c>CreateServer</c>.</summary>
    private static CompiledNexus Read(Type nexusClass)
    {
        var factories = nexusClass.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "CreateClient" or "CreateServer")
            .ToArray();
        if (factories.Length != 1)
            throw new InvalidOperationException($"{nexusClass.FullName} has no generated CreateClient/CreateServer factory.");
        var factory = factories[0];
        var sessionTypes = factory.ReturnType.GetGenericArguments();
        return new CompiledNexus(nexusClass.Name, factory.Name == "CreateServer", Surface(sessionTypes[0]), Surface(sessionTypes[1]));
    }

    private static HashSurface Surface(Type type)
    {
        var methodHash = (int)Accessor(nameof(IInvocationMethodHash.GetMethodHash), type).Invoke(null, null)!;
        var table = (IEnumerable<KeyValuePair<string, int>>)Accessor(nameof(IInvocationMethodHash.GetVersionHashTable), type).Invoke(null, null)!;
        var set = (IEnumerable<long>)Accessor(nameof(IInvocationMethodHash.GetVersionMethodHashSet), type).Invoke(null, null)!;
        return new HashSurface(methodHash, table.ToArray(), set.Order().ToArray());
    }

    private static MethodInfo Accessor(string name, Type type)
        => typeof(IInvocationMethodHash).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(type);
}
