using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.JavaScript;

/// <summary>
/// The names one export's module exports. The stem is the export name without a trailing <c>ServerNexus</c> or
/// <c>Nexus</c> (<c>ChatServerNexus</c> → <c>Chat</c>), as in the Java target (K04): <c>ChatHandshake</c>,
/// <c>ChatServerProxy</c>, <c>ChatClientNexusBase</c>, <c>ChatClientBinding</c> and <c>createChatClient</c>. The module
/// itself is <c>&lt;ExportName&gt;.js</c> (<see cref="JavaScriptTarget.ExportFiles"/>).
/// </summary>
internal sealed class JsExportNames
{
    private JsExportNames(string stem)
    {
        Handshake = stem + "Handshake";
        Proxy = stem + "ServerProxy";
        ClientNexusBase = stem + "ClientNexusBase";
        Binding = stem + "ClientBinding";
        Factory = "create" + stem + "Client";
    }

    /// <summary><c>&lt;Stem&gt;Handshake</c>: the <c>HandshakeDescriptor</c> constant.</summary>
    public string Handshake { get; }

    /// <summary><c>&lt;Stem&gt;ServerProxy</c>: the server proxy class.</summary>
    public string Proxy { get; }

    /// <summary><c>&lt;Stem&gt;ClientNexusBase</c>: the base class of the user's client nexus.</summary>
    public string ClientNexusBase { get; }

    /// <summary><c>&lt;Stem&gt;ClientBinding</c>: the <c>ClientBinding</c> (handshake and proxy class), for pools.</summary>
    public string Binding { get; }

    /// <summary><c>create&lt;Stem&gt;Client</c>: the factory.</summary>
    public string Factory { get; }

    /// <summary>Every name the module exports.</summary>
    public IEnumerable<string> All => new[] { Handshake, Proxy, ClientNexusBase, Binding, Factory };

    public static JsExportNames Of(ExportIr export) => new(Stem(export.Name));

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
/// Emits the module of one export (J04, runtime contract §3.4): <c>&lt;ExportName&gt;.js</c> and its declarations
/// <c>&lt;ExportName&gt;.d.ts</c>, holding the handshake constant (the greeting values from the IR, never recomputed), the
/// server proxy, the client nexus base (<see cref="JsClientNexusEmitter"/>), the binding and the factory.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>C# <c>void</c> → <c>void</c> (<c>_invokeVoid</c>); <c>ValueTask</c> → <c>Promise&lt;void&gt;</c> and
/// <c>ValueTask&lt;T&gt;</c> → <c>Promise&lt;T&gt;</c> (<c>_invoke</c>).</item>
/// <item>A method with a <c>CancellationToken</c> takes a last <c>options</c> parameter, <c>{ signal }</c>.</item>
/// <item>Pipe and channel methods take a <c>NexusDuplexPipe</c> or <c>NexusDuplexChannel&lt;T&gt;</c> (from
/// <c>client.createPipe()</c>/<c>createChannel(formatter)</c>), written at their wire position as the initial pipe id.</item>
/// <item>Arguments are one MessagePack array of the serialized parameters in wire order; none is <c>null</c>. Composed
/// formatters are hoisted into module constants (<c>$f0</c>…).</item>
/// <item>Synchronized collections are getters returning <c>NexusList&lt;T&gt;</c>, created with their mode (C-4) and,
/// for primitive numeric items, the formatter of the C# <c>T[]</c> (ext 78 or bin).</item>
/// <item>The JavaScript module imports only <c>$</c>-prefixed names (<c>$rt</c>, <c>$fmt</c>, <c>$user&lt;n&gt;</c>), which no
/// C# name maps to. A method named like a base class member gets a trailing '_'.</item>
/// </list>
/// </remarks>
internal static class JsProxyEmitter
{
    /// <summary>The members of <c>ProxyBase</c> a generated method or getter must not hide.</summary>
    internal static readonly string[] ReservedMembers = { "_session", "_invoke", "_invokeVoid", "_invokePipe", "_invokeChannel", "_collection" };

    /// <summary>The namespace the export module imports the generated formatter module under.</summary>
    internal const string Fmt = "$fmt";

    public static IEnumerable<GeneratedFile> Emit(JsContext context, ExportIr export, IReadOnlyList<JsFormatterEmitter.UserFormatterRef> users)
    {
        var name = context.Target.Names.Type(export.Name);
        var names = JsExportNames.Of(export);
        yield return new GeneratedFile(name + ".js", ModuleJs(context, export, names, users));
        yield return new GeneratedFile(name + ".d.ts", ModuleDts(context, export, names));
    }

    // ------------------------------------------------------------------ shared with JsClientNexusEmitter

    /// <summary>The JavaScript name of a nexus method: the shared mapping, plus '_' when it names a base class member.</summary>
    internal static string MethodName(JsContext context, string name, string[] reserved)
    {
        var mapped = context.Target.Names.Method(name);
        return Array.IndexOf(reserved, mapped) >= 0 ? mapped + "_" : mapped;
    }

    internal static string ParamName(JsContext context, ParamIr param) => context.Target.Names.Parameter(param.Name);

    internal static TypeRef Value(ParamIr param)
        => param.Type ?? throw new InvalidOperationException("The value parameter '" + param.Name + "' has no type.");

    internal static TypeRef ChannelItem(MethodIr method, ParamIr param)
        => param.Type ?? method.PipeType ?? throw new InvalidOperationException("The channel parameter '" + param.Name + "' has no item type.");

    /// <summary>The serialized parameters (with their declaration index) in wire order.</summary>
    internal static IEnumerable<(ParamIr Param, int Index)> WireOrder(MethodIr method)
        => method.Params.Select((p, i) => (Param: p, Index: i)).Where(p => p.Param.WireIndex > 0).OrderBy(p => p.Param.WireIndex);

    internal static string ExportName(ExportIr export)
        => export.ClrNamespace.Length == 0 ? export.Name : export.ClrNamespace + "." + export.Name;

    /// <summary>The TypeScript type of a value.</summary>
    internal static string TsType(JsContext context, TypeRef type) => context.TsType(type, context.Scope);

    /// <summary>The TypeScript type of a pipe or channel parameter.</summary>
    internal static string PipeTsType(JsContext context, MethodIr method, ParamIr param)
        => param.Kind == ParamKind.DuplexPipe
            ? JsSyntax.Rt + ".NexusDuplexPipe"
            : JsSyntax.Rt + ".NexusDuplexChannel<" + TsType(context, ChannelItem(method, param)) + ">";

    /// <summary>The formatter expressions of one export module and what they import.</summary>
    internal sealed class Formatters
    {
        private readonly JsFormatterExpressions _expressions;
        private readonly Dictionary<string, string> _byExpression = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _usedUsers = new(StringComparer.Ordinal);
        private readonly IReadOnlyList<JsFormatterEmitter.UserFormatterRef> _users;

        public Formatters(JsContext context, IReadOnlyList<JsFormatterEmitter.UserFormatterRef> users)
        {
            _users = users;
            _expressions = new JsFormatterExpressions(context.Scope, Fmt + ".", spec =>
            {
                var user = users.FirstOrDefault(u => u.Spec == spec) ?? throw new InvalidOperationException("No user formatter " + spec + ".");
                _usedUsers.Add(user.Spec);
                return user.Alias;
            });
        }

        /// <summary>Composed formatters, as module constants, in order of first use.</summary>
        public List<(string Name, string Expression)> Hoisted { get; } = new();

        /// <summary>True once an expression named a generated formatter.</summary>
        public bool UsesGenerated { get; private set; }

        /// <summary>The user formatters an expression named.</summary>
        public IEnumerable<JsFormatterEmitter.UserFormatterRef> UsedUsers => _users.Where(u => _usedUsers.Contains(u.Spec));

        /// <summary>The formatter of a type: a plain name, or the module constant holding a composed one.</summary>
        public string For(TypeRef type)
        {
            var expression = _expressions.For(type);
            UsesGenerated |= expression.IndexOf(Fmt + ".", StringComparison.Ordinal) >= 0;
            if (JsFormatterExpressions.IsSimple(expression))
                return expression;
            if (!_byExpression.TryGetValue(expression, out var name))
            {
                name = "$f" + JsSyntax.Int(Hoisted.Count);
                Hoisted.Add((name, expression));
                _byExpression[expression] = name;
            }

            return name;
        }
    }

    // ------------------------------------------------------------------ module

    private static string ModuleJs(JsContext context, ExportIr export, JsExportNames names, IReadOnlyList<JsFormatterEmitter.UserFormatterRef> users)
    {
        var formatters = new Formatters(context, users);
        var body = new CodeWriter("  ");
        WriteHandshake(body, export, names);
        body.Line();
        WriteProxyJs(body, context, export, names, formatters);
        body.Line();
        JsClientNexusEmitter.WriteJs(body, context, export, names, formatters);
        body.Line();
        WriteFactoryJs(body, context, export, names);

        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        w.Line("import * as " + JsSyntax.Rt + " from " + JsSyntax.String(context.RuntimeModule) + ";");
        if (formatters.UsesGenerated)
            w.Line("import * as " + Fmt + " from './" + JsSyntax.FormattersModule + ".js';");
        foreach (var user in formatters.UsedUsers)
        {
            w.Line(user.Export == null
                ? "import " + user.Alias + " from " + JsSyntax.String(user.Module) + ";"
                : "import { " + user.Export + " as " + user.Alias + " } from " + JsSyntax.String(user.Module) + ";");
        }

        if (formatters.Hoisted.Count > 0)
        {
            w.Line();
            w.Line("// Formatters composed from the generated and runtime formatters.");
            foreach (var (name, expression) in formatters.Hoisted)
                w.Line("const " + name + " = " + expression + ";");
        }

        w.Line();
        w.Lines(body.ToString().TrimEnd('\n'));
        return w.ToString();
    }

    private static string ModuleDts(JsContext context, ExportIr export, JsExportNames names)
    {
        var imports = new JsTypeEmitter.DtsImports(context, "");
        foreach (var api in new[] { export.ServerApi, export.ClientApi })
        {
            foreach (var method in api.Methods)
            {
                imports.Add(method.ReturnType);
                imports.Add(method.PipeType);
                foreach (var param in method.Params)
                    imports.Add(param.Type);
            }

            foreach (var collection in api.Collections)
                imports.Add(collection.Item);
        }

        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        w.Line("import * as " + JsSyntax.Rt + " from " + JsSyntax.String(context.RuntimeModule) + ";");
        imports.Write(w, null);
        w.Line();

        HandshakeDoc(w, export);
        w.Line("export declare const " + names.Handshake + ": " + JsSyntax.Rt + ".HandshakeDescriptor;");
        w.Line();
        WriteProxyDts(w, context, export, names);
        w.Line();
        JsClientNexusEmitter.WriteDts(w, context, export, names);
        w.Line();
        BindingDoc(w, export);
        w.Line("export declare const " + names.Binding + ": " + JsSyntax.Rt + ".ClientBinding<" + names.Proxy + ">;");
        w.Line();
        FactoryDoc(w, context, export, names, js: false);
        w.Line("export declare function " + names.Factory + "<TNexus extends " + names.ClientNexusBase + ">(config: " + JsSyntax.Rt
               + ".ClientConfig, nexus: TNexus): " + JsSyntax.Rt + ".NexusClient<TNexus, " + names.Proxy + ">;");
        return w.ToString();
    }

    // ------------------------------------------------------------------ handshake

    private static void HandshakeDoc(CodeWriter w, ExportIr export)
    {
        var h = export.Handshake;
        JsSyntax.Doc(w,
            "The greeting constants of the NexNet export " + JsSyntax.Code(ExportName(export)) + ": "
            + (h.Version == null ? "an unversioned server API" : "server API version " + JsSyntax.Code(h.Version)) + ".",
            "The hashes are generated from the C# nexus interfaces and sent as they are. A server built from different interfaces refuses the connection.");
    }

    private static void WriteHandshake(CodeWriter w, ExportIr export, JsExportNames names)
    {
        var h = export.Handshake;
        HandshakeDoc(w, export);
        w.Line("export const " + names.Handshake + " = Object.freeze({");
        w.Indent();
        w.Line("version: " + (h.Version == null ? "null" : JsSyntax.String(h.Version)) + ",");
        w.Line("serverNexusHash: " + JsSyntax.Int(h.ServerNexusHash) + ",");
        w.Line("clientNexusHash: " + JsSyntax.Int(h.ClientNexusHash) + ",");
        w.Line("acceptedIds: new Set([" + string.Join(", ", export.ClientAcceptedIds.Select(a => JsSyntax.Int(a.Id))) + "]),");
        w.Unindent().Line("});");
    }

    // ------------------------------------------------------------------ proxy

    private static string ProxySummary(InterfaceIr api)
        => "The proxy of the server nexus " + JsSyntax.Code(api.ClrName) + ": calls its methods on the server.";

    private static string ProxyRemarks
        => "Get it from " + JsSyntax.Code("client.proxy") + " or " + JsSyntax.Code("this.context.proxy")
           + " in the client nexus. A call made while the client is not connected rejects with " + JsSyntax.Code("NexusTransportError") + ".";

    /// <summary>The name of the trailing options parameter: <c>options</c> unless a parameter already has it.</summary>
    private static string OptionsName(JsContext context, MethodIr method)
        => method.Params.Any(p => ParamName(context, p) == "options") ? "$options" : "options";

    /// <summary>The parameters a proxy method declares, in order (the token becomes the trailing options).</summary>
    private static IEnumerable<ParamIr> ProxyParams(MethodIr method) => method.Params.Where(p => p.Kind != ParamKind.CancellationToken);

    private static void CollectionDoc(CodeWriter w, InterfaceIr api, CollectionIr collection)
        => JsSyntax.Doc(w, collection.Docs ?? "The synchronized list " + JsSyntax.Code(api.Name + "." + collection.Name) + ".",
            "Mode " + JsSyntax.Code(Mode(collection.Mode)) + ". The same list is returned every time; call " + JsSyntax.Code("enable()") + " to synchronize it.");

    private static void WriteProxyJs(CodeWriter w, JsContext context, ExportIr export, JsExportNames names, Formatters formatters)
    {
        var api = export.ServerApi;
        JsSyntax.Doc(w, ProxySummary(api), ProxyRemarks);
        using (w.Block("export class " + names.Proxy + " extends " + JsSyntax.Rt + ".ProxyBase"))
        {
            var first = true;
            foreach (var method in api.Methods)
            {
                if (!first)
                    w.Line();
                first = false;
                WriteMethodJs(w, context, api, method, formatters);
            }

            foreach (var collection in api.Collections)
            {
                if (collection.Item == null)
                    continue;
                if (!first)
                    w.Line();
                first = false;
                CollectionDoc(w, api, collection);
                using (w.Block("get " + MethodName(context, collection.Name, ReservedMembers) + "()"))
                {
                    var options = "mode: " + JsSyntax.Rt + ".NexusCollectionMode." + Mode(collection.Mode);
                    if (ValuesFormatter(collection.Item) is { } values)
                        options += ", valuesFormatter: " + JsSyntax.Rt + "." + values;
                    w.Line("return this._collection(" + JsSyntax.Int(collection.Id) + ", " + formatters.For(collection.Item) + ", { " + options + " });");
                }
            }
        }
    }

    private static void WriteMethodJs(CodeWriter w, JsContext context, InterfaceIr api, MethodIr method, Formatters formatters)
    {
        var name = MethodName(context, method.Name, ReservedMembers);
        var parameters = ProxyParams(method).Select(p => ParamName(context, p)).ToList();
        var options = method.HasCancellation ? OptionsName(context, method) : null;
        if (options != null)
            parameters.Add(options);
        var signal = options == null ? null : "signal: " + options + "?.signal";

        JsSyntax.Doc(w, method.Docs ?? "Calls " + JsSyntax.Code(api.Name + "." + method.Name) + " on the server.");
        using (w.Block(name + "(" + string.Join(", ", parameters) + ")"))
        {
            var id = JsSyntax.Int(method.Id);
            if (method.PipeKind != PipeKind.None)
            {
                var pipe = method.Params.First(p => p.Kind is ParamKind.DuplexPipe or ParamKind.DuplexChannel);
                var call = method.PipeKind == PipeKind.Channel ? "_invokeChannel" : "_invokePipe";
                w.Line("return this." + call + "(" + id + ", " + ParamName(context, pipe) + ", ($w, $pipeId) => {");
                w.Indent();
                WriteArguments(w, context, method, formatters, "$pipeId");
                w.Unindent();
                w.Line(signal == null ? "});" : "}, { " + signal + " });");
                return;
            }

            var tail = new List<string>();
            if (signal != null)
                tail.Add(signal);
            if (method.ReturnKind == ReturnKind.TaskOf && method.ReturnType != null)
                tail.Add("returns: " + formatters.For(method.ReturnType));
            var after = tail.Count == 0 || method.ReturnKind == ReturnKind.Void ? ")" : ", { " + string.Join(", ", tail) + " })";
            var prefix = method.ReturnKind == ReturnKind.Void ? "this._invokeVoid(" : "return this._invoke(";

            if (method.SerializedCount == 0)
            {
                w.Line(prefix + id + ", null" + after + ";");
                return;
            }

            w.Line(prefix + id + ", ($w) => {");
            w.Indent();
            WriteArguments(w, context, method, formatters, null);
            w.Unindent();
            w.Line("}" + after + ";");
        }
    }

    private static void WriteArguments(CodeWriter w, JsContext context, MethodIr method, Formatters formatters, string? pipeId)
    {
        w.Line("$w.writeArrayHeader(" + JsSyntax.Int(method.SerializedCount) + ");");
        foreach (var (param, _) in WireOrder(method))
        {
            if (param.Kind is ParamKind.DuplexPipe or ParamKind.DuplexChannel)
                w.Line("$w.writeInt(" + (pipeId ?? "0") + ");");
            else if (param.Kind == ParamKind.Value)
                w.Line(formatters.For(Value(param)) + ".write($w, " + ParamName(context, param) + ");");
        }
    }

    private static void WriteProxyDts(CodeWriter w, JsContext context, ExportIr export, JsExportNames names)
    {
        var api = export.ServerApi;
        JsSyntax.Doc(w, ProxySummary(api), ProxyRemarks);
        using (w.Block("export declare class " + names.Proxy + " extends " + JsSyntax.Rt + ".ProxyBase"))
        {
            foreach (var method in api.Methods)
            {
                var name = MethodName(context, method.Name, ReservedMembers);
                var parameters = ProxyParams(method).Select(p => ParamName(context, p) + ": "
                    + (p.Kind == ParamKind.Value ? TsType(context, Value(p)) : PipeTsType(context, method, p))).ToList();
                var tags = new List<string>();
                if (method.HasCancellation)
                {
                    var options = OptionsName(context, method);
                    parameters.Add(options + "?: { signal?: AbortSignal }");
                    tags.Add("@param " + options + " " + JsSyntax.Code("signal") + " cancels the call: before it is sent the call rejects with "
                             + JsSyntax.Code("AbortError") + " and nothing is sent; after it, the server method is canceled.");
                }

                if (method.PipeKind != PipeKind.None)
                    tags.Add("@returns Resolves once the invocation is sent; the pipe's " + JsSyntax.Code("ready") + " and "
                             + JsSyntax.Code("completed") + " follow its lifetime.");
                else if (method.ReturnKind != ReturnKind.Void)
                    tags.Add("@returns The server method's completion" + (method.ReturnKind == ReturnKind.TaskOf ? " and result." : "."));

                string returns;
                if (method.PipeKind == PipeKind.None && method.ReturnKind == ReturnKind.Void)
                    returns = "void";
                else if (method.PipeKind == PipeKind.None && method.ReturnKind == ReturnKind.TaskOf && method.ReturnType != null)
                    returns = "Promise<" + TsType(context, method.ReturnType) + ">";
                else
                    returns = "Promise<void>";

                JsSyntax.Doc(w, method.Docs ?? "Calls " + JsSyntax.Code(api.Name + "." + method.Name) + " on the server.", string.Join("\n", tags));
                w.Line(name + "(" + string.Join(", ", parameters) + "): " + returns + ";");
            }

            foreach (var collection in api.Collections)
            {
                if (collection.Item == null)
                    continue;
                CollectionDoc(w, api, collection);
                w.Line("get " + MethodName(context, collection.Name, ReservedMembers) + "(): " + JsSyntax.Rt + ".NexusList<" + TsType(context, collection.Item) + ">;");
            }
        }
    }

    /// <summary>The <c>NexusCollectionMode</c> member of a mode.</summary>
    internal static string Mode(CollectionMode mode) => mode switch
    {
        CollectionMode.ServerToClient => "ServerToClient",
        CollectionMode.BiDirectional => "BiDirectional",
        CollectionMode.Relay => "Relay",
        _ => "Unset",
    };

    /// <summary>
    /// The runtime formatter of the C# <c>T[]</c> of a collection's items when C# does not write it as a MessagePack
    /// array: ext 78 for the primitive numeric types (protocol spec §10.5), bin for <c>byte</c>. Null otherwise.
    /// </summary>
    internal static string? ValuesFormatter(TypeRef item)
    {
        if (item.Kind != TypeRefKind.Prim)
            return null;
        return item.Prim switch
        {
            PrimKind.Byte => "binFormatter",
            PrimKind.SByte or PrimKind.Int16 or PrimKind.UInt16 or PrimKind.Int32 or PrimKind.UInt32 or PrimKind.Int64 or PrimKind.UInt64
                or PrimKind.Single or PrimKind.Double or PrimKind.Half or PrimKind.Char => JsFormatterExpressions.PrimArrayFormatter(item.Prim),
            _ => null,
        };
    }

    // ------------------------------------------------------------------ binding and factory

    private static void BindingDoc(CodeWriter w, ExportIr export)
        => JsSyntax.Doc(w, "The binding of the export " + JsSyntax.Code(ExportName(export)) + ": its handshake and proxy class, for "
                           + JsSyntax.Code("NexusClient.create") + " and " + JsSyntax.Code("NexusClientPool") + ".");

    private static void FactoryDoc(CodeWriter w, JsContext context, ExportIr export, JsExportNames names, bool js)
    {
        var runtime = "import(" + JsSyntax.String(context.RuntimeModule) + ")";
        var tags = js
            ? new[]
            {
                "@template {" + names.ClientNexusBase + "} TNexus",
                "@param {" + runtime + ".ClientConfig} config The configuration: exactly one transport.",
                "@param {TNexus} nexus The client nexus, which the server calls.",
                "@returns {" + runtime + ".NexusClient<TNexus, " + names.Proxy + ">} The client, not yet connected.",
            }
            : new[]
            {
                "@param config The configuration: exactly one transport.",
                "@param nexus The client nexus, which the server calls.",
                "@returns The client, not yet connected.",
            };
        JsSyntax.Doc(w,
            export.Docs,
            "Creates a client of the NexNet export " + JsSyntax.Code(ExportName(export)) + ": the proxy is " + JsSyntax.Code(names.Proxy)
            + " and the client nexus extends " + JsSyntax.Code(names.ClientNexusBase) + ".",
            string.Join("\n", tags));
    }

    private static void WriteFactoryJs(CodeWriter w, JsContext context, ExportIr export, JsExportNames names)
    {
        BindingDoc(w, export);
        w.Line("export const " + names.Binding + " = Object.freeze({").Indent();
        w.Line("handshake: " + names.Handshake + ",");
        w.Line("proxy: " + names.Proxy + ",");
        w.Unindent().Line("});");

        w.Line();
        FactoryDoc(w, context, export, names, js: true);
        using (w.Block("export function " + names.Factory + "(config, nexus)"))
            w.Line("return " + JsSyntax.Rt + ".NexusClient.create(config, nexus, " + names.Binding + ");");
    }
}
