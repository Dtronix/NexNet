using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Logging;
using NexNet.Messages;

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

    public SessionControlSender(INexusSession session)
    {
        _session = session;
    }

    /// <summary>
    /// Requests a ping reply. Any number of requests before the next drain send one reply.
    /// </summary>
    public void RequestPong()
    {
        Volatile.Write(ref _pongPending, 1);
        Schedule();
    }

    private bool HasPending => Volatile.Read(ref _pongPending) != 0;

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
