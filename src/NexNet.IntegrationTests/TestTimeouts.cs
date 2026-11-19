namespace NexNet.IntegrationTests;

/// <summary>
/// Shared hang guards, in seconds, for steps whose duration depends on machine load rather than on the code under test.
/// </summary>
internal static class TestTimeouts
{
    /// <summary>
    /// Server start and stop, and client connect. A first TLS or QUIC handshake in a cold, loaded process (the suite
    /// runs in parallel) can take more than a second, and msquic retransmits a dropped first packet after about one
    /// second. Tests that expect a connect to fail still fail fast; this only bounds a hang.
    /// </summary>
    public const double Connect = 5;

    /// <summary>
    /// Waiting for a collection change notification (<c>WaitForActionHandler.Wait</c>).
    /// </summary>
    public const double CollectionEvent = 5;

    /// <summary>
    /// Enabling or disabling a synchronized collection from a client: a pipe opened or closed in round trips with the
    /// server. Under the parallel suite's load a 1 s bound can be missed on the slower transports (QUIC, TLS) without
    /// anything being wrong; this only bounds a hang.
    /// </summary>
    public const double CollectionEnable = 5;
}
