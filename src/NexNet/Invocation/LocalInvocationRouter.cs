using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;

namespace NexNet.Invocation;

/// <summary>
/// Local (single-server) implementation of IInvocationRouter.
/// </summary>
internal sealed class LocalInvocationRouter : IInvocationRouter
{
    private readonly LocalSessionContext _context;

    public LocalInvocationRouter(LocalSessionContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async ValueTask InvokeAllAsync<TMessage>(TMessage message)
        where TMessage : IInvocationMessage, IMessageBase
    {
        foreach (var (_, session) in _context.Sessions)
        {
            message.InvocationId = session.SessionInvocationStateManager.GetNextId(false);

            try
            {
                await session.SendMessage(message).ConfigureAwait(false);
            }
            catch
            {
                // Fire-and-forget: ignore send failures
            }
        }
    }

    public async ValueTask InvokeAllExceptAsync<TMessage>(TMessage message, long excludeSessionId)
        where TMessage : IInvocationMessage, IMessageBase
    {
        foreach (var (id, session) in _context.Sessions)
        {
            if (id == excludeSessionId)
                continue;

            message.InvocationId = session.SessionInvocationStateManager.GetNextId(false);

            try
            {
                await session.SendMessage(message).ConfigureAwait(false);
            }
            catch
            {
                // Fire-and-forget: ignore send failures
            }
        }
    }

    public async ValueTask<bool> InvokeClientAsync<TMessage>(TMessage message, long sessionId)
        where TMessage : IInvocationMessage, IMessageBase
    {
        if (!_context.Sessions.TryGetValue(sessionId, out var session))
            return false;

        message.InvocationId = session.SessionInvocationStateManager.GetNextId(false);

        try
        {
            await session.SendMessage(message).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask InvokeClientsAsync<TMessage>(TMessage message, long[] sessionIds)
        where TMessage : IInvocationMessage, IMessageBase
    {
        for (int i = 0; i < sessionIds.Length; i++)
        {
            if (_context.Sessions.TryGetValue(sessionIds[i], out var session))
            {
                message.InvocationId = session.SessionInvocationStateManager.GetNextId(false);

                try
                {
                    await session.SendMessage(message).ConfigureAwait(false);
                }
                catch
                {
                    // Fire-and-forget: ignore send failures
                }
            }
        }
    }

    public async ValueTask InvokeGroupAsync<TMessage>(TMessage message, string groupName, long? excludeSessionId = null)
        where TMessage : IInvocationMessage, IMessageBase
    {
        if (!_context.GroupIdDictionary.TryGetValue(groupName, out int id))
            return;

        if (!_context.SessionGroups.TryGetValue(id, out var group))
            return;

        foreach (var session in group.Sessions)
        {
            if (excludeSessionId.HasValue && session.Id == excludeSessionId)
                continue;

            message.InvocationId = session.SessionInvocationStateManager.GetNextId(false);

            try
            {
                await session.SendMessage(message).ConfigureAwait(false);
            }
            catch
            {
                // Fire-and-forget: ignore send failures
            }
        }
    }

    public async ValueTask InvokeGroupsAsync<TMessage>(TMessage message, string[] groupNames, long? excludeSessionId = null)
        where TMessage : IInvocationMessage, IMessageBase
    {
        for (int i = 0; i < groupNames.Length; i++)
        {
            await InvokeGroupAsync(message, groupNames[i], excludeSessionId).ConfigureAwait(false);
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
