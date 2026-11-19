using System.Text.Json;
using System.Text.Json.Nodes;
using NexNet.Interop;

namespace NexNet.GoldenVectors;

/// <summary>A protocol message body: a MessagePack array of named fields (tests/golden/README.md, "schemas.jsonl").</summary>
/// <param name="Id">Message name, the <c>type</c> of its vectors.</param>
/// <param name="MessageType">The frame type byte, or null for a value embedded in another message.</param>
/// <param name="MinCount">Fewest elements a reader accepts.</param>
/// <param name="MaxCount">Most elements a reader accepts; null when readers skip any extra trailing elements.</param>
/// <param name="Fields">The fields, by position.</param>
public sealed record MessageSchema(string Id, int? MessageType, int MinCount, int? MaxCount, IReadOnlyList<(string Name, string Type)> Fields);

/// <summary>
/// The declared types of the golden vectors: the interop type zoo (objects, unions, enums), types with a custom
/// formatter (externals) and the protocol message bodies. Written to and read from <c>tests/golden/schemas.jsonl</c>.
/// </summary>
public sealed class GoldenSchemas
{
    private readonly Dictionary<string, DeclSchema> _decls = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, string> _externals = new(StringComparer.Ordinal);
    private readonly List<MessageSchema> _messages = new();

    private GoldenSchemas()
    {
    }

    /// <summary>The declared types (zoo and messages) as a typed-JSON schema set.</summary>
    public SchemaSet Set { get; private set; } = SchemaSet.Empty;

    /// <summary>The protocol message bodies.</summary>
    public IReadOnlyList<MessageSchema> Messages => _messages;

    /// <summary>True when <paramref name="name"/> is a type with a custom formatter.</summary>
    public bool IsExternal(string name) => _externals.ContainsKey(name);

    /// <summary>The declared type named <paramref name="name"/>.</summary>
    public bool TryGetDecl(string name, out DeclSchema decl) => _decls.TryGetValue(name, out decl!);

    /// <summary>The message named <paramref name="name"/>.</summary>
    public MessageSchema? FindMessage(string name) => _messages.FirstOrDefault(m => m.Id == name);

    /// <summary>The message sent in frames of type <paramref name="messageType"/>.</summary>
    public MessageSchema? FindMessage(int messageType) => _messages.FirstOrDefault(m => m.MessageType == messageType);

    /// <summary>Parses a type expression against these schemas.</summary>
    public TypeSchema Parse(string type) => TypeExpr.Parse(type, this);

    /// <summary>
    /// Builds the schemas from a generated schema set (full metadata names), renamed to short ids: the type name without
    /// namespace or generic arity.
    /// </summary>
    public static GoldenSchemas Create(SchemaSet generated, IEnumerable<(string Name, string Note)> externals, IEnumerable<MessageSchema> messages)
    {
        var schemas = new GoldenSchemas();
        foreach (var (name, note) in externals)
            schemas._externals.Add(name, note);

        foreach (var decl in generated.Decls)
        {
            var id = ShortName(decl.Id);
            DeclSchema renamed = decl switch
            {
                ObjectSchema o => new ObjectSchema(id, o.TypeParameterCount, o.ArrayLength,
                    o.Members.Select(m => new MemberSchema(m.Key, m.Name, Rename(m.Type))).OrderBy(m => m.Key).ToArray()),
                UnionSchema u => new UnionSchema(id, u.TypeParameterCount,
                    u.Cases.Select(c => new UnionCaseSchema(c.Tag, ShortName(c.CaseId))).OrderBy(c => c.Tag).ToArray()),
                EnumSchema e => new EnumSchema(id, e.Underlying),
                _ => throw new InvalidOperationException("Unknown declared schema " + decl.Id + "."),
            };
            schemas._decls.Add(id, renamed);
        }

        schemas.AddMessages(messages);
        return schemas;
    }

    private void AddMessages(IEnumerable<MessageSchema> messages)
    {
        foreach (var message in messages)
            _messages.Add(message);

        // Message bodies are objects of the value model (an array by position); the field types refer to the zoo only
        // through built-ins, so they are parsed once every declared type is known.
        foreach (var message in _messages)
        {
            var members = message.Fields.Select((f, i) => new MemberSchema(i, f.Name, Parse(f.Type))).ToArray();
            _decls.Add(message.Id, new ObjectSchema(message.Id, 0, members.Length, members));
        }

        Set = new SchemaSet(_decls.Values.ToArray());
    }

    /// <summary>A full metadata name without its namespace, nesting or generic arity.</summary>
    public static string ShortName(string fullName)
    {
        var name = fullName;
        var cut = Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+'));
        if (cut >= 0)
            name = name.Substring(cut + 1);
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? name.Substring(0, tick) : name;
    }

    /// <summary><paramref name="type"/> with every declared and external type id shortened.</summary>
    public static TypeSchema Rename(TypeSchema type)
    {
        TypeSchema[] Args() => type.Args.Select(Rename).ToArray();
        return type.Kind switch
        {
            TypeSchemaKind.Prim => TypeSchema.Primitive(type.Prim),
            TypeSchemaKind.Bin => TypeSchema.Bin(),
            TypeSchemaKind.PrimArray => TypeSchema.PrimArray(type.Prim),
            TypeSchemaKind.Seq => TypeSchema.Seq(Rename(type.Args[0]), type.Clr),
            TypeSchemaKind.Set => TypeSchema.Set(Rename(type.Args[0])),
            TypeSchemaKind.Queue => TypeSchema.Queue(Rename(type.Args[0])),
            TypeSchemaKind.Stack => TypeSchema.Stack(Rename(type.Args[0])),
            TypeSchemaKind.Map => TypeSchema.Map(Rename(type.Args[0]), Rename(type.Args[1])),
            TypeSchemaKind.Tuple => TypeSchema.Tuple(Args()),
            TypeSchemaKind.Nullable => TypeSchema.Nullable(Rename(type.Args[0])),
            TypeSchemaKind.Decl => TypeSchema.Decl(ShortName(type.DeclId!), Args()),
            TypeSchemaKind.TypeParam => TypeSchema.TypeParam(type.Ordinal),
            TypeSchemaKind.External => TypeSchema.External(ShortName(type.Clr!)),
            _ => throw new InvalidOperationException("Unknown schema kind " + type.Kind + "."),
        };
    }

    // ------------------------------------------------------------------ schemas.jsonl

    /// <summary>The lines of <c>schemas.jsonl</c>: zoo types by id, then externals, then messages in frame type order.</summary>
    public IEnumerable<JsonObject> ToJson()
    {
        foreach (var decl in _decls.Values.Where(d => FindMessage(d.Id) == null).OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            switch (decl)
            {
                case EnumSchema e:
                    yield return new JsonObject { ["id"] = e.Id, ["kind"] = "enum", ["underlying"] = TypeExpr.PrimName(e.Underlying) };
                    break;
                case ObjectSchema o:
                {
                    var members = new JsonArray();
                    foreach (var m in o.Members)
                        members.Add(new JsonArray(m.Key, m.Name, TypeExpr.Print(m.Type)));
                    yield return new JsonObject
                    {
                        ["id"] = o.Id, ["kind"] = "object", ["typeParams"] = o.TypeParameterCount, ["length"] = o.ArrayLength, ["members"] = members,
                    };
                    break;
                }
                case UnionSchema u:
                {
                    var cases = new JsonArray();
                    foreach (var c in u.Cases)
                        cases.Add(new JsonArray((int)c.Tag, c.CaseId));
                    yield return new JsonObject { ["id"] = u.Id, ["kind"] = "union", ["typeParams"] = u.TypeParameterCount, ["cases"] = cases };
                    break;
                }
            }
        }

        foreach (var (name, note) in _externals)
            yield return new JsonObject { ["id"] = name, ["kind"] = "external", ["note"] = note };

        foreach (var m in _messages.OrderBy(m => m.MessageType ?? int.MaxValue).ThenBy(m => m.Id, StringComparer.Ordinal))
        {
            var fields = new JsonArray();
            foreach (var (name, type) in m.Fields)
                fields.Add(new JsonArray(name, type));
            var line = new JsonObject { ["id"] = m.Id, ["kind"] = "message", ["messageType"] = m.MessageType, ["minCount"] = m.MinCount, ["maxCount"] = m.MaxCount, ["fields"] = fields };
            yield return line;
        }
    }

    /// <summary>Reads the lines of <c>schemas.jsonl</c>.</summary>
    /// <exception cref="FormatException">A line is malformed.</exception>
    public static GoldenSchemas Parse(IEnumerable<string> lines)
    {
        var schemas = new GoldenSchemas();
        var objects = new List<JsonObject>();
        var messages = new List<MessageSchema>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var json = JsonNode.Parse(line) as JsonObject ?? throw new FormatException("A schema line must be a JSON object.");
            var id = (string?)json["id"] ?? throw new FormatException("A schema line needs an id.");
            switch ((string?)json["kind"])
            {
                case "enum":
                    schemas._decls.Add(id, new EnumSchema(id, TypeExpr.ParsePrim((string)json["underlying"]!)));
                    break;
                case "object":
                case "union":
                    // Placeholder until every id is known; member types may refer to any declared type.
                    schemas._decls.Add(id, new ObjectSchema(id, (int)json["typeParams"]!, 0, Array.Empty<MemberSchema>()));
                    objects.Add(json);
                    break;
                case "external":
                    schemas._externals.Add(id, (string?)json["note"] ?? "");
                    break;
                case "message":
                    messages.Add(new MessageSchema(id, (int?)json["messageType"], (int)json["minCount"]!, (int?)json["maxCount"],
                        json["fields"]!.AsArray().Select(f => ((string)f![0]!, (string)f[1]!)).ToArray()));
                    break;
                default:
                    throw new FormatException("Schema '" + id + "' has an unknown kind.");
            }
        }

        foreach (var json in objects)
        {
            var id = (string)json["id"]!;
            var typeParams = (int)json["typeParams"]!;
            schemas._decls[id] = (string)json["kind"]! == "object"
                ? new ObjectSchema(id, typeParams, (int)json["length"]!,
                    json["members"]!.AsArray().Select(m => new MemberSchema((int)m![0]!, (string)m[1]!, schemas.Parse((string)m[2]!))).ToArray())
                : new UnionSchema(id, typeParams,
                    json["cases"]!.AsArray().Select(c => new UnionCaseSchema((ushort)(int)c![0]!, (string)c[1]!)).ToArray());
        }

        schemas.AddMessages(messages);
        return schemas;
    }
}
