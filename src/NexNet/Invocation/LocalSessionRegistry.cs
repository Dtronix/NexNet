using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;

namespace NexNet.Invocation;

/// <summary>
/// Local (single-server) implementation of ISessionRegistry.
/// </summary>
internal sealed class LocalSessionRegistry : ISessionRegistry
{
    private readonly LocalSessionContext _context;

    public LocalSessionRegistry(LocalSessionContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ValueTask<bool> RegisterSessionAsync(INexusSession session)
    {
        var result = _context.Sessions.TryAdd(session.Id, session);
        return ValueTask.FromResult(result);
    }

    public ValueTask UnregisterSessionAsync(INexusSession session)
    {
        _context.Sessions.TryRemove(session.Id, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<INexusSession?> GetSessionAsync(long sessionId)
    {
        _context.Sessions.TryGetValue(sessionId, out var session);
        return ValueTask.FromResult(session);
    }

    public ValueTask<bool> SessionExistsAsync(long sessionId)
    {
        return ValueTask.FromResult(_context.Sessions.ContainsKey(sessionId));
    }

    public IEnumerable<INexusSession> LocalSessions => _context.Sessions.Values;

    public ValueTask<long> GetSessionCountAsync()
    {
        return ValueTask.FromResult((long)_context.Sessions.Count);
    }

    public ValueTask<IReadOnlyCollection<long>> GetSessionIdsAsync()
    {
        return ValueTask.FromResult<IReadOnlyCollection<long>>(_context.Sessions.Keys.ToArray());
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _context.Sessions.Clear();
        return ValueTask.CompletedTask;
    }
}
