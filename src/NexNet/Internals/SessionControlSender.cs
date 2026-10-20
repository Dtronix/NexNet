using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;

namespace NexNet.Internals;

/// <summary>
/// Sends the control replies the read loop asks for, so the read loop never waits on a send
/// (docs/internals/flow-control-v2.md, section 5.1).
/// </summary>
/// <remarks>
/// Requests are coalesced: there is at most one pending item of each kind, so outbound control memory does not grow
/// with the number of messages received. A drain runs on the thread pool when a request arrives and nothing is
/// draining yet; it sends through the session's normal send path, which serializes on the write mutex.
/// </remarks>
internal sealed class SessionControlSender
{
    private readonly INexusSession _session;

    // 1 while a drain is scheduled or running.
    private int _draining;
    private int _pongPending;

    // Pipes with a pending window grant or close acknowledgement; each pipe is queued at most once at a time.
    private readonly ConcurrentQueue<NexusDuplexPipe> _pipes = new();

    // Large-message aborts to send: at most one per stream key and side.
    private readonly ConcurrentQueue<(ushort InvocationId, LargeMessageKind Kind, LargeMessageAbortReason Reason)> _aborts = new();

    // Invocation credit to return to the peer, sent once it reaches the threshold (half the credits advertised).
    private int _pendingInvocationCredit;
    private readonly int _invocationCreditThreshold;

    /// <param name="session">The session to send on.</param>
    /// <param name="invocationCreditThreshold">Invocation credit is returned in batches of at least this much.</param>
    public SessionControlSender(INexusSession session, int invocationCreditThreshold = 1)
    {
        _session = session;
        _invocationCreditThreshold = Math.Max(1, invocationCreditThreshold);
    }

    /// <summary>
    /// Returns one invocation credit to the peer. Coalesced: sent as one message once the pending credit reaches the
    /// threshold, so a busy session sends one credit message per batch of invocations.
    /// </summary>
    public void ReturnInvocationCredit()
    {
        if (Interlocked.Increment(ref _pendingInvocationCredit) >= _invocationCreditThreshold)
            Schedule();
    }

    /// <summary>
    /// Takes the pending invocation credit once it has reached the threshold, for the caller to send; 0 if it has
    /// not. A send already in progress takes it to carry the credit along (see
    /// <c>NexusSession.WritePendingCredit</c>), so the drain only sends credit while nothing else is being sent.
    /// </summary>
    public int TakeInvocationCredit()
    {
        if (Volatile.Read(ref _pendingInvocationCredit) < _invocationCreditThreshold)
            return 0;

        return Interlocked.Exchange(ref _pendingInvocationCredit, 0);
    }

    /// <summary>
    /// Requests a ping reply. Any number of requests before the next drain send one reply.
    /// </summary>
    public void RequestPong()
    {
        Volatile.Write(ref _pongPending, 1);
        Schedule();
    }

    /// <summary>
    /// Requests a large-message abort.
    /// </summary>
    public void RequestAbort(ushort invocationId, LargeMessageKind kind, LargeMessageAbortReason reason)
    {
        _aborts.Enqueue((invocationId, kind, reason));
        Schedule();
    }

    /// <summary>
    /// Requests that <paramref name="pipe"/>'s pending control state be sent. The pipe guarantees it is queued at most
    /// once until it is drained.
    /// </summary>
    public void RequestPipeControl(NexusDuplexPipe pipe)
    {
        _pipes.Enqueue(pipe);
        Schedule();
    }

    private bool HasPending => Volatile.Read(ref _pongPending) != 0
                               || !_pipes.IsEmpty
                               || !_aborts.IsEmpty
                               || Volatile.Read(ref _pendingInvocationCredit) >= _invocationCreditThreshold;

    private void Schedule()
    {
        if (Interlocked.Exchange(ref _draining, 1) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(static sender => _ = sender.DrainAsync(), this, preferLocal: false);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            try
            {
                if (Interlocked.Exchange(ref _pongPending, 0) != 0)
                    await _session.SendHeader(MessageType.Ping).ConfigureAwait(false);

                while (_pipes.TryDequeue(out var pipe))
                    await pipe.SendPendingControl().ConfigureAwait(false);

                while (_aborts.TryDequeue(out var abort))
                {
                    using var message = _session.PoolManager.Rent<MessageAbortMessage>();
                    message.InvocationId = abort.InvocationId;
                    message.Kind = abort.Kind;
                    message.Reason = abort.Reason;
                    await _session.SendMessage(message).ConfigureAwait(false);
                }

                var credit = TakeInvocationCredit();
                if (credit > 0)
                {
                    while (credit > 0)
                    {
                        using var message = _session.PoolManager.Rent<InvocationCreditMessage>();
                        message.Credits = (ushort)Math.Min(credit, ushort.MaxValue);
                        credit -= message.Credits;
                        await _session.SendMessage(message).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception e)
            {
                // The session is going away; the disconnect path reports why.
                _session.Logger?.LogDebug(e, "Control send failed.");
            }

            // Hand back the drain, then take it again if a request raced in after the sends above.
            Volatile.Write(ref _draining, 0);
            if (!HasPending || Interlocked.Exchange(ref _draining, 1) != 0)
                return;
        }
    }
}
