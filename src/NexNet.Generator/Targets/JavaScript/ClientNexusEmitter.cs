using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.JavaScript;

/// <summary>
/// Writes <c>&lt;Stem&gt;ClientNexusBase extends ClientNexusBase</c> into an export module (runtime contract §3.4): a method
/// per client nexus method, which the user implements, and the generated <c>_dispatch</c> switch that reads the
/// arguments, calls the method and reports its result through the <c>DispatchContext</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The methods are abstract in the declarations. In JavaScript the base class has a method per C# method that
/// throws until a subclass implements it (a class field works as well), and constructing the base class itself throws.
/// Implementations may be async: C# <c>void</c> → <c>void</c>, <c>ValueTask</c> → <c>void | Promise&lt;void&gt;</c>,
/// <c>ValueTask&lt;T&gt;</c> → <c>T | Promise&lt;T&gt;</c>.</item>
/// <item><c>_dispatch</c> awaits the method, so a failure, sync or async, reaches the runtime (logged; sent to the server for
/// methods that return a result), and pipe and channel methods keep their pipe open until the method's promise settles
/// (amendment C-3): the runtime releases it once <c>_dispatch</c> completes.</item>
/// <item>A <c>CancellationToken</c> parameter receives <c>ctx.signal</c>, which the server's cancellation and the session's
/// end abort.</item>
/// <item><c>[NexusMethod(SeparatePool = true)]</c> methods are listed by an override of <c>_separatePool</c>.</item>
/// <item>A method named like a <c>ClientNexusBase</c> member gets a trailing '_'.</item>
/// </list>
/// </remarks>
internal static class JsClientNexusEmitter
{
    /// <summary>The members of <c>ClientNexusBase</c> a generated method must not override.</summary>
    internal static readonly string[] ReservedMembers =
    {
        "context", "onConnected", "onDisconnected", "onReconnecting", "onInvocationException", "_separatePool", "_dispatch",
    };

    private static string Summary(InterfaceIr api)
        => "The client nexus " + JsSyntax.Code(api.ClrName) + ": extend it and implement the methods the server calls.";

    private static string Remarks(JsExportNames names)
        => "Pass an instance to " + JsSyntax.Code(names.Factory + "(config, nexus)") + ". Override " + JsSyntax.Code("onConnected") + ", "
           + JsSyntax.Code("onDisconnected") + " and " + JsSyntax.Code("onReconnecting") + " for the session's lifecycle; "
           + JsSyntax.Code("this.context") + " is the connection (" + JsSyntax.Code("proxy") + ", " + JsSyntax.Code("sessionId") + "…).";

    private static string MethodSummary(InterfaceIr api, MethodIr method)
        => method.Docs ?? "Called when the server invokes " + JsSyntax.Code(api.Name + "." + method.Name) + ".";

    /// <summary>Writes the JavaScript class.</summary>
    public static void WriteJs(CodeWriter w, JsContext context, ExportIr export, JsExportNames names, JsProxyEmitter.Formatters formatters)
    {
        var api = export.ClientApi;
        JsSyntax.Doc(w, Summary(api), Remarks(names));
        using (w.Block("export class " + names.ClientNexusBase + " extends " + JsSyntax.Rt + ".ClientNexusBase"))
        {
            JsSyntax.Doc(w, "Creates the nexus. Throws when called on " + JsSyntax.Code(names.ClientNexusBase) + " itself: extend it.");
            using (w.Block("constructor()"))
            {
                w.Line("super();");
                using (w.Block("if (new.target === " + names.ClientNexusBase + ")"))
                    w.Line("throw new TypeError(" + JsSyntax.String(names.ClientNexusBase + " is abstract: extend it and implement its methods.") + ");");
            }

            foreach (var method in api.Methods)
            {
                var name = JsProxyEmitter.MethodName(context, method.Name, ReservedMembers);
                var parameters = method.Params.Select(p => JsProxyEmitter.ParamName(context, p));
                w.Line();
                JsSyntax.Doc(w, MethodSummary(api, method), "Abstract: implement it in the subclass.");
                using (w.Block(name + "(" + string.Join(", ", parameters) + ")"))
                    w.Line("throw new Error(" + JsSyntax.String(names.ClientNexusBase + "." + name + " is not implemented.") + ");");
            }

            w.Line();
            JsSyntax.Doc(w, "Runs one server invocation: reads its arguments, calls the method and reports the result. Generated.");
            using (w.Block("async _dispatch($id, $r, $ctx)"))
            {
                using (w.Block("switch ($id)"))
                {
                    foreach (var method in api.Methods)
                    {
                        using (w.Block("case " + JsSyntax.Int(method.Id) + ":"))
                            WriteCase(w, context, method, formatters);
                    }

                    w.Line("default:");
                    w.Indent().Line("return super._dispatch($id, $r, $ctx);").Unindent();
                }
            }

            var separate = api.Methods.Where(m => m.SeparatePool).Select(m => "$id === " + JsSyntax.Int(m.Id)).ToArray();
            if (separate.Length > 0)
            {
                w.Line();
                JsSyntax.Doc(w, "The methods marked " + JsSyntax.Code("[NexusMethod(SeparatePool = true)]") + " run in the separate invocation pool.");
                using (w.Block("_separatePool($id)"))
                    w.Line("return " + string.Join(" || ", separate) + ";");
            }
        }
    }

    private static void WriteCase(CodeWriter w, JsContext context, MethodIr method, JsProxyEmitter.Formatters formatters)
    {
        if (method.SerializedCount > 0)
            w.Line("$r.readArrayHeader();");

        foreach (var (param, index) in JsProxyEmitter.WireOrder(method))
        {
            var local = "a" + JsSyntax.Int(index);
            if (param.Kind is ParamKind.DuplexPipe or ParamKind.DuplexChannel)
                w.Line("const " + local + " = $r.readByte();");
            else if (param.Kind == ParamKind.Value)
                w.Line("const " + local + " = " + formatters.For(JsProxyEmitter.Value(param)) + ".read($r);");
        }

        var args = method.Params.Select((p, i) => p.Kind switch
        {
            ParamKind.CancellationToken => "$ctx.signal",
            ParamKind.DuplexPipe => "$ctx.pipe(a" + JsSyntax.Int(i) + ")",
            ParamKind.DuplexChannel => "$ctx.channel(a" + JsSyntax.Int(i) + ", " + formatters.For(JsProxyEmitter.ChannelItem(method, p)) + ")",
            _ => "a" + JsSyntax.Int(i),
        });
        var call = "this." + JsProxyEmitter.MethodName(context, method.Name, ReservedMembers) + "(" + string.Join(", ", args) + ")";

        if (method.PipeKind == PipeKind.None && method.ReturnKind == ReturnKind.TaskOf && method.ReturnType != null)
            w.Line("$ctx.writeResult(" + formatters.For(method.ReturnType) + ", await " + call + ");");
        else
            w.Line("await " + call + ";");
        w.Line("return;");
    }

    /// <summary>Writes the class declaration.</summary>
    public static void WriteDts(CodeWriter w, JsContext context, ExportIr export, JsExportNames names)
    {
        var api = export.ClientApi;
        JsSyntax.Doc(w, Summary(api), Remarks(names));
        using (w.Block("export declare abstract class " + names.ClientNexusBase + " extends " + JsSyntax.Rt + ".ClientNexusBase<" + names.Proxy + ">"))
        {
            JsSyntax.Doc(w, "Creates the nexus.");
            w.Line("constructor();");
            foreach (var method in api.Methods)
            {
                var name = JsProxyEmitter.MethodName(context, method.Name, ReservedMembers);
                var parameters = method.Params.Select(p => JsProxyEmitter.ParamName(context, p) + ": " + p.Kind switch
                {
                    ParamKind.CancellationToken => "AbortSignal",
                    ParamKind.DuplexPipe or ParamKind.DuplexChannel => JsProxyEmitter.PipeTsType(context, method, p),
                    _ => JsProxyEmitter.TsType(context, JsProxyEmitter.Value(p)),
                });

                var tags = new List<string>();
                foreach (var token in method.Params.Where(p => p.Kind == ParamKind.CancellationToken))
                    tags.Add("@param " + JsProxyEmitter.ParamName(context, token) + " Aborted when the server cancels the invocation or the session ends.");
                string returns;
                if (method.PipeKind != PipeKind.None)
                {
                    returns = "void | Promise<void>";
                    tags.Add("@returns Settles when the method is done with its pipe; the runtime then releases it.");
                }
                else if (method.ReturnKind == ReturnKind.Void)
                {
                    returns = "void";
                }
                else if (method.ReturnKind == ReturnKind.TaskOf && method.ReturnType != null)
                {
                    var result = JsProxyEmitter.TsType(context, method.ReturnType);
                    returns = Wrap(result) + " | Promise<" + result + ">";
                    tags.Add("@returns The result, sent to the server.");
                }
                else
                {
                    returns = "void | Promise<void>";
                    tags.Add("@returns Settles when the method completes; the server's call then completes.");
                }

                JsSyntax.Doc(w, MethodSummary(api, method), string.Join("\n", tags));
                w.Line("protected abstract " + name + "(" + string.Join(", ", parameters) + "): " + returns + ";");
            }
        }
    }

    /// <summary>A union type in parentheses, so another union can follow it.</summary>
    private static string Wrap(string type) => type.IndexOf('|') >= 0 || type.IndexOf("=>", StringComparison.Ordinal) >= 0 ? "(" + type + ")" : type;
}
