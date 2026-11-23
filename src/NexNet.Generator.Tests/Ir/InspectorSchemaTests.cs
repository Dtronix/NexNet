using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NexNet.Diagnostics;
using NexNet.Generator.Emission;
using NexNet.Generator.Ir;
using NexNet.Generator.Serialization;
using NexNet.Generator.Tests.Ir.SchemaNexus;
using NexNet.Generator.Tests.Targets;

namespace NexNet.Generator.Tests.Ir;

/// <summary>
/// The embedded inspector schema (I06): the MessagePack IR writer, its agreement with NexNet's MsgPackReader, the
/// NexNetEmitInspectorSchema opt-in and the IInspectorSchemaSource implementation it emits.
/// </summary>
public class InspectorSchemaTests
{
    private const string SchemaSuffix = ".InspectorSchema.g.cs";

    /// <summary>A small pair for the generated-code snapshot: a result, a pipe, a channel, an object and a collection.</summary>
    private const string Chat = """
        using System.Threading.Tasks;
        using NexNet;
        using NexNet.Collections;
        using NexNet.Collections.Lists;
        using NexNet.Pipes;
        using NexNet.Serialization;

        namespace Chat;

        [NexusObject]
        public partial class Message
        {
            [NexusKey(0)] public string Text { get; set; } = "";
            [NexusKey(1)] public long Sent { get; set; }
        }

        public interface IChatServer
        {
            ValueTask<int> Send(Message message);
            ValueTask Attach(INexusDuplexPipe pipe);
            ValueTask Log(INexusDuplexChannel<Message> channel);

            [NexusCollection(NexusCollectionMode.ServerToClient)]
            INexusList<string> Users { get; }
        }

        public interface IChatClient
        {
            void Received(Message message);
        }

        [Nexus<IChatServer, IChatClient>(NexusType = NexusType.Server)]
        public partial class ChatServerNexus { }

        [Nexus<IChatClient, IChatServer>(NexusType = NexusType.Client)]
        public partial class ChatClientNexus { }
        """;

    private static Dictionary<string, string> On() => new() { [InspectorSchemaEmitter.Property] = "true" };

    /// <summary>Generated sources of the real generator, by hint name.</summary>
    private static Dictionary<string, string> Generate(Dictionary<string, string>? options, params string[] sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp13);
        var compilation = CSharpGeneratorRunner.CreateCompilation(sources[0])
            .AddSyntaxTrees(sources.Skip(1).Select(s => CSharpSyntaxTree.ParseText(s, parseOptions)));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new NexusGenerator().AsSourceGenerator() },
            parseOptions: parseOptions,
            optionsProvider: new TargetTestRunner.TestOptionsProvider(options ?? new Dictionary<string, string>()));
        var result = driver.RunGenerators(compilation).GetRunResult();
        if (result.Results.FirstOrDefault(r => r.Exception != null).Exception is { } exception)
            throw new InvalidOperationException("The generator threw.", exception);

        return result.Results
            .SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString());
    }

    private static string SchemaFile(Dictionary<string, string> sources, string typeName)
        => sources.Single(p => p.Key.EndsWith(typeName + SchemaSuffix, StringComparison.Ordinal)).Value;

    /// <summary>The bytes and hash written in an emitted schema file.</summary>
    private static (byte[] Bytes, ulong Hash) Parse(string source)
    {
        var start = source.IndexOf("new byte[]", StringComparison.Ordinal);
        var end = source.IndexOf("};", start, StringComparison.Ordinal);
        var bytes = Regex.Matches(source.Substring(start, end - start), "0x([0-9A-F]{2}),")
            .Select(m => byte.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ToArray();
        var hash = ulong.Parse(Regex.Match(source, "InspectorSchemaHash => 0x([0-9A-F]{16})UL;").Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (bytes, hash);
    }

    /// <summary>The IR as JSON can hold it: source locations dropped.</summary>
    private static NexusIr WithoutLocations(NexusIr ir) => IrJson.Read(IrJson.Write(ir));

    private static ReadOnlySpan<byte> Schema<T>()
        where T : IInspectorSchemaSource
        => T.InspectorSchema;

    private static ulong SchemaHash<T>()
        where T : IInspectorSchemaSource
        => T.InspectorSchemaHash;

    private static async Task AssertSameBytes(byte[] actual, byte[] expected)
    {
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    // ------------------------------------------------------------------ writer and reader

    public static IEnumerable<string> Fixtures() => new[] { "basic", "versioned", "zoo", "nullable-tuples" };

    private static NexusIr FixtureIr(string name) => name switch
    {
        "basic" => IrTestRunner.Run(IrFixtures.Basic).Ir,
        "versioned" => IrTestRunner.Run(IrFixtures.Versioned).Ir,
        "zoo" => IrTestRunner.Run(IrFixtures.ZooTypes, IrFixtures.ZooNexus).Ir,
        _ => IrTestRunner.Run(IrFixtures.NullableTuples).Ir,
    };

    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task MsgPackIrDecodesWithNexNetReaderToTheJsonIr(string fixture)
    {
        var ir = FixtureIr(fixture);
        var bytes = IrMsgPack.Write(ir);

        var decoded = IrMsgPackDecoder.Decode(bytes);

        // Record equality compares every field; the JSON text is the same comparison in a reviewable form.
        await Assert.That(decoded).IsEqualTo(WithoutLocations(ir));
        await Assert.That(IrJson.Write(decoded)).IsEqualTo(IrJson.Write(ir));
    }

    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task MsgPackIrIsCanonical(string fixture)
    {
        var bytes = IrMsgPack.Write(FixtureIr(fixture));

        await AssertSameBytes(IrMsgPackDecoder.CanonicalCopy(bytes), bytes);
    }

    [Test]
    public async Task ZooCoversEveryShapeTheDecoderWalks()
    {
        var decoded = IrMsgPackDecoder.Decode(IrMsgPack.Write(FixtureIr("zoo")));
        var server = decoded.Exports.Single(e => e.Name == "ZooServerNexus").ServerApi;

        await Assert.That(server.Methods.Any(m => m.PipeKind == PipeKind.DuplexPipe)).IsTrue();
        await Assert.That(server.Methods.Any(m => m.PipeKind == PipeKind.Channel && m.PipeType != null)).IsTrue();
        await Assert.That(server.Methods.Any(m => m.ReturnKind == ReturnKind.TaskOf && m.ReturnType != null)).IsTrue();
        await Assert.That(server.Collections.Length).IsGreaterThan(0);
        await Assert.That(decoded.Types.OfType<ObjectDeclIr>().Any()).IsTrue();
        await Assert.That(decoded.Types.OfType<UnionDeclIr>().Any()).IsTrue();
        await Assert.That(decoded.Types.OfType<EnumDeclIr>().Any()).IsTrue();
        await Assert.That(decoded.Types.OfType<ObjectDeclIr>().SelectMany(o => o.Members).Any(m => m.DefaultLiteral != null)).IsTrue();
    }

    [Test]
    public async Task IntegerAndLengthBoundariesRoundTrip()
    {
        long[] values =
        [
            0, 1, 127, 128, 255, 256, 65535, 65536, uint.MaxValue, (long)uint.MaxValue + 1, long.MaxValue,
            -1, -32, -33, -128, -129, -32768, -32769, int.MinValue, (long)int.MinValue - 1, long.MinValue,
        ];
        var members = values.Select((v, i) => new EnumMemberIr(new string('m', i * 3), v)).ToArray();
        var decl = new EnumDeclIr(
            "Edge", "Edge", "", "Edge", EquatableArray<string>.Empty,
            new string('d', 70_000),
            PrimKind.Int64, false, new EquatableArray<EnumMemberIr>(members));
        var other = new EnumDeclIr(
            "Edge2", "Edge2", "", new string('n', 200), EquatableArray<string>.Empty, new string('\u00e9', 40) + "\ud83d\ude00",
            PrimKind.Byte, true, EquatableArray<EnumMemberIr>.Empty);
        var ir = new NexusIr(NexusIr.CurrentVersion, EquatableArray<ExportIr>.Empty, new EquatableArray<TypeDeclIr>(new TypeDeclIr[] { decl, other }));

        var bytes = IrMsgPack.Write(ir);

        await Assert.That(IrMsgPackDecoder.Decode(bytes)).IsEqualTo(ir);
        await AssertSameBytes(IrMsgPackDecoder.CanonicalCopy(bytes), bytes);
    }

    [Test]
    public async Task HashIsFnv1a64()
    {
        await Assert.That(IrMsgPack.Hash([])).IsEqualTo(0xcbf29ce484222325UL);
        await Assert.That(IrMsgPack.Hash("a"u8.ToArray())).IsEqualTo(0xaf63dc4c8601ec8cUL);
        await Assert.That(IrMsgPack.Hash("foobar"u8.ToArray())).IsEqualTo(0x85944171f73967e8UL);
    }

    // ------------------------------------------------------------------ generator opt-in

    [Test]
    public Task ChatServerSchemaSnapshot()
        => IrSnapshots.Verify("chat.ChatServerNexus.InspectorSchema", SchemaFile(Generate(On(), Chat), "ChatServerNexus"), ".g.cs.txt");

    [Test]
    public async Task PropertyOffEmitsExactlyTodaysOutput()
    {
        var unset = Generate(null, IrFixtures.ZooTypes, IrFixtures.ZooNexus);
        var off = Generate(new Dictionary<string, string> { [InspectorSchemaEmitter.Property] = "false" }, IrFixtures.ZooTypes, IrFixtures.ZooNexus);
        var on = Generate(On(), IrFixtures.ZooTypes, IrFixtures.ZooNexus);

        await Assert.That(unset.Keys.Any(k => k.EndsWith(SchemaSuffix, StringComparison.Ordinal))).IsFalse();
        await Assert.That(off.Count).IsEqualTo(unset.Count);
        foreach (var (hint, text) in unset)
            await Assert.That(off[hint]).IsEqualTo(text);

        // On adds one file per generated nexus (a validation error keeps the zoo server nexus from generating at all) and changes nothing else.
        var added = on.Keys.Except(unset.Keys).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var nexuses = unset.Keys.Where(k => k.EndsWith(".Nexus.g.cs", StringComparison.Ordinal))
            .Select(k => k.Replace(".Nexus.g.cs", SchemaSuffix))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        await Assert.That(nexuses.Length).IsGreaterThan(0);
        await Assert.That(added).IsEquivalentTo(nexuses);
        foreach (var (hint, text) in unset)
            await Assert.That(on[hint]).IsEqualTo(text);
    }

    [Test]
    public async Task EmittedSchemaIsTheNexusPairIr()
    {
        var full = WithoutLocations(FixtureIr("zoo"));
        var sources = Generate(On(), IrFixtures.ZooTypes, IrFixtures.ZooNexus);

        // The client nexus reaches every zoo type through the server API it calls.
        foreach (var nexus in new[] { "ZooClientNexus" })
        {
            var (bytes, hash) = Parse(SchemaFile(sources, nexus));
            await Assert.That(hash).IsEqualTo(IrMsgPack.Hash(bytes));

            var schema = IrMsgPackDecoder.Decode(bytes);
            await Assert.That(schema.IrVersion).IsEqualTo(NexusIr.CurrentVersion);
            await Assert.That(schema.Exports.Single()).IsEqualTo(full.Exports.Single(e => e.Name == nexus));

            // Every type the pair reaches, as the compilation's IR describes it.
            foreach (var decl in schema.Types)
                await Assert.That(decl).IsEqualTo(full.Types.Single(t => t.Id == decl.Id));
            await Assert.That(schema.Types.Any(t => t is UnionDeclIr)).IsTrue();
        }
    }

    [Test]
    public async Task SchemaIsDeterministic()
    {
        var first = Generate(On(), IrFixtures.ZooTypes, IrFixtures.ZooNexus, Chat);
        var second = Generate(On(), IrFixtures.ZooTypes, IrFixtures.ZooNexus, Chat);
        var reordered = Generate(On(), Chat, IrFixtures.ZooNexus, IrFixtures.ZooTypes);

        foreach (var nexus in new[] { "ZooClientNexus", "ChatServerNexus", "ChatClientNexus" })
        {
            await Assert.That(SchemaFile(second, nexus)).IsEqualTo(SchemaFile(first, nexus));
            await Assert.That(SchemaFile(reordered, nexus)).IsEqualTo(SchemaFile(first, nexus));
        }

        await AssertSameBytes(IrMsgPack.Write(FixtureIr("zoo")), IrMsgPack.Write(FixtureIr("zoo")));
    }

    [Test]
    public async Task BlockedNexusGetsNoSchema()
    {
        // Not partial: the nexus file is not generated (NEXNET001), so neither is its schema.
        var sources = Generate(On(), """
            using System.Threading.Tasks;
            using NexNet;
            namespace Blocked;
            public interface IServer { ValueTask Run(); }
            public interface IClient { }
            [Nexus<IServer, IClient>(NexusType = NexusType.Server)]
            public class ServerNexus { }
            """);

        await Assert.That(sources.Keys.Any(k => k.EndsWith(SchemaSuffix, StringComparison.Ordinal))).IsFalse();
    }

    // ------------------------------------------------------------------ compiled into this project

    [Test]
    public async Task StaticAbstractSchemaIsReachableThroughGenericConstraint()
    {
        var bytes = Schema<SchemaServerNexus>().ToArray();
        await Assert.That(SchemaHash<SchemaServerNexus>()).IsEqualTo(IrMsgPack.Hash(bytes));

        var export = IrMsgPackDecoder.Decode(bytes).Exports.Single();
        await Assert.That(export.Name).IsEqualTo(nameof(SchemaServerNexus));
        await Assert.That(export.ServerApi.Methods.Select(m => m.Name).ToArray()).IsEquivalentTo(new[] { "Add", "Upload", "Lines" });
        await Assert.That(export.ServerApi.Collections.Single().Name).IsEqualTo("Names");
        await Assert.That(export.ClientApi.Methods.Single().Name).IsEqualTo("Notify");

        // The client nexus describes the same pair seen from the client.
        var client = IrMsgPackDecoder.Decode(Schema<SchemaClientNexus>().ToArray()).Exports.Single();
        await Assert.That(IrJson.WriteExport(client with { Name = "", Docs = null })).IsEqualTo(IrJson.WriteExport(export with { Name = "", Docs = null }));
        await Assert.That(SchemaHash<SchemaClientNexus>()).IsNotEqualTo(SchemaHash<SchemaServerNexus>());
    }
}
