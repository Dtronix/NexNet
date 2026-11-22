using NexNet.Generator.Ir;
using NexNet.Generator.Targets;
using NexNet.Generator.Targets.JavaScript;

namespace NexNet.Generator.Tests.Targets.JavaScript;

/// <summary>
/// The opt-in checked-number mapping of long/ulong for JavaScript (D13, C-8): <c>[NexusJsNumber]</c> on a member,
/// parameter, return value, channel, collection or the assembly sets <see cref="TypeRef.JsNumber"/> in the IR, and the
/// emitter types those values <c>number</c> with the checked runtime formatters.
/// </summary>
public class JsNumberTests
{
    private static ObjectDeclIr Decl(NexusIr? ir, string id)
        => ir!.Types.OfType<ObjectDeclIr>().Single(t => t.Id == id);

    private static TypeRef Member(NexusIr? ir, string id, string name)
        => Decl(ir, id).Members.Single(m => m.Name == name).Type;

    private static MethodIr Method(NexusIr? ir, string name)
        => ir!.Exports.Single().ServerApi.Methods.Single(m => m.Name == name);

    // ------------------------------------------------------------------ IR

    [Test]
    public async Task MemberAttributeMarksTheLongLeaves()
    {
        var ir = JsEmitterTests.Features.Ir;
        await Assert.That(Member(ir, "Feat.Checked", "Id").JsNumber).IsTrue();
        await Assert.That(Member(ir, "Feat.Checked", "Max").JsNumber).IsTrue();
        await Assert.That(Member(ir, "Feat.Checked", "Maybe").Args[0].JsNumber).IsTrue();

        var totals = Member(ir, "Feat.Checked", "Totals");
        await Assert.That(totals.Args[0].JsNumber).IsFalse();   // string key
        await Assert.That(totals.Args[1].JsNumber).IsTrue();
        await Assert.That(Member(ir, "Feat.Checked", "Seq").Args[0].JsNumber).IsTrue();
        await Assert.That(Member(ir, "Feat.Checked", "Held").Args[0].JsNumber).IsTrue();

        // Ext 78 primitive arrays and unattributed members keep the bigint mapping.
        var packed = Member(ir, "Feat.Checked", "Packed");
        await Assert.That(packed.Kind).IsEqualTo(TypeRefKind.PrimArray);
        await Assert.That(packed.JsNumber).IsFalse();
        await Assert.That(Member(ir, "Feat.Checked", "Plain").JsNumber).IsFalse();
        await Assert.That(Member(ir, "Feat.Defaults", "Big").JsNumber).IsFalse();
    }

    [Test]
    public async Task JsonWritesAndReadsTheFlag()
    {
        var ir = JsEmitterTests.Features.Ir!;
        var json = IrJson.Write(ir);
        await Assert.That(json).Contains("\"jsNumber\": true");
        var read = IrJson.Read(json);
        await Assert.That(IrJson.Write(read)).IsEqualTo(json);
        await Assert.That(read.Types.OfType<ObjectDeclIr>().Single(t => t.Id == "Feat.Checked").Members[0].Type.JsNumber).IsTrue();

        // Absent by default: an IR without the attribute writes no jsNumber field.
        await Assert.That(IrJson.Write(JsEmitterTests.Zoo.Ir!)).DoesNotContain("jsNumber");
    }

    [Test]
    public async Task ParametersReturnsChannelsAndCollectionsAreMarked()
    {
        var run = JsTestRunner.Run(Api(assemblyWide: false));
        await Assert.That(run.Diagnostics).IsEmpty();
        var ir = run.Ir;

        var count = Method(ir, "Count");
        await Assert.That(count.ReturnType!.JsNumber).IsTrue();
        await Assert.That(count.Params.Single(p => p.Name == "since").Type!.JsNumber).IsTrue();
        await Assert.That(count.Params.Single(p => p.Name == "other").Type!.JsNumber).IsFalse();

        var stream = Method(ir, "Stream");
        await Assert.That(stream.PipeType!.JsNumber).IsTrue();
        await Assert.That(stream.Params.Single(p => p.Name == "channel").Type!.JsNumber).IsTrue();

        await Assert.That(ir!.Exports.Single().ServerApi.Collections.Single().Item!.JsNumber).IsTrue();
        await Assert.That(Method(ir, "Plain").ReturnType!.JsNumber).IsFalse();
        await Assert.That(Member(ir, "Demo.Item", "Id").JsNumber).IsFalse();

        // A positional record parameter's attribute applies to its property.
        await Assert.That(Member(ir, "Demo.Pos", "A").JsNumber).IsTrue();
        await Assert.That(Member(ir, "Demo.Pos", "B").JsNumber).IsFalse();
    }

    [Test]
    public async Task TheAttributeChangesNoHash()
    {
        var with = JsTestRunner.Run(Api(assemblyWide: true)).Ir!.Exports.Single();
        var without = JsTestRunner.Run(Api(assemblyWide: false).Replace("[NexusJsNumber]", "").Replace("[return: ]", "")).Ir!.Exports.Single();
        await Assert.That(with.ServerApi.Hash).IsEqualTo(without.ServerApi.Hash);
        await Assert.That(with.ClientApi.Hash).IsEqualTo(without.ClientApi.Hash);
        await Assert.That(with.ServerApi.Methods.Select(m => m.Hash)).IsEquivalentTo(without.ServerApi.Methods.Select(m => m.Hash));
        await Assert.That(with.Handshake).IsEqualTo(without.Handshake);
    }

    [Test]
    public async Task AssemblyAttributeMarksEverything()
    {
        var run = JsTestRunner.Run(Api(assemblyWide: true));
        await Assert.That(run.Diagnostics).IsEmpty();
        var ir = run.Ir;

        await Assert.That(Method(ir, "Count").Params.Single(p => p.Name == "other").Type!.JsNumber).IsTrue();
        await Assert.That(Method(ir, "Plain").ReturnType!.JsNumber).IsTrue();
        await Assert.That(ir!.Exports.Single().ClientApi.Methods.Single().Params[0].Type!.JsNumber).IsTrue();
        await Assert.That(Member(ir, "Demo.Item", "Id").JsNumber).IsTrue();
        await Assert.That(Member(ir, "Demo.Item", "Packed").JsNumber).IsFalse();
        await Assert.That(Member(ir, "Demo.Item", "Wide").Kind).IsEqualTo(TypeRefKind.Decl);

        // 64-bit enums stay bigint.
        await Assert.That(run.File("Item.d.ts")).Contains("id: number;");
        await Assert.That(run.File("Item.d.ts")).Contains("wide: Wide;");
        await Assert.That(run.File("Wide.d.ts")).Contains("export type Wide = 1n;");
        await Assert.That(run.File("formatters.js")).Contains("$rt.int64NumberFormatter");
        await Assert.That(run.File("formatters.js")).DoesNotContain("$rt.int64Formatter,");
    }

    // ------------------------------------------------------------------ JavaScript

    [Test]
    public async Task OptedInMembersAreNumbers()
    {
        var dts = JsEmitterTests.Features.File("Checked.d.ts");
        await Assert.That(dts).Contains("id: number;");
        await Assert.That(dts).Contains("max: number;");
        await Assert.That(dts).Contains("maybe: number | null;");
        await Assert.That(dts).Contains("totals: Map<string, number> | null;");
        await Assert.That(dts).Contains("seq: Array<number> | null;");
        await Assert.That(dts).Contains("packed: BigInt64Array | null;");
        await Assert.That(dts).Contains("held: Holder<number> | null;");
        await Assert.That(dts).Contains("plain: bigint;");
    }

    [Test]
    public async Task OptedInMembersStartAtNumberLiterals()
    {
        var js = JsEmitterTests.Features.File("Checked.js");
        await Assert.That(js).Contains("this.id = init?.id === undefined ? 7 : init.id;");
        await Assert.That(js).Contains("this.max = init?.max === undefined ? 9007199254740991 : init.max;");
        await Assert.That(js).Contains("this.plain = init?.plain === undefined ? 3n : init.plain;");

        // Beyond the safe range there is no exact number literal: the member starts at 0.
        var run = JsTestRunner.Run(Api(assemblyWide: false));
        await Assert.That(run.File("Item.js")).Contains("this.big = init?.big === undefined ? 0 : init.big;");
        await Assert.That(run.File("Item.js")).Contains("this.safe = init?.safe === undefined ? -9007199254740991 : init.safe;");
    }

    [Test]
    public async Task OptedInMembersUseTheCheckedFormatters()
    {
        var formatters = JsEmitterTests.Features.File("formatters.js");
        await Assert.That(formatters).Contains("$rt.int64NumberFormatter");
        await Assert.That(formatters).Contains("$rt.nullableFormatter($rt.int64NumberFormatter)");
        await Assert.That(formatters).Contains("$rt.mapFormatter($rt.stringFormatter, $rt.uint64NumberFormatter)");
        await Assert.That(formatters).Contains("HolderFormatter($rt.int64NumberFormatter)");

        // A generic T[] / List<T> bound to a checked long keeps the C# ext 78 wire form.
        await Assert.That(formatters).Contains("[$rt.int64NumberFormatter, $rt.int64ArrayFormatter, BigInt64Array, 'number'],");
        await Assert.That(formatters).Contains("[$rt.uint64NumberFormatter, $rt.uint64ArrayFormatter, BigUint64Array, 'number'],");
    }

    [Test]
    public async Task ExpressionsForCheckedPrimitives()
    {
        var expressions = new JsFormatterExpressions(new TypeScope(JsEmitterTests.Features.Ir!), "fmt.", _ => "user");
        await Assert.That(expressions.For(new TypeRef(TypeRefKind.Prim) { Prim = PrimKind.UInt64, JsNumber = true })).IsEqualTo("$rt.uint64NumberFormatter");
        await Assert.That(expressions.For(new TypeRef(TypeRefKind.Prim) { Prim = PrimKind.Int64 })).IsEqualTo("$rt.int64Formatter");
    }

    private static string Api(bool assemblyWide) => TargetTestRunner.Usings + """
        #nullable enable
        using NexNet.Collections;
        using NexNet.Collections.Lists;
        using NexNet.Pipes;

        """ + (assemblyWide ? "[assembly: NexusJsNumber]\n" : "") + """

        namespace Demo;

        public enum Wide : long { A = 1 }

        [NexusObject]
        public partial record Pos([property: NexusKey(0)][NexusJsNumber] long A, [property: NexusKey(1)] long B);

        [NexusObject]
        public class Item
        {
            [NexusKey(0)] public long Id { get; set; }
            [NexusKey(1)] public long[]? Packed { get; set; }
            [NexusKey(2)] public Wide Wide { get; set; }
            [NexusKey(3), NexusJsNumber] public long Big { get; set; } = 9007199254740992;
            [NexusKey(4), NexusJsNumber] public long Safe { get; set; } = -9007199254740991;
        }

        public interface IClientNexus { ValueTask Notify(long value); }

        public interface IServerNexus
        {
            [return: NexusJsNumber] ValueTask<long> Count([NexusJsNumber] long since, long other);
            ValueTask<long> Plain();
            ValueTask Stream([NexusJsNumber] INexusDuplexChannel<ulong> channel);
            ValueTask<Item> Get();
            ValueTask<Pos> GetPos();
            [NexusCollection(NexusCollectionMode.ServerToClient), NexusJsNumber] INexusList<long> Values { get; }
        }

        [Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
        public partial class ClientNexus { }

        [Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server, Targets = NexusTargets.JavaScript)]
        public partial class ServerNexus { }
        """;
}
