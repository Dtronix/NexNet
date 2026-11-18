using NexNet.Generator.Ir;
using NexNet.Generator.Models;
using NUnit.Framework;

namespace NexNet.Generator.Tests.Ir;

public class IrBuilderTests
{
    private static IrRun? _zoo;

    private static IrRun Zoo => _zoo ??= IrTestRunner.Run(IrFixtures.ZooTypes, IrFixtures.ZooNexus);

    private static ObjectDeclIr Object(NexusIr ir, string id) => (ObjectDeclIr)Decl(ir, id);

    private static TypeDeclIr Decl(NexusIr ir, string id)
        => ir.Types.FirstOrDefault(t => t.Id == id) ?? throw new AssertionException($"Declaration '{id}' not found.");

    private static MemberIr Member(ObjectDeclIr decl, string name)
        => decl.Members.FirstOrDefault(m => m.Name == name) ?? throw new AssertionException($"Member '{name}' not found.");

    private static ExportIr Export(NexusIr ir, string name)
        => ir.Exports.FirstOrDefault(e => e.Name == name) ?? throw new AssertionException($"Export '{name}' not found.");

    private static MethodIr Method(InterfaceIr api, string name)
        => api.Methods.FirstOrDefault(m => m.Name == name) ?? throw new AssertionException($"Method '{name}' not found.");

    private static Serialization.EquatableArray<T> Array<T>(IEnumerable<T> items)
        where T : IEquatable<T>
        => new(items.ToArray());

    // ------------------------------------------------------------------ snapshots

    [Test]
    public void BasicTestNexusSnapshot() => IrSnapshots.Verify("basic", IrJson.Write(IrTestRunner.Run(IrFixtures.Basic).Ir));

    [Test]
    public void VersionedTestNexusSnapshot() => IrSnapshots.Verify("versioned", IrJson.Write(IrTestRunner.Run(IrFixtures.Versioned).Ir));

    [Test]
    public void ZooSnapshot() => IrSnapshots.Verify("zoo", IrJson.Write(Zoo.Ir));

    // ------------------------------------------------------------------ determinism and JSON

    [Test]
    public void BuildIsDeterministicAndIndependentOfSourceOrder()
    {
        var first = IrTestRunner.Run(IrFixtures.ZooTypes, IrFixtures.ZooNexus, IrFixtures.Versioned);
        var second = IrTestRunner.Run(IrFixtures.Versioned, IrFixtures.ZooNexus, IrFixtures.ZooTypes);
        var third = IrTestRunner.Run(IrFixtures.ZooTypes, IrFixtures.ZooNexus, IrFixtures.Versioned);

        Assert.That(IrJson.Write(second.Ir), Is.EqualTo(IrJson.Write(first.Ir)));
        Assert.That(IrJson.Write(third.Ir), Is.EqualTo(IrJson.Write(first.Ir)));
        Assert.That(third.Ir, Is.EqualTo(first.Ir));
    }

    [Test]
    public void IrIsEquatable()
    {
        var a = IrTestRunner.Run(IrFixtures.ZooTypes, IrFixtures.ZooNexus).Ir;
        var b = IrTestRunner.Run(IrFixtures.ZooTypes, IrFixtures.ZooNexus).Ir;
        Assert.That(b, Is.EqualTo(a));
        Assert.That(b.GetHashCode(), Is.EqualTo(a.GetHashCode()));
    }

    [Test]
    public void MergeIgnoresFragmentOrder()
    {
        var run = IrTestRunner.Run(IrFixtures.Basic, IrFixtures.Versioned);
        var merged = IrBuilder.Merge(new[]
        {
            new IrFragment(Array(run.Ir.Exports.Reverse()), run.Ir.Types),
            new IrFragment(run.Ir.Exports, Array(run.Ir.Types.Reverse())),
        });
        Assert.That(merged, Is.EqualTo(run.Ir));
    }

    [Test]
    public void JsonRoundTrips()
    {
        foreach (var json in new[]
                 {
                     IrJson.Write(Zoo.Ir),
                     IrJson.Write(IrTestRunner.Run(IrFixtures.Basic).Ir),
                     IrJson.Write(IrTestRunner.Run(IrFixtures.Versioned).Ir),
                 })
        {
            var read = IrJson.Read(json);
            Assert.That(IrJson.Write(read), Is.EqualTo(json));
        }
    }

    [Test]
    public void JsonReadRestoresEverythingButLocations()
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
        Assert.That(read, Is.EqualTo(expected));
    }

    [Test]
    public void JsonIsSortedAndHasNoPathsOrCarriageReturns()
    {
        var json = IrJson.Write(Zoo.Ir);
        Assert.That(json, Does.Not.Contain("\r"));
        Assert.That(json, Does.Not.Contain(".cs\""));
        Assert.That(json, Does.StartWith("{\n  \"exports\": ["));
        Assert.That(json, Does.EndWith("}\n"));
    }

    [Test]
    public void JsonReadRejectsInvalidInput()
    {
        Assert.Throws<FormatException>(() => IrJson.Read("{ \"irVersion\": 2 }"));
        Assert.Throws<FormatException>(() => IrJson.Read("{ \"irVersion\": 1, \"exports\": [ { \"name\": 1 } ] }"));
        Assert.Throws<FormatException>(() => IrJson.Read("{ \"irVersion\": 1 } trailing"));
    }

    // ------------------------------------------------------------------ hashes and handshake

    [Test]
    public void HashesEqualTheGenerationData()
    {
        foreach (var run in new[] { IrTestRunner.Run(IrFixtures.Basic), IrTestRunner.Run(IrFixtures.Versioned), Zoo })
        {
            Assert.That(run.Ir.Exports.Length, Is.EqualTo(run.Data.Length));
            foreach (var data in run.Data)
            {
                var export = run.Ir.Exports.Single(e => e.Name == data.TypeName && e.ClrNamespace == data.Namespace);
                var server = data.NexusAttribute.IsServer ? data.NexusInterface : data.ProxyInterface;
                var client = data.NexusAttribute.IsServer ? data.ProxyInterface : data.NexusInterface;

                AssertApi(export.ServerApi, server);
                AssertApi(export.ClientApi, client);
                Assert.That(export.Handshake.ClientNexusHash, Is.EqualTo(client.NexusHash));
                Assert.That(export.ClientAcceptedIds.Select(a => a.Hash).Distinct(), Is.EqualTo(new[] { client.NexusHash }));
                Assert.That(export.ClientAcceptedIds.Select(a => a.Id),
                    Is.EquivalentTo(client.AllMethods.Select(m => m.Id).Concat(client.AllCollections.Select(c => c.Id))));
            }
        }
    }

    private static void AssertApi(InterfaceIr api, InvocationInterfaceData data)
    {
        Assert.That(api.Hash, Is.EqualTo(data.NexusHash), api.ClrName);
        Assert.That(api.Methods.Select(m => (m.Id, m.Name, m.Hash)),
            Is.EquivalentTo(data.AllMethods.Select(m => (m.Id, m.Name, m.NexusHash))), api.ClrName);
        Assert.That(api.Collections.Select(c => (c.Id, c.Name, c.Hash)),
            Is.EquivalentTo(data.AllCollections.Select(c => (c.Id, c.Name, c.NexusHash))), api.ClrName);
        Assert.That(api.Versions.Select(v => (v.Version, v.Hash)),
            Is.EqualTo(data.Versions.Select(v => (v.VersionAttribute?.Version ?? "", v.NexusHash))), api.ClrName);
    }

    [Test]
    public void HashesEqualTheEmittedCSharp()
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
                Assert.That(hashes, Is.EqualTo(expected), data.TypeName);
            }
        }
    }

    [Test]
    public void HandshakeUsesTheLastServerVersion()
    {
        var ir = IrTestRunner.Run(IrFixtures.Versioned).Ir;

        var v2 = Export(ir, "VersionedClientNexusV2");
        Assert.That(v2.Handshake.Version, Is.EqualTo("v1.2"));
        Assert.That(v2.Handshake.ServerNexusHash, Is.EqualTo(1866304169));
        Assert.That(v2.ServerApi.Versions.Select(v => v.Version), Is.EqualTo(new[] { "v1.0", "v1.1", "v1.2" }));
        Assert.That(v2.ServerApi.Versions.Select(v => v.Hash), Is.EqualTo(new[] { -326045431, -1058213897, 1866304169 }));
        Assert.That(v2.ServerApi.Versions[0].MethodIds, Is.EqualTo(new ushort[] { 1 }));
        Assert.That(v2.ServerApi.Versions[2].MethodIds, Is.EqualTo(new ushort[] { 1, 2, 3, 4 }));

        var v1 = Export(ir, "VersionedClientNexusV1");
        Assert.That(v1.Handshake.Version, Is.EqualTo("v1.0"));
        Assert.That(v1.Handshake.ServerNexusHash, Is.EqualTo(-326045431));

        var unversioned = Export(ir, "NonVersionedClientNexus");
        Assert.That(unversioned.Handshake.Version, Is.Null);
        Assert.That(unversioned.Handshake.ServerNexusHash, Is.EqualTo(unversioned.ServerApi.Hash));

        // Server and client builds of the same pair agree.
        Assert.That(Export(ir, "VersionedServerNexusV2").Handshake, Is.EqualTo(v2.Handshake));
    }

    // ------------------------------------------------------------------ methods

    [Test]
    public void MethodsDescribeReturnsParametersAndPipes()
    {
        var api = Export(Zoo.Ir, "ZooServerNexus").ServerApi;

        Assert.That(api.Methods.Select(m => m.Name), Does.Not.Contain("Skipped"));
        Assert.That(api.Methods.Select(m => (int)m.Id), Is.Ordered);

        var echo = Method(api, "Echo");
        Assert.That(echo.ReturnKind, Is.EqualTo(ReturnKind.TaskOf));
        Assert.That(echo.ReturnType, Is.EqualTo(new TypeRef(TypeRefKind.Decl) { DeclId = "Zoo.Everything" }));
        Assert.That(echo.HasCancellation, Is.True);
        Assert.That(echo.SerializedCount, Is.EqualTo(1));
        Assert.That(echo.Params.Select(p => (p.Name, p.Kind, p.WireIndex)), Is.EqualTo(new[]
        {
            ("value", ParamKind.Value, 1),
            ("cancellationToken", ParamKind.CancellationToken, 0),
        }));
        Assert.That(echo.Docs, Is.EqualTo("Echoes value back."));

        var fire = Method(api, "Fire");
        Assert.That(fire.ReturnKind, Is.EqualTo(ReturnKind.Void));
        Assert.That(fire.Params.Select(p => p.Nullable), Is.EqualTo(new[] { true, true }));
        Assert.That(fire.Params[1].Type!.Kind, Is.EqualTo(TypeRefKind.Nullable));

        var stream = Method(api, "Stream");
        Assert.That(stream.PipeKind, Is.EqualTo(PipeKind.Channel));
        Assert.That(stream.PipeType, Is.EqualTo(new TypeRef(TypeRefKind.Decl) { DeclId = "Zoo.Node" }));
        Assert.That(stream.Params.Single().Kind, Is.EqualTo(ParamKind.DuplexChannel));
        Assert.That(stream.Params.Single().WireIndex, Is.EqualTo(1));

        Assert.That(Method(api, "Upload").PipeKind, Is.EqualTo(PipeKind.DuplexPipe));
        Assert.That(Method(api, "Upload").ReturnKind, Is.EqualTo(ReturnKind.Task));
        Assert.That(Method(api, "Fixed").Id, Is.EqualTo(50));
        Assert.That(Method(api, "Slow").SeparatePool, Is.True);
        Assert.That(Method(api, "Slow").IsAuthorized, Is.True);
        Assert.That(Method(api, "Echo").IsAuthorized, Is.False);

        // Authorization lives on the server class: the client's view of the same API does not know it.
        Assert.That(Method(Export(Zoo.Ir, "ZooClientNexus").ServerApi, "Slow").IsAuthorized, Is.False);

        var shapes = api.Collections.Single();
        Assert.That(shapes.Kind, Is.EqualTo(CollectionKind.List));
        Assert.That(shapes.Mode, Is.EqualTo(CollectionMode.ServerToClient));
        Assert.That(shapes.Item, Is.EqualTo(new TypeRef(TypeRefKind.Decl) { DeclId = "Zoo.Shape" }));
        Assert.That(shapes.Docs, Is.EqualTo("Every shape on the server."));
    }

    // ------------------------------------------------------------------ types

    [Test]
    public void TypeRefsFollowTheWireFormat()
    {
        var e = Object(Zoo.Ir, "Zoo.Everything");
        TypeRef Type(string name) => Member(e, name).Type;

        Assert.That(Type("Flag"), Is.EqualTo(new TypeRef(TypeRefKind.Prim) { Prim = PrimKind.Bool }));
        Assert.That(Type("Bytes"), Is.EqualTo(new TypeRef(TypeRefKind.Bin) { Clr = "Array" }));
        Assert.That(Type("Memory"), Is.EqualTo(new TypeRef(TypeRefKind.Bin) { Clr = "Memory" }));
        Assert.That(Type("Ints"), Is.EqualTo(new TypeRef(TypeRefKind.PrimArray) { Prim = PrimKind.Int32, Clr = "Array" }));
        Assert.That(Type("Longs"), Is.EqualTo(new TypeRef(TypeRefKind.PrimArray) { Prim = PrimKind.Int64, Clr = "List" }));
        Assert.That(Type("Floats"), Is.EqualTo(new TypeRef(TypeRefKind.PrimArray) { Prim = PrimKind.Single, Clr = "ReadOnlyMemory" }));
        Assert.That(Type("Halves"), Is.EqualTo(new TypeRef(TypeRefKind.PrimArray) { Prim = PrimKind.Half, Clr = "Array", RefNullable = true }));
        Assert.That(Type("ByteList").Kind, Is.EqualTo(TypeRefKind.Seq));
        Assert.That(Type("ByteList").Args.Single(), Is.EqualTo(new TypeRef(TypeRefKind.Prim) { Prim = PrimKind.Byte }));

        var names = Type("Names");
        Assert.That(names.Kind, Is.EqualTo(TypeRefKind.Seq));
        Assert.That(names.Clr, Is.EqualTo("IReadOnlyList"));
        Assert.That(names.Args.Single(), Is.EqualTo(new TypeRef(TypeRefKind.Prim) { Prim = PrimKind.String, RefNullable = true }));

        Assert.That(Type("Ids").Kind, Is.EqualTo(TypeRefKind.Set));
        Assert.That(Type("Colors").Kind, Is.EqualTo(TypeRefKind.Map));
        Assert.That(Type("Colors").Args[1], Is.EqualTo(new TypeRef(TypeRefKind.Decl) { DeclId = "Zoo.Color" }));
        Assert.That(Type("Queue").Kind, Is.EqualTo(TypeRefKind.Queue));
        Assert.That(Type("Stack").Kind, Is.EqualTo(TypeRefKind.Stack));
        Assert.That(Type("Tuple").TupleKind, Is.EqualTo("Value"));
        Assert.That(Type("RefTuple").TupleKind, Is.EqualTo("Ref"));
        Assert.That(Type("RefTuple").RefNullable, Is.True);
        Assert.That(Type("Kvp").TupleKind, Is.EqualTo("KVP"));
        Assert.That(Type("MaybeInt").Kind, Is.EqualTo(TypeRefKind.Nullable));
        Assert.That(Type("MaybeInt").MayBeNull, Is.True);
        Assert.That(Type("When").Prim, Is.EqualTo(PrimKind.DateTimeOffset));
        Assert.That(Type("Huge").Prim, Is.EqualTo(PrimKind.BigInteger));
        Assert.That(Type("Stamp").Prim, Is.EqualTo(PrimKind.DateTime));
        Assert.That(Type("Point"), Is.EqualTo(new TypeRef(TypeRefKind.External) { Clr = "Zoo.Point" }));

        var wrapped = Type("Wrapped");
        Assert.That(wrapped.DeclId, Is.EqualTo("Zoo.Envelope`1"));
        Assert.That(wrapped.RefNullable, Is.True);
        Assert.That(wrapped.Args.Single(), Is.EqualTo(new TypeRef(TypeRefKind.Decl) { DeclId = "Zoo.Shape", RefNullable = true }));
    }

    [Test]
    public void MembersCarryNullabilityVisibilityAndLiterals()
    {
        var e = Object(Zoo.Ir, "Zoo.Everything");
        LiteralIr? Literal(string name) => Member(e, name).DefaultLiteral;

        Assert.That(Literal("Flag"), Is.EqualTo(new LiteralIr(LiteralKind.Bool, "true")));
        Assert.That(Literal("Color"), Is.EqualTo(new LiteralIr(LiteralKind.Enum, "Green")));
        Assert.That(Literal("Access"), Is.Null, "Only simple constants are recorded.");
        Assert.That(Literal("Bytes"), Is.Null);
        Assert.That(Literal("Price"), Is.EqualTo(new LiteralIr(LiteralKind.Decimal, "1.25")));
        Assert.That(Literal("Letter"), Is.EqualTo(new LiteralIr(LiteralKind.Char, "x")));
        Assert.That(Literal("Secret"), Is.EqualTo(new LiteralIr(LiteralKind.String, "")));
        Assert.That(Literal("Note"), Is.EqualTo(new LiteralIr(LiteralKind.Null, null)));
        Assert.That(Literal("Big"), Is.EqualTo(new LiteralIr(LiteralKind.Integer, "-9000000000")));
        Assert.That(Member(Object(Zoo.Ir, "Zoo.Circle"), "Radius").DefaultLiteral, Is.EqualTo(new LiteralIr(LiteralKind.Real, "1.5")));
        Assert.That(Member(Object(Zoo.Ir, "Zoo.Square"), "Side").DefaultLiteral, Is.EqualTo(new LiteralIr(LiteralKind.Integer, "-3")));
        Assert.That(Member(Object(Zoo.Ir, "Zoo.Circle"), "Name").DefaultLiteral, Is.EqualTo(new LiteralIr(LiteralKind.String, "shape")));

        Assert.That(Member(e, "Secret").WasNonPublic, Is.True);
        Assert.That(Member(e, "Note").WasNonPublic, Is.False);
        Assert.That(Member(e, "Note").RefNullable, Is.True);
        Assert.That(Member(e, "Bytes").RefNullable, Is.False);
        Assert.That(Member(e, "MaybeInt").RefNullable, Is.False);
        Assert.That(e.ArrayLength, Is.EqualTo(36));
    }

    [Test]
    public void UnionsGenericsEnumsAndRecursion()
    {
        var ir = Zoo.Ir;

        var shape = (UnionDeclIr)Decl(ir, "Zoo.Shape");
        Assert.That(shape.IsInterface, Is.False);
        Assert.That(shape.Cases.Select(c => (c.Tag, c.CaseDeclId, c.InheritanceDepth)), Is.EqualTo(new[]
        {
            ((ushort)1, "Zoo.Circle", 2),
            ((ushort)2, "Zoo.Square", 2),
            ((ushort)3, "Zoo.RoundedSquare", 3),
        }));
        Assert.That(shape.Docs, Is.EqualTo("A drawable shape with a ignored reference."));
        Assert.That(Object(ir, "Zoo.RoundedSquare").BaseDeclId, Is.EqualTo("Zoo.Square"));
        Assert.That(Object(ir, "Zoo.Circle").BaseDeclId, Is.EqualTo("Zoo.Shape"));
        Assert.That(Object(ir, "Zoo.RoundedSquare").Members.Select(m => m.Key), Is.EqualTo(new[] { 0, 1, 2 }));

        var envelope = Object(ir, "Zoo.Envelope`1");
        Assert.That(envelope.Name, Is.EqualTo("Envelope"));
        Assert.That(envelope.TypeParams, Is.EqualTo(new[] { "T" }));
        Assert.That(envelope.ArrayLength, Is.EqualTo(4));
        Assert.That(Member(envelope, "Value").Type, Is.EqualTo(new TypeRef(TypeRefKind.TypeParam) { Ordinal = 0 }));
        Assert.That(Member(envelope, "Maybe").Type, Is.EqualTo(new TypeRef(TypeRefKind.TypeParam) { Ordinal = 0, RefNullable = true }));

        var color = (EnumDeclIr)Decl(ir, "Zoo.Color");
        Assert.That(color.Underlying, Is.EqualTo(PrimKind.Byte));
        Assert.That(color.IsFlags, Is.False);
        Assert.That(color.Members.Select(m => (m.Name, m.Value)), Is.EqualTo(new[] { ("Red", 1L), ("Green", 2L), ("Blue", 4L) }));
        Assert.That(color.Docs, Is.EqualTo("Primary colors. See Shape."));

        var access = (EnumDeclIr)Decl(ir, "Zoo.Access");
        Assert.That(access.Underlying, Is.EqualTo(PrimKind.UInt64));
        Assert.That(access.IsFlags, Is.True);
        Assert.That(access.Members.Single(m => m.Name == "All").Value, Is.EqualTo(-1L));

        Assert.That(Object(ir, "Zoo.Node").IsRecursive, Is.True);
        Assert.That(Object(ir, "Zoo.Tree").IsRecursive, Is.True);
        Assert.That(Object(ir, "Zoo.Everything").IsRecursive, Is.False);
        Assert.That(shape.IsRecursive, Is.False);

        var pair = Object(ir, "Zoo.Pair");
        Assert.That(pair.IsStruct, Is.True);
        Assert.That(pair.IsRecord, Is.True);

        // Reached only through other producers.
        Assert.That(ir.Types.Select(t => t.Id), Does.Contain("Zoo.RuntimeOnly"));
        Assert.That(ir.Types.Select(t => t.Id), Does.Contain("Zoo.Orphan"));
        Assert.That(ir.Types.Select(t => t.Id), Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));
    }

    [Test]
    public void NestedTypesRecordTheirContainers()
    {
        var ir = IrTestRunner.Run(IrFixtures.Versioned).Ir;
        var state = (EnumDeclIr)Decl(ir, "NexNet.IntegrationTests.TestInterfaces.IVersionedServerNexusV1_1+ReturnState");
        Assert.That(state.Name, Is.EqualTo("ReturnState"));
        Assert.That(state.ClrNamespace, Is.EqualTo("NexNet.IntegrationTests.TestInterfaces"));
        Assert.That(state.ContainingTypes, Is.EqualTo(new[] { "IVersionedServerNexusV1_1" }));
        Assert.That(state.Underlying, Is.EqualTo(PrimKind.UInt16));
    }
}
