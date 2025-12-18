using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Invocation;

/// <summary>
/// Default local (single-server) implementation of ServerSessionManager.
/// Internal - only instantiated by NexusServer when SessionManager is null.
/// </summary>
internal sealed class LocalServerSessionManager
    : ServerSessionManager<LocalSessionRegistry, LocalGroupRegistry, LocalInvocationRouter>
{
    private readonly LocalSessionContext _context;

    /// <summary>
    /// Creates a new LocalServerSessionManager with a fresh context.
    /// </summary>
    public LocalServerSessionManager()
        : this(new LocalSessionContext())
    {
    }

    private LocalServerSessionManager(LocalSessionContext context)
        : base(
            new LocalSessionRegistry(context),
            new LocalGroupRegistry(context),
            new LocalInvocationRouter(context))
    {
        _context = context;
    }

    /// <summary>
    /// Shuts down and clears all state.
    /// </summary>
    public override async ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await base.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        _context.Clear();
    }
}
