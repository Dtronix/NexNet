namespace NexNet;

/// <summary>
/// One place a server accepts sessions from: a listener added with
/// <see cref="NexusServer{TServerNexus,TClientProxy}.AddListener"/>, an external endpoint added with
/// <see cref="NexusServer{TServerNexus,TClientProxy}.AddExternalEndpoint"/>, or the endpoint built from the
/// server's primary configuration (named after its transport, for example <c>tcp</c> or <c>websocket</c>).
/// </summary>
/// <param name="Name">Name of the endpoint; unique within its server. Reported as the <c>nexnet.endpoint</c> metric
/// tag and in the server's log path.</param>
/// <param name="Transport">Transport name, as in the <c>nexnet.transport</c> metric tag (<c>tcp</c>, <c>tls</c>,
/// <c>uds</c>, <c>quic</c>, <c>websocket</c>, <c>httpsocket</c>, ...).</param>
/// <param name="IsSecure">True when the connection is encrypted by the transport (TLS, QUIC). For ASP.NET endpoints
/// the endpoint itself reports false, and a session's <c>Context.Endpoint</c> reports
/// <c>HttpRequest.IsHttps</c> of the request that opened it.</param>
/// <param name="Path">The request path of an ASP.NET endpoint (WebSocket, HttpSocket); null otherwise.</param>
public sealed record NexusEndpointInfo(string Name, string Transport, bool IsSecure, string? Path);
