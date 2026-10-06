# Implementation plan: one shape walk for code generation and hashing, warnings as errors

This plan continues the work in PR #78 (branch `worktree-messagepack-eval`, released as NexNet 0.17.0). It does two
things:

1. It fixes the failing CI run (https://github.com/Dtronix/NexNet/actions/runs/37538141512) and makes the **whole
   solution treat warnings as errors**.
2. It replaces the two independent type walks in the source generator, one in `TypeHasher` and one in
   `SerializationBuilder`, with **one shape model built once per producer** and read by both. The hash
   (`HashLock`) becomes a function of exactly what goes on the wire.

It is written for an implementer starting in a fresh context. Read it end to end before starting. The phases are
ordered so that the tree builds and the tests pass after each one.

## 0. How to use this document

**Where to work.** Worktree `Z:\Projects\NexNet\NexNet-master\.claude\worktrees\messagepack-eval`, branch
`worktree-messagepack-eval`, which backs draft PR #78 (https://github.com/Dtronix/NexNet/pull/78). Do not touch the
main checkout at `Z:\Projects\NexNet\NexNet-master`. Start from commit `745d8cb` or later.

**Ground rules.**

- Commit and push to `origin/worktree-messagepack-eval` after each phase. WIP commits are fine; the PR is
  squash-merged at the end. Never push to `master`, never merge, never force-push.
- The build and all three test suites must be green before each commit: `NexNet.Serialization.Tests`,
  `NexNet.Generator.Tests` and `NexNet.IntegrationTests`. After Phase 1 the build must also have **zero warnings**.
- **Do not run the fuzzer** (libFuzzer, `start-24h.ps1`, `--smoke` from the command line). The `FuzzSmokeTests` unit
  test is part of the normal suite and is the only fuzz-related execution allowed.
- Don't modify StreamStruct. Don't copy MessagePack-CSharp code.
- Don't build while tests or benchmarks are running; they share `bin`/`obj`.
- The worktree's command guard rejects complex one-liners (relative `..` paths, `cd` followed by writes, `awk`).
  Use the Edit/Write tools, or put logic in a Python script under the session scratchpad and run that.
- Never pass C# or regex text containing backslashes through an inline shell heredoc. The shell layer can strip one
  level of escaping (`\n` becomes a newline). Write scripts with the Write tool.
- Files use CRLF in the working tree and some have a UTF-8 BOM; git stores LF (`.gitattributes`). Preserve BOMs.
  Avoid `sed -i` from Git Bash: it rewrites CRLF files as LF.
- Check every new expected walk or `HashLock` value by eye before pasting it in.

**Commands** (run from `src/`):

```
dotnet build -c Release
dotnet test NexNet.Serialization.Tests -c Release --no-build
dotnet test NexNet.Generator.Tests     -c Release --no-build
dotnet test NexNet.IntegrationTests    -c Release --no-build
```

The integration suite takes about 3 minutes; run it in the background. The serialization suite takes about
40 seconds.

**Baseline before this plan:** 248 / 162 / 2570 tests, 13 build warnings (12 distinct, listed in Phase 1), Native
AOT publish with 0 IL warnings.

## 1. Decisions

Every row was confirmed with the user. The reasons are included so the implementer can apply them to cases the plan
does not list.

| # | Topic | Decision | Why |
|---|---|---|---|
| G1 | Warnings | **`TreatWarningsAsErrors` for every project in the solution**: libraries, generator, tests, fuzz, benchmarks, samples. | The user wants a strict build. The CI failure came from a warning that only the AOT publish step promoted to an error. |
| G2 | NuGet audit | **Strict.** NuGet vulnerability advisories (NU1901–NU1904) are errors too. No `WarningsNotAsErrors`. | The user's choice. A new advisory blocks builds until the dependency is updated. |
| G3 | One walk | A **shape model** (`TypeShape`) is built once per producer by a `ShapeBuilder`. `SerializationBuilder` and the hasher both read it. Producers stay separate pipeline nodes; formatter output is still deduplicated by `Merge`. | Sharing one walk across producers would need `CompilationProvider`, which re-runs on every keystroke. The value is a single definition of the serialized shape, not speed. |
| G4 | Hash scope | Parameters, **return types** (`ValueTask<T>`'s `T`) and **`[NexusCollection]` item types** are hashed structurally. The return *kind* (`void`, `ValueTask`, `ValueTask<T>`) and the collection kind are hashed too. | All of these go on the wire. Today a change to a returned DTO is not caught by `HashLock`. |
| G5 | Nullability | **Wire-only.** Reference-type annotations (`string?`, `Foo?`) are not hashed. `Nullable<T>` is. | A reference-type annotation does not change the bytes. `int` vs `int?` changes what a reader accepts. It also removes today's cache ambiguity, where `T` and `T?` share a cache key. |
| G6 | Names | **Structure only** for `[NexusObject]` types, unions and enums. Type names, member names and enum member names are not hashed. A reference back to a type already on the path is hashed by its position in the walk. | Names never travel on the wire; structure is all a peer sees. Renaming a DTO or an enum member keeps the `HashLock`. |
| G7 | Built-ins | Built-in, CLR and custom-formatter types keep their **.NET type identity**: full metadata name plus type arguments. | Conservative. `int` to `long` or `List<T>` to `T[]` may or may not stay readable; changing them changes the `HashLock`. |
| G8 | Walk format | A **shape listing**: the root reference, then one block per `[NexusObject]`/union type, each exactly once, in canonical walk order. Names in the listing are labels only. | The current tree with `[seen]` markers is an artifact of the stack walk order. |
| G9 | Test generator | **Remove `TypeHasherTestGenerator`** from the shipped generator. Tests call `ShapeBuilder` and `ShapeHasher` directly. | It is a `[Generator]` in the shipped analyzer and runs in every consumer build, only to support tests. |
| G10 | Gaps fixed by construction | Generic `[NexusObject]` types are hashed by their constructed members. `[NexusIgnore]` members are excluded. Attribute checks always verify the `NexNet.Serialization` namespace. | These are today's differences between the hasher and code generation (§2). |
| G11 | Refactor safety | After the code-generation port (Phase 3) the generated code is **byte-identical** to before. | Separates the refactor from behavior changes. Only hashes change, in Phase 4. |
| G12 | Version | Stays **0.17.0**; every `HashLock` changes again before release. | 0.17.0 is unreleased; better to break hashes once. |

## 2. Core concepts

Short explanations of the pieces this plan touches.

**Producers.** The generator has three kinds of producers, each its own incremental pipeline node: one per `[Nexus]`
class (`NexusDataExtractor.Extract`), one per non-generic `[NexusObject]` declared in the assembly
(`NexusGenerator.ExtractNexusObject`), and one for the assembly attributes (`[assembly: NexusSerializable<T>]`,
`[assembly: NexusFormatter<TFormatter, T>]`). Each producer returns equatable records. Symbols must never leave the
transform phase.

**Formatter specs and `Merge`.** Every producer returns `FormatterSpec` records (type key, class name, class code,
registration). `SerializationBuilder.Merge` deduplicates them by type key and `EmitSource` writes the single
`NexNet.Formatters.g.cs`. This plan does not change that mechanism.

**Today's two walks.** `TypeHasher` (`NexNet.Generator/TypeHasher.cs`) walks parameter types with an explicit stack
and FNV-1a, writing an indented walk string with `[seen]` markers on revisits. `SerializationBuilder.Require` walks
every type reachable from a producer's roots to emit formatters and report diagnostics. They apply different rules:

- The hasher checks for generic types (line ~323) before `[NexusObject]` (line ~373). A generic `[NexusObject]` is
  therefore hashed as name, arity and type arguments only, and a change to its members does not change the
  `HashLock`.
- The hasher ignores `[NexusIgnore]`. A member with both `[NexusKey]` and `[NexusIgnore]` is hashed but never
  serialized.
- The hasher matches `NexusKeyAttribute` by name without checking the namespace.
- The hasher includes reference-type nullability. Its cache uses `SymbolEqualityComparer.Default`, which ignores
  nullability, so `Foo` and `Foo?` share a cached result that depends on which was hashed first.
- Return types are hashed as a display string (`ComputeMethodHash`), collection item types as a display string
  (`ComputeCollectionHash`). `void` and `ValueTask` contribute nothing, so changing one to the other does not change
  the hash.

**What a peer sees.** On the wire a `[NexusObject]` is an array of `maxKey + 1` elements. Element *n* is the member
with key *n* and gaps are nil. A union is `[tag, value]`. An enum is its underlying integer. A class may be nil; a
struct may not. Built-in types have fixed encodings defined by their formatters. Names of types, members and enum
members never appear. This is what the new hash describes.

**Shape.** A `TypeShape` is the generator's description of how one type is serialized, built from symbols once. Kinds:
object (a `[NexusObject]` class or struct), union, enum, nullable value type, array, named (built-in, CLR, or
custom-formatter type, with its type arguments), and type parameter (only inside open generic definitions). Shapes
reference other shapes, so the shape graph can contain cycles (`Node.Next` is a `Node`).

**Canonical walk.** A pre-order depth-first walk of the shape graph from one root, in a fixed order: object members by
key, union cases by tag, type arguments by position. Every object and union shape gets an index when first reached
(the root object is `#0`). A later reference to an indexed shape writes `#i` instead of walking it again. Because the
order depends only on keys, tags and positions, the same structure always produces the same token stream, whatever the
declaration order, names, or cache state.

**Hash.** FNV-1a (`IncrementalHasher`, kept from `TypeHasher.cs`) over the token stream of the canonical walk. The
listing (G8) is a readable rendering of the same stream.

## 3. Phases

### Phase 1: Warnings as errors, solution-wide (fixes the CI run)

**The failure.** Run 37538141512 failed in "Native AOT publish (zero trim/AOT warnings)". The step passes
`-p:TreatWarningsAsErrors=true`, which applies to every project the publish builds, not just to ILCompiler. The
generator's existing CS1573 warning (`MethodEmitter.cs(143,108)`: parameter `argPrefix` has no `<param>` tag) became
an error. The local verification had published without that flag, so it reported 0 IL warnings and missed this.

**Warning inventory.** A full rebuild (`dotnet build NexNet.slnx -c Release --no-incremental`) reports these 12
distinct warnings on both Windows and the CI runner. `dotnet pack` of the four packages adds none.

| File | Warning | Fix |
|---|---|---|
| `NexNet.Generator/Emission/MethodEmitter.cs:143` | CS1573: no `<param>` for `argPrefix` | Add `/// <param name="argPrefix">Prefix of the local variables holding deserialized arguments (<c>__arg</c>).</param>` |
| `NexNet/Serialization/Formatters/CollectionFormatters.cs:453` | CS8600 | See the code below |
| `NexNet/Serialization/Formatters/CollectionFormatters.cs:456` | CS8601 | See the code below |
| `NexNet/Serialization/NexusSerializer.cs:7` | CS1735: class summary uses `<typeparamref name="T"/>` on a non-generic class | Reword: "serialize through the registered formatter for the value's type" (no `typeparamref`) |
| `NexNet/Internals/ReadingHelpers.cs:11` | CS1587: XML comment after `[MethodImpl]` | Move the `///` block above the attribute |
| `NexNet.IntegrationTests/NexusCollectionBroadcasterTests.cs:105,129,134,160,185,210,230` | CS8602 in `Has.All.Matches<TestBroadcastSession>(c => c.…)` (the predicate parameter is nullable) | Use `c => c!.…`, or a property pattern such as `c => c is { BufferWrites.Count: 1 }` |

The dictionary read loop in `CollectionFormatters.cs` becomes:

```csharp
for (var i = 0; i < count; i++)
{
    TKey? key = default;
    TValue? item = default;
    keyFormatter.Deserialize(ref reader, ref key);
    valueFormatter.Deserialize(ref reader, ref item);
    if (key is null)
        throw new NexusSerializationException("Dictionary keys cannot be nil.");
    dictionary[key] = item!; // a nil value is a valid value for a nullable TValue
}
```

Check the `Deserialize` signature (`ref T? value`) in `NexusFormatter.cs` and keep the existing behavior: nil keys
throw, nil values are stored.

**Turning it on.**

1. Create `src/Directory.Build.props`. It applies to every project under `src/`, including samples, benchmarks, the
   fuzz project and the generator.

   ```xml
   <Project>
     <!-- Every warning is an error, including NuGet audit advisories (NU1901-NU1904). Do not add NoWarn or
          WarningsNotAsErrors to get a build through; fix the cause. -->
     <PropertyGroup>
       <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
     </PropertyGroup>
   </Project>
   ```

   The existing `NoWarn` for the MessagePack-CSharp analyzer (`MsgPack003`–`MsgPack009`) in
   `NexNet.Serialization.Tests.csproj` stays. It disables rules for test-only interop types and predates this plan.

2. In `.github/workflows/dotnet.yml`, the AOT step no longer needs `-p:TreatWarningsAsErrors=true`; the props file
   sets it. Keep `-p:WarningsAsErrors=IL…` and the final `! grep -E "warning IL[0-9]+" aot.log`, because some ILCompiler
   warnings bypass `TreatWarningsAsErrors`. Keep `shell: bash` (pipefail).

3. Generated code is compiled under the same setting. Build output already has no generator warnings (Phase 4–5 of the
   previous plan added `#pragma warning disable CS8601` around argument readers). Any future warning in generated code
   now fails the build, which is intended.

**Verification.**

- `dotnet build NexNet.slnx -c Release --no-incremental` reports `0 Warning(s)`, `0 Error(s)`.
- Reproduce the exact CI publish command locally from a Developer PowerShell for VS, with
  `C:\Program Files (x86)\Microsoft Visual Studio\Installer` added to `PATH` (the dev shell adds `link.exe` but not
  `vswhere.exe`). Use `-r win-x64` and the same `-p:` flags as CI. `dotnet publish` has no `--no-incremental`, so run
  `dotnet clean` on the sample first. Expect exit code 0 and no `warning` lines.
- `dotnet pack` of NexNet, NexNet.Quic, NexNet.Asp and NexNet.Generator succeeds.
- All three suites green.
- After pushing, check the PR's CI run with `gh run list --branch worktree-messagepack-eval` and `gh run view`.

**Done when** CI on the branch is green.

### Phase 2: The shape model

**Goal.** A `ShapeBuilder` that turns type symbols into `TypeShape`s, applying exactly the rules the code generation
uses today. Nothing reads it yet.

**Files.** New `NexNet.Generator/Serialization/TypeShape.cs` (model) and `NexNet.Generator/Serialization/ShapeBuilder.cs`
(builder).

**Model.**

```csharp
namespace NexNet.Generator.Serialization;

/// <summary>How one type is serialized. Built once per producer; holds symbols, so never leaves the transform phase.</summary>
internal abstract class TypeShape
{
    protected TypeShape(ITypeSymbol type) => Type = type;

    /// <summary>The type, with its top-level nullable annotation removed.</summary>
    public ITypeSymbol Type { get; }
}

/// <summary>A [NexusObject] class or struct, written as an array of MaxKey + 1 elements.</summary>
internal sealed class ObjectShape : TypeShape
{
    public ObjectShape(INamedTypeSymbol type) : base(type) { }
    public bool IsValueType => Type.IsValueType;
    public bool IsAbstractOrInterface => Type.IsAbstract || Type.TypeKind == TypeKind.Interface;
    /// <summary>Keyed, non-ignored members in key order. Filled after construction (cycles).</summary>
    public List<MemberShape> Members { get; } = new();
    /// <summary>Problems found while reading members (NEXNET030 and similar); reported only by code generation.</summary>
    public List<SerializationDiagnostic> Problems { get; } = new();
}

internal sealed class MemberShape
{
    public ISymbol Symbol = null!;
    public string Name = null!;
    public int Key;
    public TypeShape Type = null!;
    public ITypeSymbol DeclaredType = null!; // with its annotation, for generated local declarations
    public bool IsField, CanGet, CanSet, IsInitOnly, IsRequired;
    public IFieldSymbol? BackingField;
    public string LocalName = null!;
}

/// <summary>A [NexusObject] abstract class or interface with [NexusUnion&lt;T&gt;(tag)] cases, written as [tag, value].</summary>
internal sealed class UnionShape : TypeShape
{
    public UnionShape(INamedTypeSymbol type) : base(type) { }
    /// <summary>Cases in declaration order (code generation keeps it); hashing sorts by tag.</summary>
    public List<(ushort Tag, TypeShape Case)> Cases { get; } = new();
}

internal sealed class EnumShape : TypeShape
{
    public EnumShape(INamedTypeSymbol type, SpecialType underlying, long[] values) : base(type)
    {
        Underlying = underlying;
        Values = values; // distinct, ascending
    }
    public SpecialType Underlying { get; }
    public long[] Values { get; }
}

internal sealed class NullableShape : TypeShape   // Nullable<T> only
{
    public NullableShape(ITypeSymbol type, TypeShape inner) : base(type) => Inner = inner;
    public TypeShape Inner { get; }
}

internal sealed class ArrayShape : TypeShape
{
    public ArrayShape(IArrayTypeSymbol type, TypeShape element) : base(type) => Element = element;
    public int Rank => ((IArrayTypeSymbol)Type).Rank;
    public TypeShape Element { get; }
}

internal enum NamedKind : byte { Special, Clr, BuiltInGeneric, UserFormatter, Unsupported }

/// <summary>Built-in, CLR, custom-formatter or unsupported type, identified by its .NET identity (G7).</summary>
internal sealed class NamedShape : TypeShape
{
    public NamedShape(ITypeSymbol type, NamedKind kind, string metadataName, TypeShape[] arguments) : base(type)
    {
        Kind = kind;
        MetadataName = metadataName;   // e.g. "System.Collections.Generic.List`1"
        Arguments = arguments;
    }
    public NamedKind Kind { get; }
    public string MetadataName { get; }
    public TypeShape[] Arguments { get; }
}

/// <summary>A type parameter of an open generic definition (code generation of generic formatter classes only).</summary>
internal sealed class TypeParameterShape : TypeShape
{
    public TypeParameterShape(ITypeParameterSymbol type) : base(type) { }
    public int Ordinal => ((ITypeParameterSymbol)Type).Ordinal;
}
```

`MemberShape` takes over the fields of today's private `SerializationBuilder.KeyedMember` class, so code generation
keeps everything it needs.

**Builder.** One instance per producer, created with the producer's `Compilation`.

```csharp
internal sealed class ShapeBuilder
{
    private readonly Compilation _compilation;
    private readonly Dictionary<ITypeSymbol, TypeShape> _cache = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ITypeSymbol, string> _userFormatters = new(SymbolEqualityComparer.Default);

    public ShapeBuilder(Compilation compilation)
    {
        _compilation = compilation;
        CollectUserFormatters(); // moved from SerializationBuilder.CollectAssemblyAttributes
    }

    public IReadOnlyDictionary<ITypeSymbol, string> UserFormatters => _userFormatters;

    public TypeShape Get(ITypeSymbol type)
    {
        var normalized = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        if (_cache.TryGetValue(normalized, out var shape))
            return shape;

        shape = Create(normalized);
        _cache[normalized] = shape;        // before populating: members may point back here
        Populate(shape);
        return shape;
    }
}
```

`SymbolEqualityComparer.Default` ignores nullable annotations at every level, so `List<string>` and `List<string?>` map
to the same entry. With G5 that is now correct: annotations are not part of the shape.

**Classification (`Create`).** Apply the checks in this order. It is the order `SerializationBuilder.Require` uses
today, with the user-formatter check first.

1. `ITypeParameterSymbol` → `TypeParameterShape`.
2. The type has a registered user formatter (`_userFormatters`) → `NamedShape(UserFormatter)`.
3. A special type (`bool`, integers, `float`, `double`, `decimal`, `char`, `string`, `DateTime`, `object`) →
   `NamedShape(Special)`.
4. An enum → `EnumShape`. The underlying type is `EnumUnderlyingType.SpecialType`. Values are the distinct constant
   values of its fields, converted with `Convert.ToInt64`, sorted ascending. Names are not stored.
5. `IArrayTypeSymbol` → `ArrayShape` (element filled in `Populate`).
6. `Nullable<T>` → `NullableShape`.
7. A named type with `[NexusObject]` from the `NexNet.Serialization` namespace (`SerializationBuilder.HasAttribute`) →
   `UnionShape` if it has at least one `[NexusUnion<T>]`, otherwise `ObjectShape`. The type may be generic and
   constructed (`Envelope<int>`) or an open definition (`Envelope<T>`).
8. A generic named type → `NamedShape(BuiltInGeneric)` when its definition is one of the built-ins
   `SerializationBuilder.TryRequireGenericBuiltIn` handles, otherwise `NamedShape(Clr)` if it is in the `System`
   namespace, otherwise `NamedShape(Unsupported)`.
9. Any other type in the `System` namespace (for example `Guid`, `Uri`, `CancellationToken`) → `NamedShape(Clr)`.
10. Anything else → `NamedShape(Unsupported)`. Code generation reports NEXNET028 for it, and the hasher treats it by
    name, as the old `[NotNexusObject]` rule did. This also covers `INexusDuplexPipe` and `INexusDuplexChannel<T>`,
    which code generation never requires. The hasher still sees `INexusDuplexChannel<T>` as a generic named type with
    `T` walked structurally, so a change to a channel's item type changes the hash.

`MetadataName` is `SerializationBuilder.MetadataFullName(type.OriginalDefinition)` for named types (namespace,
containing types joined with `+`, and the arity suffix). The display name is not used, because it depends on
`SymbolDisplayFormat`.

**Populating (`Populate`).** Fills the parts that reference other shapes, after the shape is in the cache:

- `ArrayShape`: `Element = Get(array.ElementType)`.
- `NullableShape`: `Inner = Get(T)`.
- `NamedShape`: `Arguments = typeArguments.Select(Get)`.
- `UnionShape`: for each `[NexusUnion<T>(tag)]` on `Type.OriginalDefinition`, in attribute order, add
  `(tag, Get(T))`. Copy today's tag conversion (`tagValue is ushort us ? us : Convert.ToUInt16(tagValue)`).
- `ObjectShape`: run today's `SerializationBuilder.GetKeyedMembers` logic on `Type`, which can be the constructed
  type, so member types are substituted. For each keyed member, `Type = Get(memberType)` and
  `DeclaredType = memberType`. Problems (NEXNET030) go into `Problems`. Keep these rules exactly: walk base types until
  `object`/`ValueType`; skip static and hidden members; the namespace-checked `[NexusKey]`; `[NexusIgnore]` excludes;
  unkeyed public serializable-shape members are reported; sort by key; `LocalName = "__m" + i`.

The member order and accessor fields must match today's output exactly; Phase 3 depends on byte-identical
generation (G11). Move the code, don't rewrite it.

Shapes are built recursively. Nesting depth in real contracts is small, and the cache-before-populate step makes
cycles terminate. Don't convert this to an explicit stack.

**Tests in this phase.** A few unit tests in a new `NexNet.Generator.Tests/ShapeBuilderTests.cs`, using
`CSharpGeneratorRunner.CreateCompilation`:

- a `[NexusObject]` with keys 0, 1 and 3 gives three members in key order;
- `[NexusIgnore]` on a keyed member removes it;
- `Envelope<int>` yields an `ObjectShape` whose member type is `int`, not `T`;
- a self-referencing `Node` returns the same `ObjectShape` instance for `Node.Next`;
- `string` and `string?` members give the same `NamedShape` instance.

**Done when** the new tests pass and the existing suites are unchanged.

### Phase 3: Code generation reads shapes

**Goal.** `SerializationBuilder` gets all type information from a `ShapeBuilder`. The generated code is
byte-identical (G11).

**Before you change anything,** capture the generated sources of the current tree as the baseline. Build each of
`NexNet.IntegrationTests`, `NexNet.Serialization.Tests` and `NexNet.Fuzz` with:

```
dotnet build <project> -c Release --no-incremental -p:BuildProjectReferences=false
    -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=<scratch>/gen-before/<project>
```

Keep only the files under `NexNet.Generator.NexusGenerator/`. Expect 17 files: 14 nexus files and 3 formatter files (Serialization.Tests has formatters only).

**Changes.**

1. The `SerializationBuilder` constructor takes a `ShapeBuilder` (and keeps `Compilation` for accessibility checks
   and conversions). `CollectAssemblyAttributes` moves to `ShapeBuilder`; `AddAssemblyDeclaredRoots` iterates
   `shapes.UserFormatters`.
2. `Require(ITypeSymbol, …)` becomes a dispatch on `shapes.Get(type)`:
   - `TypeParameterShape`: return (resolved at runtime).
   - `NamedShape(UserFormatter)`: registration with the user formatter.
   - `NamedShape(Special)`, and the built-in leaf types (`BuiltInLeafTypes`): return.
   - `EnumShape`: `EnumFormatter` registration.
   - `ArrayShape`: today's array branch (NEXNET028 for rank above 1, `byte[]` is bin, primitive arrays, otherwise
     `ArrayFormatter` plus `Require(element)`).
   - `NullableShape` and `NamedShape(BuiltInGeneric)`: today's `TryRequireGenericBuiltIn` cases, driven by the
     definition's metadata name and the argument shapes.
   - `ObjectShape` / `UnionShape`: `RequireNexusObject`.
   - `NamedShape(Clr)` that is not a built-in leaf, and `NamedShape(Unsupported)`: NEXNET028.

   Keep `_visited` keyed by the shape instance (reference equality) instead of the symbol.
3. `RequireNexusObject`, `EmitObject`, `EmitUnion` and the accessor emitters read `ObjectShape.Members` /
   `UnionShape.Cases` in place of `GetKeyedMembers` / `TryGetUnionCases`. The formatter class is emitted from the
   **definition's** shape (`shapes.Get(type.OriginalDefinition)`), as today. The closure walk uses the constructed
   shape. `ObjectShape.Problems` are reported when the class is emitted, where `GetKeyedMembers` problems are
   reported today.
4. Delete `GetKeyedMembers`, `KeyedMember`, `TryGetUnionCases` and `CollectAssemblyAttributes` from
   `SerializationBuilder`.
5. Each producer creates one `ShapeBuilder` and passes it to its `SerializationBuilder`:
   - `NexusDataExtractor.Extract` (also used by the hasher in Phase 4);
   - `NexusGenerator.ExtractNexusObject`;
   - the assembly-declarations `Select` in `NexusGenerator.Initialize`.

**Verification.** Rebuild, emit the generated sources the same way into `gen-before`'s sibling `gen-after`, and
compare every file byte for byte (a small Python script under the scratchpad). Any difference is a bug in the port,
not a baseline to update. The generator tests `TypeReachedFromSeveralProducersHasOneFormatter`,
`GeneratedSourcesAreByteIdenticalAcrossRuns` and `UnrelatedEditDoesNotRegenerateFormatters` must still pass.

**Done when** the comparison shows no differences and all suites are green.

### Phase 4: Hashing reads shapes

**Goal.** `TypeHasher` is replaced by `ShapeHasher`, which hashes the canonical walk of a shape. Parameters, return
types and collection items are hashed structurally (G4–G7, G10).

**Token stream.** The walk emits tokens into an `IncrementalHasher` (FNV-1a, moved from `TypeHasher.cs` into
`ShapeHasher.cs`). A token is a tag byte followed by its payload:

| Shape | Tokens |
|---|---|
| Object, first visit | `Object`, `IsValueType ? 1 : 0`, member count; then for each member in key order: `Key`, key value, the member type's tokens |
| Union, first visit | `Union`, case count; then for each case **sorted by tag**: `Tag`, tag value, the case type's tokens |
| Object or union, revisit | `Ref`, its walk index |
| Enum | `Enum`, `(int)Underlying`, value count, each value (as `long`) |
| Nullable value type | `Nullable`, the inner type's tokens |
| Array | `Array`, rank, the element type's tokens |
| Named | `Named`, `(byte)Kind`, `MetadataName` (string), argument count, each argument's tokens |
| Type parameter | `TypeParam`, ordinal |

Names of `[NexusObject]` types, union types, members and enum members are never written (G6). `MetadataName` is
written only for named types (G7). Including the member count before the members, and the argument count before the
arguments, keeps different structures from producing the same token sequence.

**Algorithm.**

```csharp
internal static class ShapeHasher
{
    private enum Token : byte { Object = 1, Union, Ref, Key, Tag, Enum, Nullable, Array, Named, TypeParam }

    /// <summary>Structural hash of a type: its canonical walk (see the class remarks).</summary>
    public static int Hash(TypeShape root) => new Walker(listing: null).Run(root);

    /// <summary>The hash plus the readable listing of the same walk (tests).</summary>
    public static (int Hash, string Listing) HashWithListing(TypeShape root)
    {
        var listing = new ListingWriter();
        var hash = new Walker(listing).Run(root);
        return (hash, listing.ToString());
    }

    private sealed class Walker
    {
        private IncrementalHasher _hasher = new();
        private readonly Dictionary<TypeShape, int> _index = new(ReferenceEqualityComparer.Instance);
        private readonly ListingWriter? _listing;

        public Walker(ListingWriter? listing) => _listing = listing;

        public int Run(TypeShape root)
        {
            _listing?.Root(Visit(root));
            return _hasher.ToHashCode();
        }

        // Writes the shape's tokens and returns its listing reference text.
        private string Visit(TypeShape shape)
        {
            switch (shape)
            {
                case ObjectShape or UnionShape when _index.TryGetValue(shape, out var seen):
                    Add(Token.Ref); _hasher.Add(seen);
                    return "#" + seen;

                case ObjectShape obj:
                {
                    var i = _index.Count;
                    _index.Add(obj, i);
                    var block = _listing?.Begin(i, obj.IsValueType ? "struct" : "object", obj.Type.Name);
                    Add(Token.Object); _hasher.Add((byte)(obj.IsValueType ? 1 : 0)); _hasher.Add(obj.Members.Count);
                    foreach (var m in obj.Members)
                    {
                        Add(Token.Key); _hasher.Add(m.Key);
                        block?.Line(m.Key + ": " + Visit(m.Type));
                    }
                    return "#" + i;
                }

                case UnionShape union:
                {
                    var i = _index.Count;
                    _index.Add(union, i);
                    var block = _listing?.Begin(i, "union", union.Type.Name);
                    Add(Token.Union); _hasher.Add(union.Cases.Count);
                    foreach (var (tag, c) in union.Cases.OrderBy(c => c.Tag))
                    {
                        Add(Token.Tag); _hasher.Add(tag);
                        block?.Line("tag " + tag + ": " + Visit(c));
                    }
                    return "#" + i;
                }

                case EnumShape e:
                    Add(Token.Enum); _hasher.Add((int)e.Underlying); _hasher.Add(e.Values.Length);
                    foreach (var v in e.Values) _hasher.Add(v);
                    return "enum " + SpecialName(e.Underlying) + " {" + string.Join(", ", e.Values) + "}";

                case NullableShape n:
                    Add(Token.Nullable);
                    return Visit(n.Inner) + "?";

                case ArrayShape a:
                    Add(Token.Array); _hasher.Add(a.Rank);
                    return Visit(a.Element) + "[" + new string(',', a.Rank - 1) + "]";

                case NamedShape named:
                {
                    Add(Token.Named); _hasher.Add((byte)named.Kind); _hasher.AddString(named.MetadataName);
                    _hasher.Add(named.Arguments.Length);
                    var args = named.Arguments.Select(Visit).ToArray();
                    return DisplayName(named) + (args.Length == 0 ? "" : "<" + string.Join(", ", args) + ">");
                }

                case TypeParameterShape p:
                    Add(Token.TypeParam); _hasher.Add(p.Ordinal);
                    return "!" + p.Ordinal;
            }

            throw new InvalidOperationException("Unknown shape " + shape.GetType().Name);
        }

        private void Add(Token token) => _hasher.Add((byte)token);
    }
}
```

`IncrementalHasher` is a `ref struct` today. Make it a plain `struct` (or keep the state in the `Walker` as a `uint`)
so it can live in a class field. `ReferenceEqualityComparer` does not exist in netstandard2.0; add a small internal
`ReferenceEqualityComparer<T>` using `RuntimeHelpers.GetHashCode`. `DisplayName` returns the simple name without the
arity suffix (`List`, `Guid`, `Int32`). It is used only for the listing; the hash uses `MetadataName`.

**Why this is deterministic and independent of the walk history.** The walk order is fixed by keys, tags and
argument positions. The index of a shape is the order in which this one root's walk first reaches it, so it depends
only on the structure reachable from the root. Each root is walked from scratch, so nothing cached for another root
can leak in, unlike today's `[seen]` set. Two types with the same structure and different names produce the same
tokens. A cyclic structure produces a finite stream, because the second visit writes `Ref`.

**Caching.** Hashing walks in-memory shapes only, and contracts are small, so per-root caching is optional. If
profiling shows a need, cache `Hash(root)` in a `Dictionary<TypeShape, int>` with reference equality, scoped to the
producer.

**Listing format (G8).** `ListingWriter` collects one block per indexed object or union and writes them in index
order after the walk, preceded by a root line:

```
root: List<#0>
#0 object Order
  0: Int32
  1: #1
  2: List<#2>
  4: enum Int32 {0, 1, 2}
#1 object Customer
  0: String
  1: #1
  2: Int32?
#2 struct Line
  0: String
  1: Int32
  2: Double
```

Here `Customer.1` is declared `Customer?`: it renders as `#1`, because a reference to an already indexed type is
written as its index and reference-type `?` is not part of the shape. `Customer.2` is a value-type `int?` and
renders as `Int32?`. Type names after `object`/`struct`/`union` are labels for readers; they are not hashed. Members are listed by key, so a gap in the keys is visible (`2:` then
`4:`).

**Extractor integration.** In `NexusDataExtractor`:

1. `Extract` creates one `ShapeBuilder` and passes it to both the `SerializationBuilder` (Phase 3) and every
   `Extract*Data` method that took the `TypeHasher` (the parameter type changes; the call chain is
   `ExtractInterfaceData` → `ExtractInterfaceDataShallow` → `ExtractMethodData` → `ExtractParameterData`, plus
   `ExtractCollectionData`).
2. `ExtractParameterData`: `NexusHashCode: ShapeHasher.Hash(shapes.Get(symbol.Type))`. This keeps hashing
   `CancellationToken`, `INexusDuplexPipe` and `INexusDuplexChannel<T>` parameters (as named types with their
   arguments), as today.
3. `ComputeMethodHash` takes the `ShapeBuilder` and replaces the return-type string with a return kind and, for
   `ValueTask<T>`, the structural hash of `T`:

   ```csharp
   private enum ReturnKind : byte { Void = 0, ValueTask = 1, ValueTaskOfT = 2 }

   var returnSymbol = symbol.ReturnType as INamedTypeSymbol;
   if (returnSymbol is { Arity: 1 } && returnSymbol.ConstructedFrom.MetadataName == "ValueTask`1")
   {
       hash.Add((byte)ReturnKind.ValueTaskOfT);
       hash.Add(ShapeHasher.Hash(shapes.Get(returnSymbol.TypeArguments[0])));
   }
   else
   {
       hash.Add((byte)(returnSymbol?.MetadataName == "ValueTask" ? ReturnKind.ValueTask : ReturnKind.Void));
   }
   ```

   The method ID, the method name (when there is no ID) and the parameter hashes are added as today.
4. `ComputeCollectionHash` takes the item type symbol and the collection kind. It adds `(byte)collectionType`
   (`CollectionTypeValue`) and `ShapeHasher.Hash(shapes.Get(itemType))` in place of the item-type string. The ID/name
   part stays as it is.
5. Delete `TypeHasher.cs` (`TypeHasher`, `TypeHashResult`). `IncrementalHasher` moves to `ShapeHasher.cs` or its own
   file.

The interface hash (`ComputeInterfaceHash`) and the version tables are unchanged. They combine method and collection
hashes, which now carry the new structure.

**Done when** the solution builds. Generator tests that assert hashes or walks fail until Phase 5 re-baselines them.
Phases 4 and 5 may share one commit, like Phases 4–5 of the previous plan.

### Phase 5: Tests

**Remove the test generator (G9).**

- Delete `NexNet.Generator/TypeHasherTestGenerator.cs`.
- Delete `CSharpGeneratorRunner.RunTypeHasherGenerator`.

**New test helper.** In `NexNet.Generator.Tests/TypeHasherTests.cs` (keep the file and class names), replace `Run`
and the `GenerateStructureHashAttribute` constant with:

```csharp
private static (int Hash, string Listing) Walk(string source, string typeName)
{
    var compilation = CSharpGeneratorRunner.CreateCompilation(source);
    var type = compilation.GetTypeByMetadataName(typeName)
               ?? throw new AssertionException($"Type '{typeName}' not found.");
    return ShapeHasher.HashWithListing(new ShapeBuilder(compilation).Get(type));
}

private static void AssertWalk(string source, string typeName, string expected)
    => Assert.That(Walk(source, typeName).Listing, Is.EqualTo(expected.ReplaceLineEndings("\n")));

private static int HashOf(string source, string typeName = "Message")
    => Walk("using NexNet.Serialization;\n" + source, typeName).Hash;
```

The listing writer uses `\n` line endings. Test sources must not contain the `[GenerateStructureHash(...)]` attribute
anymore.

**Convert the walk tests.** There are 59 tests with `[GenerateStructureHash(ExpectedWalk = "...")]`. Each becomes a
call to `AssertWalk(source, "<TypeName>", expected)`. Write a scratchpad Python script that moves the attribute's type
name into the call and removes the attribute line; check its output by eye. Then re-baseline:

- Run the generator tests with a TRX logger, read the actual listing from each failure message, and paste it in
  (adapt the scratchpad script `apply_walks.py` from the previous plan).
- **Check every listing by eye.** It must describe the type the test intends. Watch these points:
  - members appear by key, with gaps visible;
  - revisits appear as `#i`;
  - reference-type `?` is gone and `Int32?` stays;
  - enums show values only;
  - union cases are sorted by tag even when the attributes are not.
- Tests whose intent was about names or `[seen]` placement now test structure. For example,
  `CyclicReference_SeenMultipleTimes` asserts the `#i` references.
- `SimpleType_WithSpecialTypes` (a type without `[NexusObject]`) now lists `root: SimpleMessage`, a named
  (unsupported) type with no blocks.

**New hash-rule tests** (all with `HashOf`, comparing two compilations):

| Test | Expectation |
|---|---|
| `RenamingType_KeepsHash` | `Message`→`Envelope` with the same keyed members: equal (pass the type name) |
| `RenamingMember_KeepsHash` | equal |
| `RenamingEnumMember_KeepsHash` | `{ A, B }` → `{ X, Y }`: equal |
| `ChangingEnumValue_ChangesHash` | `B = 1` → `B = 2`: different |
| `ChangingEnumUnderlyingType_ChangesHash` | `: int` → `: long`: different |
| `ReferenceNullability_KeepsHash` | `string` → `string?`, `Inner` → `Inner?`: equal |
| `ValueNullability_ChangesHash` | `int` → `int?`: different |
| `ClassToStruct_ChangesHash` | different |
| `StructurallyIdenticalTypes_HashEqual` | two different names, same keys and member types: equal |
| `GenericNexusObjectMemberChange_ChangesHash` | `Envelope<T> { [NexusKey(0)] T Value; }` vs. adding `[NexusKey(1)] int Extra`, hashed through `Envelope<int>`: different (the G10 gap) |
| `NexusIgnoredKeyedMember_KeepsHash` | adding `[NexusKey(1)][NexusIgnore] int X`: equal |
| `CycleShape_ChangesHash` | `Node { int; Node? }` vs `Node { int; int }`: different |
| `ListToArray_ChangesHash` | `List<int>` → `int[]`: different (G7) |

Keep the existing key-rule tests: reorder with the same keys, change, add or remove a key, a key gap, and the nested
and union-tag changes.

**`ShapeHasher` used by `GeneratorSerializationTests.NexusKeyOrderAffectsHash`.** That test uses
`new TypeHasher(generateWalkString: true)`. Rewrite it with `ShapeBuilder` + `ShapeHasher.HashWithListing`, and assert
`Does.Contain("0: ")` instead of `[Key:1]`.

**`VersioningTests`.** Every `HashLock` changes. Re-baseline with the method from the previous plan: run once, take
the computed value from the NEXNET019 message, check it, paste it. Keep the pairing that makes the "fails" tests
meaningful: `HashLockFailsOnMemberChange` uses the same lock as `NexusObjects`, and `HashLockFailsOnNextedMemberChange`
the same lock as `NexusObject_NestedCreation`, with sources identical except for the changed member. Add:

- `HashLockFailsOnReturnTypeMemberChange`: a method returning `ValueTask<Result>`; lock from the unchanged version,
  then change a `Result` member type and expect NEXNET019.
- `HashLockFailsOnCollectionItemMemberChange`: the same for a `[NexusCollection]` `INexusList<Item>`.
- `HashLockFailsOnVoidToValueTask`: `void Update()` → `ValueTask Update()`.
- `HashLockKeepsOnTypeRename`: rename a parameter DTO and keep the lock; expect no NEXNET019.

`HashLockIsDeterministicAcrossMultipleRuns` stays as it is.

**Integration tests.** `NexNet.IntegrationTests/TestInterfaces/VersionedTestsInterfaces.cs` pins three `HashLock`
values (v1.0, v1.1, v1.2). Re-baseline them the same way. The handshake tests compare hashes computed by the same generator on
both sides, so they need no other change.

**Done when** all three suites are green. Record the new counts. Generator tests rise by the new tests and
`ShapeBuilderTests`, and fall by nothing.

### Phase 6: Documentation and PR

1. `docs/articles/versioning.md`, section "How Serialized Types Are Hashed". Rewrite to match G4–G7:
   - parameter types, the type inside `ValueTask<T>`, and collection item types are hashed structurally;
   - so are the return kind (`void`, `ValueTask`, `ValueTask<T>`) and the collection kind;
   - for `[NexusObject]` types, keys, member types, class vs struct and union tags are hashed;
   - names of types, members and enum members, and reference-type nullability, are not;
   - enums hash their underlying type and values;
   - built-in, CLR and custom-formatter types hash their .NET identity.

   State the consequence plainly: renaming a DTO, a member or an enum member keeps the `HashLock`; changing keys,
   member types, enum values or a returned type's structure changes it. Remove the earlier sentence that types are
   hashed for parameters only.
2. `docs/articles/serialization.md`, "Evolving Types": point at the new rules; return types are now covered too.
3. `llm-usage.md`: update the versioning/hash notes with the same rules.
4. `llm-dev.md`:
   - replace the `TypeHasher.cs` entry with `Serialization/TypeShape.cs`, `ShapeBuilder.cs` and `ShapeHasher.cs`;
   - describe the single shape walk and the canonical hash walk;
   - remove `TypeHasherTestGenerator`;
   - mention `src/Directory.Build.props` (warnings as errors, including NuGet audit) under build instructions.
5. Update the PR #78 description with `gh pr edit 78 --body-file <file>`. Read the current body first
   (`gh pr view 78 --json body`) and edit it, don't rewrite it:
   - "What's in it": add the shape model with one walk per producer for code generation and hashing, the new hash
     rules, warnings as errors solution-wide, and the removal of the test generator from the shipped analyzer.
   - "Breaking changes": `HashLock` values change again; the hash now covers return and collection item types; names
     are no longer hashed.
   - "Tests": new counts.
   - Leave it a draft.

### Phase 7: Final verification

- `dotnet build NexNet.slnx -c Release --no-incremental`: 0 warnings, 0 errors.
- All three suites green; record the counts.
- The CI publish command reproduced locally (Phase 1 procedure): exit 0, no `warning` lines, so no `warning IL` lines.
- `dotnet pack` for the four packages succeeds.
- `grep -rn "TypeHasher\b\|TypeHasherTestGenerator\|GenerateStructureHash\|NotNexusObject\|\[seen\]" src docs *.md`
  (excluding `bin`/`obj`) finds nothing.
- Push, then check that the PR's CI run is green (`gh run list --branch worktree-messagepack-eval`, `gh run view`).
- Delete this file (`impl-plan-gen.md`) in a final commit and push.

## 4. Risks and gotchas

- **Byte-identical generation (Phase 3).** Member order, local names (`__m0`…), accessor emission and diagnostic order
  all come from the member list. Moving `GetKeyedMembers` must preserve its loop order exactly: walk base types
  outward, skip names already seen, then sort by key. A stable sort matters for duplicate keys, which report
  NEXNET029. `List.Sort` is not stable; today's code uses it, so keep the same call rather than switching to LINQ
  `OrderBy` (that would change the order of duplicates).
- **Definition vs constructed shapes.** Generic formatter classes are emitted from the open definition (members typed
  `T`); registrations and hashing use constructed types. Both shapes come from the same builder and cache, keyed by
  different symbols. Don't hash a definition shape for a parameter.
- **Diagnostics stay with code generation.** `ShapeBuilder` records problems on shapes. Only `SerializationBuilder`
  reports them, so hashing a type never adds diagnostics, and a type reached by several producers reports the same
  diagnostics it does today.
- **Symbols must not leak.** Shapes hold symbols. `NexusGenerationData`, `FormatterSpec` and the hash integers are the
  only things that leave a transform. Never put a shape into an equatable record.
- **`IncrementalHasher` as a field.** It is a `ref struct` today and can't be a class field. Change it to a `struct`,
  and check every existing use (`NexusDataExtractor` computes method, collection and interface hashes with it).
- **Unchanged parts of the method hash.** Method IDs and the method-name fallback are protocol identity, not
  structure. Keep them exactly as they are.
- **Warnings as errors and future tool updates.** A new SDK, analyzer or NuGet advisory can fail the build without a
  code change. That is the intended behavior (G1, G2). Fix the cause; don't add `NoWarn`.
- **Generated code under warnings as errors.** A warning in generated nexus or formatter code now fails every test
  project build. If a new generator change introduces one, fix the emitted code; a `#pragma` is acceptable only with
  a comment explaining why, as for CS8601 in argument readers.
- **Hash re-baselining** is where mistakes hide. Check every listing and `HashLock` by eye.
- **No fuzz runs.** Use the `FuzzSmokeTests` unit test.

## 5. Reference: state at the start of this plan

- Head `745d8cb`; NexNet 0.17.0; PR #78 is a draft.
- 248 / 162 / 2570 tests passing.
- CI run 37538141512 fails at the Native AOT publish step with CS1573 promoted to an error.
- Generated sources for the test projects: 17 NexNet files (14 nexus files, 3 `NexNet.Formatters.g.cs`).
- `TypeHasher` and `SerializationBuilder` walk types independently; `TypeHasherTestGenerator` ships in the analyzer.
