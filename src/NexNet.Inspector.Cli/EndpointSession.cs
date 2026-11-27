using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using NexNet.Inspector.Tap;
using NexNet.Messages;
using NexNet.Transports;

namespace NexNet.Inspector.Cli;

/// <summary>Connecting to an inspector endpoint, with failures reported as text instead of exceptions.</summary>
internal static class EndpointSession
{
    /// <summary>Connects; on failure writes why to <see cref="TapCli.Error"/> and returns null.</summary>
    public static async Task<InspectorClient?> ConnectAsync(TapCli cli, CliArguments args, CancellationToken cancellationToken)
    {
        try
        {
            var config = await args.Endpoint.CreateClientConfigAsync(args.Insecure, args.ServerName, args.ConnectTimeout,
                cancellationToken).ConfigureAwait(false);
            return await InspectorClient.ConnectAsync(config, Encoding.UTF8.GetBytes(args.Token!), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InspectorConnectionException e) when (e.Reason == DisconnectReason.Authentication && e.InnerException is TransportException)
        {
            // NexNet reports a failed TLS handshake with the same reason as a refused token; the transport error tells them apart.
            await cli.Error.WriteLineAsync(
                $"nexnet-tap: the TLS handshake with {args.Endpoint} failed: {e.InnerException.Message} "
                + "For a self-signed development certificate pass --insecure; for a name mismatch pass --server-name.")
                .ConfigureAwait(false);
        }
        catch (InspectorConnectionException e) when (e.Reason == DisconnectReason.Authentication)
        {
            await cli.Error.WriteLineAsync($"nexnet-tap: {args.Endpoint} refused the token.").ConfigureAwait(false);
        }
        catch (InspectorConnectionException e)
        {
            var detail = e.InnerException is { } inner ? $": {inner.Message}" : "";
            await cli.Error.WriteLineAsync($"nexnet-tap: could not connect to {args.Endpoint} ({e.State}, {e.Reason}){detail}")
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or AuthenticationException or IOException)
        {
            await cli.Error.WriteLineAsync($"nexnet-tap: could not connect to {args.Endpoint}: {e.Message}").ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>The message for a refused request; the recording-policy wording matches the app's tooltip.</summary>
    public static string Describe(InspectorStatus status) => status switch
    {
        InspectorStatus.RecordingDisabled => "Recording disabled by server config",
        InspectorStatus.NoSuchSession => "no live session has that ID",
        InspectorStatus.NoConnection => "the inspected client has no live connection",
        InspectorStatus.WrongTarget => "the target does not match the inspected side",
        InspectorStatus.Busy => "the endpoint connection already streams",
        InspectorStatus.Unavailable => "the inspector was disposed",
        _ => status.ToString(),
    };

    public static ExitCode ToExitCode(InspectorStatus status) => status switch
    {
        InspectorStatus.RecordingDisabled => ExitCode.Refused,
        InspectorStatus.NoSuchSession or InspectorStatus.NoConnection => ExitCode.NotFound,
        _ => ExitCode.Failed,
    };
}
