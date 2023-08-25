namespace NexNet.Transports;

/// <summary>
/// Contains configuration options for a transport.
/// </summary>
public class TransportConfiguration
{
    /// <summary>
    /// If true, the transport will not pass the flush cancellation token to the underlying transport.
    /// Currently exists due to an issue in the QUIC implementation.  Should be removed once the issue is fixed.
    /// </summary>
    /// <remarks>
    /// https://github.com/dotnet/runtime/issues/82704
    /// https://github.com/dotnet/runtime/pull/90253
    /// </remarks>
    public bool DoNotPassFlushCancellationToken { get; init; } = false;
}
