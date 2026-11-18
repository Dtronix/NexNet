using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.JavaScript;

/// <summary>
/// The JavaScript client target (<c>@dtronix/nexnet</c>, ESM + <c>.d.ts</c>). Type names are TypeScript declaration
/// syntax. Type and name mapping only; the emitters arrive with J03/J04/J11.
/// </summary>
internal sealed class JavaScriptTarget : TargetLanguageBase
{
    /// <summary>Option: the module name generated code imports the runtime from (optional).</summary>
    public const string ModuleNameOption = "ModuleName";

    private static readonly string[] Options = { ModuleNameOption };

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
    };

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

    /// <summary>One module and its declarations per export: <c>ChatServer.js</c>, <c>ChatServer.d.ts</c>.</summary>
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
                    return Prim(type.Prim);
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
