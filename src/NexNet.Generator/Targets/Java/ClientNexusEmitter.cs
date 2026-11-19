using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.Java;

/// <summary>
/// Emits <c>&lt;Stem&gt;ClientNexusBase extends ClientNexusBase&lt;&lt;Stem&gt;ServerProxy&gt;</c> (runtime contract §3.4): an
/// abstract method per client nexus method, which the user implements, and the generated <c>dispatch</c> switch that
/// reads the arguments, calls the method and hands its result to the <c>InvocationContext</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>C# <c>void</c> → <c>void</c> (<c>noResult()</c>); <c>ValueTask</c> → <c>CompletableFuture&lt;Void&gt;</c>
/// (<c>result(future)</c>); <c>ValueTask&lt;T&gt;</c> → <c>CompletableFuture&lt;T&gt;</c> (<c>result(future, formatter)</c>).</item>
/// <item>Pipe and channel methods return <c>result(future)</c>, so the runtime keeps the pipe open until the method
/// completes (amendment C-3).</item>
/// <item>A <c>CancellationToken</c> parameter receives <c>context.cancellationToken()</c>, which the server's
/// cancellation and the session's end cancel.</item>
/// <item><c>[NexusMethod(SeparatePool = true)]</c> methods are listed by an override of <c>separatePool</c>.</item>
/// <item>A method named like a <c>ClientNexusBase</c> member gets a trailing '_'.</item>
/// </list>
/// </remarks>
internal static class ClientNexusEmitter
{
    /// <summary>The members of <c>ClientNexusBase</c> a generated method must not override or overload.</summary>
    internal static readonly string[] ReservedMembers =
    {
        "context", "onConnected", "onDisconnected", "onReconnecting", "dispatch", "separatePool", "onInvocationException", "bind",
    };

    public static string Emit(JavaContext context, ExportIr export, JavaExportNames names)
    {
        var api = export.ClientApi;
        var imports = new ProxyEmitter.Imports();
        imports.Add("com.dtronix.nexnet.spi.ClientNexusBase");
        imports.Add("com.dtronix.nexnet.spi.InvocationContext");
        imports.Add("com.dtronix.nexnet.serialization.MsgPackReader");
        imports.Add("java.util.concurrent.CompletableFuture");
        var fields = new ProxyEmitter.FormatterFields(context);

        var methods = new CodeWriter();
        foreach (var method in api.Methods)
        {
            methods.Line();
            var tags = new List<string>();
            if (method.PipeKind != PipeKind.None)
                tags.Add("@return a future that completes when the method is done with its pipe; the runtime then closes it");
            else if (method.ReturnKind != ReturnKind.Void)
                tags.Add("@return a future of the result, sent to the server when it completes");
            JavaSyntax.Javadoc(methods, ProxyEmitter.MethodDoc(method, "Called when the server invokes " + ProxyEmitter.ClrCode(api.Name + "." + method.Name) + "."), tags.ToArray());
            var parameters = string.Join(", ", method.Params.Select(p => ProxyEmitter.ParamType(context, method, p, imports) + " " + ProxyEmitter.ParamName(context, p)));
            methods.Line("protected abstract " + ProxyEmitter.ReturnType(context, method, imports) + " "
                         + ProxyEmitter.MethodName(context, method.Name, ReservedMembers) + "(" + parameters + ");");
        }

        var dispatch = new CodeWriter();
        dispatch.Line();
        dispatch.Line("@Override");
        using (dispatch.Block("protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments, InvocationContext context)"))
        {
            using (dispatch.Block("switch (methodId)"))
            {
                foreach (var method in api.Methods)
                {
                    using (dispatch.Block("case " + JavaSyntax.Int(method.Id) + ":"))
                        WriteCase(dispatch, context, method, fields);
                }

                dispatch.Line("default:");
                dispatch.Indent().Line("throw new IllegalArgumentException(\"Unknown method id \" + methodId);").Unindent();
            }
        }

        var separate = api.Methods.Where(m => m.SeparatePool).Select(m => "methodId == " + JavaSyntax.Int(m.Id)).ToArray();
        if (separate.Length > 0)
        {
            dispatch.Line();
            dispatch.Line("@Override");
            using (dispatch.Block("protected boolean separatePool(int methodId)"))
                dispatch.Line("return " + string.Join(" || ", separate) + ";");
        }

        if (fields.Any)
            imports.Add("com.dtronix.nexnet.serialization.Formatter");

        var w = new CodeWriter();
        w.Line(JavaSyntax.Header);
        w.Line("package " + context.Package + ";");
        w.Line();
        imports.Write(w);
        JavaSyntax.Javadoc(w, "The client nexus " + ProxyEmitter.ClrCode(api.ClrName) + ": extend it and implement the methods the server calls.",
            "<p>Pass an instance to {@link " + names.Client + "#create(com.dtronix.nexnet.client.ClientConfig, " + names.ClientNexusBase + ")}.",
            "The methods run on the client's invocation executor, never on the network thread. Override {@code onConnected},",
            "{@code onDisconnected} and {@code onReconnecting} for the session's lifecycle; {@code context()} is the session.");
        using (w.Block("public abstract class " + names.ClientNexusBase + " extends ClientNexusBase<" + names.Proxy + ">"))
        {
            fields.Write(w);
            JavaSyntax.Javadoc(w, "Creates the nexus.");
            using (w.Block("protected " + names.ClientNexusBase + "()"))
            {
            }

            var text = methods.ToString() + dispatch.ToString();
            w.Lines(text.TrimEnd('\n'));
        }

        return w.ToString();
    }

    private static void WriteCase(CodeWriter w, JavaContext context, MethodIr method, ProxyEmitter.FormatterFields fields)
    {
        if (method.SerializedCount > 0)
            w.Line("arguments.readArrayHeader();");

        foreach (var (param, index) in ProxyEmitter.WireOrder(method))
        {
            var local = "a" + JavaSyntax.Int(index);
            if (param.Kind is ParamKind.DuplexPipe or ParamKind.DuplexChannel)
                w.Line("int " + local + " = arguments.readInt();");
            else if (param.Kind == ParamKind.Value)
                w.Line(context.Qualified.Map(ProxyEmitter.Value(param), context.Scope).Name + " " + local + " = " + fields.For(ProxyEmitter.Value(param)) + ".read(arguments);");
        }

        var args = method.Params.Select((p, i) => p.Kind switch
        {
            ParamKind.CancellationToken => "context.cancellationToken()",
            ParamKind.DuplexPipe => "context.pipe(a" + JavaSyntax.Int(i) + ")",
            ParamKind.DuplexChannel => "context.channel(a" + JavaSyntax.Int(i) + ", " + fields.For(ProxyEmitter.ChannelItem(method, p)) + ")",
            _ => "a" + JavaSyntax.Int(i),
        });
        var call = ProxyEmitter.MethodName(context, method.Name, ReservedMembers) + "(" + string.Join(", ", args) + ")";

        if (method.PipeKind != PipeKind.None || method.ReturnKind == ReturnKind.Task || (method.ReturnKind == ReturnKind.TaskOf && method.ReturnType == null))
        {
            w.Line("return context.result(" + call + ");");
        }
        else if (method.ReturnKind == ReturnKind.Void)
        {
            w.Line(call + ";");
            w.Line("return context.noResult();");
        }
        else
        {
            w.Line("return context.result(" + call + ", " + fields.For(method.ReturnType!) + ");");
        }
    }
}
