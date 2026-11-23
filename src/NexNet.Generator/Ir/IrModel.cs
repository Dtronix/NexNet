using NexNet.Generator.Models;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Ir;

// The language-neutral intermediate representation of everything a target language needs to talk to a NexNet server:
// the exported nexus pairs (methods, ids, hashes, handshake values) and every serialized type they reach. It holds no
// Roslyn symbols and every record has value equality, so it can flow through incremental pipeline steps. The schema
// is documented in docs/internals/nexnet-ir.md; any breaking change bumps NexusIr.CurrentVersion.

/// <summary>Root of the IR: one per compilation, the deterministic merge of every producer's <see cref="IrFragment"/>.</summary>
/// <param name="IrVersion">Schema version (<see cref="CurrentVersion"/>).</param>
/// <param name="Exports">Exported nexus pairs, ordered by namespace then name (ordinal).</param>
/// <param name="Types">Every serialized declared type reachable from the exports or other producers, ordered by id (ordinal).</param>
internal sealed record NexusIr(int IrVersion, EquatableArray<ExportIr> Exports, EquatableArray<TypeDeclIr> Types)
{
    /// <summary>Current schema version. Bump on any breaking change to the IR or its JSON or MessagePack form.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>The IR contributed by one producer (a nexus class, a [NexusObject] type or the assembly attributes).</summary>
internal sealed record IrFragment(EquatableArray<ExportIr> Exports, EquatableArray<TypeDeclIr> Types)
{
    public static readonly IrFragment Empty = new(EquatableArray<ExportIr>.Empty, EquatableArray<TypeDeclIr>.Empty);
}

/// <summary>One exported nexus pair, seen from the client: the API it calls on the server and the API it implements.</summary>
/// <param name="Name">Simple name of the nexus class the export was built from.</param>
/// <param name="ClrNamespace">Namespace of that class (empty for the global namespace).</param>
/// <param name="Targets">Target-language flags (bit values of <c>NexNet.NexusTargets</c>); 0 when none were requested.</param>
/// <param name="ServerApi">The server nexus interface: what the client invokes.</param>
/// <param name="ClientApi">The client nexus interface: what the server invokes on the client.</param>
/// <param name="Handshake">The values the client sends in its greeting.</param>
/// <param name="ClientAcceptedIds">The (hash, id) pairs a client accepts in incoming invocations, ordered by id.</param>
/// <param name="Docs">Summary documentation of the nexus class, or null.</param>
/// <param name="Location">Source location of the class identifier, for diagnostics. In memory only: not written to JSON.</param>
internal sealed record ExportIr(
    string Name,
    string ClrNamespace,
    int Targets,
    InterfaceIr ServerApi,
    InterfaceIr ClientApi,
    HandshakeIr Handshake,
    EquatableArray<AcceptedIdIr> ClientAcceptedIds,
    string? Docs,
    LocationData? Location);

/// <summary>
/// Precomputed client greeting values, exactly as <c>NexusSession.StartAsClient</c> derives them. Opaque constants: no
/// target recomputes them.
/// </summary>
/// <param name="Version">The server API's latest version string, or null when the server API is not versioned.</param>
/// <param name="ServerNexusHash">The hash of that version, or the server API hash when not versioned.</param>
/// <param name="ClientNexusHash">The client API hash.</param>
internal sealed record HandshakeIr(string? Version, int ServerNexusHash, int ClientNexusHash);

/// <summary>An invocation a client accepts: <paramref name="Hash"/> is the API hash it is checked against.</summary>
internal sealed record AcceptedIdIr(int Hash, ushort Id);

/// <summary>A nexus interface with everything it inherits.</summary>
/// <param name="ClrName">Full metadata name of the interface, e.g. <c>Ns.IServerNexus</c>.</param>
/// <param name="Name">Simple name of the interface.</param>
/// <param name="ClrNamespace">Namespace of the interface.</param>
/// <param name="Hash">The API hash (<c>IInvocationMethodHash.MethodHash</c>).</param>
/// <param name="Methods">Every invocable method, including inherited ones, ordered by id.</param>
/// <param name="Collections">Every synchronized collection, including inherited ones, ordered by id.</param>
/// <param name="Versions">The [NexusVersion] interfaces of the hierarchy in generator order; the last one is the current version.</param>
internal sealed record InterfaceIr(
    string ClrName,
    string Name,
    string ClrNamespace,
    int Hash,
    EquatableArray<MethodIr> Methods,
    EquatableArray<CollectionIr> Collections,
    EquatableArray<VersionIr> Versions);

/// <summary>One [NexusVersion] interface of a hierarchy.</summary>
/// <param name="Version">The version string ("" when the attribute names none).</param>
/// <param name="Hash">The hash of that interface (its HashLock value when correct).</param>
/// <param name="MethodIds">Ids of the methods and collections invocable on that version, ascending.</param>
internal sealed record VersionIr(string Version, int Hash, EquatableArray<ushort> MethodIds);

internal enum ReturnKind : byte
{
    /// <summary>Fire and forget: no result is sent back.</summary>
    Void,

    /// <summary><c>ValueTask</c>: completes when the remote method completes.</summary>
    Task,

    /// <summary><c>ValueTask&lt;T&gt;</c>: returns a value of <see cref="MethodIr.ReturnType"/>.</summary>
    TaskOf,
}

internal enum PipeKind : byte
{
    None,
    DuplexPipe,
    Channel,
}

/// <summary>One invocable method.</summary>
/// <param name="Name">Method name.</param>
/// <param name="Id">Method id on the wire.</param>
/// <param name="Hash">Method hash (a component of the API hash).</param>
/// <param name="ReturnKind">How the method completes.</param>
/// <param name="ReturnType">The result type for <see cref="Ir.ReturnKind.TaskOf"/>; null otherwise.</param>
/// <param name="Params">Every parameter in declaration order, including cancellation tokens and pipes.</param>
/// <param name="SerializedCount">Number of parameters written in the invocation arguments.</param>
/// <param name="HasCancellation">True when the method takes a CancellationToken.</param>
/// <param name="PipeKind">The pipe or channel parameter kind, if any.</param>
/// <param name="PipeType">The channel item type for <see cref="Ir.PipeKind.Channel"/>; null otherwise.</param>
/// <param name="SeparatePool">True when the method runs in the separate invocation pool on the receiving side.</param>
/// <param name="IsAuthorized">True when the server checks authorization before running the method.</param>
/// <param name="Docs">Summary documentation, or null.</param>
/// <param name="Location">Source location, for diagnostics. In memory only: not written to JSON.</param>
internal sealed record MethodIr(
    string Name,
    ushort Id,
    int Hash,
    ReturnKind ReturnKind,
    TypeRef? ReturnType,
    EquatableArray<ParamIr> Params,
    int SerializedCount,
    bool HasCancellation,
    PipeKind PipeKind,
    TypeRef? PipeType,
    bool SeparatePool,
    bool IsAuthorized,
    string? Docs,
    LocationData? Location);

internal enum ParamKind : byte
{
    Value,
    CancellationToken,
    DuplexPipe,
    DuplexChannel,
}

/// <summary>One method parameter.</summary>
/// <param name="Name">Parameter name.</param>
/// <param name="Kind">What the parameter is.</param>
/// <param name="Type">The value type for <see cref="ParamKind.Value"/>, the item type for <see cref="ParamKind.DuplexChannel"/>; null otherwise.</param>
/// <param name="WireIndex">1-based position in the serialized arguments (pipes are written as their initial pipe id); 0 when not serialized.</param>
/// <param name="Nullable">True when the value may be null (<c>Nullable&lt;T&gt;</c> or a nullable reference).</param>
internal sealed record ParamIr(string Name, ParamKind Kind, TypeRef? Type, int WireIndex, bool Nullable);

internal enum CollectionKind : byte
{
    Unset,
    List,
}

internal enum CollectionMode : byte
{
    Unset,
    ServerToClient,
    BiDirectional,
    Relay,
}

/// <summary>One synchronized collection.</summary>
internal sealed record CollectionIr(
    string Name,
    ushort Id,
    int Hash,
    CollectionKind Kind,
    CollectionMode Mode,
    TypeRef? Item,
    bool IsAuthorized,
    string? Docs);

/// <summary>Built-in leaf types, named after their .NET type.</summary>
internal enum PrimKind : byte
{
    None,
    Bool,
    SByte,
    Byte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Single,
    Double,
    Half,
    Char,
    Decimal,
    String,
    DateTime,
    DateTimeOffset,
    TimeSpan,
    DateOnly,
    TimeOnly,
    Guid,
    BigInteger,
    Uri,
    Version,
}

internal enum TypeRefKind : byte
{
    /// <summary>A built-in leaf: <see cref="TypeRef.Prim"/>.</summary>
    Prim,

    /// <summary>Bytes written as MessagePack bin: <c>byte[]</c>, <c>Memory&lt;byte&gt;</c>... (<see cref="TypeRef.Clr"/>).</summary>
    Bin,

    /// <summary>An ext 78 primitive array of <see cref="TypeRef.Prim"/> (<see cref="TypeRef.Clr"/>: Array, List, Memory, ReadOnlyMemory).</summary>
    PrimArray,

    /// <summary>An element-wise sequence of <c>Args[0]</c>.</summary>
    Seq,

    /// <summary>A <c>HashSet&lt;T&gt;</c> of <c>Args[0]</c>.</summary>
    Set,

    /// <summary>A <c>Queue&lt;T&gt;</c> of <c>Args[0]</c>, written front first.</summary>
    Queue,

    /// <summary>A <c>Stack&lt;T&gt;</c> of <c>Args[0]</c>, written top first.</summary>
    Stack,

    /// <summary>A dictionary from <c>Args[0]</c> to <c>Args[1]</c>.</summary>
    Map,

    /// <summary>A tuple of <c>Args</c>, written as an array (<see cref="TypeRef.Clr"/>: ValueTuple, Tuple, KeyValuePair).</summary>
    Tuple,

    /// <summary>A <c>Nullable&lt;T&gt;</c> of <c>Args[0]</c>.</summary>
    Nullable,

    /// <summary>A declared type (<see cref="TypeRef.DeclId"/>) with <c>Args</c> as its type arguments.</summary>
    Decl,

    /// <summary>A type parameter of the enclosing generic declaration (<see cref="TypeRef.Ordinal"/>).</summary>
    TypeParam,

    /// <summary>A type with no language-neutral form: a user formatter type or an unsupported type (<see cref="TypeRef.Clr"/>).</summary>
    External,
}

/// <summary>A reference to a serialized type. Only the fields meaningful for <see cref="Kind"/> are set.</summary>
internal sealed record TypeRef(TypeRefKind Kind)
{
    /// <summary><see cref="TypeRefKind.Prim"/>: the leaf; <see cref="TypeRefKind.PrimArray"/>: the element.</summary>
    public PrimKind Prim { get; init; }

    /// <summary>
    /// The .NET container (Bin, PrimArray, Seq, Set, Queue, Stack, Map, Tuple: e.g. <c>Array</c>, <c>List</c>,
    /// <c>IReadOnlyList</c>, <c>Dictionary</c>, <c>ValueTuple</c>), or the full .NET name (External).
    /// </summary>
    public string? Clr { get; init; }

    /// <summary>Element, key/value, tuple element, nullable inner or type argument references.</summary>
    public EquatableArray<TypeRef> Args { get; init; } = EquatableArray<TypeRef>.Empty;

    /// <summary><see cref="TypeRefKind.Decl"/>: the <see cref="TypeDeclIr.Id"/>.</summary>
    public string? DeclId { get; init; }

    /// <summary><see cref="TypeRefKind.TypeParam"/>: the type parameter position.</summary>
    public int Ordinal { get; init; }

    /// <summary>True on a reference-type occurrence that may be null (annotated, or declared nullable-oblivious).</summary>
    public bool RefNullable { get; init; }

    /// <summary>
    /// <see cref="TypeRefKind.External"/>: the native formatters declared for the type with
    /// <c>[assembly: NexusTargetFormatter&lt;T&gt;]</c>, one per target bit, ordered by target. Empty when there are none.
    /// </summary>
    public EquatableArray<NativeFormatterIr> NativeFormatters { get; init; } = EquatableArray<NativeFormatterIr>.Empty;

    /// <summary>
    /// <see cref="TypeRefKind.Prim"/> Int64/UInt64 only: the occurrence opted in to the checked JavaScript <c>number</c>
    /// mapping (D13, <c>[NexusJsNumber]</c>) instead of <c>bigint</c>. Other targets ignore it.
    /// </summary>
    public bool JsNumber { get; init; }

    /// <summary>True when the referenced value may be null: a nullable reference or a <c>Nullable&lt;T&gt;</c>.</summary>
    public bool MayBeNull => RefNullable || Kind == TypeRefKind.Nullable;

    /// <summary>The <see cref="TypeRefKind.Tuple"/> flavor: Value (ValueTuple), Ref (Tuple) or KVP (KeyValuePair).</summary>
    public string? TupleKind => Kind != TypeRefKind.Tuple
        ? null
        : Clr switch
        {
            "ValueTuple" => "Value",
            "KeyValuePair" => "KVP",
            _ => "Ref",
        };
}

/// <summary>A hand-written formatter in one target language for an <see cref="TypeRefKind.External"/> type.</summary>
/// <param name="Target">The target bit (a single value of <c>NexNet.NexusTargets</c>).</param>
/// <param name="Formatter">Fully qualified name of the native formatter class.</param>
internal sealed record NativeFormatterIr(int Target, string Formatter);

/// <summary>A serialized declared type: a [NexusObject] object, a [NexusObject] union or an enum.</summary>
/// <param name="Id">Unique id: the full metadata name of the definition, e.g. <c>Ns.Outer+Box`1</c>.</param>
/// <param name="ClrMetadataName">Same as <paramref name="Id"/>.</param>
/// <param name="ClrNamespace">Namespace (empty for the global namespace).</param>
/// <param name="Name">Simple name without arity.</param>
/// <param name="ContainingTypes">Simple names of the containing types, outermost first.</param>
/// <param name="Docs">Summary documentation, or null.</param>
internal abstract record TypeDeclIr(
    string Id,
    string ClrMetadataName,
    string ClrNamespace,
    string Name,
    EquatableArray<string> ContainingTypes,
    string? Docs);

/// <summary>A [NexusObject] class or struct, written as an array of <see cref="ArrayLength"/> elements (nil in the gaps).</summary>
internal sealed record ObjectDeclIr(
    string Id,
    string ClrMetadataName,
    string ClrNamespace,
    string Name,
    EquatableArray<string> ContainingTypes,
    string? Docs,
    bool IsStruct,
    bool IsRecord,
    bool IsAbstract,
    EquatableArray<string> TypeParams,
    string? BaseDeclId,
    int ArrayLength,
    bool IsRecursive,
    EquatableArray<MemberIr> Members)
    : TypeDeclIr(Id, ClrMetadataName, ClrNamespace, Name, ContainingTypes, Docs);

/// <summary>A [NexusObject] abstract class or interface with [NexusUnion] cases, written as <c>[tag, value]</c>.</summary>
internal sealed record UnionDeclIr(
    string Id,
    string ClrMetadataName,
    string ClrNamespace,
    string Name,
    EquatableArray<string> ContainingTypes,
    string? Docs,
    bool IsInterface,
    EquatableArray<string> TypeParams,
    bool IsRecursive,
    EquatableArray<UnionCaseIr> Cases)
    : TypeDeclIr(Id, ClrMetadataName, ClrNamespace, Name, ContainingTypes, Docs);

/// <summary>An enum, written as its underlying integer.</summary>
internal sealed record EnumDeclIr(
    string Id,
    string ClrMetadataName,
    string ClrNamespace,
    string Name,
    EquatableArray<string> ContainingTypes,
    string? Docs,
    PrimKind Underlying,
    bool IsFlags,
    EquatableArray<EnumMemberIr> Members)
    : TypeDeclIr(Id, ClrMetadataName, ClrNamespace, Name, ContainingTypes, Docs);

/// <summary>One keyed member of an object.</summary>
/// <param name="Name">Member name.</param>
/// <param name="Key">Array position ([NexusKey]).</param>
/// <param name="Type">Member type.</param>
/// <param name="RefNullable">Same as <c>Type.RefNullable</c>.</param>
/// <param name="WasNonPublic">True when the member is not public in .NET.</param>
/// <param name="DefaultLiteral">The member's initializer when it is a simple constant; null otherwise.</param>
/// <param name="Docs">Summary documentation, or null.</param>
internal sealed record MemberIr(
    string Name,
    int Key,
    TypeRef Type,
    bool RefNullable,
    bool WasNonPublic,
    LiteralIr? DefaultLiteral,
    string? Docs);

internal enum LiteralKind : byte
{
    Null,
    Bool,
    Integer,
    Real,
    Decimal,
    String,
    Char,

    /// <summary>An enum member: the value is the member name.</summary>
    Enum,
}

/// <summary>A constant initializer. <see cref="Value"/> is in invariant culture; null for <see cref="LiteralKind.Null"/>.</summary>
internal sealed record LiteralIr(LiteralKind Kind, string? Value);

/// <summary>One union case.</summary>
/// <param name="Tag">Tag written on the wire.</param>
/// <param name="CaseDeclId">The case's declared type.</param>
/// <param name="InheritanceDepth">Base type chain length of the case; writers test deeper cases first.</param>
internal sealed record UnionCaseIr(ushort Tag, string CaseDeclId, int InheritanceDepth);

/// <summary>One enum member. <see cref="Value"/> holds the raw 64 bits (reinterpret for UInt64 enums).</summary>
internal sealed record EnumMemberIr(string Name, long Value);
