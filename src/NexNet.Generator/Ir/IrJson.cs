using System.Globalization;
using System.Text;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Ir;

/// <summary>
/// The JSON form of the IR (<c>nexnet-ir.json</c>): deterministic (object keys sorted ordinally, two-space indent, LF
/// line endings, no timestamps or paths), null fields omitted. Source locations are in-memory only and not written.
/// </summary>
internal static class IrJson
{
    /// <summary>Writes <paramref name="ir"/> as JSON text, ending with a line feed.</summary>
    public static string Write(NexusIr ir)
    {
        var sb = new StringBuilder();
        JsonText.Write(sb, ToJson(ir), 0);
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Reads JSON written by <see cref="Write"/>. Throws <see cref="FormatException"/> on invalid input.</summary>
    public static NexusIr Read(string json)
    {
        var root = JsonText.Parse(json).AsObject();
        var version = root.Int("irVersion");
        if (version != NexusIr.CurrentVersion)
            throw new FormatException("Unsupported IR version " + version + "; this reader handles " + NexusIr.CurrentVersion + ".");

        return new NexusIr(
            version,
            Array(root, "exports", ReadExport),
            Array(root, "types", ReadDecl));
    }

    internal static string WriteExport(ExportIr export) => Text(ToJson(export));

    internal static string WriteDecl(TypeDeclIr decl) => Text(ToJson(decl));

    private static string Text(JsonNode node)
    {
        var sb = new StringBuilder();
        JsonText.Write(sb, node, 0);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ writing

    private static JsonNode ToJson(NexusIr ir) => new JsonObject()
        .Add("irVersion", ir.IrVersion)
        .Add("exports", ToArray(ir.Exports, ToJson))
        .Add("types", ToArray(ir.Types, ToJson));

    private static JsonNode ToJson(ExportIr e) => new JsonObject()
        .Add("name", e.Name)
        .Add("clrNamespace", e.ClrNamespace)
        .Add("targets", e.Targets)
        .Add("serverApi", ToJson(e.ServerApi))
        .Add("clientApi", ToJson(e.ClientApi))
        .Add("handshake", new JsonObject()
            .Add("version", e.Handshake.Version)
            .Add("serverNexusHash", e.Handshake.ServerNexusHash)
            .Add("clientNexusHash", e.Handshake.ClientNexusHash))
        .Add("clientAcceptedIds", ToArray(e.ClientAcceptedIds, a => new JsonObject().Add("hash", a.Hash).Add("id", a.Id)))
        .Add("docs", e.Docs);

    private static JsonNode ToJson(InterfaceIr i) => new JsonObject()
        .Add("clrName", i.ClrName)
        .Add("name", i.Name)
        .Add("clrNamespace", i.ClrNamespace)
        .Add("hash", i.Hash)
        .Add("methods", ToArray(i.Methods, ToJson))
        .Add("collections", ToArray(i.Collections, ToJson))
        .Add("versions", ToArray(i.Versions, v => new JsonObject()
            .Add("version", v.Version)
            .Add("hash", v.Hash)
            .Add("methodIds", ToArray(v.MethodIds, id => new JsonNumber(id)))));

    private static JsonNode ToJson(MethodIr m) => new JsonObject()
        .Add("name", m.Name)
        .Add("id", m.Id)
        .Add("hash", m.Hash)
        .Add("returnKind", m.ReturnKind.ToString())
        .Add("returnType", m.ReturnType == null ? null : ToJson(m.ReturnType))
        .Add("params", ToArray(m.Params, p => new JsonObject()
            .Add("name", p.Name)
            .Add("kind", p.Kind.ToString())
            .Add("type", p.Type == null ? null : ToJson(p.Type))
            .Add("wireIndex", p.WireIndex)
            .Add("nullable", p.Nullable)))
        .Add("serializedCount", m.SerializedCount)
        .Add("hasCancellation", m.HasCancellation)
        .Add("pipeKind", m.PipeKind.ToString())
        .Add("pipeType", m.PipeType == null ? null : ToJson(m.PipeType))
        .Add("separatePool", m.SeparatePool)
        .Add("isAuthorized", m.IsAuthorized)
        .Add("docs", m.Docs);

    private static JsonNode ToJson(CollectionIr c) => new JsonObject()
        .Add("name", c.Name)
        .Add("id", c.Id)
        .Add("hash", c.Hash)
        .Add("kind", c.Kind.ToString())
        .Add("mode", c.Mode.ToString())
        .Add("item", c.Item == null ? null : ToJson(c.Item))
        .Add("isAuthorized", c.IsAuthorized)
        .Add("docs", c.Docs);

    private static JsonNode ToJson(TypeRef t)
    {
        var o = new JsonObject().Add("kind", t.Kind.ToString());
        if (t.Prim != PrimKind.None)
            o.Add("prim", t.Prim.ToString());
        o.Add("clr", t.Clr);
        if (t.Args.Length > 0)
            o.Add("args", ToArray(t.Args, ToJson));
        o.Add("id", t.DeclId);
        if (t.Kind == TypeRefKind.TypeParam)
            o.Add("ordinal", t.Ordinal);
        if (t.RefNullable)
            o.Add("refNullable", true);
        if (t.JsNumber)
            o.Add("jsNumber", true);
        if (t.NativeFormatters.Length > 0)
            o.Add("nativeFormatters", ToArray(t.NativeFormatters, f => new JsonObject().Add("target", f.Target).Add("formatter", f.Formatter)));
        return o;
    }

    private static JsonNode ToJson(TypeDeclIr d)
    {
        var o = new JsonObject()
            .Add("id", d.Id)
            .Add("clrMetadataName", d.ClrMetadataName)
            .Add("clrNamespace", d.ClrNamespace)
            .Add("name", d.Name)
            .Add("containingTypes", ToArray(d.ContainingTypes, s => new JsonString(s)))
            .Add("docs", d.Docs);

        switch (d)
        {
            case ObjectDeclIr obj:
                return o.Add("kind", "Object")
                    .Add("isStruct", obj.IsStruct)
                    .Add("isRecord", obj.IsRecord)
                    .Add("isAbstract", obj.IsAbstract)
                    .Add("typeParams", ToArray(obj.TypeParams, s => new JsonString(s)))
                    .Add("baseDeclId", obj.BaseDeclId)
                    .Add("arrayLength", obj.ArrayLength)
                    .Add("isRecursive", obj.IsRecursive)
                    .Add("members", ToArray(obj.Members, m => new JsonObject()
                        .Add("name", m.Name)
                        .Add("key", m.Key)
                        .Add("type", ToJson(m.Type))
                        .Add("refNullable", m.RefNullable)
                        .Add("wasNonPublic", m.WasNonPublic)
                        .Add("defaultLiteral", m.DefaultLiteral == null
                            ? null
                            : new JsonObject().Add("kind", m.DefaultLiteral.Kind.ToString()).Add("value", m.DefaultLiteral.Value))
                        .Add("docs", m.Docs)));

            case UnionDeclIr union:
                return o.Add("kind", "Union")
                    .Add("isInterface", union.IsInterface)
                    .Add("typeParams", ToArray(union.TypeParams, s => new JsonString(s)))
                    .Add("isRecursive", union.IsRecursive)
                    .Add("cases", ToArray(union.Cases, c => new JsonObject()
                        .Add("tag", c.Tag)
                        .Add("caseDeclId", c.CaseDeclId)
                        .Add("inheritanceDepth", c.InheritanceDepth)));

            case EnumDeclIr e:
                return o.Add("kind", "Enum")
                    .Add("underlying", e.Underlying.ToString())
                    .Add("isFlags", e.IsFlags)
                    .Add("members", ToArray(e.Members, m => new JsonObject().Add("name", m.Name).Add("value", m.Value)));
        }

        throw new InvalidOperationException("Unknown declaration " + d.GetType().Name);
    }

    private static JsonArray ToArray<T>(EquatableArray<T> items, Func<T, JsonNode> convert)
        where T : IEquatable<T>
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Items.Add(convert(item));
        return array;
    }

    // ------------------------------------------------------------------ reading

    private static EquatableArray<T> Array<T>(JsonObject o, string key, Func<JsonObject, T> read)
        where T : IEquatable<T>
    {
        if (!o.TryGet(key, out var node))
            return EquatableArray<T>.Empty;
        return new EquatableArray<T>(node.AsArray().Items.Select(i => read(i.AsObject())).ToArray());
    }

    private static EquatableArray<T> ScalarArray<T>(JsonObject o, string key, Func<JsonNode, T> read)
        where T : IEquatable<T>
    {
        if (!o.TryGet(key, out var node))
            return EquatableArray<T>.Empty;
        return new EquatableArray<T>(node.AsArray().Items.Select(read).ToArray());
    }

    private static ExportIr ReadExport(JsonObject o)
    {
        var handshake = o.Object("handshake");
        return new ExportIr(
            o.String("name"),
            o.String("clrNamespace"),
            o.Int("targets"),
            ReadInterface(o.Object("serverApi")),
            ReadInterface(o.Object("clientApi")),
            new HandshakeIr(handshake.OptString("version"), handshake.Int("serverNexusHash"), handshake.Int("clientNexusHash")),
            Array(o, "clientAcceptedIds", a => new AcceptedIdIr(a.Int("hash"), a.UShort("id"))),
            o.OptString("docs"),
            null);
    }

    private static InterfaceIr ReadInterface(JsonObject o) => new(
        o.String("clrName"),
        o.String("name"),
        o.String("clrNamespace"),
        o.Int("hash"),
        Array(o, "methods", ReadMethod),
        Array(o, "collections", c => new CollectionIr(
            c.String("name"),
            c.UShort("id"),
            c.Int("hash"),
            c.Enum<CollectionKind>("kind"),
            c.Enum<CollectionMode>("mode"),
            c.TryGet("item", out var item) ? ReadTypeRef(item.AsObject()) : null,
            c.Bool("isAuthorized"),
            c.OptString("docs"))),
        Array(o, "versions", v => new VersionIr(
            v.String("version"),
            v.Int("hash"),
            ScalarArray(v, "methodIds", n => checked((ushort)n.AsLong())))));

    private static MethodIr ReadMethod(JsonObject o) => new(
        o.String("name"),
        o.UShort("id"),
        o.Int("hash"),
        o.Enum<ReturnKind>("returnKind"),
        o.TryGet("returnType", out var returnType) ? ReadTypeRef(returnType.AsObject()) : null,
        Array(o, "params", p => new ParamIr(
            p.String("name"),
            p.Enum<ParamKind>("kind"),
            p.TryGet("type", out var type) ? ReadTypeRef(type.AsObject()) : null,
            p.Int("wireIndex"),
            p.Bool("nullable"))),
        o.Int("serializedCount"),
        o.Bool("hasCancellation"),
        o.Enum<PipeKind>("pipeKind"),
        o.TryGet("pipeType", out var pipeType) ? ReadTypeRef(pipeType.AsObject()) : null,
        o.Bool("separatePool"),
        o.Bool("isAuthorized"),
        o.OptString("docs"),
        null);

    private static TypeRef ReadTypeRef(JsonObject o) => new(o.Enum<TypeRefKind>("kind"))
    {
        Prim = o.TryGet("prim", out _) ? o.Enum<PrimKind>("prim") : PrimKind.None,
        Clr = o.OptString("clr"),
        Args = Array(o, "args", ReadTypeRef),
        DeclId = o.OptString("id"),
        Ordinal = o.TryGet("ordinal", out _) ? o.Int("ordinal") : 0,
        RefNullable = o.TryGet("refNullable", out _) && o.Bool("refNullable"),
        JsNumber = o.TryGet("jsNumber", out _) && o.Bool("jsNumber"),
        NativeFormatters = Array(o, "nativeFormatters", f => new NativeFormatterIr(f.Int("target"), f.String("formatter"))),
    };

    private static TypeDeclIr ReadDecl(JsonObject o)
    {
        var id = o.String("id");
        var clrMetadataName = o.String("clrMetadataName");
        var ns = o.String("clrNamespace");
        var name = o.String("name");
        var containing = ScalarArray(o, "containingTypes", n => n.AsString());
        var docs = o.OptString("docs");

        switch (o.String("kind"))
        {
            case "Object":
                return new ObjectDeclIr(id, clrMetadataName, ns, name, containing, docs,
                    o.Bool("isStruct"),
                    o.Bool("isRecord"),
                    o.Bool("isAbstract"),
                    ScalarArray(o, "typeParams", n => n.AsString()),
                    o.OptString("baseDeclId"),
                    o.Int("arrayLength"),
                    o.Bool("isRecursive"),
                    Array(o, "members", m => new MemberIr(
                        m.String("name"),
                        m.Int("key"),
                        ReadTypeRef(m.Object("type")),
                        m.Bool("refNullable"),
                        m.Bool("wasNonPublic"),
                        m.TryGet("defaultLiteral", out var literal)
                            ? new LiteralIr(literal.AsObject().Enum<LiteralKind>("kind"), literal.AsObject().OptString("value"))
                            : null,
                        m.OptString("docs"))));

            case "Union":
                return new UnionDeclIr(id, clrMetadataName, ns, name, containing, docs,
                    o.Bool("isInterface"),
                    ScalarArray(o, "typeParams", n => n.AsString()),
                    o.Bool("isRecursive"),
                    Array(o, "cases", c => new UnionCaseIr(c.UShort("tag"), c.String("caseDeclId"), c.Int("inheritanceDepth"))));

            case "Enum":
                return new EnumDeclIr(id, clrMetadataName, ns, name, containing, docs,
                    o.Enum<PrimKind>("underlying"),
                    o.Bool("isFlags"),
                    Array(o, "members", m => new EnumMemberIr(m.String("name"), m.Long("value"))));
        }

        throw new FormatException("Unknown declaration kind '" + o.String("kind") + "'.");
    }

    // ------------------------------------------------------------------ minimal JSON document model

    private abstract class JsonNode
    {
        public JsonObject AsObject() => this as JsonObject ?? throw new FormatException("Expected a JSON object.");

        public JsonArray AsArray() => this as JsonArray ?? throw new FormatException("Expected a JSON array.");

        public string AsString() => (this as JsonString)?.Value ?? throw new FormatException("Expected a JSON string.");

        public long AsLong()
        {
            if (this is JsonNumber number
                && long.TryParse(number.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                return value;
            throw new FormatException("Expected a JSON integer.");
        }
    }

    private sealed class JsonObject : JsonNode
    {
        public SortedDictionary<string, JsonNode> Members { get; } = new(StringComparer.Ordinal);

        public JsonObject Add(string key, JsonNode? value)
        {
            if (value != null)
                Members[key] = value;
            return this;
        }

        public JsonObject Add(string key, string? value) => Add(key, value == null ? null : new JsonString(value));

        public JsonObject Add(string key, long value) => Add(key, new JsonNumber(value));

        public JsonObject Add(string key, bool value) => Add(key, new JsonBool(value));

        public bool TryGet(string key, out JsonNode node)
        {
            if (Members.TryGetValue(key, out var found) && found is not JsonNull)
            {
                node = found;
                return true;
            }

            node = null!;
            return false;
        }

        private JsonNode Get(string key)
            => TryGet(key, out var node) ? node : throw new FormatException("Missing JSON member '" + key + "'.");

        public JsonObject Object(string key) => Get(key).AsObject();

        public string String(string key) => Get(key).AsString();

        public string? OptString(string key) => TryGet(key, out var node) ? node.AsString() : null;

        public long Long(string key) => Get(key).AsLong();

        public int Int(string key) => checked((int)Long(key));

        public ushort UShort(string key) => checked((ushort)Long(key));

        public bool Bool(string key) => (Get(key) as JsonBool)?.Value ?? throw new FormatException("Expected a JSON boolean for '" + key + "'.");

        public T Enum<T>(string key)
            where T : struct
        {
            var text = String(key);
            if (text.Length > 0 && char.IsLetter(text[0])
                && System.Enum.TryParse<T>(text, false, out var value) && System.Enum.IsDefined(typeof(T), value))
                return value;
            throw new FormatException("Unknown " + typeof(T).Name + " '" + text + "'.");
        }
    }

    private sealed class JsonArray : JsonNode
    {
        public List<JsonNode> Items { get; } = new();
    }

    private sealed class JsonString : JsonNode
    {
        public JsonString(string value) => Value = value;

        public string Value { get; }
    }

    private sealed class JsonNumber : JsonNode
    {
        public JsonNumber(long value) => Text = value.ToString(CultureInfo.InvariantCulture);

        public JsonNumber(string text) => Text = text;

        public string Text { get; }
    }

    private sealed class JsonBool : JsonNode
    {
        public JsonBool(bool value) => Value = value;

        public bool Value { get; }
    }

    private sealed class JsonNull : JsonNode
    {
        public static readonly JsonNull Instance = new();
    }

    private static class JsonText
    {
        public static void Write(StringBuilder sb, JsonNode node, int indent)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o.Members.Count == 0)
                    {
                        sb.Append("{}");
                        return;
                    }

                    sb.Append("{\n");
                    var i = 0;
                    foreach (var pair in o.Members)
                    {
                        Indent(sb, indent + 1);
                        WriteString(sb, pair.Key);
                        sb.Append(": ");
                        Write(sb, pair.Value, indent + 1);
                        sb.Append(++i < o.Members.Count ? ",\n" : "\n");
                    }

                    Indent(sb, indent);
                    sb.Append('}');
                    return;

                case JsonArray a:
                    if (a.Items.Count == 0)
                    {
                        sb.Append("[]");
                        return;
                    }

                    if (a.Items.All(item => item is JsonString or JsonNumber or JsonBool or JsonNull))
                    {
                        sb.Append('[');
                        for (var j = 0; j < a.Items.Count; j++)
                        {
                            if (j > 0)
                                sb.Append(", ");
                            Write(sb, a.Items[j], indent);
                        }

                        sb.Append(']');
                        return;
                    }

                    sb.Append("[\n");
                    for (var j = 0; j < a.Items.Count; j++)
                    {
                        Indent(sb, indent + 1);
                        Write(sb, a.Items[j], indent + 1);
                        sb.Append(j + 1 < a.Items.Count ? ",\n" : "\n");
                    }

                    Indent(sb, indent);
                    sb.Append(']');
                    return;

                case JsonString s:
                    WriteString(sb, s.Value);
                    return;

                case JsonNumber n:
                    sb.Append(n.Text);
                    return;

                case JsonBool b:
                    sb.Append(b.Value ? "true" : "false");
                    return;

                default:
                    sb.Append("null");
                    return;
            }
        }

        private static void Indent(StringBuilder sb, int indent) => sb.Append(' ', indent * 2);

        private const char Backslash = (char)92;

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append(Backslash).Append('"'); break;
                    case Backslash: sb.Append(Backslash).Append(Backslash); break;
                    case '\n': sb.Append(Backslash).Append('n'); break;
                    case '\r': sb.Append(Backslash).Append('r'); break;
                    case '\t': sb.Append(Backslash).Append('t'); break;
                    default:
                        if (c < 0x20)
                            sb.Append(Backslash).Append('u').Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
        }

        public static JsonNode Parse(string text)
        {
            var parser = new Parser(text);
            var node = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd)
                throw new FormatException("Unexpected text after the JSON value at " + parser.Position + ".");
            return node;
        }

        private sealed class Parser
        {
            private const char ByteOrderMark = (char)0xFEFF;

            private readonly string _text;
            private int _pos;

            public Parser(string text) => _text = text;

            public bool AtEnd => _pos >= _text.Length;

            public int Position => _pos;

            public void SkipWhitespace()
            {
                while (_pos < _text.Length && _text[_pos] is ' ' or '\t' or '\r' or '\n' or ByteOrderMark)
                    _pos++;
            }

            private char Peek()
            {
                if (_pos >= _text.Length)
                    throw new FormatException("Unexpected end of JSON.");
                return _text[_pos];
            }

            private void Expect(char c)
            {
                if (Peek() != c)
                    throw new FormatException("Expected '" + c + "' at " + _pos + ".");
                _pos++;
            }

            private void ExpectWord(string word)
            {
                if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0)
                    throw new FormatException("Expected '" + word + "' at " + _pos + ".");
                _pos += word.Length;
            }

            public JsonNode ParseValue()
            {
                SkipWhitespace();
                switch (Peek())
                {
                    case '{':
                    {
                        _pos++;
                        var o = new JsonObject();
                        SkipWhitespace();
                        if (Peek() == '}')
                        {
                            _pos++;
                            return o;
                        }

                        while (true)
                        {
                            SkipWhitespace();
                            var key = ParseString();
                            SkipWhitespace();
                            Expect(':');
                            var value = ParseValue();
                            if (o.Members.ContainsKey(key))
                                throw new FormatException("Duplicate JSON member '" + key + "'.");
                            o.Members[key] = value;
                            SkipWhitespace();
                            if (Peek() == ',')
                            {
                                _pos++;
                                continue;
                            }

                            Expect('}');
                            return o;
                        }
                    }

                    case '[':
                    {
                        _pos++;
                        var a = new JsonArray();
                        SkipWhitespace();
                        if (Peek() == ']')
                        {
                            _pos++;
                            return a;
                        }

                        while (true)
                        {
                            a.Items.Add(ParseValue());
                            SkipWhitespace();
                            if (Peek() == ',')
                            {
                                _pos++;
                                continue;
                            }

                            Expect(']');
                            return a;
                        }
                    }

                    case '"':
                        return new JsonString(ParseString());

                    case 't':
                        ExpectWord("true");
                        return new JsonBool(true);

                    case 'f':
                        ExpectWord("false");
                        return new JsonBool(false);

                    case 'n':
                        ExpectWord("null");
                        return JsonNull.Instance;

                    default:
                    {
                        var start = _pos;
                        while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] is '-' or '+' or '.' or 'e' or 'E'))
                            _pos++;
                        if (start == _pos)
                            throw new FormatException("Unexpected character '" + _text[_pos] + "' at " + _pos + ".");
                        return new JsonNumber(_text.Substring(start, _pos - start));
                    }
                }
            }

            private string ParseString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    var c = Peek();
                    _pos++;
                    if (c == '"')
                        return sb.ToString();
                    if (c != Backslash)
                    {
                        sb.Append(c);
                        continue;
                    }

                    var escape = Peek();
                    _pos++;
                    switch (escape)
                    {
                        case '"': sb.Append('"'); break;
                        case Backslash: sb.Append(Backslash); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_pos + 4 > _text.Length
                                || !int.TryParse(_text.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw new FormatException("Invalid unicode escape at " + _pos + ".");
                            sb.Append((char)code);
                            _pos += 4;
                            break;
                        default:
                            throw new FormatException("Invalid escape '" + escape + "' at " + _pos + ".");
                    }
                }
            }
        }
    }
}
