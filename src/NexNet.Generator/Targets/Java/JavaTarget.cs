using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.Java;

/// <summary>
/// The Java client target (<c>com.dtronix:nexnet-client</c>, Java 11 / Android API 24). Client emission writes one file
/// per declared type into the configured package (<see cref="TypeEmitter"/>) and the formatter classes into its
/// <c>formatters</c> sub-package (<see cref="FormatterEmitter"/>); proxies and client nexus bases arrive with K04,
/// test scaffolding with K11.
/// </summary>
internal sealed class JavaTarget : TargetLanguageBase
{
    /// <summary>The runtime package of the Java client.</summary>
    public const string RuntimePackage = "com.dtronix.nexnet";

    /// <summary>Option: the Java package of the generated code (required).</summary>
    public const string PackageOption = "Package";

    private static readonly string[] Options = { PackageOption };

    /// <summary>Java reserved words and literals, plus restricted identifiers that cannot name a type or variable.</summary>
    internal static readonly string[] Keywords =
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue",
        "default", "do", "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if",
        "implements", "import", "instanceof", "int", "interface", "long", "native", "new", "package", "private",
        "protected", "public", "return", "short", "static", "strictfp", "super", "switch", "synchronized", "this",
        "throw", "throws", "transient", "try", "void", "volatile", "while", "true", "false", "null", "var", "yield",
        "record", "sealed", "permits", "_",
    };

    /// <summary>Methods of <c>java.lang.Object</c>: a generated method or accessor must not override them by accident.</summary>
    internal static readonly string[] ObjectMethods =
    {
        "clone", "equals", "finalize", "getClass", "hashCode", "notify", "notifyAll", "toString", "wait",
    };

    // Declared after the static tables it reads: static fields initialize in textual order.
    public static readonly JavaTarget Instance = new();

    private JavaTarget()
    {
        Names = new NameMapper(Keywords, ObjectMethods);
        Types = new JavaTypeMapper(this, "");
    }

    public override string Id => "java";

    public override string DisplayName => "Java";

    public override NexusTargets Flag => NexusTargets.Java;

    public override ITypeMapper Types { get; }

    public override INameMapper Names { get; }

    protected override IReadOnlyList<string> OptionNames => Options;

    public override IEnumerable<string> ValidateOptions(TargetOptions options)
    {
        var package = options.Get(PackageOption);
        if (package == null)
        {
            yield return "the java target requires a package: set NexNetJavaPackage or <NexNetTarget Include=\"java\" Package=\"…\" />";
            yield break;
        }

        if (!IsValidPackage(package))
            yield return "'" + package + "' is not a valid Java package name";
    }

    /// <summary>Dot-separated Java identifiers, none of them a keyword.</summary>
    internal static bool IsValidPackage(string package)
    {
        foreach (var segment in package.Split('.'))
        {
            if (segment.Length == 0 || Array.IndexOf(Keywords, segment) >= 0)
                return false;
            if (!(char.IsLetter(segment[0]) || segment[0] == '_' || segment[0] == '$'))
                return false;
            foreach (var c in segment)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$'))
                    return false;
            }
        }

        return true;
    }

    /// <summary>All generated code goes in the configured package: <c>com/acme/ChatServer.java</c>.</summary>
    public override IEnumerable<string> ExportFiles(ExportIr export, TargetOptions options)
        => new[] { PackagePath(options) + Names.Type(export.Name) + ".java" };

    public override IEnumerable<string> TypeFiles(TypeDeclIr type, TargetOptions options)
        => new[] { PackagePath(options) + TypeName(type) + ".java" };

    /// <summary>The path of the formatter classes: <c>com/acme/formatters/</c>.</summary>
    internal static string FormattersPath(TargetOptions options) => PackagePath(options) + FormatterEmitter.SubPackage + "/";

    /// <summary>A type mapper that writes generated types fully qualified (<c>com.acme.Item</c>), for code outside the package.</summary>
    internal ITypeMapper QualifiedTypes(string package) => new JavaTypeMapper(this, package + ".");

    public override IEnumerable<TargetDiagnostic> Validate(NexusIr ir, ExportIr export, TargetOptions options)
    {
        var diagnostics = base.Validate(ir, export, options).ToList();

        // The formatter classes refer to the type constants by simple name; a type with their name would shadow them.
        foreach (var decl in TargetIrWalker.OwnedDecls(ir, export, Flag))
        {
            var name = TypeName(decl);
            if (Array.IndexOf(FormatterEmitter.ReservedTypeNames, name) >= 0)
            {
                diagnostics.Add(new TargetDiagnostic(
                    DiagnosticDescriptors.TargetNameCollision.Id,
                    DisplayName,
                    "type '" + decl.Id + "' maps to '" + name + "', which the generated formatter classes use",
                    export.Location));
            }
        }

        return diagnostics;
    }

    public override IEnumerable<GeneratedFile> Emit(NexusIr ir, TargetOptions options, EmitMode mode)
    {
        if (mode != EmitMode.Client)
            return Array.Empty<GeneratedFile>();

        var package = options.Get(PackageOption) ?? throw new InvalidOperationException("The java target requires a package (NEXNET045).");
        var context = new JavaContext(this, ir, package);
        var decls = TargetIrWalker.TargetDecls(ir, Flag).Select(d => d.Decl).ToArray();

        var unionsOfCase = new Dictionary<string, List<UnionDeclIr>>(StringComparer.Ordinal);
        foreach (var union in decls.OfType<UnionDeclIr>())
        {
            foreach (var unionCase in union.Cases)
            {
                if (!unionsOfCase.TryGetValue(unionCase.CaseDeclId, out var list))
                    unionsOfCase[unionCase.CaseDeclId] = list = new List<UnionDeclIr>();
                list.Add(union);
            }
        }

        var files = new List<GeneratedFile>();
        foreach (var decl in decls)
            files.Add(new GeneratedFile(TypeFiles(decl, options).Single(), TypeEmitter.Emit(context, decl, unionsOfCase)));

        files.AddRange(FormatterEmitter.Emit(context, decls, UserFormatters(ir, decls), FormattersPath(options)));
        return files;
    }

    /// <summary>The native formatters (D12) of every external type the Java code serializes, by formatter class.</summary>
    private IReadOnlyList<FormatterEmitter.UserFormatterRef> UserFormatters(NexusIr ir, IReadOnlyList<TypeDeclIr> decls)
    {
        var found = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        void Visit(TypeRef? type)
        {
            if (type == null)
                return;
            if (type.Kind == TypeRefKind.External && NativeFormatter(type) is { } formatter)
            {
                if (!found.TryGetValue(formatter, out var clrTypes))
                    found[formatter] = clrTypes = new SortedSet<string>(StringComparer.Ordinal);
                clrTypes.Add(type.Clr ?? "?");
            }

            foreach (var arg in type.Args)
                Visit(arg);
        }

        foreach (var decl in decls.OfType<ObjectDeclIr>())
        {
            foreach (var member in decl.Members)
                Visit(member.Type);
        }

        foreach (var export in ir.Exports.Where(e => (e.Targets & (int)Flag) != 0))
        {
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
        }

        return found.Select(f => new FormatterEmitter.UserFormatterRef(f.Key, f.Value.ToArray())).ToArray();
    }

    private static string PackagePath(TargetOptions options)
    {
        var package = options.Get(PackageOption);
        return string.IsNullOrEmpty(package) ? "" : package!.Replace('.', '/') + "/";
    }

    /// <summary>The boxed form of a Java primitive, for generic arguments and nullable values.</summary>
    internal static string Box(string name) => name switch
    {
        "boolean" => "Boolean",
        "byte" => "Byte",
        "short" => "Short",
        "int" => "Integer",
        "long" => "Long",
        "float" => "Float",
        "double" => "Double",
        "char" => "Character",
        _ => name,
    };

    /// <summary>Report §3.5 and 01-context §5, Java column.</summary>
    private sealed class JavaTypeMapper : ITypeMapper
    {
        private const string Rt = RuntimePackage + ".";
        private readonly JavaTarget _target;
        private readonly string _declPrefix;

        /// <param name="target">The target.</param>
        /// <param name="declPrefix">Written before the names of generated types: "" or <c>com.acme.</c>.</param>
        public JavaTypeMapper(JavaTarget target, string declPrefix)
        {
            _target = target;
            _declPrefix = declPrefix;
        }

        public TargetType Map(TypeRef type, TypeScope scope)
        {
            var args = new TargetType[type.Args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                args[i] = Map(type.Args[i], scope);
                if (!args[i].IsSupported)
                    return args[i];
            }

            string Generic(string name) => name + "<" + string.Join(", ", args.Select(a => Box(a.Name))) + ">";

            switch (type.Kind)
            {
                case TypeRefKind.Prim:
                    return Prim(type.Prim);
                case TypeRefKind.Bin:
                    return new TargetType("byte[]", false);
                case TypeRefKind.PrimArray:
                    return PrimArray(type.Prim);
                case TypeRefKind.Seq:
                    return new TargetType(Generic("java.util.List"), true);
                case TypeRefKind.Set:
                    return new TargetType(Generic("java.util.Set"), true);
                case TypeRefKind.Map:
                    return new TargetType(Generic("java.util.Map"), true);
                case TypeRefKind.Queue:
                case TypeRefKind.Stack:
                    // ArrayDeque does not override equals.
                    return new TargetType(Generic("java.util.ArrayDeque"), false);
                case TypeRefKind.Tuple:
                    if (type.TupleKind == "KVP")
                        return new TargetType(Generic("java.util.Map.Entry"), true);
                    if (args.Length is < 1 or > 7)
                        return TargetType.NotSupported("the Java runtime has tuples of 1 to 7 elements; this tuple has " + args.Length);
                    return new TargetType(Generic(Rt + "Tuple" + args.Length), true);
                case TypeRefKind.Nullable:
                    return args.Length == 1
                        ? args[0] with { Name = Box(args[0].Name) }
                        : TargetType.NotSupported("a malformed nullable reference");
                case TypeRefKind.Decl:
                {
                    var decl = scope.Find(type.DeclId);
                    if (decl == null)
                        return TargetType.NotSupported("the declared type '" + type.DeclId + "' is not in the IR");
                    // Generated classes (objects, unions, open enum classes) have value equals/hashCode.
                    var name = _declPrefix + _target.TypeName(decl);
                    return new TargetType(args.Length == 0 ? name : Generic(name), true);
                }
                case TypeRefKind.TypeParam:
                    return new TargetType(scope.TypeParam(type.Ordinal), true);
                case TypeRefKind.External:
                {
                    var formatter = _target.NativeFormatter(type);
                    return formatter == null
                        ? TargetType.NotSupported(_target.NoFormatterReason(type))
                        : new TargetType("Object", true, NativeFormatter: formatter);
                }
                default:
                    return TargetType.NotSupported("the type kind " + type.Kind + " has no Java mapping");
            }
        }

        private static TargetType Prim(PrimKind prim) => prim switch
        {
            PrimKind.Bool => new("boolean", true),
            PrimKind.SByte => new("byte", true),
            PrimKind.Byte => new("int", true),          // range-checked on write
            PrimKind.Int16 => new("short", true),
            PrimKind.UInt16 => new("int", true),        // range-checked on write
            PrimKind.Int32 => new("int", true),
            PrimKind.UInt32 => new("long", true),       // range-checked on write
            PrimKind.Int64 => new("long", true),
            PrimKind.UInt64 => new("long", true),       // raw bits + unsigned helpers
            PrimKind.Single => new("float", true),
            PrimKind.Double => new("double", true),
            PrimKind.Half => new("float", true),        // manual f16 conversion
            PrimKind.Char => new("char", true),
            PrimKind.Decimal => new("java.math.BigDecimal", true),
            PrimKind.String => new("String", true),
            PrimKind.DateTime => new(Rt + "NexusDateTime", true),
            PrimKind.DateTimeOffset => new(Rt + "NexusDateTimeOffset", true),
            PrimKind.TimeSpan => new(Rt + "NexusTimeSpan", true),
            PrimKind.DateOnly => new(Rt + "NexusDateOnly", true),
            PrimKind.TimeOnly => new(Rt + "NexusTimeOnly", true),
            PrimKind.Guid => new("java.util.UUID", true),
            PrimKind.BigInteger => new("java.math.BigInteger", true),
            PrimKind.Uri => new("String", true),
            PrimKind.Version => new(Rt + "NexusVersion", true),
            _ => TargetType.NotSupported("the primitive " + prim + " has no Java mapping"),
        };

        // Java arrays compare by reference.
        private static TargetType PrimArray(PrimKind prim) => prim switch
        {
            PrimKind.SByte => new("byte[]", false),
            PrimKind.Int16 => new("short[]", false),
            PrimKind.UInt16 => new("char[]", false),    // unsigned 16-bit
            PrimKind.Int32 => new("int[]", false),
            PrimKind.UInt32 => new("int[]", false),     // raw bits
            PrimKind.Int64 => new("long[]", false),
            PrimKind.UInt64 => new("long[]", false),    // raw bits
            PrimKind.Single => new("float[]", false),
            PrimKind.Double => new("double[]", false),
            PrimKind.Half => new("float[]", false),
            PrimKind.Char => new("char[]", false),
            _ => TargetType.NotSupported("a primitive array of " + prim + " has no Java mapping"),
        };
    }
}
