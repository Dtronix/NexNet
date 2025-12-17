using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;

namespace NexNet.Invocation;

/// <summary>
/// Registry for managing session lifecycle across the server infrastructure.
/// </summary>
internal interface ISessionRegistry
{
    /// <summary>
    /// Registers a session with the registry.
    /// </summary>
    /// <param name="session">Session to register.</param>
    /// <returns>True if registration succeeded, false if session already exists.</returns>
    ValueTask<bool> RegisterSessionAsync(INexusSession session);

    /// <summary>
    /// Unregisters a session from the registry.
    /// </summary>
    /// <param name="session">Session to unregister.</param>
    ValueTask UnregisterSessionAsync(INexusSession session);

    /// <summary>
    /// Attempts to get a session by ID.
    /// </summary>
    /// <param name="sessionId">The session ID to look up.</param>
    /// <returns>The session if found, null otherwise.</returns>
    ValueTask<INexusSession?> GetSessionAsync(long sessionId);

    /// <summary>
    /// Checks if a session exists locally on this server.
    /// </summary>
    /// <param name="sessionId">The session ID to check.</param>
    /// <returns>True if session exists locally.</returns>
    ValueTask<bool> SessionExistsAsync(long sessionId);

    /// <summary>
    /// Gets all locally connected sessions.
    /// For local implementation, returns all sessions.
    /// For backplane implementation, returns only sessions on this server.
    /// </summary>
    IEnumerable<INexusSession> LocalSessions { get; }

    /// <summary>
    /// Gets the count of all sessions (local only for local impl, global for backplane).
    /// </summary>
    ValueTask<long> GetSessionCountAsync();

    /// <summary>
    /// Gets all session IDs (local only for local impl).
    /// </summary>
    ValueTask<IReadOnlyCollection<long>> GetSessionIdsAsync();

    /// <summary>
    /// Initializes the registry. Called when the server starts.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the registry. Called when the server stops.
    /// </summary>
    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
}
