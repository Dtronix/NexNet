using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Transports;

namespace NexNet.Asp.WebSocket;
/// <summary>
/// Configurations for the server to allow connections from WebSocket NexNet clients.
/// </summary>
public class WebSocketServerConfig : AspServerConfig
{
    /// <inheritdoc />
    protected internal override string TransportName => "websocket";

    /// <summary>
    /// Origin header values allowed to open the WebSocket, to prevent Cross-Site WebSocket Hijacking
    /// (for example <c>https://app.example.com</c>). Compared case-insensitively. Empty (the default) or a
    /// <c>*</c> entry allows every origin. Requests without an Origin header (non-browser clients) are always
    /// allowed. A rejected request is answered 403. Set this whenever the server uses cookie authentication.
    /// </summary>
    public IList<string> AllowedOrigins { get; } = new List<string>();

    /// <summary>
    /// Name of a query-string parameter (for example <c>"access_token"</c>) that carries a bearer token for
    /// clients that cannot send an <c>Authorization</c> header, such as browsers. Null (the default) disables it.
    /// </summary>
    /// <remarks>
    /// Only used with <see cref="AspServerConfig.AspEnableAuthentication"/>. When the request has no
    /// <c>Authorization</c> header, the value is promoted to <c>Authorization: Bearer &lt;token&gt;</c> before the
    /// ASP.NET authentication scheme runs, and the parameter is removed from the request's query string. NexNet
    /// never logs the value, but URLs can be recorded by proxies and ASP.NET request logging; prefer the NexNet
    /// greeting token (<c>ClientConfig.Authenticate</c>) where possible.
    /// </remarks>
    public string? AccessTokenQueryParameter { get; set; }

    /// <inheritdoc />
    protected override ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<ITransportListener?>(null);
    }

}
