using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Invocation;

/// <summary>
/// Local (single-server) implementation of IInvocationRouter.
/// </summary>
/// <remarks>
/// An invocation for several sessions is sent in two passes. The first writes it, without waiting, to every session
/// that can take it now (<see cref="INexusSession.TryWriteInvocation{TMessage}"/>), copying one encoded frame to
/// each. The second waits for the sessions
/// that were busy, at most <see cref="MaxPendingSends"/> at a time and all within one
/// <see cref="Transports.ConfigBase.ProgressTimeout"/>; a session that grants no credit in that time misses the
/// invocation (logged by the session). A slow or unresponsive client therefore delays neither the others nor the
/// broadcast by more than one timeout.
/// </remarks>
internal sealed class LocalInvocationRouter : IInvocationRouter
{
    /// <summary>
    /// Most busy sessions one invocation waits for at once.
    /// </summary>
    internal const int MaxPendingSends = 256;

    private readonly LocalSessionContext _context;

    public LocalInvocationRouter(LocalSessionContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// State of the first pass of one invocation: the frame encoded for it, and the sessions that were busy.
    /// </summary>
    private struct FirstPass
    {
        // Encoded on first use. Not encoded for a streamed invocation, which each session sends under its own ID.
        private PooledArrayBufferWriter? _frame;
        private bool _encoded;

        public List<INexusSession>? Busy;

        /// <summary>
        /// Writes the invocation if the session can take it now, otherwise adds the session to <see cref="Busy"/>.
        /// </summary>
        public void TryWrite<TMessage>(INexusSession session, TMessage message)
            where TMessage : IInvocationMessage, IMessageBase
        {
            try
            {
                if (session.TryWriteInvocation(message, GetFrame(message)) == InvocationWriteResult.Busy)
                    (Busy ??= ListPool<INexusSession>.Rent()).Add(session);
            }
            catch
            {
                // Fire-and-forget: ignore send failures
            }
        }

        private ReadOnlySpan<byte> GetFrame<TMessage>(TMessage message)
            where TMessage : IInvocationMessage, IMessageBase
        {
            if (!_encoded)
            {
                _encoded = true;
                if (message.Arguments.Length <= IInvocationMessage.MaxArgumentSize)
                {
                    _frame = PooledArrayBufferWriter.Rent();
                    if (!MessageFrame.TryWrite(message, _frame))
                    {
                        _frame.Return();
                        _frame = null;
                    }
                }
            }

            return _frame == null ? default : _frame.WrittenSpan;
        }

        /// <summary>
        /// Ends the first pass: returns the frame and starts the second pass for the busy sessions.
        /// </summary>
        public ValueTask Finish<TMessage>(TMessage message)
            where TMessage : IInvocationMessage, IMessageBase
        {
            _frame?.Return();
            _frame = null;
            return SendToBusy(message, Busy);
        }
    }

    /// <summary>
    /// Second pass: sends to the sessions that were busy, waiting for at most <see cref="MaxPendingSends"/> of them
    /// at once, all within one progress timeout. Returns the list to its pool.
    /// </summary>
    private static async ValueTask SendToBusy<TMessage>(TMessage message, List<INexusSession>? busy)
        where TMessage : IInvocationMessage, IMessageBase
    {
        if (busy == null)
            return;

        try
        {
            var timeout = TimeSpan.FromMilliseconds(busy[0].Config.ProgressTimeout);
            var start = Stopwatch.GetTimestamp();

            if (busy.Count == 1)
            {
                await Send(busy[0], message, timeout).ConfigureAwait(false);
                return;
            }

            var pending = new List<Task>(Math.Min(busy.Count, MaxPendingSends));
            foreach (var session in busy)
            {
                if (pending.Count == MaxPendingSends)
                    pending.Remove(await Task.WhenAny(pending).ConfigureAwait(false));

                var remaining = timeout - Stopwatch.GetElapsedTime(start);
                pending.Add(Send(session, message, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero).AsTask());
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        finally
        {
            ListPool<INexusSession>.Return(busy);
        }
    }

    /// <summary>
    /// Sends to one session, waiting at most <paramref name="creditTimeout"/> for its invocation credit.
    /// </summary>
    /// <remarks>
    /// An argument larger than one frame is streamed under its invocation ID, so it is sent as a copy of the message
    /// with an ID the session tracks until the stream is sent. Otherwise the shared message is sent unchanged.
    /// </remarks>
    private static async ValueTask Send<TMessage>(INexusSession session, TMessage message, TimeSpan creditTimeout)
        where TMessage : IInvocationMessage, IMessageBase
    {
        try
        {
            if (message.Arguments.Length <= IInvocationMessage.MaxArgumentSize)
            {
                await session.SendInvocation(message, creditTimeout: creditTimeout).ConfigureAwait(false);
                return;
            }

            using var copy = session.PoolManager.Rent<InvocationMessage>();
            copy.MethodId = message.MethodId;
            copy.Flags = message.Flags;
            copy.Arguments = message.Arguments;
            copy.InvocationId = session.SessionInvocationStateManager.GetNextId(true);
            try
            {
                await session.SendInvocation(copy, creditTimeout: creditTimeout).ConfigureAwait(false);
            }
            finally
            {
                session.SessionInvocationStateManager.ReleaseId(copy.InvocationId);
            }
        }
        catch
        {
            // Fire-and-forget: ignore send failures
        }
    }

    public ValueTask InvokeAllAsync<TMessage>(TMessage message)
        where TMessage : IInvocationMessage, IMessageBase
    {
        var pass = new FirstPass();
        foreach (var (_, session) in _context.Sessions)
            pass.TryWrite(session, message);

        return pass.Finish(message);
    }

    public ValueTask InvokeAllExceptAsync<TMessage>(TMessage message, long excludeSessionId)
        where TMessage : IInvocationMessage, IMessageBase
    {
        var pass = new FirstPass();
        foreach (var (id, session) in _context.Sessions)
        {
            if (id != excludeSessionId)
                pass.TryWrite(session, message);
        }

        return pass.Finish(message);
    }

    public async ValueTask<bool> InvokeClientAsync<TMessage>(TMessage message, long sessionId)
        where TMessage : IInvocationMessage, IMessageBase
    {
        if (!_context.Sessions.TryGetValue(sessionId, out var session))
            return false;

        try
        {
            if (session.TryWriteInvocation(message) == InvocationWriteResult.Busy)
                await Send(session, message, TimeSpan.FromMilliseconds(session.Config.ProgressTimeout)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public ValueTask InvokeClientsAsync<TMessage>(TMessage message, long[] sessionIds)
        where TMessage : IInvocationMessage, IMessageBase
    {
        var pass = new FirstPass();
        for (int i = 0; i < sessionIds.Length; i++)
        {
            if (_context.Sessions.TryGetValue(sessionIds[i], out var session))
                pass.TryWrite(session, message);
        }

        return pass.Finish(message);
    }

    public ValueTask InvokeGroupAsync<TMessage>(TMessage message, string groupName, long? excludeSessionId = null)
        where TMessage : IInvocationMessage, IMessageBase
    {
        var pass = new FirstPass();
        WriteGroup(message, groupName, excludeSessionId, ref pass);
        return pass.Finish(message);
    }

    public ValueTask InvokeGroupsAsync<TMessage>(TMessage message, string[] groupNames, long? excludeSessionId = null)
        where TMessage : IInvocationMessage, IMessageBase
    {
        var pass = new FirstPass();
        for (int i = 0; i < groupNames.Length; i++)
            WriteGroup(message, groupNames[i], excludeSessionId, ref pass);

        return pass.Finish(message);
    }

    /// <summary>
    /// First pass for the sessions of one group.
    /// </summary>
    private void WriteGroup<TMessage>(TMessage message, string groupName, long? excludeSessionId, ref FirstPass pass)
        where TMessage : IInvocationMessage, IMessageBase
    {
        if (!_context.GroupIdDictionary.TryGetValue(groupName, out int id))
            return;

        if (!_context.SessionGroups.TryGetValue(id, out var group))
            return;

        foreach (var session in group.Sessions)
        {
            if (!excludeSessionId.HasValue || session.Id != excludeSessionId)
                pass.TryWrite(session, message);
        }
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }
}
