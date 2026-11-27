using NexNet.Transports;

namespace NexNet.Inspector.Tap;

/// <summary>
/// A viewer's or the CLI's connection to an inspector endpoint: reads the policy and the session list, then attaches to
/// one session or records them all as a stream of <c>.nntl</c> blocks.
/// </summary>
public sealed class InspectorClient : IAsyncDisposable
{
    private readonly NexusClient<InspectorViewerNexus, InspectorViewerNexus.ServerProxy> _client;

    private InspectorClient(NexusClient<InspectorViewerNexus, InspectorViewerNexus.ServerProxy> client) => _client = client;

    /// <summary>Connects to an endpoint.</summary>
    /// <param name="config">Where the endpoint listens (TCP, TLS or UDS). Its <see cref="ClientConfig.Authenticate"/> is set to send <paramref name="token"/>.</param>
    /// <param name="token">The endpoint's token.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <exception cref="InspectorConnectionException">The endpoint is unreachable or refused the token.</exception>
    public static async Task<InspectorClient> ConnectAsync(ClientConfig config, ReadOnlyMemory<byte> token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var secret = token.ToArray();
        config.Authenticate = () => secret;
        var client = InspectorViewerNexus.CreateClient(config, new InspectorViewerNexus());
        var result = await client.TryConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new InspectorConnectionException(result.State, result.DisconnectReason, result.Exception);
        }

        return new InspectorClient(client);
    }

    /// <summary>The endpoint's API version, role, recording policy and schema hash.</summary>
    public async Task<InspectorServerInfo> GetServerInfoAsync() =>
        await _client.Proxy.GetServerInfo().ConfigureAwait(false);

    /// <summary>The live sessions of the inspected server (or the client's connection).</summary>
    public async Task<InspectorSessionInfo[]> ListSessionsAsync() =>
        await _client.Proxy.ListSessions().ConfigureAwait(false);

    /// <summary>Attaches to one session. The stream shows data from now on, after an optional snapshot.</summary>
    /// <exception cref="InspectorRequestException">The endpoint refused (no such session, recording disabled, busy, …).</exception>
    public async Task<InspectorStream> AttachAsync(InspectorAttachRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var status = await _client.Proxy.Attach(request).ConfigureAwait(false);
        if (status != InspectorStatus.Ok)
            throw new InspectorRequestException(status);
        return await OpenStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records every session (current ones from now, new ones from their first byte) into one stream.</summary>
    /// <exception cref="InspectorRequestException">The endpoint refused (recording disabled, busy, …).</exception>
    public async Task<InspectorStream> RecordAllAsync(InspectorRecordRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var status = await _client.Proxy.RecordAll(request).ConfigureAwait(false);
        if (status != InspectorStatus.Ok)
            throw new InspectorRequestException(status);
        return await OpenStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the current stream. The endpoint still sends what it buffered, a close block per session and the footer, so
    /// keep reading the stream to its end.
    /// </summary>
    public async Task DetachAsync() => await _client.Proxy.Detach().ConfigureAwait(false);

    /// <summary>Disconnects from the endpoint; any stream ends.</summary>
    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<InspectorStream> OpenStreamAsync(CancellationToken cancellationToken)
    {
        var pipe = _client.CreatePipe();
        try
        {
            await _client.Proxy.OpenStream(pipe).ConfigureAwait(false);
            await pipe.ReadyTask.WaitAsync(cancellationToken).ConfigureAwait(false);

            // The pipe can be ready before the endpoint has linked the stream to the session. Its first byte is written
            // after the link, so waiting for it (without consuming) makes "attached" mean ops from here on are seen.
            var first = await pipe.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
            pipe.Input.AdvanceTo(first.Buffer.Start);
            return new InspectorStream(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
