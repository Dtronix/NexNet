namespace NexNet.Interop;

/// <summary>Harness-only operations on an <see cref="IRemotePipe"/> of a client under test.</summary>
public static class RemotePipeExtensions
{
    /// <summary>
    /// Runs the client runtime's pipe forward in the client under test (control protocol §6.7 <c>pipe.forward</c>; C#
    /// <c>NexusPipeForwardingExtensions.ForwardToAsync</c>, Java <c>NexusDuplexPipe.forwardTo</c>, JS
    /// <c>NexusDuplexPipe.forwardTo</c>): copies everything <paramref name="source"/> receives to
    /// <paramref name="destination"/> until the source's input completes, then completes the destination's writing side
    /// (half-close). Bytes an earlier <see cref="IRemotePipe.ReadAsync"/> left unread are forwarded first. Do not read
    /// the source or write the destination while it runs.
    /// </summary>
    /// <param name="source">The pipe whose input is forwarded.</param>
    /// <param name="destination">A pipe of the same client, or of another client of the same driver.</param>
    /// <returns>
    /// A task which completes when the forward ended: the source's input completed and the destination was half-closed,
    /// or the destination's writing side was completed first (the source is then left open).
    /// </returns>
    /// <exception cref="NotSupportedException">The pipes are not pipes of one harness driver.</exception>
    public static ValueTask ForwardToAsync(this IRemotePipe source, IRemotePipe destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        return (source, destination) switch
        {
            (Remote.RemotePipe remote, Remote.RemotePipe to) => remote.ForwardToAsync(to),
            (InProc.InProcRemotePipe inProc, InProc.InProcRemotePipe to) => inProc.ForwardToAsync(to),
            _ => throw new NotSupportedException(
                $"{source.GetType()} cannot forward to {destination.GetType()}: both must be pipes of one harness driver."),
        };
    }
}
