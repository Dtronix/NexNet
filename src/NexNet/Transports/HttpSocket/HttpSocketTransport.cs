﻿using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.HttpSocket;

internal class HttpSocketTransport : ITransport
{
    private readonly HttpSocketDuplexPipe _pipe;
    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    public HttpSocketTransport(HttpSocketDuplexPipe pipe)
    {
        _pipe = pipe;
        Input = pipe.Input;
        Output = pipe.Output;
    }
    
    public ValueTask CloseAsync(bool linger)
    {
        return _pipe.CompleteAsync();
    }

    public void Dispose()
    {
        _pipe.DisposeAsync();
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    /// 
    internal static async ValueTask<ITransport> ConnectAsync(
        HttpSocketClientConfig config,
        CancellationToken cancellationToken)
    {

        using var timeoutCancellation = new CancellationTokenSource(config.ConnectionTimeout);
        using var cancellationTokenRegistration =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        try
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
            };

            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };

            var message = new HttpRequestMessage(HttpMethod.Get, config.Url)
            {
                Version = HttpVersion.Version11, 
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            //message.Headers.Host
            message.Headers.Host = config.Url.Host;
            message.Headers.Connection.Add("Upgrade");
            message.Headers.Upgrade.Add(new ProductHeaderValue("nexnet-httpsockets"));
            message.Headers.Authorization = config.AuthenticationHeader;
            var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationTokenRegistration.Token)
                .ConfigureAwait(false);
            
            // the protocol should be switching.  If not, then check for error.
            if(response.StatusCode != HttpStatusCode.SwitchingProtocols)
                response.EnsureSuccessStatusCode();
      
            // Don't cancel the stream from the passed cancellation token as it is only valid
            // until the connection has been completed.
            // ReSharper disable once MethodSupportsCancellation
            var connectedStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
  
            var pipe = new HttpSocketDuplexPipe(connectedStream, false);
            
            return new HttpSocketTransport(pipe);

        }
        catch (HttpRequestException e)
        {
            if (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new TransportException(TransportError.AuthenticationError,
                    $"Http connection authentication failed with stats code: {e.StatusCode}", e);
            
            if(e.StatusCode == HttpStatusCode.InternalServerError)
                throw new TransportException(TransportError.InternalError,
                    $"Http connection authentication failed with stats code: {e.StatusCode}", e);
            
            throw new TransportException(GetTransportError(e.HttpRequestError), e.Message, e);
        }
        catch (TaskCanceledException e)
        {
            throw new TransportException(TransportError.ConnectionTimeout, e.Message, e);
        }
        catch (Exception e)
        {
            throw new TransportException(TransportError.ConnectionRefused, e.Message, e);
        }
    }
    

    private static TransportError GetTransportError(HttpRequestError error)
    {
        return error switch
        {
            HttpRequestError.Unknown => TransportError.InternalError,
            HttpRequestError.NameResolutionError => TransportError.Unreachable,
            HttpRequestError.ConnectionError => TransportError.ConnectionTimeout,
            HttpRequestError.SecureConnectionError => TransportError.ProtocolError,
            HttpRequestError.HttpProtocolError => TransportError.ProtocolError,
            HttpRequestError.ExtendedConnectNotSupported => TransportError.ProtocolError,
            HttpRequestError.VersionNegotiationError => TransportError.VersionNegotiationError,
            HttpRequestError.UserAuthenticationError => TransportError.AuthenticationError,
            HttpRequestError.ProxyTunnelError => TransportError.Unreachable,
            HttpRequestError.InvalidResponse => TransportError.ProtocolError,
            HttpRequestError.ResponseEnded => TransportError.ConnectionAborted,
            HttpRequestError.ConfigurationLimitExceeded => TransportError.InternalError,
            _ => throw new ArgumentOutOfRangeException(nameof(error), error, null)
        };
    }
}
