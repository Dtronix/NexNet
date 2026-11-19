using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexNet.Interop.Remote;

/// <summary>
/// The runner's listening end (control protocol §2): listens on <c>127.0.0.1:0</c>, accepts a host's connection and
/// checks its hello (protocol version, token, language and, when given, the schema hash).
/// </summary>
public sealed class ControlListener : IDisposable
{
    private readonly TcpListener _listener;

    private ControlListener(TcpListener listener)
    {
        _listener = listener;
    }

    /// <summary>The bound endpoint.</summary>
    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>The value of the host's <c>--control</c> argument: <c>127.0.0.1:&lt;port&gt;</c>.</summary>
    public string ControlArgument => "127.0.0.1:" + EndPoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Starts listening on an ephemeral loopback port.</summary>
    public static ControlListener Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new ControlListener(listener);
    }

    /// <summary>A new launch token: 64 lower-case hex characters.</summary>
    public static string CreateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Accepts the next connection and checks its hello. On a bad hello the connection gets <c>bye</c> and is closed,
    /// and the method throws.
    /// </summary>
    /// <param name="token">The token the host was launched with.</param>
    /// <param name="language">The expected language, or null to accept any.</param>
    /// <param name="expectedSchemaHash">The expected schema hash, or null to skip the check.</param>
    /// <param name="rebuildCommand">The command that rebuilds the host, for the schema mismatch message.</param>
    /// <param name="transcript">The transcript of the new connection, or null.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <exception cref="ControlProtocolException">The hello is invalid or does not match.</exception>
    public async Task<ControlConnection> AcceptAsync(
        string token,
        string? language,
        string? expectedSchemaHash,
        string? rebuildCommand = null,
        ControlTranscript? transcript = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        client.NoDelay = true;
        var stream = client.GetStream();
        var helloRead = false;
        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var frame = await ControlFraming.ReadAsync(stream, helloTimeout.Token).ConfigureAwait(false)
                        ?? throw new ControlProtocolException("The host closed the control connection before its hello.");
            helloRead = true;

            JsonObject message;
            try
            {
                message = JsonNode.Parse(frame.Payload) as JsonObject ?? throw new ControlProtocolException("The hello is not a JSON object.");
            }
            catch (JsonException e)
            {
                throw new ControlProtocolException("The hello is not valid JSON: " + e.Message, e);
            }

            var hello = HostHello.Parse(message);
            Check(hello, token, language, expectedSchemaHash, rebuildCommand);
            transcript?.Record(null, "<<", message.ToJsonString().Replace(hello.Token, "***", StringComparison.Ordinal));
            var connection = new ControlConnection(stream, hello, transcript);
            connection.Start();
            return connection;
        }
        catch (Exception e)
        {
            if (helloRead && e is ControlProtocolException refusal)
                refusal.HelloRefused = true;
            await SendByeAsync(stream, e.Message).ConfigureAwait(false);
            client.Dispose();
            throw;
        }
    }

    /// <summary>Checks a hello against what the runner expects.</summary>
    /// <exception cref="ControlProtocolException">It does not match.</exception>
    public static void Check(HostHello hello, string token, string? language, string? expectedSchemaHash, string? rebuildCommand)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (hello.Proto != HostHello.ProtocolVersion)
            throw new ControlProtocolException($"The host speaks control protocol {hello.Proto}; this runner speaks {HostHello.ProtocolVersion}.");

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Token), Encoding.UTF8.GetBytes(token)))
            throw new ControlProtocolException("The host's hello has the wrong token.");

        if (language != null && !string.Equals(hello.Lang, language, StringComparison.Ordinal))
            throw new ControlProtocolException($"The host reports language '{hello.Lang}', expected '{language}'.");

        if (expectedSchemaHash != null && !string.Equals(hello.SchemaHash, expectedSchemaHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ControlProtocolException(
                $"The {hello.Lang} language host was built from TestNexus IR {(hello.SchemaHash.Length == 0 ? "(none)" : hello.SchemaHash)}, "
                + $"but the current IR is {expectedSchemaHash}.{Environment.NewLine}Rebuild it: {rebuildCommand ?? HostBuildCommands.For(hello.Lang)}");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _listener.Stop();

    private static async Task SendByeAsync(Stream stream, string reason)
    {
        try
        {
            var bye = Encoding.UTF8.GetBytes(new JsonObject { ["t"] = "bye", ["reason"] = reason }.ToJsonString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await ControlFraming.WriteAsync(stream, ControlFrameKind.Json, bye, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
