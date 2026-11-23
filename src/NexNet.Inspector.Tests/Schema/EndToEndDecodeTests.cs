using System.Net.Sockets;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Pipes;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNet.Inspector.Tests.Schema;

/// <summary>
/// A real server and client over a Unix domain socket, both tapped. Each side's capture is parsed, assembled and
/// decoded with the nexus's own generated schema.
/// </summary>
[NotInParallel(nameof(EndToEndDecodeTests))]
public class EndToEndDecodeTests
{
    private static readonly Lazy<Task<Capture>> Shared = new(RunSessionAsync);

    [Test]
    public async Task Client_side_arguments_and_results_decode()
    {
        var capture = await Shared.Value;
        var replay = Replay(capture.Client, SchemaKit.Server, NntlSessionRole.Client);
        var decoder = replay.Decoder;

        var add = Single(replay, "Add");
        var args = decoder.DecodeArguments(add);
        await Assert.That(args.Problem).IsEqualTo(DecodeProblem.None);
        await Assert.That(SchemaKit.CheckCoverage(args)).IsNull();
        await Assert.That(SchemaKit.At(args, "a").Value.Int64).IsEqualTo(1);
        await Assert.That(SchemaKit.At(args, "b").Value.Int64).IsEqualTo(2);
        await Assert.That(SchemaKit.At(args, "c").Value.UInt64).IsEqualTo(3UL);
        var sum = decoder.DecodeResult(add);
        await Assert.That(sum.Fields[0].Value.Int64).IsEqualTo(6);

        var describe = decoder.DecodeArguments(Single(replay, "Describe"));
        await Assert.That(describe.GetText(SchemaKit.At(describe, "text"))).IsEqualTo("text");
        await Assert.That(SchemaKit.At(describe, "ratio").Value.Double).IsEqualTo(0.5);
        await Assert.That(SchemaKit.At(describe, "flag").Value.Boolean).IsTrue();
        await Assert.That(SchemaKit.At(describe, "when").Value.Int64).IsEqualTo(When.ToBinary());
        await Assert.That(describe.FormatValue(SchemaKit.At(describe, "price"))).IsEqualTo("12.34");
        var description = decoder.DecodeResult(Single(replay, "Describe"));
        await Assert.That(description.Fields[0].Kind).IsEqualTo(FieldKind.String);

        var store = Single(replay, "Store");
        var item = decoder.DecodeArguments(store);
        await Assert.That(item.Problem).IsEqualTo(DecodeProblem.None);
        await Assert.That(item.GetText(SchemaKit.At(item, "item", "Name"))).IsEqualTo("it");
        await Assert.That(SchemaKit.At(item, "item", "Shape", "DecodeCircle", "Radius").Value.Double).IsEqualTo(3.0);
        await Assert.That(item.FormatValue(SchemaKit.At(item, "item", "Flags"))).IsEqualTo("A | B");
        var stored = decoder.DecodeResult(store);
        await Assert.That(stored.GetText(SchemaKit.At(stored, "Name"))).IsEqualTo("hello it");

        // The server called the client during Store: decoded with the client API.
        var greet = replay.Assembler.Invocations.ToArray().Single(i => i.Direction == TapDirection.Inbound && i.RequestParsed);
        await Assert.That(decoder.TryGetMethod(greet, out var greetMethod)).IsTrue();
        await Assert.That(greetMethod!.Name).IsEqualTo("Greet");
        var greeted = decoder.DecodeArguments(greet);
        await Assert.That(greeted.GetText(SchemaKit.At(greeted, "item", "Name"))).IsEqualTo("it");
        var greetResult = decoder.DecodeResult(greet);
        await Assert.That(greetResult.GetText(greetResult.Fields[0])).IsEqualTo("hello it");

        var reshaped = decoder.DecodeResult(Single(replay, "Reshape"));
        await Assert.That(reshaped.Fields[0].Kind).IsEqualTo(FieldKind.Union);
        await Assert.That(reshaped.Fields[0].Value.Int64).IsEqualTo(1);
        var square = SchemaKit.At(reshaped, "DecodeSquare");
        await Assert.That(SchemaKit.Child(reshaped, square, "Side").Value.Int64).IsEqualTo(3);
        await Assert.That(SchemaKit.Children(reshaped, square)[1].Kind).IsEqualTo(FieldKind.Nil);
        await Assert.That(reshaped.GetText(SchemaKit.Child(reshaped, square, "Label"))).IsEqualTo("from circle");

        var count = decoder.DecodeArguments(Single(replay, "Count"));
        await Assert.That(SchemaKit.At(count, "words").ChildCount).IsEqualTo(3);
        await Assert.That(SchemaKit.At(count, "numbers").Kind).IsEqualTo(FieldKind.PrimitiveArray);
        await Assert.That(SchemaKit.At(count, "set").Kind).IsEqualTo(FieldKind.Sequence);
        var counted = decoder.DecodeResult(Single(replay, "Count"));
        await Assert.That(counted.Fields[0].Kind).IsEqualTo(FieldKind.Map);
        await Assert.That(counted.Fields[0].ChildCount).IsEqualTo(2);
        await Assert.That(SchemaKit.CheckCoverage(counted)).IsNull();

        var maybes = Calls(replay, "Maybe");
        var some = decoder.DecodeArguments(maybes[0]);
        await Assert.That(SchemaKit.At(some, "value").Value.Int64).IsEqualTo(5);
        await Assert.That(some.FormatValue(SchemaKit.At(some, "color"))).IsEqualTo("Blue");
        var none = decoder.DecodeArguments(maybes[1]);
        await Assert.That(SchemaKit.At(none, "value").Kind).IsEqualTo(FieldKind.Nil);
        await Assert.That(SchemaKit.At(none, "color").Kind).IsEqualTo(FieldKind.Nil);
        await Assert.That(decoder.DecodeResult(maybes[1]).Fields[0].Kind).IsEqualTo(FieldKind.Nil);

        var pair = decoder.DecodeArguments(Single(replay, "Pair"));
        await Assert.That(SchemaKit.At(pair, "pair", "Item1").Value.Int64).IsEqualTo(1);
        await Assert.That(pair.GetText(SchemaKit.At(pair, "entry", "Key"))).IsEqualTo("k");
        var paired = decoder.DecodeResult(Single(replay, "Pair"));
        await Assert.That(paired.GetText(SchemaKit.At(paired, "Item2"))).IsEqualTo("onek");

        var wrap = decoder.DecodeArguments(Single(replay, "Wrap"));
        await Assert.That(SchemaKit.At(wrap, "box", "Value").Value.Int64).IsEqualTo(4);
        await Assert.That(SchemaKit.At(wrap, "box", "More").Kind).IsEqualTo(FieldKind.PrimitiveArray);
        var wrapped = decoder.DecodeResult(Single(replay, "Wrap"));
        await Assert.That(SchemaKit.At(wrapped, "Value", "Id").Value.Int64).IsEqualTo(4);

        var wait = Single(replay, "Wait");
        await Assert.That(wait.CancellationSeq).IsGreaterThanOrEqualTo(0);
        var waitArgs = decoder.DecodeArguments(wait);
        await Assert.That(waitArgs.Fields[0].ChildCount).IsEqualTo(1);
        await Assert.That(SchemaKit.At(waitArgs, "milliseconds").Value.Int64).IsEqualTo(10_000);

        var fail = Single(replay, "Fail");
        await Assert.That(fail.State).IsEqualTo(InvocationState.Exception);
        var detail = decoder.DecodeResult(fail);
        await Assert.That(detail.Fields[0].Kind).IsEqualTo(FieldKind.ExceptionDetail);
        await Assert.That(SchemaKit.At(detail, "Code").Value.Int64).IsEqualTo(42);
        await Assert.That(detail.GetText(SchemaKit.At(detail, "Message"))).IsEqualTo("boom");

        // Every invocation with captured bytes decodes without a problem.
        foreach (var invocation in replay.Assembler.Invocations.ToArray())
        {
            await Assert.That(decoder.DecodeArguments(invocation).Problem).IsEqualTo(DecodeProblem.None);
            await Assert.That(decoder.DecodeResult(invocation).Problem).IsEqualTo(DecodeProblem.None);
        }
    }

    [Test]
    public async Task Pipes_link_exactly_to_their_opener()
    {
        var capture = await Shared.Value;
        foreach (var (session, role) in new[] { (capture.Client, NntlSessionRole.Client), (capture.Server, NntlSessionRole.Server) })
        {
            var replay = Replay(session, SchemaKit.Server, role);
            var uploads = Calls(replay, "Upload");
            await Assert.That(uploads.Length).IsEqualTo(2);
            foreach (var upload in uploads)
            {
                var args = replay.Decoder.DecodeArguments(upload);
                var pipeId = (byte)SchemaKit.At(args, "pipe").Value.Int64;
                var pipe = replay.Assembler.Pipes.ToArray().Single(p => p.OpenerInvocation == upload.Index);
                await Assert.That((byte)pipe.PipeId).IsEqualTo(pipeId).Because(role.ToString());
                await Assert.That(pipe.Content).IsEqualTo(PipeContent.Raw);
                await Assert.That(upload.Pipe).IsEqualTo(pipe.Index);
                await Assert.That(pipe.OutboundBytes + pipe.InboundBytes).IsEqualTo(UploadLength);
            }

            var stream = Single(replay, "Stream");
            var channel = replay.Assembler.Pipes[stream.Pipe];
            await Assert.That(channel.Content).IsEqualTo(PipeContent.Channel);
            var collection = replay.Assembler.Collections.ToArray().Single();
            await Assert.That(replay.Assembler.Pipes[collection.Pipe].Content).IsEqualTo(PipeContent.Collection);
            await Assert.That(replay.Assembler.Pipes.ToArray().All(p => p.OpenerInvocation >= 0)).IsTrue();
        }
    }

    [Test]
    public async Task Without_a_schema_bodies_stay_raw_and_openers_are_guessed()
    {
        var capture = await Shared.Value;
        var replay = Replay(capture.Client, null, NntlSessionRole.Client);
        var add = SchemaKit.Method("Add");
        var invocation = replay.Assembler.Invocations.ToArray().First(i => i.RequestParsed && i.MethodId == add.Id && i.Direction == TapDirection.Outbound);
        var raw = replay.Decoder.DecodeArguments(invocation);
        await Assert.That(raw.IsRaw).IsTrue();
        await Assert.That(raw.Problem).IsEqualTo(DecodeProblem.NoSchema);

        // The first Upload sent tag 2 and pipe 1; the second sent tag 7 and pipe 2. Guessing from the integers links the
        // second pipe to the first Upload, which still holds the candidate 2; the schema says only argument 1 is a pipe.
        var uploadId = SchemaKit.Method("Upload").Id;
        var uploads = replay.Assembler.Invocations.ToArray().Where(i => i.RequestParsed && i.MethodId == uploadId).ToArray();
        var secondPipe = replay.Assembler.Pipes.ToArray().Single(p => (byte)p.PipeId == 2 && p.OpenSeq >= 0 && p.OpenerDirection == TapDirection.Outbound);
        await Assert.That(secondPipe.OpenerInvocation).IsEqualTo(uploads[0].Index);

        var exact = Replay(capture.Client, SchemaKit.Server, NntlSessionRole.Client);
        var exactPipe = exact.Assembler.Pipes[secondPipe.Index];
        await Assert.That(exactPipe.OpenerInvocation).IsEqualTo(uploads[1].Index);
    }

    [Test]
    public async Task Channel_items_and_collection_messages_decode()
    {
        var capture = await Shared.Value;
        var replay = Replay(capture.Client, SchemaKit.Server, NntlSessionRole.Client);
        var stream = Single(replay, "Stream");
        var items = replay.Assembler.ChannelItems.ToArray().Where(i => i.Pipe == stream.Pipe).ToArray();
        await Assert.That(items.Length).IsEqualTo(StreamedNames.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var value = replay.Decoder.DecodeChannelItem(items[i]);
            await Assert.That(value.Problem).IsEqualTo(DecodeProblem.None);
            await Assert.That(value.Fields[0].Decl!.Name).IsEqualTo("DecodeItem");
            await Assert.That(value.GetText(SchemaKit.At(value, "Name"))).IsEqualTo(StreamedNames[i]);
            await Assert.That(SchemaKit.CheckCoverage(value)).IsNull();
        }

        var messages = replay.Assembler.CollectionMessages.ToArray();
        await Assert.That(messages.Length).IsGreaterThanOrEqualTo(4);
        var kinds = new List<string?>();
        foreach (var message in messages)
        {
            var value = replay.Decoder.DecodeCollectionMessage(message);
            await Assert.That(value.Problem).IsEqualTo(DecodeProblem.None);
            await Assert.That(value.Fields[0].Kind).IsEqualTo(FieldKind.CollectionMessage);
            kinds.Add(value.Fields[0].Name);
            if (message.Kind == CollectionMessageKind.Insert)
                await Assert.That(value.GetText(SchemaKit.At(value, "Insert", "Value", "Name"))).IsEqualTo("listed");
        }

        await Assert.That(kinds).Contains("ResetStart");
        await Assert.That(kinds).Contains("ResetComplete");
        await Assert.That(messages.Count(m => m.Kind == CollectionMessageKind.Insert && m.Direction == TapDirection.Outbound)).IsEqualTo(1);
        await Assert.That(messages.Count(m => m.Kind == CollectionMessageKind.Insert && m.Direction == TapDirection.Inbound)).IsEqualTo(1);

        // The collection's connect invocation decodes as the collection with its pipe argument.
        var connect = replay.Assembler.Invocations[replay.Assembler.Pipes[replay.Assembler.Collections[0].Pipe].OpenerInvocation];
        var connectArgs = replay.Decoder.DecodeArguments(connect);
        await Assert.That(connectArgs.Fields[0].Name).IsEqualTo("Items");
        await Assert.That(SchemaKit.At(connectArgs, "pipe").Kind).IsEqualTo(FieldKind.PipeId);
    }

    [Test]
    public async Task Server_side_decodes_with_the_opposite_role()
    {
        var capture = await Shared.Value;
        var replay = Replay(capture.Server, SchemaKit.Server, NntlSessionRole.Server);
        var add = Single(replay, "Add");
        await Assert.That(add.Direction).IsEqualTo(TapDirection.Inbound);
        await Assert.That(SchemaKit.At(replay.Decoder.DecodeArguments(add), "b").Value.Int64).IsEqualTo(2);

        var greet = replay.Assembler.Invocations.ToArray().Single(i => i.Direction == TapDirection.Outbound && i.RequestParsed);
        await Assert.That(replay.Decoder.TryGetMethod(greet, out var method)).IsTrue();
        await Assert.That(method!.Name).IsEqualTo("Greet");

        // Inferred from the greetings when the role is not given.
        var inferred = Replay(capture.Server, SchemaKit.Server, role: null);
        await Assert.That(inferred.Assembler.Role).IsEqualTo(NntlSessionRole.Server);
        await Assert.That(inferred.Decoder.DecodeArguments(Single(inferred, "Add")).Problem).IsEqualTo(DecodeProblem.None);
    }

    private const int UploadLength = 1000;
    private static readonly DateTime When = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly string[] StreamedNames = ["one", "two", "three"];

    private sealed record Capture(CapturedSession Client, CapturedSession Server);

    private static SessionReplay Replay(CapturedSession session, NexusSchema? schema, NntlSessionRole? role)
    {
        var replay = new SessionReplay(schema, role);
        foreach (var (direction, bytes) in session.Ops)
            replay.Push(direction, bytes);
        return replay.Complete();
    }

    private static InvocationRow[] Calls(SessionReplay replay, string name)
    {
        var id = SchemaKit.Method(name).Id;
        return replay.Assembler.Invocations.ToArray()
            .Where(i => i.RequestParsed && i.MethodId == id && replay.Decoder.IsClientCall(i) == true)
            .ToArray();
    }

    private static InvocationRow Single(SessionReplay replay, string name) => Calls(replay, name).Single();

    private static async Task<Capture> RunSessionAsync()
    {
        var directory = Directory.CreateTempSubdirectory("nni");
        var endPoint = new UnixDomainSocketEndPoint(Path.Combine(directory.FullName, "s"));
        var serverTap = new CaptureTap();
        var clientTap = new CaptureTap();
        var server = DecodeServerNexus.CreateServer(new UdsServerConfig { EndPoint = endPoint, Tap = serverTap }, () => new DecodeServerNexus());
        var client = DecodeClientNexus.CreateClient(new UdsClientConfig { EndPoint = endPoint, Tap = clientTap }, new DecodeClientNexus());
        var timeout = TimeSpan.FromSeconds(20);

        try
        {
            await server.StartAsync().WaitAsync(timeout);
            await client.ConnectAsync().WaitAsync(timeout);
            var proxy = client.Proxy;

            // Pipes first, so their local IDs are 1 and 2 (allocation is round-robin from 1).
            foreach (var tag in new[] { 2, 7 })
            {
                await using var pipe = client.CreatePipe();
                var before = DecodeServerNexus.Uploaded.Count;
                await proxy.Upload(tag, pipe).AsTask().WaitAsync(timeout);
                await pipe.ReadyTask.WaitAsync(timeout);
                await pipe.Output.WriteAsync(new byte[UploadLength]).AsTask().WaitAsync(timeout);
                await pipe.Output.CompleteAsync();
                await WaitUntil(() => DecodeServerNexus.Uploaded.Count > before, timeout);
                await pipe.CompleteTask.WaitAsync(timeout);
            }

            await proxy.Add(1, 2, 3).AsTask().WaitAsync(timeout);
            await proxy.Describe("text", 0.5, true, When, 12.34m).AsTask().WaitAsync(timeout);
            var item = SchemaDecoderTests.SampleItem();
            item.Name = "it";
            item.Shape = new DecodeCircle { Radius = 3 };
            await proxy.Store(item).AsTask().WaitAsync(timeout);
            await proxy.Reshape(new DecodeCircle { Radius = 3 }).AsTask().WaitAsync(timeout);
            await proxy.Count(["a", "b", "a"], [1, 2, 3], ["x"]).AsTask().WaitAsync(timeout);
            await proxy.Maybe(5, DecodeColor.Blue).AsTask().WaitAsync(timeout);
            await proxy.Maybe(null, null).AsTask().WaitAsync(timeout);
            await proxy.Pair((1, "one"), new KeyValuePair<string, int>("k", 2)).AsTask().WaitAsync(timeout);
            await proxy.Wrap(new DecodeBox<int> { Value = 4, More = [5, 6] }).AsTask().WaitAsync(timeout);

            var streamedBefore = DecodeServerNexus.Streamed.Count;
            var channel = client.CreateChannel<DecodeItem>();
            await proxy.Stream(channel).AsTask().WaitAsync(timeout);
            var writer = await channel.GetWriterAsync().AsTask().WaitAsync(timeout);
            foreach (var name in StreamedNames)
                await writer.WriteAsync(new DecodeItem { Name = name, Tags = [name] }).AsTask().WaitAsync(timeout);
            await writer.CompleteAsync();
            await WaitUntil(() => DecodeServerNexus.Streamed.Count >= streamedBefore + StreamedNames.Length, timeout);

            using (var cts = new CancellationTokenSource())
            {
                var waiting = proxy.Wait(10_000, cts.Token).AsTask();
                await Task.Delay(200);
                await cts.CancelAsync();
                await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
            }

            await Assert.That(async () => await proxy.Fail("boom")).ThrowsException();

            var list = proxy.Items;
            await Assert.That(await list.EnableAsync().AsTask().WaitAsync(timeout)).IsTrue();
            await list.AddAsync(new DecodeItem { Id = 1, Name = "listed" }).WaitAsync(timeout);
            await WaitUntil(() => list.Count == 1, timeout);

            var clientSession = clientTap.Sessions.Single();
            var serverSession = serverTap.Sessions.Single();
            // The late result of the canceled call and any credit can still be in flight: wait until both sides agree.
            await WaitUntil(() => clientSession.OutboundBytes == serverSession.InboundBytes
                                  && serverSession.OutboundBytes == clientSession.InboundBytes, timeout);
            await Task.Delay(300);
            await WaitUntil(() => clientSession.OutboundBytes == serverSession.InboundBytes
                                  && serverSession.OutboundBytes == clientSession.InboundBytes, timeout);

            await client.DisconnectAsync().WaitAsync(timeout);
            await clientSession.Closed.Task.WaitAsync(timeout);
            await serverSession.Closed.Task.WaitAsync(timeout);
            return new Capture(clientSession, serverSession);
        }
        finally
        {
            await server.StopAsync();
            try
            {
                directory.Delete(true);
            }
            catch (IOException)
            {
                // Best effort: the socket file can still be held open briefly.
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }
}
