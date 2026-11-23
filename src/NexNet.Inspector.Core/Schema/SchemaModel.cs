using System.Diagnostics.CodeAnalysis;

namespace NexNet.Inspector.Core.Schema;

/// <summary>One exported nexus pair, seen from the client: the API it calls on the server and the API it implements.</summary>
/// <param name="Name">Simple name of the nexus class the export was built from.</param>
/// <param name="ClrNamespace">Namespace of that class ("" for the global namespace).</param>
/// <param name="Targets">Target-language flags; 0 in an embedded inspector schema.</param>
/// <param name="ServerApi">The server nexus interface: what the client invokes.</param>
/// <param name="ClientApi">The client nexus interface: what the server invokes on the client.</param>
/// <param name="Handshake">The values the client sends in its greeting.</param>
/// <param name="ClientAcceptedIds">The (hash, id) pairs a client accepts in incoming invocations, ascending by id.</param>
/// <param name="Docs">Summary documentation of the nexus class, or null.</param>
public sealed record SchemaExport(
    string Name,
    string ClrNamespace,
    int Targets,
    SchemaInterface ServerApi,
    SchemaInterface ClientApi,
    SchemaHandshake Handshake,
    IReadOnlyList<SchemaAcceptedId> ClientAcceptedIds,
    string? Docs)
{
    /// <summary>The API whose methods a caller on the given side invokes.</summary>
    /// <param name="callerIsClient">True when the client calls (its invocations target the server API).</param>
    public SchemaInterface CalleeApi(bool callerIsClient) => callerIsClient ? ServerApi : ClientApi;
}

/// <summary>Client greeting values of an export.</summary>
/// <param name="Version">The server API's current version string, or null when it is not versioned.</param>
/// <param name="ServerNexusHash">That version's hash, or the server API hash.</param>
/// <param name="ClientNexusHash">The client API hash.</param>
public readonly record struct SchemaHandshake(string? Version, int ServerNexusHash, int ClientNexusHash);

/// <summary>An incoming invocation a client accepts.</summary>
/// <param name="Hash">The API hash it is checked against.</param>
/// <param name="Id">The method ID.</param>
public readonly record struct SchemaAcceptedId(int Hash, ushort Id);

/// <summary>A nexus interface with everything it inherits.</summary>
public sealed class SchemaInterface
{
    private readonly Dictionary<ushort, SchemaMethod> _methods;
    private readonly Dictionary<ushort, SchemaCollection> _collections;

    internal SchemaInterface(
        string clrName,
        string name,
        string clrNamespace,
        int hash,
        SchemaMethod[] methods,
        SchemaCollection[] collections,
        SchemaVersion[] versions)
    {
        ClrName = clrName;
        Name = name;
        ClrNamespace = clrNamespace;
        Hash = hash;
        Methods = methods;
        Collections = collections;
        Versions = versions;
        _methods = new Dictionary<ushort, SchemaMethod>(methods.Length);
        foreach (var method in methods)
            _methods.TryAdd(method.Id, method);
        _collections = new Dictionary<ushort, SchemaCollection>(collections.Length);
        foreach (var collection in collections)
            _collections.TryAdd(collection.Id, collection);
    }

    /// <summary>Full metadata name, e.g. <c>Ns.IServerNexus</c>.</summary>
    public string ClrName { get; }

    /// <summary>Simple name.</summary>
    public string Name { get; }

    /// <summary>Namespace.</summary>
    public string ClrNamespace { get; }

    /// <summary>The API hash.</summary>
    public int Hash { get; }

    /// <summary>Every invocable method, ascending by ID.</summary>
    public IReadOnlyList<SchemaMethod> Methods { get; }

    /// <summary>Every synchronized collection, ascending by ID.</summary>
    public IReadOnlyList<SchemaCollection> Collections { get; }

    /// <summary>The <c>[NexusVersion]</c> interfaces of the hierarchy; the last one is current. Empty when unversioned.</summary>
    public IReadOnlyList<SchemaVersion> Versions { get; }

    /// <summary>Finds a method by its wire ID.</summary>
    /// <param name="id">The method ID.</param>
    /// <param name="method">The method.</param>
    /// <returns>False when the interface has no such method (it may be a collection).</returns>
    public bool TryGetMethod(ushort id, [NotNullWhen(true)] out SchemaMethod? method) => _methods.TryGetValue(id, out method);

    /// <summary>Finds a collection by its ID (the method ID of its connect invocation).</summary>
    /// <param name="id">The collection ID.</param>
    /// <param name="collection">The collection.</param>
    /// <returns>False when the interface has no such collection.</returns>
    public bool TryGetCollection(ushort id, [NotNullWhen(true)] out SchemaCollection? collection)
        => _collections.TryGetValue(id, out collection);

    /// <summary>True when <paramref name="hash"/> is the API hash or the hash of one of its versions.</summary>
    /// <param name="hash">A nexus hash from a greeting.</param>
    public bool MatchesHash(int hash)
    {
        if (hash == Hash)
            return true;
        foreach (var version in Versions)
        {
            if (version.Hash == hash)
                return true;
        }

        return false;
    }
}

/// <summary>One <c>[NexusVersion]</c> interface of a hierarchy.</summary>
/// <param name="Version">The version string ("" when the attribute names none).</param>
/// <param name="Hash">The hash of that interface.</param>
/// <param name="MethodIds">IDs of the methods and collections invocable on that version, ascending.</param>
public sealed record SchemaVersion(string Version, int Hash, IReadOnlyList<ushort> MethodIds);

/// <summary>One invocable method.</summary>
public sealed class SchemaMethod
{
    // Parameters by wire position (WireIndex - 1); null where no parameter claims a position.
    private readonly SchemaParam?[] _wireParams;

    internal SchemaMethod(
        string name,
        ushort id,
        int hash,
        SchemaReturnKind returnKind,
        SchemaTypeRef? returnType,
        SchemaParam[] parameters,
        int serializedCount,
        bool hasCancellation,
        SchemaPipeKind pipeKind,
        SchemaTypeRef? pipeType,
        bool separatePool,
        bool isAuthorized,
        string? docs)
    {
        Name = name;
        Id = id;
        Hash = hash;
        ReturnKind = returnKind;
        ReturnType = returnType;
        Parameters = parameters;
        SerializedCount = serializedCount;
        HasCancellation = hasCancellation;
        PipeKind = pipeKind;
        PipeType = pipeType;
        SeparatePool = separatePool;
        IsAuthorized = isAuthorized;
        Docs = docs;

        var positions = serializedCount;
        foreach (var parameter in parameters)
            positions = Math.Max(positions, parameter.WireIndex);
        _wireParams = new SchemaParam?[Math.Max(0, positions)];
        PipeArgumentIndex = -1;
        foreach (var parameter in parameters)
        {
            if (parameter.WireIndex <= 0)
                continue;
            _wireParams[parameter.WireIndex - 1] = parameter;
            if (PipeArgumentIndex < 0 && parameter.Kind is SchemaParamKind.DuplexPipe or SchemaParamKind.DuplexChannel)
                PipeArgumentIndex = parameter.WireIndex - 1;
        }
    }

    /// <summary>Method name.</summary>
    public string Name { get; }

    /// <summary>Method ID on the wire.</summary>
    public ushort Id { get; }

    /// <summary>Method hash.</summary>
    public int Hash { get; }

    /// <summary>How the method completes.</summary>
    public SchemaReturnKind ReturnKind { get; }

    /// <summary>The result type for <see cref="SchemaReturnKind.TaskOf"/>; null otherwise.</summary>
    public SchemaTypeRef? ReturnType { get; }

    /// <summary>Every parameter in declaration order, including cancellation tokens and pipes.</summary>
    public IReadOnlyList<SchemaParam> Parameters { get; }

    /// <summary>Number of values in the invocation arguments.</summary>
    public int SerializedCount { get; }

    /// <summary>True when the method takes a cancellation token.</summary>
    public bool HasCancellation { get; }

    /// <summary>The pipe or channel parameter kind, if any.</summary>
    public SchemaPipeKind PipeKind { get; }

    /// <summary>The channel item type for <see cref="SchemaPipeKind.Channel"/>; null otherwise.</summary>
    public SchemaTypeRef? PipeType { get; }

    /// <summary>True when the method runs in the receiver's separate invocation pool.</summary>
    public bool SeparatePool { get; }

    /// <summary>True when the server checks authorization first.</summary>
    public bool IsAuthorized { get; }

    /// <summary>Summary documentation, or null.</summary>
    public string? Docs { get; }

    /// <summary>0-based position of the pipe or channel argument in the arguments array; -1 when there is none.</summary>
    public int PipeArgumentIndex { get; }

    /// <summary>The parameter written at a 0-based position of the arguments array.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The parameter, or null when the position is out of range.</returns>
    public SchemaParam? GetWireParam(int position)
        => (uint)position < (uint)_wireParams.Length ? _wireParams[position] : null;
}

/// <summary>One method parameter.</summary>
/// <param name="Name">Parameter name.</param>
/// <param name="Kind">What the parameter is.</param>
/// <param name="Type">The value type, or the channel item type; null otherwise.</param>
/// <param name="WireIndex">1-based position in the serialized arguments; 0 when not serialized.</param>
/// <param name="Nullable">True when the value may be null.</param>
public sealed record SchemaParam(string Name, SchemaParamKind Kind, SchemaTypeRef? Type, int WireIndex, bool Nullable);

/// <summary>One synchronized collection.</summary>
/// <param name="Name">Property name.</param>
/// <param name="Id">Collection ID: the method ID of its connect invocation.</param>
/// <param name="Hash">Collection hash.</param>
/// <param name="Kind">Collection kind.</param>
/// <param name="Mode">Synchronization mode.</param>
/// <param name="Item">The item type.</param>
/// <param name="IsAuthorized">True when the server checks authorization first.</param>
/// <param name="Docs">Summary documentation, or null.</param>
public sealed record SchemaCollection(
    string Name,
    ushort Id,
    int Hash,
    SchemaCollectionKind Kind,
    SchemaCollectionMode Mode,
    SchemaTypeRef? Item,
    bool IsAuthorized,
    string? Docs)
{
    /// <summary>
    /// The type of a ResetValues chunk: the items as one <c>Memory&lt;T&gt;</c>, which is ext 78, bin or an array
    /// depending on the item type.
    /// </summary>
    public SchemaTypeRef? ValuesType { get; } = Item is null ? null : SchemaTypeRef.MemoryOf(Item);
}

/// <summary>A hand-written formatter in one target language for an external type.</summary>
/// <param name="Target">The target bit.</param>
/// <param name="Formatter">Fully qualified name of the native formatter class.</param>
public readonly record struct SchemaNativeFormatter(int Target, string Formatter);

/// <summary>A reference to a serialized type. Only the fields meaningful for <see cref="Kind"/> are set.</summary>
public sealed class SchemaTypeRef
{
    private static readonly SchemaTypeRef[] Prims = CreatePrims();

    internal SchemaTypeRef(
        SchemaTypeKind kind,
        SchemaPrimKind prim = SchemaPrimKind.None,
        string? clr = null,
        SchemaTypeRef[]? args = null,
        string? declId = null,
        int ordinal = 0,
        bool refNullable = false,
        bool jsNumber = false,
        SchemaNativeFormatter[]? nativeFormatters = null)
    {
        Kind = kind;
        Prim = prim;
        Clr = clr;
        Args = args ?? [];
        DeclId = declId;
        Ordinal = ordinal;
        RefNullable = refNullable;
        JsNumber = jsNumber;
        NativeFormatters = nativeFormatters ?? [];
    }

    /// <summary>What the reference is.</summary>
    public SchemaTypeKind Kind { get; }

    /// <summary><see cref="SchemaTypeKind.Prim"/>: the leaf; <see cref="SchemaTypeKind.PrimArray"/>: the element.</summary>
    public SchemaPrimKind Prim { get; }

    /// <summary>The .NET container (e.g. <c>Array</c>, <c>List</c>, <c>ValueTuple</c>), or the full .NET name of an external type.</summary>
    public string? Clr { get; }

    /// <summary>Element, key/value, tuple element, nullable inner or type argument references.</summary>
    public IReadOnlyList<SchemaTypeRef> Args { get; }

    /// <summary><see cref="SchemaTypeKind.Decl"/>: the declaration's ID.</summary>
    public string? DeclId { get; }

    /// <summary>The declaration <see cref="DeclId"/> names; null when the schema does not contain it.</summary>
    public SchemaTypeDecl? Decl { get; internal set; }

    /// <summary><see cref="SchemaTypeKind.TypeParam"/>: the type parameter position.</summary>
    public int Ordinal { get; }

    /// <summary>True on a reference-type occurrence that may be null.</summary>
    public bool RefNullable { get; }

    /// <summary>Int64/UInt64 leaves that JavaScript maps to <c>number</c>; no effect on the wire.</summary>
    public bool JsNumber { get; }

    /// <summary>External types: the native formatters declared for them, by target.</summary>
    public IReadOnlyList<SchemaNativeFormatter> NativeFormatters { get; }

    /// <summary>True for <see cref="SchemaTypeKind.Tuple"/> over a <c>KeyValuePair</c>: its elements are Key and Value.</summary>
    public bool IsKeyValuePair => Kind == SchemaTypeKind.Tuple && Clr == "KeyValuePair";

    /// <summary>A shared reference to a primitive leaf.</summary>
    /// <param name="prim">The primitive.</param>
    public static SchemaTypeRef Primitive(SchemaPrimKind prim)
        => (uint)prim < (uint)Prims.Length ? Prims[(int)prim] : Prims[0];

    /// <summary>
    /// The wire type of a <c>Memory&lt;T&gt;</c> of <paramref name="item"/>: ext 78 for the primitive array element types,
    /// bin for bytes, otherwise an array.
    /// </summary>
    /// <param name="item">The element type.</param>
    public static SchemaTypeRef MemoryOf(SchemaTypeRef item)
    {
        if (item.Kind == SchemaTypeKind.Prim)
        {
            if (item.Prim == SchemaPrimKind.Byte)
                return new SchemaTypeRef(SchemaTypeKind.Bin, clr: "Memory");
            if (IsPrimArrayElement(item.Prim))
                return new SchemaTypeRef(SchemaTypeKind.PrimArray, item.Prim, "Memory");
        }

        return new SchemaTypeRef(SchemaTypeKind.Seq, clr: "Memory", args: [item]);
    }

    /// <summary>True for the element types of ext 78 primitive arrays (protocol specification §10.5).</summary>
    /// <param name="prim">The element type.</param>
    public static bool IsPrimArrayElement(SchemaPrimKind prim) => prim is SchemaPrimKind.SByte or SchemaPrimKind.Int16
        or SchemaPrimKind.UInt16 or SchemaPrimKind.Int32 or SchemaPrimKind.UInt32 or SchemaPrimKind.Int64
        or SchemaPrimKind.UInt64 or SchemaPrimKind.Single or SchemaPrimKind.Double or SchemaPrimKind.Char
        or SchemaPrimKind.Half;

    private static SchemaTypeRef[] CreatePrims()
    {
        var prims = new SchemaTypeRef[(int)SchemaPrimKind.Version + 1];
        for (var i = 0; i < prims.Length; i++)
            prims[i] = new SchemaTypeRef(SchemaTypeKind.Prim, (SchemaPrimKind)i, refNullable: true);
        return prims;
    }
}

/// <summary>A serialized declared type: an object, a union or an enum.</summary>
public abstract class SchemaTypeDecl
{
    private protected SchemaTypeDecl(
        SchemaDeclKind kind,
        string id,
        string clrMetadataName,
        string clrNamespace,
        string name,
        IReadOnlyList<string> containingTypes,
        string? docs)
    {
        Kind = kind;
        Id = id;
        ClrMetadataName = clrMetadataName;
        ClrNamespace = clrNamespace;
        Name = name;
        ContainingTypes = containingTypes;
        Docs = docs;
    }

    /// <summary>Declaration kind.</summary>
    public SchemaDeclKind Kind { get; }

    /// <summary>Unique ID: the full metadata name of the definition, e.g. <c>Ns.Outer+Box`1</c>.</summary>
    public string Id { get; }

    /// <summary>Same as <see cref="Id"/>.</summary>
    public string ClrMetadataName { get; }

    /// <summary>Namespace ("" for global).</summary>
    public string ClrNamespace { get; }

    /// <summary>Simple name without arity.</summary>
    public string Name { get; }

    /// <summary>Simple names of the containing types, outermost first.</summary>
    public IReadOnlyList<string> ContainingTypes { get; }

    /// <summary>Summary documentation, or null.</summary>
    public string? Docs { get; }
}

/// <summary>A <c>[NexusObject]</c> class or struct: an array of <see cref="ArrayLength"/> elements, nil in the gaps.</summary>
public sealed class SchemaObjectDecl : SchemaTypeDecl
{
    // Members by key; null in the gaps.
    private readonly SchemaMember?[] _byKey;

    internal SchemaObjectDecl(
        string id,
        string clrMetadataName,
        string clrNamespace,
        string name,
        IReadOnlyList<string> containingTypes,
        string? docs,
        bool isStruct,
        bool isRecord,
        bool isAbstract,
        IReadOnlyList<string> typeParams,
        string? baseDeclId,
        int arrayLength,
        bool isRecursive,
        SchemaMember[] members)
        : base(SchemaDeclKind.Object, id, clrMetadataName, clrNamespace, name, containingTypes, docs)
    {
        IsStruct = isStruct;
        IsRecord = isRecord;
        IsAbstract = isAbstract;
        TypeParams = typeParams;
        BaseDeclId = baseDeclId;
        ArrayLength = arrayLength;
        IsRecursive = isRecursive;
        Members = members;

        var length = arrayLength;
        foreach (var member in members)
            length = Math.Max(length, member.Key + 1);
        _byKey = new SchemaMember?[Math.Max(0, length)];
        foreach (var member in members)
        {
            if (member.Key >= 0)
                _byKey[member.Key] = member;
        }
    }

    /// <summary>Value type: never null on the wire.</summary>
    public bool IsStruct { get; }

    /// <summary>Declared as a record.</summary>
    public bool IsRecord { get; }

    /// <summary>Abstract class or interface without union cases.</summary>
    public bool IsAbstract { get; }

    /// <summary>Type parameter names.</summary>
    public IReadOnlyList<string> TypeParams { get; }

    /// <summary>The base type's ID, when it is a <c>[NexusObject]</c>.</summary>
    public string? BaseDeclId { get; }

    /// <summary>Highest key + 1.</summary>
    public int ArrayLength { get; }

    /// <summary>The type can reach itself through member types.</summary>
    public bool IsRecursive { get; }

    /// <summary>Keyed members, ascending by key.</summary>
    public IReadOnlyList<SchemaMember> Members { get; }

    /// <summary>The member at an array position.</summary>
    /// <param name="key">The array position.</param>
    /// <returns>The member, or null for an unused key or a position past the known ones.</returns>
    public SchemaMember? GetMember(int key) => (uint)key < (uint)_byKey.Length ? _byKey[key] : null;
}

/// <summary>A <c>[NexusObject]</c> union: <c>[tag, value]</c>.</summary>
public sealed class SchemaUnionDecl : SchemaTypeDecl
{
    private readonly Dictionary<ushort, SchemaUnionCase> _cases;

    internal SchemaUnionDecl(
        string id,
        string clrMetadataName,
        string clrNamespace,
        string name,
        IReadOnlyList<string> containingTypes,
        string? docs,
        bool isInterface,
        IReadOnlyList<string> typeParams,
        bool isRecursive,
        SchemaUnionCase[] cases)
        : base(SchemaDeclKind.Union, id, clrMetadataName, clrNamespace, name, containingTypes, docs)
    {
        IsInterface = isInterface;
        TypeParams = typeParams;
        IsRecursive = isRecursive;
        Cases = cases;
        _cases = new Dictionary<ushort, SchemaUnionCase>(cases.Length);
        foreach (var unionCase in cases)
            _cases.TryAdd(unionCase.Tag, unionCase);
    }

    /// <summary>Declared as an interface.</summary>
    public bool IsInterface { get; }

    /// <summary>Type parameter names; the cases share them.</summary>
    public IReadOnlyList<string> TypeParams { get; }

    /// <summary>The type can reach itself.</summary>
    public bool IsRecursive { get; }

    /// <summary>The cases.</summary>
    public IReadOnlyList<SchemaUnionCase> Cases { get; }

    /// <summary>Finds a case by its tag.</summary>
    /// <param name="tag">The tag.</param>
    /// <param name="unionCase">The case.</param>
    /// <returns>False for an unknown tag.</returns>
    public bool TryGetCase(ushort tag, [NotNullWhen(true)] out SchemaUnionCase? unionCase) => _cases.TryGetValue(tag, out unionCase);
}

/// <summary>An enum, written as its underlying integer.</summary>
public sealed class SchemaEnumDecl : SchemaTypeDecl
{
    internal SchemaEnumDecl(
        string id,
        string clrMetadataName,
        string clrNamespace,
        string name,
        IReadOnlyList<string> containingTypes,
        string? docs,
        SchemaPrimKind underlying,
        bool isFlags,
        SchemaEnumMember[] members)
        : base(SchemaDeclKind.Enum, id, clrMetadataName, clrNamespace, name, containingTypes, docs)
    {
        Underlying = underlying;
        IsFlags = isFlags;
        Members = members;
    }

    /// <summary>The underlying integer type.</summary>
    public SchemaPrimKind Underlying { get; }

    /// <summary>Declared with <c>[Flags]</c>.</summary>
    public bool IsFlags { get; }

    /// <summary>The members.</summary>
    public IReadOnlyList<SchemaEnumMember> Members { get; }

    /// <summary>True when the underlying type is unsigned.</summary>
    public bool IsUnsigned => Underlying is SchemaPrimKind.Byte or SchemaPrimKind.UInt16 or SchemaPrimKind.UInt32
        or SchemaPrimKind.UInt64;

    /// <summary>Finds the member with exactly this value.</summary>
    /// <param name="value">The raw 64 bits.</param>
    /// <param name="name">The member name.</param>
    /// <returns>False when no member has the value.</returns>
    public bool TryGetName(long value, [NotNullWhen(true)] out string? name)
    {
        foreach (var member in Members)
        {
            if (member.Value == value)
            {
                name = member.Name;
                return true;
            }
        }

        name = null;
        return false;
    }
}

/// <summary>One keyed member of an object.</summary>
/// <param name="Name">Member name.</param>
/// <param name="Key">Array position.</param>
/// <param name="Type">Member type.</param>
/// <param name="RefNullable">Same as <c>Type.RefNullable</c>.</param>
/// <param name="WasNonPublic">Not public in .NET.</param>
/// <param name="DefaultLiteral">The member's initializer when it is a simple constant.</param>
/// <param name="Docs">Summary documentation, or null.</param>
public sealed record SchemaMember(
    string Name,
    int Key,
    SchemaTypeRef Type,
    bool RefNullable,
    bool WasNonPublic,
    SchemaLiteral? DefaultLiteral,
    string? Docs);

/// <summary>A constant member initializer.</summary>
/// <param name="Kind">Literal kind.</param>
/// <param name="Value">The value in invariant culture; null for <see cref="SchemaLiteralKind.Null"/>.</param>
public sealed record SchemaLiteral(SchemaLiteralKind Kind, string? Value);

/// <summary>One union case.</summary>
/// <param name="Tag">Tag written on the wire.</param>
/// <param name="CaseDeclId">The case's declared type.</param>
/// <param name="InheritanceDepth">Base type chain length of the case.</param>
public sealed record SchemaUnionCase(ushort Tag, string CaseDeclId, int InheritanceDepth)
{
    /// <summary>The declaration <see cref="CaseDeclId"/> names; null when the schema does not contain it.</summary>
    public SchemaTypeDecl? Decl { get; internal set; }
}

/// <summary>One enum member.</summary>
/// <param name="Name">Member name.</param>
/// <param name="Value">The raw 64 bits (reinterpret for UInt64 enums).</param>
public readonly record struct SchemaEnumMember(string Name, long Value);
