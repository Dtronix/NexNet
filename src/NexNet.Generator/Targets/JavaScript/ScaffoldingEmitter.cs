using System.Security.Cryptography;
using System.Text;
using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.JavaScript;

/// <summary>The names of one export's test scaffolding: <c>&lt;Stem&gt;ScriptableNexus</c> and <c>&lt;Stem&gt;Scaffolding</c>.</summary>
internal sealed class JsScaffoldingNames
{
    private JsScaffoldingNames(string stem)
    {
        ScriptableNexus = stem + "ScriptableNexus";
        Scaffolding = stem + "Scaffolding";
    }

    /// <summary><c>&lt;Stem&gt;ScriptableNexus</c>: the client nexus whose calls run scripts in the language host.</summary>
    public string ScriptableNexus { get; }

    /// <summary>
    /// <c>&lt;Stem&gt;Scaffolding</c>: the export's <c>ScaffoldingExport</c> instance (invocation by name, codecs), also the
    /// name of the module (<c>scaffolding/&lt;Stem&gt;Scaffolding.js</c>) that holds both.
    /// </summary>
    public string Scaffolding { get; }

    public static JsScaffoldingNames Of(ExportIr export) => new(JsExportNames.Stem(export.Name));
}

/// <summary>
/// Emits the JavaScript test scaffolding (J11, <see cref="EmitMode.TestScaffolding"/>) that the Node and browser language
/// hosts of the cross-language test harness (J12, control protocol <c>docs/internals/interop-control-protocol.md</c>)
/// build on. The layout mirrors the Java target's (K11). Everything goes in the <c>scaffolding/</c> directory of the
/// output directory, an ES module and its declarations each:
/// <list type="bullet">
/// <item><c>&lt;Stem&gt;Scaffolding.js</c>: <c>&lt;Stem&gt;ScriptableNexus extends &lt;Stem&gt;ClientNexusBase</c>, whose every
/// client nexus method (and <c>onConnected</c>/<c>onDisconnected</c>) hands its typed-JSON arguments to a
/// <c>ScriptHost</c>, which runs the slot's script, and converts the typed-JSON result back; and the
/// <c>&lt;Stem&gt;Scaffolding</c> instance of <c>ScaffoldingExport</c>: <c>invokeByName(proxy, method, args, { pipe, signal })</c>
/// with typed-JSON arguments and result, collections by name, method/collection/slot tables and the client factory.</item>
/// <item><c>test-codecs.js</c>: a <c>TypedCodec</c> per type the exports use (typed JSON to and from JavaScript values
/// through the generated formatters), the declared type schemas, and <c>forType(expression)</c> for channel item
/// types.</item>
/// <item><c>index.js</c>: <c>SCHEMA_HASH</c> (the lower-case hex SHA-256 of the <c>nexnet-ir.json</c> bytes, control
/// protocol §4.6), <c>EXPORTS</c> and <c>find(clientNexus, serverNexus)</c>.</item>
/// <item>The fixed modules of <see cref="JsScaffoldingRuntime"/>: <c>typed-json.js</c> and <c>scaffolding-runtime.js</c>.</item>
/// </list>
/// Script semantics (steps, <c>script.call</c> events, signals, forwarding) live in the host; the generated code only
/// adapts the typed nexus API to typed JSON.
/// </summary>
internal static class JsScaffoldingEmitter
{
    /// <summary>The directory of the scaffolding in the output directory.</summary>
    public const string Directory = "scaffolding/";

    /// <summary>The module of the codecs.</summary>
    public const string CodecsModule = "test-codecs";

    /// <summary>The module of the schema hash and the export index.</summary>
    public const string IndexModule = "index";

    /// <summary>The module of the fixed scaffolding runtime.</summary>
    private const string RuntimeModule = "scaffolding-runtime";

    /// <summary>The module of typed JSON.</summary>
    private const string TypedJsonModule = "typed-json";

    private const string Sr = "$sr";
    private const string Tc = "$tc";
    private const string Tj = "$tj";
    private const string Export = "$m";
    private const string Se = "$se";

    /// <summary>Doc texts below write inline code between '~'.</summary>
    private const char Backtick = (char)96;

    private const string SchemaHashDoc =
        "The ~schemaHash~ of the language host's ~hello~ (control protocol §4.6): the lower-case hex SHA-256 of the ~nexnet-ir.json~ this code was generated from.";

    private const string ForTypeDoc = "The codec of a golden type expression (~channel.writer~/~channel.reader~)";

    private const string ScriptableRemarks =
        "Every call hands its typed-JSON arguments to the script host, which runs the slot's script, and completes with the typed-JSON "
        + "result converted back. ~onConnected~ and ~onDisconnected~ run their slots without waiting for them. Fire-and-forget "
        + "methods drop their result and errors. ~onInvocationException~ asks the script host.";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The scaffolding files, in path order.</summary>
    /// <param name="context">The emission context.</param>
    /// <param name="users">The user formatters (D12) the generated code imports.</param>
    public static IEnumerable<GeneratedFile> Emit(JsContext context, IReadOnlyList<JsFormatterEmitter.UserFormatterRef> users)
    {
        var exports = context.Ir.Exports.Where(e => (e.Targets & (int)NexusTargets.JavaScript) != 0).ToArray();
        var codecs = new CodecTable(context, users);

        var files = new List<GeneratedFile>();
        foreach (var export in exports)
        {
            var names = JsScaffoldingNames.Of(export);
            files.Add(new GeneratedFile(Directory + names.Scaffolding + ".js", ExportJs(context, export, codecs)));
            files.Add(new GeneratedFile(Directory + names.Scaffolding + ".d.ts", ExportDts(context, export)));
        }

        files.Add(new GeneratedFile(Directory + CodecsModule + ".js", codecs.WriteJs()));
        files.Add(new GeneratedFile(Directory + CodecsModule + ".d.ts", codecs.WriteDts()));
        files.Add(new GeneratedFile(Directory + IndexModule + ".js", IndexJs(context, exports)));
        files.Add(new GeneratedFile(Directory + IndexModule + ".d.ts", IndexDts()));

        // The sources are raw string literals: normalize the line endings a CRLF checkout gives them.
        var runtime = JsSyntax.String(context.RuntimeModule);
        foreach (var (name, text) in JsScaffoldingRuntime.Files)
        {
            var content = text.Replace("\r\n", "\n").Replace(JsScaffoldingRuntime.RuntimePlaceholder, runtime);
            files.Add(new GeneratedFile(Directory + name, JsSyntax.Header + "\n" + content + "\n"));
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

    /// <summary>
    /// A module specifier as seen from the scaffolding directory: relative specifiers (relative to the output
    /// directory) gain a <c>../</c>; package specifiers stay.
    /// </summary>
    internal static string FromScaffolding(string module)
    {
        if (module.StartsWith("./", StringComparison.Ordinal))
            return "../" + module.Substring(2);
        if (module.StartsWith("../", StringComparison.Ordinal))
            return "../" + module;
        return module;
    }

    private static string Import(string name, string module) => "import * as " + name + " from " + JsSyntax.String(module) + ";";

    private static string Doc(string text) => text.Replace('~', Backtick);

    // ------------------------------------------------------------------ export module

    private static string ExportJs(JsContext context, ExportIr export, CodecTable codecs)
    {
        var names = JsExportNames.Of(export);
        var own = JsScaffoldingNames.Of(export);

        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        w.Line(Import(JsSyntax.Rt, context.RuntimeModule));
        w.Line(Import(Export, "../" + context.Target.Names.Type(export.Name) + ".js"));
        w.Line(Import(Sr, "./" + RuntimeModule + ".js"));
        w.Line(Import(Tc, "./" + CodecsModule + ".js"));
        w.Line();
        w.Line("const " + Se + " = " + Sr + ".ScaffoldingExport;");
        w.Line();
        WriteScriptableNexus(w, context, export, names, own, codecs);
        w.Line();
        WriteExportClass(w, context, export, names, own, codecs);
        return w.ToString();
    }

    private static string ScriptableSummary(ExportIr export)
        => "The scriptable client nexus " + JsSyntax.Code(export.ClientApi.ClrName) + " of the cross-language test harness.";

    private static void WriteScriptableNexus(CodeWriter w, JsContext context, ExportIr export, JsExportNames names, JsScaffoldingNames own, CodecTable codecs)
    {
        JsSyntax.Doc(w, ScriptableSummary(export), Doc(ScriptableRemarks));
        using (w.Block("export class " + own.ScriptableNexus + " extends " + Export + "." + names.ClientNexusBase))
        {
            JsSyntax.Doc(w, "@type {import('./" + RuntimeModule + ".js').ScriptHost}");
            w.Line("#host;");
            w.Line();
            JsSyntax.Doc(w, "Creates the nexus.", "@param {import('./" + RuntimeModule + ".js').ScriptHost} host Runs the scripts of its calls.");
            using (w.Block("constructor(host)"))
            {
                w.Line("super();");
                w.Line("this.#host = host;");
            }

            foreach (var method in export.ClientApi.Methods)
            {
                var name = JsProxyEmitter.MethodName(context, method.Name, JsClientNexusEmitter.ReservedMembers);
                var parameters = method.Params.Select(p => JsProxyEmitter.ParamName(context, p));
                var values = JsProxyEmitter.WireOrder(method)
                    .Where(p => p.Param.Kind == ParamKind.Value)
                    .Select(p => codecs.For(JsProxyEmitter.Value(p.Param)) + ".toJson(" + JsProxyEmitter.ParamName(context, p.Param) + ")");
                var args = "[" + string.Join(", ", values) + "]";
                var slot = JsSyntax.String(method.Name);

                w.Line();
                if (method.PipeKind == PipeKind.None && method.ReturnKind == ReturnKind.Void)
                {
                    using (w.Block(name + "(" + string.Join(", ", parameters) + ")"))
                        w.Line(Sr + ".runDetached(this.#host, " + slot + ", " + args + ");");
                    continue;
                }

                var token = method.Params.FirstOrDefault(p => p.Kind == ParamKind.CancellationToken);
                var pipe = method.Params.FirstOrDefault(p => p.Kind is ParamKind.DuplexPipe or ParamKind.DuplexChannel);
                var item = pipe is { Kind: ParamKind.DuplexChannel } ? codecs.For(JsProxyEmitter.ChannelItem(method, pipe)) : "null";
                var call = Sr + ".runScript(this.#host, " + slot + ", " + args + ", "
                           + (token == null ? "null" : JsProxyEmitter.ParamName(context, token)) + ", "
                           + (pipe == null ? "null" : JsProxyEmitter.ParamName(context, pipe)) + ", " + item + ")";
                using (w.Block("async " + name + "(" + string.Join(", ", parameters) + ")"))
                {
                    if (method.PipeKind == PipeKind.None && method.ReturnKind == ReturnKind.TaskOf && method.ReturnType != null)
                        w.Line("return " + codecs.For(method.ReturnType) + ".fromJson(await " + call + ");");
                    else
                        w.Line("await " + call + ";");
                }
            }

            w.Line();
            using (w.Block("onConnected(isReconnected)"))
                w.Line(Sr + ".runDetached(this.#host, " + Sr + ".ON_CONNECTED, [isReconnected]);");
            w.Line();
            using (w.Block("onDisconnected()"))
                w.Line(Sr + ".runDetached(this.#host, " + Sr + ".ON_DISCONNECTED, []);");
            w.Line();
            JsSyntax.Doc(w, Doc("Asks the script host (~ScriptHost.onInvocationException~)."),
                "@param {unknown} error\n@param {number} methodId\n@returns {{ code?: number | null, message?: string | null } | null | undefined}");
            using (w.Block("onInvocationException(error, methodId)"))
                w.Line("return this.#host.onInvocationException?.(error, methodId) ?? null;");
        }
    }

    private static string ExportSummary(ExportIr export)
        => "The test scaffolding of the NexNet export " + JsSyntax.Code(JsProxyEmitter.ExportName(export)) + ": "
           + JsSyntax.Code(export.ClientApi.ClrName) + " / " + JsSyntax.Code(export.ServerApi.ClrName) + ".";

    private static void WriteExportClass(CodeWriter w, JsContext context, ExportIr export, JsExportNames names, JsScaffoldingNames own, CodecTable codecs)
    {
        var server = export.ServerApi;
        var className = own.Scaffolding + "Export";
        JsSyntax.Doc(w, ExportSummary(export));
        using (w.Block("class " + className + " extends " + Se))
        {
            using (w.Block("constructor()"))
            {
                w.Line("super({").Indent();
                w.Line("clientNexus: " + JsSyntax.String(export.ClientApi.ClrName) + ",");
                w.Line("serverNexus: " + JsSyntax.String(server.ClrName) + ",");
                w.Line("binding: " + Export + "." + names.Binding + ",");

                var slots = export.ClientApi.Methods.Select(m => JsSyntax.String(m.Name)).Concat(new[] { Sr + ".ON_CONNECTED", Sr + ".ON_DISCONNECTED" });
                w.Line("slots: [" + string.Join(", ", slots) + "],");

                WriteList(w, "methods", server.Methods.Select(m => "new " + Sr + ".Method(" + JsSyntax.String(m.Name) + ", " + JsSyntax.Int(m.Id) + ", "
                    + JsSyntax.String(ReturnKindName(m)) + ", " + JsSyntax.String(PipeKindName(m.PipeKind)) + ", " + (m.HasCancellation ? "true" : "false") + ", ["
                    + string.Join(", ", JsProxyEmitter.WireOrder(m).Where(p => p.Param.Kind == ParamKind.Value).Select(p => codecs.For(JsProxyEmitter.Value(p.Param)))) + "], "
                    + (ReturnKindName(m) == "TaskOf" ? codecs.For(m.ReturnType!) : "null") + ", "
                    + (m.PipeKind == PipeKind.Channel ? codecs.For(ChannelItem(m)) : "null") + ")"));

                var collections = server.Collections.Where(c => c.Item != null).ToArray();
                WriteList(w, "collections", collections.Select(c => "new " + Sr + ".Collection(" + JsSyntax.String(c.Name) + ", " + JsSyntax.Int(c.Id) + ", "
                    + JsSyntax.Rt + ".NexusCollectionMode." + CollectionMode(c.Mode) + ", " + codecs.For(c.Item!) + ")"));
                w.Unindent().Line("});");
            }

            w.Line();
            using (w.Block("createNexus(host)"))
                w.Line("return new " + own.ScriptableNexus + "(host);");

            w.Line();
            using (w.Block("_invoke(proxy, method, args, pipe, signal)"))
            {
                using (w.Block("switch (method)"))
                {
                    foreach (var method in server.Methods)
                    {
                        using (w.Block("case " + JsSyntax.String(method.Name) + ":"))
                            WriteInvokeCase(w, context, method, codecs);
                    }

                    w.Line("default:");
                    w.Indent().Line("return super._invoke(proxy, method, args, pipe, signal);").Unindent();
                }
            }

            w.Line();
            using (w.Block("list(proxy, collection)"))
            {
                using (w.Block("switch (collection)"))
                {
                    foreach (var collection in server.Collections.Where(c => c.Item != null))
                    {
                        w.Line("case " + JsSyntax.String(collection.Name) + ":");
                        w.Indent().Line("return proxy." + JsProxyEmitter.MethodName(context, collection.Name, JsProxyEmitter.ReservedMembers) + ";").Unindent();
                    }

                    w.Line("default:");
                    w.Indent().Line("return super.list(proxy, collection);").Unindent();
                }
            }
        }

        w.Line();
        JsSyntax.Doc(w, ExportSummary(export));
        w.Line("export const " + own.Scaffolding + " = new " + className + "();");
    }

    private static void WriteList(CodeWriter w, string name, IEnumerable<string> items)
    {
        var list = items.ToArray();
        if (list.Length == 0)
        {
            w.Line(name + ": [],");
            return;
        }

        w.Line(name + ": [").Indent();
        foreach (var item in list)
            w.Line(item + ",");
        w.Unindent().Line("],");
    }

    private static void WriteInvokeCase(CodeWriter w, JsContext context, MethodIr method, CodecTable codecs)
    {
        var values = JsProxyEmitter.WireOrder(method).Where(p => p.Param.Kind == ParamKind.Value).Select(p => p.Param).ToList();
        w.Line(Se + ".expectArguments(method, args, " + JsSyntax.Int(values.Count) + ");");

        var args = method.Params
            .Where(p => p.Kind != ParamKind.CancellationToken)
            .Select(p => p.Kind switch
            {
                ParamKind.DuplexPipe => Se + ".pipe(pipe)",
                ParamKind.DuplexChannel => Se + ".channel(pipe)",
                _ => codecs.For(JsProxyEmitter.Value(p)) + ".fromJson(args[" + JsSyntax.Int(values.IndexOf(p)) + "])",
            })
            .ToList();
        if (method.HasCancellation)
            args.Add("{ signal }");
        var call = "proxy." + JsProxyEmitter.MethodName(context, method.Name, JsProxyEmitter.ReservedMembers) + "(" + string.Join(", ", args) + ")";

        switch (ReturnKindName(method))
        {
            case "Void":
                w.Line(call + ";");
                w.Line("return null;");
                break;
            case "Task":
                w.Line("return " + call + ".then(" + Se + ".nothing);");
                break;
            default:
                w.Line("return " + call + ".then((r) => " + codecs.For(method.ReturnType!) + ".toJson(r));");
                break;
        }
    }

    /// <summary>The <c>returnKind</c> of a method: pipe methods and value-less results complete like <c>Task</c>.</summary>
    private static string ReturnKindName(MethodIr method)
    {
        if (method.PipeKind != PipeKind.None)
            return "Task";
        return method.ReturnKind switch
        {
            ReturnKind.Void => "Void",
            ReturnKind.TaskOf when method.ReturnType != null => "TaskOf",
            _ => "Task",
        };
    }

    private static string PipeKindName(PipeKind kind) => kind switch
    {
        PipeKind.DuplexPipe => "Pipe",
        PipeKind.Channel => "Channel",
        _ => "None",
    };

    private static TypeRef ChannelItem(MethodIr method)
        => JsProxyEmitter.ChannelItem(method, method.Params.First(p => p.Kind == ParamKind.DuplexChannel));

    private static string CollectionMode(CollectionMode mode) => mode switch
    {
        Ir.CollectionMode.BiDirectional => "BiDirectional",
        Ir.CollectionMode.Relay => "Relay",
        _ => "ServerToClient",
    };

    private static string ExportDts(JsContext context, ExportIr export)
    {
        var names = JsExportNames.Of(export);
        var own = JsScaffoldingNames.Of(export);
        var api = export.ClientApi;

        var imports = new JsTypeEmitter.DtsImports(context, "");
        foreach (var method in api.Methods)
        {
            imports.Add(method.ReturnType);
            imports.Add(method.PipeType);
            foreach (var param in method.Params)
                imports.Add(param.Type);
        }

        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        w.Line(Import(JsSyntax.Rt, context.RuntimeModule));
        w.Line(Import(Export, "../" + context.Target.Names.Type(export.Name) + ".js"));
        w.Line("import type * as " + Sr + " from './" + RuntimeModule + ".js';");
        WriteDeclImports(w, imports);
        w.Line();

        JsSyntax.Doc(w, ScriptableSummary(export), Doc(ScriptableRemarks));
        using (w.Block("export declare class " + own.ScriptableNexus + " extends " + Export + "." + names.ClientNexusBase))
        {
            JsSyntax.Doc(w, "Creates the nexus.", "@param host Runs the scripts of its calls.");
            w.Line("constructor(host: " + Sr + ".ScriptHost);");
            foreach (var method in api.Methods)
            {
                var name = JsProxyEmitter.MethodName(context, method.Name, JsClientNexusEmitter.ReservedMembers);
                var parameters = method.Params.Select(p => JsProxyEmitter.ParamName(context, p) + ": " + p.Kind switch
                {
                    ParamKind.CancellationToken => "AbortSignal",
                    ParamKind.DuplexPipe or ParamKind.DuplexChannel => JsProxyEmitter.PipeTsType(context, method, p),
                    _ => JsProxyEmitter.TsType(context, JsProxyEmitter.Value(p)),
                });
                string returns;
                if (method.PipeKind == PipeKind.None && method.ReturnKind == ReturnKind.Void)
                    returns = "void";
                else if (method.PipeKind == PipeKind.None && method.ReturnKind == ReturnKind.TaskOf && method.ReturnType != null)
                    returns = "Promise<" + JsProxyEmitter.TsType(context, method.ReturnType) + ">";
                else
                    returns = "Promise<void>";
                w.Line("protected " + name + "(" + string.Join(", ", parameters) + "): " + returns + ";");
            }

            w.Line("onConnected(isReconnected: boolean): void;");
            w.Line("onDisconnected(): void;");
            w.Line("onInvocationException(error: unknown, methodId: number): { code?: number | null; message?: string | null } | null | undefined;");
        }

        w.Line();
        JsSyntax.Doc(w, ExportSummary(export));
        w.Line("export declare const " + own.Scaffolding + ": " + Sr + ".ScaffoldingExport<" + Export + "." + names.Proxy + ">;");
        return w.ToString();
    }

    /// <summary>The declared-type and runtime-type imports of a declaration file in the scaffolding directory.</summary>
    private static void WriteDeclImports(CodeWriter w, JsTypeEmitter.DtsImports imports)
    {
        var lines = new CodeWriter("  ");
        imports.Write(lines, null);
        var text = lines.ToString().Replace(" from './", " from '../").TrimEnd('\n');
        if (text.Length > 0)
            w.Lines(text);
    }

    // ------------------------------------------------------------------ index

    private static string IndexJs(JsContext context, IReadOnlyList<ExportIr> exports)
    {
        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        var items = exports.Select(e => JsScaffoldingNames.Of(e).Scaffolding).ToArray();
        foreach (var item in items)
            w.Line("import { " + item + " } from './" + item + ".js';");
        if (items.Length > 0)
            w.Line();
        JsSyntax.Doc(w, Doc(SchemaHashDoc));
        w.Line("export const SCHEMA_HASH = " + JsSyntax.String(SchemaHash(context.Ir)) + ";");
        w.Line();
        JsSyntax.Doc(w, "The exports, in IR order.");
        w.Line("export const EXPORTS = Object.freeze([" + string.Join(", ", items) + "]);");
        w.Line();
        JsSyntax.Doc(w, Doc("The export of a nexus pair, as ~client.create~ names it."),
            "@param {string} clientNexus The full metadata name of the C# client nexus interface.\n"
            + "@param {string} serverNexus The full metadata name of the C# server nexus interface.\n"
            + "@returns {import('./" + RuntimeModule + ".js').ScaffoldingExport<any> | null} The export, or null when there is none for the pair.");
        using (w.Block("export function find(clientNexus, serverNexus)"))
            w.Line("return EXPORTS.find((e) => e.clientNexus === clientNexus && e.serverNexus === serverNexus) ?? null;");
        return w.ToString();
    }

    private static string IndexDts()
    {
        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        w.Line("import type * as " + Sr + " from './" + RuntimeModule + ".js';");
        w.Line();
        JsSyntax.Doc(w, Doc(SchemaHashDoc));
        w.Line("export declare const SCHEMA_HASH: string;");
        JsSyntax.Doc(w, "The exports, in IR order.");
        w.Line("export declare const EXPORTS: readonly " + Sr + ".ScaffoldingExport<any>[];");
        JsSyntax.Doc(w, Doc("The export of a nexus pair, as ~client.create~ names it; null when there is none for the pair."));
        w.Line("export declare function find(clientNexus: string, serverNexus: string): " + Sr + ".ScaffoldingExport<any> | null;");
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
            TypeRefKind.TypeParam => "$" + JsSyntax.Int(type.Ordinal),
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

    /// <summary>The typed-JSON primitive name of a leaf (<c>Prim</c> in <c>typed-json.js</c>), quoted.</summary>
    private static string PrimConstant(PrimKind prim) => JsSyntax.String(prim switch
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
    });

    /// <summary>The JavaScript expression of the typed-JSON shape of an IR type.</summary>
    internal static string Shape(TypeRef type)
    {
        string Arg(int i) => i < type.Args.Length ? Shape(type.Args[i]) : throw new InvalidOperationException("Malformed type reference " + type.Kind + ".");
        string Args() => string.Concat(type.Args.Select(a => ", " + Shape(a)));

        return type.Kind switch
        {
            TypeRefKind.Prim => Tj + ".prim(" + PrimConstant(type.Prim) + ")",
            TypeRefKind.Bin => Tj + ".bin()",
            TypeRefKind.PrimArray => Tj + ".primArray(" + PrimConstant(type.Prim) + ")",
            TypeRefKind.Seq => Tj + ".seq(" + (type.Clr == null ? "null" : JsSyntax.String(type.Clr)) + ", " + Arg(0) + ")",
            TypeRefKind.Set => Tj + ".set(" + Arg(0) + ")",
            TypeRefKind.Queue => Tj + ".queue(" + Arg(0) + ")",
            TypeRefKind.Stack => Tj + ".stack(" + Arg(0) + ")",
            TypeRefKind.Map => Tj + ".map(" + Arg(0) + ", " + Arg(1) + ")",
            TypeRefKind.Tuple => Tj + ".tuple(" + JsSyntax.String(type.Clr ?? "Tuple") + Args() + ")",
            TypeRefKind.Nullable => Tj + ".nullable(" + Arg(0) + ")",
            TypeRefKind.Decl => Tj + ".decl(" + JsSyntax.String(type.DeclId ?? "") + Args() + ")",
            TypeRefKind.TypeParam => Tj + ".typeParam(" + JsSyntax.Int(type.Ordinal) + ")",
            TypeRefKind.External => Tj + ".external(" + JsSyntax.String(type.Clr ?? "raw") + ")",
            _ => throw new InvalidOperationException("Unknown type reference kind " + type.Kind + "."),
        };
    }

    /// <summary>The <c>TypedCodec</c> constants of <c>test-codecs.js</c>: one per distinct (TypeScript type, formatter, shape).</summary>
    private sealed class CodecTable
    {
        private readonly JsContext _context;
        private readonly IReadOnlyList<JsFormatterEmitter.UserFormatterRef> _users;
        private readonly JsFormatterExpressions _formatters;
        private readonly SortedSet<string> _usedUsers = new(StringComparer.Ordinal);
        private readonly List<(TypeRef Type, string TsType, string Expression, string Formatter, string Shape)> _codecs = new();
        private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);

        public CodecTable(JsContext context, IReadOnlyList<JsFormatterEmitter.UserFormatterRef> users)
        {
            _context = context;
            _users = users;
            _formatters = new JsFormatterExpressions(context.Scope, JsProxyEmitter.Fmt + ".", spec =>
            {
                var user = users.FirstOrDefault(u => u.Spec == spec) ?? throw new InvalidOperationException("No user formatter " + spec + ".");
                _usedUsers.Add(user.Spec);
                return user.Alias;
            });
        }

        /// <summary>The expression of the codec of <paramref name="type"/>: <c>$tc.C$n</c>.</summary>
        public string For(TypeRef type)
        {
            var tsType = JsProxyEmitter.TsType(_context, type);
            var formatter = _formatters.For(type);
            var shape = Shape(type);
            var key = tsType + "|" + formatter + "|" + shape;
            if (!_byKey.TryGetValue(key, out var field))
            {
                field = Tc + ".C$" + JsSyntax.Int(_codecs.Count);
                _codecs.Add((type, tsType, Expression(type), formatter, shape));
                _byKey[key] = field;
            }

            return field;
        }

        public string WriteJs()
        {
            var w = new CodeWriter("  ");
            w.Line(JsSyntax.Header);
            w.Line(Import(JsSyntax.Rt, _context.RuntimeModule));
            if (_codecs.Any(c => c.Formatter.IndexOf(JsProxyEmitter.Fmt + ".", StringComparison.Ordinal) >= 0))
                w.Line(Import(JsProxyEmitter.Fmt, "../" + JsSyntax.FormattersModule + ".js"));
            foreach (var user in _users.Where(u => _usedUsers.Contains(u.Spec)))
            {
                var module = JsSyntax.String(FromScaffolding(user.Module));
                w.Line(user.Export == null
                    ? "import " + user.Alias + " from " + module + ";"
                    : "import { " + user.Export + " as " + user.Alias + " } from " + module + ";");
            }

            w.Line(Import(Sr, "./" + RuntimeModule + ".js"));
            w.Line(Import(Tj, "./" + TypedJsonModule + ".js"));
            w.Line();

            JsSyntax.Doc(w, "The declared types, by id.");
            var ordered = _context.Decls.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
            if (ordered.Length == 0)
            {
                w.Line("export const SCHEMAS = new " + Tj + ".Schemas();");
            }
            else
            {
                w.Line("export const SCHEMAS = new " + Tj + ".Schemas(").Indent();
                var byId = ordered.ToDictionary(d => d.Id, StringComparer.Ordinal);
                foreach (var decl in ordered)
                    w.Line(Decl(decl, byId) + ",");
                w.Unindent().Line(");");
            }

            for (var i = 0; i < _codecs.Count; i++)
            {
                var codec = _codecs[i];
                w.Line();
                JsSyntax.Doc(w, JsSyntax.Code(codec.Expression) + ".");
                w.Line("export const C$" + JsSyntax.Int(i) + " = new " + Sr + ".TypedCodec(").Indent();
                w.Line(JsSyntax.String(codec.Expression) + ",");
                w.Line(codec.Formatter + ",");
                w.Line(codec.Shape + ",");
                w.Line("SCHEMAS,");
                w.Unindent().Line(");");
            }

            w.Line();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<string>();
            for (var i = 0; i < _codecs.Count; i++)
            {
                if (seen.Add(_codecs[i].Expression))
                    entries.Add("[" + JsSyntax.String(_codecs[i].Expression) + ", C$" + JsSyntax.Int(i) + "]");
            }

            if (entries.Count == 0)
            {
                w.Line("const BY_TYPE = new Map();");
            }
            else
            {
                w.Line("const BY_TYPE = new Map([").Indent();
                foreach (var entry in entries)
                    w.Line(entry + ",");
                w.Unindent().Line("]);");
            }

            w.Line();
            JsSyntax.Doc(w, Doc(ForTypeDoc + "."),
                "@param {string} expression The expression, declared types by full metadata name.\n"
                + "@returns {import('./" + RuntimeModule + ".js').TypedCodec<any> | null} The codec, or null when no export uses the type.");
            using (w.Block("export function forType(expression)"))
                w.Line("return BY_TYPE.get(expression) ?? null;");
            return w.ToString();
        }

        public string WriteDts()
        {
            var imports = new JsTypeEmitter.DtsImports(_context, "");
            foreach (var codec in _codecs)
                imports.Add(codec.Type);

            var w = new CodeWriter("  ");
            w.Line(JsSyntax.Header);
            WriteDeclImports(w, imports);
            w.Line("import type * as " + Sr + " from './" + RuntimeModule + ".js';");
            w.Line("import type * as " + Tj + " from './" + TypedJsonModule + ".js';");
            w.Line();
            JsSyntax.Doc(w, "The declared types, by id.");
            w.Line("export declare const SCHEMAS: " + Tj + ".Schemas;");
            for (var i = 0; i < _codecs.Count; i++)
            {
                JsSyntax.Doc(w, JsSyntax.Code(_codecs[i].Expression) + ".");
                w.Line("export declare const C$" + JsSyntax.Int(i) + ": " + Sr + ".TypedCodec<" + _codecs[i].TsType + ">;");
            }

            JsSyntax.Doc(w, Doc(ForTypeDoc + "; null when no export uses the type."));
            w.Line("export declare function forType(expression: string): " + Sr + ".TypedCodec<any> | null;");
            return w.ToString();
        }

        private static string Decl(TypeDeclIr decl, Dictionary<string, TypeDeclIr> byId)
        {
            var id = JsSyntax.String(decl.Id);
            switch (decl)
            {
                case ObjectDeclIr obj:
                {
                    // Members of the base types are part of the same array.
                    var members = new SortedDictionary<int, MemberIr>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (var current = obj; current != null && seen.Add(current.Id);
                         current = current.BaseDeclId != null && byId.TryGetValue(current.BaseDeclId, out var b) ? b as ObjectDeclIr : null)
                    {
                        foreach (var member in current.Members)
                        {
                            if (!members.ContainsKey(member.Key))
                                members[member.Key] = member;
                        }
                    }

                    var list = string.Concat(members.Values.Select(m => ", " + Tj + ".member(" + JsSyntax.Int(m.Key) + ", " + Shape(m.Type) + ")"));
                    return Tj + ".object(" + id + ", " + JsSyntax.Int(obj.ArrayLength) + ", " + (obj.IsStruct ? "true" : "false") + list + ")";
                }
                case UnionDeclIr union:
                    return Tj + ".union(" + id + string.Concat(union.Cases.Select(c => ", " + JsSyntax.Int(c.Tag) + ", " + JsSyntax.String(c.CaseDeclId))) + ")";
                case EnumDeclIr enumDecl:
                    return Tj + ".enumOf(" + id + ", " + PrimConstant(enumDecl.Underlying) + ")";
                default:
                    throw new InvalidOperationException("Unknown declaration " + decl.GetType().Name + ".");
            }
        }
    }
}
