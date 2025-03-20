using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using NexNet.Transports.HttpSocket;

namespace NexNet.Asp.HttpSocket;

/// <summary>
/// Enables accepting HttpSocket requests by adding a <see cref="IHttpSocketFeature"/>
/// to the <see cref="HttpContext"/> if the request is a valid HttpSocket request.
/// </summary>
public class HttpSocketMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger _logger;
    private readonly bool _anyOriginAllowed;
    private readonly List<string> _allowedOrigins;
    private readonly HttpSocketOptions _options;

    /// <summary>
    /// Creates a new instance of the <see cref="HttpSocketMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="options">The configuration options.</param>
    /// <param name="loggerFactory">An <see cref="ILoggerFactory"/> instance used to create loggers.</param>
    public HttpSocketMiddleware(RequestDelegate next, ILoggerFactory loggerFactory, HttpSocketOptions options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);

        _next = next;
        _options = options;
        _allowedOrigins = _options.AllowedOrigins.Select(o => o.ToLowerInvariant()).ToList();
        _anyOriginAllowed = _options.AllowedOrigins.Count == 0 || _options.AllowedOrigins.Contains("*", StringComparer.Ordinal);

        _logger = loggerFactory.CreateLogger<HttpSocketMiddleware>();

        // TODO: validate options.
    }

    /// <summary>
    /// Processes a request to determine if it is a HttpSocket request, and if so,
    /// sets the <see cref="IHttpSocketFeature"/> on the <see cref="HttpContext.Features"/>.
    /// </summary>
    /// <param name="context">The <see cref="HttpContext"/> representing the request.</param>
    /// <returns>The <see cref="Task"/> that represents the completion of the middleware pipeline.</returns>
    public Task Invoke(HttpContext context)
    {
        // Detect if an opaque upgrade is available. If so, add a httpsocket upgrade.
        var upgradeFeature = context.Features.Get<IHttpUpgradeFeature>();
        var connectFeature = context.Features.Get<IHttpExtendedConnectFeature>();
        if ((upgradeFeature != null || connectFeature != null) && context.Features.Get<IHttpSocketFeature>() == null)
        {
            var webSocketFeature = new HttpSocketHandshake(context, upgradeFeature, connectFeature, _options, _logger);
            context.Features.Set<IHttpSocketFeature>(webSocketFeature);
            if (!_anyOriginAllowed)
            {
                // Check for Origin header
                var originHeader = context.Request.Headers.Origin;

                if (!StringValues.IsNullOrEmpty(originHeader) && webSocketFeature.IsHttpSocketRequest)
                {
                    // Check allowed origins to see if request is allowed
                    if (!_allowedOrigins.Contains(originHeader.ToString(), StringComparer.Ordinal))
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("Request origin {Origin} is not in the list of allowed origins.", originHeader.ToString());
                        }
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }
                }
            }
        }

        return _next(context);
    }
    

    private sealed class HttpSocketHandshake : IHttpSocketFeature
    {
        private readonly HttpContext _context;
        private readonly IHttpUpgradeFeature? _upgradeFeature;
        private readonly IHttpExtendedConnectFeature? _connectFeature;
        private readonly HttpSocketOptions _options;
        private readonly ILogger _logger;
        private bool? _isHttpSocketRequest;
        private bool _isH2HttpSocket;

        public HttpSocketHandshake(HttpContext context, IHttpUpgradeFeature? upgradeFeature, IHttpExtendedConnectFeature? connectFeature, HttpSocketOptions options, ILogger logger)
        {
            _context = context;
            _upgradeFeature = upgradeFeature;
            _connectFeature = connectFeature;
            _options = options;
            _logger = logger;
        }

        public bool IsHttpSocketRequest
        {
            get
            {
                if (_isHttpSocketRequest == null)
                {
                    if (_connectFeature?.IsExtendedConnect == true)
                    {
                        _isH2HttpSocket = HttpMethods.IsConnect(_context.Request.Method)
                                          && string.Equals(_connectFeature.Protocol, HttpHeaderConstants.UpgradeHttpSocket, StringComparison.OrdinalIgnoreCase);
                        _isHttpSocketRequest = _isH2HttpSocket;
                    }
                    else if (_upgradeFeature?.IsUpgradableRequest == true)
                    {
                        _isHttpSocketRequest = CheckSupportedHttpSocketRequest(_context.Request.Method, _context.Request.Headers);
                    }
                    else
                    {
                        _isHttpSocketRequest = false;
                    }
                }
                return _isHttpSocketRequest.Value;
            }
        }
        
        public async Task<HttpSocketDuplexPipe> AcceptAsync()
        {
            if (!IsHttpSocketRequest)
            {
                throw new InvalidOperationException("Not a HttpSocket request.");
            }

            if (!_isH2HttpSocket)
            {
                _context.Response.Headers.Connection = HeaderNames.Upgrade;
                _context.Response.Headers.Upgrade = HttpHeaderConstants.UpgradeHttpSocket;
            }
            
            Stream opaqueTransport;
            // HTTP/2
            if (_isH2HttpSocket)
            {
                // Send the response headers
                opaqueTransport = await _connectFeature!.AcceptAsync();
            }
            // HTTP/1.1
            else
            {
                opaqueTransport = await _upgradeFeature!.UpgradeAsync(); // Sets status code to 101
            }
            
            // Disable request timeout, if there is one, after the httpsocket has been accepted
            _context.Features.Get<IHttpRequestTimeoutFeature>()?.DisableTimeout();
            var lifetime = _context.RequestServices.GetService<IHostApplicationLifetime>();
            
            return new HttpSocketDuplexPipe(opaqueTransport, _context.RequestAborted, lifetime?.ApplicationStopping ?? CancellationToken.None);
        }

        private static bool CheckSupportedHttpSocketRequest(string method, IHeaderDictionary requestHeaders)
        {
            if (!HttpMethods.IsGet(method))
            {
                return false;
            }
            
            var foundHeader = false;

            var values = requestHeaders.GetCommaSeparatedValues(HeaderNames.Upgrade);
            foreach (var value in values)
            {
                if (string.Equals(value, HttpHeaderConstants.UpgradeHttpSocket, StringComparison.OrdinalIgnoreCase))
                {
                    // HttpSockets are long-lived; so if the header values are valid we switch them out for the interned versions.
                    if (values.Length == 1)
                    {
                        requestHeaders.Upgrade = HttpHeaderConstants.UpgradeHttpSocket;
                    }
                    foundHeader = true;
                    break;
                }
            }
            if (!foundHeader)
            {
                return false;
            }
            foundHeader = false;

            values = requestHeaders.GetCommaSeparatedValues(HeaderNames.Connection);
            foreach (var value in values)
            {
                if (string.Equals(value, HeaderNames.Upgrade, StringComparison.OrdinalIgnoreCase))
                {
                    // HttpSockets are long-lived; so if the header values are valid we switch them out for the interned versions.
                    if (values.Length == 1)
                    {
                        requestHeaders.Connection = HeaderNames.Upgrade;
                    }
                    foundHeader = true;
                    break;
                }
            }
            if (!foundHeader)
            {
                return false;
            }

            return true;
        }
    }
}
