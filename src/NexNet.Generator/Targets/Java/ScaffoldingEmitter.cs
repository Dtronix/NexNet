using System.Security.Cryptography;
using System.Text;
using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.Java;

/// <summary>The class names of one export's test scaffolding: <c>&lt;Stem&gt;ScriptableNexus</c> and <c>&lt;Stem&gt;Scaffolding</c>.</summary>
internal sealed class ScaffoldingNames
{
    private ScaffoldingNames(string stem)
    {
        ScriptableNexus = stem + "ScriptableNexus";
        Scaffolding = stem + "Scaffolding";
    }

    /// <summary><c>&lt;Stem&gt;ScriptableNexus</c>: the client nexus whose calls run scripts in the language host.</summary>
    public string ScriptableNexus { get; }

    /// <summary><c>&lt;Stem&gt;Scaffolding</c>: the export's <c>ScaffoldingExport</c> (invocation by name, codecs).</summary>
    public string Scaffolding { get; }

    public static ScaffoldingNames Of(ExportIr export) => new(JavaExportNames.Stem(export.Name));
}

/// <summary>
/// Emits the Java test scaffolding (K11, <see cref="EmitMode.TestScaffolding"/>) that the JVM language host of the
/// cross-language test harness (K12, control protocol <c>docs/internals/interop-control-protocol.md</c>) builds on. All
/// of it goes in the <c>scaffolding</c> sub-package of the configured package:
/// <list type="bullet">
/// <item><c>&lt;Stem&gt;ScriptableNexus extends &lt;Stem&gt;ClientNexusBase</c>: every client nexus method (and
/// <c>onConnected</c>/<c>onDisconnected</c>) hands its typed-JSON arguments to a <c>ScriptHost</c>, which runs the slot's
/// script, and converts the typed-JSON result back.</item>
/// <item><c>&lt;Stem&gt;Scaffolding extends ScaffoldingExport</c>: <c>invokeByName(proxy, method, args, pipe, token)</c>
/// with typed-JSON arguments and result, collections by name, method/collection/slot tables and client factories.</item>
/// <item><c>TestCodecs</c>: a <c>TypedCodec</c> per type the exports use (typed JSON ↔ Java values through the generated
/// formatters), the declared type schemas, and <c>forType(expression)</c> for channel item types.</item>
/// <item><c>Scaffolding</c>: <c>SCHEMA_HASH</c> (the lower-case hex SHA-256 of the <c>nexnet-ir.json</c> bytes, control
/// protocol §4.6) and the exports by nexus pair.</item>
/// <item>The fixed sources of <see cref="ScaffoldingRuntime"/>: <c>TypedJson</c>, <c>TypedCodec</c>, <c>ScriptHost</c>,
/// <c>ScriptCall</c>, <c>ScaffoldingExport</c>.</item>
/// </list>
/// Script semantics (steps, <c>script.call</c> events, signals) live in the host; the generated code only adapts the
/// typed nexus API to typed JSON.
/// </summary>
internal static class ScaffoldingEmitter
{
    /// <summary>The sub-package of the scaffolding.</summary>
    public const string SubPackage = "scaffolding";

    /// <summary>The class of the codecs.</summary>
    public const string CodecsClass = "TestCodecs";

    /// <summary>The class of the schema hash and the export index.</summary>
    public const string IndexClass = "Scaffolding";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The scaffolding files, in path order.</summary>
    /// <param name="context">The emission context.</param>
    /// <param name="decls">The declared types the Java target emits.</param>
    /// <param name="packagePath">The path of the configured package (<c>com/acme/</c>).</param>
    public static IEnumerable<GeneratedFile> Emit(JavaContext context, IReadOnlyList<TypeDeclIr> decls, string packagePath)
    {
        var package = context.Package + "." + SubPackage;
        var directory = packagePath + SubPackage + "/";
        var exports = context.Ir.Exports.Where(e => (e.Targets & (int)NexusTargets.Java) != 0).ToArray();
        var codecs = new CodecTable(context);

        var files = new List<GeneratedFile>();
        foreach (var export in exports)
        {
            var names = ScaffoldingNames.Of(export);
            files.Add(new GeneratedFile(directory + names.ScriptableNexus + ".java", ScriptableNexus(context, export, package, codecs)));
            files.Add(new GeneratedFile(directory + names.Scaffolding + ".java", ExportClass(context, export, package, codecs)));
        }

        files.Add(new GeneratedFile(directory + CodecsClass + ".java", codecs.Write(package, decls)));
        files.Add(new GeneratedFile(directory + IndexClass + ".java", Index(context, exports, package)));
        // The sources are raw string literals: normalize the line endings a CRLF checkout gives them.
        foreach (var (name, text) in ScaffoldingRuntime.Files)
        {
            var content = text.Replace("\r\n", "\n").Replace(ScaffoldingRuntime.PackagePlaceholder, package);
            files.Add(new GeneratedFile(directory + name + ".java", JavaSyntax.Header + "\n" + content + "\n"));
        }

        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The lower-case hex SHA-256 of the UTF-8 bytes of the IR JSON, as written to <c>nexnet-ir.json</c>.</summary>
    internal static string SchemaHash(NexusIr ir)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Utf8.GetBytes(IrJson.Write(ir)));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ scriptable nexus

    private static string ScriptableNexus(JavaContext context, ExportIr export, string package, CodecTable codecs)
    {
        var api = export.ClientApi;
        var names = JavaExportNames.Of(export);
        var own = ScaffoldingNames.Of(export);
        var imports = new ProxyEmitter.Imports();
        imports.Add("com.dtronix.nexnet.CancellationToken");
        imports.Add("java.util.concurrent.CompletableFuture");

        var body = new CodeWriter();
        foreach (var method in api.Methods)
        {
            body.Line();
            var parameters = string.Join(", ", method.Params.Select(p => ProxyEmitter.ParamType(context, method, p, imports) + " " + ProxyEmitter.ParamName(context, p)));
            var returnType = ProxyEmitter.ReturnType(context, method, imports);
            var name = ProxyEmitter.MethodName(context, method.Name, ClientNexusEmitter.ReservedMembers);

            var values = ProxyEmitter.WireOrder(method)
                .Where(p => p.Param.Kind == ParamKind.Value)
                .Select(p => codecs.For(ProxyEmitter.Value(p.Param)) + ".toJson(" + ProxyEmitter.ParamName(context, p.Param) + ")");
            var token = method.Params.FirstOrDefault(p => p.Kind == ParamKind.CancellationToken);
            var pipe = method.Params.FirstOrDefault(p => p.Kind is ParamKind.DuplexPipe or ParamKind.DuplexChannel);
            var item = pipe is { Kind: ParamKind.DuplexChannel } ? codecs.For(ProxyEmitter.ChannelItem(method, pipe)) : "null";
            var call = "ScriptCall.run(host$, " + JavaSyntax.StringLiteral(method.Name) + ", ScriptCall.args(" + string.Join(", ", values) + "), "
                       + (token == null ? "CancellationToken.NONE" : ProxyEmitter.ParamName(context, token)) + ", "
                       + (pipe == null ? "null" : ProxyEmitter.ParamName(context, pipe)) + ", " + item + ")";

            body.Line("@Override");
            using (body.Block("protected " + returnType + " " + name + "(" + parameters + ")"))
            {
                if (method.PipeKind == PipeKind.None && method.ReturnKind == ReturnKind.Void)
                    body.Line(call + ";");
                else if (method.PipeKind != PipeKind.None || method.ReturnKind == ReturnKind.Task || method.ReturnType == null)
                    body.Line("return " + call + ".thenApply(r$ -> null);");
                else
                    body.Line("return " + call + ".thenApply(" + codecs.For(method.ReturnType) + "::fromJson);");
            }
        }

        body.Line();
        body.Line("@Override");
        using (body.Block("protected CompletableFuture<Void> onConnected(boolean isReconnected)"))
        {
            body.Line("ScriptCall.run(host$, ScriptCall.ON_CONNECTED, ScriptCall.args(isReconnected), CancellationToken.NONE, null, null);");
            body.Line("return CompletableFuture.completedFuture(null);");
        }

        body.Line();
        body.Line("@Override");
        using (body.Block("protected CompletableFuture<Void> onDisconnected()"))
        {
            body.Line("ScriptCall.run(host$, ScriptCall.ON_DISCONNECTED, ScriptCall.args(), CancellationToken.NONE, null, null);");
            body.Line("return CompletableFuture.completedFuture(null);");
        }

        var w = new CodeWriter();
        w.Line(JavaSyntax.Header);
        w.Line("package " + package + ";");
        w.Line();
        imports.Write(w);
        JavaSyntax.Javadoc(w, "The scriptable client nexus " + ProxyEmitter.ClrCode(api.ClrName) + " of the cross-language test harness.",
            "<p>Every call hands its typed-JSON arguments to the {@link ScriptHost}, which runs the slot's script, and completes",
            "with the typed-JSON result converted back. {@code onConnected} and {@code onDisconnected} run their slots without",
            "waiting for them. Fire-and-forget methods drop their result and errors.");
        using (w.Block("public final class " + own.ScriptableNexus + " extends " + context.Package + "." + names.ClientNexusBase))
        {
            w.Line("private final ScriptHost host$;");
            w.Line();
            JavaSyntax.Javadoc(w, "Creates the nexus.", "@param host runs the scripts of its calls");
            using (w.Block("public " + own.ScriptableNexus + "(ScriptHost host)"))
                w.Line("this.host$ = host;");
            w.Lines(body.ToString().TrimEnd('\n'));
        }

        return w.ToString();
    }

    // ------------------------------------------------------------------ export class

    private static string ExportClass(JavaContext context, ExportIr export, string package, CodecTable codecs)
    {
        var server = export.ServerApi;
        var names = JavaExportNames.Of(export);
        var own = ScaffoldingNames.Of(export);
        var proxy = context.Package + "." + names.Proxy;

        var imports = new ProxyEmitter.Imports();
        foreach (var name in new[]
                 {
                     "com.dtronix.nexnet.CancellationToken", "com.dtronix.nexnet.collections.NexusList",
                     "com.dtronix.nexnet.spi.ClientNexusBase", "com.dtronix.nexnet.spi.HandshakeDescriptor",
                     "com.dtronix.nexnet.spi.ProxyInvoker", "java.util.Arrays", "java.util.Collections", "java.util.List",
                     "java.util.concurrent.CompletableFuture", "java.util.function.Function", "org.jspecify.annotations.Nullable",
                 })
            imports.Add(name);

        var methods = server.Methods.Select(m => "new Method(" + JavaSyntax.StringLiteral(m.Name) + ", " + JavaSyntax.Int(m.Id) + ", ReturnKind."
            + ReturnKindName(m) + ", PipeKind." + PipeKindName(m.PipeKind) + ", " + (m.HasCancellation ? "true" : "false") + ", "
            + CodecList(ProxyEmitter.WireOrder(m).Where(p => p.Param.Kind == ParamKind.Value).Select(p => codecs.For(ProxyEmitter.Value(p.Param))).ToArray()) + ", "
            + (ReturnKindName(m) == "TASK_OF" ? codecs.For(m.ReturnType!) : "null") + ", "
            + (m.PipeKind == PipeKind.Channel ? codecs.For(ChannelItem(m)) : "null") + ")").ToArray();

        var collections = server.Collections.Where(c => c.Item != null).ToArray();
        if (collections.Length > 0)
            imports.Add("com.dtronix.nexnet.collections.NexusCollectionMode");
        var collectionItems = collections.Select(c => "new Collection(" + JavaSyntax.StringLiteral(c.Name) + ", " + JavaSyntax.Int(c.Id)
            + ", NexusCollectionMode." + CollectionMode(c.Mode) + ", " + codecs.For(c.Item!) + ")").ToArray();

        var slots = export.ClientApi.Methods.Select(m => JavaSyntax.StringLiteral(m.Name))
            .Concat(new[] { "ScriptCall.ON_CONNECTED", "ScriptCall.ON_DISCONNECTED" });

        var w = new CodeWriter();
        w.Line(JavaSyntax.Header);
        w.Line("package " + package + ";");
        w.Line();
        imports.Write(w);
        JavaSyntax.Javadoc(w, "The test scaffolding of the NexNet export " + ProxyEmitter.ClrCode(ProxyEmitter.ExportName(export)) + ": "
                              + ProxyEmitter.ClrCode(export.ClientApi.ClrName) + " / " + ProxyEmitter.ClrCode(server.ClrName) + ".");
        using (w.Block("public final class " + own.Scaffolding + " extends ScaffoldingExport<" + proxy + ">"))
        {
            WriteList(w, "Method", "METHODS", methods);
            WriteList(w, "Collection", "COLLECTIONS", collectionItems);
            WriteList(w, "String", "SLOTS", slots.ToArray());

            JavaSyntax.Javadoc(w, "The instance.");
            w.Line("public static final " + own.Scaffolding + " INSTANCE = new " + own.Scaffolding + "();");
            w.Line();
            using (w.Block("private " + own.Scaffolding + "()"))
            {
            }

            void Getter(string type, string name, string value)
            {
                w.Line();
                w.Line("@Override");
                using (w.Block("public " + type + " " + name + "()"))
                    w.Line("return " + value + ";");
            }

            Getter("String", "clientNexus", JavaSyntax.StringLiteral(export.ClientApi.ClrName));
            Getter("String", "serverNexus", JavaSyntax.StringLiteral(server.ClrName));
            Getter("Class<" + proxy + ">", "proxyType", proxy + ".class");
            Getter("HandshakeDescriptor", "handshake", context.Package + "." + names.Handshake + ".DESCRIPTOR");
            Getter("Function<ProxyInvoker, " + proxy + ">", "proxyFactory", proxy + "::new");
            Getter("List<String>", "slots", "SLOTS");
            Getter("List<Method>", "methods", "METHODS");
            Getter("List<Collection>", "collections", "COLLECTIONS");

            w.Line();
            w.Line("@Override");
            using (w.Block("public ClientNexusBase<" + proxy + "> createNexus(ScriptHost host)"))
                w.Line("return new " + own.ScriptableNexus + "(host);");

            w.Line();
            w.Line("@Override");
            w.Line("public CompletableFuture<@Nullable Object> invokeByName(" + proxy + " proxy, String method, List<@Nullable Object> arguments,");
            w.Line("                                                       @Nullable Object pipe, CancellationToken cancellationToken) {");
            w.Indent();
            using (w.Block("switch (method)"))
            {
                foreach (var method in server.Methods)
                {
                    using (w.Block("case " + JavaSyntax.StringLiteral(method.Name) + ":"))
                        WriteInvokeCase(w, context, method, codecs);
                }

                w.Line("default:");
                w.Indent().Line("throw unknown(\"method\", method, toString());").Unindent();
            }

            w.Unindent();
            w.Line("}");

            w.Line();
            w.Line("@Override");
            using (w.Block("public NexusList<?> list(" + proxy + " proxy, String collection)"))
            {
                using (w.Block("switch (collection)"))
                {
                    foreach (var collection in collections)
                    {
                        w.Line("case " + JavaSyntax.StringLiteral(collection.Name) + ":");
                        w.Indent().Line("return proxy." + ProxyEmitter.MethodName(context, collection.Name, ProxyEmitter.ReservedMembers) + "();").Unindent();
                    }

                    w.Line("default:");
                    w.Indent().Line("throw unknown(\"collection\", collection, toString());").Unindent();
                }
            }
        }

        return w.ToString();
    }

    private static void WriteInvokeCase(CodeWriter w, JavaContext context, MethodIr method, CodecTable codecs)
    {
        var values = ProxyEmitter.WireOrder(method).Where(p => p.Param.Kind == ParamKind.Value).Select(p => p.Param).ToList();
        w.Line("expectArguments(method, arguments, " + JavaSyntax.Int(values.Count) + ");");

        var isPipe = method.PipeKind != PipeKind.None;
        var args = method.Params
            .Where(p => !(isPipe && p.Kind == ParamKind.CancellationToken))
            .Select(p => p.Kind switch
            {
                ParamKind.CancellationToken => "cancellationToken",
                ParamKind.DuplexPipe => "pipe(pipe)",
                ParamKind.DuplexChannel => "channel(pipe)",
                _ => codecs.For(ProxyEmitter.Value(p)) + ".fromJson(arguments.get(" + JavaSyntax.Int(values.IndexOf(p)) + "))",
            });
        var call = "proxy." + ProxyEmitter.MethodName(context, method.Name, ProxyEmitter.ReservedMembers) + "(" + string.Join(", ", args) + ")";

        switch (ReturnKindName(method))
        {
            case "VOID":
                w.Line(call + ";");
                w.Line("return sent();");
                break;
            case "TASK":
                w.Line("return " + call + ".thenApply(ScaffoldingExport::nothing);");
                break;
            default:
                w.Line("return " + call + ".thenApply(" + codecs.For(method.ReturnType!) + "::toJson);");
                break;
        }
    }

    private static void WriteList(CodeWriter w, string type, string name, string[] items)
    {
        if (items.Length == 0)
        {
            w.Line("private static final List<" + type + "> " + name + " = Collections.emptyList();");
            w.Line();
            return;
        }

        w.Line("private static final List<" + type + "> " + name + " = Collections.unmodifiableList(Arrays.asList(");
        w.Indent();
        for (var i = 0; i < items.Length; i++)
            w.Line(items[i] + (i == items.Length - 1 ? "));" : ","));
        w.Unindent();
        w.Line();
    }

    private static string CodecList(string[] codecs)
        => codecs.Length == 0
            ? "Collections.<TypedCodec<?>>emptyList()"
            : "Arrays.<TypedCodec<?>>asList(" + string.Join(", ", codecs) + ")";

    /// <summary>The Java <c>ReturnKind</c>: pipe methods and value-less results complete like <c>TASK</c>.</summary>
    private static string ReturnKindName(MethodIr method)
    {
        if (method.PipeKind != PipeKind.None)
            return "TASK";
        return method.ReturnKind switch
        {
            ReturnKind.Void => "VOID",
            ReturnKind.TaskOf when method.ReturnType != null => "TASK_OF",
            _ => "TASK",
        };
    }

    private static string PipeKindName(PipeKind kind) => kind switch
    {
        PipeKind.DuplexPipe => "PIPE",
        PipeKind.Channel => "CHANNEL",
        _ => "NONE",
    };

    private static TypeRef ChannelItem(MethodIr method)
    {
        var param = method.Params.First(p => p.Kind == ParamKind.DuplexChannel);
        return ProxyEmitter.ChannelItem(method, param);
    }

    private static string CollectionMode(CollectionMode mode) => mode switch
    {
        Ir.CollectionMode.ServerToClient => "SERVER_TO_CLIENT",
        Ir.CollectionMode.BiDirectional => "BI_DIRECTIONAL",
        Ir.CollectionMode.Relay => "RELAY",
        _ => "UNSET",
    };

    // ------------------------------------------------------------------ index

    private static string Index(JavaContext context, IReadOnlyList<ExportIr> exports, string package)
    {
        var w = new CodeWriter();
        w.Line(JavaSyntax.Header);
        w.Line("package " + package + ";");
        w.Line();
        w.Line("import java.util.Arrays;");
        w.Line("import java.util.Collections;");
        w.Line("import java.util.List;");
        w.Line("import org.jspecify.annotations.Nullable;");
        w.Line();
        JavaSyntax.Javadoc(w, "The test scaffolding of this package: the schema hash and the exported nexus pairs.");
        using (w.Block("public final class " + IndexClass))
        {
            JavaSyntax.Javadoc(w, "The {@code schemaHash} of the language host's {@code hello} (control protocol &#167;4.6): the lower-case hex SHA-256 of the {@code nexnet-ir.json} this code was generated from.");
            w.Line("public static final String SCHEMA_HASH = " + JavaSyntax.StringLiteral(SchemaHash(context.Ir)) + ";");
            w.Line();
            JavaSyntax.Javadoc(w, "The exports, in IR order.");
            var items = exports.Select(e => ScaffoldingNames.Of(e).Scaffolding + ".INSTANCE").ToArray();
            w.Line(items.Length == 0
                ? "public static final List<ScaffoldingExport<?>> EXPORTS = Collections.emptyList();"
                : "public static final List<ScaffoldingExport<?>> EXPORTS = Collections.unmodifiableList(Arrays.<ScaffoldingExport<?>>asList(");
            if (items.Length > 0)
            {
                w.Indent();
                for (var i = 0; i < items.Length; i++)
                    w.Line(items[i] + (i == items.Length - 1 ? "));" : ","));
                w.Unindent();
            }

            w.Line();
            using (w.Block("private " + IndexClass + "()"))
            {
            }

            w.Line();
            JavaSyntax.Javadoc(w, "The export of a nexus pair, as {@code client.create} names it.",
                "@param clientNexus the full metadata name of the C# client nexus interface",
                "@param serverNexus the full metadata name of the C# server nexus interface",
                "@return the export, or null when this package has none for the pair");
            using (w.Block("public static @Nullable ScaffoldingExport<?> find(String clientNexus, String serverNexus)"))
            {
                using (w.Block("for (ScaffoldingExport<?> export : EXPORTS)"))
                {
                    using (w.Block("if (export.clientNexus().equals(clientNexus) && export.serverNexus().equals(serverNexus))"))
                        w.Line("return export;");
                }

                w.Line("return null;");
            }
        }

        return w.ToString();
    }

    // ------------------------------------------------------------------ codecs

    /// <summary>The golden type expression of an IR type, declared types by id (as the C# runner prints channel types).</summary>
    internal static string Expression(TypeRef type)
    {
        string Arg(int i) => i < type.Args.Length ? Expression(type.Args[i]) : throw new InvalidOperationException("Malformed type reference " + type.Kind + ".");
        string Generic(string name) => name + "<" + string.Join(",", type.Args.Select(Expression)) + ">";

        return type.Kind switch
        {
            TypeRefKind.Prim => PrimName(type.Prim),
            TypeRefKind.Bin => "byte[]",
            TypeRefKind.PrimArray => PrimName(type.Prim) + "[]",
            TypeRefKind.Seq when type.Clr == "Array" => Arg(0) + "[]",
            TypeRefKind.Seq => Generic(type.Clr ?? "IEnumerable"),
            TypeRefKind.Set => Generic("HashSet"),
            TypeRefKind.Queue => Generic("Queue"),
            TypeRefKind.Stack => Generic("Stack"),
            TypeRefKind.Map => Generic("Dictionary"),
            TypeRefKind.Tuple => "(" + string.Join(",", type.Args.Select(Expression)) + ")",
            TypeRefKind.Nullable => Arg(0) + "?",
            TypeRefKind.Decl => type.Args.Length == 0 ? type.DeclId ?? "" : Generic(type.DeclId ?? ""),
            TypeRefKind.TypeParam => "$" + JavaSyntax.Int(type.Ordinal),
            _ => type.Clr ?? "raw",
        };
    }

    private static string PrimName(PrimKind prim) => prim switch
    {
        PrimKind.Bool => "bool",
        PrimKind.SByte => "sbyte",
        PrimKind.Byte => "byte",
        PrimKind.Int16 => "short",
        PrimKind.UInt16 => "ushort",
        PrimKind.Int32 => "int",
        PrimKind.UInt32 => "uint",
        PrimKind.Int64 => "long",
        PrimKind.UInt64 => "ulong",
        PrimKind.Single => "float",
        PrimKind.Double => "double",
        PrimKind.Half => "half",
        PrimKind.Char => "char",
        PrimKind.Decimal => "decimal",
        PrimKind.String => "string",
        _ => prim.ToString(),
    };

    /// <summary>The <c>TypedJson.Prim</c> constant of a leaf.</summary>
    private static string PrimConstant(PrimKind prim) => "TypedJson.Prim." + prim switch
    {
        PrimKind.Bool => "BOOL",
        PrimKind.SByte => "SBYTE",
        PrimKind.Byte => "BYTE",
        PrimKind.Int16 => "INT16",
        PrimKind.UInt16 => "UINT16",
        PrimKind.Int32 => "INT32",
        PrimKind.UInt32 => "UINT32",
        PrimKind.Int64 => "INT64",
        PrimKind.UInt64 => "UINT64",
        PrimKind.Single => "SINGLE",
        PrimKind.Double => "DOUBLE",
        PrimKind.Half => "HALF",
        PrimKind.Char => "CHAR",
        PrimKind.Decimal => "DECIMAL",
        PrimKind.String => "STRING",
        PrimKind.DateTime => "DATE_TIME",
        PrimKind.DateTimeOffset => "DATE_TIME_OFFSET",
        PrimKind.TimeSpan => "TIME_SPAN",
        PrimKind.DateOnly => "DATE_ONLY",
        PrimKind.TimeOnly => "TIME_ONLY",
        PrimKind.Guid => "GUID",
        PrimKind.BigInteger => "BIG_INTEGER",
        PrimKind.Uri => "URI",
        PrimKind.Version => "VERSION",
        _ => throw new InvalidOperationException("No typed-JSON primitive for " + prim + "."),
    };

    /// <summary>The Java expression of the <c>TypedJson.Type</c> of an IR type.</summary>
    internal static string Shape(TypeRef type)
    {
        string Arg(int i) => i < type.Args.Length ? Shape(type.Args[i]) : throw new InvalidOperationException("Malformed type reference " + type.Kind + ".");
        string Args() => string.Concat(type.Args.Select(a => ", " + Shape(a)));
        string Clr(string fallback) => JavaSyntax.StringLiteral(type.Clr ?? fallback);

        return type.Kind switch
        {
            TypeRefKind.Prim => "TypedJson.prim(" + PrimConstant(type.Prim) + ")",
            TypeRefKind.Bin => "TypedJson.bin()",
            TypeRefKind.PrimArray => "TypedJson.primArray(" + PrimConstant(type.Prim) + ")",
            TypeRefKind.Seq => "TypedJson.seq(" + (type.Clr == null ? "null" : JavaSyntax.StringLiteral(type.Clr)) + ", " + Arg(0) + ")",
            TypeRefKind.Set => "TypedJson.set(" + Arg(0) + ")",
            TypeRefKind.Queue => "TypedJson.queue(" + Arg(0) + ")",
            TypeRefKind.Stack => "TypedJson.stack(" + Arg(0) + ")",
            TypeRefKind.Map => "TypedJson.map(" + Arg(0) + ", " + Arg(1) + ")",
            TypeRefKind.Tuple => "TypedJson.tuple(" + Clr("Tuple") + Args() + ")",
            TypeRefKind.Nullable => "TypedJson.nullable(" + Arg(0) + ")",
            TypeRefKind.Decl => "TypedJson.decl(" + JavaSyntax.StringLiteral(type.DeclId ?? "") + Args() + ")",
            TypeRefKind.TypeParam => "TypedJson.typeParam(" + JavaSyntax.Int(type.Ordinal) + ")",
            TypeRefKind.External => "TypedJson.external(" + Clr("raw") + ")",
            _ => throw new InvalidOperationException("Unknown type reference kind " + type.Kind + "."),
        };
    }

    /// <summary>The <c>TypedCodec</c> fields of <c>TestCodecs</c>: one per distinct (Java type, formatter, shape).</summary>
    private sealed class CodecTable
    {
        private readonly JavaContext _context;
        private readonly FormatterExpressions _formatters;
        private readonly List<(string JavaType, string Expression, string Formatter, string Shape)> _codecs = new();
        private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);

        public CodecTable(JavaContext context)
        {
            _context = context;
            _formatters = new FormatterExpressions(context.Scope, context.FormattersClass, context.UserFormattersClass);
        }

        /// <summary>The expression of the codec of <paramref name="type"/>: <c>TestCodecs.C$n</c>.</summary>
        public string For(TypeRef type)
        {
            var javaType = JavaTarget.Box(_context.Qualified.Map(type, _context.Scope).Name);
            var formatter = _formatters.For(type);
            var shape = Shape(type);
            var key = javaType + "|" + formatter + "|" + shape;
            if (!_byKey.TryGetValue(key, out var field))
            {
                field = CodecsClass + ".C$" + JavaSyntax.Int(_codecs.Count);
                _codecs.Add((javaType, Expression(type), formatter, shape));
                _byKey[key] = field;
            }

            return field;
        }

        public string Write(string package, IReadOnlyList<TypeDeclIr> decls)
        {
            var w = new CodeWriter();
            w.Line(JavaSyntax.Header);
            w.Line("package " + package + ";");
            w.Line();
            w.Line("import org.jspecify.annotations.Nullable;");
            w.Line();
            JavaSyntax.Javadoc(w, "The typed-JSON codecs of the types the exports of this package use, and the declared type schemas.");
            using (w.Block("public final class " + CodecsClass))
            {
                JavaSyntax.Javadoc(w, "The declared types, by id.");
                var ordered = decls.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
                if (ordered.Length == 0)
                {
                    w.Line("public static final TypedJson.Schemas SCHEMAS = new TypedJson.Schemas();");
                }
                else
                {
                    w.Line("public static final TypedJson.Schemas SCHEMAS = new TypedJson.Schemas(");
                    w.Indent();
                    var byId = ordered.ToDictionary(d => d.Id, StringComparer.Ordinal);
                    for (var i = 0; i < ordered.Length; i++)
                        w.Line(Decl(ordered[i], byId) + (i == ordered.Length - 1 ? ");" : ","));
                    w.Unindent();
                }

                for (var i = 0; i < _codecs.Count; i++)
                {
                    var (javaType, expression, formatter, shape) = _codecs[i];
                    w.Line();
                    JavaSyntax.Javadoc(w, ProxyEmitter.ClrCode(expression) + ".");
                    w.Line("public static final TypedCodec<" + javaType + "> C$" + JavaSyntax.Int(i) + " = new TypedCodec<>(");
                    w.Indent();
                    w.Line(JavaSyntax.StringLiteral(expression) + ",");
                    w.Line(formatter + ",");
                    w.Line(shape + ",");
                    w.Line("SCHEMAS);");
                    w.Unindent();
                }

                w.Line();
                using (w.Block("private " + CodecsClass + "()"))
                {
                }

                w.Line();
                JavaSyntax.Javadoc(w, "The codec of a golden type expression ({@code channel.writer}/{@code channel.reader}).",
                    "@param expression the expression, declared types by full metadata name",
                    "@return the codec, or null when no export of this package uses the type");
                using (w.Block("public static @Nullable TypedCodec<?> forType(String expression)"))
                {
                    using (w.Block("switch (expression)"))
                    {
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        for (var i = 0; i < _codecs.Count; i++)
                        {
                            if (!seen.Add(_codecs[i].Expression))
                                continue;
                            w.Line("case " + JavaSyntax.StringLiteral(_codecs[i].Expression) + ":");
                            w.Indent().Line("return C$" + JavaSyntax.Int(i) + ";").Unindent();
                        }

                        w.Line("default:");
                        w.Indent().Line("return null;").Unindent();
                    }
                }
            }

            return w.ToString();
        }

        private static string Decl(TypeDeclIr decl, Dictionary<string, TypeDeclIr> byId)
        {
            var id = JavaSyntax.StringLiteral(decl.Id);
            switch (decl)
            {
                case ObjectDeclIr obj:
                {
                    // Members of the base types are part of the same array.
                    var members = new List<MemberIr>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (var current = obj; current != null && seen.Add(current.Id);
                         current = current.BaseDeclId != null && byId.TryGetValue(current.BaseDeclId, out var b) ? b as ObjectDeclIr : null)
                        members.AddRange(current.Members);

                    var list = string.Concat(members.OrderBy(m => m.Key).Select(m => ", TypedJson.member(" + JavaSyntax.Int(m.Key) + ", " + Shape(m.Type) + ")"));
                    return "TypedJson.object(" + id + ", " + JavaSyntax.Int(obj.ArrayLength) + ", " + (obj.IsStruct ? "true" : "false") + list + ")";
                }
                case UnionDeclIr union:
                    return "TypedJson.union(" + id + string.Concat(union.Cases.Select(c => ", " + JavaSyntax.Int(c.Tag) + ", " + JavaSyntax.StringLiteral(c.CaseDeclId))) + ")";
                case EnumDeclIr enumDecl:
                    return "TypedJson.enumOf(" + id + ", " + PrimConstant(enumDecl.Underlying) + ")";
                default:
                    throw new InvalidOperationException("Unknown declaration " + decl.GetType().Name + ".");
            }
        }
    }
}
