using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.Java;

/// <summary>
/// The class names of one export's Java files. The stem is the export name without a trailing <c>ServerNexus</c> or
/// <c>Nexus</c> (<c>ChatServerNexus</c> → <c>Chat</c>), and the files are <c>ChatHandshake</c>, <c>ChatServerProxy</c>,
/// <c>ChatClientNexusBase</c> and <c>ChatClient</c>. Two exports with the same stem in one package are NEXNET047.
/// </summary>
internal sealed class JavaExportNames
{
    private JavaExportNames(string stem)
    {
        Handshake = stem + "Handshake";
        Proxy = stem + "ServerProxy";
        ClientNexusBase = stem + "ClientNexusBase";
        Client = stem + "Client";
    }

    /// <summary><c>&lt;Stem&gt;Handshake</c>: the <c>HandshakeDescriptor</c> constant.</summary>
    public string Handshake { get; }

    /// <summary><c>&lt;Stem&gt;ServerProxy</c>: the server proxy.</summary>
    public string Proxy { get; }

    /// <summary><c>&lt;Stem&gt;ClientNexusBase</c>: the base class of the user's client nexus.</summary>
    public string ClientNexusBase { get; }

    /// <summary><c>&lt;Stem&gt;Client</c>: the factory.</summary>
    public string Client { get; }

    /// <summary>Every class, in emission order.</summary>
    public IEnumerable<string> All => new[] { Handshake, Proxy, ClientNexusBase, Client };

    public static JavaExportNames Of(ExportIr export) => new(Stem(export.Name));

    internal static string Stem(string exportName)
    {
        foreach (var suffix in new[] { "ServerNexus", "Nexus" })
        {
            if (exportName.Length > suffix.Length && exportName.EndsWith(suffix, StringComparison.Ordinal))
                return exportName.Substring(0, exportName.Length - suffix.Length);
        }

        return exportName;
    }
}

/// <summary>
/// Emits the client-side classes of one export (K04, runtime contract §3.2–3.5): <c>&lt;Stem&gt;Handshake</c> (the
/// greeting constants from the IR), <c>&lt;Stem&gt;ServerProxy extends ProxyBase</c> (a method per server method and an
/// accessor per synchronized collection) and the <c>&lt;Stem&gt;Client</c> factory. The client nexus base comes from
/// <see cref="ClientNexusEmitter"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>C# <c>void</c> → <c>void</c> (<c>invokeVoid</c>); <c>ValueTask</c> → <c>CompletableFuture&lt;Void&gt;</c>
/// (<c>invokeTask</c>); <c>ValueTask&lt;T&gt;</c> → <c>CompletableFuture&lt;T&gt;</c>, boxed (<c>invoke</c>). The runtime's
/// future cancels the call remotely on <c>cancel(true)</c>.</item>
/// <item>A method with a <c>CancellationToken</c> gets two overloads: without the token (it passes
/// <c>CancellationToken.NONE</c>) and with it, last, as in C#.</item>
/// <item>Pipe and channel methods take a <c>NexusDuplexPipe</c> or <c>NexusDuplexChannel&lt;T&gt;</c>, written at their
/// wire position as the initial pipe id.</item>
/// <item>Arguments are a MessagePack array of the serialized parameters in wire order; none is an empty payload.
/// Composed formatters (<c>list(…)</c>) are hoisted into static fields.</item>
/// <item>Generated types are written fully qualified, runtime types are imported, so no generated type can shadow a
/// runtime type. A method or accessor named like a <c>ProxyBase</c> member gets a trailing '_'.</item>
/// </list>
/// </remarks>
internal static class ProxyEmitter
{
    /// <summary>The members of <c>ProxyBase</c> a generated method must not hide or overload.</summary>
    internal static readonly string[] ReservedMembers = { "invoke", "invokeTask", "invokeVoid", "invokePipe", "invokeChannel", "list" };

    /// <summary>
    /// The runtime version the generated code targets (<c>NexusRuntime.VERSION</c> major.minor must match): the version
    /// of this generator, which ships in lockstep with the runtimes.
    /// </summary>
    public static string RuntimeVersion { get; } = ComputeRuntimeVersion();

    private static string ComputeRuntimeVersion()
    {
        var version = typeof(ProxyEmitter).Assembly.GetName().Version;
        return version == null
            ? "0.0.0"
            : JavaSyntax.Int(version.Major) + "." + JavaSyntax.Int(version.Minor) + "." + JavaSyntax.Int(Math.Max(version.Build, 0));
    }

    /// <summary>The files of one export, in <paramref name="directory"/> (the package path).</summary>
    public static IEnumerable<GeneratedFile> Emit(JavaContext context, ExportIr export, string directory)
    {
        var names = JavaExportNames.Of(export);
        yield return new GeneratedFile(directory + names.Handshake + ".java", Handshake(context, export, names));
        yield return new GeneratedFile(directory + names.Proxy + ".java", Proxy(context, export, names));
        yield return new GeneratedFile(directory + names.ClientNexusBase + ".java", ClientNexusEmitter.Emit(context, export, names));
        yield return new GeneratedFile(directory + names.Client + ".java", Factory(context, export, names));
    }

    // ------------------------------------------------------------------ shared with ClientNexusEmitter

    /// <summary>The Java name of a nexus method: the shared mapping, plus '_' when it names a base class member.</summary>
    internal static string MethodName(JavaContext context, string name, string[] reserved)
    {
        var mapped = context.Target.Names.Method(name);
        return Array.IndexOf(reserved, mapped) >= 0 ? mapped + "_" : mapped;
    }

    internal static string ParamName(JavaContext context, ParamIr param) => context.Target.Names.Parameter(param.Name);

    /// <summary>The boxed Java type of a value, with <c>@Nullable</c> when it may be null.</summary>
    internal static string BoxedType(JavaContext context, TypeRef type, bool nullable)
    {
        var boxed = JavaTarget.Box(context.Qualified.Map(type, context.Scope).Name);
        return nullable || type.MayBeNull ? JavaSyntax.Nullable(boxed) : boxed;
    }

    /// <summary>The Java type of a parameter.</summary>
    internal static string ParamType(JavaContext context, MethodIr method, ParamIr param, Imports imports)
    {
        switch (param.Kind)
        {
            case ParamKind.CancellationToken:
                imports.Add("com.dtronix.nexnet.CancellationToken");
                return "CancellationToken";
            case ParamKind.DuplexPipe:
                imports.Add("com.dtronix.nexnet.pipes.NexusDuplexPipe");
                return "NexusDuplexPipe";
            case ParamKind.DuplexChannel:
                imports.Add("com.dtronix.nexnet.pipes.NexusDuplexChannel");
                return "NexusDuplexChannel<" + BoxedType(context, ChannelItem(method, param), false) + ">";
            default:
            {
                var type = context.Qualified.Map(Value(param), context.Scope).Name;
                if (param.Nullable || Value(param).MayBeNull)
                {
                    var annotated = JavaSyntax.Nullable(type);
                    if (annotated != type)
                        imports.Add("org.jspecify.annotations.Nullable");
                    return annotated;
                }

                return type;
            }
        }
    }

    /// <summary>The Java return type of a method.</summary>
    internal static string ReturnType(JavaContext context, MethodIr method, Imports imports)
    {
        if (method.PipeKind == PipeKind.None && method.ReturnKind == ReturnKind.Void)
            return "void";

        imports.Add("java.util.concurrent.CompletableFuture");
        if (method.PipeKind != PipeKind.None || method.ReturnKind == ReturnKind.Task || method.ReturnType == null)
            return "CompletableFuture<Void>";

        var result = BoxedType(context, method.ReturnType, false);
        if (result.IndexOf("@Nullable", StringComparison.Ordinal) >= 0)
            imports.Add("org.jspecify.annotations.Nullable");
        return "CompletableFuture<" + result + ">";
    }

    internal static TypeRef Value(ParamIr param)
        => param.Type ?? throw new InvalidOperationException("The value parameter '" + param.Name + "' has no type.");

    internal static TypeRef ChannelItem(MethodIr method, ParamIr param)
        => param.Type ?? method.PipeType ?? throw new InvalidOperationException("The channel parameter '" + param.Name + "' has no item type.");

    /// <summary>The serialized parameters (with their declaration index) in wire order.</summary>
    internal static IEnumerable<(ParamIr Param, int Index)> WireOrder(MethodIr method)
        => method.Params.Select((p, i) => (Param: p, Index: i)).Where(p => p.Param.WireIndex > 0).OrderBy(p => p.Param.WireIndex);

    /// <summary>The Javadoc summary of a method: its C# docs, or a description.</summary>
    internal static string MethodDoc(MethodIr method, string fallback) => JavaSyntax.Doc(method.Docs, fallback);

    internal static string ClrCode(string text) => "{@code " + JavaSyntax.DocText(text) + "}";

    /// <summary>The import list of one file, written sorted.</summary>
    internal sealed class Imports
    {
        private readonly SortedSet<string> _names = new(StringComparer.Ordinal);

        public void Add(string name) => _names.Add(name);

        public void Write(CodeWriter w)
        {
            if (_names.Count == 0)
                return;
            foreach (var name in _names)
                w.Line("import " + name + ";");
            w.Line();
        }
    }

    /// <summary>
    /// The formatters a class uses: built-in constants inline, anything composed (<c>list(…)</c>, a generic type's
    /// factory) in a <c>private static final</c> field created once.
    /// </summary>
    internal sealed class FormatterFields
    {
        private readonly JavaContext _context;
        private readonly FormatterExpressions _expressions;
        private readonly Dictionary<string, string> _byExpression = new(StringComparer.Ordinal);
        private readonly List<(string Type, string Name, string Expression)> _fields = new();

        public FormatterFields(JavaContext context)
        {
            _context = context;
            _expressions = new FormatterExpressions(context.Scope, context.FormattersClass, context.UserFormattersClass);
        }

        public bool Any => _fields.Count > 0;

        public string For(TypeRef type)
        {
            var expression = _expressions.For(type);
            if (expression.IndexOf('(') < 0)
                return expression;

            if (!_byExpression.TryGetValue(expression, out var name))
            {
                name = "F$" + JavaSyntax.Int(_fields.Count);
                _fields.Add((JavaTarget.Box(_context.Qualified.Map(type, _context.Scope).Name), name, expression));
                _byExpression[expression] = name;
            }

            return name;
        }

        public void Write(CodeWriter w)
        {
            foreach (var (type, name, expression) in _fields)
            {
                w.Line("private static final Formatter<" + type + "> " + name + " =");
                w.Indent().Line(expression + ";").Unindent();
            }

            if (_fields.Count > 0)
                w.Line();
        }
    }

    // ------------------------------------------------------------------ handshake

    private static string Handshake(JavaContext context, ExportIr export, JavaExportNames names)
    {
        var w = new CodeWriter();
        w.Line(JavaSyntax.Header);
        w.Line("package " + context.Package + ";");
        w.Line();
        w.Line("import com.dtronix.nexnet.spi.HandshakeDescriptor;");
        w.Line();
        JavaSyntax.Javadoc(w, "The greeting constants of the NexNet export " + ClrCode(ExportName(export)) + ".",
            "<p>The hashes are generated from the C# nexus interfaces and sent as they are. A server built from different",
            "interfaces refuses the connection.");
        using (w.Block("public final class " + names.Handshake))
        {
            var h = export.Handshake;
            var ids = string.Join(", ", export.ClientAcceptedIds.Select(a => JavaSyntax.Int(a.Id)));
            JavaSyntax.Javadoc(w, "The descriptor passed to {@code NexusClient.create}: runtime " + JavaSyntax.DocText(RuntimeVersion)
                                  + ", " + (h.Version == null ? "an unversioned server API" : "server API version " + ClrCode(h.Version)) + ".");
            w.Line("public static final HandshakeDescriptor DESCRIPTOR = new HandshakeDescriptor(");
            w.Indent();
            w.Line(JavaSyntax.StringLiteral(RuntimeVersion) + ",");
            w.Line((h.Version == null ? "null" : JavaSyntax.StringLiteral(h.Version)) + ",");
            w.Line(JavaSyntax.Int(h.ServerNexusHash) + ",");
            w.Line(JavaSyntax.Int(h.ClientNexusHash) + ",");
            w.Line("new int[] {" + ids + "});");
            w.Unindent();
            w.Line();
            using (w.Block("private " + names.Handshake + "()"))
            {
            }
        }

        return w.ToString();
    }

    internal static string ExportName(ExportIr export)
        => export.ClrNamespace.Length == 0 ? export.Name : export.ClrNamespace + "." + export.Name;

    // ------------------------------------------------------------------ proxy

    private static string Proxy(JavaContext context, ExportIr export, JavaExportNames names)
    {
        var api = export.ServerApi;
        var imports = new Imports();
        imports.Add("com.dtronix.nexnet.spi.ProxyBase");
        imports.Add("com.dtronix.nexnet.spi.ProxyInvoker");
        var fields = new FormatterFields(context);
        var body = new CodeWriter();

        foreach (var method in api.Methods)
        {
            body.Line();
            WriteMethod(body, context, api, method, imports, fields);
        }

        foreach (var collection in api.Collections)
        {
            if (collection.Item == null)
                continue;
            imports.Add("com.dtronix.nexnet.collections.NexusCollectionMode");
            imports.Add("com.dtronix.nexnet.collections.NexusList");
            var item = BoxedType(context, collection.Item, false);
            if (item.IndexOf("@Nullable", StringComparison.Ordinal) >= 0)
                imports.Add("org.jspecify.annotations.Nullable");
            body.Line();
            JavaSyntax.Javadoc(body, JavaSyntax.Doc(collection.Docs, "The synchronized list " + ClrCode(api.Name + "." + collection.Name) + "."),
                "<p>Mode " + Mode(collection.Mode) + ". The same instance is returned on every call; call {@code enable()} to",
                "synchronize it.",
                "",
                "@return the list");
            using (body.Block("public NexusList<" + item + "> " + MethodName(context, collection.Name, ReservedMembers) + "()"))
            {
                body.Line("return list(" + JavaSyntax.Int(collection.Id) + ", NexusCollectionMode." + Mode(collection.Mode) + ", "
                          + fields.For(collection.Item) + ");");
            }
        }

        if (fields.Any)
            imports.Add("com.dtronix.nexnet.serialization.Formatter");

        var w = new CodeWriter();
        w.Line(JavaSyntax.Header);
        w.Line("package " + context.Package + ";");
        w.Line();
        imports.Write(w);
        JavaSyntax.Javadoc(w, "The proxy of the server nexus " + ClrCode(api.ClrName) + ": calls its methods on the server.",
            "<p>Get it from {@code NexusClient.proxy()} or {@code context().proxy()} of the client nexus. Futures complete",
            "on the client's callback executor; {@code cancel(true)} on a returned future cancels the call on the server.");
        using (w.Block("public final class " + names.Proxy + " extends ProxyBase"))
        {
            fields.Write(w);
            JavaSyntax.Javadoc(w, "Creates the proxy. Called by the runtime.", "@param invoker the session side");
            using (w.Block("public " + names.Proxy + "(ProxyInvoker invoker)"))
                w.Line("super(invoker);");
            w.Lines(body.ToString().TrimEnd('\n'));
        }

        return w.ToString();
    }

    private static string Mode(CollectionMode mode) => mode switch
    {
        CollectionMode.ServerToClient => "SERVER_TO_CLIENT",
        CollectionMode.BiDirectional => "BI_DIRECTIONAL",
        CollectionMode.Relay => "RELAY",
        _ => "UNSET",
    };

    private static void WriteMethod(CodeWriter w, JavaContext context, InterfaceIr api, MethodIr method, Imports imports, FormatterFields fields)
    {
        var name = MethodName(context, method.Name, ReservedMembers);
        var returnType = ReturnType(context, method, imports);
        var summary = MethodDoc(method, "Calls " + ClrCode(api.Name + "." + method.Name) + " on the server.");
        var isPipe = method.PipeKind != PipeKind.None;
        var token = isPipe ? null : method.Params.FirstOrDefault(p => p.Kind == ParamKind.CancellationToken);

        string Declare(IEnumerable<ParamIr> ps) => string.Join(", ", ps.Select(p => ParamType(context, method, p, imports) + " " + ParamName(context, p)));
        var returnTag = method.ReturnKind == ReturnKind.Void && !isPipe
            ? Array.Empty<string>()
            : new[] { isPipe ? "@return a future that completes when the invocation is sent" : "@return a future of the server method's completion" };

        if (token != null)
        {
            // The overload without the token.
            var others = method.Params.Where(p => p != token).ToArray();
            JavaSyntax.Javadoc(w, summary, returnTag);
            using (w.Block("public " + returnType + " " + name + "(" + Declare(others) + ")"))
            {
                var args = method.Params.Select(p => p == token ? "CancellationToken.NONE" : ParamName(context, p));
                w.Line("return " + name + "(" + string.Join(", ", args) + ");");
            }

            w.Line();
            JavaSyntax.Javadoc(w, summary, new[]
            {
                "@param " + ParamName(context, token) + " cancels the call: before it is sent nothing is sent, after it the server",
                "    method is canceled",
            }.Concat(returnTag).ToArray());
        }
        else
        {
            JavaSyntax.Javadoc(w, summary, returnTag);
        }

        var parameters = isPipe ? method.Params.Where(p => p.Kind != ParamKind.CancellationToken) : method.Params;
        using (w.Block("public " + returnType + " " + name + "(" + Declare(parameters) + ")"))
        {
            var id = JavaSyntax.Int(method.Id);
            var tokenArg = token == null ? "null" : ParamName(context, token);
            if (isPipe)
            {
                var pipe = method.Params.First(p => p.Kind is ParamKind.DuplexPipe or ParamKind.DuplexChannel);
                var call = method.PipeKind == PipeKind.Channel ? "invokeChannel" : "invokePipe";
                w.Line("return " + call + "(" + id + ", " + ParamName(context, pipe) + ", (writer$, pipeId$) -> {");
                w.Indent();
                WriteArguments(w, context, method, fields, "writer$", "pipeId$");
                w.Unindent();
                w.Line("});");
                return;
            }

            var arguments = ArgumentWriter(w, context, method, fields, imports);
            switch (method.ReturnKind)
            {
                case ReturnKind.Void:
                    w.Line("invokeVoid(" + id + ", " + arguments + ");");
                    break;
                case ReturnKind.Task:
                    w.Line("return invokeTask(" + id + ", " + arguments + ", " + tokenArg + ");");
                    break;
                default:
                    w.Line("return invoke(" + id + ", " + arguments + ", " + fields.For(method.ReturnType!) + ", " + tokenArg + ");");
                    break;
            }
        }
    }

    /// <summary>Writes the argument writer to a local when there are arguments; returns the expression to pass.</summary>
    private static string ArgumentWriter(CodeWriter w, JavaContext context, MethodIr method, FormatterFields fields, Imports imports)
    {
        imports.Add("com.dtronix.nexnet.spi.ArgumentWriter");
        if (method.SerializedCount == 0)
            return "ArgumentWriter.NONE";

        w.Line("ArgumentWriter arguments$ = writer$ -> {");
        w.Indent();
        WriteArguments(w, context, method, fields, "writer$", null);
        w.Unindent();
        w.Line("};");
        return "arguments$";
    }

    private static void WriteArguments(CodeWriter w, JavaContext context, MethodIr method, FormatterFields fields, string writer, string? pipeId)
    {
        w.Line(writer + ".writeArrayHeader(" + JavaSyntax.Int(method.SerializedCount) + ");");
        foreach (var (param, _) in WireOrder(method))
        {
            if (param.Kind is ParamKind.DuplexPipe or ParamKind.DuplexChannel)
                w.Line(writer + ".writeInt(" + (pipeId ?? "0") + ");");
            else if (param.Kind == ParamKind.Value)
                w.Line(fields.For(Value(param)) + ".write(" + writer + ", " + ParamName(context, param) + ");");
        }
    }

    // ------------------------------------------------------------------ factory

    private static string Factory(JavaContext context, ExportIr export, JavaExportNames names)
    {
        var w = new CodeWriter();
        w.Line(JavaSyntax.Header);
        w.Line("package " + context.Package + ";");
        w.Line();
        w.Line("import com.dtronix.nexnet.client.ClientConfig;");
        w.Line("import com.dtronix.nexnet.client.NexusClient;");
        w.Line("import com.dtronix.nexnet.transport.Transport;");
        w.Line();
        JavaSyntax.Javadoc(w, JavaSyntax.Doc(export.Docs, "Clients of the NexNet export " + ClrCode(ExportName(export)) + "."),
            "<p>Creates {@link NexusClient} instances of the export " + ClrCode(ExportName(export)) + ": the proxy is {@link "
            + names.Proxy + "} and the client nexus extends {@link " + names.ClientNexusBase + "}.");
        using (w.Block("public final class " + names.Client))
        {
            using (w.Block("private " + names.Client + "()"))
            {
            }

            w.Line();
            JavaSyntax.Javadoc(w, "Creates a client over the transport the configuration names.",
                "@param config the configuration",
                "@param nexus the client nexus, which the server calls",
                "@return the client, not yet connected");
            using (w.Block("public static NexusClient<" + names.Proxy + "> create(ClientConfig config, " + names.ClientNexusBase + " nexus)"))
                w.Line("return NexusClient.create(config, " + names.Handshake + ".DESCRIPTOR, nexus, " + names.Proxy + "::new);");

            w.Line();
            JavaSyntax.Javadoc(w, "Creates a client over a custom transport, or the default one with extra upgrade headers ({@code NexusConnector.builder()}).",
                "@param config the configuration",
                "@param nexus the client nexus, which the server calls",
                "@param connector opens the transport of each connection attempt",
                "@return the client, not yet connected");
            using (w.Block("public static NexusClient<" + names.Proxy + "> create(ClientConfig config, " + names.ClientNexusBase + " nexus, Transport.Connector connector)"))
                w.Line("return NexusClient.create(config, " + names.Handshake + ".DESCRIPTOR, nexus, " + names.Proxy + "::new, connector);");
        }

        return w.ToString();
    }
}
