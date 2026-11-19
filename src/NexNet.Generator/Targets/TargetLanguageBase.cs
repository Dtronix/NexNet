using System.Globalization;
using Microsoft.CodeAnalysis.Diagnostics;
using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;
using NexNet.Generator.Models;
using NexNet.Generator.Runtime;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Targets;

/// <summary>
/// The parts of a target language that only depend on its type and name mappers: option parsing and the IR checks of
/// <see cref="ITargetLanguage.Validate"/> (NEXNET040, 041, 042, 046).
/// </summary>
internal abstract class TargetLanguageBase : ITargetLanguage
{
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public abstract NexusTargets Flag { get; }

    public abstract ITypeMapper Types { get; }

    public abstract INameMapper Names { get; }

    /// <summary>The option names this target reads (<c>NexNetTarget_&lt;id&gt;_&lt;Name&gt;</c>), excluding OutputDir.</summary>
    protected abstract IReadOnlyList<string> OptionNames { get; }

    public TargetOptions ParseOptions(AnalyzerConfigOptions global)
    {
        var values = new List<TargetOption>();
        foreach (var name in OptionNames)
        {
            if (global.TryGetValue(TargetRegistry.OptionPrefix + Id + "_" + name, out var value) && !string.IsNullOrWhiteSpace(value))
                values.Add(new TargetOption(name, value.Trim()));
        }

        return new TargetOptions(
            Id,
            TargetRegistry.ConfiguredIds(global).Contains(Id),
            new EquatableArray<TargetOption>(values.ToArray()));
    }

    /// <summary>Problems with the options every target has (<c>EmitRuntime</c>); overrides add their own.</summary>
    public virtual IEnumerable<string> ValidateOptions(TargetOptions options)
    {
        var emitRuntime = options.Get(EmbeddedRuntime.EmitRuntimeOption);
        if (emitRuntime != null && !IsBoolean(emitRuntime))
            yield return "EmitRuntime of the " + Id + " target must be true or false, not '" + emitRuntime + "'";
    }

    /// <summary>
    /// <c>EmitRuntime</c> when set; otherwise true, except for the test scaffolding of the in-repo language hosts
    /// (<c>EmitTestScaffolding=true</c>), which build against the workspace runtime, and when
    /// <see cref="RuntimeByDefault"/> says no.
    /// </summary>
    public bool EmitsRuntime(TargetOptions options)
        => EmbeddedRuntime.IsEnabled(
            options,
            !string.Equals(options.Get(Bundle.TargetBundleBuilder.TestScaffoldingOption), "true", StringComparison.OrdinalIgnoreCase)
            && RuntimeByDefault(options));

    /// <summary>The implicit <c>EmitRuntime</c> of a target without test scaffolding.</summary>
    protected virtual bool RuntimeByDefault(TargetOptions options) => true;

    private readonly object _runtimeGate = new();
    private (TargetOptions Options, IReadOnlyList<GeneratedFile> Files)? _lastRuntime;

    public IReadOnlyList<GeneratedFile> EmitRuntime(TargetOptions options)
    {
        if (!EmitsRuntime(options))
            return Array.Empty<GeneratedFile>();

        // The runtime depends only on the options; validation and the bundle both ask for it.
        lock (_runtimeGate)
        {
            if (_lastRuntime is { } last && last.Options.Equals(options))
                return last.Files;
        }

        var files = EmitRuntimeCore(options).ToArray();
        lock (_runtimeGate)
            _lastRuntime = (options, files);
        return files;
    }

    /// <summary>The runtime files of an enabled runtime (<see cref="EmitRuntime"/>).</summary>
    protected abstract IEnumerable<GeneratedFile> EmitRuntimeCore(TargetOptions options);

    /// <summary>True for <c>true</c> or <c>false</c>, in any case.</summary>
    protected static bool IsBoolean(string value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    public abstract IEnumerable<string> ExportFiles(ExportIr export, TargetOptions options);

    public abstract IEnumerable<string> TypeFiles(TypeDeclIr type, TargetOptions options);

    public virtual IEnumerable<GeneratedFile> Emit(NexusIr ir, TargetOptions options, EmitMode mode) => Array.Empty<GeneratedFile>();

    /// <summary>
    /// The flat name of a declared type in this target: containing types and the name joined by '_'
    /// (<c>Outer+Inner</c> → <c>Outer_Inner</c>), without generic arity, keyword-escaped.
    /// </summary>
    public string TypeName(TypeDeclIr decl)
        => Names.Type(decl.ContainingTypes.Length == 0 ? decl.Name : string.Join("_", decl.ContainingTypes.Concat(new[] { decl.Name })));

    public virtual IEnumerable<TargetDiagnostic> Validate(NexusIr ir, ExportIr export, TargetOptions options)
    {
        var diagnostics = new List<TargetDiagnostic>();
        var scope = new TypeScope(ir);

        ValidateInterface(export, export.ServerApi, serverApi: true, scope, diagnostics);
        ValidateInterface(export, export.ClientApi, serverApi: false, scope, diagnostics);

        foreach (var decl in TargetIrWalker.OwnedDecls(ir, export, Flag))
            ValidateDecl(export, decl, scope, diagnostics);

        return diagnostics;
    }

    /// <summary>
    /// The final name of a method or collection accessor of the server API (the proxy) or the client API (the client
    /// nexus base), given its mapped name. A target whose generated classes rename members that clash with their runtime
    /// base class overrides this, so NEXNET041 sees the names the emitters write (e.g. Java <c>Invoke</c> → <c>invoke_</c>).
    /// </summary>
    protected virtual string EscapeApiMember(bool serverApi, string mapped) => mapped;

    private void ValidateInterface(ExportIr export, InterfaceIr api, bool serverApi, TypeScope scope, List<TargetDiagnostic> diagnostics)
    {
        // NEXNET042: G02 blocks such exports, so this only guards IR built some other way.
        foreach (var group in api.Methods.GroupBy(m => m.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            diagnostics.Add(new TargetDiagnostic(
                DiagnosticDescriptors.DuplicateMethodName.Id, api.ClrName, group.Key, group.First().Location ?? export.Location));
        }

        // Compare the names as the emitters write them, after any escape for a base class member (G08).
        var members = api.Methods.Select(m => (Kind: "method", m.Name, Mapped: EscapeApiMember(serverApi, Names.Method(m.Name)), Location: m.Location))
            .Concat(api.Collections.Select(c => (Kind: "collection", c.Name, Mapped: EscapeApiMember(serverApi, Names.Property(c.Name)), Location: (LocationData?)null)));
        ReportCollisions(members.Select(m => (m.Kind + " '" + m.Name + "'", m.Mapped, m.Location ?? export.Location)), "of '" + api.ClrName + "'", diagnostics);

        foreach (var method in api.Methods)
        {
            var site = "'" + api.ClrName + "." + method.Name + "'";
            var location = method.Location ?? export.Location;

            ReportCollisions(
                method.Params.Select(p => ("parameter '" + p.Name + "'", Names.Parameter(p.Name), location)),
                "of " + site,
                diagnostics);

            if (method.ReturnType != null)
                CheckType(method.ReturnType, "the return type of " + site, location, scope, diagnostics);

            var checkedPipeType = false;
            foreach (var param in method.Params)
            {
                if (param.Type == null)
                    continue;
                checkedPipeType |= param.Kind == ParamKind.DuplexChannel;
                CheckType(param.Type, "parameter '" + param.Name + "' of " + site, location, scope, diagnostics);
            }

            if (method.PipeType != null && !checkedPipeType)
                CheckType(method.PipeType, "the channel type of " + site, location, scope, diagnostics);
        }

        foreach (var collection in api.Collections)
        {
            if (collection.Item != null)
                CheckType(collection.Item, "the item type of collection '" + api.ClrName + "." + collection.Name + "'", export.Location, scope, diagnostics);
        }
    }

    private void ValidateDecl(ExportIr export, TypeDeclIr decl, TypeScope scope, List<TargetDiagnostic> diagnostics)
    {
        var location = export.Location;
        switch (decl)
        {
            case ObjectDeclIr obj:
            {
                var inner = scope.WithTypeParams(obj.TypeParams.ToArray());

                // Members of the base types become members of the same target class.
                var all = new List<MemberIr>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var current = obj;
                while (current != null && seen.Add(current.Id))
                {
                    all.AddRange(current.Members);
                    current = scope.Find(current.BaseDeclId) as ObjectDeclIr;
                }

                ReportCollisions(
                    all.Select(m => ("member '" + m.Name + "'", Names.Property(m.Name), location)),
                    "of '" + obj.Id + "'",
                    diagnostics);

                foreach (var member in obj.Members)
                    CheckType(member.Type, "member '" + member.Name + "' of '" + obj.Id + "'", location, inner, diagnostics);
                break;
            }

            case EnumDeclIr enumDecl:
                ReportCollisions(
                    enumDecl.Members.Select(m => ("member '" + m.Name + "'", Names.EnumMember(m.Name), location)),
                    "of '" + enumDecl.Id + "'",
                    diagnostics);
                break;
        }
    }

    /// <summary>NEXNET040 when the type cannot be represented; otherwise NEXNET046 for a key compared by reference.</summary>
    private void CheckType(TypeRef type, string site, LocationData? location, TypeScope scope, List<TargetDiagnostic> diagnostics)
    {
        var mapped = Types.Map(type, scope);
        if (!mapped.IsSupported)
        {
            diagnostics.Add(new TargetDiagnostic(
                DiagnosticDescriptors.TargetTypeUnsupported.Id,
                "'" + Describe(type) + "' used by " + site,
                DisplayName + ": " + mapped.Unsupported,
                location));
            return;
        }

        var key = FindReferenceKey(type, scope);
        if (key != null)
        {
            diagnostics.Add(new TargetDiagnostic(
                DiagnosticDescriptors.TargetReferenceKey.Id,
                site,
                DisplayName + " (key type '" + Describe(key) + "' maps to '" + Types.Map(key, scope).Name + "')",
                location));
        }
    }

    /// <summary>The first set element or dictionary key type, at any depth, that compares by reference in the target.</summary>
    private TypeRef? FindReferenceKey(TypeRef type, TypeScope scope)
    {
        if ((type.Kind == TypeRefKind.Set || type.Kind == TypeRefKind.Map) && type.Args.Length > 0
            && !Types.Map(type.Args[0], scope).ValueEquality)
            return type.Args[0];

        foreach (var arg in type.Args)
        {
            var found = FindReferenceKey(arg, scope);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>NEXNET041 for every group of distinct names that map to the same target name.</summary>
    private void ReportCollisions(
        IEnumerable<(string What, string Mapped, LocationData? Location)> names,
        string owner,
        List<TargetDiagnostic> diagnostics)
    {
        foreach (var group in names.GroupBy(n => n.Mapped, StringComparer.Ordinal))
        {
            var distinct = group.Select(n => n.What).Distinct(StringComparer.Ordinal).ToArray();
            if (distinct.Length < 2)
                continue;

            diagnostics.Add(new TargetDiagnostic(
                DiagnosticDescriptors.TargetNameCollision.Id,
                DisplayName,
                string.Join(" and ", distinct) + " " + owner + (distinct.Length == 2 ? " both" : " all") + " map to '" + group.Key + "'",
                group.Skip(1).First().Location));
        }
    }

    /// <summary>A short .NET description of a type reference, for diagnostics.</summary>
    internal static string Describe(TypeRef type)
    {
        string Args() => string.Join(", ", type.Args.Select(Describe));
        return type.Kind switch
        {
            TypeRefKind.Prim => type.Prim.ToString(),
            TypeRefKind.Bin => "byte[]",
            TypeRefKind.PrimArray => type.Prim + "[]",
            TypeRefKind.Nullable => (type.Args.Length > 0 ? Describe(type.Args[0]) : "?") + "?",
            TypeRefKind.Decl => type.Args.Length == 0 ? type.DeclId ?? "?" : type.DeclId + "<" + Args() + ">",
            TypeRefKind.TypeParam => "T" + type.Ordinal.ToString(CultureInfo.InvariantCulture),
            TypeRefKind.External => type.Clr ?? "?",
            _ => (type.Clr ?? type.Kind.ToString()) + "<" + Args() + ">",
        };
    }

    /// <summary>The native formatter declared for an external type in this target, or null.</summary>
    protected string? NativeFormatter(TypeRef type)
    {
        foreach (var formatter in type.NativeFormatters)
        {
            if (formatter.Target == (int)Flag)
                return formatter.Formatter;
        }

        return null;
    }

    /// <summary>The NEXNET040 reason for an external type without a native formatter.</summary>
    protected string NoFormatterReason(TypeRef type)
        => "it is serialized by a custom formatter; declare [assembly: NexusTargetFormatter<"
           + type.Clr + ">(NexusTargets." + Flag + ", \"<fully qualified native formatter>\")]";
}
