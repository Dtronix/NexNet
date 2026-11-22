using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexNet.Collections;
using NexNet.Invocation;

namespace NexNet.InteropServer;

/// <summary>
/// Hosts interop servers: one Kestrel instance with a plain HTTP and an HTTPS listener, both bound on port 0, that
/// routes HttpSocket and WebSocket requests to the server they belong to. A server added without a
/// <see cref="InteropServerOptions.TestId"/> is served at the root paths; one with a test id at
/// <c>/t/{testId}/…</c>, so many independent servers share one Kestrel start (shared host mode).
/// </summary>
/// <remarks>
/// Use <see cref="StartAsync{TServerNexus,TClientProxy}"/> for a dedicated server with its own host, for tests that
/// restart the server or need refused connections; use <see cref="StartSharedAsync"/> and
/// <see cref="AddServerAsync{TServerNexus,TClientProxy}"/> to share one host between tests.
/// </remarks>
public sealed class InteropServerHost : IAsyncDisposable
{
    private const string RootKey = "";
    private const string TenantPrefix = "/t/";

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, Routes> _routes = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>
    /// Base URL of the plain HTTP listener, for example <c>http://127.0.0.1:50123/</c>.
    /// </summary>
    public Uri HttpBaseUri { get; private set; } = null!;

    /// <summary>
    /// Base URL of the HTTPS listener, for example <c>https://127.0.0.1:50124/</c>.
    /// </summary>
    public Uri HttpsBaseUri { get; private set; } = null!;

    /// <summary>
    /// Certificate of the HTTPS listener and of the TCP TLS listeners of this host's servers.
    /// </summary>
    public X509Certificate2 ServerCertificate { get; }

    private InteropServerHost(WebApplication app, X509Certificate2 serverCertificate)
    {
        _app = app;
        ServerCertificate = serverCertificate;
    }

    /// <summary>
    /// Starts a dedicated host and one server on it, served at the root paths. Disposing the server disposes the host.
    /// </summary>
    /// <param name="nexusFactory">Creates the nexus of each session.</param>
    /// <param name="options">Server options; <see cref="InteropServerOptions.TestId"/> must be null or is ignored.</param>
    /// <param name="hostOptions">Host options.</param>
    /// <param name="configureCollections">Configures the server's collections.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The started server.</returns>
    public static async Task<InteropServer<TServerNexus, TClientProxy>> StartAsync<TServerNexus, TClientProxy>(
        Func<TServerNexus> nexusFactory,
        InteropServerOptions? options = null,
        InteropHostOptions? hostOptions = null,
        Action<TServerNexus>? configureCollections = null,
        CancellationToken cancellationToken = default)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        var host = await StartSharedAsync(hostOptions, cancellationToken).ConfigureAwait(false);
        try
        {
            options ??= new InteropServerOptions();
            options.TestId = null;
            return await host.AddServerCoreAsync<TServerNexus, TClientProxy>(
                nexusFactory, options, configureCollections, ownsHost: true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts a host without servers; add them with <see cref="AddServerAsync{TServerNexus,TClientProxy}"/>.
    /// </summary>
    public static async Task<InteropServerHost> StartSharedAsync(
        InteropHostOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var certificate = options?.ServerCertificate ?? InteropCertificates.LoadServerCertificate();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // HTTP/1.1 only: HttpSocket and the WebSocket clients upgrade an HTTP/1.1 request.
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificate);
            });
        });

        var app = builder.Build();
        var host = new InteropServerHost(app, certificate);
        app.UseWebSockets();
        app.Run(host.DispatchAsync);

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            var addresses = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses ?? [];
            foreach (var address in addresses)
            {
                var uri = new Uri(address);
                var baseUri = new UriBuilder(uri.Scheme, "127.0.0.1", uri.Port).Uri;
                if (uri.Scheme == Uri.UriSchemeHttps)
                    host.HttpsBaseUri = baseUri;
                else
                    host.HttpBaseUri = baseUri;
            }

            if (host.HttpBaseUri == null || host.HttpsBaseUri == null)
                throw new InvalidOperationException($"Kestrel did not report both listeners: {string.Join(", ", addresses)}");
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return host;
    }

    /// <summary>
    /// Adds and starts a server on this host. With a <see cref="InteropServerOptions.TestId"/> its HttpSocket and
    /// WebSocket endpoints are served at <c>/t/{testId}/…</c>; without one at the root paths (one such server per
    /// host). Its TCP, TLS and UDS listeners are its own.
    /// </summary>
    /// <param name="nexusFactory">Creates the nexus of each session.</param>
    /// <param name="options">Server options.</param>
    /// <param name="configureCollections">Configures the server's collections.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The started server. Disposing it removes it from the host.</returns>
    public Task<InteropServer<TServerNexus, TClientProxy>> AddServerAsync<TServerNexus, TClientProxy>(
        Func<TServerNexus> nexusFactory,
        InteropServerOptions? options = null,
        Action<TServerNexus>? configureCollections = null,
        CancellationToken cancellationToken = default)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        return AddServerCoreAsync<TServerNexus, TClientProxy>(
            nexusFactory, options ?? new InteropServerOptions(), configureCollections, ownsHost: false, cancellationToken);
    }

    private async Task<InteropServer<TServerNexus, TClientProxy>> AddServerCoreAsync<TServerNexus, TClientProxy>(
        Func<TServerNexus> nexusFactory,
        InteropServerOptions options,
        Action<TServerNexus>? configureCollections,
        bool ownsHost,
        CancellationToken cancellationToken)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(nexusFactory);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var key = options.TestId ?? RootKey;
        if (options.TestId != null)
            ValidateTestId(options.TestId);

        // Reserve the route first so two servers can not claim the same test id.
        var routes = new Routes();
        if (!_routes.TryAdd(key, routes))
            throw new InvalidOperationException(options.TestId == null
                ? "The host already has a server at the root paths."
                : $"The host already has a server with test id '{options.TestId}'.");

        var pathPrefix = options.TestId == null ? "" : TenantPrefix + options.TestId;
        InteropServer<TServerNexus, TClientProxy> server;
        try
        {
            server = new InteropServer<TServerNexus, TClientProxy>(
                this, options, pathPrefix, nexusFactory, configureCollections, ownsHost);
        }
        catch
        {
            await RemoveRoutesAsync(options.TestId).ConfigureAwait(false);
            throw;
        }

        try
        {
            routes.Http = server.BuildRoutes(secure: false);
            routes.Https = server.BuildRoutes(secure: true);
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            routes.Server = server;
        }
        catch
        {
            // Owning the host, the server disposes it; the host owner does that otherwise.
            await server.DisposeCoreAsync(disposeHost: false).ConfigureAwait(false);
            throw;
        }

        return server;
    }

    internal async ValueTask RemoveRoutesAsync(string? testId)
    {
        if (_routes.TryRemove(testId ?? RootKey, out var routes))
            await routes.DisposeAsync().ConfigureAwait(false);
    }

    private static void ValidateTestId(string testId)
    {
        if (testId.Length is 0 or > 64 || !testId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ArgumentException(
                $"Test id '{testId}' must be 1 to 64 ASCII letters, digits, '-', '_' or '.'.", nameof(testId));
    }

    private Task DispatchAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var key = RootKey;
        if (path.StartsWith(TenantPrefix, StringComparison.Ordinal))
        {
            var end = path.IndexOf('/', TenantPrefix.Length);
            key = end < 0 ? path[TenantPrefix.Length..] : path[TenantPrefix.Length..end];
        }

        if (!_routes.TryGetValue(key, out var routes))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        var pipeline = context.Request.IsHttps ? routes.Https : routes.Http;
        if (pipeline == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return pipeline.Invoke(context);
    }

    /// <summary>
    /// Stops Kestrel and every server still on this host.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var routes in _routes.Values.ToList())
        {
            if (routes.Server != null)
                await routes.Server.DisposeCoreAsync(disposeHost: false).ConfigureAwait(false);
        }

        // Routes of servers that failed to start.
        foreach (var key in _routes.Keys.ToList())
        {
            if (_routes.TryRemove(key, out var routes))
                await routes.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await _app.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The request pipelines of one server: one for the plain HTTP listener and one for the HTTPS listener.
    /// </summary>
    private sealed class Routes : IAsyncDisposable
    {
        public IInteropServerLifetime? Server { get; set; }
        public RoutePipeline? Http { get; set; }
        public RoutePipeline? Https { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (Http != null)
                await Http.DisposeAsync().ConfigureAwait(false);
            if (Https != null)
                await Https.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Disposal of a server by its host.
/// </summary>
internal interface IInteropServerLifetime
{
    /// <summary>
    /// Stops the server, removes its routes and deletes its socket folder; disposes the host too when
    /// <paramref name="disposeHost"/> is true and the server owns it.
    /// </summary>
    ValueTask DisposeCoreAsync(bool disposeHost);
}

/// <summary>
/// A request pipeline built on a <see cref="WebApplication"/> that is never started: NexNet.Asp maps endpoints on a
/// <see cref="WebApplication"/>, and building one without Kestrel costs far less than starting one.
/// </summary>
internal sealed class RoutePipeline : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly RequestDelegate _pipeline;

    public RoutePipeline(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddSingleton<IServer, NoServer>();
        _app = builder.Build();
        configure(_app);
        _pipeline = ((IApplicationBuilder)_app).Build();
    }

    public Task Invoke(HttpContext context) => _pipeline(context);

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    /// <summary>
    /// The <see cref="IServer"/> a <see cref="WebApplication"/> requires; never started.
    /// </summary>
    private sealed class NoServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull
            => throw new NotSupportedException("Route pipelines are never started.");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
