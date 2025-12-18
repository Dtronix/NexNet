using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Invocation;

/// <summary>
/// Abstract base class for session managers that combines session registry, group registry, and invocation routing.
/// Extend this class to create custom implementations (e.g., backplane-backed).
/// </summary>
/// <typeparam name="TSessionRegistry">Session registry implementation.</typeparam>
/// <typeparam name="TGroupRegistry">Group registry implementation.</typeparam>
/// <typeparam name="TInvocationRouter">Invocation router implementation.</typeparam>
internal abstract class ServerSessionManager<TSessionRegistry, TGroupRegistry, TInvocationRouter>
    : IServerSessionManager
    where TSessionRegistry : ISessionRegistry
    where TGroupRegistry : IGroupRegistry
    where TInvocationRouter : IInvocationRouter
{
    /// <summary>
    /// The session registry implementation.
    /// </summary>
    public TSessionRegistry Sessions { get; }

    /// <summary>
    /// The group registry implementation.
    /// </summary>
    public TGroupRegistry Groups { get; }

    /// <summary>
    /// The invocation router implementation.
    /// </summary>
    public TInvocationRouter Router { get; }

    // Explicit interface implementation for non-generic access
    ISessionRegistry IServerSessionManager.Sessions => Sessions;
    IGroupRegistry IServerSessionManager.Groups => Groups;
    IInvocationRouter IServerSessionManager.Router => Router;

    /// <summary>
    /// Creates a new ServerSessionManager with the specified implementations.
    /// </summary>
    protected ServerSessionManager(
        TSessionRegistry sessionRegistry,
        TGroupRegistry groupRegistry,
        TInvocationRouter invocationRouter)
    {
        Sessions = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        Groups = groupRegistry ?? throw new ArgumentNullException(nameof(groupRegistry));
        Router = invocationRouter ?? throw new ArgumentNullException(nameof(invocationRouter));
    }

    /// <summary>
    /// Initializes all components. Called when the server starts.
    /// Override to add custom initialization logic.
    /// </summary>
    public virtual async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Sessions.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Groups.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Router.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shuts down all components. Called when the server stops.
    /// Override to add custom shutdown logic.
    /// </summary>
    public virtual async ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await Router.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        await Groups.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        await Sessions.ShutdownAsync(cancellationToken).ConfigureAwait(false);
    }
}
