# NexNet generator IR (`nexnet-ir.json`)

Schema version **1**. The model lives in `src/NexNet.Generator/Ir/`.

The IR is a language-neutral description of everything a client in another language needs to talk to a NexNet server:
the exported nexus pairs (methods, ids, hashes, handshake values) and every serialized type they reach. Target-language
emitters (Java, JavaScript, the C# test target, future Rust/C++) read only the IR; they never look at Roslyn symbols or
re-derive ids and hashes.

It is projected from the same data the C# emitter uses:

- method, collection and interface ids, hashes, versions and flags come from `InvocationInterfaceData` (`NexusDataExtractor`);
- types come from the `TypeShape` graph (`ShapeBuilder`), the single definition of the wire shape that code generation
  and `ShapeHasher` both read.

So the wire shape, the hashes and every target's code cannot drift apart. Building the IR does not change the C# output.

## 1. Building and merging

| API | Use |
|---|---|
| `IrBuilder.Build(compilation or shapes, nexusClass, data, ct)` | Fragment of one `[Nexus]` class: its export and the types both APIs reach. `data` is the extractor's `NexusGenerationData` for that class. |
| `IrBuilder.BuildExport(shapes, name, ns, targets, server…, client…, docs, location, ct)` | Fragment of an export given by its two interfaces and their extractor data (used for `[assembly: NexusClientExport<TServer, TClient>]`, which has no class). |
| `IrBuilder.BuildTypes(shapes, roots, ct)` | Fragment of standalone types (a `[NexusObject]` type, a type used only at runtime). |
| `IrBuilder.BuildAssemblyDeclared(compilation, ct)` | Fragment of the `[assembly: NexusSerializable<T>]` roots. |
| `IrBuilder.Merge(fragments)` | The compilation's `NexusIr`. Independent of fragment order. |
| `IrNativeFormatters.Apply(ir, mappings)` | Attaches `[assembly: NexusTargetFormatter<T>]` mappings to the External references of a merged IR. |
| `IrJsNumbers.Apply(ir, compilation)` | Applies `[assembly: NexusJsNumber]` to every reference of a merged IR (`jsNumber`, [#259](https://github.com/Dtronix/NexNet/pull/259)). Member, parameter and return-value attributes are applied while the fragments are built. |
| `IrJson.Write(ir)` / `IrJson.Read(json)` | JSON form (this document). |
| `IrMsgPack.Write(ir)` / `IrMsgPack.Hash(bytes)` | MessagePack form, the inspector schema (§5), and its 64-bit FNV-1a hash. |

Fragments are built in the transform phase (symbol access); every IR record is symbol-free and has value equality, so
fragments and the merged IR are cacheable incremental pipeline values.

**Pipeline.** `NexusGenerator` produces the IR as an `IncrementalValueProvider<NexusIr?>` (tracking name
`NexusIr`), built by `Extraction/TargetExtractor`:

- a server `[Nexus]` class with `Targets` set contributes its export fragment (built in its transform, `targets` set
  from the flags); `Targets` on a client nexus is NEXNET043 and exports nothing;
- each `[assembly: NexusClientExport<TServer, TClient>(Targets = …)]` with non-zero targets contributes an export built
  from the two interfaces exactly as a C# client nexus of them would extract them (no authorization data);
- when at least one export exists, the `[NexusObject]` types and the `[assembly: NexusSerializable<T>]` roots are added,
  the fragments are merged and the native formatter mappings are applied. With no export the value is null and no IR
  work is done.

**Merge rules.** Exports are keyed by (`clrNamespace`, `name`) and ordered ordinally by namespace, then name. Types are keyed
by `id` and ordered ordinally by id. A key produced by several producers appears once (producers build identical items
for the same key; if they ever differ, the one with the ordinally smallest JSON wins, so the result stays order
independent).

## 2. JSON format

- UTF-8, LF line endings, two-space indent, trailing line feed.
- Object keys are sorted ordinally. Arrays keep IR order. Arrays whose items are all scalars are written on one line.
- A member whose value is null is omitted; readers treat a missing member as null (or as the documented default).
- No timestamps, absolute paths or source locations. The in-memory IR carries `Location` on exports and methods for
  diagnostics; it is not serialized and reads back as null.
- Enum-valued fields are written as their member names (PascalCase). Integers are JSON numbers; 64-bit values may
  exceed 2^53 (enum member values), so readers in languages with double-only numbers must parse them as big integers.

### 2.1 Root

| Field | Type | Meaning |
|---|---|---|
| `irVersion` | int | Schema version, currently 1. Readers reject versions they do not know. |
| `exports` | Export[] | Exported nexus pairs. |
| `types` | TypeDecl[] | Every serialized declared type reached by any producer. |

### 2.2 Export

One nexus pair seen from the **client**: the API it calls and the API it implements. `IrBuilder.Build` can build one for
any `[Nexus]` class; the generator pipeline exports only opted-in server nexuses and `NexusClientExport` declarations.

| Field | Type | Meaning |
|---|---|---|
| `name` | string | Simple name of the nexus class, or the `NexusClientExport` name (default: the server interface name without its leading `I`). |
| `clrNamespace` | string | Namespace of the class, or of the server interface for a `NexusClientExport` ("" for the global namespace). |
| `targets` | int | Target-language flags (bit values of `NexNet.NexusTargets`: Java = 1, JavaScript = 2; 4 and 8 reserved for Rust and C++). Non-zero in the generator's IR. |
| `serverApi` | Interface | The server nexus interface: what the client invokes. |
| `clientApi` | Interface | The client nexus interface: what the server invokes on the client. |
| `handshake` | Handshake | Client greeting values. |
| `clientAcceptedIds` | {hash, id}[] | Incoming invocations a client accepts (its `VersionMethodHashSet`), ascending by id. |
| `docs` | string? | Summary documentation of the class. |

**Handshake** (opaque constants: targets copy them, never recompute them):

| Field | Type | Meaning |
|---|---|---|
| `version` | string? | The server API's last `[NexusVersion]` string (last entry of `serverApi.versions`), or absent when the server API is not versioned. "" when the attribute names no version. |
| `serverNexusHash` | int | That version's hash, or `serverApi.hash` when not versioned. |
| `clientNexusHash` | int | `clientApi.hash`. |

This is exactly what `NexusSession.StartAsClient` sends. A client accepts an incoming invocation of method `id` only when
`(hash, id)` is in `clientAcceptedIds`; the hash is always `clientApi.hash` (the C# key is `(hash << 16) | id`).

### 2.3 Interface

| Field | Type | Meaning |
|---|---|---|
| `clrName` | string | Full metadata name, e.g. `Ns.IServerNexus` (nested: `Ns.Outer+IApi`). |
| `name` | string | Simple name. |
| `clrNamespace` | string | Namespace. |
| `hash` | int | API hash (`IInvocationMethodHash.MethodHash`). |
| `methods` | Method[] | Every invocable method including inherited ones (`[NexusMethod(Ignore = true)]` excluded), ascending by id. |
| `collections` | Collection[] | Every synchronized collection including inherited ones, ascending by id. |
| `versions` | Version[] | The `[NexusVersion]` interfaces of the hierarchy in generator order (inherited interfaces ordinally by display name, then the interface itself); the last entry is the current version. Empty when unversioned. |

**Version**: `version` (string, "" when unnamed), `hash` (int, that interface's hash), `methodIds` (ushort[], ascending ids of
the methods **and collections** invocable on that version; a server accepts `(hash, id)` for each).

### 2.4 Method

| Field | Type | Meaning |
|---|---|---|
| `name` | string | Method name. |
| `id` | ushort | Method id on the wire. |
| `hash` | int | Method hash (an input of the API hash). |
| `returnKind` | `Void` | `Task` | `TaskOf` | Fire and forget (`void`), `ValueTask`, `ValueTask<T>`. |
| `returnType` | TypeRef? | `T` for `TaskOf`. |
| `params` | Param[] | All parameters in declaration order, including cancellation tokens and pipes. |
| `serializedCount` | int | Number of values in the invocation arguments (pipes count, cancellation tokens do not). |
| `hasCancellation` | bool | Takes a `CancellationToken`. |
| `pipeKind` | `None` | `DuplexPipe` | `Channel` | Pipe or channel parameter, if any. Pipe methods are invoked with `IgnoreReturn | DuplexPipe`. |
| `pipeType` | TypeRef? | Channel item type for `Channel`. |
| `separatePool` | bool | Runs in the receiver's separate invocation pool (`[NexusMethod(SeparatePool = true)]` without a pipe). |
| `isAuthorized` | bool | The server checks `[NexusAuthorize]` before running it. Known only from a server class build. |
| `docs` | string? | Summary documentation. |

**Param**:

| Field | Type | Meaning |
|---|---|---|
| `name` | string | Parameter name. |
| `kind` | `Value` | `CancellationToken` | `DuplexPipe` | `DuplexChannel` | What it is. |
| `type` | TypeRef? | The value type (`Value`) or the channel item type (`DuplexChannel`); absent otherwise. |
| `wireIndex` | int | 1-based position in the serialized arguments; pipes and channels are written as their initial pipe id (a byte). 0 = not serialized. |
| `nullable` | bool | The value may be null (`Nullable<T>` or a nullable reference). |

### 2.5 Collection

`name`, `id` (ushort), `hash` (int), `kind` (`List` | `Unset`), `mode` (`ServerToClient` | `BiDirectional` | `Relay` | `Unset`), `item`
(TypeRef?), `isAuthorized` (bool), `docs` (string?).

### 2.6 TypeRef

A reference to a serialized type. `kind` selects which other fields are present.

| `kind` | Fields | Wire form |
|---|---|---|
| `Prim` | `prim` | A built-in leaf (see PrimKind). |
| `Bin` | `clr` (`Array`, `Memory`, `ReadOnlyMemory`, `ArraySegment`, `ReadOnlySequence`) | MessagePack bin: `byte[]` and byte memories. |
| `PrimArray` | `prim` (element), `clr` (`Array`, `List`, `Memory`, `ReadOnlyMemory`) | Ext 78 primitive array. Elements: SByte, Int16, UInt16, Int32, UInt32, Int64, UInt64, Single, Double, Char, Half. |
| `Seq` | `args[0]` element, `clr` (`Array`, `List`, `IList`, `IReadOnlyList`, `ICollection`, `IReadOnlyCollection`, `IEnumerable`) | Element-wise array (`List<byte>` is a Seq of Byte, not bin). |
| `Set` | `args[0]`, `clr` `HashSet` | Array. |
| `Queue` | `args[0]`, `clr` `Queue` | Array, front first. |
| `Stack` | `args[0]`, `clr` `Stack` | Array, top first. |
| `Map` | `args[0]` key, `args[1]` value, `clr` (`Dictionary`, `IDictionary`, `IReadOnlyDictionary`) | Map. |
| `Tuple` | `args` elements, `clr`: `ValueTuple` (tuple kind Value), `Tuple` (Ref), `KeyValuePair` (KVP) | Array of the elements. |
| `Nullable` | `args[0]` inner | Nil or the inner value. |
| `Decl` | `id`, `args` (type arguments of a generic declaration) | See TypeDecl. |
| `TypeParam` | `ordinal` | Type parameter of the enclosing generic declaration. |
| `External` | `clr` (full .NET name), `args`, `nativeFormatters` | No language-neutral form: a `[NexusFormatter]` user type (needs a `[NexusTargetFormatter<T>(target, "native.Formatter")]` mapping), `object`, multi-dimensional arrays or another type C# rejects (NEXNET028). The target validator turns these into NEXNET040 per target that has no native formatter. |

**nativeFormatters** (External only; absent when empty): `{target, formatter}[]` ordered by `target`, one entry per target
bit declared with `[assembly: NexusTargetFormatter<T>(target, formatter)]` for the type whose definition's full metadata
name equals `clr` (a generic user type's mapping applies to every construction). `formatter` is the native class's
fully qualified name, as written in the attribute (trimmed).

**jsNumber** ([#259](https://github.com/Dtronix/NexNet/pull/259); `Prim` with `prim` `Int64` or `UInt64` only; absent = false): the occurrence opted in to the checked
JavaScript `number` mapping with `[NexusJsNumber]`, instead of the default `bigint`. The wire form is unchanged; other targets
ignore the flag. It is set on every Int64/UInt64 leaf of the type of an attributed `[NexusObject]` member (property or
field), method parameter (for a channel: its item type), return value (`[return: NexusJsNumber]`) or collection property,
looking through `Nullable`, `Seq`, `Set`, `Queue`, `Stack`, `Map`, `Tuple` and the `args` of a `Decl`, but not into
`PrimArray` (ext 78 arrays keep their typed-array mapping) or `External`. `[assembly: NexusJsNumber]` sets it on every such
leaf of the IR. Enum declarations are not affected. The attribute does not change any hash.

Any occurrence may also carry `refNullable: true` (absent = false): a reference-type occurrence that may be null because it is
annotated (`string?`) or declared in a nullable-oblivious context. `Nullable<T>` is expressed by `kind: Nullable` instead.

**PrimKind**: `Bool`, `SByte`, `Byte`, `Int16`, `UInt16`, `Int32`, `UInt32`, `Int64`, `UInt64`, `Single`, `Double`, `Half`, `Char`,
`Decimal`, `String`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly`, `Guid`, `BigInteger`, `Uri`, `Version`. Their encodings are those of
the C# built-in formatters (see `docs/internals/protocol-specification.md`).

### 2.7 TypeDecl

Common fields:

| Field | Type | Meaning |
|---|---|---|
| `kind` | `Object` | `Union` | `Enum` | Declaration kind. |
| `id` | string | Unique id: the full metadata name of the **definition**, e.g. `` Ns.Outer+Box`1 ``. Generic types have one declaration; references carry the type arguments. |
| `clrMetadataName` | string | Same as `id`. |
| `clrNamespace` | string | Namespace ("" for global). |
| `name` | string | Simple name without arity. |
| `containingTypes` | string[] | Simple names of containing types, outermost first. |
| `docs` | string? | Summary documentation. |

**Object** (a `[NexusObject]` class, struct or record, written as an array of `arrayLength` elements with nil in the gaps;
readers skip extra trailing elements):

| Field | Type | Meaning |
|---|---|---|
| `isStruct` | bool | Value type: never null on the wire. |
| `isRecord` | bool | Declared as a record. |
| `isAbstract` | bool | Abstract class or interface without union cases (C# rejects it, NEXNET034). |
| `typeParams` | string[] | Type parameter names. |
| `baseDeclId` | string? | The base type, when it is a `[NexusObject]` (union cases derive from their union). |
| `arrayLength` | int | Highest key + 1 (0 when there are no members). |
| `isRecursive` | bool | The type can reach itself through member types: targets may need lazy formatter references. |
| `members` | Member[] | Keyed members (inherited included, `[NexusIgnore]` excluded), ascending by key. |

**Member**: `name`, `key` (int), `type` (TypeRef), `refNullable` (same as `type.refNullable`), `wasNonPublic` (not public in .NET),
`defaultLiteral` (Literal?), `docs` (string?).

**Literal** records a member initializer that is a simple constant, so targets can give members missing from an older
peer the same default as C#: `kind` is `Null`, `Bool` (`"true"`/`"false"`), `Integer`, `Real` (round-trip format), `Decimal`,
`String`, `Char` or `Enum` (`value` is the member name); `value` is invariant-culture text, absent for `Null`. The literal is
the source text's value, not converted to the member type (`double D = 5` gives `Integer "5"`). Anything else
(expressions, `new()`, `default`, flag combinations, positional record defaults) records no literal.

**Union** (a `[NexusObject]` abstract class or interface with `[NexusUnion<T>(tag)]`, written as `[tag, value]` or nil):
`isInterface` (bool), `typeParams` (string[]), `isRecursive` (bool), `cases` ({`tag` ushort, `caseDeclId` string, `inheritanceDepth`
int}[], declaration order). Writers test cases in descending `inheritanceDepth` (ties in listed order) so a subclass case is
not shadowed by its base case; readers switch on the tag.

**Enum** (written as its underlying integer): `underlying` (PrimKind), `isFlags` (bool), `members` ({`name`, `value`}[],
declaration order). `value` holds the raw 64 bits as a signed integer: reinterpret it for `UInt64` enums
(`0xFFFFFFFFFFFFFFFF` is written as `-1`).

## 3. Versioning policy

- `irVersion` (`NexusIr.CurrentVersion`) is bumped on **any breaking change**: a removed or renamed field, a changed meaning or
  encoding, a new enum value that existing readers would misread, or a new required field.
- Adding an optional field that readers may ignore is not breaking, but it is still a contract change: the IR schema is frozen,
  and every change goes through one PR that updates this document, the
  snapshots in `src/NexNet.Generator.Tests/Ir/Snapshots/` and every consumer in one PR.
- `IrJson.Read` rejects any `irVersion` other than the one it was built for.
- The MessagePack form (§5) shares `irVersion`: a change to its layout (field order, an enum's numeric values) is a
  breaking change like any other. Appending a field to the end of a record array is the MessagePack form of an optional
  field.

## 4. Example

The export of `VersionedClientNexusV1` (a client of a versioned server API) from the versioned snapshot:

```json
{
  "clientAcceptedIds": [
    {
      "hash": -165921370,
      "id": 0
    }
  ],
  "clientApi": {
    "clrName": "NexNet.IntegrationTests.TestInterfaces.ISimpleClientNexus",
    "clrNamespace": "NexNet.IntegrationTests.TestInterfaces",
    "collections": [],
    "hash": -165921370,
    "methods": [
      {
        "hasCancellation": false,
        "hash": -205654457,
        "id": 0,
        "isAuthorized": false,
        "name": "Run",
        "params": [],
        "pipeKind": "None",
        "returnKind": "Task",
        "separatePool": false,
        "serializedCount": 0
      }
    ],
    "name": "ISimpleClientNexus",
    "versions": []
  },
  "clrNamespace": "NexNet.IntegrationTests.TestInterfaces",
  "handshake": {
    "clientNexusHash": -165921370,
    "serverNexusHash": -326045431,
    "version": "v1.0"
  },
  "name": "VersionedClientNexusV1",
  "serverApi": {
    "clrName": "NexNet.IntegrationTests.TestInterfaces.IVersionedServerNexusV1",
    "clrNamespace": "NexNet.IntegrationTests.TestInterfaces",
    "collections": [],
    "hash": -326045431,
    "methods": [
      {
        "hasCancellation": false,
        "hash": -78067274,
        "id": 1,
        "isAuthorized": false,
        "name": "VerifyVersionV1",
        "params": [
          {
            "kind": "Value",
            "name": "version",
            "nullable": true,
            "type": {
              "kind": "Prim",
              "prim": "String",
              "refNullable": true
            },
            "wireIndex": 1
          }
        ],
        "pipeKind": "None",
        "returnKind": "TaskOf",
        "returnType": {
          "kind": "Prim",
          "prim": "Bool"
        },
        "separatePool": false,
        "serializedCount": 1
      }
    ],
    "name": "IVersionedServerNexusV1",
    "versions": [
      {
        "hash": -326045431,
        "methodIds": [1],
        "version": "v1.0"
      }
    ]
  },
  "targets": 0
}
```

A declaration (`Envelope<T>` from the zoo snapshot, trimmed to one member):

```json
{
  "arrayLength": 4,
  "clrMetadataName": "Zoo.Envelope`1",
  "clrNamespace": "Zoo",
  "containingTypes": [],
  "id": "Zoo.Envelope`1",
  "isAbstract": false,
  "isRecord": false,
  "isRecursive": false,
  "isStruct": false,
  "kind": "Object",
  "members": [
    {
      "key": 0,
      "name": "Value",
      "refNullable": false,
      "type": {
        "kind": "TypeParam",
        "ordinal": 0
      },
      "wasNonPublic": false
    }
  ],
  "name": "Envelope",
  "typeParams": ["T"]
}
```

The complete snapshots in `src/NexNet.Generator.Tests/Ir/Snapshots/` (`basic.json`, `versioned.json`, `zoo.json`) are the
reference examples; set `NEXNET_UPDATE_SNAPSHOTS=1` when running the generator tests to regenerate them after an
intended change.

## 5. MessagePack form (inspector schema, `.nnschema`)

`IrMsgPack.Write` (`src/NexNet.Generator/Ir/IrMsgPack.cs`) writes the same IR as MessagePack. It is the inspector
schema: the bytes a generated nexus embeds (§5.3), the inspector endpoint sends on attach and recordings store, and the
`.nnschema` file format. It carries exactly the fields of the JSON form; `src/NexNet.Generator.Tests/Ir/IrMsgPackDecoder.cs`
reads it back with NexNet's `MsgPackReader` into records equal to `IrJson.Read(IrJson.Write(ir))`.

### 5.1 Encoding

- Every record is a MessagePack **array** with its fields in the fixed order of the tables below; there are no maps, so
  there is no key order to keep stable. A null field is `nil` (never omitted), so positions never shift.
- Canonical, deterministic encoding (the form NexNet's `MsgPackWriter` produces): the smallest array and str header;
  non-negative integers in the smallest of positive fixint / uint8 / uint16 / uint32 / uint64, negative integers in the
  smallest of negative fixint / int8 / int16 / int32 / int64. Strings are UTF-8. Booleans are `true`/`false`.
- Enum-valued fields are their **numeric** values (the declaration order in `IrModel.cs`), not names:

| Enum | Values |
|---|---|
| ReturnKind | Void 0, Task 1, TaskOf 2 |
| PipeKind | None 0, DuplexPipe 1, Channel 2 |
| ParamKind | Value 0, CancellationToken 1, DuplexPipe 2, DuplexChannel 3 |
| CollectionKind | Unset 0, List 1 |
| CollectionMode | Unset 0, ServerToClient 1, BiDirectional 2, Relay 3 |
| PrimKind | None 0, Bool 1, SByte 2, Byte 3, Int16 4, UInt16 5, Int32 6, UInt32 7, Int64 8, UInt64 9, Single 10, Double 11, Half 12, Char 13, Decimal 14, String 15, DateTime 16, DateTimeOffset 17, TimeSpan 18, DateOnly 19, TimeOnly 20, Guid 21, BigInteger 22, Uri 23, Version 24 |
| TypeRefKind | Prim 0, Bin 1, PrimArray 2, Seq 3, Set 4, Queue 5, Stack 6, Map 7, Tuple 8, Nullable 9, Decl 10, TypeParam 11, External 12 |
| LiteralKind | Null 0, Bool 1, Integer 2, Real 3, Decimal 4, String 5, Char 6, Enum 7 |
| Declaration kind | Object 0, Union 1, Enum 2 (`IrMsgPack.DeclObject` / `DeclUnion` / `DeclEnum`) |

- A reader must skip trailing elements of a record array it does not know (§3: that is how an optional field is added).

### 5.2 Layout

Field names are those of §2; `?` marks a field that may be `nil`; `X[]` is an array of `X`.

| Record | Array elements, in order |
|---|---|
| Root | `irVersion`, `exports` Export[], `types` TypeDecl[] |
| Export | `name`, `clrNamespace`, `targets`, `serverApi` Interface, `clientApi` Interface, `handshake` Handshake, `clientAcceptedIds` AcceptedId[], `docs`? |
| Handshake | `version`?, `serverNexusHash`, `clientNexusHash` |
| AcceptedId | `hash`, `id` |
| Interface | `clrName`, `name`, `clrNamespace`, `hash`, `methods` Method[], `collections` Collection[], `versions` Version[] |
| Version | `version`, `hash`, `methodIds` int[] |
| Method | `name`, `id`, `hash`, `returnKind`, `returnType` TypeRef?, `params` Param[], `serializedCount`, `hasCancellation`, `pipeKind`, `pipeType` TypeRef?, `separatePool`, `isAuthorized`, `docs`? |
| Param | `name`, `kind`, `type` TypeRef?, `wireIndex`, `nullable` |
| Collection | `name`, `id`, `hash`, `kind`, `mode`, `item` TypeRef?, `isAuthorized`, `docs`? |
| TypeRef | `kind`, `prim` (0 when unset), `clr`?, `args` TypeRef[], `id`?, `ordinal` (0 unless TypeParam), `refNullable`, `jsNumber`, `nativeFormatters` NativeFormatter[] |
| NativeFormatter | `target`, `formatter` |
| TypeDecl (Object) | 0, `id`, `clrMetadataName`, `clrNamespace`, `name`, `containingTypes` string[], `docs`?, `isStruct`, `isRecord`, `isAbstract`, `typeParams` string[], `baseDeclId`?, `arrayLength`, `isRecursive`, `members` Member[] (15 elements) |
| TypeDecl (Union) | 1, the six common fields, `isInterface`, `typeParams` string[], `isRecursive`, `cases` UnionCase[] (11 elements) |
| TypeDecl (Enum) | 2, the six common fields, `underlying` (PrimKind), `isFlags`, `members` EnumMember[] (10 elements) |
| Member | `name`, `key`, `type` TypeRef, `refNullable`, `wasNonPublic`, `defaultLiteral` Literal?, `docs`? |
| Literal | `kind`, `value`? |
| UnionCase | `tag`, `caseDeclId`, `inheritanceDepth` |
| EnumMember | `name`, `value` (int64; reinterpret for UInt64 enums) |

The schema hash (`InspectorSchemaHash`) is 64-bit FNV-1a (offset basis `0xcbf29ce484222325`, prime `0x100000001b3`) over
these bytes; readers can recompute it.

### 5.3 Embedded schema (`NexNetEmitInspectorSchema`)

With the MSBuild property `<NexNetEmitInspectorSchema>true</NexNetEmitInspectorSchema>`, every generated `[Nexus]` class
(server and client) also implements `NexNet.Diagnostics.IInspectorSchemaSource`, independent of `Targets`:

```csharp
public interface IInspectorSchemaSource
{
    static abstract ReadOnlySpan<byte> InspectorSchema { get; }
    static abstract ulong InspectorSchemaHash { get; }
}

static ReadOnlySpan<byte> Schema<T>() where T : IInspectorSchemaSource => T.InspectorSchema;
```

- The schema of a nexus class is `IrBuilder.Merge([IrBuilder.Build(compilation, nexusClass, data)])`: one export (the
  pair seen from the client, named after the class) and every type both APIs reach. It does not include `[NexusObject]`
  types reached only at runtime (`CreateChannel<T>()`), the `[assembly: NexusSerializable<T>]` roots, the
  `[assembly: NexusTargetFormatter<T>]` mappings or `[assembly: NexusJsNumber]`; `targets` is 0.
- It is emitted in its own file, `<Namespace>.<Class>.InspectorSchema.g.cs`, as explicit interface implementations: the
  bytes are `=> new byte[] { ... }` (stored in the assembly's data section, no allocation) and the hash a literal computed
  at generation time. With the property off nothing runs and the generated output is byte-identical to a build without it.
- A nexus that the generator does not emit (a blocking validation or serialization error, NEXNET042) gets no schema.
- The output is deterministic (no timestamps or paths; independent of syntax tree order), so it is identical across builds.
- `build/NexNet.Generator.props` (imported by `buildTransitive/`) makes the property compiler visible when it is `true`. A project that references
  the generator as a `ProjectReference` analyzer does not import that file and must add
  `<CompilerVisibleProperty Include="NexNetEmitInspectorSchema" />` itself (as `NexNet.Generator.Tests` does).
- Tests: `src/NexNet.Generator.Tests/Ir/InspectorSchemaTests.cs`; the generated-code snapshot is
  `Ir/Snapshots/chat.ChatServerNexus.InspectorSchema.g.cs.txt`.
