using NexNet.Generator.Ir;
using NexNet.Generator.Models;
using TUnit.Assertions.Enums;

namespace NexNet.Generator.Tests.Ir;

public class IrBuilderTests
{
    private static IrRun? _zoo;

    private static IrRun Zoo => _zoo ??= IrTestRunner.Run(IrFixtures.ZooTypes, IrFixtures.ZooNexus);

    private static ObjectDeclIr Object(NexusIr ir, string id) => (ObjectDeclIr)Decl(ir, id);

    private static TypeDeclIr Decl(NexusIr ir, string id)
        => ir.Types.FirstOrDefault(t => t.Id == id) ?? throw new InvalidOperationException($"Declaration '{id}' not found.");

    private static MemberIr Member(ObjectDeclIr decl, string name)
        => decl.Members.FirstOrDefault(m => m.Name == name) ?? throw new InvalidOperationException($"Member '{name}' not found.");

    private static ExportIr Export(NexusIr ir, string name)
        => ir.Exports.FirstOrDefault(e => e.Name == name) ?? throw new InvalidOperationException($"Export '{name}' not found.");

    private static MethodIr Method(InterfaceIr api, string name)
        => api.Methods.FirstOrDefault(m => m.Name == name) ?? throw new InvalidOperationException($"Method '{name}' not found.");

    private static Serialization.EquatableArray<T> Array<T>(IEnumerable<T> items)
        where T : IEquatable<T>
        => new(items.ToArray());

    // ------------------------------------------------------------------ snapshots

    [Test]
    public Task BasicTestNexusSnapshot() => IrSnapshots.Verify("basic", IrJson.Write(IrTestRunner.Run(IrFixtures.Basic).Ir));

    [Test]
    public Task VersionedTestNexusSnapshot() => IrSnapshots.Verify("versioned", IrJson.Write(IrTestRunner.Run(IrFixtures.Versioned).Ir));

    [Test]
    public Task ZooSnapshot() => IrSnapshots.Verify("zoo", IrJson.Write(Zoo.Ir));

    // ------------------------------------------------------------------ determinism and JSON

    [Test]
    public async Task BuildIsDeterministicAndIndependentOfSourceOrder()
    {
        var first = IrTestRunner.Run(IrFixtures.ZooTypes, IrFixtures.ZooNexus, IrFixtures.Versioned);
        var second = IrTestRunner.Run(IrFixtures.Versioned, IrFixtures.ZooNexus, IrFixtures.ZooTypes);
        var third = IrTestRunner.Run(IrFixtures.ZooTypes, IrFixtures.ZooNexus, IrFixtures.Versioned);

        await Assert.That(IrJson.Write(second.Ir)).IsEqualTo(IrJson.Write(first.Ir));
        await Assert.That(IrJson.Write(third.Ir)).IsEqualTo(IrJson.Write(first.Ir));
        await Assert.That(third.Ir).IsEqualTo(first.Ir);
    }

    [Test]
    public async Task IrIsEquatable()
    {
        var a = IrTestRunner.Run(IrFixtures.ZooTypes, IrFixtures.ZooNexus).Ir;
        var b = IrTestRunner.Run(IrFixtures.ZooTypes, IrFixtures.ZooNexus).Ir;
        await Assert.That(b).IsEqualTo(a);
        await Assert.That(b.GetHashCode()).IsEqualTo(a.GetHashCode());
    }

    [Test]
    public async Task MergeIgnoresFragmentOrder()
    {
        var run = IrTestRunner.Run(IrFixtures.Basic, IrFixtures.Versioned);
        var merged = IrBuilder.Merge(new[]
        {
            new IrFragment(Array(run.Ir.Exports.Reverse()), run.Ir.Types),
            new IrFragment(run.Ir.Exports, Array(run.Ir.Types.Reverse())),
        });
        await Assert.That(merged).IsEqualTo(run.Ir);
    }

    [Test]
    public async Task JsonRoundTrips()
    {
        foreach (var json in new[]
                 {
                     IrJson.Write(Zoo.Ir),
                     IrJson.Write(IrTestRunner.Run(IrFixtures.Basic).Ir),
                     IrJson.Write(IrTestRunner.Run(IrFixtures.Versioned).Ir),
                 })
        {
            var read = IrJson.Read(json);
            await Assert.That(IrJson.Write(read)).IsEqualTo(json);
        }
    }

    [Test]
    public async Task JsonReadRestoresEverythingButLocations()
    {
        static InterfaceIr WithoutLocations(InterfaceIr api)
            => api with { Methods = Array(api.Methods.Select(m => m with { Location = null })) };

        var read = IrJson.Read(IrJson.Write(Zoo.Ir));
        var expected = Zoo.Ir with
        {
            Exports = Array(Zoo.Ir.Exports.Select(e => e with
            {
                Location = null,
                ServerApi = WithoutLocations(e.ServerApi),
                ClientApi = WithoutLocations(e.ClientApi),
            })),
        };
        await Assert.That(read).IsEqualTo(expected);
    }

    [Test]
    public async Task JsonIsSortedAndHasNoPathsOrCarriageReturns()
    {
        var json = IrJson.Write(Zoo.Ir);
        await Assert.That(json).DoesNotContain("\r");
        await Assert.That(json).DoesNotContain(".cs\"");
        await Assert.That(json).StartsWith("{\n  \"exports\": [");
        await Assert.That(json).EndsWith("}\n");
    }

    [Test]
    public async Task JsonReadRejectsInvalidInput()
    {
        await Assert.That(() => IrJson.Read("{ \"irVersion\": 2 }")).ThrowsExactly<FormatException>();
        await Assert.That(() => IrJson.Read("{ \"irVersion\": 1, \"exports\": [ { \"name\": 1 } ] }")).ThrowsExactly<FormatException>();
        await Assert.That(() => IrJson.Read("{ \"irVersion\": 1 } trailing")).ThrowsExactly<FormatException>();
    }

    // ------------------------------------------------------------------ hashes and handshake

    [Test]
    public async Task HashesEqualTheGenerationData()
    {
        foreach (var run in new[] { IrTestRunner.Run(IrFixtures.Basic), IrTestRunner.Run(IrFixtures.Versioned), Zoo })
        {
            await Assert.That(run.Ir.Exports.Length).IsEqualTo(run.Data.Length);
            foreach (var data in run.Data)
            {
                var export = run.Ir.Exports.Single(e => e.Name == data.TypeName && e.ClrNamespace == data.Namespace);
                var server = data.NexusAttribute.IsServer ? data.NexusInterface : data.ProxyInterface;
                var client = data.NexusAttribute.IsServer ? data.ProxyInterface : data.NexusInterface;

                await AssertApi(export.ServerApi, server);
                await AssertApi(export.ClientApi, client);
                await Assert.That(export.Handshake.ClientNexusHash).IsEqualTo(client.NexusHash);
                await Assert.That(export.ClientAcceptedIds.Select(a => a.Hash).Distinct().ToArray()).IsEquivalentTo(new[] { client.NexusHash }, CollectionOrdering.Matching);
                await Assert.That(export.ClientAcceptedIds.Select(a => a.Id).ToArray())
                    .IsEquivalentTo(client.AllMethods.Select(m => m.Id).Concat(client.AllCollections.Select(c => c.Id)));
            }
        }
    }

    private static async Task AssertApi(InterfaceIr api, InvocationInterfaceData data)
    {
        await Assert.That(api.Hash).IsEqualTo(data.NexusHash).Because(api.ClrName);
        await Assert.That(api.Methods.Select(m => (m.Id, m.Name, m.Hash)).ToArray())
            .IsEquivalentTo(data.AllMethods.Select(m => (m.Id, m.Name, m.NexusHash))).Because(api.ClrName);
        await Assert.That(api.Collections.Select(c => (c.Id, c.Name, c.Hash)).ToArray())
            .IsEquivalentTo(data.AllCollections.Select(c => (c.Id, c.Name, c.NexusHash))).Because(api.ClrName);
        await Assert.That(api.Versions.Select(v => (v.Version, v.Hash)).ToArray())
            .IsEquivalentTo(data.Versions.Select(v => (v.VersionAttribute?.Version ?? "", v.NexusHash)), CollectionOrdering.Matching).Because(api.ClrName);
    }

    [Test]
    public async Task HashesEqualTheEmittedCSharp()
    {
        foreach (var source in new[] { IrFixtures.Basic, IrFixtures.Versioned })
        {
            var run = IrTestRunner.Run(source);
            var generated = IrTestRunner.RunCSharpGenerator(run.Compilation);
            foreach (var data in run.Data)
            {
                var export = run.Ir.Exports.Single(e => e.Name == data.TypeName);
                var hashes = IrTestRunner.EmittedMethodHashes(generated[data.FullTypeName + ".Nexus.g.cs"]);

                // The nexus's own hash, then its proxy's.
                var expected = data.NexusAttribute.IsServer
                    ? new[] { export.ServerApi.Hash, export.ClientApi.Hash }
                    : new[] { export.ClientApi.Hash, export.ServerApi.Hash };
                await Assert.That(hashes).IsEquivalentTo(expected, CollectionOrdering.Matching).Because(data.TypeName);
            }
        }
    }

    [Test]
    public async Task HandshakeUsesTheLastServerVersion()
    {
        var ir = IrTestRunner.Run(IrFixtures.Versioned).Ir;

        var v2 = Export(ir, "VersionedClientNexusV2");
        await Assert.That(v2.Handshake.Version).IsEqualTo("v1.2");
        await Assert.That(v2.Handshake.ServerNexusHash).IsEqualTo(1866304169);
        await Assert.That(v2.ServerApi.Versions.Select(v => v.Version).ToArray()).IsEquivalentTo(new[] { "v1.0", "v1.1", "v1.2" }, CollectionOrdering.Matching);
        await Assert.That(v2.ServerApi.Versions.Select(v => v.Hash).ToArray()).IsEquivalentTo(new[] { -326045431, -1058213897, 1866304169 }, CollectionOrdering.Matching);
        await Assert.That(v2.ServerApi.Versions[0].MethodIds).IsEquivalentTo(new ushort[] { 1 }, CollectionOrdering.Matching);
        await Assert.That(v2.ServerApi.Versions[2].MethodIds).IsEquivalentTo(new ushort[] { 1, 2, 3, 4 }, CollectionOrdering.Matching);

        var v1 = Export(ir, "VersionedClientNexusV1");
        await Assert.That(v1.Handshake.Version).IsEqualTo("v1.0");
        await Assert.That(v1.Handshake.ServerNexusHash).IsEqualTo(-326045431);

        var unversioned = Export(ir, "NonVersionedClientNexus");
        await Assert.That(unversioned.Handshake.Version).IsNull();
        await Assert.That(unversioned.Handshake.ServerNexusHash).IsEqualTo(unversioned.ServerApi.Hash);

        // Server and client builds of the same pair agree.
        await Assert.That(Export(ir, "VersionedServerNexusV2").Handshake).IsEqualTo(v2.Handshake);
    }

    // ------------------------------------------------------------------ methods

    [Test]
    public async Task MethodsDescribeReturnsParametersAndPipes()
    {
        var api = Export(Zoo.Ir, "ZooServerNexus").ServerApi;

        await Assert.That(api.Methods.Select(m => m.Name)).DoesNotContain("Skipped");
        await Assert.That(api.Methods.Select(m => (int)m.Id).ToArray()).IsInOrder();

        var echo = Method(api, "Echo");
        await Assert.That(echo.ReturnKind).IsEqualTo(ReturnKind.TaskOf);
        await Assert.That(echo.ReturnType).IsEqualTo(new TypeRef(TypeRefKind.Decl) { DeclId = "Zoo.Everything" });
        await Assert.That(echo.HasCancellation).IsTrue();
        await Assert.That(echo.SerializedCount).IsEqualTo(1);
        await Assert.That(echo.Params.Select(p => (p.Name, p.Kind, p.WireIndex)).ToArray()).IsEquivalentTo(new[]
        {
            ("value", ParamKind.Value, 1),
            ("cancellationToken", ParamKind.CancellationToken, 0),
        }, CollectionOrdering.Matching);
        await Assert.That(echo.Docs).IsEqualTo("Echoes value back.");

        var fire = Method(api, "Fire");
        await Assert.That(fire.ReturnKind).IsEqualTo(ReturnKind.Void);
        await Assert.That(fire.Params.Select(p => p.Nullable).ToArray()).IsEquivalentTo(new[] { true, true }, CollectionOrdering.Matching);
        await Assert.That(fire.Params[1].Type!.Kind).IsEqualTo(TypeRefKind.Nullable);

        var stream = Method(api, "Stream");
        await Assert.That(stream.PipeKind).IsEqualTo(PipeKind.Channel);
        await Assert.That(stream.PipeType).IsEqualTo(new TypeRef(TypeRefKind.Decl) { DeclId = "Zoo.Node" });
        await Assert.That(stream.Params.Single().Kind).IsEqualTo(ParamKind.DuplexChannel);
        await Assert.That(stream.Params.Single().WireIndex).IsEqualTo(1);

        await Assert.That(Method(api, "Upload").PipeKind).IsEqualTo(PipeKind.DuplexPipe);
        await Assert.That(Method(api, "Upload").ReturnKind).IsEqualTo(ReturnKind.Task);
        await Assert.That(Method(api, "Fixed").Id).IsEqualTo(50);
        await Assert.That(Method(api, "Slow").SeparatePool).IsTrue();
        await Assert.That(Method(api, "Slow").IsAuthorized).IsTrue();
        await Assert.That(Method(api, "Echo").IsAuthorized).IsFalse();

        // Authorization lives on the server class: the client's view of the same API does not know it.
        await Assert.That(Method(Export(Zoo.Ir, "ZooClientNexus").ServerApi, "Slow").IsAuthorized).IsFalse();

        var shapes = api.Collections.Single();
        await Assert.That(shapes.Kind).IsEqualTo(CollectionKind.List);
        await Assert.That(shapes.Mode).IsEqualTo(CollectionMode.ServerToClient);
        await Assert.That(shapes.Item).IsEqualTo(new TypeRef(TypeRefKind.Decl) { DeclId = "Zoo.Shape" });
        await Assert.That(shapes.Docs).IsEqualTo("Every shape on the server.");
    }

    // ------------------------------------------------------------------ types

    [Test]
    public async Task TypeRefsFollowTheWireFormat()
    {
        var e = Object(Zoo.Ir, "Zoo.Everything");
        TypeRef Type(string name) => Member(e, name).Type;

        await Assert.That(Type("Flag")).IsEqualTo(new TypeRef(TypeRefKind.Prim) { Prim = PrimKind.Bool });
        await Assert.That(Type("Bytes")).IsEqualTo(new TypeRef(TypeRefKind.Bin) { Clr = "Array" });
        await Assert.That(Type("Memory")).IsEqualTo(new TypeRef(TypeRefKind.Bin) { Clr = "Memory" });
        await Assert.That(Type("Ints")).IsEqualTo(new TypeRef(TypeRefKind.PrimArray) { Prim = PrimKind.Int32, Clr = "Array" });
        await Assert.That(Type("Longs")).IsEqualTo(new TypeRef(TypeRefKind.PrimArray) { Prim = PrimKind.Int64, Clr = "List" });
        await Assert.That(Type("Floats")).IsEqualTo(new TypeRef(TypeRefKind.PrimArray) { Prim = PrimKind.Single, Clr = "ReadOnlyMemory" });
        await Assert.That(Type("Halves")).IsEqualTo(new TypeRef(TypeRefKind.PrimArray) { Prim = PrimKind.Half, Clr = "Array", RefNullable = true });
        await Assert.That(Type("ByteList").Kind).IsEqualTo(TypeRefKind.Seq);
        await Assert.That(Type("ByteList").Args.Single()).IsEqualTo(new TypeRef(TypeRefKind.Prim) { Prim = PrimKind.Byte });

        var names = Type("Names");
        await Assert.That(names.Kind).IsEqualTo(TypeRefKind.Seq);
        await Assert.That(names.Clr).IsEqualTo("IReadOnlyList");
        await Assert.That(names.Args.Single()).IsEqualTo(new TypeRef(TypeRefKind.Prim) { Prim = PrimKind.String, RefNullable = true });

        await Assert.That(Type("Ids").Kind).IsEqualTo(TypeRefKind.Set);
        await Assert.That(Type("Colors").Kind).IsEqualTo(TypeRefKind.Map);
        await Assert.That(Type("Colors").Args[1]).IsEqualTo(new TypeRef(TypeRefKind.Decl) { DeclId = "Zoo.Color" });
        await Assert.That(Type("Queue").Kind).IsEqualTo(TypeRefKind.Queue);
        await Assert.That(Type("Stack").Kind).IsEqualTo(TypeRefKind.Stack);
        await Assert.That(Type("Tuple").TupleKind).IsEqualTo("Value");
        await Assert.That(Type("RefTuple").TupleKind).IsEqualTo("Ref");
        await Assert.That(Type("RefTuple").RefNullable).IsTrue();
        await Assert.That(Type("Kvp").TupleKind).IsEqualTo("KVP");
        await Assert.That(Type("MaybeInt").Kind).IsEqualTo(TypeRefKind.Nullable);
        await Assert.That(Type("MaybeInt").MayBeNull).IsTrue();
        await Assert.That(Type("When").Prim).IsEqualTo(PrimKind.DateTimeOffset);
        await Assert.That(Type("Huge").Prim).IsEqualTo(PrimKind.BigInteger);
        await Assert.That(Type("Stamp").Prim).IsEqualTo(PrimKind.DateTime);
        await Assert.That(Type("Point")).IsEqualTo(new TypeRef(TypeRefKind.External) { Clr = "Zoo.Point" });

        var wrapped = Type("Wrapped");
        await Assert.That(wrapped.DeclId).IsEqualTo("Zoo.Envelope`1");
        await Assert.That(wrapped.RefNullable).IsTrue();
        await Assert.That(wrapped.Args.Single()).IsEqualTo(new TypeRef(TypeRefKind.Decl) { DeclId = "Zoo.Shape", RefNullable = true });
    }

    [Test]
    public async Task MembersCarryNullabilityVisibilityAndLiterals()
    {
        var e = Object(Zoo.Ir, "Zoo.Everything");
        LiteralIr? Literal(string name) => Member(e, name).DefaultLiteral;

        await Assert.That(Literal("Flag")).IsEqualTo(new LiteralIr(LiteralKind.Bool, "true"));
        await Assert.That(Literal("Color")).IsEqualTo(new LiteralIr(LiteralKind.Enum, "Green"));
        await Assert.That(Literal("Access")).IsNull().Because("Only simple constants are recorded.");
        await Assert.That(Literal("Bytes")).IsNull();
        await Assert.That(Literal("Price")).IsEqualTo(new LiteralIr(LiteralKind.Decimal, "1.25"));
        await Assert.That(Literal("Letter")).IsEqualTo(new LiteralIr(LiteralKind.Char, "x"));
        await Assert.That(Literal("Secret")).IsEqualTo(new LiteralIr(LiteralKind.String, ""));
        await Assert.That(Literal("Note")).IsEqualTo(new LiteralIr(LiteralKind.Null, null));
        await Assert.That(Literal("Big")).IsEqualTo(new LiteralIr(LiteralKind.Integer, "-9000000000"));
        await Assert.That(Member(Object(Zoo.Ir, "Zoo.Circle"), "Radius").DefaultLiteral).IsEqualTo(new LiteralIr(LiteralKind.Real, "1.5"));
        await Assert.That(Member(Object(Zoo.Ir, "Zoo.Square"), "Side").DefaultLiteral).IsEqualTo(new LiteralIr(LiteralKind.Integer, "-3"));
        await Assert.That(Member(Object(Zoo.Ir, "Zoo.Circle"), "Name").DefaultLiteral).IsEqualTo(new LiteralIr(LiteralKind.String, "shape"));

        await Assert.That(Member(e, "Secret").WasNonPublic).IsTrue();
        await Assert.That(Member(e, "Note").WasNonPublic).IsFalse();
        await Assert.That(Member(e, "Note").RefNullable).IsTrue();
        await Assert.That(Member(e, "Bytes").RefNullable).IsFalse();
        await Assert.That(Member(e, "MaybeInt").RefNullable).IsFalse();
        await Assert.That(e.ArrayLength).IsEqualTo(36);
    }

    [Test]
    public async Task UnionsGenericsEnumsAndRecursion()
    {
        var ir = Zoo.Ir;

        var shape = (UnionDeclIr)Decl(ir, "Zoo.Shape");
        await Assert.That(shape.IsInterface).IsFalse();
        await Assert.That(shape.Cases.Select(c => (c.Tag, c.CaseDeclId, c.InheritanceDepth)).ToArray()).IsEquivalentTo(new[]
        {
            ((ushort)1, "Zoo.Circle", 2),
            ((ushort)2, "Zoo.Square", 2),
            ((ushort)3, "Zoo.RoundedSquare", 3),
        }, CollectionOrdering.Matching);
        await Assert.That(shape.Docs).IsEqualTo("A drawable shape with a ignored reference.");
        await Assert.That(Object(ir, "Zoo.RoundedSquare").BaseDeclId).IsEqualTo("Zoo.Square");
        await Assert.That(Object(ir, "Zoo.Circle").BaseDeclId).IsEqualTo("Zoo.Shape");
        await Assert.That(Object(ir, "Zoo.RoundedSquare").Members.Select(m => m.Key).ToArray()).IsEquivalentTo(new[] { 0, 1, 2 }, CollectionOrdering.Matching);

        var envelope = Object(ir, "Zoo.Envelope`1");
        await Assert.That(envelope.Name).IsEqualTo("Envelope");
        await Assert.That(envelope.TypeParams).IsEquivalentTo(new[] { "T" }, CollectionOrdering.Matching);
        await Assert.That(envelope.ArrayLength).IsEqualTo(4);
        await Assert.That(Member(envelope, "Value").Type).IsEqualTo(new TypeRef(TypeRefKind.TypeParam) { Ordinal = 0 });
        await Assert.That(Member(envelope, "Maybe").Type).IsEqualTo(new TypeRef(TypeRefKind.TypeParam) { Ordinal = 0, RefNullable = true });

        var color = (EnumDeclIr)Decl(ir, "Zoo.Color");
        await Assert.That(color.Underlying).IsEqualTo(PrimKind.Byte);
        await Assert.That(color.IsFlags).IsFalse();
        await Assert.That(color.Members.Select(m => (m.Name, m.Value)).ToArray()).IsEquivalentTo(new[] { ("Red", 1L), ("Green", 2L), ("Blue", 4L) }, CollectionOrdering.Matching);
        await Assert.That(color.Docs).IsEqualTo("Primary colors. See Shape.");

        var access = (EnumDeclIr)Decl(ir, "Zoo.Access");
        await Assert.That(access.Underlying).IsEqualTo(PrimKind.UInt64);
        await Assert.That(access.IsFlags).IsTrue();
        await Assert.That(access.Members.Single(m => m.Name == "All").Value).IsEqualTo(-1L);

        await Assert.That(Object(ir, "Zoo.Node").IsRecursive).IsTrue();
        await Assert.That(Object(ir, "Zoo.Tree").IsRecursive).IsTrue();
        await Assert.That(Object(ir, "Zoo.Everything").IsRecursive).IsFalse();
        await Assert.That(shape.IsRecursive).IsFalse();

        var pair = Object(ir, "Zoo.Pair");
        await Assert.That(pair.IsStruct).IsTrue();
        await Assert.That(pair.IsRecord).IsTrue();

        // Reached only through other producers.
        await Assert.That(ir.Types.Select(t => t.Id)).Contains("Zoo.RuntimeOnly");
        await Assert.That(ir.Types.Select(t => t.Id)).Contains("Zoo.Orphan");
        var ids = ir.Types.Select(t => t.Id).ToArray();
        await Assert.That(ids).IsEquivalentTo(ids.OrderBy(id => id, StringComparer.Ordinal), CollectionOrdering.Matching);
    }

    [Test]
    public async Task NestedTypesRecordTheirContainers()
    {
        var ir = IrTestRunner.Run(IrFixtures.Versioned).Ir;
        var state = (EnumDeclIr)Decl(ir, "NexNet.IntegrationTests.TestInterfaces.IVersionedServerNexusV1_1+ReturnState");
        await Assert.That(state.Name).IsEqualTo("ReturnState");
        await Assert.That(state.ClrNamespace).IsEqualTo("NexNet.IntegrationTests.TestInterfaces");
        await Assert.That(state.ContainingTypes).IsEquivalentTo(new[] { "IVersionedServerNexusV1_1" }, CollectionOrdering.Matching);
        await Assert.That(state.Underlying).IsEqualTo(PrimKind.UInt16);
    }
}
