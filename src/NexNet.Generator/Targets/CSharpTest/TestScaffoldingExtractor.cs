using Microsoft.CodeAnalysis;
using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;
using NexNet.Generator.Models;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Targets.CSharpTest;

/// <summary>
/// Reads <c>[assembly: NexusTestScaffolding&lt;TServer, TClient&gt;]</c> and builds the C# test scaffolding (H06): a remote
/// proxy per server interface and a script facade per client interface. Each interface is emitted once, however many
/// pairs name it. Problems are NEXNET049. Without the attribute this is one scan of the assembly attributes.
/// </summary>
internal static class TestScaffoldingExtractor
{
    public const string AttributeName = "NexusTestScaffoldingAttribute";
    public const string AttributeNamespace = "NexNet.Interop";

    /// <summary>Prefix of every generated hint name.</summary>
    public const string HintPrefix = "NexNet.TestScaffolding.";

    public static TestScaffoldingOutput Extract(Compilation compilation, CancellationToken cancellationToken)
    {
        List<AttributeData>? attributes = null;
        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass is { IsGenericType: true, Name: AttributeName, TypeArguments.Length: 2 } cls
                && cls.ContainingNamespace?.ToDisplayString() == AttributeNamespace)
                (attributes ??= new List<AttributeData>()).Add(attribute);
        }

        if (attributes == null)
            return TestScaffoldingOutput.Empty;

        var diagnostics = new List<TargetDiagnostic>();
        var shapes = new ShapeBuilder(compilation);
        var servers = new Dictionary<string, ScaffoldInterface>(StringComparer.Ordinal);
        var clients = new Dictionary<string, ScaffoldInterface>(StringComparer.Ordinal);

        foreach (var attribute in attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cls = attribute.AttributeClass!;
            var location = LocationData.FromSyntax(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken));
            var pairName = cls.TypeArguments[0].ToDisplayString() + ", " + cls.TypeArguments[1].ToDisplayString();

            var problem = CheckInterface(compilation, cls.TypeArguments[0]) ?? CheckInterface(compilation, cls.TypeArguments[1]);
            if (problem != null)
            {
                diagnostics.Add(Diagnostic(pairName, problem, location));
                continue;
            }

            var server = (INamedTypeSymbol)cls.TypeArguments[0];
            var client = (INamedTypeSymbol)cls.TypeArguments[1];
            var serverId = ShapeToIr.DeclId(server);
            var clientId = ShapeToIr.DeclId(client);
            if (servers.ContainsKey(serverId) && clients.ContainsKey(clientId))
                continue;

            var (serverData, clientData) = NexusDataExtractor.ExtractExportInterfaces(server, client, shapes, cancellationToken);
            var duplicate = DuplicateMethodName(serverData) ?? DuplicateMethodName(clientData);
            if (duplicate != null)
            {
                diagnostics.Add(Diagnostic(pairName, duplicate, location));
                continue;
            }

            var fragment = IrBuilder.BuildExport(shapes, "Scaffold", "", 0, server, serverData, client, clientData, null, location, cancellationToken);
            var export = fragment.Exports[0];

            if (!servers.ContainsKey(serverId))
            {
                var model = BuildInterface(server, export.ServerApi, fragment.Types, location, out problem, cancellationToken);
                if (model == null)
                    diagnostics.Add(Diagnostic(pairName, problem!, location));
                else
                    servers[serverId] = model;
            }

            if (!clients.ContainsKey(clientId))
            {
                var model = BuildInterface(client, export.ClientApi, fragment.Types, location, out problem, cancellationToken);
                if (model == null)
                    diagnostics.Add(Diagnostic(pairName, problem!, location));
                else
                    clients[clientId] = model;
            }
        }

        var files = new List<GeneratedFile>();
        var classNames = new Dictionary<string, string>(StringComparer.Ordinal);

        void Emit(ScaffoldInterface model, string className, Func<ScaffoldInterface, string, string?> check, Func<ScaffoldInterface, string, string> emit, string kind)
        {
            var qualified = model.Namespace.Length == 0 ? className : model.Namespace + "." + className;
            if (classNames.TryGetValue(qualified, out var other))
            {
                diagnostics.Add(Diagnostic(model.MetadataName, "the generated " + kind + " '" + qualified + "' has the same name as the one generated for '" + other + "'", model.Location));
                return;
            }

            var problem = check(model, className);
            if (problem != null)
            {
                diagnostics.Add(Diagnostic(model.MetadataName, problem, model.Location));
                return;
            }

            classNames[qualified] = model.MetadataName;
            files.Add(new GeneratedFile(HintPrefix + CSharpNames.HintSafe(qualified) + ".g.cs", emit(model, className)));
        }

        foreach (var server in servers.OrderBy(s => s.Key, StringComparer.Ordinal))
            Emit(server.Value, RemoteProxyEmitter.ClassName(server.Value), RemoteProxyEmitter.Check, RemoteProxyEmitter.Emit, "remote proxy");
        foreach (var client in clients.OrderBy(c => c.Key, StringComparer.Ordinal))
            Emit(client.Value, ScriptFacadeEmitter.ClassName(client.Value), ScriptFacadeEmitter.Check, ScriptFacadeEmitter.Emit, "script facade");

        return new TestScaffoldingOutput(
            new EquatableArray<GeneratedFile>(files.ToArray()),
            new EquatableArray<TargetDiagnostic>(diagnostics.ToArray()));
    }

    private static string? DuplicateMethodName(InvocationInterfaceData data)
    {
        var duplicate = data.AllMethods.GroupBy(m => m.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        return duplicate == null ? null : "'" + data.TypeName + "' has more than one method named '" + duplicate.Key + "' (NEXNET042)";
    }

    private static TargetDiagnostic Diagnostic(string subject, string problem, LocationData? location)
        => new(DiagnosticDescriptors.InvalidTestScaffolding.Id, subject, problem, location);

    private static string? CheckInterface(Compilation compilation, ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Interface } named)
            return "'" + type.ToDisplayString() + "' is not an interface";
        if (named.IsGenericType)
            return "'" + type.ToDisplayString() + "' is generic";
        if (!compilation.IsSymbolAccessibleWithin(named, compilation.Assembly))
            return "'" + type.ToDisplayString() + "' is not accessible from this assembly";
        return null;
    }

    private static bool IsPublic(INamedTypeSymbol symbol)
    {
        for (ISymbol? current = symbol; current is INamedTypeSymbol type; current = type.ContainingType)
        {
            if (type.DeclaredAccessibility != Accessibility.Public)
                return false;
        }

        return true;
    }

    private static ScaffoldInterface? BuildInterface(
        INamedTypeSymbol symbol,
        InterfaceIr api,
        EquatableArray<TypeDeclIr> decls,
        LocationData? location,
        out string? problem,
        CancellationToken cancellationToken)
    {
        problem = null;
        var model = new ScaffoldInterface(
            api.ClrName,
            CSharpNames.Type(symbol),
            symbol.Name,
            symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
            IsPublic(symbol),
            api,
            decls,
            location);

        var methods = api.Methods.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var collections = api.Collections.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);

        var declaring = new[] { symbol }.Concat(symbol.AllInterfaces.OrderBy(i => i.ToDisplayString(), StringComparer.Ordinal));
        foreach (var iface in declaring)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ifaceName = CSharpNames.Type(iface);
            foreach (var member in iface.GetMembers())
            {
                if (member.IsStatic || !member.IsAbstract)
                    continue;

                switch (member)
                {
                    case IMethodSymbol { MethodKind: MethodKind.Ordinary } method:
                        if (method.IsGenericMethod)
                        {
                            problem = "the method '" + iface.Name + "." + method.Name + "' is generic";
                            return null;
                        }

                        if (methods.TryGetValue(method.Name, out var methodIr) && done.Add("m:" + method.Name))
                        {
                            var built = BuildMethod(method, methodIr, ifaceName, out problem);
                            if (built == null)
                                return null;
                            model.Methods.Add(built);
                        }
                        else
                        {
                            model.UnsupportedMembers.Add(UnsupportedMethod(method, ifaceName));
                        }
                        break;

                    case IPropertySymbol property:
                        if (property.IsIndexer)
                        {
                            problem = "the indexer of '" + iface.Name + "' cannot be implemented by a remote proxy";
                            return null;
                        }

                        if (collections.TryGetValue(property.Name, out var collectionIr)
                            && property.Type is INamedTypeSymbol { TypeArguments.Length: 1 } collectionType
                            && collectionIr.Item != null
                            && done.Add("c:" + property.Name))
                        {
                            model.Collections.Add(new ScaffoldCollection(
                                collectionIr,
                                ifaceName,
                                CSharpNames.Type(property.Type),
                                CSharpNames.Type(collectionType.TypeArguments[0]),
                                property.SetMethod != null));
                        }
                        else
                        {
                            model.UnsupportedMembers.Add(UnsupportedProperty(property, ifaceName));
                        }
                        break;

                    case IEventSymbol @event:
                        model.UnsupportedMembers.Add(
                            "event " + CSharpNames.Type(@event.Type) + " " + ifaceName + "." + CSharpNames.Escape(@event.Name)
                            + " { add => throw " + NotSupported(@event.Name) + "; remove => throw " + NotSupported(@event.Name) + "; }");
                        break;
                }
            }
        }

        model.Methods.Sort((a, b) => a.Ir.Id.CompareTo(b.Ir.Id));
        model.Collections.Sort((a, b) => a.Ir.Id.CompareTo(b.Ir.Id));
        return model;
    }

    private static ScaffoldMethod? BuildMethod(IMethodSymbol method, MethodIr ir, string ifaceName, out string? problem)
    {
        problem = null;
        if (method.Parameters.Length != ir.Params.Length)
        {
            problem = "the parameters of '" + method.Name + "' do not match its nexus description";
            return null;
        }

        var parameters = new List<ScaffoldParam>(method.Parameters.Length);
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var symbol = method.Parameters[i];
            var paramIr = ir.Params[i];
            if (symbol.RefKind != RefKind.None || symbol.IsParams)
            {
                problem = "the parameter '" + symbol.Name + "' of '" + method.Name + "' is ref, out, in or params";
                return null;
            }

            if (paramIr.Kind is ParamKind.Value or ParamKind.DuplexChannel && paramIr.Type == null)
            {
                problem = "the parameter '" + symbol.Name + "' of '" + method.Name + "' has no serializable type";
                return null;
            }

            var parameter = new ScaffoldParam(paramIr, CSharpNames.Escape(symbol.Name), CSharpNames.Type(symbol.Type), CSharpNames.DefaultValue(symbol));
            if (paramIr.Kind == ParamKind.DuplexChannel && symbol.Type is INamedTypeSymbol { TypeArguments.Length: 1 } channel)
                parameter.ItemType = CSharpNames.Type(channel.TypeArguments[0]);
            parameters.Add(parameter);
        }

        string? resultType = null;
        if (ir.ReturnKind == ReturnKind.TaskOf)
        {
            if (ir.ReturnType == null || method.ReturnType is not INamedTypeSymbol { TypeArguments.Length: 1 } task)
            {
                problem = "the result of '" + method.Name + "' has no serializable type";
                return null;
            }

            resultType = CSharpNames.Type(task.TypeArguments[0]);
        }

        return new ScaffoldMethod(ir, ifaceName, CSharpNames.Type(method.ReturnType), resultType, parameters);
    }

    private static string UnsupportedMethod(IMethodSymbol method, string ifaceName)
        => CSharpNames.Type(method.ReturnType) + " " + ifaceName + "." + CSharpNames.Escape(method.Name) + "("
           + string.Join(", ", method.Parameters.Select(p => CSharpNames.RefPrefix(p) + CSharpNames.Type(p.Type) + " " + CSharpNames.Escape(p.Name)))
           + ") => throw " + NotSupported(method.Name) + ";";

    private static string UnsupportedProperty(IPropertySymbol property, string ifaceName)
    {
        var accessors = new List<string>();
        if (property.GetMethod != null)
            accessors.Add("get => throw " + NotSupported(property.Name) + ";");
        if (property.SetMethod != null)
            accessors.Add((property.SetMethod.IsInitOnly ? "init" : "set") + " => throw " + NotSupported(property.Name) + ";");
        return CSharpNames.Type(property.Type) + " " + ifaceName + "." + CSharpNames.Escape(property.Name) + " { " + string.Join(" ", accessors) + " }";
    }

    internal static string NotSupported(string member)
        => "new global::System.NotSupportedException(" + CSharpNames.Literal("'" + member + "' is not available through the test scaffolding.") + ")";
}
