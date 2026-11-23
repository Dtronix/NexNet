using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Tap;
using NexNet.Interop;
using NexNet.Interop.Hosts;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The NexNet Inspector tap on a C# server records a session driven by each client language (I11): the server-side
/// capture parses (I02) and assembles (I03) whatever client produced it, TLS and WebSocket framing included, since the
/// tap sits above the transport.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class InspectorCaptureTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    /// <inheritdoc />
    protected override bool CompleteServerMethodsByDefault => true;

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task ServerSideCapture_ParsesAndAssembles(ClientTarget target)
    {
        // One inspector for every endpoint of the interop server, recording from each session's first byte.
        var inspector = NexusInspector.Create(new InspectorOptions { AllowRecording = true });
        var file = new MemoryStream();
        var recording = inspector.RecordTo(file, leaveOpen: true, InspectorOverflow.Block);
        await using (var server = await StartServerAsync(
                         nexus => nexus.ServerTaskValueWithParamEvent = (_, value) => ValueTask.FromResult(value + 1),
                         inspector.Install))
        {
            var client = await ConnectAsync(target, server);
            await using (client)
            {
                await Assert.That(await Proxy(client).ServerTaskValueWithParam(41).Timeout(T(1))).IsEqualTo(42);
                await EndAsync(client);
            }

            await WaitUntil(() => inspector.GetSessions().Length == 0);
        }

        await recording.StopAsync().Timeout(T(5));
        await inspector.DisposeAsync();
        await Assert.That(recording.Fault).IsNull();

        using var reader = NntlReader.Open(new MemoryStream(file.ToArray()));
        await Assert.That(reader.IndexSource).IsEqualTo(NntlIndexSource.Footer);
        var open = reader.Sessions.Single();
        await Assert.That(open.Role).IsEqualTo(NntlSessionRole.Server);

        var inbound = new InMemoryStreamBytes();
        var outbound = new InMemoryStreamBytes();
        var assembler = new MessageAssembler(inbound, outbound, NntlSessionRole.Server);
        var inboundParser = new FrameParser(TapDirection.Inbound, expectPreamble: true);
        var outboundParser = new FrameParser(TapDirection.Outbound, expectPreamble: true);
        foreach (var op in reader.ReadTransportOps(open.SessionId))
        {
            var bytes = op.Bytes.ToArray();
            if (op.Direction == TapDirection.Inbound)
            {
                inbound.Append(bytes);
                inboundParser.Push(op.OpId, op.Ticks, bytes, assembler);
            }
            else
            {
                outbound.Append(bytes);
                outboundParser.Push(op.OpId, op.Ticks, bytes, assembler);
            }
        }

        outboundParser.Complete(assembler);
        inboundParser.Complete(assembler);
        assembler.Complete();

        using (Assert.Multiple())
        {
            await Assert.That(inboundParser.IsFaulted).IsFalse();
            await Assert.That(outboundParser.IsFaulted).IsFalse();
            await Assert.That(inboundParser.HasPending).IsFalse();
            await Assert.That(outboundParser.HasPending).IsFalse();

            // Both sides open with the preamble, then their greetings.
            await Assert.That(assembler.GetFrames(TapDirection.Inbound)[0].Kind).IsEqualTo(FrameKind.Preamble);
            await Assert.That(assembler.GetFrames(TapDirection.Outbound)[0].Kind).IsEqualTo(FrameKind.Preamble);
            var greetings = assembler.Greetings.ToArray();
            await Assert.That(greetings.Count(g => g.Kind == GreetingKind.Client && g.Direction == TapDirection.Inbound)).IsEqualTo(1);
            await Assert.That(greetings.Count(g => g.Kind == GreetingKind.Server && g.Direction == TapDirection.Outbound)).IsEqualTo(1);

            // The client's call and the server's result, paired by invocation ID.
            var call = assembler.Invocations.ToArray().Single(i => i.Direction == TapDirection.Inbound && i.RequestParsed);
            await Assert.That(call.Result.IsNone).IsFalse();
            await Assert.That(call.State).IsEqualTo(InvocationState.Completed);
            await Assert.That(call.ResultState).IsEqualTo(InvocationResultState.CompletedResult);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(T(5));
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Assert.That(condition()).IsTrue();
    }
}
