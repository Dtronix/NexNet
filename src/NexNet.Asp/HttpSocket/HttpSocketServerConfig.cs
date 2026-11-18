using System.Threading;
using System.Threading.Tasks;
using NexNet.Transports;

namespace NexNet.Asp.HttpSocket;

/// <summary>
/// Configurations for the server to allow connections from HttpSockeet NexNet clients.
/// </summary>
public class HttpSocketServerConfig : AspServerConfig
{
    /// <inheritdoc />
    protected internal override string TransportName => "httpsocket";

    /// <summary>
    /// Name of a query-string parameter (for example <c>"access_token"</c>) that carries a bearer token for
    /// clients that cannot send an <c>Authorization</c> header. Null (the default) disables it.
    /// </summary>
    /// <remarks>
    /// Only used with <see cref="AspServerConfig.AspEnableAuthentication"/>. When the request has no
    /// <c>Authorization</c> header, the value is promoted to <c>Authorization: Bearer &lt;token&gt;</c> before the
    /// ASP.NET authentication scheme runs, and the parameter is removed from the request's query string. NexNet
    /// never logs the value, but URLs can be recorded by proxies and ASP.NET request logging; prefer the NexNet
    /// greeting token (<c>ClientConfig.Authenticate</c>) where possible. Origins are restricted with
    /// <see cref="HttpSocketOptions.AllowedOrigins"/>.
    /// </remarks>
    public string? AccessTokenQueryParameter { get; set; }

    /// <inheritdoc />
    protected override ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<ITransportListener?>(null);
    }
}
