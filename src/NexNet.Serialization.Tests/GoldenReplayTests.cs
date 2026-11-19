using System.Buffers;
using MessagePack;
using NexNet.GoldenVectors;
using NexNet.Internals;
using NexNet.Interop;
using NexNet.Messages;

namespace NexNet.Serialization.Tests;

/// <summary>
/// Replays every golden vector of tests/golden (format: tests/golden/README.md) against the C# implementation, the way
/// each other runtime replays them, and checks the checked-in files are what the generator produces now.
/// </summary>
public class GoldenReplayTests
{
    private const string Regenerate = "Regenerate them: dotnet run --project src/NexNet.GoldenVectors -- --out tests/golden";

    private static readonly Lazy<string> Root = new(FindRoot);
    private static readonly Lazy<GoldenSchemas> Schemas = new(() => GoldenSchemas.Parse(File.ReadAllLines(Path.Combine(GoldenDirectory, GoldenGenerator.SchemaFile))));
    private static readonly Lazy<GoldenBuilder> Generated = new(GoldenGenerator.Build);
    private static readonly Lazy<IReadOnlyDictionary<string, GoldenNative>> Natives = new(CreateNatives);

    private static string GoldenDirectory => Path.Combine(Root.Value, "tests", "golden");

    public static IEnumerable<Func<TestDataRow<GoldenVector>>> Vectors()
    {
        foreach (var vector in Load())
            yield return () => new TestDataRow<GoldenVector>(vector, DisplayName: $"Golden({vector.File}: {vector.Id})");
    }

    [Test]
    [MethodDataSource(nameof(Vectors))]
    public async Task Replay(GoldenVector vector)
    {
        switch (vector.Type)
        {
            case FrameCodec.PreambleType:
                await Assert.That(Hex(FrameCodec.EncodePreamble(vector.Value))).IsEqualTo(Hex(vector.Hex));
                await Assert.That(GoldenFormat.Canonical(FrameCodec.DecodePreamble(vector.Hex))).IsEqualTo(GoldenFormat.Canonical(vector.Value));
                return;
            case FrameCodec.FrameType:
                await ReplayFrame(vector);
                return;
        }

        var schema = Schemas.Value.Parse(vector.Type);
        await Assert.That(Natives.Value.TryGetValue(vector.Type, out var native)).IsTrue().Because("no C# implementation of " + vector.Type);
        var options = vector.SerializerOptions;
        switch (vector.Mode)
        {
            case GoldenMode.Both:
                // The value model encodes the value to the bytes and reads the bytes back to the value ...
                await Assert.That(Hex(TypedJson.ToMsgPack(vector.Value, schema, Schemas.Value.Set))).IsEqualTo(Hex(vector.Hex));
                await Assert.That(GoldenFormat.Canonical(TypedJson.FromMsgPack(vector.Hex, schema, Schemas.Value.Set))).IsEqualTo(GoldenFormat.Canonical(vector.Value));

                // ... and the C# formatter reads the bytes and writes them again.
                await Assert.That(Hex(native!.Reencode(vector.Hex, options))).IsEqualTo(Hex(vector.Hex));
                if (Oracles.TryGetValue(vector.Type, out var oracle))
                    await Assert.That(Hex(oracle(vector.Hex))).IsEqualTo(Hex(vector.Hex)).Because("MessagePack-CSharp");
                break;
            case GoldenMode.Decode:
            {
                var canonical = TypedJson.ToMsgPack(vector.Value, schema, Schemas.Value.Set);
                await Assert.That(GoldenFormat.Canonical(TypedJson.FromMsgPack(canonical, schema, Schemas.Value.Set))).IsEqualTo(GoldenFormat.Canonical(vector.Value))
                    .Because("the value of a decode vector is canonical");
                await Assert.That(Hex(native!.Reencode(vector.Hex, options))).IsEqualTo(Hex(canonical));
                break;
            }
            case GoldenMode.Reject:
                await Assert.That(() => native!.Reencode(vector.Hex, options)).ThrowsExactly<NexusSerializationException>();
                break;
        }
    }

    private static async Task ReplayFrame(GoldenVector vector)
    {
        await Assert.That(Hex(FrameCodec.Encode(vector.Value, Schemas.Value))).IsEqualTo(Hex(vector.Hex));
        await Assert.That(GoldenFormat.Canonical(FrameCodec.Decode(vector.Hex, Schemas.Value))).IsEqualTo(GoldenFormat.Canonical(vector.Value));

        var type = vector.Hex[0];
        if (FrameCodec.HasNoBody(type))
        {
            await Assert.That(type is 1 or >= 20 and <= 38).IsTrue();
            return;
        }

        // A message frame is what the C# session writes for the message parsed from its body.
        if (Schemas.Value.FindMessage(type) is { } message && Natives.Value[message.Id] is IMessageNative framer)
            await Assert.That(Hex(framer.Frame(vector.Hex.AsMemory(3)))).IsEqualTo(Hex(vector.Hex));
        else
            await Assert.That(Enum.IsDefined((MessageType)type)).IsTrue();
    }

    [Test]
    public async Task CheckedInVectorsAreCurrent()
    {
        var temp = Directory.CreateTempSubdirectory("nexnet-golden-");
        try
        {
            var corpus = Path.Combine(temp.FullName, "golden.hex");
            GoldenGenerator.Write(temp.FullName, corpus);
            foreach (var generated in Directory.EnumerateFiles(temp.FullName, "*.jsonl").Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(generated);
                var checkedIn = Path.Combine(GoldenDirectory, name);
                await Assert.That(File.Exists(checkedIn)).IsTrue().Because($"tests/golden/{name} is missing. {Regenerate}");
                await Assert.That(Normalize(await File.ReadAllTextAsync(checkedIn)) == Normalize(await File.ReadAllTextAsync(generated))).IsTrue()
                    .Because($"tests/golden/{name} is stale. {Regenerate}");
            }

            foreach (var checkedIn in Directory.EnumerateFiles(GoldenDirectory, "*.jsonl"))
            {
                await Assert.That(File.Exists(Path.Combine(temp.FullName, Path.GetFileName(checkedIn)))).IsTrue()
                    .Because($"tests/golden/{Path.GetFileName(checkedIn)} is not generated. {Regenerate}");
            }

            var checkedInCorpus = Path.Combine(Root.Value, GoldenGenerator.CorpusPath);
            await Assert.That(Normalize(await File.ReadAllTextAsync(checkedInCorpus)) == Normalize(await File.ReadAllTextAsync(corpus))).IsTrue()
                .Because($"src/NexNet.Fuzz/Corpus/golden.hex is stale. {Regenerate}");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task EveryFileIsReplayed()
    {
        var vectors = Load().ToList();
        await Assert.That(vectors.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(vectors.Count).Because("ids are unique");
        foreach (var file in GoldenGenerator.Files)
            await Assert.That(vectors.Any(v => v.File == file)).IsTrue().Because(file + ".jsonl has vectors");
        await Assert.That(vectors.Count(v => v.Mode == GoldenMode.Reject)).IsEqualTo(vectors.Count(v => v.File == GoldenBuilder.RejectFile));
    }

    [Test]
    public async Task ClientGreetingMatchesTheProtocolMessageTests()
    {
        var vector = Load().Single(v => v.Id == "msg.client-greeting");
        await Assert.That(Hex(vector.Hex)).IsEqualTo("96" + "a27631" + "cd012c" + "fb" + "c4020102" + "ce00400000" + "20");
    }

    private static IEnumerable<GoldenVector> Load()
    {
        foreach (var file in GoldenGenerator.Files)
        {
            foreach (var line in File.ReadLines(Path.Combine(GoldenDirectory, file + ".jsonl")))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    yield return GoldenFormat.Read(file, line);
            }
        }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tests", "golden", "README.md")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("No tests/golden above " + AppContext.BaseDirectory + ".");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Hex(byte[] data) => Convert.ToHexStringLower(data);

    // ------------------------------------------------------------------ C# implementations

    /// <summary>The formatters of the generated vectors plus the message codecs, which are internal to NexNet.</summary>
    private static IReadOnlyDictionary<string, GoldenNative> CreateNatives()
    {
        var natives = new Dictionary<string, GoldenNative>(Generated.Value.Natives, StringComparer.Ordinal)
        {
            ["ClientGreeting"] = new MessageNative<ClientGreetingMessage>(),
            ["ClientGreetingReconnection"] = new MessageNative<ClientGreetingReconnectionMessage>(),
            ["ServerGreeting"] = new MessageNative<ServerGreetingMessage>(),
            ["Invocation"] = new MessageNative<InvocationMessage>(),
            ["InvocationCancellation"] = new MessageNative<InvocationCancellationMessage>(),
            ["InvocationResult"] = new MessageNative<InvocationResultMessage>(),
            ["MessageStart"] = new MessageNative<MessageStartMessage>(),
            ["MessageAbort"] = new MessageNative<MessageAbortMessage>(),
            ["InvocationCredit"] = new MessageNative<InvocationCreditMessage>(),
            ["DuplexPipeUpdateState"] = new MessageNative<DuplexPipeUpdateStateMessage>(),
            ["RemoteErrorDetail"] = new RemoteErrorDetailNative(),
        };
        return natives;
    }

    private interface IMessageNative
    {
        /// <summary>The frame the C# session writes for the message in <paramref name="body"/>.</summary>
        byte[] Frame(ReadOnlyMemory<byte> body);
    }

    private sealed class MessageNative<T> : GoldenNative, IMessageNative
        where T : class, IMessageBase, new()
    {
        public override Type ClrType => typeof(T);

        public override byte[] Reencode(ReadOnlyMemory<byte> data, NexusSerializerOptions options)
            => TestHelpers.Write((ref MsgPackWriter w) => Parse(data, options).Serialize(ref w));

        public byte[] Frame(ReadOnlyMemory<byte> body)
        {
            var output = PooledArrayBufferWriter.Rent();
            try
            {
                Check(MessageFrame.TryWrite(Parse(body, NexusSerializerOptions.Untrusted), output), "The message is larger than one frame.");
                return output.WrittenSpan.ToArray();
            }
            finally
            {
                output.Return();
            }
        }

        private static T Parse(ReadOnlyMemory<byte> data, NexusSerializerOptions options)
        {
            var message = new T();
            var reader = new MsgPackReader(data, options);
            message.Deserialize(ref reader);
            if (!reader.End)
                throw TrailingBytes(typeof(T));
            return message;
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }

    private sealed class RemoteErrorDetailNative : GoldenNative
    {
        public override Type ClrType => typeof(RemoteErrorDetail);

        public override byte[] Reencode(ReadOnlyMemory<byte> data, NexusSerializerOptions options)
        {
            if (!RemoteErrorDetail.TryRead(new ReadOnlySequence<byte>(data), options, out var code, out var type, out var message))
                throw new NexusSerializationException("Not a valid remote error detail.");
            return TestHelpers.Write((ref MsgPackWriter w) => RemoteErrorDetail.Write(ref w, code, type, message));
        }
    }

    // ------------------------------------------------------------------ MessagePack-CSharp cross-check

    /// <summary>Types MessagePack-CSharp encodes the same way: it reads the bytes of a both vector and writes them again.</summary>
    private static readonly Dictionary<string, Func<byte[], byte[]>> Oracles = new(StringComparer.Ordinal)
    {
        ["bool"] = Oracle<bool>,
        ["sbyte"] = Oracle<sbyte>,
        ["byte"] = Oracle<byte>,
        ["short"] = Oracle<short>,
        ["ushort"] = Oracle<ushort>,
        ["int"] = Oracle<int>,
        ["uint"] = Oracle<uint>,
        ["long"] = Oracle<long>,
        ["ulong"] = Oracle<ulong>,
        ["float"] = Oracle<float>,
        ["double"] = Oracle<double>,
        ["char"] = Oracle<char>,
        ["string"] = Oracle<string?>,
        ["byte[]"] = Oracle<byte[]?>,
        ["string[]"] = Oracle<string?[]?>,
        ["List<string>"] = Oracle<List<string>>,
        ["Dictionary<string,int>"] = Oracle<Dictionary<string, int>?>,
        ["Dictionary<string,string>"] = Oracle<Dictionary<string, string?>>,
        ["(int,string,double)"] = Oracle<(int, string, double)>,
        ["KeyValuePair<string,int>"] = Oracle<KeyValuePair<string, int>>,
    };

    private static byte[] Oracle<T>(byte[] data) => MessagePackSerializer.Serialize(MessagePackSerializer.Deserialize<T>(data));
}
