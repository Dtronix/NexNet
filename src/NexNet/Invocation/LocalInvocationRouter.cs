using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Pools;

namespace NexNet.Invocation;

/// <summary>
/// Local (single-server) implementation of IInvocationRouter.
/// </summary>
/// <remarks>
/// An invocation for several sessions is sent in two passes. The first writes it, without waiting, to every session
/// that can take it now (<see cref="INexusSession.TryWriteInvocation{TMessage}"/>). The second waits for the sessions
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
    /// First pass for one session: writes the invocation if the session can take it now, otherwise adds the
    /// session to <paramref name="busy"/>.
    /// </summary>
    private static void TryWrite<TMessage>(INexusSession session, TMessage message, ref List<INexusSession>? busy)
        where TMessage : IInvocationMessage, IMessageBase
    {
        try
        {
            if (session.TryWriteInvocation(message) == InvocationWriteResult.Busy)
                (busy ??= ListPool<INexusSession>.Rent()).Add(session);
        }
        catch
        {
            // Fire-and-forget: ignore send failures
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
        List<INexusSession>? busy = null;
        foreach (var (_, session) in _context.Sessions)
            TryWrite(session, message, ref busy);

        return SendToBusy(message, busy);
    }

    public ValueTask InvokeAllExceptAsync<TMessage>(TMessage message, long excludeSessionId)
        where TMessage : IInvocationMessage, IMessageBase
    {
        List<INexusSession>? busy = null;
        foreach (var (id, session) in _context.Sessions)
        {
            if (id != excludeSessionId)
                TryWrite(session, message, ref busy);
        }

        return SendToBusy(message, busy);
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
        List<INexusSession>? busy = null;
        for (int i = 0; i < sessionIds.Length; i++)
        {
            if (_context.Sessions.TryGetValue(sessionIds[i], out var session))
                TryWrite(session, message, ref busy);
        }

        return SendToBusy(message, busy);
    }

    public ValueTask InvokeGroupAsync<TMessage>(TMessage message, string groupName, long? excludeSessionId = null)
        where TMessage : IInvocationMessage, IMessageBase
    {
        List<INexusSession>? busy = null;
        WriteGroup(message, groupName, excludeSessionId, ref busy);
        return SendToBusy(message, busy);
    }

    public ValueTask InvokeGroupsAsync<TMessage>(TMessage message, string[] groupNames, long? excludeSessionId = null)
        where TMessage : IInvocationMessage, IMessageBase
    {
        List<INexusSession>? busy = null;
        for (int i = 0; i < groupNames.Length; i++)
            WriteGroup(message, groupNames[i], excludeSessionId, ref busy);

        return SendToBusy(message, busy);
    }

    /// <summary>
    /// First pass for the sessions of one group.
    /// </summary>
    private void WriteGroup<TMessage>(TMessage message, string groupName, long? excludeSessionId, ref List<INexusSession>? busy)
        where TMessage : IInvocationMessage, IMessageBase
    {
        if (!_context.GroupIdDictionary.TryGetValue(groupName, out int id))
            return;

        if (!_context.SessionGroups.TryGetValue(id, out var group))
            return;

        foreach (var session in group.Sessions)
        {
            if (!excludeSessionId.HasValue || session.Id != excludeSessionId)
                TryWrite(session, message, ref busy);
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
