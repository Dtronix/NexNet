using System.Buffers;
using NexNet.Diagnostics;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;

namespace NexNet.Inspector.Tests.Schema;

/// <summary>A tap that keeps every op of every session in memory, in capture order across both directions.</summary>
internal sealed class CaptureTap : INexusTap
{
    private readonly List<CapturedSession> _sessions = [];

    public IReadOnlyList<CapturedSession> Sessions
    {
        get
        {
            lock (_sessions)
                return _sessions.ToArray();
        }
    }

    public INexusSessionTap SessionStarted(in NexusTapSessionInfo session)
    {
        var captured = new CapturedSession(session);
        lock (_sessions)
            _sessions.Add(captured);
        return captured;
    }
}

internal sealed class CapturedSession(NexusTapSessionInfo info) : INexusSessionTap
{
    private readonly List<(TapDirection Direction, byte[] Bytes)> _ops = [];
    private long _inbound;
    private long _outbound;

    public NexusTapSessionInfo Info { get; } = info;

    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long InboundBytes => Interlocked.Read(ref _inbound);

    public long OutboundBytes => Interlocked.Read(ref _outbound);

    /// <summary>The ops so far, in the order they were reported.</summary>
    public (TapDirection Direction, byte[] Bytes)[] Ops
    {
        get
        {
            lock (_ops)
                return _ops.ToArray();
        }
    }

    public ValueTask OnRead(ReadOnlySequence<byte> data, long timestamp)
    {
        Add(TapDirection.Inbound, data, ref _inbound);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnWrite(ReadOnlySequence<byte> data, long timestamp)
    {
        Add(TapDirection.Outbound, data, ref _outbound);
        return ValueTask.CompletedTask;
    }

    public void OnConnected(long sessionId)
    {
    }

    public void OnClosed(DisconnectReason reason) => Closed.TrySetResult();

    private void Add(TapDirection direction, ReadOnlySequence<byte> data, ref long total)
    {
        if (data.IsEmpty)
            return;
        lock (_ops)
            _ops.Add((direction, data.ToArray()));
        Interlocked.Add(ref total, data.Length);
    }
}
