using System.Buffers;
using System.Diagnostics;
using NexNet.Diagnostics;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;

namespace NexNet.Inspector.Tap;

/// <summary>
/// The per-session tap NexNet calls for every transport read and write. It fans each op out to the sinks linked to
/// the session (attached viewers, recorders) and redacts the auth token on the way.
/// </summary>
/// <remarks>
/// It stays installed for the life of the session, so with no viewer or recorder the path must be free: one volatile
/// read of the link array (null when nothing is linked) and a completed task. No allocation, no interlocked operation.
/// The client-to-server direction also checks its greeting redactor, which exists only until the greeting has passed.
/// Op IDs count per direction from the first op a sink saw; each direction is reported by one thread at a time (see
/// <see cref="INexusSessionTap"/>), so the counters and redactors need no synchronization.
/// </remarks>
internal sealed class TappedSession : INexusSessionTap
{
    private readonly NexusInspector _inspector;
    private readonly Lock _linkGate = new();
    private SinkLink[]? _links;
    private GreetingRedactor? _inboundGreeting;
    private GreetingRedactor? _outboundGreeting;
    private long _inboundOpId;
    private long _outboundOpId;
    private long _connectedId;
    private bool _closed;

    public TappedSession(NexusInspector inspector, long id, in NexusTapSessionInfo info, bool redact)
    {
        _inspector = inspector;
        Id = id;
        Info = info;
        Role = info.IsServer ? NntlSessionRole.Server : NntlSessionRole.Client;
        if (redact)
        {
            // The token travels client to server: read by a server session, written by a client session.
            if (info.IsServer)
                _inboundGreeting = new GreetingRedactor();
            else
                _outboundGreeting = new GreetingRedactor();
        }
    }

    /// <summary>The session's key in streams and recordings (see <see cref="InspectorSessionInfo.Id"/>).</summary>
    public long Id { get; }

    public NexusTapSessionInfo Info { get; }

    public NntlSessionRole Role { get; }

    /// <summary>The NexNet session ID once connected; 0 before.</summary>
    public long ConnectedId => Volatile.Read(ref _connectedId);

    /// <summary>The ID NexNet's snapshots use for this session.</summary>
    public long NexusId => Role == NntlSessionRole.Server ? Id : ConnectedId;

    public InspectorSessionInfo Describe(in NntlFileHeader clock) => new(
        Id,
        Role,
        ConnectedId != 0 ? InspectorSessionState.Connected : InspectorSessionState.Handshaking,
        ConnectedId,
        Info.TransportName,
        Info.Endpoint,
        RemoteEndpoint,
        Info.Timestamp,
        clock.ToUtc(Info.Timestamp).Ticks);

    public string? RemoteEndpoint => Info.RemoteAddress == null
        ? null
        : Info.RemotePort is { } port ? $"{Info.RemoteAddress}:{port}" : Info.RemoteAddress;

    /// <summary>The session-open block for a stream that starts at <paramref name="ticks"/>.</summary>
    public NntlSessionOpen CreateOpen(long ticks, ReadOnlyMemory<byte> schemaHash) =>
        new(Id, Role, ticks, Info.TransportName, Info.Endpoint, null, RemoteEndpoint, schemaHash);

    /// <summary>Adds a sink; false when the session has already closed.</summary>
    public bool AddLink(SinkLink link)
    {
        lock (_linkGate)
        {
            if (_closed)
                return false;
            var links = _links;
            if (links == null)
            {
                Volatile.Write(ref _links, [link]);
                return true;
            }

            var grown = new SinkLink[links.Length + 1];
            links.CopyTo(grown, 0);
            grown[^1] = link;
            Volatile.Write(ref _links, grown);
            return true;
        }
    }

    public void RemoveLink(SinkLink link)
    {
        lock (_linkGate)
        {
            var links = _links;
            var index = links == null ? -1 : Array.IndexOf(links, link);
            if (index < 0)
                return;

            if (links!.Length == 1)
            {
                Volatile.Write(ref _links, null);
                return;
            }

            var shrunk = new SinkLink[links.Length - 1];
            Array.Copy(links, 0, shrunk, 0, index);
            Array.Copy(links, index + 1, shrunk, index, links.Length - index - 1);
            Volatile.Write(ref _links, shrunk);
        }
    }

    /// <summary>The ID the next op of <paramref name="direction"/> will get (tests; read while the session is quiet).</summary>
    internal long NextOpId(TapDirection direction) => direction == TapDirection.Inbound ? _inboundOpId : _outboundOpId;

    /// <summary>Number of sinks linked now (tests).</summary>
    internal int LinkCount => Volatile.Read(ref _links)?.Length ?? 0;

    public ValueTask OnRead(ReadOnlySequence<byte> data, long timestamp)
    {
        var links = Volatile.Read(ref _links);
        if (links == null && _inboundGreeting == null)
            return ValueTask.CompletedTask;
        return Deliver(TapDirection.Inbound, links, data, timestamp);
    }

    public ValueTask OnWrite(ReadOnlySequence<byte> data, long timestamp)
    {
        var links = Volatile.Read(ref _links);
        if (links == null && _outboundGreeting == null)
            return ValueTask.CompletedTask;
        return Deliver(TapDirection.Outbound, links, data, timestamp);
    }

    public void OnConnected(long sessionId) => Volatile.Write(ref _connectedId, sessionId);

    public void OnClosed(DisconnectReason reason)
    {
        SinkLink[]? links;
        lock (_linkGate)
        {
            if (_closed)
                return;
            _closed = true;
            links = _links;
            Volatile.Write(ref _links, null);
        }

        var ticks = Stopwatch.GetTimestamp();
        if (links != null)
        {
            foreach (var link in links)
                link.Sink.EnqueueClose(link, reason, ticks);
        }

        _inspector.OnSessionClosed(this);
    }

    private ValueTask Deliver(TapDirection direction, SinkLink[]? links, in ReadOnlySequence<byte> data, long ticks)
    {
        ref var greeting = ref direction == TapDirection.Inbound ? ref _inboundGreeting : ref _outboundGreeting;
        if (greeting != null)
        {
            if (!greeting.Push(data, ticks, out var held))
                return ValueTask.CompletedTask;

            greeting = null;
            links = Volatile.Read(ref _links);
            return links == null ? ValueTask.CompletedTask : DeliverHeldAsync(direction, links, held);
        }

        return DeliverOp(direction, links!, data, ticks, -1, 0);
    }

    private ValueTask DeliverOp(TapDirection direction, SinkLink[] links, in ReadOnlySequence<byte> data, long ticks,
        int redactionStart, int redactionLength)
    {
        var opId = direction == TapDirection.Inbound ? _inboundOpId++ : _outboundOpId++;
        for (var i = 0; i < links.Length; i++)
        {
            var pending = links[i].Sink.EnqueueOp(links[i], direction, opId, ticks, data, redactionStart, redactionLength);
            if (!pending.IsCompleted)
                return ContinueAsync(pending, direction, links, i + 1, opId, data, ticks, redactionStart, redactionLength);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Block mode: a sink held the op; the rest of the sinks get it once that sink has room.</summary>
    private static async ValueTask ContinueAsync(ValueTask pending, TapDirection direction, SinkLink[] links, int next,
        long opId, ReadOnlySequence<byte> data, long ticks, int redactionStart, int redactionLength)
    {
        await pending.ConfigureAwait(false);
        for (var i = next; i < links.Length; i++)
            await links[i].Sink.EnqueueOp(links[i], direction, opId, ticks, data, redactionStart, redactionLength)
                .ConfigureAwait(false);
    }

    private async ValueTask DeliverHeldAsync(TapDirection direction, SinkLink[] links, List<HeldOp> held)
    {
        foreach (var op in held)
        {
            await DeliverOp(direction, links, new ReadOnlySequence<byte>(op.Bytes), op.Ticks, op.RedactionStart,
                op.RedactionLength).ConfigureAwait(false);
        }
    }
}
