using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// Interface for accepting an external transport connection used by servers
/// </summary>
public interface IAcceptsExternalTransport
{
    /// <summary>
    /// Accept an already connected ITransport for a connection.
    /// </summary>
    /// <param name="transport">Transport to accept.</param>
    /// <param name="cancellationToken">Cancellation token to invoke upon transport completion.</param>
    /// <returns>Talk that completed upon the Client's completion.  Could be successful closure or exception.</returns>
    public ValueTask AcceptTransport(ITransport transport, CancellationToken cancellationToken = default);
}
