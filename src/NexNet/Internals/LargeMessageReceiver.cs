using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using NexNet.Diagnostics;
using NexNet.Messages;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Internals;

/// <summary>
/// Reassembles large arguments and results the peer streams to this side (docs/internals/flow-control.md,
/// section 4.3). Called from the read loop only, except <see cref="FindStalledStream"/>, <see cref="AddSnapshots"/>,
/// <see cref="Count"/> and <see cref="Clear"/>.
/// </summary>
/// <remarks>
/// A stream is keyed by (invocation ID, kind). Its total length is reserved from the session budget at Start;
/// segments (pooled <see cref="SegmentSize"/> arrays) are allocated only as chunks arrive. A stream that can't be
/// reserved is aborted with Busy and its chunks are discarded up to the total length. On completion the reservation
/// moves to the deserialized message and is released when the message is disposed.
/// </remarks>
internal sealed class LargeMessageReceiver
{
    public const int SegmentSize = 64 * 1024;

    private readonly ConcurrentDictionary<int, Stream> _streams = new();
    private readonly SessionBudget _budget;
    private readonly int _maxMessageSize;
    private readonly PoolManager _poolManager;
    private readonly NexusSerializerOptions _options;
    private readonly TimeProvider _time;
    private readonly IOwner _owner;

    // Streams in _streams; ConcurrentDictionary.Count takes every lock, so it is kept here for metrics.
    private int _count;

    /// <summary>
    /// Streams being received.
    /// </summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// What the receiver needs from its session.
    /// </summary>
    public interface IOwner
    {
        /// <summary>True if this side has a call with the ID waiting for its result.</summary>
        bool IsCallOutstanding(ushort invocationId);

        /// <summary>Takes one invocation credit for a large argument; false if the peer has none left.</summary>
        bool TryTakeInvocationCredit();

        /// <summary>Returns a credit taken for an argument that will not be queued.</summary>
        void ReturnInvocationCredit();

        /// <summary>Queues an abort to send (never waits).</summary>
        void SendAbort(ushort invocationId, LargeMessageKind kind, LargeMessageAbortReason reason);
    }

    public LargeMessageReceiver(IOwner owner, SessionBudget budget, int maxMessageSize, PoolManager poolManager,
        NexusSerializerOptions options, TimeProvider time)
    {
        _owner = owner;
        _budget = budget;
        _maxMessageSize = maxMessageSize;
        _poolManager = poolManager;
        _options = options;
        _time = time;
    }

    private static int Key(ushort invocationId, LargeMessageKind kind) => invocationId | ((int)kind << 16);

    /// <summary>
    /// Handles a Start. Returns a disconnect reason if it breaks the protocol.
    /// </summary>
    public DisconnectReason OnStart(MessageStartMessage start)
    {
        var expectedInner = start.Kind switch
        {
            LargeMessageKind.Argument => MessageType.Invocation,
            LargeMessageKind.Result => MessageType.InvocationResult,
            _ => MessageType.Unset,
        };

        if (expectedInner == MessageType.Unset || start.InnerType != expectedInner || start.TotalLength == 0)
            return DisconnectReason.ProtocolError;

        // Larger than this side advertised.
        if (start.TotalLength > _maxMessageSize)
            return DisconnectReason.ProtocolError;

        var key = Key(start.InvocationId, start.Kind);
        if (_streams.ContainsKey(key))
            return DisconnectReason.ProtocolError;

        if (start.Kind == LargeMessageKind.Result && !_owner.IsCallOutstanding(start.InvocationId))
            return DisconnectReason.ProtocolError;

        // A large argument uses the invocation's credit.
        if (start.Kind == LargeMessageKind.Argument && !_owner.TryTakeInvocationCredit())
            return DisconnectReason.FlowControl;

        var stream = new Stream(start.TotalLength, start.InnerType, start.Kind, _time.GetTickCount64());
        if (_budget.TryReserve(start.TotalLength))
        {
            stream.Reserved = true;
        }
        else
        {
            // No room now: refuse it and discard its chunks. The argument will not be queued, so its credit returns.
            stream.Aborting = true;
            if (start.Kind == LargeMessageKind.Argument)
                _owner.ReturnInvocationCredit();
            _owner.SendAbort(start.InvocationId, start.Kind, LargeMessageAbortReason.Busy);
        }

        _streams[key] = stream;
        Interlocked.Increment(ref _count);
        return DisconnectReason.None;
    }

    /// <summary>
    /// Handles a chunk. Returns a disconnect reason if it breaks the protocol; <paramref name="completed"/> is the
    /// reassembled message once the last chunk arrived.
    /// </summary>
    public DisconnectReason OnChunk(ushort invocationId, LargeMessageKind kind, ReadOnlySequence<byte> payload, out IMessageBase? completed)
    {
        completed = null;
        var key = Key(invocationId, kind);
        if (!_streams.TryGetValue(key, out var stream))
            return DisconnectReason.ProtocolError;

        if (stream.Received + payload.Length > stream.Total)
            return DisconnectReason.ProtocolError;

        stream.Received += payload.Length;
        stream.LastProgress = _time.GetTickCount64();

        if (stream.Aborting)
        {
            // Discarded, up to the total length; then the key closes.
            if (stream.Received == stream.Total && _streams.TryRemove(key, out _))
                Interlocked.Decrement(ref _count);
            return DisconnectReason.None;
        }

        stream.Append(payload);
        if (stream.Received < stream.Total)
            return DisconnectReason.None;

        if (_streams.TryRemove(key, out _))
            Interlocked.Decrement(ref _count);
        try
        {
            completed = _poolManager.Deserialize(stream.InnerType, stream.ToSequence(), _options);
        }
        catch (NexusSerializationException)
        {
            stream.Close(_budget);
            return DisconnectReason.ProtocolError;
        }

        // The reservation now covers the message; it is released when the message is disposed.
        stream.ReturnSegments();
        stream.Reserved = false;
        switch (completed)
        {
            case InvocationMessage invocation:
                invocation.ReserveFrom(_budget, stream.Total);
                break;
            case InvocationResultMessage result:
                result.ReserveFrom(_budget, stream.Total);
                break;
        }

        return DisconnectReason.None;
    }

    /// <summary>
    /// Handles an abort from the stream's sender: Cancelled ends the stream, Ack ends a stream this side aborted.
    /// An abort for an unknown key is ignored, as it can race with completion.
    /// </summary>
    public void OnAbort(MessageAbortMessage abort)
    {
        var key = Key(abort.InvocationId, abort.Kind);
        if (!_streams.TryGetValue(key, out var stream))
            return;

        if (abort.Reason == LargeMessageAbortReason.Ack && !stream.Aborting)
            return;

        if (!_streams.TryRemove(key, out _))
            return;
        Interlocked.Decrement(ref _count);

        // An argument this side had accepted will not be queued now.
        if (stream.Kind == LargeMessageKind.Argument && !stream.Aborting)
            _owner.ReturnInvocationCredit();

        stream.Close(_budget);
    }

    /// <summary>
    /// Describes a stream that received nothing for longer than <paramref name="timeoutTicks"/>; null if none did.
    /// </summary>
    public string? FindStalledStream(long nowTicks, long timeoutTicks)
    {
        foreach (var (key, stream) in _streams)
        {
            var idle = nowTicks - Volatile.Read(ref stream.LastProgress);
            if (idle > timeoutTicks)
            {
                return $"inbound large {(stream.Kind == LargeMessageKind.Argument ? "argument" : "result")} of invocation " +
                       $"{(ushort)key}, {LogFormat.Bytes(Volatile.Read(ref stream.Received))} of {LogFormat.Bytes(stream.Total)}, " +
                       $"no data for {LogFormat.Seconds(idle)}";
            }
        }

        return null;
    }

    /// <summary>
    /// Adds a snapshot of every stream being received to <paramref name="snapshots"/>.
    /// </summary>
    public void AddSnapshots(List<NexusLargeMessageSnapshot> snapshots, long nowTicks)
    {
        foreach (var (key, stream) in _streams)
        {
            snapshots.Add(new NexusLargeMessageSnapshot
            {
                Direction = NexusDirection.Inbound,
                Kind = stream.Kind == LargeMessageKind.Argument ? NexusLargeMessageKind.Argument : NexusLargeMessageKind.Result,
                InvocationId = (ushort)key,
                Length = stream.Total,
                Transferred = Volatile.Read(ref stream.Received),
                SinceLastProgress = TimeSpan.FromMilliseconds(Math.Max(0, nowTicks - Volatile.Read(ref stream.LastProgress))),
                Aborting = stream.Aborting,
            });
        }
    }

    /// <summary>
    /// Drops every stream when the session ends.
    /// </summary>
    public void Clear()
    {
        foreach (var key in _streams.Keys)
        {
            if (_streams.TryRemove(key, out var stream))
            {
                Interlocked.Decrement(ref _count);
                stream.Close(_budget);
            }
        }
    }

    private sealed class Stream
    {
        public readonly long Total;
        public readonly MessageType InnerType;
        public readonly LargeMessageKind Kind;
        public long Received;
        public long LastProgress;
        public bool Aborting;
        public bool Reserved;

        private readonly List<byte[]> _segments = new();
        private int _lastSegmentLength;

        public Stream(long total, MessageType innerType, LargeMessageKind kind, long now)
        {
            Total = total;
            InnerType = innerType;
            Kind = kind;
            LastProgress = now;
        }

        public void Append(ReadOnlySequence<byte> payload)
        {
            foreach (var memory in payload)
            {
                var span = memory.Span;
                while (span.Length > 0)
                {
                    if (_segments.Count == 0 || _lastSegmentLength == SegmentSize)
                    {
                        _segments.Add(ArrayPool<byte>.Shared.Rent(SegmentSize));
                        _lastSegmentLength = 0;
                    }

                    var segment = _segments[^1];
                    var count = Math.Min(span.Length, SegmentSize - _lastSegmentLength);
                    span[..count].CopyTo(segment.AsSpan(_lastSegmentLength));
                    _lastSegmentLength += count;
                    span = span[count..];
                }
            }
        }

        public ReadOnlySequence<byte> ToSequence()
        {
            if (_segments.Count == 1)
                return new ReadOnlySequence<byte>(_segments[0], 0, _lastSegmentLength);

            var first = new Segment(_segments[0].AsMemory(0, SegmentSize), 0);
            var last = first;
            for (var i = 1; i < _segments.Count; i++)
            {
                var length = i == _segments.Count - 1 ? _lastSegmentLength : SegmentSize;
                last = last.Append(_segments[i].AsMemory(0, length));
            }

            return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        }

        public void ReturnSegments()
        {
            foreach (var segment in _segments)
                ArrayPool<byte>.Shared.Return(segment);
            _segments.Clear();
        }

        public void Close(SessionBudget budget)
        {
            ReturnSegments();
            if (Reserved)
            {
                Reserved = false;
                budget.Release(Total);
            }
        }
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
