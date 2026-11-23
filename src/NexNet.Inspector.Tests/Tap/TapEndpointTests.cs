using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Tap;
using NexNet.Messages;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNet.Inspector.Tests.Tap;

/// <summary>The inspector endpoint end to end, over a real in-process server.</summary>
public class TapEndpointTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Test]
    [Arguments(TapTransport.Uds)]
    [Arguments(TapTransport.Tcp)]
    [Arguments(TapTransport.TcpTls)]
    public async Task Attach_streams_a_snapshot_and_ops_that_frame_and_assemble(TapTransport transport)
    {
        await using var h = new TapHarness(transport);
        await h.StartAsync();
        await h.StartEndpointAsync();
        var client = await h.ConnectClientAsync();
        var session = await h.WaitForSessionAsync();
        var viewer = await h.ConnectViewerAsync();

        var info = await viewer.GetServerInfoAsync();
        await Assert.That(info.ApiVersion).IsEqualTo(NexusInspector.ApiVersion);
        await Assert.That(info.Role).IsEqualTo(NntlSessionRole.Server);
        await Assert.That(info.SchemaHash!).IsEquivalentTo(SchemaHash.ToBytes(TapTestData.SchemaHash<TapServerNexus>()));

        var sessions = await viewer.ListSessionsAsync();
        await Assert.That(sessions.Length).IsEqualTo(1);
        await Assert.That(sessions[0].Id).IsEqualTo(session.Id);
        await Assert.That(sessions[0].ConnectedId).IsEqualTo(session.Id);
        await Assert.That(sessions[0].Role).IsEqualTo(NntlSessionRole.Server);

        await using var stream = await viewer.AttachAsync(new InspectorAttachRequest(InspectorTargetKind.Session, session.Id,
            InspectorOverflow.Drop, Snapshot: true, Record: false));
        var reading = TapCapture.ReadAllAsync(stream);

        for (var i = 0; i < 10; i++)
            await Assert.That(await client.Proxy.Echo([(byte)i, 2, 3])).IsEquivalentTo(new byte[] { (byte)i, 2, 3 });
        var large = new byte[200 * 1024];
        Random.Shared.NextBytes(large);
        await Assert.That(await client.Proxy.Echo(large)).IsEquivalentTo(large);
        await client.DisconnectAsync();

        var capture = await reading.WaitAsync(Wait);
        await Assert.That(capture.Complete).IsTrue();
        var kinds = capture.Blocks.Select(b => b.Kind).ToArray();
        await Assert.That(kinds.Take(4)).IsEquivalentTo(new[]
            { NntlBlockKind.Header, NntlBlockKind.Schema, NntlBlockKind.SessionOpen, NntlBlockKind.Snapshot });
        await Assert.That(kinds[^1]).IsEqualTo(NntlBlockKind.SessionClose);
        await Assert.That(kinds.Contains(NntlBlockKind.Gap)).IsFalse();

        var schema = NntlCodec.DecodeSchema(capture.Blocks[1].Body);
        await Assert.That(schema.Hash.ToArray()).IsEquivalentTo(info.SchemaHash!);
        await Assert.That(schema.Schema.Length).IsGreaterThan(0);
        var open = NntlCodec.DecodeSessionOpen(capture.Blocks[2].Body);
        await Assert.That(open.SessionId).IsEqualTo(session.Id);
        await Assert.That(open.SchemaHash.ToArray()).IsEquivalentTo(info.SchemaHash!);

        var snapshot = NntlCodec.DecodeSnapshot(capture.Blocks[3].Body);
        await Assert.That(snapshot.SessionId).IsEqualTo(session.Id);
        var sections = snapshot.Sections.ToArray();
        await Assert.That(sections.Select(s => s.Kind)).IsEquivalentTo(new[]
        {
            NntlSnapshotSectionKind.OpenPipes, NntlSnapshotSectionKind.PendingInvocations, NntlSnapshotSectionKind.CollectionState,
        });
        await Assert.That(NntlSnapshotSections.DecodeOpenPipes(sections[0].Data).Length).IsEqualTo(0);
        await Assert.That(NntlSnapshotSections.DecodeInvocations(sections[1].Data).Executing).IsEqualTo(0);
        await Assert.That(NntlSnapshotSections.DecodeCollections(sections[2].Data).Length).IsEqualTo(0);

        var close = NntlCodec.DecodeSessionClose(capture.Blocks[^1].Body);
        await Assert.That(close.Kind).IsEqualTo(NntlCloseKind.Disconnected);

        // The I04 follow-up: what the tap captured frames with the I02 parser and assembles with the I03 assembler.
        var ops = capture.Blocks.Where(b => b.Kind == NntlBlockKind.TransportOp)
            .Select(b => NntlCodec.DecodeTransportOp(b.Body)).ToArray();
        var assembled = new AssembledSession(ops, NntlSessionRole.Server, fromInception: false);
        await Assert.That(assembled.InboundParser.IsFaulted).IsFalse();
        await Assert.That(assembled.OutboundParser.IsFaulted).IsFalse();
        var calls = assembled.Assembler.Invocations.ToArray()
            .Count(i => i.HasRequest && i.ResultState == Core.Assembly.InvocationResultState.CompletedResult);
        await Assert.That(calls).IsEqualTo(11);
    }

    [Test]
    public async Task A_wrong_endpoint_token_is_refused()
    {
        await using var h = new TapHarness(TapTransport.Uds);
        await h.StartAsync();
        await h.StartEndpointAsync();

        var refused = await Assert.ThrowsAsync<InspectorConnectionException>(() => h.ConnectViewerAsync("wrong"u8.ToArray()));
        await Assert.That(refused!.Reason).IsEqualTo(DisconnectReason.Authentication);
    }

    [Test]
    public async Task The_endpoint_is_never_tapped()
    {
        await using var h = new TapHarness(TapTransport.Uds);
        await h.StartAsync();

        var tapped = new UdsServerConfig { EndPoint = new System.Net.Sockets.UnixDomainSocketEndPoint(Path.GetTempFileName()) };
        tapped.UseInspector();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Inspector.StartEndpointAsync(tapped, TapHarness.EndpointToken));

        var endpoint = await h.StartEndpointAsync();
        await Assert.That(endpoint.Server.Config.Tap).IsNull();
        var viewer = await h.ConnectViewerAsync();
        await h.ConnectClientAsync();
        await h.WaitForSessionAsync();

        // The viewer's own session is on the endpoint, not the inspected server: only the client is listed.
        await Assert.That((await viewer.ListSessionsAsync()).Length).IsEqualTo(1);
        await Assert.That((await viewer.GetServerInfoAsync()).Sessions).IsEqualTo(1);
    }

    [Test]
    public async Task Detach_ends_the_stream_and_a_new_attach_starts_fresh()
    {
        await using var h = new TapHarness(TapTransport.Uds);
        await h.StartAsync();
        await h.StartEndpointAsync();
        var client = await h.ConnectClientAsync();
        var session = await h.WaitForSessionAsync();
        var viewer = await h.ConnectViewerAsync();
        var request = new InspectorAttachRequest(InspectorTargetKind.Session, session.Id, InspectorOverflow.Drop, true, false);
        var tapped = h.Inspector.FindSession(session.Id)!;

        await using (var first = await viewer.AttachAsync(request))
        {
            var reading = TapCapture.ReadAllAsync(first);
            await client.Proxy.Echo([1]);
            await Assert.That(tapped.LinkCount).IsEqualTo(1);
            await Assert.ThrowsAsync<InspectorRequestException>(() => viewer.AttachAsync(request));
            await viewer.DetachAsync();
            var capture = await reading.WaitAsync(Wait);

            await Assert.That(capture.Complete).IsTrue();
            var close = NntlCodec.DecodeSessionClose(capture.Blocks[^1].Body);
            await Assert.That(close.Kind).IsEqualTo(NntlCloseKind.RecordingStopped);
            await Assert.That(capture.Ops(session.Id, TapDirection.Inbound).Any()).IsTrue();
        }

        await Assert.That(tapped.LinkCount).IsEqualTo(0);

        await using var second = await viewer.AttachAsync(request);
        var again = TapCapture.ReadAllAsync(second);
        await client.Proxy.Echo([2]);
        await viewer.DetachAsync();
        var fresh = await again.WaitAsync(Wait);
        await Assert.That(fresh.Blocks.Select(b => b.Kind).Take(4)).IsEquivalentTo(new[]
            { NntlBlockKind.Header, NntlBlockKind.Schema, NntlBlockKind.SessionOpen, NntlBlockKind.Snapshot });
        var echoed = fresh.Ops(session.Id, TapDirection.Inbound).SelectMany(o => o.Bytes.ToArray()).ToArray();
        await Assert.That(echoed.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Recording_disabled_is_refused_and_visible_to_clients()
    {
        await using var h = new TapHarness(TapTransport.Uds, new InspectorOptions { AllowRecording = false });
        await h.StartAsync();
        await h.StartEndpointAsync();
        await h.ConnectClientAsync();
        var session = await h.WaitForSessionAsync();
        var viewer = await h.ConnectViewerAsync();

        await Assert.That((await viewer.GetServerInfoAsync()).AllowRecording).IsFalse();
        var all = await Assert.ThrowsAsync<InspectorRequestException>(() =>
            viewer.RecordAllAsync(new InspectorRecordRequest(InspectorOverflow.Drop, true)));
        await Assert.That(all!.Status).IsEqualTo(InspectorStatus.RecordingDisabled);
        var record = await Assert.ThrowsAsync<InspectorRequestException>(() => viewer.AttachAsync(
            new InspectorAttachRequest(InspectorTargetKind.Session, session.Id, InspectorOverflow.Drop, true, Record: true)));
        await Assert.That(record!.Status).IsEqualTo(InspectorStatus.RecordingDisabled);
        await Assert.That(() => h.Inspector.RecordTo(new MemoryStream())).Throws<InvalidOperationException>();

        // Watching live stays allowed.
        await using var live = await viewer.AttachAsync(
            new InspectorAttachRequest(InspectorTargetKind.Session, session.Id, InspectorOverflow.Drop, true, Record: false));
        await viewer.DetachAsync();
    }

    [Test]
    public async Task Attach_refusals_name_the_reason()
    {
        await using var h = new TapHarness(TapTransport.Uds);
        await h.StartAsync();
        await h.StartEndpointAsync();
        var viewer = await h.ConnectViewerAsync();

        var missing = await Assert.ThrowsAsync<InspectorRequestException>(() => viewer.AttachAsync(
            new InspectorAttachRequest(InspectorTargetKind.Session, 12345, InspectorOverflow.Drop, true, false)));
        await Assert.That(missing!.Status).IsEqualTo(InspectorStatus.NoSuchSession);
        var client = await Assert.ThrowsAsync<InspectorRequestException>(() => viewer.AttachAsync(
            new InspectorAttachRequest(InspectorTargetKind.Client, 0, InspectorOverflow.Drop, true, false)));
        await Assert.That(client!.Status).IsEqualTo(InspectorStatus.WrongTarget);
    }

    [Test]
    public async Task Record_all_over_the_endpoint_records_new_sessions_from_inception()
    {
        await using var h = new TapHarness(TapTransport.Uds, new InspectorOptions { AllowRecording = true });
        await h.StartAsync();
        await h.StartEndpointAsync();
        var first = await h.ConnectClientAsync();
        var current = await h.WaitForSessionAsync();
        var viewer = await h.ConnectViewerAsync();

        await using var stream = await viewer.RecordAllAsync(new InspectorRecordRequest(InspectorOverflow.Block, true));
        var reading = TapCapture.ReadAllAsync(stream);
        var second = await h.ConnectClientAsync();
        var joined = await h.WaitForSessionAsync(2);
        await first.Proxy.Echo([1]);
        await second.Proxy.Echo([2]);
        await second.DisconnectAsync();
        await viewer.DetachAsync();
        var capture = await reading.WaitAsync(Wait);

        await Assert.That(capture.Complete).IsTrue();
        var opens = capture.Blocks.Where(b => b.Kind == NntlBlockKind.SessionOpen)
            .Select(b => NntlCodec.DecodeSessionOpen(b.Body).SessionId).ToArray();
        await Assert.That(opens).IsEquivalentTo(new[] { current.Id, joined.Id });

        // The new session is there from its first byte: the preamble, then the greetings.
        var joinedInbound = capture.Ops(joined.Id, TapDirection.Inbound).SelectMany(o => o.Bytes.ToArray()).ToArray();
        await Assert.That(joinedInbound.Take(8).ToArray()).IsEquivalentTo(TapTestData.Preamble);
        var assembled = new AssembledSession(capture.Blocks.Where(b => b.Kind == NntlBlockKind.TransportOp)
            .Select(b => NntlCodec.DecodeTransportOp(b.Body)).Where(o => o.SessionId == joined.Id), NntlSessionRole.Server, true);
        await Assert.That(assembled.Assembler.Greetings.Length).IsEqualTo(2);

        var closes = capture.Blocks.Where(b => b.Kind == NntlBlockKind.SessionClose)
            .Select(b => NntlCodec.DecodeSessionClose(b.Body)).ToDictionary(c => c.SessionId);
        await Assert.That(closes[joined.Id].Kind).IsEqualTo(NntlCloseKind.Disconnected);
        await Assert.That(closes[current.Id].Kind).IsEqualTo(NntlCloseKind.RecordingStopped);
    }

    [Test]
    public async Task A_client_serves_its_single_connection()
    {
        await using var h = new TapHarness(TapTransport.Uds);
        await h.StartAsync();

        var config = h.CreateClientConfig();
        var inspector = config.UseInspector<TapClientNexus>();
        await using var _ = inspector;
        var client = TapClientNexus.CreateClient(config, new TapClientNexus());
        inspector.UseSnapshotsOf(client);
        await client.ConnectAsync();
        var path = Path.Combine(Path.GetTempPath(), $"nntap-{Guid.NewGuid():N}.sock");
        await using var endpoint = await inspector.StartEndpointAsync(
            new UdsServerConfig { EndPoint = new System.Net.Sockets.UnixDomainSocketEndPoint(path) }, TapHarness.EndpointToken);
        await using var viewer = await InspectorClient.ConnectAsync(
            new UdsClientConfig { EndPoint = new System.Net.Sockets.UnixDomainSocketEndPoint(path) }, TapHarness.EndpointToken);

        var info = await viewer.GetServerInfoAsync();
        await Assert.That(info.Role).IsEqualTo(NntlSessionRole.Client);
        var sessions = await viewer.ListSessionsAsync();
        await Assert.That(sessions.Length).IsEqualTo(1);
        await Assert.That(sessions[0].Id).IsEqualTo(1);
        await Assert.That(sessions[0].ConnectedId).IsNotEqualTo(0);

        await using var stream = await viewer.AttachAsync(
            new InspectorAttachRequest(InspectorTargetKind.Client, 0, InspectorOverflow.Drop, true, false));
        var reading = TapCapture.ReadAllAsync(stream);
        await client.Proxy.Echo([7, 7]);
        await client.DisconnectAsync();
        var capture = await reading.WaitAsync(Wait);

        await Assert.That(capture.Complete).IsTrue();
        var open = NntlCodec.DecodeSessionOpen(capture.Blocks[2].Body);
        await Assert.That(open.Role).IsEqualTo(NntlSessionRole.Client);
        await Assert.That(capture.Blocks[3].Kind).IsEqualTo(NntlBlockKind.Snapshot);
        var assembled = new AssembledSession(capture.Blocks.Where(b => b.Kind == NntlBlockKind.TransportOp)
            .Select(b => NntlCodec.DecodeTransportOp(b.Body)), NntlSessionRole.Client, false);
        var calls = assembled.Assembler.Invocations.ToArray().Count(i => i.Direction == TapDirection.Outbound && i.HasRequest);
        await Assert.That(calls).IsEqualTo(1);
        File.Delete(path);
    }
}
