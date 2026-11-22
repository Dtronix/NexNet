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
        => ForwardToAsync(source, destination, RemoteCancel.None);

    /// <summary>
    /// <see cref="ForwardToAsync(IRemotePipe, IRemotePipe)"/> that can be canceled (control protocol §6.7 <c>pipe.forward</c>
    /// with <c>cancel</c>; C# <c>ForwardToAsync(destination, cancellationToken)</c>, Java
    /// <c>forwardTo(destination, cancellationToken)</c>, JS <c>forwardTo(destination, { signal })</c>). A canceled forward
    /// stops and completes neither pipe; the bytes it did not send stay in the source.
    /// </summary>
    /// <param name="source">The pipe whose input is forwarded.</param>
    /// <param name="destination">A pipe of the same client, or of another client of the same driver.</param>
    /// <param name="cancel">Cancels the forward: a host-side timer, or a local token.</param>
    /// <returns>A task which completes when the forward ended, as <see cref="ForwardToAsync(IRemotePipe, IRemotePipe)"/>.</returns>
    /// <exception cref="TaskCanceledException">The forward was canceled.</exception>
    /// <exception cref="NotSupportedException">The pipes are not pipes of one harness driver.</exception>
    public static ValueTask ForwardToAsync(this IRemotePipe source, IRemotePipe destination, RemoteCancel cancel)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(cancel);
        return (source, destination) switch
        {
            (Remote.RemotePipe remote, Remote.RemotePipe to) => remote.ForwardToAsync(to, cancel),
            (InProc.InProcRemotePipe inProc, InProc.InProcRemotePipe to) => inProc.ForwardToAsync(to, cancel),
            _ => throw new NotSupportedException(
                $"{source.GetType()} cannot forward to {destination.GetType()}: both must be pipes of one harness driver."),
        };
    }

    /// <summary>
    /// Runs the client runtime's pipe bridge in the client under test (control protocol §6.7 <c>pipe.bridge</c>; C#
    /// <c>NexusPipeForwardingExtensions.BridgeAsync</c>, Java <c>NexusDuplexPipe.bridge</c>, JS
    /// <c>NexusDuplexPipe.bridge</c>): forwards <paramref name="a"/> to <paramref name="b"/> and <paramref name="b"/> to
    /// <paramref name="a"/> concurrently, each direction half-closing its destination when its source's input completes.
    /// If either pipe ends abnormally while a direction still runs, both pipes are completed. Bytes an earlier
    /// <see cref="IRemotePipe.ReadAsync"/> left unread on either pipe are forwarded first. Do not read or write either
    /// pipe while it runs.
    /// </summary>
    /// <param name="a">First pipe.</param>
    /// <param name="b">A pipe of the same client, or of another client of the same driver.</param>
    /// <returns>A task which completes when the bridge ended: both directions finished, or a pipe ended abnormally.</returns>
    /// <exception cref="NotSupportedException">The pipes are not pipes of one harness driver.</exception>
    public static ValueTask BridgeAsync(this IRemotePipe a, IRemotePipe b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return (a, b) switch
        {
            (Remote.RemotePipe remote, Remote.RemotePipe other) => remote.BridgeAsync(other),
            (InProc.InProcRemotePipe inProc, InProc.InProcRemotePipe other) => inProc.BridgeAsync(other),
            _ => throw new NotSupportedException(
                $"{a.GetType()} cannot bridge to {b.GetType()}: both must be pipes of one harness driver."),
        };
    }
}
