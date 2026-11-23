using System.Buffers;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Tests.Tap;

/// <summary>Server-config recording (RecordTo) and auth-token redaction over real connections.</summary>
public class TapRecordingTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly byte[] Token = "client-auth-token-0123456789-abcdefghij"u8.ToArray();

    [Test]
    public async Task RecordTo_captures_current_and_future_sessions_and_survives_a_crash()
    {
        await using var h = new TapHarness(TapTransport.Tcp, new InspectorOptions { AllowRecording = true });
        await h.StartAsync();
        var first = await h.ConnectClientAsync();
        var current = await h.WaitForSessionAsync();

        var file = new SnapshotStream();
        var recording = h.Inspector.RecordTo(file, leaveOpen: true, InspectorOverflow.Block);
        var second = await h.ConnectClientAsync();
        var joined = await h.WaitForSessionAsync(2);
        await first.Proxy.Echo([1, 1]);
        await second.Proxy.Echo([2, 2]);

        // A crash: the recording as it stands, no footer.
        await WaitForBlocksAsync(file, joined.Id);
        var crashed = file.Copy();

        await second.DisconnectAsync();
        await recording.StopAsync().WaitAsync(Wait);
        await Assert.That(recording.Fault).IsNull();

        foreach (var (bytes, complete) in new[] { (file.Copy(), true), (crashed, false) })
        {
            var capture = TapCapture.FromRecording(bytes, out var reader);
            using (reader)
            {
                await Assert.That(reader.IndexSource).IsEqualTo(complete ? NntlIndexSource.Footer : NntlIndexSource.Scan);
                await Assert.That(reader.Damage.All(d => d.Kind == NntlDamageKind.TornTail)).IsTrue();
                await Assert.That(reader.Sessions.Select(s => s.SessionId)).IsEquivalentTo(new[] { current.Id, joined.Id });
                await Assert.That(reader.Sessions.All(s => s.SchemaHash.Length == SchemaHash.Size)).IsTrue();
            }

            // The session that was already connected starts with a snapshot; the new one from its first byte.
            var currentSnapshot = capture.Blocks.Where(b => b.Kind == NntlBlockKind.Snapshot)
                .Select(b => NntlCodec.DecodeSnapshot(b.Body)).Single();
            await Assert.That(currentSnapshot.SessionId).IsEqualTo(current.Id);
            var inbound = capture.Ops(joined.Id, TapDirection.Inbound).SelectMany(o => o.Bytes.ToArray()).ToArray();
            var outbound = capture.Ops(joined.Id, TapDirection.Outbound).SelectMany(o => o.Bytes.ToArray()).ToArray();
            await Assert.That(inbound.Take(8).ToArray()).IsEquivalentTo(TapTestData.Preamble);
            await Assert.That(outbound.Take(8).ToArray()).IsEquivalentTo(TapTestData.Preamble);

            var assembled = new AssembledSession(capture.Blocks.Where(b => b.Kind == NntlBlockKind.TransportOp)
                .Select(b => NntlCodec.DecodeTransportOp(b.Body)).Where(o => o.SessionId == joined.Id),
                NntlSessionRole.Server, fromInception: true);
            await Assert.That(assembled.InboundParser.IsFaulted).IsFalse();
            await Assert.That(assembled.Assembler.Greetings.Length).IsEqualTo(2);
            await Assert.That(assembled.Assembler.Invocations.Length).IsGreaterThanOrEqualTo(1);
        }

        var full = TapCapture.FromRecording(file.Copy(), out var fullReader);
        fullReader.Dispose();
        var closes = full.Blocks.Where(b => b.Kind == NntlBlockKind.SessionClose)
            .Select(b => NntlCodec.DecodeSessionClose(b.Body)).ToDictionary(c => c.SessionId);
        await Assert.That(closes[joined.Id].Kind).IsEqualTo(NntlCloseKind.Disconnected);
        await Assert.That(closes[current.Id].Kind).IsEqualTo(NntlCloseKind.RecordingStopped);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task The_greeting_token_is_redacted_unless_secrets_are_included(bool includeSecrets)
    {
        await using var h = new TapHarness(TapTransport.Tcp,
            new InspectorOptions { AllowRecording = true, IncludeSecrets = includeSecrets }, authenticate: true);
        var file = new MemoryStream();
        var recording = h.Inspector.RecordTo(file, leaveOpen: true, InspectorOverflow.Block);
        await h.StartAsync();
        var client = await h.ConnectClientAsync(Token);
        var session = await h.WaitForSessionAsync();
        await client.Proxy.Echo([9]);
        await client.DisconnectAsync();
        await recording.StopAsync().WaitAsync(Wait);

        var bytes = file.ToArray();
        await Assert.That(TapTestData.IndexOf(bytes, Token) >= 0).IsEqualTo(includeSecrets);

        var capture = TapCapture.FromRecording(bytes, out var reader);
        reader.Dispose();
        var ops = capture.Ops(session.Id, TapDirection.Inbound).ToArray();
        var assembled = new AssembledSession(ops, NntlSessionRole.Server, fromInception: true);
        var greeting = assembled.Assembler.Greetings.ToArray().Single(g => g.Direction == TapDirection.Inbound);
        await Assert.That(assembled.Assembler.TryGetPayload(greeting.Token, out var tokenBytes)).IsTrue();
        var expected = includeSecrets ? Token : TapTestData.Marker(Token);
        await Assert.That(tokenBytes.ToArray()).IsEquivalentTo(expected);

        // The flagged ranges are exactly the token's bytes in the stream.
        var flagged = new List<long>();
        long offset = 0;
        foreach (var op in ops)
        {
            foreach (var r in op.Redactions.ToArray())
                flagged.AddRange(Enumerable.Range(r.Start, r.Length).Select(i => offset + i));
            await Assert.That((op.Flags & NntlTransportOpFlags.Redacted) != 0).IsEqualTo(!op.Redactions.IsEmpty);
            offset += op.Bytes.Length;
        }

        var stream = ops.SelectMany(o => o.Bytes.ToArray()).ToArray();
        var at = TapTestData.IndexOf(stream, expected);
        var tokenOffsets = Enumerable.Range(at, Token.Length).Select(i => (long)i).ToArray();
        await Assert.That(flagged.ToArray()).IsEquivalentTo(includeSecrets ? [] : tokenOffsets);
    }

    private static async Task WaitForBlocksAsync(SnapshotStream file, long sessionId)
    {
        for (var i = 0; i < 500; i++)
        {
            using var reader = NntlReader.Open(new MemoryStream(file.Copy()), NntlOpenMode.Scan);
            if (reader.Index.FindGroup(NntlBlockKind.TransportOp, sessionId)?.Entries.Length > 4)
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException("The recording did not catch up.");
    }
}
