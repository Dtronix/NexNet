using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals.Pipelines;
using NexNet.Transports;

#pragma warning disable CA1416

namespace NexNet.Quic;

internal class QuicTransport : ITransport
{

    private readonly QuicConnection _quicConnection;

    private readonly QuicStream _quicStream;
    private readonly StreamSendPipe _sendPipe;

    public PipeReader Input { get; }
    public PipeWriter Output => _sendPipe.Writer;
    public string? RemoteAddress { get; }
    public int? RemotePort { get; }
    public X509Certificate2? RemoteCertificate => _quicConnection.RemoteCertificate as X509Certificate2;

    private QuicTransport(QuicConnection quicConnection, QuicStream stream, PipeOptions sendPipeOptions)
    {
        _quicConnection = quicConnection;
        _quicStream = stream;
        Input = PipeReader.Create(stream);
        _sendPipe = new StreamSendPipe(stream, sendPipeOptions);

        // Extract from QUIC connection's RemoteEndPoint
        if (quicConnection.RemoteEndPoint is System.Net.IPEndPoint ipEndPoint)
        {
            RemoteAddress = ipEndPoint.Address.ToString();
            RemotePort = ipEndPoint.Port;
        }
        else
        {
            RemoteAddress = quicConnection.RemoteEndPoint?.ToString();
            RemotePort = null;
        }
    }

    public async ValueTask CloseAsync(bool linger)
    {
        if (linger)
        {
            // Let the send loop write what the session flushed before it completed the output.
            await _sendPipe.DrainAsync(StreamSendPipe.DrainTimeout).ConfigureAwait(false);
        }
        else
        {
            _sendPipe.Abort();
        }

        await _quicConnection.CloseAsync(0).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _quicConnection.DisposeAsync();
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    public static async ValueTask<ITransport> CreateFromConnection(QuicConnection connection, QuicServerConfig config)
    {
        var stream = await connection.AcceptInboundStreamAsync(CancellationToken.None).ConfigureAwait(false);
        return new QuicTransport(connection, stream, config.SendSessionPipeOptions);
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    /// 
    public static async ValueTask<ITransport> ConnectAsync(QuicClientConfig clientConfig,
        CancellationToken cancellationToken)
    {
        if (QuicConnection.IsSupported == false)

            throw new PlatformNotSupportedException("QUIC is not supported on this platform.");

        // Set the application protocol.
        clientConfig.SslClientAuthenticationOptions.ApplicationProtocols = new List<SslApplicationProtocol>
        {
            new SslApplicationProtocol("nn1"),
        };
        var connectionOptions = new QuicClientConnectionOptions()
        {
            RemoteEndPoint = clientConfig.EndPoint,
            ClientAuthenticationOptions = clientConfig.SslClientAuthenticationOptions,
            DefaultStreamErrorCode = 0x0A, // Protocol-dependent error code.
            DefaultCloseErrorCode = 0x0B, // Protocol-dependent error code.
        };

        using var timeoutCancellation = new CancellationTokenSource(clientConfig.ConnectionTimeout);
        await using var cancellationTokenRegistration = cancellationToken.Register(timeoutCancellation.Cancel).ConfigureAwait(false);

        QuicConnection quicConnection;
        try
        {
            quicConnection = await QuicConnection.ConnectAsync(connectionOptions, timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (QuicException e)
        {
            throw new TransportException(QuicHelpers.GetTransportError(e.QuicError), e.Message, e);
        }
        catch (Exception e)
        {
            throw new TransportException(TransportError.ConnectionRefused, e.Message, e);
        }

        var mainStream = await quicConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeoutCancellation.Token).ConfigureAwait(false);

        return new QuicTransport(quicConnection, mainStream, clientConfig.SendSessionPipeOptions);
    }

}
