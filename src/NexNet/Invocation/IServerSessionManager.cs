using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Invocation;

/// <summary>
/// Non-generic base interface for ServerSessionManager.
/// Used by ServerConfig to hold any ServerSessionManager variant.
/// </summary>
internal interface IServerSessionManager
{
    /// <summary>
    /// The session registry.
    /// </summary>
    ISessionRegistry Sessions { get; }

    /// <summary>
    /// The group registry.
    /// </summary>
    IGroupRegistry Groups { get; }

    /// <summary>
    /// The invocation router.
    /// </summary>
    IInvocationRouter Router { get; }

    /// <summary>
    /// Initializes all components.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down all components.
    /// </summary>
    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
}
