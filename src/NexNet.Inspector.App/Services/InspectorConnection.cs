using System.Net.Sockets;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Tap;
using NexNet.Messages;

namespace NexNet.Inspector.App.Services;

/// <summary>What a reachability probe of an endpoint found (OQ18: the Connect dialog's status line).</summary>
/// <param name="State">Reached, unreachable, refused or invalid.</param>
/// <param name="Address">The parsed address, when the text was one.</param>
/// <param name="Info">The server's policy, when reached.</param>
/// <param name="Message">The status line.</param>
public readonly record struct EndpointProbeResult(ProbeState State, EndpointAddress? Address, InspectorServerInfo? Info, string Message);

/// <summary>Options of a connection to an inspector endpoint.</summary>
/// <param name="Timeout">Bound on the connect and, for TLS, the handshake.</param>
/// <param name="AllowUntrustedCertificate">TLS only: accept any server certificate (a development endpoint's self-signed one).</param>
public readonly record struct EndpointConnectOptions(TimeSpan Timeout, bool AllowUntrustedCertificate = false)
{
    /// <summary>Five seconds, certificates validated.</summary>
    public static EndpointConnectOptions Default => new(TimeSpan.FromSeconds(5));
}

/// <summary>
/// A connection to an inspector endpoint: the server's policy, its sessions, and at most one attach stream at a time
/// (the endpoint streams one attach per connection). A live document owns the connection it attached through.
/// </summary>
public sealed class InspectorConnection : IAsyncDisposable
{
    // Kept so the same endpoint can be reached again on a second connection (Record all streams beside the attach).
    private readonly byte[] _token;
    private readonly EndpointConnectOptions _options;

    private InspectorConnection(EndpointAddress address, InspectorClient client, InspectorServerInfo info, byte[] token,
        EndpointConnectOptions options)
    {
        Address = address;
        Client = client;
        ServerInfo = info;
        _token = token;
        _options = options;
    }

    /// <summary>Where it is connected.</summary>
    public EndpointAddress Address { get; }

    /// <summary>The endpoint client.</summary>
    public InspectorClient Client { get; }

    /// <summary>The policy read when connecting.</summary>
    public InspectorServerInfo ServerInfo { get; }

    /// <summary>Connects and reads the server's policy.</summary>
    /// <exception cref="InspectorConnectionException">Unreachable, or the token was refused.</exception>
    /// <exception cref="SocketException">The host name did not resolve.</exception>
    public static async Task<InspectorConnection> ConnectAsync(EndpointAddress address, ReadOnlyMemory<byte> token,
        EndpointConnectOptions options, CancellationToken cancellationToken = default)
    {
        var config = await address.CreateClientConfigAsync(options.AllowUntrustedCertificate, null, options.Timeout,
            cancellationToken).ConfigureAwait(false);
        var client = await InspectorClient.ConnectAsync(config, token, cancellationToken).ConfigureAwait(false);
        try
        {
            var info = await client.GetServerInfoAsync().WaitAsync(options.Timeout, cancellationToken).ConfigureAwait(false);
            return new InspectorConnection(address, client, info, token.ToArray(), options);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Connects, reads the policy and disconnects: the probe behind the Connect dialog's status line.</summary>
    /// <param name="text">The endpoint as typed.</param>
    /// <param name="defaultScheme">Scheme of an address typed without one.</param>
    /// <param name="token">The token.</param>
    /// <param name="options">Timeout and certificate policy.</param>
    /// <param name="cancellationToken">Cancels the probe (the user typed on).</param>
    /// <exception cref="OperationCanceledException">Canceled.</exception>
    public static async Task<EndpointProbeResult> ProbeAsync(string text, EndpointScheme defaultScheme, ReadOnlyMemory<byte> token,
        EndpointConnectOptions options, CancellationToken cancellationToken = default)
    {
        if (!EndpointAddress.TryParse(text, defaultScheme, out var address, out var error))
            return new EndpointProbeResult(ProbeState.Invalid, null, null, error);

        try
        {
            await using var connection = await ConnectAsync(address, token, options, cancellationToken).ConfigureAwait(false);
            return new EndpointProbeResult(ProbeState.Reached, address, connection.ServerInfo, DescribeReached(address, connection.ServerInfo));
        }
        catch (InspectorConnectionException e) when (e.Reason == DisconnectReason.Authentication)
        {
            return new EndpointProbeResult(ProbeState.Refused, address, null, $"Reached {address} · the token was refused");
        }
        catch (Exception e) when (e is InspectorConnectionException or SocketException or IOException or TimeoutException)
        {
            return new EndpointProbeResult(ProbeState.Unreachable, address, null, $"Could not reach {address}");
        }
    }

    /// <summary>The status line for a reached endpoint.</summary>
    public static string DescribeReached(EndpointAddress address, InspectorServerInfo info) =>
        $"Reached {address} · server policy: recording {(info.AllowRecording ? "allowed" : "disabled")} · "
        + $"secrets {(info.IncludeSecrets ? "included" : "redacted")} · "
        + (info.SchemaHash is { Length: > 0 } ? "schema provided" : "no schema provided");

    /// <summary>
    /// Opens a second connection to the same endpoint with the same token and options. The endpoint streams one attach or
    /// record-all per connection, so recording every session while this one stays attached needs its own.
    /// </summary>
    /// <exception cref="InspectorConnectionException">Unreachable, or the token was refused.</exception>
    public Task<InspectorConnection> ConnectAgainAsync(CancellationToken cancellationToken = default) =>
        ConnectAsync(Address, _token, _options, cancellationToken);

    /// <summary>The live sessions (a server's; a client's single connection).</summary>
    public Task<InspectorSessionInfo[]> ListSessionsAsync() => Client.ListSessionsAsync();

    /// <summary>Disconnects; any stream ends.</summary>
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
