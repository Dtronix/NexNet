using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;
using NexNet.Generator.Runtime;

namespace NexNet.Generator.Targets.JavaScript;

/// <summary>
/// The JavaScript client target (<c>@dtronix/nexnet</c>, ESM + <c>.d.ts</c>). Type names are TypeScript declaration
/// syntax. J03 emits a module and declarations per declared type, <c>formatters.js</c> and the support module; J04 emits
/// a module per export with its handshake, server proxy, client nexus base and factory (see <see cref="Emit"/>). The test
/// scaffolding of the cross-language harness (<see cref="EmitMode.TestScaffolding"/>, option <c>EmitTestScaffolding</c>)
/// goes in its <c>scaffolding/</c> directory (J11, <see cref="JsScaffoldingEmitter"/>).
/// </summary>
internal sealed class JavaScriptTarget : TargetLanguageBase
{
    /// <summary>Option: the module name generated code imports the runtime from (optional).</summary>
    public const string ModuleNameOption = "ModuleName";

    /// <summary>Option: <c>true</c> also emits the test scaffolding (<see cref="EmitMode.TestScaffolding"/>).</summary>
    public const string TestScaffoldingOption = Bundle.TargetBundleBuilder.TestScaffoldingOption;

    /// <summary>
    /// Option: how generated code imports the runtime when <c>ModuleName</c> is not set: <c>package</c> (the default,
    /// <c>@dtronix/nexnet</c>) or <c>relative</c> (<c>./nexnet-runtime/index.js</c>, the emitted runtime). D31.
    /// </summary>
    public const string RuntimeImportOption = "RuntimeImport";

    /// <summary>The runtime folder in the output directory (D31).</summary>
    public const string RuntimeDirectory = "nexnet-runtime";

    /// <summary>The runtime specifier of <c>RuntimeImport=relative</c>, for files at the top of the output directory.</summary>
    public const string RelativeRuntimeModule = "./" + RuntimeDirectory + "/index.js";

    private static readonly string[] Options = { ModuleNameOption, TestScaffoldingOption, EmbeddedRuntime.EmitRuntimeOption, RuntimeImportOption };

    /// <summary>ECMAScript reserved words (strict mode and modules) and TypeScript type keywords.</summary>
    internal static readonly string[] Keywords =
    {
        "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do",
        "else", "enum", "export", "extends", "false", "finally", "for", "function", "if", "implements", "import",
        "in", "instanceof", "interface", "let", "new", "null", "package", "private", "protected", "public",
        "return", "static", "super", "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while",
        "with", "yield", "arguments", "eval", "undefined", "NaN", "Infinity",
        "any", "boolean", "never", "number", "bigint", "string", "symbol", "object", "unknown",
    };

    /// <summary>Names every object already has or that change object behavior.</summary>
    internal static readonly string[] ReservedMembers =
    {
        "constructor", "__proto__", "prototype", "hasOwnProperty", "isPrototypeOf", "propertyIsEnumerable",
        "toLocaleString", "toString", "valueOf", "then",
        "equals", "hashKey",    // methods of every generated class (J03)
    };

    /// <summary>File names (without extension) the emitter uses for its own modules; a type or export may not take them.</summary>
    internal static readonly string[] ReservedFileNames = { JsSyntax.FormattersModule, JsSyntax.SupportModule, JsSyntax.UserFormattersModule };

    // Declared after the static tables it reads: static fields initialize in textual order.
    public static readonly JavaScriptTarget Instance = new();

    private JavaScriptTarget()
    {
        Names = new NameMapper(Keywords, ReservedMembers);
        Types = new JavaScriptTypeMapper(this);
    }

    public override string Id => "javascript";

    public override string DisplayName => "JavaScript";

    public override NexusTargets Flag => NexusTargets.JavaScript;

    public override ITypeMapper Types { get; }

    public override INameMapper Names { get; }

    protected override IReadOnlyList<string> OptionNames => Options;

    public override IEnumerable<string> ValidateOptions(TargetOptions options)
    {
        foreach (var problem in base.ValidateOptions(options))
            yield return problem;

        var import = options.Get(RuntimeImportOption);
        if (import != null && !string.Equals(import, "package", StringComparison.OrdinalIgnoreCase) && !IsRelative(options))
            yield return "RuntimeImport of the javascript target must be package or relative, not '" + import + "'";
    }

    /// <summary>A <c>ModuleName</c> names the module the runtime comes from, so the runtime is not emitted by default.</summary>
    protected override bool RuntimeByDefault(TargetOptions options) => options.Get(ModuleNameOption) == null;

    private static bool IsRelative(TargetOptions options)
        => string.Equals(options.Get(RuntimeImportOption), "relative", StringComparison.OrdinalIgnoreCase);

    /// <summary>The module specifier generated code imports the runtime from (for top-level files).</summary>
    internal static string RuntimeModule(TargetOptions options)
        => options.Get(ModuleNameOption) ?? (IsRelative(options) ? RelativeRuntimeModule : JsSyntax.DefaultRuntimeModule);

    /// <summary>
    /// D31: the @dtronix/nexnet sources and their committed declarations (J27, <c>.d.ts</c> beside each <c>.js</c>) in
    /// <c>nexnet-runtime/</c>, each after the runtime header line, with a <c>package.json</c> that has the published
    /// conditional exports, and <c>LICENSE</c>.
    /// </summary>
    protected override IEnumerable<GeneratedFile> EmitRuntimeCore(TargetOptions options)
    {
        var header = EmbeddedRuntime.HeaderLine("//") + "\n";
        foreach (var source in EmbeddedRuntime.Sources(Id))
            yield return new GeneratedFile(RuntimeDirectory + "/" + source.Path, header + source.Content);

        yield return new GeneratedFile(RuntimeDirectory + "/LICENSE", EmbeddedRuntime.License);
        yield return new GeneratedFile(RuntimeDirectory + "/package.json", RuntimePackageJson);
    }

    /// <summary>
    /// The <c>package.json</c> of the emitted runtime: the exports of clients/js/packages/nexnet, with the <c>types</c>
    /// conditions pointing at the emitted declarations (J27).
    /// </summary>
    internal static string RuntimePackageJson { get; } =
        "{\n"
        + "  \"name\": \"@dtronix/nexnet\",\n"
        + "  \"version\": \"" + EmbeddedRuntime.Version + "\",\n"
        + "  \"description\": \"NexNet client runtime, written by NexNet.Generator. Do not edit.\",\n"
        + "  \"license\": \"MIT\",\n"
        + "  \"private\": true,\n"
        + "  \"type\": \"module\",\n"
        + "  \"sideEffects\": false,\n"
        + "  \"types\": \"./index.d.ts\",\n"
        + "  \"engines\": {\n"
        + "    \"node\": \">=22\"\n"
        + "  },\n"
        + "  \"exports\": {\n"
        + "    \".\": {\n"
        + "      \"types\": \"./index.d.ts\",\n"
        + "      \"browser\": \"./browser.js\",\n"
        + "      \"node\": \"./node.js\",\n"
        + "      \"default\": \"./index.js\"\n"
        + "    },\n"
        + "    \"./node\": {\n"
        + "      \"types\": \"./node.d.ts\",\n"
        + "      \"node\": \"./node.js\"\n"
        + "    },\n"
        + "    \"./diagnostics\": {\n"
        + "      \"types\": \"./diagnostics.d.ts\",\n"
        + "      \"default\": \"./diagnostics.js\"\n"
        + "    },\n"
        + "    \"./package.json\": \"./package.json\"\n"
        + "  }\n"
        + "}\n";

    /// <summary>
    /// The proxy and client nexus base escape names of their runtime base class members with a trailing '_'
    /// (<see cref="JsProxyEmitter.MethodName"/>), so NEXNET041 sees the name the emitters write.
    /// </summary>
    protected override string EscapeApiMember(bool serverApi, string mapped)
    {
        var reserved = serverApi ? JsProxyEmitter.ReservedMembers : JsClientNexusEmitter.ReservedMembers;
        return Array.IndexOf(reserved, mapped) >= 0 ? mapped + "_" : mapped;
    }

    /// <summary>
    /// One module and its declarations per export: <c>ChatServerNexus.js</c>, <c>ChatServerNexus.d.ts</c>
    /// (<see cref="JsProxyEmitter"/>).
    /// </summary>
    public override IEnumerable<string> ExportFiles(ExportIr export, TargetOptions options)
    {
        var name = Names.Type(export.Name);
        return new[] { name + ".js", name + ".d.ts" };
    }

    public override IEnumerable<string> TypeFiles(TypeDeclIr type, TargetOptions options)
    {
        var name = TypeName(type);
        return new[] { name + ".js", name + ".d.ts" };
    }

    public override IEnumerable<TargetDiagnostic> Validate(NexusIr ir, ExportIr export, TargetOptions options)
    {
        var diagnostics = base.Validate(ir, export, options).ToList();

        // The emitter's own modules share the output directory with the type and export modules.
        var named = TargetIrWalker.OwnedDecls(ir, export, Flag).Select(d => (What: "type '" + d.Id + "'", Name: TypeName(d)))
            .Append((What: "export '" + export.Name + "'", Name: Names.Type(export.Name)));
        foreach (var (what, name) in named)
        {
            if (ReservedFileNames.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new TargetDiagnostic(
                    DiagnosticDescriptors.TargetNameCollision.Id,
                    DisplayName,
                    what + " maps to '" + name + "', which the generated " + name.ToLowerInvariant() + " module uses",
                    export.Location));
            }
        }

        // The export module's declarations import the types its methods use by name, beside its own exports.
        var exported = new HashSet<string>(JsExportNames.Of(export).All, StringComparer.Ordinal);
        foreach (var decl in TargetIrWalker.OwnedDecls(ir, export, Flag))
        {
            var name = TypeName(decl);
            if (exported.Contains(name))
            {
                diagnostics.Add(new TargetDiagnostic(
                    DiagnosticDescriptors.TargetNameCollision.Id,
                    DisplayName,
                    "type '" + decl.Id + "' maps to '" + name + "', which the module of export '" + export.Name + "' exports",
                    export.Location));
            }
        }

        // D12: a JavaScript formatter is '<module>#<export>' or '<module>' (its default export).
        var invalid = new SortedSet<string>(StringComparer.Ordinal);
        void Visit(TypeRef? type)
        {
            if (type == null)
                return;
            if (type.Kind == TypeRefKind.External && NativeFormatter(type) is { } spec && JsFormatterEmitter.ParseSpec(spec) == null)
                invalid.Add((type.Clr ?? "?") + "\n" + spec);
            foreach (var arg in type.Args)
                Visit(arg);
        }

        foreach (var api in new[] { export.ServerApi, export.ClientApi })
        {
            foreach (var method in api.Methods)
            {
                Visit(method.ReturnType);
                Visit(method.PipeType);
                foreach (var param in method.Params)
                    Visit(param.Type);
            }

            foreach (var collection in api.Collections)
                Visit(collection.Item);
        }

        foreach (var decl in TargetIrWalker.OwnedDecls(ir, export, Flag).OfType<ObjectDeclIr>())
        {
            foreach (var member in decl.Members)
                Visit(member.Type);
        }

        foreach (var entry in invalid)
        {
            var parts = entry.Split('\n');
            diagnostics.Add(new TargetDiagnostic(
                DiagnosticDescriptors.InvalidTargetFormatter.Id,
                parts[0],
                "the JavaScript formatter '" + parts[1] + "' is not '<module>#<export>' or '<module>'",
                export.Location));
        }

        return diagnostics;
    }

    /// <summary>
    /// Emits, in the output directory: <c>&lt;Type&gt;.js</c> and <c>&lt;Type&gt;.d.ts</c> per declared type,
    /// <c>formatters.js</c>/<c>.d.ts</c>, <c>nexnet-support.js</c>/<c>.d.ts</c>, with user formatters
    /// <c>user-formatters.d.ts</c>, and per export <c>&lt;ExportName&gt;.js</c>/<c>.d.ts</c>. Nothing when no export opts in.
    /// </summary>
    public override IEnumerable<GeneratedFile> Emit(NexusIr ir, TargetOptions options, EmitMode mode)
    {
        if ((mode != EmitMode.Client && mode != EmitMode.TestScaffolding) || !ir.Exports.Any(e => (e.Targets & (int)Flag) != 0))
            return Array.Empty<GeneratedFile>();

        var decls = TargetIrWalker.TargetDecls(ir, Flag).Select(d => d.Decl).ToArray();
        var context = new JsContext(this, ir, decls, RuntimeModule(options));
        var users = JsFormatterEmitter.UserFormatters(ir, decls);
        if (mode == EmitMode.TestScaffolding)
            return RelocateRuntimeImports(JsScaffoldingEmitter.Emit(context, users), options);

        var files = new List<GeneratedFile>();
        foreach (var decl in decls)
            files.AddRange(JsTypeEmitter.Emit(context, decl));
        files.AddRange(JsFormatterEmitter.Emit(context, users));
        files.AddRange(JsSupportEmitter.Emit(context));
        foreach (var export in ir.Exports.Where(e => (e.Targets & (int)Flag) != 0))
            files.AddRange(JsProxyEmitter.Emit(context, export, users));
        return RelocateRuntimeImports(files, options);
    }

    /// <summary>
    /// With <c>RuntimeImport=relative</c>, a file in a sub-folder (the test scaffolding) reaches the runtime folder
    /// through '../' per level.
    /// </summary>
    private static IEnumerable<GeneratedFile> RelocateRuntimeImports(IEnumerable<GeneratedFile> files, TargetOptions options)
    {
        if (options.Get(ModuleNameOption) != null || !IsRelative(options))
            return files;

        var top = "'" + RelativeRuntimeModule.Substring(0, RelativeRuntimeModule.Length - "index.js".Length);
        return files.Select(f =>
        {
            var depth = f.Path.Count(c => c == '/');
            if (depth == 0)
                return f;
            var up = "'" + string.Concat(Enumerable.Repeat("../", depth)) + RuntimeDirectory + "/";
            return f with { Content = f.Content.Replace(top, up) };
        }).ToArray();
    }

    /// <summary>Report §3.5, JS column. Only primitives (boolean, number, bigint, string) compare by value in a Map/Set.</summary>
    private sealed class JavaScriptTypeMapper : ITypeMapper
    {
        private readonly JavaScriptTarget _target;

        public JavaScriptTypeMapper(JavaScriptTarget target) => _target = target;

        public TargetType Map(TypeRef type, TypeScope scope)
        {
            var mapped = MapCore(type, scope);
            return mapped.IsSupported && type.RefNullable && type.Kind != TypeRefKind.Nullable
                ? mapped with { Name = mapped.Name + " | null" }
                : mapped;
        }

        private TargetType MapCore(TypeRef type, TypeScope scope)
        {
            var args = new TargetType[type.Args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                args[i] = Map(type.Args[i], scope);
                if (!args[i].IsSupported)
                    return args[i];
            }

            string Generic(string name) => name + "<" + string.Join(", ", args.Select(a => a.Name)) + ">";

            switch (type.Kind)
            {
                case TypeRefKind.Prim:
                    return type.JsNumber ? new TargetType("number", true) : Prim(type.Prim);   // D13 opt-in
                case TypeRefKind.Bin:
                    return new TargetType("Uint8Array", false);
                case TypeRefKind.PrimArray:
                    return PrimArray(type.Prim);
                case TypeRefKind.Seq:
                case TypeRefKind.Queue:
                case TypeRefKind.Stack:     // index 0 is the top
                    return new TargetType(Generic("Array"), false);
                case TypeRefKind.Set:
                    return new TargetType(Generic("Set"), false);
                case TypeRefKind.Map:
                    return new TargetType(Generic("Map"), false);
                case TypeRefKind.Tuple:
                    return new TargetType("[" + string.Join(", ", args.Select(a => a.Name)) + "]", false);
                case TypeRefKind.Nullable:
                    return args.Length == 1
                        ? args[0] with { Name = args[0].Name + " | null" }
                        : TargetType.NotSupported("a malformed nullable reference");
                case TypeRefKind.Decl:
                {
                    var decl = scope.Find(type.DeclId);
                    if (decl == null)
                        return TargetType.NotSupported("the declared type '" + type.DeclId + "' is not in the IR");
                    var name = _target.TypeName(decl);
                    // Enums are numbers (bigint for 64-bit underlying types); objects and unions are class instances.
                    return new TargetType(args.Length == 0 ? name : Generic(name), decl is EnumDeclIr);
                }
                case TypeRefKind.TypeParam:
                    return new TargetType(scope.TypeParam(type.Ordinal), true);
                case TypeRefKind.External:
                {
                    var formatter = _target.NativeFormatter(type);
                    return formatter == null
                        ? TargetType.NotSupported(_target.NoFormatterReason(type))
                        : new TargetType("unknown", false, NativeFormatter: formatter);
                }
                default:
                    return TargetType.NotSupported("the type kind " + type.Kind + " has no JavaScript mapping");
            }
        }

        private static TargetType Prim(PrimKind prim) => prim switch
        {
            PrimKind.Bool => new("boolean", true),
            PrimKind.SByte or PrimKind.Byte or PrimKind.Int16 or PrimKind.UInt16 or PrimKind.Int32 or PrimKind.UInt32
                or PrimKind.Single or PrimKind.Double or PrimKind.Half => new("number", true),
            PrimKind.Int64 or PrimKind.UInt64 => new("bigint", true),     // D13: bigint by default
            PrimKind.Char => new("string", true),                         // one UTF-16 code unit
            PrimKind.Decimal => new("NexusDecimal", false),
            PrimKind.String => new("string", true),
            PrimKind.DateTime => new("NexusDateTime", false),
            PrimKind.DateTimeOffset => new("NexusDateTimeOffset", false),
            PrimKind.TimeSpan => new("NexusTimeSpan", false),
            PrimKind.DateOnly => new("NexusDateOnly", false),
            PrimKind.TimeOnly => new("NexusTimeOnly", false),
            PrimKind.Guid => new("string", true),                         // lower case
            PrimKind.BigInteger => new("bigint", true),
            PrimKind.Uri => new("string", true),
            PrimKind.Version => new("Version", false),
            _ => TargetType.NotSupported("the primitive " + prim + " has no JavaScript mapping"),
        };

        // Typed arrays compare by reference.
        private static TargetType PrimArray(PrimKind prim) => prim switch
        {
            PrimKind.SByte => new("Int8Array", false),
            PrimKind.Int16 => new("Int16Array", false),
            PrimKind.UInt16 => new("Uint16Array", false),
            PrimKind.Int32 => new("Int32Array", false),
            PrimKind.UInt32 => new("Uint32Array", false),
            PrimKind.Int64 => new("BigInt64Array", false),
            PrimKind.UInt64 => new("BigUint64Array", false),
            PrimKind.Single => new("Float32Array", false),
            PrimKind.Double => new("Float64Array", false),
            PrimKind.Half => new("Float32Array", false),    // Float16Array is not available everywhere
            PrimKind.Char => new("Uint16Array", false),     // UTF-16 code units
            _ => TargetType.NotSupported("a primitive array of " + prim + " has no JavaScript mapping"),
        };
    }
}
