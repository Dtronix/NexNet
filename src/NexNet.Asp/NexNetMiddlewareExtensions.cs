using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexNet.Collections;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Transports;

namespace NexNet.Asp;

/// <summary>
/// Extension methods to simplify adding NexNet servers to ASP.
/// </summary>
public static partial class NexNetMiddlewareExtensions
{
    /// <summary>
    /// Adds a Nexus Server and ClientProxy to the Service collection
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static IServiceCollection AddNexusServer<
        // The DI container constructs the nexus for every connection.
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TServerNexus,
        TClientProxy>(this IServiceCollection services)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        // Add the nexus to the services as a transient service to allow for instancing on each connection
        services.AddTransient<TServerNexus>();
        
        // Adds the server as a singleton to the service collection
        services.AddSingleton<NexusServer<TServerNexus, TClientProxy>>();
        
        // Adds the context provider
        services.AddSingleton<ServerNexusContextProvider<TServerNexus, TClientProxy>>(sp =>
            sp.GetRequiredService<NexusServer<TServerNexus, TClientProxy>>().ContextProvider);

        return services;
    }

    /// <summary>
    /// Adds a Nexus Server configured by <paramref name="configure"/> to the service collection, with a hosted service
    /// that starts it once the application has started (after Kestrel binds) and stops it when the application stops.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="configure">Sets the primary configuration and adds listener endpoints, for example
    /// <c>o => { o.Configure(primary); o.AddListener(tcp, "tcp"); }</c>. Runs when the server is first resolved.
    /// WebSocket and HttpSocket endpoints are mapped on the app with <c>MapNexusWebSocket</c> and
    /// <c>MapNexusHttpSocket</c>.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static IServiceCollection AddNexusServer<
        // The DI container constructs the nexus for every connection.
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TServerNexus,
        TClientProxy>(this IServiceCollection services, Action<NexusServerOptions<TServerNexus, TClientProxy>> configure)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddTransient<TServerNexus>();

        services.AddSingleton(sp =>
        {
            var options = new NexusServerOptions<TServerNexus, TClientProxy>();
            configure(options);
            var server = new NexusServer<TServerNexus, TClientProxy>();
            options.Apply(server, sp);
            return server;
        });

        services.AddSingleton<ServerNexusContextProvider<TServerNexus, TClientProxy>>(sp =>
            sp.GetRequiredService<NexusServer<TServerNexus, TClientProxy>>().ContextProvider);

        services.AddHostedService(sp => new NexusServerHostedService(
            sp.GetRequiredService<NexusServer<TServerNexus, TClientProxy>>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetService<ILogger<INexusServer>>()));

        return services;
    }

    /// <summary>
    /// The endpoint a Map* call routes to: an external endpoint added for <paramref name="config"/> when it is not the
    /// primary configuration of an already configured server; null to route to the primary endpoint (the primary
    /// configuration, or a server not configured yet when the map is made, as before endpoints existed).
    /// </summary>
    private static INexusExternalEndpoint? ResolveEndpoint<TServerNexus, TClientProxy>(
        WebApplication app,
        AspServerConfig config,
        ref NexusServer<TServerNexus, TClientProxy>? server,
        string? name,
        bool requireConfigured)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        server ??= app.Services.GetService<NexusServer<TServerNexus, TClientProxy>>();

        if (server is not { IsConfigured: true })
        {
            if (requireConfigured)
                throw new InvalidOperationException("Configure the server before mapping endpoints to it.");
            return null;
        }

        if (ReferenceEquals(server.Config, config))
            return null;

        if (name == null)
        {
            // Named after the transport, or after transport and path when that name is taken (several WebSocket
            // endpoints, or a WebSocket primary endpoint).
            name = config.TransportName;
            foreach (var endpoint in server.Endpoints)
            {
                if (endpoint.Name == name)
                {
                    name = config.TransportName + config.Path;
                    break;
                }
            }
        }

        return (INexusExternalEndpoint)server.AddExternalEndpoint(config, name);
    }
    
    /// <summary>
    /// Applies the current configured authentication scheme.
    /// </summary>
    /// <param name="context">Http context for the current connection.</param>
    /// <param name="config">Configuration containing the authentication scheme.</param>
    /// <remarks>On success the authenticated principal is assigned to <c>HttpContext.User</c>.</remarks>
    /// <returns>
    /// Returns true if the connection should continue based upon the configurations.
    /// The client may be authenticated or not if configured to disable authentication.
    /// </returns>
    public static Task<bool> ApplyAuthentication(HttpContext context, AspServerConfig config)
    {
        return ApplyAuthentication(context, config, accessTokenQueryParameter: null);
    }

    /// <summary>
    /// Applies the current configured authentication scheme, first promoting an access token passed in the
    /// query string to the <c>Authorization</c> header when <paramref name="accessTokenQueryParameter"/> is set.
    /// </summary>
    internal static async Task<bool> ApplyAuthentication(HttpContext context, AspServerConfig config, string? accessTokenQueryParameter)
    {
        if (await AuthenticateRequest(context, config, accessTokenQueryParameter).ConfigureAwait(false))
            return true;

        // Notify the connecting client with Unauthorized.
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return false;
    }

    /// <summary>
    /// Runs the configured ASP.NET authentication, as <see cref="ApplyAuthentication(HttpContext, AspServerConfig, string?)"/>
    /// does, without answering the request: on failure the caller decides how the client is refused.
    /// </summary>
    /// <returns>True if the connection should continue (authenticated, or authentication disabled).</returns>
    internal static async Task<bool> AuthenticateRequest(HttpContext context, AspServerConfig config, string? accessTokenQueryParameter)
    {
        if (!config.AspEnableAuthentication)
            return true;

        var promoted = PromoteQueryAccessToken(context, accessTokenQueryParameter, config);

        var authScheme = config.AspAuthenticationScheme?.Trim();
        AuthenticateResult authResult;
        if (promoted)
        {
            // An earlier UseAuthentication() may already have authenticated this request without the header,
            // and authentication handlers cache their result per request scope. Authenticate in a fresh scope
            // so the promoted header is read.
            var requestServices = context.RequestServices;
            var scope = requestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            context.RequestServices = scope.ServiceProvider;
            try
            {
                authResult = await AuthenticateCore(context, authScheme).ConfigureAwait(false);
            }
            finally
            {
                context.RequestServices = requestServices;
                await scope.DisposeAsync().ConfigureAwait(false);
            }
        }
        else
        {
            authResult = await AuthenticateCore(context, authScheme).ConfigureAwait(false);
        }

        if (authResult.Succeeded)
        {
            // AuthenticateAsync does not set the request's principal; the session reads it from context.User.
            if (authResult.Principal != null)
                context.User = authResult.Principal;
            return true;
        }

        // Log the failure.
        config.Logger?.LogInfo(authResult.Failure, "Authentication failed");
        return false;
    }

    private static Task<AuthenticateResult> AuthenticateCore(HttpContext context, string? authScheme)
    {
        return string.IsNullOrWhiteSpace(authScheme)
            ? context.AuthenticateAsync()
            : context.AuthenticateAsync(authScheme);
    }

    /// <summary>
    /// When <paramref name="parameterName"/> is set and the request has no <c>Authorization</c> header, moves the
    /// query-string value of that parameter to <c>Authorization: Bearer &lt;token&gt;</c> and removes the parameter
    /// from the request's query string. The token value is never logged.
    /// </summary>
    /// <returns>True if a token was promoted.</returns>
    internal static bool PromoteQueryAccessToken(HttpContext context, string? parameterName, AspServerConfig config)
    {
        if (string.IsNullOrEmpty(parameterName))
            return false;

        var request = context.Request;
        if (!request.Query.TryGetValue(parameterName, out var values))
            return false;

        // Remove the token from the query string so later middleware and application code do not see it.
        var remaining = new QueryBuilder();
        foreach (var pair in request.Query)
        {
            if (string.Equals(pair.Key, parameterName, StringComparison.Ordinal))
                continue;
            foreach (var value in pair.Value)
                remaining.Add(pair.Key, value ?? string.Empty);
        }
        request.QueryString = remaining.ToQueryString();

        if (!StringValues.IsNullOrEmpty(request.Headers.Authorization))
        {
            config.Logger?.LogDebug($"Ignored the access token in query parameter '{parameterName}' because the request has an Authorization header.");
            return false;
        }

        var token = values.Count == 1 ? values[0] : null;
        if (string.IsNullOrWhiteSpace(token))
        {
            config.Logger?.LogDebug($"Ignored query parameter '{parameterName}': expected exactly one non-empty access token.");
            return false;
        }

        request.Headers.Authorization = "Bearer " + token;
        config.Logger?.LogDebug($"Promoted the access token in query parameter '{parameterName}' to the Authorization header (value redacted).");
        return true;
    }

    /// <summary>
    /// Checks the request's Origin header against an allow list. An empty list or a <c>*</c> entry allows every
    /// origin, and a request without an Origin header (a non-browser client) is always allowed.
    /// </summary>
    internal static bool IsOriginAllowed(HttpContext context, IList<string> allowedOrigins)
    {
        if (allowedOrigins.Count == 0)
            return true;

        var origin = context.Request.Headers.Origin;
        if (StringValues.IsNullOrEmpty(origin))
            return true;

        var originValue = origin.ToString();
        foreach (var allowed in allowedOrigins)
        {
            if (string.Equals(allowed, "*", StringComparison.Ordinal)
                || string.Equals(allowed, originValue, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

}
