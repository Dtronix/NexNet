using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.CSharpTest;

/// <summary>
/// Emits <c>Remote_&lt;IServerNexus&gt;</c>: implements the server nexus interface by forwarding every call to an
/// <c>IRemoteInvoker</c> with typed-JSON arguments. Cancellation tokens become remote tokens (with an overload taking
/// <c>RemoteCancel</c>, e.g. <c>RemoteCancel.After(ms)</c>); pipe and channel parameters get an overload taking an
/// <c>IRemotePipe</c>; collections are exposed as <c>IRemoteList&lt;T&gt;</c>.
/// </summary>
internal static class RemoteProxyEmitter
{
    private const string Rt = "global::NexNet.Interop.";
    private const string JsonArray = "global::System.Text.Json.Nodes.JsonArray";

    /// <summary>Members the generated class declares besides the interface members.</summary>
    private static readonly string[] Reserved = { "Invoker", "Create", "RemoteMethods", "RemoteCollections", "__Schemas" };

    public static string ClassName(ScaffoldInterface model) => "Remote_" + model.Name;

    /// <summary>NEXNET049 problem of the interface, or null.</summary>
    public static string? Check(ScaffoldInterface model, string className)
    {
        foreach (var name in model.Methods.Select(m => m.Ir.Name).Concat(model.Collections.Select(c => c.Ir.Name)))
        {
            if (name == className || Array.IndexOf(Reserved, name) >= 0)
                return "the member '" + name + "' collides with a member of the generated remote proxy '" + className + "'";
        }

        return null;
    }

    public static string Emit(ScaffoldInterface model, string className)
    {
        var w = new CodeWriter();
        var codecs = new CodecTable(className);
        var ns = ScaffoldFile.Begin(w, model.Namespace);

        w.Line("/// <summary>");
        w.Line("/// Remote proxy of <see cref=\"" + model.FullName + "\"/>: every call is forwarded to the client under test through an");
        w.Line("/// <see cref=\"" + Rt + "IRemoteInvoker\"/>.");
        w.Line("/// </summary>");
        using (w.Block((model.IsPublic ? "public" : "internal") + " sealed partial class " + className + " : " + model.FullName + ", " + Rt + "IRemoteProxy<" + className + ">"))
        {
            using (w.Block("public " + className + "(" + Rt + "IRemoteInvoker invoker)"))
                w.Line("Invoker = invoker ?? throw new global::System.ArgumentNullException(nameof(invoker));");
            w.Line();
            w.Line("public " + Rt + "IRemoteInvoker Invoker { get; }");
            w.Line();
            w.Line("public static " + className + " Create(" + Rt + "IRemoteInvoker invoker) => new " + className + "(invoker);");

            foreach (var method in model.Methods)
            {
                w.Line();
                WriteMethod(w, method, className, codecs);
            }

            foreach (var collection in model.Collections)
            {
                var item = collection.ItemType;
                var list = Rt + "IRemoteList<" + item + ">";
                var field = "__list_" + collection.Ir.Name;
                w.Line();
                w.Line("private " + list + "? " + field + ";");
                w.Line();
                w.Line("public " + list + " " + collection.Name + " => " + field + " ??= this.Invoker.GetList(" + className + ".RemoteCollections." + collection.Name + ", "
                       + codecs.Codec(item, collection.Ir.Item!) + ");");
                w.Line();
                var notSupported = NotSupported("A remote proxy exposes '" + collection.Ir.Name + "' as " + list + ": use the property of the proxy class.");
                var accessors = "get => throw " + notSupported + ";" + (collection.HasSetter ? " set => throw " + notSupported + ";" : "");
                w.Line(collection.PropertyType + " " + collection.DeclaringInterface + "." + collection.Name + " { " + accessors + " }");
            }

            foreach (var member in model.UnsupportedMembers)
            {
                w.Line();
                w.Line(member);
            }

            w.Line();
            using (w.Block("public static class RemoteMethods"))
            {
                foreach (var method in model.Methods)
                {
                    var names = method.Values.Select(v => CSharpNames.Literal(v.Ir.Name)).ToArray();
                    w.Line("public static readonly " + Rt + "RemoteMethod " + method.Name + " = new " + Rt + "RemoteMethod("
                           + CSharpNames.Literal(model.MetadataName) + ", " + CSharpNames.Literal(method.Ir.Name) + ", "
                           + method.Ir.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", "
                           + Rt + "RemoteReturnKind." + method.Ir.ReturnKind + ", " + Rt + "RemotePipeKind." + method.Ir.PipeKind + ", "
                           + (method.Ir.HasCancellation ? "true" : "false") + ", "
                           + (names.Length == 0 ? "global::System.Array.Empty<string>()" : "new string[] { " + string.Join(", ", names) + " }") + ");");
                }
            }

            w.Line();
            using (w.Block("public static class RemoteCollections"))
            {
                foreach (var collection in model.Collections)
                {
                    w.Line("public static readonly " + Rt + "RemoteCollection " + collection.Name + " = new " + Rt + "RemoteCollection("
                           + CSharpNames.Literal(model.MetadataName) + ", " + CSharpNames.Literal(collection.Ir.Name) + ", "
                           + collection.Ir.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", "
                           + Rt + "RemoteCollectionMode." + collection.Ir.Mode + ");");
                }
            }

            w.Line();
            codecs.Write(w, model.Decls);
        }

        ns?.Dispose();
        return w.ToString();
    }

    private static string NotSupported(string message)
        => "new global::System.NotSupportedException(" + CSharpNames.Literal(message) + ")";

    private static void WriteMethod(CodeWriter w, ScaffoldMethod method, string className, CodecTable codecs)
    {
        var pipe = method.Pipe;
        var cancel = method.Cancellation;

        // The interface implementation: public unless the method takes a pipe, which cannot be remoted.
        var interfaceParams = string.Join(", ", method.Params.Select(p => p.Type + " " + p.Name + (p.DefaultValue != null ? " = " + p.DefaultValue : "")));
        if (pipe == null)
        {
            WriteForwarder(w, method, className, codecs, interfaceParams,
                cancel == null ? Rt + "RemoteCancel.None" : Rt + "RemoteCancel.FromToken(" + cancel.Name + ")",
                "null");
        }
        else
        {
            var plain = string.Join(", ", method.Params.Select(p => p.Type + " " + p.Name));
            w.Line(method.ReturnType + " " + method.DeclaringInterface + "." + method.Name + "(" + plain + ") => throw "
                   + NotSupported("A pipe cannot be remoted: call the overload of '" + method.Ir.Name + "' that takes a " + Rt + "IRemotePipe.") + ";");
        }

        // The remote overload: RemoteCancel for the token, IRemotePipe for the pipe or channel.
        if (pipe != null || cancel != null)
        {
            var remoteParams = string.Join(", ", method.Params.Select(p => p.Ir.Kind switch
            {
                ParamKind.CancellationToken => Rt + "RemoteCancel " + p.Name,
                ParamKind.DuplexPipe or ParamKind.DuplexChannel => Rt + "IRemotePipe " + p.Name,
                _ => p.Type + " " + p.Name,
            }));
            w.Line();
            WriteForwarder(w, method, className, codecs, remoteParams,
                cancel == null ? Rt + "RemoteCancel.None" : cancel.Name,
                pipe == null ? "null" : pipe.Name);
        }
    }

    private static void WriteForwarder(CodeWriter w, ScaffoldMethod method, string className, CodecTable codecs, string parameters, string cancel, string pipe)
    {
        var values = method.Values.Select(v => codecs.Codec(v.Type, v.Ir.Type!) + ".Encode(" + v.Name + ")").ToArray();
        var args = "new " + JsonArray + "(" + string.Join(", ", values) + ")";
        var invocation = "new " + Rt + "RemoteInvocation(" + className + ".RemoteMethods." + method.Name + ", " + args + ", " + cancel + ", " + pipe + ")";

        switch (method.Ir.ReturnKind)
        {
            case ReturnKind.TaskOf:
                w.Line("public async " + method.ReturnType + " " + method.Name + "(" + parameters + ")");
                w.Indent().Line("=> " + codecs.Codec(method.ResultType!, method.Ir.ReturnType!) + ".Decode(await this.Invoker.InvokeWithResultAsync(" + invocation + ").ConfigureAwait(false))!;").Unindent();
                break;
            case ReturnKind.Task:
                w.Line("public " + method.ReturnType + " " + method.Name + "(" + parameters + ")");
                w.Indent().Line("=> this.Invoker.InvokeAsync(" + invocation + ");").Unindent();
                break;
            default:
                w.Line("public " + method.ReturnType + " " + method.Name + "(" + parameters + ")");
                w.Indent().Line("=> this.Invoker.Invoke(" + invocation + ");").Unindent();
                break;
        }
    }
}
