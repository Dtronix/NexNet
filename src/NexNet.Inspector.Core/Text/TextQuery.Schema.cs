using System.Text;
using NexNet.Inspector.Core.Schema;

namespace NexNet.Inspector.Core.Text;

public sealed partial class TextQuery
{
    /// <summary>
    /// `schema`: the session's schema as compact signatures with their wire IDs: the server API's methods (`#1 Join(string name)
    /// -> UserInfo`, `(oneway)` for fire-and-forget, `-> ok` for a task without a value), its collections (`☰4 Users
    /// INexusList&lt;long&gt; ServerToClient`), the client API's methods, then the declared types they use.
    /// </summary>
    /// <param name="destination">Receives the lines.</param>
    public void Schema(List<TextRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        AddHeader(destination);
        var resolution = _session.Schema;
        var export = resolution.Schema is null ? null : _session.Decoder.Export;
        if (resolution.Schema is not { } schema || export is null)
        {
            destination.Add(Stat("schema none (bodies decode raw)"));
            return;
        }

        destination.Add(Stat(string.Create(Invariant, $"schema {export.Name} {resolution.Status.ToString().ToLowerInvariant()} hash {schema.Hash:x16}")));
        var used = new List<SchemaTypeDecl>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Api("server", export.ServerApi, destination, used, seen);
        foreach (var collection in export.ServerApi.Collections)
        {
            _sb.Append(Invariant, $"  ☰{collection.Id} {collection.Name} ");
            _sb.Append(collection.Kind == SchemaCollectionKind.List ? "INexusList" : collection.Kind.ToString()).Append('<');
            AppendType(_sb, collection.Item, used, seen).Append("> ").Append(collection.Mode.ToString());
            if (collection.IsAuthorized)
                _sb.Append(" [auth]");
            destination.Add(Stat(Text()));
        }

        Api("client", export.ClientApi, destination, used, seen);
        if (used.Count == 0)
            return;
        destination.Add(Stat("types"));
        for (var i = 0; i < used.Count; i++)
            Declaration(used[i], used, seen, destination);
    }

    private void Api(string side, SchemaInterface api, List<TextRecord> destination, List<SchemaTypeDecl> used, HashSet<string> seen)
    {
        destination.Add(Stat(string.Create(Invariant, $"{side} {api.Name} hash {api.Hash:x8} methods {api.Methods.Count}")));
        foreach (var method in api.Methods)
        {
            _sb.Append(Invariant, $"  #{method.Id} {method.Name}(");
            var first = true;
            foreach (var parameter in method.Parameters)
            {
                if (!first)
                    _sb.Append(", ");
                first = false;
                switch (parameter.Kind)
                {
                    case SchemaParamKind.CancellationToken:
                        _sb.Append("CancellationToken");
                        break;
                    case SchemaParamKind.DuplexPipe:
                        _sb.Append("DuplexPipe");
                        break;
                    case SchemaParamKind.DuplexChannel:
                        AppendType(_sb.Append("Channel<"), parameter.Type, used, seen).Append('>');
                        break;
                    default:
                        AppendType(_sb, parameter.Type, used, seen);
                        if (parameter.Nullable && parameter.Type is { RefNullable: false })
                            _sb.Append('?');
                        break;
                }

                _sb.Append(' ').Append(parameter.Name);
            }

            _sb.Append(')');
            switch (method.ReturnKind)
            {
                case SchemaReturnKind.Void:
                    _sb.Append(" (oneway)");
                    break;
                case SchemaReturnKind.Task:
                    _sb.Append(" -> ok");
                    break;
                default:
                    AppendType(_sb.Append(" -> "), method.ReturnType, used, seen);
                    break;
            }

            if (method.IsAuthorized)
                _sb.Append(" [auth]");
            destination.Add(Stat(Text()));
        }
    }

    private void Declaration(SchemaTypeDecl decl, List<SchemaTypeDecl> used, HashSet<string> seen, List<TextRecord> destination)
    {
        _sb.Append("  ").Append(decl.Name);
        switch (decl)
        {
            case SchemaObjectDecl obj:
                _sb.Append(obj.IsStruct ? " struct {" : " {");
                for (var i = 0; i < obj.Members.Count; i++)
                {
                    var member = obj.Members[i];
                    _sb.Append(i == 0 ? "" : ", ").Append(member.Name).Append(": ");
                    AppendType(_sb, member.Type, used, seen);
                }

                _sb.Append('}');
                break;
            case SchemaUnionDecl union:
                _sb.Append(" union");
                for (var i = 0; i < union.Cases.Count; i++)
                {
                    var unionCase = union.Cases[i];
                    _sb.Append(i == 0 ? " " : " | ").Append(Invariant, $"{unionCase.Tag}:").Append(unionCase.Decl?.Name ?? unionCase.CaseDeclId);
                    if (unionCase.Decl is { } caseDecl && seen.Add(caseDecl.Id))
                        used.Add(caseDecl);
                }

                break;
            case SchemaEnumDecl enumeration:
                _sb.Append(enumeration.IsFlags ? " flags" : " enum");
                for (var i = 0; i < enumeration.Members.Count; i++)
                    _sb.Append(i == 0 ? " " : " | ").Append(enumeration.Members[i].Name).Append(Invariant, $"={enumeration.Members[i].Value}");
                break;
        }

        destination.Add(Stat(Text()));
    }

    // A type as C#-like text; declared types are queued for the types section.
    private static StringBuilder AppendType(StringBuilder sb, SchemaTypeRef? type, List<SchemaTypeDecl> used, HashSet<string> seen)
    {
        if (type is null)
            return sb.Append('?');
        switch (type.Kind)
        {
            case SchemaTypeKind.Prim:
                sb.Append(PrimName(type.Prim));
                break;
            case SchemaTypeKind.Bin:
                sb.Append("byte[]");
                break;
            case SchemaTypeKind.PrimArray:
                sb.Append(PrimName(type.Prim)).Append("[]");
                break;
            case SchemaTypeKind.Seq:
                if (type.Clr is null or "Array")
                    AppendArgument(sb, type, 0, used, seen).Append("[]");
                else
                    AppendArgument(sb.Append(type.Clr).Append('<'), type, 0, used, seen).Append('>');
                break;
            case SchemaTypeKind.Set:
            case SchemaTypeKind.Queue:
            case SchemaTypeKind.Stack:
                sb.Append(type.Clr ?? type.Kind.ToString()).Append('<');
                AppendArgument(sb, type, 0, used, seen).Append('>');
                break;
            case SchemaTypeKind.Map:
                sb.Append(type.Clr ?? "Dictionary").Append('<');
                AppendArgument(sb, type, 0, used, seen).Append(", ");
                AppendArgument(sb, type, 1, used, seen).Append('>');
                break;
            case SchemaTypeKind.Tuple:
                if (type.IsKeyValuePair)
                    sb.Append("KeyValuePair<");
                else
                    sb.Append('(');
                for (var i = 0; i < type.Args.Count; i++)
                    AppendArgument(i == 0 ? sb : sb.Append(", "), type, i, used, seen);
                sb.Append(type.IsKeyValuePair ? '>' : ')');
                break;
            case SchemaTypeKind.Nullable:
                AppendArgument(sb, type, 0, used, seen).Append('?');
                return sb;
            case SchemaTypeKind.Decl:
                var decl = type.Decl;
                sb.Append(decl?.Name ?? type.DeclId);
                if (decl is not null && seen.Add(decl.Id))
                    used.Add(decl);
                if (type.Args.Count > 0)
                {
                    sb.Append('<');
                    for (var i = 0; i < type.Args.Count; i++)
                        AppendArgument(i == 0 ? sb : sb.Append(", "), type, i, used, seen);
                    sb.Append('>');
                }

                break;
            case SchemaTypeKind.TypeParam:
                sb.Append(Invariant, $"T{type.Ordinal}");
                break;
            default:
                sb.Append(type.Clr ?? "object");
                break;
        }

        if (type.RefNullable && type.Kind != SchemaTypeKind.Prim)
            sb.Append('?');
        return sb;
    }

    private static StringBuilder AppendArgument(StringBuilder sb, SchemaTypeRef type, int index, List<SchemaTypeDecl> used, HashSet<string> seen)
        => index < type.Args.Count ? AppendType(sb, type.Args[index], used, seen) : sb.Append('?');

    private static string PrimName(SchemaPrimKind prim) => prim switch
    {
        SchemaPrimKind.Bool => "bool",
        SchemaPrimKind.SByte => "sbyte",
        SchemaPrimKind.Byte => "byte",
        SchemaPrimKind.Int16 => "short",
        SchemaPrimKind.UInt16 => "ushort",
        SchemaPrimKind.Int32 => "int",
        SchemaPrimKind.UInt32 => "uint",
        SchemaPrimKind.Int64 => "long",
        SchemaPrimKind.UInt64 => "ulong",
        SchemaPrimKind.Single => "float",
        SchemaPrimKind.Double => "double",
        SchemaPrimKind.Half => "Half",
        SchemaPrimKind.Char => "char",
        SchemaPrimKind.Decimal => "decimal",
        SchemaPrimKind.String => "string",
        SchemaPrimKind.None => "?",
        _ => prim.ToString(),
    };
}
