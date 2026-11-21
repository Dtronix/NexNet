using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.CSharpTest;

/// <summary>
/// Emits <c>&lt;ClientNexus&gt;Script</c>: a <c>ScriptFacade</c> with one <c>ScriptSlot&lt;TArgs, TResult&gt;</c> per method of
/// the client nexus interface, a typed arguments record per method with serialized parameters, and the codecs.
/// </summary>
internal static class ScriptFacadeEmitter
{
    private const string Rt = "global::NexNet.Interop.";
    private const string JsonArray = "global::System.Text.Json.Nodes.JsonArray";

    /// <summary>Members the generated class declares or inherits besides the slots.</summary>
    private static readonly string[] Reserved =
    {
        "OnConnected", "OnDisconnected", "Slots", "Find", "ClientInterface", "Add", "ScriptMethods", "__Schemas",
        "Equals", "GetHashCode", "ToString", "GetType", "MemberwiseClone", "Finalize",
    };

    /// <summary>Members of a record that a positional parameter must not take.</summary>
    private static readonly string[] RecordReserved =
    {
        "EqualityContract", "Equals", "GetHashCode", "ToString", "Deconstruct", "PrintMembers", "GetType", "MemberwiseClone", "Finalize", "__Codec",
    };

    public static string ClassName(ScaffoldInterface model) => TargetExtractor.DefaultExportName(model.Name) + "Script";

    private static string ArgsName(ScaffoldMethod method) => method.Ir.Name + "Args";

    private static bool HasArgs(ScaffoldMethod method) => method.Values.Any();

    /// <summary>NEXNET049 problem of the interface, or null.</summary>
    public static string? Check(ScaffoldInterface model, string className)
    {
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var method in model.Methods)
        {
            var names = HasArgs(method) ? new[] { method.Ir.Name, ArgsName(method) } : new[] { method.Ir.Name };
            foreach (var name in names)
            {
                if (name == className || Array.IndexOf(Reserved, name) >= 0)
                    return "the member '" + name + "' collides with a member of the generated script facade '" + className + "'";
                if (members.TryGetValue(name, out var other))
                    return "the generated member '" + name + "' of '" + className + "' is declared for both '" + other + "' and '" + method.Ir.Name + "'";
                members[name] = method.Ir.Name;
            }

            foreach (var value in method.Values)
            {
                if (value.Ir.Name == ArgsName(method) || Array.IndexOf(RecordReserved, value.Ir.Name) >= 0)
                    return "the parameter '" + value.Ir.Name + "' of '" + method.Ir.Name + "' collides with a member of the generated record '" + ArgsName(method) + "'";
            }
        }

        return null;
    }

    public static string Emit(ScaffoldInterface model, string className)
    {
        var w = new CodeWriter();
        var codecs = new CodecTable(className);
        var ns = ScaffoldFile.Begin(w, model.Namespace);

        string ArgsType(ScaffoldMethod m) => HasArgs(m) ? className + "." + ArgsName(m) : Rt + "NoArgs";
        string ResultType(ScaffoldMethod m) => m.Ir.ReturnKind == ReturnKind.TaskOf ? m.ResultType! : Rt + "NoResult";
        string SlotType(ScaffoldMethod m) => Rt + "ScriptSlot<" + ArgsType(m) + ", " + ResultType(m) + ">";

        w.Line("/// <summary>");
        w.Line("/// Script facade of <see cref=\"" + model.FullName + "\"/>: what the client under test does when the server invokes");
        w.Line("/// each method.");
        w.Line("/// </summary>");
        using (w.Block((model.IsPublic ? "public" : "internal") + " sealed partial class " + className + " : " + Rt + "ScriptFacade"))
        {
            w.Line("public " + className + "()");
            w.Indent().Line(": base(" + CSharpNames.Literal(model.MetadataName) + ")").Unindent();
            using (w.Block(""))
            {
                foreach (var method in model.Methods)
                {
                    var argsCodec = HasArgs(method) ? className + "." + ArgsName(method) + ".__Codec" : Rt + "NoArgs.Codec";
                    var resultCodec = method.Ir.ReturnKind == ReturnKind.TaskOf ? codecs.Codec(method.ResultType!, method.Ir.ReturnType!) : "null";
                    w.Line(method.Name + " = Add(new " + SlotType(method) + "(" + CSharpNames.Literal(method.Ir.Name) + ", "
                           + className + ".ScriptMethods." + method.Name + ", " + argsCodec + ", " + resultCodec + "));");
                }
            }

            foreach (var method in model.Methods)
            {
                w.Line();
                w.Line("public " + SlotType(method) + " " + method.Name + " { get; }");
            }

            foreach (var method in model.Methods.Where(HasArgs))
            {
                var values = method.Values.ToArray();
                var record = ArgsName(method);
                w.Line();
                w.Line("public sealed record " + record + "(" + string.Join(", ", values.Select(v => v.Type + " " + v.Name)) + ")");
                using (w.Block(""))
                {
                    w.Line("public static " + Rt + "ScriptArgsCodec<" + record + "> __Codec { get; } = new " + Rt + "ScriptArgsCodec<" + record + ">(");
                    w.Indent();
                    w.Line("static a => new " + record + "(" + string.Join(", ", values.Select((v, i) => codecs.Codec(v.Type, v.Ir.Type!) + ".Decode(a[" + CSharpNames.Int(i) + "])!")) + "),");
                    w.Line("static v => new " + JsonArray + "(" + string.Join(", ", values.Select(v => codecs.Codec(v.Type, v.Ir.Type!) + ".Encode(v." + v.Name + ")")) + "));");
                    w.Unindent();
                }
            }

            w.Line();
            using (w.Block("public static class ScriptMethods"))
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
            codecs.Write(w, model.Decls);
        }

        ns?.Dispose();
        return w.ToString();
    }
}
